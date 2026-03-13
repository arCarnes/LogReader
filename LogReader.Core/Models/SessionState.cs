namespace LogReader.Core.Models;

public class SessionState
{
    public List<OpenTabState> OpenTabs { get; set; } = new();
    public string? ActiveTabId { get; set; }
}

public class OpenTabState
{
    public string FileId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public FileEncoding Encoding { get; set; } = FileEncoding.Auto;
    public bool AutoScrollEnabled { get; set; } = true;
    public bool IsPinned { get; set; }
}

public enum FileEncoding
{
    Auto,
    Utf8,
    Utf8Bom,
    Ansi,
    Utf16,
    Utf16Be
}
