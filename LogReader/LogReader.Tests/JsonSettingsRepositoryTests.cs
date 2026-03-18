namespace LogReader.Tests;

using System.Text.Json;
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
        Assert.Empty(settings.HighlightRules);
    }

    [Fact]
    public async Task SaveLoad_RoundTrip_PersistsSettings()
    {
        var repo = new JsonSettingsRepository();
        var expected = new AppSettings
        {
            DefaultOpenDirectory = @"C:\logs",
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
        Assert.Equal(expected.LogFontFamily, loaded.LogFontFamily);
        Assert.Single(loaded.HighlightRules);
        Assert.Equal("ERROR", loaded.HighlightRules[0].Pattern);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("settings.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(@"C:\logs", data.GetProperty("defaultOpenDirectory").GetString());
    }

    [Fact]
    public async Task LoadAsync_LegacyPayload_RewritesVersionedEnvelope()
    {
        var path = JsonStore.GetFilePath("settings.json");
        await File.WriteAllTextAsync(path, """
            {
              "defaultOpenDirectory": "C:\\legacy-logs",
              "logFontFamily": "Fira Code",
              "highlightRules": []
            }
            """);

        var repo = new JsonSettingsRepository();

        var loaded = await repo.LoadAsync();

        Assert.Equal(@"C:\legacy-logs", loaded.DefaultOpenDirectory);
        Assert.Equal("Fira Code", loaded.LogFontFamily);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("settings.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal("Fira Code", data.GetProperty("logFontFamily").GetString());
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_ResetsStoreToDefaults()
    {
        var path = JsonStore.GetFilePath("settings.json");
        await File.WriteAllTextAsync(path, "{ invalid json");

        var repo = new JsonSettingsRepository();

        var loaded = await repo.LoadAsync();

        Assert.Null(loaded.DefaultOpenDirectory);
        Assert.Equal("Consolas", loaded.LogFontFamily);
        Assert.Empty(loaded.HighlightRules);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("settings.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("defaultOpenDirectory").ValueKind);
        Assert.Equal("Consolas", data.GetProperty("logFontFamily").GetString());
        Assert.Equal(JsonValueKind.Array, data.GetProperty("highlightRules").ValueKind);
        Assert.Empty(data.GetProperty("highlightRules").EnumerateArray());
    }

    [Fact]
    public async Task LoadAsync_MissingSchemaVersionInEnvelope_ResetsStoreToDefaults()
    {
        var path = JsonStore.GetFilePath("settings.json");
        await File.WriteAllTextAsync(path, """
            {
              "data": {
                "logFontFamily": "JetBrains Mono"
              }
            }
            """);

        var repo = new JsonSettingsRepository();

        var loaded = await repo.LoadAsync();

        Assert.Null(loaded.DefaultOpenDirectory);
        Assert.Equal("Consolas", loaded.LogFontFamily);
        Assert.Empty(loaded.HighlightRules);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("settings.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("defaultOpenDirectory").ValueKind);
        Assert.Equal("Consolas", data.GetProperty("logFontFamily").GetString());
        Assert.Equal(JsonValueKind.Array, data.GetProperty("highlightRules").ValueKind);
        Assert.Empty(data.GetProperty("highlightRules").EnumerateArray());
    }

    [Fact]
    public async Task LoadAsync_MalformedVersionedPayload_ResetsStoreToDefaults()
    {
        var path = JsonStore.GetFilePath("settings.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "data": []
            }
            """);

        var repo = new JsonSettingsRepository();

        var loaded = await repo.LoadAsync();

        Assert.Null(loaded.DefaultOpenDirectory);
        Assert.Equal("Consolas", loaded.LogFontFamily);
        Assert.Empty(loaded.HighlightRules);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("settings.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("defaultOpenDirectory").ValueKind);
        Assert.Equal("Consolas", data.GetProperty("logFontFamily").GetString());
        Assert.Equal(JsonValueKind.Array, data.GetProperty("highlightRules").ValueKind);
        Assert.Empty(data.GetProperty("highlightRules").EnumerateArray());
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

        // Either write may win; assert the final state is a coherent snapshot from one input.
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

    [Fact]
    public async Task LoadAsync_WhileSaving_CompletesWithoutInvalidState()
    {
        var repo = new JsonSettingsRepository();
        var initial = new AppSettings
        {
            DefaultOpenDirectory = @"C:\logs\initial",
            LogFontFamily = "Consolas",
            HighlightRules = new List<LineHighlightRule>()
        };
        var updated = new AppSettings
        {
            DefaultOpenDirectory = @"C:\logs\updated",
            LogFontFamily = "JetBrains Mono",
            HighlightRules = Enumerable.Range(0, 250).Select(i => new LineHighlightRule
            {
                Pattern = $"WARN-{i}",
                IsRegex = i % 2 == 0,
                CaseSensitive = i % 3 == 0,
                Color = "#AACCEE",
                IsEnabled = true
            }).ToList()
        };

        await repo.SaveAsync(initial);

        var saveTask = Task.Run(() => repo.SaveAsync(updated));
        var loadTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => repo.LoadAsync())).ToArray();

        await Task.WhenAll(loadTasks.Cast<Task>().Append(saveTask));

        foreach (var loadTask in loadTasks)
        {
            var loaded = await loadTask;
            Assert.NotNull(loaded);
            Assert.NotNull(loaded.HighlightRules);
        }

        var final = await repo.LoadAsync();
        Assert.Equal(@"C:\logs\updated", final.DefaultOpenDirectory);
        Assert.Equal(250, final.HighlightRules.Count);
    }
}
