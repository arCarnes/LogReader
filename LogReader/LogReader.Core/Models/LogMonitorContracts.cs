namespace LogReader.Core.Models;

using System.Collections.Immutable;

public sealed class LogMonitorQuery
{
    public IReadOnlyList<ConfiguredLogTarget> Targets { get; init; } = [];
    public string? Cursor { get; init; }
    public string? HandoffCursor { get; init; }
    public int? ObserveForMilliseconds { get; init; }
    public string? Query { get; init; }
    public string? ExcludeQuery { get; init; }
    public bool UseRegex { get; init; }
    public bool CaseSensitive { get; init; }
    public int DateOffsetDays { get; init; }
    public string? StartTimestamp { get; init; }
    public string? EndTimestamp { get; init; }
    public string? RelativeWindow { get; init; }
    public int MaxFiles { get; init; } = 10;
    public int? MaxLines { get; init; }
    public int? TimeoutMilliseconds { get; init; }
    public bool Release { get; init; }
}

public sealed class LogMonitorResult
{
    public int ContractVersion { get; init; } = 1;
    public string? NextCursor { get; init; }
    public string State { get; init; } = "observing";
    public string? ObservationEndsAt { get; init; }
    public string SelectedDate { get; init; } = string.Empty;
    public int SelectedFileCount { get; init; }
    public int CheckedFileCount { get; init; }
    public int UncheckedFileCount { get; init; }
    public int BacklogFileCount { get; init; }
    public int StoppedFileCount { get; init; }
    public bool IsScopeChecked { get; init; }
    public bool IsCaughtUp { get; init; }
    public bool IsIdle { get; init; }
    public long BytesEvaluated { get; init; }
    public int ExaminedLineCount { get; init; }
    public LogTimestampCoverage TimestampCoverage { get; init; } = new(0, 0, 0);
    public LogCountResolvedTimeRange? ResolvedTimeRange { get; init; }
    public ImmutableArray<LogMonitorFileResult> Files { get; init; } = [];
}

public sealed record LogMonitorFileResult(
    string FileId,
    string DisplayName,
    ImmutableArray<ConfiguredLogProvenance> Provenance,
    string? Generation,
    string State,
    long RemainingBytes,
    ImmutableArray<LogMonitorEvent> Events,
    ConfiguredLogRequestError? Error)
{
    public int ProvenanceTotalCount { get; init; }
    public bool IsProvenanceTruncated { get; init; }
}

/// <summary>Identity is file + generation + byteOffset. Updated replaces and removed deletes that identity.</summary>
public sealed record LogMonitorEvent(string Kind, long ByteOffset, string? Text, bool IsTruncated);
