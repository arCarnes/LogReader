namespace LogReader.Core;

using System.Text.RegularExpressions;

public static class RegexPatternFactory
{
    public static TimeSpan MatchTimeout { get; } = TimeSpan.FromMilliseconds(250);

    public static Regex Create(string pattern, bool caseSensitive, bool wholeWord = false)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var options = RegexOptions.Compiled;
        if (!caseSensitive)
            options |= RegexOptions.IgnoreCase;

        var effectivePattern = wholeWord ? $@"\b{pattern}\b" : pattern;
        return new Regex(effectivePattern, options, MatchTimeout);
    }

    public static bool TryCreate(string pattern, bool caseSensitive, bool wholeWord, out Regex? regex)
    {
        try
        {
            regex = Create(pattern, caseSensitive, wholeWord);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }
    }

    public static bool TryCreate(string pattern, bool caseSensitive, out Regex? regex)
        => TryCreate(pattern, caseSensitive, wholeWord: false, out regex);
}
