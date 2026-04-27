namespace LogReader.Core.Tests;

using LogReader.Core;
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

        Assert.Null(settings.DefaultOpenDirectory);
        Assert.Equal("Consolas", settings.LogFontFamily);
        Assert.False(settings.ShowFullPathsInDashboard);
        Assert.Empty(settings.HighlightRules);
        Assert.Empty(settings.ColorPickerCustomColors);
        Assert.Empty(settings.DateRollingPatterns);
    }

    [Fact]
    public async Task SaveLoad_RoundTrip_PersistsSettings()
    {
        var repo = new JsonSettingsRepository();
        var expected = new AppSettings
        {
            DefaultOpenDirectory = @"C:\logs",
            LogFontFamily = "Cascadia Mono",
            ShowFullPathsInDashboard = true,
            ColorPickerCustomColors = new List<string>
            {
                "#FF4D4D",
                "#00AA66"
            },
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
            },
            DateRollingPatterns = new List<ReplacementPattern>
            {
                new()
                {
                    Name = "Log4Net",
                    FindPattern = ".log",
                    ReplacePattern = ".log{yyyyMMdd}"
                }
            }
        };

        await repo.SaveAsync(expected);
        var loaded = await repo.LoadAsync();

        Assert.Equal(expected.DefaultOpenDirectory, loaded.DefaultOpenDirectory);
        Assert.Equal(expected.LogFontFamily, loaded.LogFontFamily);
        Assert.Equal(expected.ShowFullPathsInDashboard, loaded.ShowFullPathsInDashboard);
        Assert.Equal(expected.ColorPickerCustomColors, loaded.ColorPickerCustomColors);
        Assert.Single(loaded.HighlightRules);
        Assert.Equal("ERROR", loaded.HighlightRules[0].Pattern);
        var savedPattern = Assert.Single(loaded.DateRollingPatterns);
        Assert.Equal("Log4Net", savedPattern.Name);
        Assert.Equal(".log{yyyyMMdd}", savedPattern.ReplacePattern);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync(_testDir, "settings.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(@"C:\logs", data.GetProperty("defaultOpenDirectory").GetString());
        Assert.False(data.TryGetProperty("dashboardLoadConcurrency", out _));
        Assert.True(data.GetProperty("showFullPathsInDashboard").GetBoolean());
        Assert.Equal(["#FF4D4D", "#00AA66"], data.GetProperty("colorPickerCustomColors").EnumerateArray().Select(color => color.GetString()).ToArray());
        Assert.Single(data.GetProperty("dateRollingPatterns").EnumerateArray());
    }

    [Fact]
    public async Task LoadAsync_LegacyPayload_RewritesVersionedEnvelope()
    {
        var path = JsonStore.GetFilePath("settings.json");
        await File.WriteAllTextAsync(path, """
            {
              "defaultOpenDirectory": "C:\\legacy-logs",
              "logFontFamily": "Fira Code",
              "dashboardLoadConcurrency": 6,
              "showFullPathsInDashboard": true,
              "highlightRules": []
            }
            """);

        var repo = new JsonSettingsRepository();

        var loaded = await repo.LoadAsync();

        Assert.Equal(@"C:\legacy-logs", loaded.DefaultOpenDirectory);
        Assert.Equal("Fira Code", loaded.LogFontFamily);
        Assert.True(loaded.ShowFullPathsInDashboard);
        Assert.Empty(loaded.ColorPickerCustomColors);
        Assert.Empty(loaded.DateRollingPatterns);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync(_testDir, "settings.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal("Fira Code", data.GetProperty("logFontFamily").GetString());
        Assert.False(data.TryGetProperty("dashboardLoadConcurrency", out _));
        Assert.Empty(data.GetProperty("dateRollingPatterns").EnumerateArray());
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_ThrowsRecoveryExceptionAndPreservesStore()
    {
        const string contents = "{ invalid json";
        var path = JsonStore.GetFilePath("settings.json");
        await File.WriteAllTextAsync(path, contents);

        var repo = new JsonSettingsRepository();

        var ex = await Assert.ThrowsAsync<PersistedStateRecoveryException>(() => repo.LoadAsync());

        Assert.Equal("settings", ex.StoreDisplayName);
        Assert.Equal(path, ex.StorePath);
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LoadAsync_MissingSchemaVersionInEnvelope_ThrowsRecoveryExceptionAndPreservesStore()
    {
        const string contents = """
            {
              "data": {
                "logFontFamily": "JetBrains Mono"
              }
            }
            """;
        var path = JsonStore.GetFilePath("settings.json");
        await File.WriteAllTextAsync(path, contents);

        var repo = new JsonSettingsRepository();

        var ex = await Assert.ThrowsAsync<PersistedStateRecoveryException>(() => repo.LoadAsync());

        Assert.Equal("settings", ex.StoreDisplayName);
        Assert.Equal(path, ex.StorePath);
        Assert.Equal(contents.ReplaceLineEndings(), (await File.ReadAllTextAsync(path)).ReplaceLineEndings());
    }

    [Fact]
    public async Task LoadAsync_MalformedVersionedPayload_ThrowsRecoveryExceptionAndPreservesStore()
    {
        const string contents = """
            {
              "schemaVersion": 1,
              "data": []
            }
            """;
        var path = JsonStore.GetFilePath("settings.json");
        await File.WriteAllTextAsync(path, contents);

        var repo = new JsonSettingsRepository();

        var ex = await Assert.ThrowsAsync<PersistedStateRecoveryException>(() => repo.LoadAsync());

        Assert.Equal("settings", ex.StoreDisplayName);
        Assert.Equal(path, ex.StorePath);
        Assert.Equal(contents.ReplaceLineEndings(), (await File.ReadAllTextAsync(path)).ReplaceLineEndings());
    }

    [Fact]
    public async Task SaveAsync_ConcurrentCalls_SerializesWritesWithoutExceptions()
    {
        var repo = new JsonSettingsRepository();
        var first = new AppSettings
        {
            DefaultOpenDirectory = @"C:\logs\first",
            LogFontFamily = "Consolas",
            HighlightRules = Enumerable.Range(0, 5_000).Select(i => new LineHighlightRule
            {
                Pattern = $"pattern-{i}",
                IsRegex = false,
                CaseSensitive = i % 2 == 0,
                Color = "#FFFFFF",
                IsEnabled = true
            }).ToList()
        };
        var second = new AppSettings
        {
            DefaultOpenDirectory = @"C:\logs\second",
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

        var firstSave = Task.Run(() => repo.SaveAsync(first));
        var secondSave = Task.Run(() => repo.SaveAsync(second));

        await Task.WhenAll(firstSave, secondSave);
        var loaded = await repo.LoadAsync();

        if (loaded.DefaultOpenDirectory == @"C:\logs\first")
        {
            Assert.Equal("Consolas", loaded.LogFontFamily);
            Assert.Equal(5_000, loaded.HighlightRules.Count);
        }
        else
        {
            Assert.Equal(@"C:\logs\second", loaded.DefaultOpenDirectory);
            Assert.Equal("Cascadia Mono", loaded.LogFontFamily);
            Assert.Single(loaded.HighlightRules);
            Assert.Equal("ERROR", loaded.HighlightRules[0].Pattern);
        }
    }
}
