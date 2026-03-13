namespace LogReader.Core.Models;

public class AppSettings
{
    public string? DefaultOpenDirectory { get; set; }
    public bool GlobalAutoTailEnabled { get; set; } = true;
    public FileEncoding DefaultFileEncoding { get; set; } = FileEncoding.Auto;
    public string LogFontFamily { get; set; } = "Consolas";
    public List<LineHighlightRule> HighlightRules { get; set; } = new();
}
