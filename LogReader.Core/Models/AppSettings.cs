namespace LogReader.Core.Models;

public class AppSettings
{
    public string? DefaultOpenDirectory { get; set; }
    public List<LineHighlightRule> HighlightRules { get; set; } = new();
}
