namespace LogReader.Core.Models;

public class AppSettings
{
    public string? DefaultOpenDirectory { get; set; }
    public bool GlobalAutoTailEnabled { get; set; } = true;
    public FileEncoding DefaultFileEncoding { get; set; } = FileEncoding.Utf8;
    public List<FileEncoding> FileEncodingFallbacks { get; set; } = new();
    public List<LineHighlightRule> HighlightRules { get; set; } = new();
}
