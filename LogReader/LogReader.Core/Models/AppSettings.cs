namespace LogReader.Core.Models;

public class AppSettings
{
    public string? DefaultOpenDirectory { get; set; }
    public string LogFontFamily { get; set; } = "Consolas";
    public int LogFontSize { get; set; } = 12;
    public bool ShowFullPathsInDashboard { get; set; }
    public bool EnableSearchMatchHighlighting { get; set; } = true;
    public string SearchMatchHighlightColor { get; set; } = "#FFF59D";
    public List<LineHighlightRule> HighlightRules { get; set; } = new();
    public List<string> ColorPickerCustomColors { get; set; } = new();
    public List<ReplacementPattern> DateRollingPatterns { get; set; } = new();
}
