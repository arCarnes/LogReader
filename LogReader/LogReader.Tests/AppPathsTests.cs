namespace LogReader.Tests;

using System.IO.MemoryMappedFiles;
using System.Text.Json;
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
        Directory.CreateDirectory(_testRoot);
        AppPaths.SetRootPathForTests(_testRoot);
        AppPaths.SetBaseDirectoryForTests(_testRoot);
        JsonStore.SetBasePathForTests(_testRoot);
    }

    public void Dispose()
    {
        JsonStore.SetBasePathForTests(null);
        AppPaths.SetRootPathForTests(null);
        AppPaths.SetBaseDirectoryForTests(null);

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
    public void RootDirectory_WithoutRuntimeConfiguration_FallsBackToLocalAppData()
    {
        UseRuntimeConfigurationMode();

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogReader");

        Assert.Equal(expected, AppPaths.RootDirectory);
    }

    [Fact]
    public void RootDirectory_RuntimeConfiguration_UsesAbsoluteStorageRoot()
    {
        UseRuntimeConfigurationMode();
        var configuredRoot = Path.Combine(_testRoot, "AbsoluteStorage");
        WriteRuntimeConfiguration(configuredRoot);

        Assert.Equal(Path.GetFullPath(configuredRoot), AppPaths.RootDirectory);
    }

    [Fact]
    public void JsonStore_GetFilePath_WithRelativeRuntimeConfiguration_UsesResolvedDataDirectory()
    {
        UseRuntimeConfigurationMode();
        WriteRuntimeConfiguration(@".\LogReaderData");

        var filePath = JsonStore.GetFilePath("settings.json");

        Assert.Equal(Path.Combine(_testRoot, "LogReaderData", "Data", "settings.json"), filePath);
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "LogReaderData", "Data")));
    }

    [Fact]
    public void Freeze_WithRelativeRuntimeConfiguration_CreatesIndexFileUnderResolvedCacheDirectory()
    {
        UseRuntimeConfigurationMode();
        WriteRuntimeConfiguration(@".\LogReaderData");

        using var offsets = new MappedLineOffsets();
        offsets.Add(0);
        offsets.Add(42);

        offsets.Freeze();

        var indexDirectory = Path.Combine(_testRoot, "LogReaderData", "Cache", "idx");
        Assert.True(Directory.Exists(indexDirectory));
        Assert.Single(Directory.GetFiles(indexDirectory, "*.bin"));
    }

    [Fact]
    public void RootDirectory_WithMalformedRuntimeConfiguration_ThrowsConfigurationException()
    {
        UseRuntimeConfigurationMode();
        var configurationPath = Path.Combine(_testRoot, "LogReader.runtime.json");
        File.WriteAllText(configurationPath, "{ invalid json", System.Text.Encoding.ASCII);

        var ex = Assert.Throws<AppRuntimeConfigurationException>(() => _ = AppPaths.RootDirectory);

        Assert.Equal(configurationPath, ex.ConfigurationPath);
        Assert.Contains("not valid JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private void UseRuntimeConfigurationMode()
    {
        JsonStore.SetBasePathForTests(null);
        AppPaths.SetRootPathForTests(null);
        AppPaths.SetBaseDirectoryForTests(_testRoot);
    }

    private void WriteRuntimeConfiguration(string storageRoot)
    {
        Directory.CreateDirectory(_testRoot);

        var configurationPath = Path.Combine(_testRoot, "LogReader.runtime.json");
        var payload = new
        {
            storageRoot
        };

        File.WriteAllText(
            configurationPath,
            JsonSerializer.Serialize(payload),
            System.Text.Encoding.ASCII);
    }
}
