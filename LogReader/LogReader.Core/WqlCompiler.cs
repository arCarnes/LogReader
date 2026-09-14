namespace LogReader.Core;

using System.Collections.Immutable;
using System.Text;
using LogReader.Core.Models;

public sealed class WqlQueryPlan
{
    private readonly Func<Func<string, StructuredFieldValue>, bool?> _predicate;

    internal WqlQueryPlan(string expression, bool caseSensitive, StructuredFieldExtractor extractor,
        Func<Func<string, StructuredFieldValue>, bool?> predicate)
    {
        Expression = expression;
        CaseSensitive = caseSensitive;
        Extractor = extractor;
        _predicate = predicate;
    }

    public string Expression { get; }
    public bool CaseSensitive { get; }
    public StructuredFieldExtractor Extractor { get; }

    public WqlLineEvaluation Evaluate(string line, long lineNumber, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fields = Extractor.Extract(line, ct);
        return new WqlLineEvaluation(_predicate(GetValue) == true, fields);

        StructuredFieldValue GetValue(string name)
            => name.Equals("raw", StringComparison.OrdinalIgnoreCase)
                ? new(StructuredFieldType.Text, StructuredFieldState.Value, Text: line)
                : name.Equals("line_number", StringComparison.OrdinalIgnoreCase)
                    ? new(StructuredFieldType.Number, StructuredFieldState.Value, Number: lineNumber)
                    : fields[name];
    }
}

public sealed class WqlException : ArgumentException
{
    public WqlException(string message, int position) : base($"{message} (position {position + 1}).")
        => Position = position;
    public int Position { get; }
}

public static class WqlCompiler
{
    public static WqlQueryPlan Compile(string expression, StructuredFieldProfile? profile = null, bool caseSensitive = false)
        => Compile(expression, StructuredFieldExtractor.Compile(profile), caseSensitive);

