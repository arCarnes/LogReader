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
        "LogReaderAppPathsTests_" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Freeze_CreatesIndexFileUnderCacheDirectory()
    {
        using var offsets = new MappedLineOffsets();
        offsets.Add(0);
        offsets.Add(42);

        offsets.Freeze();

        var indexDirectory = Path.Combine(_testRoot, "Cache", "idx");
        Assert.True(Directory.Exists(indexDirectory));
        Assert.Single(Directory.GetFiles(indexDirectory, "*.bin"));
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
        Assert.Empty(Directory.GetFiles(indexDirectory, "*.bin"));
    }

    [Fact]
    public void CleanupIndexCacheDirectory_DeletesIndexDirectoryAndIgnoresMissingPath()
    {
        var indexDirectory = AppPaths.EnsureDirectory(AppPaths.IndexDirectory);
        File.WriteAllText(Path.Combine(indexDirectory, "stale.bin"), "stale");

        App.CleanupIndexCacheDirectory();
        App.CleanupIndexCacheDirectory();

        Assert.False(Directory.Exists(indexDirectory));
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
