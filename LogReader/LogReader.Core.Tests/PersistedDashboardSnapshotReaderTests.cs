namespace LogReader.Core.Tests;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public sealed class PersistedDashboardSnapshotReaderTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"WeezTailReadOnlySnapshot_{Guid.NewGuid():N}");
    private readonly string _dataDirectory;

    public PersistedDashboardSnapshotReaderTests()
    {
        _dataDirectory = Path.Combine(_root, AppPaths.DataFolderName);
        Directory.CreateDirectory(_dataDirectory);
    }

    [Fact]
    public async Task ReadAsync_CurrentStores_ReturnsImmutableCoherentSnapshotWithoutChangingFiles()
    {
        var groupsPath = WriteEnvelope(
            "loggroups.json",
            new List<LogGroup>
            {
                new()
                {
                    Id = "folder",
                    Name = "Folder",
                    Kind = LogGroupKind.Branch,
                    SortOrder = 0
                },
                new()
                {
                    Id = "dashboard",
                    Name = "Dashboard",
                    Kind = LogGroupKind.Dashboard,
                    ParentGroupId = "folder",
                    SortOrder = 0,
                    FileIds = ["file"]
                }
            });
        var filesPath = WriteEnvelope(
            "logfiles.json",
            new List<LogFileEntry>
            {
                new() { Id = "file", FilePath = Path.Combine(_root, "current", "app.log") }
            });
        var settingsPath = WriteEnvelope(
            "settings.json",
            new AppSettings
            {
                DateRollingPatterns =
                [
                    new ReplacementPattern
                    {
                        Id = "pattern",
                        Name = "Daily",
                        FindPattern = "current",
                        ReplacePattern = "{yyyyMMdd}"
                    }
                ]
            });
        var before = CaptureFiles(groupsPath, filesPath, settingsPath);
        using var reader = CreateReader();

        var result = await reader.ReadAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsCacheHit);
        var snapshot = result.Snapshot!;
        Assert.Equal(["folder", "dashboard"], snapshot.Groups.Select(group => group.Id));
        Assert.Equal("file", Assert.Single(snapshot.Files).Id);
        Assert.Equal("pattern", Assert.Single(snapshot.DatePathPatterns).Id);
        Assert.True(snapshot.Diagnostics.GroupsStorePresent);
        Assert.True(snapshot.Diagnostics.FilesStorePresent);
        Assert.True(snapshot.Diagnostics.SettingsStorePresent);
        Assert.Equal(1, snapshot.Diagnostics.ReadAttemptCount);
        Assert.StartsWith("sha256:", snapshot.Revision, StringComparison.Ordinal);
        AssertFilesUnchanged(before);
    }

    [Fact]
    public async Task ReadAsync_SecondStableReadUsesOneEntryCacheAndSettingsChangeInvalidatesIt()
    {
        WriteEnvelope("loggroups.json", new List<LogGroup>());
        WriteEnvelope("logfiles.json", new List<LogFileEntry>());
        var settingsPath = WriteEnvelope("settings.json", new AppSettings());
        using var reader = CreateReader();

        var first = await reader.ReadAsync();
        var cached = await reader.ReadAsync();
        WriteEnvelope(
            "settings.json",
            new AppSettings
            {
                DateRollingPatterns =
                [
                    new ReplacementPattern
                    {
                        Id = "new",
                        Name = "New",
                        FindPattern = "old",
                        ReplacePattern = "{yyyyMMdd}"
                    }
                ]
            });
        File.SetLastWriteTimeUtc(settingsPath, DateTime.UtcNow.AddSeconds(2));
        var changed = await reader.ReadAsync();

        Assert.True(first.IsSuccess);
        Assert.True(cached.IsCacheHit);
        Assert.Equal(first.Snapshot!.Revision, cached.Snapshot!.Revision);
        Assert.False(changed.IsCacheHit);
        Assert.NotEqual(first.Snapshot.Revision, changed.Snapshot!.Revision);
    }

    [Fact]
    public async Task ReadAsync_NoStoreFiles_ReturnsEmptySnapshotWithoutCreatingThem()
    {
        using var reader = CreateReader();

        var result = await reader.ReadAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Snapshot!.Groups);
        Assert.Empty(result.Snapshot.Files);
        Assert.False(result.Snapshot.Diagnostics.GroupsStorePresent);
        Assert.False(result.Snapshot.Diagnostics.FilesStorePresent);
        Assert.False(result.Snapshot.Diagnostics.SettingsStorePresent);
        Assert.Empty(Directory.EnumerateFiles(_dataDirectory));
    }

    [Fact]
    public async Task ReadAsync_MissingCatalogReferencedByDashboard_ReturnsDistinctMissingStoreError()
    {
        WriteEnvelope(
            "loggroups.json",
            new List<LogGroup>
            {
                new()
                {
                    Id = "dashboard",
                    Name = "Dashboard",
                    Kind = LogGroupKind.Dashboard,
                    FileIds = ["missing"]
                }
            });
        using var reader = CreateReader();

        var result = await reader.ReadAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.CatalogStoreMissing, result.Error!.Code);
    }

    [Fact]
    public async Task ReadAsync_LegacyStoreReturnsMigrationRequiredAndDoesNotRewrite()
    {
        var path = Path.Combine(_dataDirectory, "loggroups.json");
        var legacy = Encoding.UTF8.GetBytes("[]");
        await File.WriteAllBytesAsync(path, legacy);
        var timestamp = File.GetLastWriteTimeUtc(path);
        using var reader = CreateReader();

        var result = await reader.ReadAsync();

        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.MigrationRequired, result.Error!.Code);
        Assert.Equal(legacy, await File.ReadAllBytesAsync(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData("{ invalid", ConfiguredLogCatalogReadErrorCodes.RecoveryRequired)]
    [InlineData("{\"schemaVersion\":1,\"data\":null}", ConfiguredLogCatalogReadErrorCodes.RecoveryRequired)]
    [InlineData("{\"schemaVersion\":2,\"data\":[]}", ConfiguredLogCatalogReadErrorCodes.UnsupportedSchema)]
    [InlineData("{\"schemaVersion\":0,\"data\":[]}", ConfiguredLogCatalogReadErrorCodes.MigrationRequired)]
    public async Task ReadAsync_InvalidOrUnsupportedEnvelopeReturnsStableErrorWithoutMutation(
        string contents,
        string expectedCode)
    {
        var path = Path.Combine(_dataDirectory, "loggroups.json");
        await File.WriteAllTextAsync(path, contents);
        var bytes = await File.ReadAllBytesAsync(path);
        using var reader = CreateReader();

        var result = await reader.ReadAsync();

        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(
        "loggroups.json",
        "{\"schemaVersion\":1,\"data\":[{\"name\":\"Dashboard\",\"sortOrder\":0,\"kind\":\"Dashboard\",\"fileIds\":[]}]}"
    )]
    [InlineData(
        "logfiles.json",
        "{\"schemaVersion\":1,\"data\":[{\"filePath\":\"C:\\\\logs\\\\app.log\"}]}"
    )]
    [InlineData(
        "settings.json",
        "{\"schemaVersion\":1,\"data\":{\"dateRollingPatterns\":[{\"findPattern\":\"old\",\"replacePattern\":\"{yyyyMMdd}\"}]}}"
    )]
    public async Task ReadAsync_MissingPersistedIdsNeverInventsAuthorizationIds(
        string fileName,
        string contents)
    {
        var path = Path.Combine(_dataDirectory, fileName);
        await File.WriteAllTextAsync(path, contents);
        using var reader = CreateReader();

        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();

        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.RecoveryRequired, first.Error!.Code);
        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.RecoveryRequired, second.Error!.Code);
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReadAsync_MissingStoreWithRecoveryArtifactRequiresInteractiveRecovery()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_dataDirectory, "loggroups.corrupt-20260804-120000000.json"),
            "corrupt");
        using var reader = CreateReader();

        var result = await reader.ReadAsync();

        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.RecoveryRequired, result.Error!.Code);
    }

    [Fact]
    public async Task ReadAsync_PersistentTemporaryArtifactRetriesThenReportsUnstableState()
    {
        await File.WriteAllTextAsync(Path.Combine(_dataDirectory, "loggroups.json.tmp"), "pending");
        var delays = 0;
        using var reader = CreateReader(
            options: TestOptions(
                maximumAttempts: 3,
                delay: (_, _) =>
                {
                    delays++;
                    return Task.CompletedTask;
                }));

        var result = await reader.ReadAsync();

        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.CatalogUnstable, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task ReadAsync_ConcurrentReplacementRetriesAndReturnsStableSecondGeneration()
    {
        var fileSystem = new ScriptedSnapshotFileSystem(
            EnvelopeBytes(new List<LogGroup>()),
            EnvelopeBytes(new List<LogFileEntry>()),
            EnvelopeBytes(new AppSettings()),
            groupStamps:
            [
                Stamp(1), Stamp(2), Stamp(2),
                Stamp(2), Stamp(2), Stamp(2)
            ]);
        using var reader = CreateReader(
            fileSystem,
            TestOptions(maximumAttempts: 2));

        var result = await reader.ReadAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Snapshot!.Diagnostics.ReadAttemptCount);
    }

    [Fact]
    public async Task ReadAsync_RepeatedConcurrentReplacementReturnsRetryableUnstableError()
    {
        var fileSystem = new ScriptedSnapshotFileSystem(
            EnvelopeBytes(new List<LogGroup>()),
            EnvelopeBytes(new List<LogFileEntry>()),
            EnvelopeBytes(new AppSettings()),
            groupStamps:
            [Stamp(1), Stamp(2), Stamp(3), Stamp(4), Stamp(5), Stamp(6)]);
        using var reader = CreateReader(fileSystem, TestOptions(maximumAttempts: 2));

        var result = await reader.ReadAsync();

        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.CatalogUnstable, result.Error!.Code);
        Assert.True(result.Error.IsRetryable);
    }

    [Fact]
    public async Task ReadAsync_TransientSharingFailureRetriesAndSucceeds()
    {
        var fileSystem = new ScriptedSnapshotFileSystem(
            EnvelopeBytes(new List<LogGroup>()),
            EnvelopeBytes(new List<LogFileEntry>()),
            EnvelopeBytes(new AppSettings()))
        {
            RemainingReadIoFailures = 1
        };
        using var reader = CreateReader(fileSystem, TestOptions(maximumAttempts: 2));

        var result = await reader.ReadAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Snapshot!.Diagnostics.ReadAttemptCount);
    }

    [Fact]
    public async Task ReadAsync_AccessDeniedAndOversizedStoresReturnDistinctErrors()
    {
        var deniedFileSystem = new ScriptedSnapshotFileSystem(null, null, null)
        {
            ThrowAccessDenied = true
        };
        using var deniedReader = CreateReader(deniedFileSystem);
        var denied = await deniedReader.ReadAsync();

        WriteEnvelope("loggroups.json", new List<LogGroup>());
        using var oversizedReader = CreateReader(options: TestOptions(maximumStoreBytes: 2));
        var oversized = await oversizedReader.ReadAsync();

        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.StorageAccessDenied, denied.Error!.Code);
        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.CatalogTooLarge, oversized.Error!.Code);
    }

    [Fact]
    public async Task ReadAsync_PreCancelledRequestReturnsStructuredCancellation()
    {
        using var reader = CreateReader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await reader.ReadAsync(cancellation.Token);

        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.ReadCancelled, result.Error!.Code);
    }

    [Fact]
    public async Task DefaultResolver_MissingPerUserSelectionDoesNotMigrateLegacySelection()
    {
        var baseDirectory = Path.Combine(_root, "app");
        Directory.CreateDirectory(baseDirectory);
        var currentSelection = Path.Combine(_root, "setup", "WeezTail.msi-user.json");
        var legacySelection = Path.Combine(_root, "legacy", "LogReader.msi-user.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacySelection)!);
        await File.WriteAllTextAsync(legacySelection, "{\"storageRootPath\":\"C:\\\\legacy\"}");
        await File.WriteAllTextAsync(
            Path.Combine(baseDirectory, AppPaths.InstallConfigFileName),
            "{\"installMode\":\"Msi\",\"storageMode\":\"PerUserChoice\"}");
        using var scope = AppPaths.BeginTestScope(
            baseDirectory: baseDirectory,
            msiUserStorageSelectionPath: currentSelection,
            legacyMsiUserStorageSelectionPath: legacySelection,
            defaultStorageRoot: _root,
            allowDebugFallback: false);
        using var reader = new PersistedDashboardSnapshotReader();

        var result = await reader.ReadAsync();

        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.StorageNotConfigured, result.Error!.Code);
        Assert.False(File.Exists(currentSelection));
        Assert.True(File.Exists(legacySelection));
    }

    [Fact]
    public async Task DefaultResolver_MsiAbsoluteUsesSavedActiveStorageWithoutCreatingOtherState()
    {
        WriteEnvelope("loggroups.json", new List<LogGroup>());
        WriteEnvelope("logfiles.json", new List<LogFileEntry>());
        var baseDirectory = Path.Combine(_root, "installed-app");
        Directory.CreateDirectory(baseDirectory);
        var escapedRoot = _root.Replace("\\", "\\\\", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            Path.Combine(baseDirectory, AppPaths.InstallConfigFileName),
            $"{{\"installMode\":\"Msi\",\"storageMode\":\"Absolute\",\"storageRootPath\":\"{escapedRoot}\"}}");
        using var scope = AppPaths.BeginTestScope(
            baseDirectory: baseDirectory,
            defaultStorageRoot: Path.Combine(_root, "unused-default"),
            allowDebugFallback: false);
        using var reader = new PersistedDashboardSnapshotReader();

        var result = await reader.ReadAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Snapshot!.Groups);
        Assert.False(Directory.Exists(Path.Combine(_root, "unused-default")));
        Assert.False(Directory.Exists(Path.Combine(_root, AppPaths.CacheFolderName)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private PersistedDashboardSnapshotReader CreateReader(
        IPersistedSnapshotFileSystem? fileSystem = null,
        PersistedDashboardSnapshotReaderOptions? options = null)
        => new(
            new FixedStorageRootResolver(_root),
            fileSystem ?? new PersistedSnapshotFileSystem(),
            options ?? TestOptions());

    private static PersistedDashboardSnapshotReaderOptions TestOptions(
        int maximumAttempts = 3,
        int maximumStoreBytes = PersistedDashboardSnapshotReader.DefaultMaximumStoreBytes,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(
            maximumAttempts,
            maximumStoreBytes,
            TimeSpan.Zero,
            delay ?? ((_, _) => Task.CompletedTask));

    private string WriteEnvelope<T>(string fileName, T data)
    {
        var path = Path.Combine(_dataDirectory, fileName);
        File.WriteAllBytes(path, EnvelopeBytes(data));
        return path;
    }

    private static byte[] EnvelopeBytes<T>(T data)
        => JsonSerializer.SerializeToUtf8Bytes(
            new VersionedRepositoryEnvelope<T>
            {
                SchemaVersion = 1,
                Data = data
            },
            JsonOptions);

    private static IReadOnlyList<FileSnapshot> CaptureFiles(params string[] paths)
        => paths.Select(path => new FileSnapshot(
            path,
            File.ReadAllBytes(path),
            File.GetCreationTimeUtc(path),
            File.GetLastWriteTimeUtc(path))).ToList();

    private static void AssertFilesUnchanged(IEnumerable<FileSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            Assert.Equal(snapshot.Bytes, File.ReadAllBytes(snapshot.Path));
            Assert.Equal(snapshot.CreationTimeUtc, File.GetCreationTimeUtc(snapshot.Path));
            Assert.Equal(snapshot.LastWriteTimeUtc, File.GetLastWriteTimeUtc(snapshot.Path));
        }
    }

    private static PersistedStoreStamp Stamp(long generation)
        => new(true, generation, generation, generation);

    private sealed record FileSnapshot(
        string Path,
        byte[] Bytes,
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc);

    private sealed class FixedStorageRootResolver : INonInteractiveStorageRootResolver
    {
        private readonly string _root;

        internal FixedStorageRootResolver(string root)
        {
            _root = root;
        }

        public string ResolveStorageRoot() => _root;
    }

    private sealed class ScriptedSnapshotFileSystem : IPersistedSnapshotFileSystem
    {
        private readonly Dictionary<string, byte[]?> _contents;
        private readonly Queue<PersistedStoreStamp> _groupStamps;
        private PersistedStoreStamp _lastGroupStamp = Stamp(1);

        internal ScriptedSnapshotFileSystem(
            byte[]? groups,
            byte[]? files,
            byte[]? settings,
            IEnumerable<PersistedStoreStamp>? groupStamps = null)
        {
            _contents = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase)
            {
                ["loggroups.json"] = groups,
                ["logfiles.json"] = files,
                ["settings.json"] = settings
            };
            _groupStamps = new Queue<PersistedStoreStamp>(groupStamps ?? []);
        }

        internal int RemainingReadIoFailures { get; set; }

        internal bool ThrowAccessDenied { get; set; }

        public PersistedStoreStamp GetStamp(string path)
        {
            if (ThrowAccessDenied)
                throw new UnauthorizedAccessException();

            if (string.Equals(Path.GetFileName(path), "loggroups.json", StringComparison.OrdinalIgnoreCase))
            {
                if (_groupStamps.Count > 0)
                    _lastGroupStamp = _groupStamps.Dequeue();
                return _lastGroupStamp;
            }

            return _contents[Path.GetFileName(path)] == null
                ? PersistedStoreStamp.Missing
                : Stamp(1);
        }

        public Task<byte[]?> ReadAllBytesAsync(
            string path,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RemainingReadIoFailures > 0)
            {
                RemainingReadIoFailures--;
                throw new IOException("sharing violation");
            }

            var bytes = _contents[Path.GetFileName(path)];
            if (bytes != null && bytes.Length > maximumBytes)
                throw new PersistedStoreTooLargeException();
            return Task.FromResult(bytes?.ToArray());
        }

        public bool HasTemporaryArtifact(string storePath) => false;

        public bool HasRecoveryArtifact(string storePath) => false;
    }
}
