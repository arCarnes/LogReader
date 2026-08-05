namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using System.Text.Json.Serialization;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed class PersistedDashboardSnapshotReader : IConfiguredLogCatalogReader, IDisposable
{
    private const string GroupsFileName = "loggroups.json";
    private const string FilesFileName = "logfiles.json";
    private const string SettingsFileName = "settings.json";
    private const int CurrentSchemaVersion = 1;
    internal const int DefaultMaximumStoreBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 64,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly INonInteractiveStorageRootResolver _storageRootResolver;
    private readonly IPersistedSnapshotFileSystem _fileSystem;
    private readonly PersistedDashboardSnapshotReaderOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CacheEntry? _cache;
    private bool _disposed;

    public PersistedDashboardSnapshotReader()
        : this(
            new NonInteractiveStorageRootResolver(),
            new PersistedSnapshotFileSystem(),
            PersistedDashboardSnapshotReaderOptions.Default)
    {
    }

    internal PersistedDashboardSnapshotReader(
        INonInteractiveStorageRootResolver storageRootResolver,
        IPersistedSnapshotFileSystem fileSystem,
        PersistedDashboardSnapshotReaderOptions options)
    {
        _storageRootResolver = storageRootResolver;
        _fileSystem = fileSystem;
        _options = options;
    }

    public async Task<ConfiguredLogCatalogReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PersistedDashboardSnapshotReader));

        string storageRoot;
        try
        {
            storageRoot = Path.GetFullPath(_storageRootResolver.ResolveStorageRoot());
        }
        catch (StorageSetupRequiredException)
        {
            return Failure(
                ConfiguredLogCatalogReadErrorCodes.StorageNotConfigured,
                "WeezTail storage has not been configured. Launch WeezTail once and choose a storage folder.");
        }
        catch (InstallConfigurationException)
        {
            return Failure(
                ConfiguredLogCatalogReadErrorCodes.InstallConfigurationInvalid,
                "The WeezTail install configuration is missing or invalid. Repair the installation or launch WeezTail for details.");
        }
        catch (UnauthorizedAccessException)
        {
            return AccessDenied();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return StorageUnavailable();
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }

        try
        {
            if (!Directory.Exists(storageRoot))
                return StorageUnavailable();

            return await ReadCoreAsync(storageRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (UnauthorizedAccessException)
        {
            return AccessDenied();
        }
        catch (PersistedStoreTooLargeException)
        {
            return Failure(
                ConfiguredLogCatalogReadErrorCodes.CatalogTooLarge,
                "A WeezTail catalog store exceeds the safe read limit. Open WeezTail to inspect or repair its saved data.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return StorageUnavailable();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _gate.Dispose();
        _cache = null;
    }

    private async Task<ConfiguredLogCatalogReadResult> ReadCoreAsync(
        string storageRoot,
        CancellationToken cancellationToken)
    {
        var paths = StorePaths.ForStorageRoot(storageRoot);
        var sawTemporaryArtifact = false;
        for (var attempt = 1; attempt <= _options.MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (HasTemporaryArtifact(paths))
                {
                    sawTemporaryArtifact = true;
                    if (attempt < _options.MaximumAttempts)
                    {
                        await _options.DelayAsync(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    break;
                }

                var before = CaptureStamps(paths);
                if (HasMissingRecoveryArtifact(paths, before))
                    return RecoveryRequired();

                if (_cache is { } cache &&
                    string.Equals(cache.StorageRoot, storageRoot, StringComparison.OrdinalIgnoreCase) &&
                    cache.Stamps == before)
                {
                    return ConfiguredLogCatalogReadResult.Success(cache.Snapshot, isCacheHit: true);
                }

                var firstRead = await ReadStoresAsync(paths, cancellationToken).ConfigureAwait(false);
                var middle = CaptureStamps(paths);
                var secondRead = await ReadStoresAsync(paths, cancellationToken).ConfigureAwait(false);
                var after = CaptureStamps(paths);
                if (before != middle || middle != after || !StorePayloads.ContentEquals(firstRead, secondRead))
                {
                    if (attempt < _options.MaximumAttempts)
                    {
                        await _options.DelayAsync(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return Failure(
                        ConfiguredLogCatalogReadErrorCodes.CatalogUnstable,
                        "The saved WeezTail catalog changed repeatedly while it was being read. Retry the request.",
                        isRetryable: true);
                }

                var result = ParseSnapshot(paths, secondRead, attempt);
                if (!result.IsSuccess)
                    return result;

                _cache = new CacheEntry(storageRoot, after, result.Snapshot!);
                return result;
            }
            catch (IOException ex) when (ex is not PersistedStoreTooLargeException)
            {
                if (attempt < _options.MaximumAttempts)
                {
                    await _options.DelayAsync(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return StorageUnavailable();
            }
        }

        return Failure(
            ConfiguredLogCatalogReadErrorCodes.CatalogUnstable,
            sawTemporaryArtifact
                ? "A WeezTail catalog write remained in progress while the catalog was being read. Retry the request."
                : "The saved WeezTail catalog did not become stable. Retry the request.",
            isRetryable: true);
    }

    private StoreStamps CaptureStamps(StorePaths paths)
        => new(
            _fileSystem.GetStamp(paths.Groups),
            _fileSystem.GetStamp(paths.Files),
            _fileSystem.GetStamp(paths.Settings));

    private async Task<StorePayloads> ReadStoresAsync(
        StorePaths paths,
        CancellationToken cancellationToken)
        => new(
            await _fileSystem.ReadAllBytesAsync(
                paths.Groups,
                _options.MaximumStoreBytes,
                cancellationToken).ConfigureAwait(false),
            await _fileSystem.ReadAllBytesAsync(
                paths.Files,
                _options.MaximumStoreBytes,
                cancellationToken).ConfigureAwait(false),
            await _fileSystem.ReadAllBytesAsync(
                paths.Settings,
                _options.MaximumStoreBytes,
                cancellationToken).ConfigureAwait(false));

    private bool HasTemporaryArtifact(StorePaths paths)
        => _fileSystem.HasTemporaryArtifact(paths.Groups) ||
           _fileSystem.HasTemporaryArtifact(paths.Files) ||
           _fileSystem.HasTemporaryArtifact(paths.Settings);

    private bool HasMissingRecoveryArtifact(StorePaths paths, StoreStamps stamps)
        => (!stamps.Groups.Exists && _fileSystem.HasRecoveryArtifact(paths.Groups)) ||
           (!stamps.Files.Exists && _fileSystem.HasRecoveryArtifact(paths.Files)) ||
           (!stamps.Settings.Exists && _fileSystem.HasRecoveryArtifact(paths.Settings));

    private ConfiguredLogCatalogReadResult ParseSnapshot(
        StorePaths paths,
        StorePayloads payloads,
        int attempt)
    {
        var groupsStore = ParseEnvelope<List<LogGroup>>(payloads.Groups, "dashboard view");
        if (groupsStore.Error != null)
            return ConfiguredLogCatalogReadResult.Failure(
                groupsStore.Error.Code,
                groupsStore.Error.Message,
                groupsStore.Error.IsRetryable);

        var filesStore = ParseEnvelope<List<LogFileEntry>>(payloads.Files, "log file metadata");
        if (filesStore.Error != null)
            return ConfiguredLogCatalogReadResult.Failure(
                filesStore.Error.Code,
                filesStore.Error.Message,
                filesStore.Error.IsRetryable);

        var settingsStore = ParseEnvelope<AppSettings>(payloads.Settings, "settings");
        if (settingsStore.Error != null)
            return ConfiguredLogCatalogReadResult.Failure(
                settingsStore.Error.Code,
                settingsStore.Error.Message,
                settingsStore.Error.IsRetryable);

        if ((groupsStore.Data == null && _fileSystem.HasRecoveryArtifact(paths.Groups)) ||
            (filesStore.Data == null && _fileSystem.HasRecoveryArtifact(paths.Files)) ||
            (settingsStore.Data == null && _fileSystem.HasRecoveryArtifact(paths.Settings)))
        {
            return RecoveryRequired();
        }

        var groups = groupsStore.Data ?? [];
        var files = filesStore.Data ?? [];
        var settings = settingsStore.Data ?? new AppSettings();
        try
        {
            DashboardTopologyValidator.ValidatePersistedGroups(groups);
            ValidateFiles(files);
            ValidateDatePatterns(settings.DateRollingPatterns);
        }
        catch (InvalidDataException)
        {
            return RecoveryRequired();
        }

        var fileIds = files.Select(file => file.Id).ToHashSet(StringComparer.Ordinal);
        var hasMissingMembership = groups
            .Where(group => group.Kind == LogGroupKind.Dashboard)
            .SelectMany(group => group.FileIds)
            .Any(fileId => !fileIds.Contains(fileId));
        if (hasMissingMembership)
        {
            return filesStore.Data == null
                ? Failure(
                    ConfiguredLogCatalogReadErrorCodes.CatalogStoreMissing,
                    "The saved dashboard references log metadata that is missing. Launch WeezTail once to restore or repair the catalog.")
                : RecoveryRequired();
        }

        var diagnostics = new ConfiguredLogCatalogSnapshotDiagnostics(
            GroupsStorePresent: groupsStore.Data != null,
            FilesStorePresent: filesStore.Data != null,
            SettingsStorePresent: settingsStore.Data != null,
            ReadAttemptCount: attempt);
        var snapshot = ConfiguredLogCatalogSnapshot.FromModels(
            CurrentSchemaVersion,
            groups,
            files,
            settings.DateRollingPatterns ?? [],
            diagnostics);
        if (!ConfiguredLogCatalogIndex.TryCreate(snapshot, out _, out _))
            return RecoveryRequired();

        return ConfiguredLogCatalogReadResult.Success(snapshot);
    }

    private static ParsedStore<T> ParseEnvelope<T>(byte[]? contents, string storeName)
        where T : class
    {
        if (contents == null)
            return new ParsedStore<T>(null, null);

        try
        {
            using var document = JsonDocument.Parse(contents, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data))
            {
                return new ParsedStore<T>(
                    null,
                    new ConfiguredLogCatalogReadError(
                        ConfiguredLogCatalogReadErrorCodes.MigrationRequired,
                        $"The saved {storeName} uses a legacy format. Launch WeezTail once to migrate it before using MCP log tools.",
                        IsRetryable: false));
            }

            if (!root.TryGetProperty("schemaVersion", out var schemaElement) ||
                !schemaElement.TryGetInt32(out var schemaVersion))
            {
                return new ParsedStore<T>(null, RecoveryError());
            }

            if (schemaVersion < CurrentSchemaVersion)
            {
                return new ParsedStore<T>(
                    null,
                    new ConfiguredLogCatalogReadError(
                        ConfiguredLogCatalogReadErrorCodes.MigrationRequired,
                        $"The saved {storeName} needs migration. Launch WeezTail once before using MCP log tools.",
                        IsRetryable: false));
            }

            if (schemaVersion > CurrentSchemaVersion)
            {
                return new ParsedStore<T>(
                    null,
                    new ConfiguredLogCatalogReadError(
                        ConfiguredLogCatalogReadErrorCodes.UnsupportedSchema,
                        $"The saved {storeName} was written by a newer incompatible WeezTail version.",
                        IsRetryable: false));
            }

            if (data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return new ParsedStore<T>(null, RecoveryError());

            if (!HasRequiredPayloadShape<T>(data))
                return new ParsedStore<T>(null, RecoveryError());

            var parsed = data.Deserialize<T>(SerializerOptions);
            return parsed == null
                ? new ParsedStore<T>(null, RecoveryError())
                : new ParsedStore<T>(parsed, null);
        }
        catch (JsonException)
        {
            return new ParsedStore<T>(null, RecoveryError());
        }
    }

    private static bool HasRequiredPayloadShape<T>(JsonElement data)
    {
        if (typeof(T) == typeof(List<LogGroup>))
            return HasRequiredGroupShape(data);
        if (typeof(T) == typeof(List<LogFileEntry>))
            return HasRequiredFileShape(data);
        if (typeof(T) == typeof(AppSettings))
            return HasRequiredSettingsShape(data);

        return true;
    }

    private static bool HasRequiredGroupShape(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var group in data.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object ||
                !HasNonBlankString(group, "id") ||
                !HasString(group, "name") ||
                !TryGetPropertyIgnoreCase(group, "sortOrder", out var sortOrder) ||
                !sortOrder.TryGetInt32(out _) ||
                !TryGetPropertyIgnoreCase(group, "kind", out var kind) ||
                kind.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
                !TryGetPropertyIgnoreCase(group, "fileIds", out var fileIds) ||
                fileIds.ValueKind != JsonValueKind.Array ||
                fileIds.EnumerateArray().Any(fileId =>
                    fileId.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(fileId.GetString())))
            {
                return false;
            }

            if (TryGetPropertyIgnoreCase(group, "parentGroupId", out var parentId) &&
                parentId.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasRequiredFileShape(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var file in data.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object ||
                !HasNonBlankString(file, "id") ||
                !HasNonBlankString(file, "filePath"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasRequiredSettingsShape(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return false;
        if (!TryGetPropertyIgnoreCase(data, "dateRollingPatterns", out var patterns))
            return true;
        if (patterns.ValueKind == JsonValueKind.Null)
            return true;
        if (patterns.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var pattern in patterns.EnumerateArray())
        {
            if (pattern.ValueKind != JsonValueKind.Object ||
                !HasNonBlankString(pattern, "id") ||
                !HasString(pattern, "findPattern") ||
                !HasString(pattern, "replacePattern"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasNonBlankString(JsonElement element, string propertyName)
        => TryGetPropertyIgnoreCase(element, propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(property.GetString());

    private static bool HasString(JsonElement element, string propertyName)
        => TryGetPropertyIgnoreCase(element, propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String;

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static void ValidateFiles(IReadOnlyList<LogFileEntry> files)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.FilePath))
                throw new InvalidDataException("The saved log file metadata is incomplete.");
            if (!ids.Add(file.Id) || !paths.Add(file.FilePath))
                throw new InvalidDataException("The saved log file metadata contains duplicate entries.");
        }
    }

    private static void ValidateDatePatterns(IReadOnlyList<ReplacementPattern>? patterns)
    {
        if (patterns == null)
            return;

        if (patterns.Any(pattern => pattern == null))
            throw new InvalidDataException("The saved date-path patterns contain a null entry.");
    }

    private static ConfiguredLogCatalogReadError RecoveryError()
        => new(
            ConfiguredLogCatalogReadErrorCodes.RecoveryRequired,
            "Saved WeezTail data is malformed and needs interactive recovery. Launch WeezTail once before using MCP log tools.",
            IsRetryable: false);

    private static ConfiguredLogCatalogReadResult RecoveryRequired()
        => ConfiguredLogCatalogReadResult.Failure(
            ConfiguredLogCatalogReadErrorCodes.RecoveryRequired,
            "Saved WeezTail data needs interactive recovery. Launch WeezTail once before using MCP log tools.");

    private static ConfiguredLogCatalogReadResult Failure(
        string code,
        string message,
        bool isRetryable = false)
        => ConfiguredLogCatalogReadResult.Failure(code, message, isRetryable);

    private static ConfiguredLogCatalogReadResult AccessDenied()
        => Failure(
            ConfiguredLogCatalogReadErrorCodes.StorageAccessDenied,
            "WeezTail's saved catalog could not be read with the current Windows user's permissions.");

    private static ConfiguredLogCatalogReadResult StorageUnavailable()
        => Failure(
            ConfiguredLogCatalogReadErrorCodes.StorageUnavailable,
            "WeezTail's configured storage is unavailable. Check the storage device or network location and retry.",
            isRetryable: true);

    private static ConfiguredLogCatalogReadResult Cancelled()
        => Failure(
            ConfiguredLogCatalogReadErrorCodes.ReadCancelled,
            "The configured-log catalog read was cancelled.",
            isRetryable: true);

    private sealed record ParsedStore<T>(T? Data, ConfiguredLogCatalogReadError? Error)
        where T : class;

    private sealed record CacheEntry(
        string StorageRoot,
        StoreStamps Stamps,
        ConfiguredLogCatalogSnapshot Snapshot);

    private readonly record struct StorePaths(string Groups, string Files, string Settings)
    {
        internal static StorePaths ForStorageRoot(string storageRoot)
        {
            var dataDirectory = Path.Combine(storageRoot, AppPaths.DataFolderName);
            return new StorePaths(
                Path.Combine(dataDirectory, GroupsFileName),
                Path.Combine(dataDirectory, FilesFileName),
                Path.Combine(dataDirectory, SettingsFileName));
        }
    }

    private readonly record struct StoreStamps(
        PersistedStoreStamp Groups,
        PersistedStoreStamp Files,
        PersistedStoreStamp Settings);

    private sealed record StorePayloads(byte[]? Groups, byte[]? Files, byte[]? Settings)
    {
        internal static bool ContentEquals(StorePayloads left, StorePayloads right)
            => Equal(left.Groups, right.Groups) &&
               Equal(left.Files, right.Files) &&
               Equal(left.Settings, right.Settings);

        private static bool Equal(byte[]? left, byte[]? right)
            => left == null ? right == null : right != null && left.AsSpan().SequenceEqual(right);
    }
}

internal sealed record PersistedDashboardSnapshotReaderOptions(
    int MaximumAttempts,
    int MaximumStoreBytes,
    TimeSpan RetryDelay,
    Func<TimeSpan, CancellationToken, Task> DelayAsync)
{
    internal static PersistedDashboardSnapshotReaderOptions Default { get; } = new(
        MaximumAttempts: 3,
        MaximumStoreBytes: PersistedDashboardSnapshotReader.DefaultMaximumStoreBytes,
        RetryDelay: TimeSpan.FromMilliseconds(25),
        DelayAsync: static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));
}
