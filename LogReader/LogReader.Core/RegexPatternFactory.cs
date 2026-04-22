namespace LogReader.Core;

using System.Text.RegularExpressions;

public static class RegexPatternFactory
{
    public static TimeSpan MatchTimeout { get; } = TimeSpan.FromMilliseconds(250);

    public static Regex Create(string pattern, bool caseSensitive)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var options = RegexOptions.Compiled;
        if (!caseSensitive)
            options |= RegexOptions.IgnoreCase;

        return new Regex(pattern, options, MatchTimeout);
    }

    public static bool TryCreate(string pattern, bool caseSensitive, out Regex? regex)
    {
        try
        {
            regex = Create(pattern, caseSensitive);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }
    }
}
