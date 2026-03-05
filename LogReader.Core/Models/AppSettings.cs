namespace LogReader.Core.Models;

public class AppSettings
{
    public string? DefaultOpenDirectory { get; set; }
    public bool GlobalAutoTailEnabled { get; set; } = true;
    public List<LineHighlightRule> HighlightRules { get; set; } = new();
}
