namespace LogReader.App.Helpers;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Models;

public static class LineHighlighter
{
    internal const int RegexCacheCapacity = 128;

    private static readonly ConcurrentDictionary<RegexCacheKey, RegexCacheEntry> RegexCache = new();
    private static readonly ConcurrentQueue<RegexCacheKey> RegexCacheInsertionOrder = new();

    internal static int CachedRegexCount => RegexCache.Count;

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
        var key = new RegexCacheKey(rule.Pattern, rule.CaseSensitive);
        if (!RegexCache.TryGetValue(key, out var entry))
        {
            var candidate = new RegexCacheEntry(
                RegexPatternFactory.TryCreate(key.Pattern, key.CaseSensitive, out var compiledRegex)
                    ? compiledRegex
                    : null);
            entry = RegexCache.GetOrAdd(key, candidate);
            if (ReferenceEquals(entry, candidate))
            {
                RegexCacheInsertionOrder.Enqueue(key);
                TrimRegexCache();
            }
        }

        return entry.Regex?.IsMatch(text) == true;
    }

    private static void TrimRegexCache()
    {
        while (RegexCache.Count > RegexCacheCapacity && RegexCacheInsertionOrder.TryDequeue(out var oldestKey))
            RegexCache.TryRemove(oldestKey, out _);
    }

    private readonly record struct RegexCacheKey(string Pattern, bool CaseSensitive);

    private sealed record RegexCacheEntry(Regex? Regex);
}
