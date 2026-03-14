namespace LogReader.Tests;

using LogReader.App;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "LogReaderAppPathsTests_" + Guid.NewGuid().ToString("N")[..8]);

    public AppPathsTests()
    {
        AppPaths.SetRootPathForTests(_testRoot);
        JsonStore.SetBasePathForTests(_testRoot);
    }

    public void Dispose()
    {
        JsonStore.SetBasePathForTests(null);
        AppPaths.SetRootPathForTests(null);

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
        var originalCreateMemoryMappedFile = MappedLineOffsets.CreateMemoryMappedFile;
        var originalCreateViewAccessor = MappedLineOffsets.CreateViewAccessor;
        using var offsets = new MappedLineOffsets();
        offsets.Add(0);
        offsets.Add(42);

        MappedLineOffsets.CreateViewAccessor = static (_, _) => throw new InvalidOperationException("Simulated accessor failure");

        try
        {
            Assert.Throws<InvalidOperationException>(() => offsets.Freeze());
        }
        finally
        {
            MappedLineOffsets.CreateMemoryMappedFile = originalCreateMemoryMappedFile;
            MappedLineOffsets.CreateViewAccessor = originalCreateViewAccessor;
        }

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
}