    public static WqlQueryPlan Compile(string expression, StructuredFieldExtractor extractor, bool caseSensitive = false)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > StructuredFieldExtractor.MaximumTextLength)
            throw new WqlException($"WQL requires 1–{StructuredFieldExtractor.MaximumTextLength} characters", 0);
        ArgumentNullException.ThrowIfNull(extractor);
        var parser = new Parser(expression, extractor.Fields, caseSensitive);
        return new WqlQueryPlan(expression, caseSensitive, extractor, parser.Parse());
    }

    private enum Kind { Word, String, Number, Operator, Left, Right, Comma, End }
    private sealed record Token(Kind Kind, string Text, int Position);
    private delegate bool? Predicate(Func<string, StructuredFieldValue> get);

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly ImmutableDictionary<string, StructuredFieldType> _fields;
        private readonly StringComparison _comparison;
        private int _index;
        private Token Current => _tokens[_index];

        public Parser(string expression, ImmutableDictionary<string, StructuredFieldType> fields, bool caseSensitive)
        {
            _tokens = Tokenize(expression);
            _fields = fields;
            _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        }

        public Func<Func<string, StructuredFieldValue>, bool?> Parse()
        {
            var predicate = ParseOr(0);
            if (Current.Kind != Kind.End) throw Error("Unexpected token or unsupported WQL clause");
            return get => predicate(get);
        }

        private Predicate ParseOr(int depth)
        {
            var terms = new List<Predicate> { ParseAnd(depth) };
            while (TakeWord("OR")) terms.Add(ParseAnd(depth));
            return get =>
            {
                bool? result = false;
                foreach (var term in terms)
                {
                    var value = term(get);
                    if (value == true) return true;
                    if (value == null) result = null;
                }
                return result;
            };
        }

        private Predicate ParseAnd(int depth)
        {
            var terms = new List<Predicate> { ParseUnary(depth) };
            while (TakeWord("AND")) terms.Add(ParseUnary(depth));
            return get =>
            {
                bool? result = true;
                foreach (var term in terms)
                {
                    var value = term(get);
                    if (value == false) return false;
                    if (value == null) result = null;
                }
                return result;
            };
        }

        private Predicate ParseUnary(int depth)
        {
            if (depth > StructuredFieldExtractor.MaximumNesting) throw Error("WQL nesting limit exceeded");
            if (TakeWord("NOT"))
            {
                var operand = ParseUnary(depth + 1);
                return get => !operand(get);
            }
            if (Take(Kind.Left))
            {
                var inner = ParseOr(depth + 1);
                Require(Kind.Right, "Expected ')'");
                return inner;
            }
            return ParseComparison();
        }

        private Predicate ParseComparison()
        {
            var field = Current;
            Require(Kind.Word, "Expected a field name");
            StructuredFieldType type;
            if (field.Text.Equals("raw", StringComparison.OrdinalIgnoreCase)) type = StructuredFieldType.Text;
            else if (field.Text.Equals("line_number", StringComparison.OrdinalIgnoreCase)) type = StructuredFieldType.Number;
            else if (!_fields.TryGetValue(field.Text, out type)) throw new WqlException("Unknown field", field.Position);

            if (TakeWord("IS"))
            {
                var negate = TakeWord("NOT");
                if (!TakeWord("MISSING")) throw Error("Expected MISSING");
                return get => (get(field.Text).State != StructuredFieldState.Value) != negate;
            }
            if (TakeWord("IN"))
            {
                Require(Kind.Left, "Expected '(' after IN");
                var literals = new List<StructuredFieldValue> { ParseLiteral(type) };
                while (Take(Kind.Comma)) literals.Add(ParseLiteral(type));
                Require(Kind.Right, "Expected ')'");
                return get =>
                {
                    var value = get(field.Text);
                    return value.State != StructuredFieldState.Value ? null : literals.Any(literal => Compare(value, literal) == 0);
                };
            }
            var op = Current;
            if (TakeWord("CONTAINS"))
            {
                if (type != StructuredFieldType.Text) throw new WqlException("CONTAINS requires a text field", op.Position);
                var literal = ParseLiteral(type);
                return get =>
                {
                    var value = get(field.Text);
                    return value.State != StructuredFieldState.Value ? null : value.Text!.Contains(literal.Text!, _comparison);
                };
            }
            Require(Kind.Operator, "Expected a comparison operator");
            if (type == StructuredFieldType.Text && op.Text is not ("=" or "!="))
                throw new WqlException("Ordering comparisons require a number field", op.Position);
            var expected = ParseLiteral(type);
            return get =>
            {
                var value = get(field.Text);
                if (value.State != StructuredFieldState.Value) return null;
                var comparison = Compare(value, expected);
                return op.Text switch
                {
                    "=" => comparison == 0, "!=" => comparison != 0,
                    "<" => comparison < 0, "<=" => comparison <= 0,
                    ">" => comparison > 0, ">=" => comparison >= 0,
                    _ => throw new InvalidOperationException("Invalid compiled WQL operator.")
                };
            };
        }

        private int Compare(StructuredFieldValue left, StructuredFieldValue right)
            => left.Type == StructuredFieldType.Number
                ? left.Number!.Value.CompareTo(right.Number!.Value)
                : string.Compare(left.Text, right.Text, _comparison);

        private StructuredFieldValue ParseLiteral(StructuredFieldType type)
        {
            var token = Current;
            if (type == StructuredFieldType.Text)
            {
                Require(Kind.String, "Expected a double-quoted text literal");
                return new(type, StructuredFieldState.Value, Text: token.Text);
            }
            Require(Kind.Number, "Expected a number literal");
            if (!StructuredFieldExtractor.TryParseNumber(token.Text, out var number))
                throw new WqlException("Invalid or out-of-range number", token.Position);
            return new(type, StructuredFieldState.Value, Number: number);
        }

        private bool Take(Kind kind)
        {
            if (Current.Kind != kind) return false;
            _index++;
            return true;
        }

        private bool TakeWord(string word)
        {
            if (Current.Kind != Kind.Word || !Current.Text.Equals(word, StringComparison.OrdinalIgnoreCase)) return false;
            _index++;
            return true;
        }

        private void Require(Kind kind, string message)
        {
            if (!Take(kind)) throw Error(message);
        }
        private WqlException Error(string message) => new(message, Current.Position);
    }

    private static List<Token> Tokenize(string expression)
    {
        var tokens = new List<Token>();
        for (var index = 0; index < expression.Length;)
        {
            var start = index;
            var character = expression[index++];
            if (char.IsWhiteSpace(character)) continue;
            if (character == '"')
            {
                var text = new StringBuilder();
                var closed = false;
                while (index < expression.Length)
                {
                    character = expression[index++];
                    if (character == '"') { closed = true; break; }
                    if (character == '\\')
                    {
                        if (index == expression.Length) throw new WqlException("Unfinished string escape", index - 1);
                        character = expression[index++] switch
                        {
                            '"' => '"', '\\' => '\\', 'n' => '\n', 'r' => '\r', 't' => '\t',
                            _ => throw new WqlException("Unsupported string escape", index - 1)
                        };
                    }
                    text.Append(character);
                }
                if (!closed) throw new WqlException("Unterminated string", start);
                tokens.Add(new(Kind.String, text.ToString(), start));
            }
            else if (char.IsAsciiLetter(character) || character == '_')
            {
                while (index < expression.Length && (char.IsAsciiLetterOrDigit(expression[index]) || expression[index] == '_')) index++;
                tokens.Add(new(Kind.Word, expression[start..index], start));
            }
            else if (char.IsAsciiDigit(character) || character is '+' or '-' or '.')
            {
                while (index < expression.Length && (char.IsAsciiDigit(expression[index]) || expression[index] == '.')) index++;
                tokens.Add(new(Kind.Number, expression[start..index], start));
            }
            else if (character is '=' or '!' or '<' or '>')
            {
                if (character != '=' && index < expression.Length && expression[index] == '=') index++;
                if (character == '!' && index == start + 1) throw new WqlException("Expected !=", start);
                tokens.Add(new(Kind.Operator, expression[start..index], start));
            }
            else
            {
                var kind = character switch
                {
                    '(' => Kind.Left, ')' => Kind.Right, ',' => Kind.Comma,
                    _ => throw new WqlException("Unexpected character", start)
                };
                tokens.Add(new(kind, character.ToString(), start));
            }
        }
        tokens.Add(new(Kind.End, string.Empty, expression.Length));
        return tokens;
    }
}
