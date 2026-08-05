namespace LogReader.Core.Models;

using System.Collections.Immutable;
using System.Text.Json.Serialization;

public sealed class ConfiguredLogCatalogSnapshot
{
    private object? _catalogIndexCache;

    public ConfiguredLogCatalogSnapshot(
        int sourceFormatVersion,
        IEnumerable<ConfiguredLogGroup> groups,
        IEnumerable<ConfiguredLogFile> files,
        IEnumerable<ConfiguredDatePathPattern>? datePathPatterns = null,
        ConfiguredLogCatalogSnapshotDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(files);

        SourceFormatVersion = sourceFormatVersion;
        Groups = groups.Select(static group => group.Copy()).ToImmutableArray();
        Files = files.Select(static file => file with { }).ToImmutableArray();
        DatePathPatterns = (datePathPatterns ?? Enumerable.Empty<ConfiguredDatePathPattern>())
            .Select(static pattern => pattern with { })
            .ToImmutableArray();
        Diagnostics = diagnostics ?? ConfiguredLogCatalogSnapshotDiagnostics.Empty;
        Revision = ConfiguredLogCatalogRevision.Calculate(
            SourceFormatVersion,
            Groups,
            Files,
            DatePathPatterns);
    }

    public int SourceFormatVersion { get; }

    public string Revision { get; }

    public ImmutableArray<ConfiguredLogGroup> Groups { get; }

    [JsonIgnore]
    public ImmutableArray<ConfiguredLogFile> Files { get; }

    [JsonIgnore]
    public ImmutableArray<ConfiguredDatePathPattern> DatePathPatterns { get; }

    public ConfiguredLogCatalogSnapshotDiagnostics Diagnostics { get; }

    public static ConfiguredLogCatalogSnapshot FromModels(
        int sourceFormatVersion,
        IEnumerable<LogGroup> groups,
        IEnumerable<LogFileEntry> files,
        IEnumerable<ReplacementPattern>? datePathPatterns = null,
        ConfiguredLogCatalogSnapshotDiagnostics? diagnostics = null)
        => new(
            sourceFormatVersion,
            groups.Select(ConfiguredLogGroup.FromModel),
            files.Select(ConfiguredLogFile.FromModel),
            datePathPatterns?.Select(ConfiguredDatePathPattern.FromModel),
            diagnostics);

    internal object GetOrCreateCatalogIndexCache(Func<object> factory)
    {
        var cached = Volatile.Read(ref _catalogIndexCache);
        if (cached != null)
            return cached;

        var created = factory();
        return Interlocked.CompareExchange(ref _catalogIndexCache, created, null) ?? created;
    }
}

public sealed record ConfiguredLogGroup(
    string Id,
    string Name,
    int SortOrder,
    string? ParentGroupId,
    LogGroupKind Kind,
    ImmutableArray<string> FileIds)
{
    public static ConfiguredLogGroup FromModel(LogGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new ConfiguredLogGroup(
            group.Id,
            group.Name,
            group.SortOrder,
            group.ParentGroupId,
            group.Kind,
            group.FileIds?.ToImmutableArray() ?? default);
    }

    internal ConfiguredLogGroup Copy()
        => this with
        {
            FileIds = FileIds.IsDefault
                ? default
                : FileIds.ToImmutableArray()
        };
}

public sealed record ConfiguredLogFile(
    string Id,
    [property: JsonIgnore] string PhysicalPath)
{
    public static ConfiguredLogFile FromModel(LogFileEntry file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new ConfiguredLogFile(file.Id, file.FilePath);
    }
}

public sealed record ConfiguredDatePathPattern(
    string Id,
    string Name,
    string FindPattern,
    string ReplacePattern)
{
    public static ConfiguredDatePathPattern FromModel(ReplacementPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return new ConfiguredDatePathPattern(
            pattern.Id,
            pattern.Name,
            pattern.FindPattern,
            pattern.ReplacePattern);
    }
}

public sealed record ConfiguredLogCatalogSnapshotDiagnostics(
    bool GroupsStorePresent,
    bool FilesStorePresent,
    bool SettingsStorePresent,
    int ReadAttemptCount)
{
    public static ConfiguredLogCatalogSnapshotDiagnostics Empty { get; } = new(
        GroupsStorePresent: false,
        FilesStorePresent: false,
        SettingsStorePresent: false,
        ReadAttemptCount: 0);
}
