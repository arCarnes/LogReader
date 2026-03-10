namespace LogReader.Tests;

using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public class JsonSettingsRepositoryTests : IAsyncLifetime
{
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderSettingsTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        JsonStore.SetBasePathForTests(_testDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        JsonStore.SetBasePathForTests(null);
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LoadAsync_NoFile_ReturnsDefaults()
    {
        var repo = new JsonSettingsRepository();

        var settings = await repo.LoadAsync();

        Assert.True(settings.GlobalAutoTailEnabled);
        Assert.True(settings.EnableTabOverflowDropdown);
        Assert.Null(settings.DefaultOpenDirectory);
        Assert.Equal(FileEncoding.Utf8, settings.DefaultFileEncoding);
        Assert.Empty(settings.FileEncodingFallbacks);
        Assert.Equal("Consolas", settings.LogFontFamily);
        Assert.Empty(settings.HighlightRules);
    }

    [Fact]
    public async Task SaveLoad_RoundTrip_PersistsGlobalAutoTailSetting()
    {
        var repo = new JsonSettingsRepository();
        var expected = new AppSettings
        {
            DefaultOpenDirectory = @"C:\logs",
            GlobalAutoTailEnabled = false,
            EnableTabOverflowDropdown = false,
            DefaultFileEncoding = FileEncoding.Ansi,
            FileEncodingFallbacks = new List<FileEncoding> { FileEncoding.Utf8, FileEncoding.Utf16 },
            LogFontFamily = "Cascadia Mono",
            HighlightRules = new List<LineHighlightRule>
            {
                new()
                {
                    Pattern = "ERROR",
                    IsRegex = false,
                    CaseSensitive = false,
                    Color = "#FFCCCC",
                    IsEnabled = true
                }
            }
        };

        await repo.SaveAsync(expected);
        var loaded = await repo.LoadAsync();

        Assert.Equal(expected.DefaultOpenDirectory, loaded.DefaultOpenDirectory);
        Assert.Equal(expected.GlobalAutoTailEnabled, loaded.GlobalAutoTailEnabled);
        Assert.Equal(expected.EnableTabOverflowDropdown, loaded.EnableTabOverflowDropdown);
        Assert.Equal(expected.DefaultFileEncoding, loaded.DefaultFileEncoding);
        Assert.Equal(expected.FileEncodingFallbacks, loaded.FileEncodingFallbacks);
        Assert.Equal(expected.LogFontFamily, loaded.LogFontFamily);
        Assert.Single(loaded.HighlightRules);
        Assert.Equal("ERROR", loaded.HighlightRules[0].Pattern);
    }
}
