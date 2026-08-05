namespace LogReader.Core.Models;

using System.Collections.Immutable;

public sealed class LogSearchQuery
{
    public IReadOnlyList<ConfiguredLogTarget> Targets { get; init; } = [];

    public string Query { get; init; } = string.Empty;

    public bool UseRegex { get; init; }

    public bool CaseSensitive { get; init; }

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
    public ImmutableArray<LogSearchFileResult> Files { get; init; } = [];

    public int SelectedFileCount { get; init; }

    public int SearchedFileCount { get; init; }

    public int TotalHitCount { get; init; }

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
    bool IsTruncated);

public sealed record LogSearchHit(
    long LineNumber,
    string Text,
    bool IsTextTruncated,
    int MatchStart,
    int MatchLength,
    ImmutableArray<LogLineResult> ContextBefore,
    ImmutableArray<LogLineResult> ContextAfter);

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
    ConfiguredLogRequestError? Error);

public sealed record LogLineResult(
    int LineNumber,
    string Text,
    bool IsTruncated);

public sealed class LogQueryStatus
{
    public bool IsReady { get; init; }

    public string ConnectionState { get; init; } = "ready";

    public string CacheOwnership { get; init; } = "process_scoped";

    public LogQueryEffectiveLimits Limits { get; init; } = LogQueryEffectiveLimits.Default;

    public int ActiveIndexedSessions { get; init; }

    public int RetainedIndexedSessions { get; init; }

    public int MappedLineOffsets { get; init; }

    public bool LiveUiAvailable { get; init; }

    public string LastFallbackReason { get; init; } = "none";
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
