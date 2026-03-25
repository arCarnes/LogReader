namespace LogReader.App.Helpers;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Models;

public static class LineHighlighter
{
    private static readonly ConcurrentDictionary<RegexCacheKey, Regex?> RegexCache = new();

    public static string? GetHighlightColor(IList<LineHighlightRule> rules, string text)
    {
        foreach (var rule in rules)
        {
            if (!rule.IsEnabled || string.IsNullOrEmpty(rule.Pattern))
                continue;

            try
            {
                bool match = rule.IsRegex
                    ? IsRegexMatch(rule, text)
                    : text.Contains(
                        rule.Pattern,
                        rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (match)
                    return rule.Color;
            }
            catch (RegexMatchTimeoutException)
            {
                // Timed-out regex - skip this rule for the current line.
            }
        }

        return null;
    }

    private static bool IsRegexMatch(LineHighlightRule rule, string text)
    {
        var regex = RegexCache.GetOrAdd(
            new RegexCacheKey(rule.Pattern, rule.CaseSensitive),
            static key => RegexPatternFactory.TryCreate(key.Pattern, key.CaseSensitive, out var compiledRegex)
                ? compiledRegex
                : null);
        return regex?.IsMatch(text) == true;
    }

    private readonly record struct RegexCacheKey(string Pattern, bool CaseSensitive);
}
