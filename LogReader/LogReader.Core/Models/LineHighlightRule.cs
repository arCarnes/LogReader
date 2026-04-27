namespace LogReader.Core.Models;

public class LineHighlightRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Pattern { get; set; } = string.Empty;
    public bool IsRegex { get; set; }
    public bool CaseSensitive { get; set; }
    public string Color { get; set; } = "#FFFFFF";
    public bool IsEnabled { get; set; } = true;
}
