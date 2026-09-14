namespace LogReader.Core.Models;

using System.Collections.Immutable;

public sealed class LogWqlQuery
{
    public IReadOnlyList<ConfiguredLogTarget> Targets { get; init; } = [];
    public string Query { get; init; } = string.Empty;
    public string? ProfileId { get; init; }
    public bool CaseSensitive { get; init; }
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

public sealed record FieldProfileSchema(string Id, string Name, string Revision, ImmutableArray<FieldSchema> Fields);
public sealed record FieldSchema(string Name, StructuredFieldType Type);
public sealed record FieldProfilesResult(ImmutableArray<FieldProfileSchema> Profiles, ImmutableArray<FieldSchema> Builtins, int? NextStartIndex);

public sealed record LogWqlResult(
    string? ProfileId,
    string ProfileRevision,
    ImmutableArray<LogWqlFileResult> Files,
    string? NextCursor,
    bool IsPageComplete,
    bool IsQueryComplete,
    ImmutableArray<string> IncompleteReasons,
    LogQueryEffectiveLimits EffectiveLimits);

public sealed record LogWqlFileResult(
    string FileId,
    string DisplayName,
    string? Generation,
    ImmutableArray<LogWqlHit> Hits,
    LogWqlParsingStatistics? Parsing,
    ConfiguredLogRequestError? Error,
    bool IsTruncated,
    ImmutableArray<string> IncompleteReasons);

public sealed record LogWqlHit(
    long LineNumber,
    string Text,
    bool IsTextTruncated,
    bool AreFieldsTruncated,
    ImmutableDictionary<string, StructuredFieldValue> Fields,
    ImmutableArray<LogLineResult> ContextBefore,
    ImmutableArray<LogLineResult> ContextAfter);

public sealed record LogWqlParsingStatistics(
    long EvaluatedLineCount,
    bool IsScanComplete,
    ImmutableDictionary<string, StructuredFieldStatistics> Fields,
    bool IsTruncated);
