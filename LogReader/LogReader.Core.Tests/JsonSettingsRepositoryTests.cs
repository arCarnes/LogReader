namespace LogReader.Core.Tests;

using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using System.Text.Json;

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
        Assert.True(settings.EnableSearchMatchHighlighting);
        Assert.Equal("#FFF59D", settings.SearchMatchHighlightColor);
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
            EnableSearchMatchHighlighting = false,
            SearchMatchHighlightColor = "#FFE082",
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
        Assert.False(loaded.EnableSearchMatchHighlighting);
        Assert.Equal("#FFE082", loaded.SearchMatchHighlightColor);
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
        Assert.False(data.GetProperty("enableSearchMatchHighlighting").GetBoolean());
        Assert.Equal("#FFE082", data.GetProperty("searchMatchHighlightColor").GetString());
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
    public async Task SaveToFileAsync_WritesVersionedSettingsEnvelope()
    {
        var exportPath = Path.Combine(_testDir, "settings-export.json");
        var repo = new JsonSettingsRepository();
        var settings = new AppSettings
        {
            DefaultOpenDirectory = @"C:\logs",
            LogFontFamily = "Cascadia Code",
            LogFontSize = 15,
            ShowFullPathsInDashboard = true,
            EnableSearchMatchHighlighting = false,
            SearchMatchHighlightColor = "#FFE082",
            ColorPickerCustomColors = new List<string> { "#112233" },
            HighlightRules = new List<LineHighlightRule>
            {
                new()
                {
                    Pattern = "ERROR",
                    IsRegex = true,
                    CaseSensitive = true,
                    Color = "#FFCCCC",
                    IsEnabled = false
                }
            },
            DateRollingPatterns = new List<ReplacementPattern>
            {
                new() { Name = "Daily", FindPattern = ".log", ReplacePattern = ".log{yyyyMMdd}" }
            }
        };

        await repo.SaveToFileAsync(exportPath, settings);

        Assert.False(File.Exists(exportPath + ".tmp"));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(exportPath));
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(@"C:\logs", data.GetProperty("defaultOpenDirectory").GetString());
        Assert.Equal("Cascadia Code", data.GetProperty("logFontFamily").GetString());
        Assert.Equal(15, data.GetProperty("logFontSize").GetInt32());
        Assert.True(data.GetProperty("showFullPathsInDashboard").GetBoolean());
        Assert.False(data.GetProperty("enableSearchMatchHighlighting").GetBoolean());
        Assert.Equal("#FFE082", data.GetProperty("searchMatchHighlightColor").GetString());
        Assert.Equal("#112233", Assert.Single(data.GetProperty("colorPickerCustomColors").EnumerateArray()).GetString());
        Assert.Single(data.GetProperty("highlightRules").EnumerateArray());
        Assert.Single(data.GetProperty("dateRollingPatterns").EnumerateArray());
    }

    [Fact]
    public async Task SaveToFileAndLoadFromFile_RoundTripsAllSettings()
    {
        var exportPath = Path.Combine(_testDir, "settings-export.json");
        var repo = new JsonSettingsRepository();
        var expected = new AppSettings
        {
            DefaultOpenDirectory = @"C:\logs",
            LogFontFamily = "Cascadia Mono",
            LogFontSize = 17,
            ShowFullPathsInDashboard = true,
            EnableSearchMatchHighlighting = false,
            SearchMatchHighlightColor = "#ABCDEF",
            ColorPickerCustomColors = new List<string> { "#112233", "#445566" },
            HighlightRules = new List<LineHighlightRule>
            {
                new()
                {
                    Pattern = "WARN",
                    IsRegex = true,
                    CaseSensitive = true,
                    Color = "#FFCCCC",
                    IsEnabled = false
                }
            },
            DateRollingPatterns = new List<ReplacementPattern>
            {
                new() { Name = "Daily", FindPattern = ".log", ReplacePattern = ".log{yyyyMMdd}" }
            }
        };

        await repo.SaveToFileAsync(exportPath, expected);
        var loaded = await repo.LoadFromFileAsync(exportPath);

        Assert.Equal(expected.DefaultOpenDirectory, loaded.DefaultOpenDirectory);
        Assert.Equal(expected.LogFontFamily, loaded.LogFontFamily);
        Assert.Equal(expected.LogFontSize, loaded.LogFontSize);
        Assert.Equal(expected.ShowFullPathsInDashboard, loaded.ShowFullPathsInDashboard);
        Assert.Equal(expected.EnableSearchMatchHighlighting, loaded.EnableSearchMatchHighlighting);
        Assert.Equal(expected.SearchMatchHighlightColor, loaded.SearchMatchHighlightColor);
        Assert.Equal(expected.ColorPickerCustomColors, loaded.ColorPickerCustomColors);
        var rule = Assert.Single(loaded.HighlightRules);
        Assert.Equal("WARN", rule.Pattern);
        Assert.True(rule.IsRegex);
        Assert.True(rule.CaseSensitive);
        Assert.Equal("#FFCCCC", rule.Color);
        Assert.False(rule.IsEnabled);
        var pattern = Assert.Single(loaded.DateRollingPatterns);
        Assert.Equal("Daily", pattern.Name);
        Assert.Equal(".log{yyyyMMdd}", pattern.ReplacePattern);
    }

    [Fact]
    public async Task LoadFromFileAsync_MalformedJson_ThrowsInvalidDataException()
    {
        var importPath = Path.Combine(_testDir, "bad-settings.json");
        await File.WriteAllTextAsync(importPath, "{ invalid json");
        var repo = new JsonSettingsRepository();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => repo.LoadFromFileAsync(importPath));

        Assert.Contains("not valid LogReader settings JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsNotType<PersistedStateRecoveryException>(ex);
    }

    [Fact]
    public async Task LoadFromFileAsync_LegacyPayload_LoadsSettings()
    {
        var importPath = Path.Combine(_testDir, "legacy-settings.json");
        await File.WriteAllTextAsync(importPath, """
            {
              "defaultOpenDirectory": "C:\\legacy-logs",
              "logFontFamily": "Cascadia Code",
              "logFontSize": 14,
              "showFullPathsInDashboard": true,
              "enableSearchMatchHighlighting": false,
              "searchMatchHighlightColor": "#FFE082",
              "highlightRules": [],
              "dateRollingPatterns": []
            }
            """);
        var repo = new JsonSettingsRepository();

        var loaded = await repo.LoadFromFileAsync(importPath);

        Assert.Equal(@"C:\legacy-logs", loaded.DefaultOpenDirectory);
        Assert.Equal("Cascadia Code", loaded.LogFontFamily);
        Assert.Equal(14, loaded.LogFontSize);
        Assert.True(loaded.ShowFullPathsInDashboard);
        Assert.False(loaded.EnableSearchMatchHighlighting);
        Assert.Equal("#FFE082", loaded.SearchMatchHighlightColor);
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
