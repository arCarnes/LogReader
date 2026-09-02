namespace LogReader.Core.Models;

using System.Collections.Immutable;
using System.Text.Json.Serialization;

public enum ConfiguredLogTargetKind
{
    Folder,
    Dashboard,
    LogFile
}

public sealed record ConfiguredLogTarget(ConfiguredLogTargetKind Kind, string Id);

public sealed record ConfiguredLogRequestError(
    string Code,
    string Message,
    string? TargetId = null,
    ConfiguredLogTargetKind? TargetKind = null,
    bool IsRetryable = false);

public sealed record ConfiguredLogFileError(
    string FileId,
    string DisplayName,
    string Code,
    string Message,
    ImmutableArray<ConfiguredLogProvenance> Provenance);

public sealed record ConfiguredLogProvenance(
    string RequestedTargetId,
    ConfiguredLogTargetKind RequestedTargetKind,
    string TargetTreePath,
    string DashboardId,
    string DashboardTreePath);

public sealed record ResolvedConfiguredLogFile(
    string FileId,
    string DisplayName,
    [property: JsonIgnore] string PhysicalPath,
    ImmutableArray<string> EquivalentFileIds,
    [property: JsonIgnore] ImmutableArray<string> OrderedPathCandidates,
    ImmutableArray<ConfiguredLogProvenance> Provenance);

public interface IConfiguredLogPathCandidateSelector
{
    /// <summary>
    /// Selects one authorized candidate. Implementations may probe availability but must return a value from
    /// <paramref name="orderedCandidates"/>; the resolver revalidates that boundary before publishing a path.
    /// </summary>
    string SelectPath(string fileId, ImmutableArray<string> orderedCandidates);
}

public sealed class ConfiguredLogSelectionRequest
{
    public ConfiguredLogSelectionRequest(
        IEnumerable<ConfiguredLogTarget> targets,
        DateOnly referenceDate,
        int dateOffsetDays = 0,
        int maxTargets = ConfiguredLogLimits.DefaultMaxTargets,
        int maxResolvedFiles = ConfiguredLogLimits.DefaultMaxResolvedFiles,
        ConfiguredLogSelectionContinuation? continuation = null,
        int maxExpandedStableFiles = ConfiguredLogLimits.DefaultMaxSearchCandidates)
    {
        ArgumentNullException.ThrowIfNull(targets);
        Targets = targets.Select(static target => target with { }).ToImmutableArray();
        ReferenceDate = referenceDate;
        DateOffsetDays = dateOffsetDays;
        MaxTargets = maxTargets;
        MaxResolvedFiles = maxResolvedFiles;
        Continuation = continuation;
        MaxExpandedStableFiles = maxExpandedStableFiles;
    }

    public ImmutableArray<ConfiguredLogTarget> Targets { get; }

    public DateOnly ReferenceDate { get; }

    public int DateOffsetDays { get; }

    public int MaxTargets { get; }

    public int MaxResolvedFiles { get; }

    public ConfiguredLogSelectionContinuation? Continuation { get; }

    public int MaxExpandedStableFiles { get; }
}

public sealed record ConfiguredLogSelectionContinuation(
    int NextStableFileIndex,
    ImmutableArray<string> SeenPhysicalPathIdentities);

public sealed record ConfiguredLogSelectionSummary(
    int RequestedTargetCount,
    int ExpandedStableFileCount,
    int ResolvedPhysicalFileCount,
    int FileErrorCount,
    int EffectiveMaxTargets,
    int EffectiveMaxResolvedFiles,
    bool RejectedByLimit)
{
    public int PageCandidateCount { get; init; }

    public int RemainingCandidateCount { get; init; }
}

public sealed class ConfiguredLogSelectionResult
{
    public ConfiguredLogSelectionResult(
        string catalogRevision,
        IEnumerable<ResolvedConfiguredLogFile>? files,
        IEnumerable<ConfiguredLogRequestError>? errors,
        IEnumerable<ConfiguredLogFileError>? fileErrors,
        ConfiguredLogSelectionSummary summary,
        ConfiguredLogSelectionContinuation? continuation = null,
        IReadOnlyDictionary<string, int>? stableFileIndexesById = null)
    {
        CatalogRevision = catalogRevision;
        Files = (files ?? Enumerable.Empty<ResolvedConfiguredLogFile>())
            .Select(CloneFile)
            .ToImmutableArray();
        Errors = (errors ?? Enumerable.Empty<ConfiguredLogRequestError>())
            .Select(static error => error with { })
            .ToImmutableArray();
        FileErrors = (fileErrors ?? Enumerable.Empty<ConfiguredLogFileError>())
            .Select(static error => error with { Provenance = error.Provenance.ToImmutableArray() })
            .ToImmutableArray();
        Summary = summary;
        Continuation = continuation;
        StableFileIndexesById = (stableFileIndexesById ?? new Dictionary<string, int>(StringComparer.Ordinal))
            .ToImmutableDictionary(StringComparer.Ordinal);
    }

    public string CatalogRevision { get; }

    public ImmutableArray<ResolvedConfiguredLogFile> Files { get; }

    public ImmutableArray<ConfiguredLogRequestError> Errors { get; }

    public ImmutableArray<ConfiguredLogFileError> FileErrors { get; }

    public ConfiguredLogSelectionSummary Summary { get; }

    [JsonIgnore]
    public ConfiguredLogSelectionContinuation? Continuation { get; }

    [JsonIgnore]
    public ImmutableDictionary<string, int> StableFileIndexesById { get; }

    public bool HasMore => Continuation != null;

    public bool IsSuccess => Errors.IsEmpty;

    public bool IsPartial => IsSuccess && !FileErrors.IsEmpty;

    private static ResolvedConfiguredLogFile CloneFile(ResolvedConfiguredLogFile file)
        => file with
        {
            EquivalentFileIds = file.EquivalentFileIds.ToImmutableArray(),
            OrderedPathCandidates = file.OrderedPathCandidates.ToImmutableArray(),
            Provenance = file.Provenance.ToImmutableArray()
        };
}

public static class ConfiguredLogLimits
{
    public const int DefaultMaxTargets = 50;
    public const int DefaultMaxResolvedFiles = 50;
    public const int DefaultMaxIdCharacters = 256;
    public const int DefaultMaxNameCharacters = 1_024;
    public const int DefaultMaxTreePathCharacters = 8_192;
    public const int DefaultMaxPhysicalPathCharacters = 32_767;
    public const int DefaultMaxTimestampCharacters = 256;
    public const int DefaultMaxDatePathPatterns = 32;
    public const int DefaultMaxDatePatternCharacters = 4_096;
    public const int DefaultTreeResponseCharacters = 100_000;
    public const int DefaultMaxProvenanceEntries = 500;
    public const int DefaultMaxExpandedStableFiles = 500;
    public const int DefaultMaxSearchCandidates = 2_000;
    public const int DefaultMaxCountBuckets = 1_000;
    public const int DefaultMaxRelativeWindowDays = 365;
    public const int DefaultTreeMaxDepth = 20;
    public const int DefaultTreeMaxNodes = 500;
    public const int HardMaxTreeDepth = 100;
    public const int HardMaxTreeNodes = 5_000;
    public const int HardMaxCatalogFiles = 50_000;
    public const int HardMaxCatalogMemberships = 100_000;
}
