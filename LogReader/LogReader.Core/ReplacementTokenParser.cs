namespace LogReader.Core;

using System.Globalization;

public static class ReplacementTokenParser
{
    /// <summary>
    /// Validates all <c>{format}</c> tokens in a replace pattern string.
    /// Returns <c>null</c> if valid, or an error message describing the first problem found.
    /// </summary>
    public static string? Validate(string replacePattern)
    {
        if (string.IsNullOrWhiteSpace(replacePattern))
            return "Replace must contain at least one date placeholder such as {yyyyMMdd}.";

        var tokenStart = -1;
        var tokenCount = 0;
        for (var index = 0; index < replacePattern.Length; index++)
        {
            var current = replacePattern[index];
            if (current == '{')
            {
                if (tokenStart >= 0)
                    return "Unmatched brace in replace pattern.";

                tokenStart = index;
                continue;
            }

            if (current != '}')
                continue;

            if (tokenStart < 0)
                return "Unmatched brace in replace pattern.";

            var inner = replacePattern[(tokenStart + 1)..index];
            var error = ValidateToken(inner);
            if (error != null)
                return error;

            tokenStart = -1;
            tokenCount++;
        }

        if (tokenStart >= 0)
            return "Unmatched brace in replace pattern.";

        return tokenCount == 0
            ? "Replace must contain at least one date placeholder such as {yyyyMMdd}."
            : null;
    }

    public static bool TryExpand(string? replacePattern, DateTime referenceDate, out string expanded, out string? error)
    {
        expanded = replacePattern ?? string.Empty;
        error = Validate(replacePattern!);
        if (error != null || string.IsNullOrEmpty(replacePattern))
            return error == null;

        var builder = new System.Text.StringBuilder(replacePattern.Length);
        var tokenStart = -1;
        for (var index = 0; index < replacePattern.Length; index++)
        {
            var current = replacePattern[index];
            if (current == '{')
            {
                tokenStart = index;
                continue;
            }

            if (current == '}')
            {
                var inner = replacePattern[(tokenStart + 1)..index];
                builder.Append(ExpandToken(inner, referenceDate));
                tokenStart = -1;
                continue;
            }

            if (tokenStart < 0)
                builder.Append(current);
        }

        expanded = builder.ToString();
        return true;
    }

    public static string DescribeTokens(string? replacePattern)
    {
        if (string.IsNullOrEmpty(replacePattern))
            return replacePattern ?? string.Empty;

        if (Validate(replacePattern) != null)
            return replacePattern;

        var builder = new System.Text.StringBuilder(replacePattern.Length);
        var tokenStart = -1;
        for (var index = 0; index < replacePattern.Length; index++)
        {
            var current = replacePattern[index];
            if (current == '{')
            {
                tokenStart = index;
                continue;
            }

            if (current == '}' && tokenStart >= 0)
            {
                var inner = replacePattern[(tokenStart + 1)..index];
                builder.Append(DescribeToken(inner));
                tokenStart = -1;
                continue;
            }

            if (tokenStart < 0)
                builder.Append(current);
        }

        return builder.ToString();
    }

    private static string? ValidateToken(string inner)
    {
        if (string.IsNullOrWhiteSpace(inner))
            return "Date placeholder '{}' has an empty format string.";

        if (inner.IndexOf(':') >= 0)
            return $"Token '{{{inner}}}' uses unsupported legacy syntax. Use '{{format}}' such as '{{yyyyMMdd}}'.";

        if (inner.IndexOf('{') >= 0 || inner.IndexOf('}') >= 0)
            return $"Token '{{{inner}}}' contains invalid brace characters.";

        return ValidateFormat(inner);
    }

    private static string? ValidateFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return "Date placeholder '{}' has an empty format string.";

        try
        {
            _ = DateTime.Now.ToString(format, CultureInfo.InvariantCulture);
            return null;
        }
        catch (FormatException)
        {
            return $"Invalid date format '{format}'.";
        }
    }

    private static string ExpandToken(string inner, DateTime referenceDate)
        => referenceDate.ToString(inner, CultureInfo.InvariantCulture);

    private static string DescribeToken(string inner) => inner;
}
