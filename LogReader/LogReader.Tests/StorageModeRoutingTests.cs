namespace LogReader.Tests;

using System.Text.Json;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public sealed class StorageModeRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WeezTailStorageModes_{Guid.NewGuid():N}");
    private string AppDirectory => Path.Combine(_root, "app");
    private string LocalRoot => Path.Combine(_root, "user", "WeezTail");
    private string SelectionPath => Path.Combine(_root, "selection.json");

    [Theory]
    [InlineData("Portable", "ExeDirectory")]
    [InlineData("Portable", "LocalAppData")]
    [InlineData("Msi", "Absolute")]
    [InlineData("Msi", "PerUserChoice")]
    public void DesktopAndMcpResolveSameStorageAndIndexInExpectedCache(string installMode, string storageMode)
    {
        var selectedRoot = Path.Combine(_root, "selected");
        WriteConfig(installMode, storageMode, selectedRoot);
        File.WriteAllText(SelectionPath, JsonSerializer.Serialize(new { storageRootPath = selectedRoot }));
        using var scope = BeginScope();
        var expectedRoot = storageMode == "ExeDirectory" ? AppDirectory :
            storageMode == "LocalAppData" ? LocalRoot : selectedRoot;
        var expectedCache = Path.Combine(storageMode == "ExeDirectory" ? AppDirectory : LocalRoot, "Cache");
        var selectionBefore = File.ReadAllText(SelectionPath);

        Assert.Equal(expectedRoot, AppPaths.RootDirectory);
        Assert.Equal(expectedRoot, new NonInteractiveStorageRootResolver().ResolveStorageRoot());
        Assert.Equal(expectedCache, AppPaths.CacheDirectory);
        AppPaths.ValidateStorageConfiguration();
        using var offsets = new MappedLineOffsets();
        offsets.Add(0);
        offsets.Add(42);
        offsets.Freeze();

        Assert.Equal(42, offsets[1]);
        Assert.Single(Directory.GetFiles(Path.Combine(expectedCache, "idx"), "*.bin", SearchOption.AllDirectories));
        Assert.Equal(selectionBefore, File.ReadAllText(SelectionPath));
        if (storageMode == "ExeDirectory")
            Assert.False(Directory.Exists(LocalRoot));
        else
        {
            Assert.False(Directory.Exists(Path.Combine(AppDirectory, "Data")));
            Assert.False(Directory.Exists(Path.Combine(AppDirectory, "Cache")));
        }
    }

    [Fact]
    public async Task AppDataUsesExistingUserCatalogAndSettingsWithoutWritingToPackage()
    {
        WriteConfig("Portable", "LocalAppData");
        File.WriteAllText(SelectionPath, "invalid MSI selection must be ignored");
        using var scope = BeginScope();
        var data = Directory.CreateDirectory(Path.Combine(LocalRoot, "Data")).FullName;
        var groupsPath = Path.Combine(data, "loggroups.json");
        var settingsPath = Path.Combine(data, "settings.json");
        var groups = JsonSerializer.Serialize(new { schemaVersion = 1, data = new[]
        {
            new LogGroup { Id = "saved", Name = "Saved dashboard", Kind = LogGroupKind.Dashboard }
        }}, JsonStore.GetOptions());
        var settings = JsonSerializer.Serialize(new { schemaVersion = 1, data = new AppSettings
        {
            McpContinuationLimits = new McpContinuationLimits { MaximumSessions = 7 }
        }}, JsonStore.GetOptions());
        File.WriteAllText(groupsPath, groups);
        File.WriteAllText(settingsPath, settings);
        var configBefore = File.ReadAllText(Path.Combine(AppDirectory, AppPaths.InstallConfigFileName));
        var dialog = new StubStorageSetupDialogService
        {
            OnShowDialog = _ => throw new InvalidOperationException("AppData must not prompt")
        };

        Assert.Equal(StartupStorageResult.Ready, new StartupStorageCoordinator(dialog).EnsureStorageReady());
        using var reader = new PersistedDashboardSnapshotReader();
        var result = await reader.ReadAsync();
        var limits = await McpContinuationSettingsReader.ReadAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("saved", Assert.Single(result.Snapshot!.Groups).Id);
        Assert.Equal(7, limits.MaximumContinuationSessions);
        Assert.Equal(groups, File.ReadAllText(groupsPath));
        Assert.Equal(settings, File.ReadAllText(settingsPath));
        Assert.Equal(configBefore, File.ReadAllText(Path.Combine(AppDirectory, AppPaths.InstallConfigFileName)));
        Assert.Single(Directory.GetFileSystemEntries(AppDirectory));
        Assert.Equal("invalid MSI selection must be ignored", File.ReadAllText(SelectionPath));
    }

    [Fact]
    public async Task AppDataBeforeDesktopStartupReportsUnavailableCatalogWithoutCreatingData()
    {
        WriteConfig("Portable", "LocalAppData");
        using var scope = BeginScope();
        using var reader = new PersistedDashboardSnapshotReader();

        var result = await reader.ReadAsync();
        Assert.Equal(ConfiguredLogCatalogReadErrorCodes.StorageUnavailable, result.Error!.Code);
        Assert.Equal(LogQueryEffectiveLimits.Default, await McpContinuationSettingsReader.ReadAsync());
        Assert.False(Directory.Exists(LocalRoot));
        Assert.Single(Directory.GetFileSystemEntries(AppDirectory));
    }

    [Fact]
    public void AppDataTracksExecutingUserAndRestoresNestedTestScopes()
    {
        WriteConfig("Portable", "LocalAppData");
        using var scope = BeginScope();
        Assert.Equal(LocalRoot, AppPaths.RootDirectory);
        var otherUser = Path.Combine(_root, "other-user", "WeezTail");
        using (BeginScope(otherUser))
        {
            Assert.Equal(otherUser, AppPaths.RootDirectory);
            Assert.Equal(otherUser, new NonInteractiveStorageRootResolver().ResolveStorageRoot());
            Assert.Equal(Path.Combine(otherUser, "Cache"), AppPaths.CacheDirectory);
        }
        Assert.Equal(LocalRoot, AppPaths.RootDirectory);
        Assert.Equal(Path.Combine(LocalRoot, "Cache"), AppPaths.CacheDirectory);
    }

    [Fact]
    public void AppDataBlockedStorageFailsWithoutPromptOrFallback()
    {
        WriteConfig("Portable", "LocalAppData");
        Directory.CreateDirectory(Path.GetDirectoryName(LocalRoot)!);
        File.WriteAllText(LocalRoot, "blocks directory creation");
        using var scope = BeginScope();
        var dialog = new StubStorageSetupDialogService
        {
            OnShowDialog = _ => throw new InvalidOperationException("AppData must not prompt")
        };

        var error = Assert.Throws<StorageValidationException>(
            () => new StartupStorageCoordinator(dialog).EnsureStorageReady());

        Assert.Equal(LocalRoot, error.StoragePath);
        Assert.Single(Directory.GetFileSystemEntries(AppDirectory));
        Assert.Equal(LocalRoot, AppPaths.RootDirectory);
    }

    [Fact]
    public async Task CopiedPortableRetainsCatalogAndRebuildsCacheBesideExecutable()
    {
        WriteConfig("Portable", "ExeDirectory");
        var data = Directory.CreateDirectory(Path.Combine(AppDirectory, "Data")).FullName;
        File.WriteAllText(Path.Combine(data, "loggroups.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            data = new[] { new LogGroup { Id = "copied", Name = "Copied", Kind = LogGroupKind.Dashboard } }
        }, JsonStore.GetOptions()));
        var copied = Directory.CreateDirectory(Path.Combine(_root, "copied-app")).FullName;
        File.Copy(Path.Combine(AppDirectory, AppPaths.InstallConfigFileName), Path.Combine(copied, AppPaths.InstallConfigFileName));
        Directory.CreateDirectory(Path.Combine(copied, "Data"));
        File.Copy(Path.Combine(data, "loggroups.json"), Path.Combine(copied, "Data", "loggroups.json"));
        using var scope = AppPaths.BeginTestScope(baseDirectory: copied, allowDebugFallback: false,
            localAppDataStorageRoot: LocalRoot, useConfiguredCacheDirectory: true);

        AppPaths.ValidateStorageConfiguration();
        using var offsets = new MappedLineOffsets();
        offsets.Add(0);
        offsets.Freeze();
        using var reader = new PersistedDashboardSnapshotReader();
        var result = await reader.ReadAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("copied", Assert.Single(result.Snapshot!.Groups).Id);
        Assert.Single(Directory.GetFiles(Path.Combine(copied, "Cache"), "*.bin", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(LocalRoot));
    }

    [Theory]
    [InlineData("ExeDirectory")]
    [InlineData("LocalAppData")]
    public void CacheCleanupPreservesAnotherActiveOwner(string storageMode)
    {
        WriteConfig("Portable", storageMode);
        using var scope = BeginScope();
        using var active = LineIndexCacheOwner.Create(AppPaths.IndexDirectory);
        Directory.SetLastWriteTimeUtc(active.DirectoryPath, DateTime.UtcNow.AddDays(-2));
        var stale = LineIndexCacheOwner.Create(AppPaths.IndexDirectory);
        var stalePath = stale.DirectoryPath;
        stale.Dispose();
        Directory.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddDays(-2));

        var result = LineIndexCacheMaintenance.CleanupOrphanedOwners();

        Assert.Equal(1, result.LockedOwnerCount);
        Assert.Equal(1, result.DeletedOwnerCount);
        Assert.True(Directory.Exists(active.DirectoryPath));
        Assert.False(Directory.Exists(stalePath));
    }

    [Theory]
    [InlineData("Msi", "LocalAppData")]
    [InlineData("Portable", "Absolute")]
    [InlineData("Portable", "PerUserChoice")]
    public void UnsupportedInstallStoragePairsRemainRejected(string installMode, string storageMode)
    {
        WriteConfig(installMode, storageMode, LocalRoot);
        using var scope = BeginScope();
        Assert.Throws<InstallConfigurationException>(() => AppPaths.RootDirectory);
    }

    [Fact]
    public void McpCacheCleanupWithInvalidConfigurationDoesNotFallBackOrPreventInitialization()
    {
        Directory.CreateDirectory(AppDirectory);
        File.WriteAllText(Path.Combine(AppDirectory, AppPaths.InstallConfigFileName), "invalid config");
        using var scope = BeginScope();

        LogReader.Mcp.McpStdioHost.CleanupIndexCacheDirectory();

        Assert.False(Directory.Exists(LocalRoot));
        Assert.Single(Directory.GetFileSystemEntries(AppDirectory));
        Assert.Throws<InstallConfigurationException>(() => AppPaths.RootDirectory);
    }

    private IDisposable BeginScope(string? localRoot = null) => AppPaths.BeginTestScope(
        baseDirectory: AppDirectory, msiUserStorageSelectionPath: SelectionPath,
        allowDebugFallback: false, defaultStorageRoot: Path.Combine(_root, "unused-default"),
        localAppDataStorageRoot: localRoot ?? LocalRoot, useConfiguredCacheDirectory: true);

    private void WriteConfig(string installMode, string storageMode, string? storageRootPath = null)
    {
        Directory.CreateDirectory(AppDirectory);
        File.WriteAllText(Path.Combine(AppDirectory, AppPaths.InstallConfigFileName),
            JsonSerializer.Serialize(new { installMode, storageMode, storageRootPath }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
