namespace LogReader.Core.Models;

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public bool IsRegex { get; set; }
    public bool CaseSensitive { get; set; }
    public bool WholeWord { get; set; }
    public List<string> FilePaths { get; set; } = new();
    public long? StartLineNumber { get; set; }
    public long? EndLineNumber { get; set; }
    public string? FromTimestamp { get; set; }
    public string? ToTimestamp { get; set; }
}
