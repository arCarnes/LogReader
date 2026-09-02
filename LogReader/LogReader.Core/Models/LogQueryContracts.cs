namespace LogReader.Core.Models;

using System.Collections.Immutable;

public sealed class LogSearchQuery
{
    public IReadOnlyList<ConfiguredLogTarget> Targets { get; init; } = [];

    public string Query { get; init; } = string.Empty;

    public bool UseRegex { get; init; }

    public bool CaseSensitive { get; init; }

    public string ResultMode { get; init; } = "samples";

    public string? Cursor { get; init; }

    public int DateOffsetDays { get; init; }

    public string? StartTimestamp { get; init; }

    public string? EndTimestamp { get; init; }

    public int? MaxFiles { get; init; }

    public int? MaxHitsPerFile { get; init; }

    public int? MaxTotalHits { get; init; }

    public int IncludeContextBefore { get; init; }

    public int IncludeContextAfter { get; init; }

    public int? TimeoutMilliseconds { get; init; }
}

public sealed class LogSearchResult
{
    public const int CurrentContractVersion = 2;

    public int ContractVersion { get; init; } = CurrentContractVersion;

    public string ResultMode { get; init; } = "samples";

    public ImmutableArray<LogSearchFileResult> Files { get; init; } = [];

    public int SelectedFileCount { get; init; }

    public int SearchedFileCount { get; init; }

    public int TotalHitCount { get; init; }

    public int ReturnedHitCount { get; init; }

    public string? NextCursor { get; init; }

    public long PageMatchingLineCount { get; init; }

    public long PageMatchOccurrenceCount { get; init; }

    public long MatchingLineCount { get; init; }

    public long MatchOccurrenceCount { get; init; }

    public int SkippedFileCount { get; init; }

    public int FailedFileCount { get; init; }

    public int RemainingFileCount { get; init; }

    public int MatchedFileCount { get; init; }

    public bool ArePageCountsExact { get; init; }

    public bool AreQueryCountsExact { get; init; }

    public bool IsPageComplete { get; init; }

    public bool IsQueryComplete { get; init; }

    public string CompletionState { get; init; } = "incomplete";

    public ImmutableArray<string> IncompleteReasons { get; init; } = [];

    public ImmutableArray<string> PageIncompleteReasons { get; init; } = [];

    public LogSearchStatistics Statistics { get; init; } = LogSearchStatistics.Empty;

    public LogQueryEffectiveLimits EffectiveLimits { get; init; } = LogQueryEffectiveLimits.Default;
}

public sealed record LogSearchFileResult(
    string FileId,
    string DisplayName,
    ImmutableArray<ConfiguredLogProvenance> Provenance,
    string Encoding,
    string? Generation,
    ImmutableArray<LogSearchHit> Hits,
    ConfiguredLogRequestError? Error,
    bool IsTruncated)
{
    public int ProvenanceTotalCount { get; init; }

    public bool IsProvenanceTruncated { get; init; }

    public long MatchingLineCount { get; init; }

    public long MatchOccurrenceCount { get; init; }

    public bool IsCountExact { get; init; }

    public long? EvaluatedThroughLine { get; init; }

    public ImmutableArray<string> IncompleteReasons { get; init; } = [];
}

