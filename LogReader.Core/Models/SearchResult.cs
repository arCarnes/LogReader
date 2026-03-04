namespace LogReader.Core.Models;

public class SearchResult
{
    public string FilePath { get; set; } = string.Empty;
    public List<SearchHit> Hits { get; set; } = new();
    public string? Error { get; set; }
}

public class SearchHit
{
    public long LineNumber { get; set; }
    public string LineText { get; set; } = string.Empty;
    public int MatchStart { get; set; }
    public int MatchLength { get; set; }
}
