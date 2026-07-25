namespace LogReader.Core.Models;

/// <summary>
/// Compact result produced when evaluating a file for viewport filtering.
/// </summary>
public sealed class FilterResult
{
    public string FilePath { get; set; } = string.Empty;
    public List<int> MatchingLineNumbers { get; set; } = new();
    public string? Error { get; set; }
    public bool HasParseableTimestamps { get; set; }
    public bool HitLimitExceeded { get; set; }
    internal FileScanGenerationEvidence GenerationEvidence { get; set; } = FileScanGenerationEvidence.Unknown;
    internal int? EvaluatedThroughLine { get; set; }
}