public sealed record LogSearchStatistics(
    long BytesEvaluated,
    long ElapsedMilliseconds,
    int FilesStarted,
    int FilesCompleted,
    int FilesSkipped,
    int PeakConcurrentDiskOperations,
    int PeakConcurrentUncOperations)
{
    public static LogSearchStatistics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

public sealed record LogSearchHit(
    long LineNumber,
    string Text,
    bool IsTextTruncated,
    int MatchStart,
    int MatchLength,
    ImmutableArray<LogLineResult> ContextBefore,
    ImmutableArray<LogLineResult> ContextAfter);

public sealed class LogCountQuery
{
    public IReadOnlyList<ConfiguredLogTarget> Targets { get; init; } = [];

    public string Query { get; init; } = string.Empty;

    public bool UseRegex { get; init; }

    public bool CaseSensitive { get; init; }

    public int DateOffsetDays { get; init; }

    public string? StartTimestamp { get; init; }

    public string? EndTimestamp { get; init; }

    public string? RelativeWindow { get; init; }

    public string BucketSize { get; init; } = "none";

    public int? TimeoutMilliseconds { get; init; }
}

public sealed class LogCountResult
{
    public const int CurrentContractVersion = 1;

    public int ContractVersion { get; init; } = CurrentContractVersion;

    public long MatchingLineCount { get; init; }

    public long MatchOccurrenceCount { get; init; }

    public long UnbucketedMatchingLineCount { get; init; }

    public long UnbucketedMatchOccurrenceCount { get; init; }

    public int SelectedFileCount { get; init; }

    public int SearchedFileCount { get; init; }

    public int MatchedFileCount { get; init; }

    public int SkippedFileCount { get; init; }

    public int FailedFileCount { get; init; }

    public int RemainingFileCount { get; init; }

    public bool AreCountsExact { get; init; }

    public bool IsComplete { get; init; }

    public string CompletionState { get; init; } = "incomplete";

    public ImmutableArray<string> IncompleteReasons { get; init; } = [];

    public LogCountResolvedTimeRange? ResolvedTimeRange { get; init; }

    public string BucketSize { get; init; } = "none";

    public ImmutableArray<LogCountBucket> Buckets { get; init; } = [];

    public ImmutableArray<LogCountFileResult> Files { get; init; } = [];

    public int FileRecordTotalCount { get; init; }

    public int ReturnedFileRecordCount { get; init; }

    public bool IsFileRecordTruncated { get; init; }

    public LogSearchStatistics Statistics { get; init; } = LogSearchStatistics.Empty;

    public LogQueryEffectiveLimits EffectiveLimits { get; init; } = LogQueryEffectiveLimits.Default;
}

public sealed record LogCountResolvedTimeRange(
    string Kind,
    string Start,
    string End,
    string TimeZoneId,
    string? RelativeWindow);

public sealed record LogCountBucket(
    string Kind,
    string Start,
    string EndExclusive,
    long MatchingLineCount,
    long MatchOccurrenceCount);

public sealed record LogCountFileResult(
    string FileId,
    string DisplayName,
    ImmutableArray<ConfiguredLogProvenance> Provenance,
    string Encoding,
    string? Generation,
    ConfiguredLogRequestError? Error)
{
    public int ProvenanceTotalCount { get; init; }

    public bool IsProvenanceTruncated { get; init; }

    public long MatchingLineCount { get; init; }

    public long MatchOccurrenceCount { get; init; }

    public bool IsCountExact { get; init; }

    public ImmutableArray<string> IncompleteReasons { get; init; } = [];
}

public sealed class LogReadLinesQuery
{
    public string FileId { get; init; } = string.Empty;

    public int StartLine { get; init; } = 1;

    public int? Count { get; init; }

    public int DateOffsetDays { get; init; }

    public int? TimeoutMilliseconds { get; init; }
}

public sealed class LogReadLinesResult
{
    public LogReadFileResult? File { get; init; }

    public int RequestedStartLine { get; init; }

    public int RequestedCount { get; init; }

    public int? ActualStartLine { get; init; }

    public int? ActualEndLine { get; init; }

    public int TotalLineCount { get; init; }
}

public sealed class LogReadTailQuery
{
    public string FileId { get; init; } = string.Empty;

    public string? Cursor { get; init; }

    public int? MaxLines { get; init; }

    public int DateOffsetDays { get; init; }

    public int? TimeoutMilliseconds { get; init; }
}

public sealed class LogReadTailResult
{
    public LogReadFileResult? File { get; init; }

    public string? NextCursor { get; init; }

    public bool GenerationChanged { get; init; }

    public bool LastLineUpdated { get; init; }

    public int TotalLineCount { get; init; }
}

public sealed record LogReadFileResult(
    string FileId,
    string DisplayName,
    ImmutableArray<ConfiguredLogProvenance> Provenance,
    string Encoding,
    string? Generation,
    ImmutableArray<LogLineResult> Lines,
    ConfiguredLogRequestError? Error)
{
    public int ProvenanceTotalCount { get; init; }

    public bool IsProvenanceTruncated { get; init; }
}

public sealed record LogLineResult(
    int LineNumber,
    string Text,
    bool IsTruncated);

public sealed class LogQueryStatus
{
    public bool IsReady { get; init; }

    public string ConnectionState { get; init; } = "ready";

    public LogQueryEffectiveLimits Limits { get; init; } = LogQueryEffectiveLimits.Default;

    public int ActiveIndexedSessions { get; init; }

    public int RetainedIndexedSessions { get; init; }

    public int MappedLineOffsets { get; init; }
}

public sealed record LogQueryEffectiveLimits(
    int MaximumTargets,
    int MaximumFiles,
    int MaximumQueryCharacters,
    int MaximumHitsPerFile,
    int MaximumTotalHits,
    int MaximumCharactersPerLine,
    int MaximumContextLines,
    int DefaultReadLineCount,
    int MaximumReadLineCount,
    int MaximumResponseCharacters,
    int MaximumConcurrentDiskOperations,
    int DefaultTimeoutMilliseconds,
    int MaximumIndexedSessions,
    int MaximumMappedLineOffsets,
    int IndexedSessionWarmRetentionMilliseconds)
{
    public int MaximumSearchCandidates { get; init; } = ConfiguredLogLimits.DefaultMaxSearchCandidates;

    public int MaximumCountBuckets { get; init; } = ConfiguredLogLimits.DefaultMaxCountBuckets;

    public int MaximumRelativeWindowDays { get; init; } = ConfiguredLogLimits.DefaultMaxRelativeWindowDays;

    public static LogQueryEffectiveLimits Default { get; } = new(
        ConfiguredLogLimits.DefaultMaxTargets,
        ConfiguredLogLimits.DefaultMaxResolvedFiles,
        4_096,
        50,
        500,
        4_096,
        20,
        200,
        1_000,
        200_000,
        2,
        30_000,
        4,
        2_000_000,
        30_000);
}
