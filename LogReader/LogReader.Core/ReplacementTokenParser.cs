namespace LogReader.Core;

using System.Globalization;
using System.Text.RegularExpressions;

public static class ReplacementTokenParser
{
    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase) { "date" };

    private static readonly Regex TokenPattern = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Validates all <c>{type:format}</c> tokens in a replace pattern string.
    /// Returns <c>null</c> if valid, or an error message describing the first problem found.
    /// </summary>
    public static string? Validate(string replacePattern)
    {
        if (string.IsNullOrEmpty(replacePattern))
            return null;

        // Check for unmatched braces.
        var openCount = replacePattern.Count(c => c == '{');
        var closeCount = replacePattern.Count(c => c == '}');
        if (openCount != closeCount)
            return "Unmatched brace in replace pattern.";

        var matches = TokenPattern.Matches(replacePattern);
        if (matches.Count == 0)
            return null;

        foreach (Match match in matches)
        {
            var inner = match.Groups[1].Value;
            var colonIndex = inner.IndexOf(':');

            if (colonIndex < 0)
                return $"Token '{{{inner}}}' is missing a format — expected {{type:format}}.";

            var type = inner[..colonIndex];
            var format = inner[(colonIndex + 1)..];

            if (string.IsNullOrWhiteSpace(type))
                return $"Token '{{{inner}}}' has an empty type.";

            if (!KnownTypes.Contains(type))
                return $"Unknown token type '{type}'. Supported types: {string.Join(", ", KnownTypes)}.";

            if (string.IsNullOrWhiteSpace(format))
                return $"Token '{{{inner}}}' has an empty format string.";

            var formatError = ValidateFormat(type, format);
            if (formatError != null)
                return formatError;
        }

        return null;
    }

    private static string? ValidateFormat(string type, string format)
    {
        if (type.Equals("date", StringComparison.OrdinalIgnoreCase))
        {
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

        return null;
    }
}
