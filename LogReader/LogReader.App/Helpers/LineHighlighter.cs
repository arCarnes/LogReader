namespace LogReader.App.Helpers;

using System.Collections.Generic;
using System.Text.RegularExpressions;
using LogReader.Core.Models;

public static class LineHighlighter
{
    public static string? GetHighlightColor(IList<LineHighlightRule> rules, string text)
    {
        foreach (var rule in rules)
        {
            if (!rule.IsEnabled || string.IsNullOrEmpty(rule.Pattern)) continue;
            try
            {
                bool match = rule.IsRegex
                    ? Regex.IsMatch(text, rule.Pattern,
                        rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase)
                    : text.Contains(rule.Pattern,
                        rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (match) return rule.Color;
            }
            catch (ArgumentException)
            {
                // Invalid regex pattern — skip this rule
            }
        }
        return null;
    }
}
