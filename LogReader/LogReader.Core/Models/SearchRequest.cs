namespace LogReader.Core.Models;

public enum SearchRequestSourceMode
{
    DiskSnapshot,
    Tail,
    SnapshotAndTail
}

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public bool IsRegex { get; set; }
    public bool CaseSensitive { get; set; }
    public List<string> FilePaths { get; set; } = new();
    public Dictionary<string, List<int>> AllowedLineNumbersByFilePath { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long? StartLineNumber { get; set; }
    public long? EndLineNumber { get; set; }
    public string? FromTimestamp { get; set; }
    public string? ToTimestamp { get; set; }
    public SearchRequestSourceMode SourceMode { get; set; } = SearchRequestSourceMode.DiskSnapshot;
}
