namespace LogReader.Tests;

using LogReader.App.Helpers;
using LogReader.Core.Models;

public class LineHighlighterTests
{
    private static LineHighlightRule Rule(
        string pattern,
        string color,
        bool enabled = true,
        bool isRegex = false,
        bool caseSensitive = false) =>
        new() { Pattern = pattern, Color = color, IsEnabled = enabled, IsRegex = isRegex, CaseSensitive = caseSensitive };

    [Fact]
    public void NoRules_ReturnsNull()
    {
        var result = LineHighlighter.GetHighlightColor(new List<LineHighlightRule>(), "any text");
        Assert.Null(result);
    }

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        var rules = new List<LineHighlightRule> { Rule("FATAL", "#FF0000") };
        var result = LineHighlighter.GetHighlightColor(rules, "INFO: started");
        Assert.Null(result);
    }

    [Fact]
    public void FirstMatchingRuleWins()
    {
        var rules = new List<LineHighlightRule>
        {
            Rule("ERROR", "#FF0000"),
            Rule("ERROR", "#00FF00"),
        };
        var result = LineHighlighter.GetHighlightColor(rules, "ERROR occurred");
        Assert.Equal("#FF0000", result);
    }

    [Fact]
    public void DisabledRule_IsSkipped()
    {
        var rules = new List<LineHighlightRule>
        {
            Rule("ERROR", "#FF0000", enabled: false),
            Rule("ERROR", "#00FF00"),
        };
        var result = LineHighlighter.GetHighlightColor(rules, "ERROR occurred");
        Assert.Equal("#00FF00", result);
    }

    [Fact]
    public void CaseInsensitive_Matches()
    {
        var rules = new List<LineHighlightRule> { Rule("error", "#FF0000", caseSensitive: false) };
        var result = LineHighlighter.GetHighlightColor(rules, "ERROR: something failed");
        Assert.Equal("#FF0000", result);
    }

    [Fact]
    public void CaseSensitive_NoMatch()
    {
        var rules = new List<LineHighlightRule> { Rule("error", "#FF0000", caseSensitive: true) };
        var result = LineHighlighter.GetHighlightColor(rules, "ERROR: something failed");
        Assert.Null(result);
    }

    [Fact]
    public void CaseSensitive_Match()
    {
        var rules = new List<LineHighlightRule> { Rule("ERROR", "#FF0000", caseSensitive: true) };
        var result = LineHighlighter.GetHighlightColor(rules, "ERROR: something failed");
        Assert.Equal("#FF0000", result);
    }

    [Fact]
    public void RegexRule_Matches()
    {
        var rules = new List<LineHighlightRule> { Rule(@"\d{4}-\d{2}-\d{2} ERROR", "#FF0000", isRegex: true) };
        var result = LineHighlighter.GetHighlightColor(rules, "2024-01-15 ERROR: something failed");
        Assert.Equal("#FF0000", result);
    }

    [Fact]
    public void InvalidRegexRule_IsSkipped_FallsThrough()
    {
        var rules = new List<LineHighlightRule>
        {
            Rule("[invalid", "#FF0000", isRegex: true),
            Rule("ERROR", "#00FF00"),
        };
        var result = LineHighlighter.GetHighlightColor(rules, "ERROR occurred");
        Assert.Equal("#00FF00", result);
    }

    [Fact]
    public void EmptyPattern_IsSkipped()
    {
        var rules = new List<LineHighlightRule>
        {
            Rule("", "#FF0000"),
            Rule("INFO", "#00FF00"),
        };
        var result = LineHighlighter.GetHighlightColor(rules, "INFO: started");
        Assert.Equal("#00FF00", result);
    }

    [Fact]
    public void CatastrophicRegexTimeout_IsSkipped()
    {
        var rules = new List<LineHighlightRule>
        {
            Rule(@"(a+)+$", "#FF0000", isRegex: true)
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = LineHighlighter.GetHighlightColor(rules, new string('a', 30) + "!");
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.ElapsedMilliseconds < 2_000,
            $"Highlighting took {sw.ElapsedMilliseconds}ms; expected regex timeout protection");
    }
}
