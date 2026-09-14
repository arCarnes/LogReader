namespace LogReader.Core;

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogReader.Core.Models;

/// <summary>Validated, immutable extraction rules shared by preview and search.</summary>
public sealed class StructuredFieldExtractor
{
    public const int MaximumTextLength = 8_192;
    public const int MaximumFields = 32;
    public const int MaximumNesting = 32;
    private readonly ImmutableArray<Rule> _rules;
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "raw", "line_number", "AND", "OR", "NOT", "IN", "IS", "MISSING", "CONTAINS"
    };

    private StructuredFieldExtractor(StructuredFieldProfile? profile, ImmutableArray<Rule> rules)
    {
        _rules = rules;
        ProfileId = profile?.Id;
        Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile))));
        Fields = rules.ToImmutableDictionary(rule => rule.Name, rule => rule.Type, StringComparer.OrdinalIgnoreCase);
    }

    public string? ProfileId { get; }
    public string Revision { get; }
    public ImmutableDictionary<string, StructuredFieldType> Fields { get; }

    public static StructuredFieldExtractor Compile(StructuredFieldProfile? profile)
    {
        if (profile == null)
            return new StructuredFieldExtractor(null, []);
        if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 128 || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 256)
            throw new ArgumentException("A field profile requires an ID (maximum 128 characters) and a name (maximum 256 characters).");
        if (profile.Fields == null || profile.Fields.Count is < 1 or > MaximumFields)
            throw new ArgumentException($"A field profile requires 1–{MaximumFields} fields.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = ImmutableArray.CreateBuilder<Rule>();
        foreach (var field in profile.Fields)
        {
            if (field == null || field.Name == null || field.Name.Length > 128 || !IsIdentifier(field.Name) || ReservedNames.Contains(field.Name) || !names.Add(field.Name))
                throw new ArgumentException("Field names must be unique identifiers and cannot use reserved WQL names.");
            if (!Enum.IsDefined(field.Type))
                throw new ArgumentException($"Field '{field.Name}' has an unsupported type.");
            if (string.IsNullOrEmpty(field.Pattern) || field.Pattern.Length > MaximumTextLength)
                throw new ArgumentException($"Field '{field.Name}' requires a pattern of at most {MaximumTextLength} characters.");
            Regex regex;
            try { regex = RegexPatternFactory.Create(field.Pattern, field.CaseSensitive); }
            catch (ArgumentException)
            {
                throw new ArgumentException($"Field '{field.Name}' has an invalid regular expression.");
            }
            if (!regex.GetGroupNames().Contains(field.Name, StringComparer.Ordinal))
                throw new ArgumentException($"Field '{field.Name}' requires a named capture '(?<{field.Name}>...)'.");
            rules.Add(new Rule(field.Name, field.Type, regex));
        }
        return new StructuredFieldExtractor(profile.Copy(), rules.ToImmutable());
    }

    public static void ValidateProfiles(IEnumerable<StructuredFieldProfile>? profiles)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles ?? [])
        {
            if (profile == null || !ids.Add(profile.Id))
                throw new ArgumentException("Field profile IDs must be unique.");
            Compile(profile);
        }
    }

    public ImmutableDictionary<string, StructuredFieldValue> Extract(string line, CancellationToken ct = default)
    {
        var values = ImmutableDictionary.CreateBuilder<string, StructuredFieldValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _rules)
        {
            ct.ThrowIfCancellationRequested();
            Group group;
            try { group = rule.Regex.Match(line).Groups[rule.Name]; }
            catch (RegexMatchTimeoutException)
            {
                // Do not propagate RegexMatchTimeoutException.Input (potentially sensitive log data).
                throw new TimeoutException($"Extraction timed out for field '{rule.Name}'. The file scan was stopped.");
            }
            if (!group.Success)
                values.Add(rule.Name, new(rule.Type, StructuredFieldState.Missing));
            else if (rule.Type == StructuredFieldType.Text)
                values.Add(rule.Name, new(rule.Type, StructuredFieldState.Value, Text: group.Captures[0].Value));
            else if (TryParseNumber(group.Captures[0].Value, out var number))
                values.Add(rule.Name, new(rule.Type, StructuredFieldState.Value, Number: number));
            else
                values.Add(rule.Name, new(rule.Type, StructuredFieldState.Invalid));
        }
        return values.ToImmutable();
    }

    internal static bool TryParseNumber(string text, out decimal number)
        => decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out number);

    internal static bool IsIdentifier(string? name)
        => !string.IsNullOrEmpty(name) && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
           name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private sealed record Rule(string Name, StructuredFieldType Type, Regex Regex);
}
