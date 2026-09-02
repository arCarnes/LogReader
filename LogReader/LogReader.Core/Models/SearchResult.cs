namespace LogReader.Core.Models;

public class SearchResult
{
    public string FilePath { get; set; } = string.Empty;
    public List<SearchHit> Hits { get; set; } = new();
    public string? Error { get; set; }
    public bool HasParseableTimestamps { get; set; }
    public bool HitLimitExceeded { get; set; }
    public long MatchingLineCount { get; set; }
    public long MatchOccurrenceCount { get; set; }
    public Dictionary<int, SearchTimestampBucketCount> TimestampBucketCounts { get; set; } = new();
    public long UnbucketedMatchingLineCount { get; set; }
    public long UnbucketedMatchOccurrenceCount { get; set; }
    public bool IsEvaluationComplete { get; set; }
    public bool WasCancelled { get; set; }
    internal FileScanGenerationEvidence GenerationEvidence { get; set; } = FileScanGenerationEvidence.Unknown;
    internal long? ScannedFileSize { get; set; }
    internal DateTime ScannedLastWriteTimeUtc { get; set; }
    internal long? EvaluatedThroughLine { get; set; }
    internal bool FileChangedDuringOrAfterScan { get; set; }
    internal FileEncoding ResolvedEncoding { get; set; } = FileEncoding.Utf8;
}

public sealed record BoundedSearchBatchResult(
    IReadOnlyList<SearchResult?> Results,
    bool WasCancelled);

public class SearchHit
{
    public long LineNumber { get; set; }
    public string LineText { get; set; } = string.Empty;
    public int MatchStart { get; set; }
    public int MatchLength { get; set; }
    public int? OriginalMatchStart { get; set; }
    public int? OriginalMatchLength { get; set; }
    public List<SearchMatchSpan> Matches { get; set; } = new();
    public bool LineTextTruncated { get; set; }
}

public class SearchMatchSpan
{
    public int MatchStart { get; set; }
    public int MatchLength { get; set; }
    public int? OriginalMatchStart { get; set; }
    public int? OriginalMatchLength { get; set; }
}
