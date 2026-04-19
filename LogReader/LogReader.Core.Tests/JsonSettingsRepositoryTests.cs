namespace LogReader.Core.Tests;

using System.Text.Json;
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
        Assert.Equal(4, settings.DashboardLoadConcurrency);
        Assert.False(settings.ShowFullPathsInDashboard);
        Assert.Empty(settings.HighlightRules);
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
            DashboardLoadConcurrency = 6,
            ShowFullPathsInDashboard = true,
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
        Assert.Equal(expected.DashboardLoadConcurrency, loaded.DashboardLoadConcurrency);
        Assert.Equal(expected.ShowFullPathsInDashboard, loaded.ShowFullPathsInDashboard);
        Assert.Single(loaded.HighlightRules);
        Assert.Equal("ERROR", loaded.HighlightRules[0].Pattern);
        var savedPattern = Assert.Single(loaded.DateRollingPatterns);
        Assert.Equal("Log4Net", savedPattern.Name);
        Assert.Equal(".log{yyyyMMdd}", savedPattern.ReplacePattern);

        using var document = await LoadPersistedDocumentAsync("settings.json");
        var data = AssertVersionedEnvelope(document);
        Assert.Equal(@"C:\logs", data.GetProperty("defaultOpenDirectory").GetString());
        Assert.Equal(6, data.GetProperty("dashboardLoadConcurrency").GetInt32());
        Assert.True(data.GetProperty("showFullPathsInDashboard").GetBoolean());
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
        Assert.Equal(6, loaded.DashboardLoadConcurrency);
        Assert.True(loaded.ShowFullPathsInDashboard);
        Assert.Empty(loaded.DateRollingPatterns);

        using var document = await LoadPersistedDocumentAsync("settings.json");
        var data = AssertVersionedEnvelope(document);
        Assert.Equal("Fira Code", data.GetProperty("logFontFamily").GetString());
        Assert.Equal(6, data.GetProperty("dashboardLoadConcurrency").GetInt32());
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

    private static async Task<JsonDocument> LoadPersistedDocumentAsync(string fileName)
    {
        var path = JsonStore.GetFilePath(fileName);
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream);
    }

    private static JsonElement AssertVersionedEnvelope(JsonDocument document)
    {
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        return root.GetProperty("data");
    }
}
