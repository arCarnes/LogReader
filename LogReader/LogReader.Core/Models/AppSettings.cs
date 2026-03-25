namespace LogReader.Core.Models;

public class AppSettings
{
    public string? DefaultOpenDirectory { get; set; }
    public string LogFontFamily { get; set; } = "Consolas";
    public bool ShowFullPathsInDashboard { get; set; }
    public List<LineHighlightRule> HighlightRules { get; set; } = new();
    public List<ReplacementPattern> DateRollingPatterns { get; set; } = new();
}
