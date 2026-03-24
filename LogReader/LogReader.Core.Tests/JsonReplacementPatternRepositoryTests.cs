namespace LogReader.Core.Tests;

using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public class JsonReplacementPatternRepositoryTests : IAsyncLifetime
{
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderPatternTests_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task LoadAsync_NoFile_ReturnsEmptyList()
    {
        var repo = new JsonReplacementPatternRepository();

        var patterns = await repo.LoadAsync();

        Assert.Empty(patterns);
    }

    [Fact]
    public async Task LoadAsync_OldStorageFileOnly_ReturnsEmptyList()
    {
        var oldPath = JsonStore.GetFilePath("patterns.json");
        await File.WriteAllTextAsync(oldPath, """
            [
              {
                "id": "old-pattern",
                "name": "Old",
                "findPattern": ".log",
                "replacePattern": "{yyyyMMdd}"
              }
            ]
            """);
        var repo = new JsonReplacementPatternRepository();

        var patterns = await repo.LoadAsync();

        Assert.Empty(patterns);
    }

    [Fact]
    public async Task SaveLoad_RoundTrip_PersistsPatterns()
    {
        var repo = new JsonReplacementPatternRepository();
        var expected = new List<ReplacementPattern>
        {
            new()
            {
                Id = "pattern-1",
                Name = "Date suffix",
                FindPattern = "app.log",
                ReplacePattern = "app-{yyyyMMdd}.log"
            }
        };

        await repo.SaveAsync(expected);
        var loaded = await repo.LoadAsync();

        var pattern = Assert.Single(loaded);
        Assert.Equal("pattern-1", pattern.Id);
        Assert.Equal("Date suffix", pattern.Name);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("date-rolling-patterns.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        var persisted = Assert.Single(data.EnumerateArray());
        Assert.Equal("pattern-1", persisted.GetProperty("id").GetString());
    }

    [Fact]
    public async Task LoadAsync_LegacyPayload_LoadsWithoutRewriting()
    {
        var path = JsonStore.GetFilePath("date-rolling-patterns.json");
        var legacyJson = """
            [
              {
                "id": "legacy-pattern",
                "name": "Legacy",
                "findPattern": "app.log",
                "replacePattern": "app-{yyyyMMdd}.log"
              }
            ]
            """;
        await File.WriteAllTextAsync(path, legacyJson);
        var repo = new JsonReplacementPatternRepository();

        var loaded = await repo.LoadAsync();

        var pattern = Assert.Single(loaded);
        Assert.Equal("legacy-pattern", pattern.Id);
        var persisted = await File.ReadAllTextAsync(path);
        Assert.Equal(legacyJson, persisted);
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_ThrowsInvalidDataException()
    {
        var path = JsonStore.GetFilePath("date-rolling-patterns.json");
        await File.WriteAllTextAsync(path, "{ invalid json");
        var repo = new JsonReplacementPatternRepository();

        await Assert.ThrowsAsync<InvalidDataException>(() => repo.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_UnsupportedSchemaVersion_ThrowsInvalidDataException()
    {
        var path = JsonStore.GetFilePath("date-rolling-patterns.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 99,
              "data": []
            }
            """);
        var repo = new JsonReplacementPatternRepository();

        await Assert.ThrowsAsync<InvalidDataException>(() => repo.LoadAsync());
    }
}
