namespace LogReader.Tests;

using System.IO.MemoryMappedFiles;
using LogReader.App;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "WeezTailAppPathsTests_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly IDisposable _appPathsScope;

    public AppPathsTests()
    {
        _appPathsScope = AppPaths.BeginTestScope(rootPath: _testRoot);
    }

    public void Dispose()
    {
        _appPathsScope.Dispose();

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Fact]
    public void JsonStore_GetFilePath_UsesDataDirectory()
    {
        var filePath = JsonStore.GetFilePath("settings.json");

        Assert.Equal(Path.Combine(_testRoot, "Data", "settings.json"), filePath);
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "Data")));
    }

    [Fact]
    public void ViewsDirectory_UsesDataViewsPath()
    {
        Assert.Equal(Path.Combine(_testRoot, "Data", "Views"), AppPaths.ViewsDirectory);
    }

    [Fact]
    public void SettingsDirectory_UsesDataSettingsPath()
    {
        Assert.Equal(Path.Combine(_testRoot, "Data", "Settings"), AppPaths.SettingsDirectory);
    }

    [Fact]
    public void Freeze_CreatesIndexFileUnderLocalCacheDirectory()
    {
        using var offsets = new MappedLineOffsets();
        offsets.Add(0);
        offsets.Add(42);

        offsets.Freeze();

        var indexDirectory = Path.Combine(_testRoot, "Cache", "idx");
        Assert.True(Directory.Exists(indexDirectory));
        Assert.Single(Directory.GetFiles(indexDirectory, "*.bin", SearchOption.AllDirectories));
    }

    [Fact]
    public void Freeze_WhenAccessorCreationFails_DeletesIndexFileOnDispose()
    {
        using var offsets = new MappedLineOffsets(
            static (path, byteLength) => MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, null, byteLength, MemoryMappedFileAccess.Read),
            static (_, _) => throw new InvalidOperationException("Simulated accessor failure"));
        offsets.Add(0);
        offsets.Add(42);

        Assert.Throws<InvalidOperationException>(() => offsets.Freeze());

        offsets.Dispose();

        var indexDirectory = Path.Combine(_testRoot, "Cache", "idx");
        Assert.Empty(Directory.GetFiles(indexDirectory, "*.bin", SearchOption.AllDirectories));
    }

    [Fact]
    public void FrozenOffsets_AppendsOverflowToSameIndexFileAndDeletesItOnDispose()
    {
        var offsets = new MappedLineOffsets();
        offsets.Add(0);
        offsets.Add(10);
        offsets.Freeze();

        var indexDirectory = Path.Combine(_testRoot, "Cache", "idx");
        var indexPath = Assert.Single(Directory.GetFiles(
            indexDirectory,
            "*.bin",
            SearchOption.AllDirectories));

        for (var i = 0; i < MappedLineOffsets.OverflowFlushThreshold; i++)
            offsets.Add((i + 2) * 10L);

        Assert.Equal(MappedLineOffsets.OverflowFlushThreshold + 2, offsets.Count);
        Assert.Equal(0, offsets[0]);
        Assert.Equal(10, offsets[1]);
        Assert.Equal((MappedLineOffsets.OverflowFlushThreshold + 1) * 10L, offsets[^1]);

        Assert.Equal(indexPath, Assert.Single(Directory.GetFiles(
            indexDirectory,
            "*.bin",
            SearchOption.AllDirectories)));
        Assert.Equal(offsets.Count * 8L, new FileInfo(indexPath).Length);

        offsets.Dispose();

        Assert.Empty(Directory.GetFiles(indexDirectory, "*.bin", SearchOption.AllDirectories));
    }

    [Fact]
    public void IndexDirectory_DoesNotFollowConfiguredStorageRoot()
    {
        var configuredStorageRoot = Path.Combine(_testRoot, "ConfiguredStorage");
        var localCacheDirectory = Path.Combine(_testRoot, "LocalAppData", "Cache");

        using var scope = AppPaths.BeginTestScope(
            rootPath: configuredStorageRoot,
            localCacheDirectory: localCacheDirectory);

        Assert.Equal(
            Path.Combine(localCacheDirectory, AppPaths.IndexFolderName),
            AppPaths.IndexDirectory);
        Assert.Equal(localCacheDirectory, AppPaths.CacheDirectory);
        Assert.False(AppPaths.IndexDirectory.StartsWith(
            configuredStorageRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CleanupIndexCacheDirectory_DeletesOnlyStaleOwnerAndPreservesLegacyFlatFiles()
    {
        var indexDirectory = AppPaths.EnsureDirectory(AppPaths.IndexDirectory);
        var legacyPath = Path.Combine(indexDirectory, "idx_legacy.bin");
        File.WriteAllText(legacyPath, "legacy");
        var owner = LineIndexCacheOwner.Create(indexDirectory);
        var ownerDirectory = owner.DirectoryPath;
        owner.Dispose();
        Directory.SetLastWriteTimeUtc(ownerDirectory, DateTime.UtcNow - TimeSpan.FromDays(2));

        App.CleanupIndexCacheDirectory();
        App.CleanupIndexCacheDirectory();

        Assert.True(File.Exists(legacyPath));
        Assert.False(Directory.Exists(ownerDirectory));
    }

    [Fact]
    public void BeginTestScope_RestoresPreviousOverridesOnDispose()
    {
        var originalRoot = AppPaths.RootDirectory;
        var nestedRoot = Path.Combine(_testRoot, "NestedRoot");

        using (AppPaths.BeginTestScope(rootPath: nestedRoot))
        {
            Assert.Equal(nestedRoot, AppPaths.RootDirectory);
            Assert.Equal(nestedRoot, AppPaths.GetDefaultStorageRoot());
        }

        Assert.Equal(originalRoot, AppPaths.RootDirectory);
        Assert.Equal(_testRoot, AppPaths.GetDefaultStorageRoot());
    }
}
