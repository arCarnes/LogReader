namespace LogReader.Core.Tests;

using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public class JsonLogFileRepositoryTests : IAsyncLifetime
{
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderFileRepoTests_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task GetAllAsync_NoFile_ReturnsEmptyList()
    {
        var repo = new JsonLogFileRepository();

        var entries = await repo.GetAllAsync();

        Assert.Empty(entries);
    }

    [Fact]
    public async Task AddAsync_RoundTripsWithVersionedEnvelope()
    {
        var repo = new JsonLogFileRepository();
        var entry = new LogFileEntry
        {
            Id = "file-1",
            FilePath = @"C:\logs\app.log",
            LastOpenedAt = new DateTime(2026, 03, 16, 12, 0, 0, DateTimeKind.Utc)
        };

        await repo.AddAsync(entry);
        var entries = await repo.GetAllAsync();

        var loaded = Assert.Single(entries);
        Assert.Equal(entry.Id, loaded.Id);
        Assert.Equal(entry.FilePath, loaded.FilePath);

        using var document = await LoadPersistedDocumentAsync("logfiles.json");
        var data = AssertVersionedEnvelope(document);
        Assert.Equal("file-1", data[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetAllAsync_LegacyArray_RewritesVersionedEnvelope()
    {
        var path = JsonStore.GetFilePath("logfiles.json");
        await File.WriteAllTextAsync(path, """
            [
              {
                "id": "legacy-file",
                "filePath": "C:\\legacy.log",
                "lastOpenedAt": "2026-03-16T12:00:00Z"
              }
            ]
            """);

        var repo = new JsonLogFileRepository();

        var entries = await repo.GetAllAsync();

        var loaded = Assert.Single(entries);
        Assert.Equal("legacy-file", loaded.Id);

        using var document = await LoadPersistedDocumentAsync("logfiles.json");
        var data = AssertVersionedEnvelope(document);
        Assert.Equal("legacy-file", data[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetAllAsync_MalformedJson_ThrowsRecoveryExceptionAndPreservesStore()
    {
        const string contents = "{ invalid json";
        var path = JsonStore.GetFilePath("logfiles.json");
        await File.WriteAllTextAsync(path, contents);

        var repo = new JsonLogFileRepository();

        var ex = await Assert.ThrowsAsync<PersistedStateRecoveryException>(() => repo.GetAllAsync());

        Assert.Equal("log file metadata", ex.StoreDisplayName);
        Assert.Equal(path, ex.StorePath);
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task GetAllAsync_MissingSchemaVersionInEnvelope_ThrowsRecoveryExceptionAndPreservesStore()
    {
        const string contents = """
            {
              "data": []
            }
            """;
        var path = JsonStore.GetFilePath("logfiles.json");
        await File.WriteAllTextAsync(path, contents);

        var repo = new JsonLogFileRepository();

        var ex = await Assert.ThrowsAsync<PersistedStateRecoveryException>(() => repo.GetAllAsync());

        Assert.Equal("log file metadata", ex.StoreDisplayName);
        Assert.Equal(path, ex.StorePath);
        Assert.Equal(contents.ReplaceLineEndings(), (await File.ReadAllTextAsync(path)).ReplaceLineEndings());
    }

    [Fact]
    public async Task GetAllAsync_MalformedVersionedPayload_ThrowsRecoveryExceptionAndPreservesStore()
    {
        const string contents = """
            {
              "schemaVersion": 1,
              "data": {}
            }
            """;
        var path = JsonStore.GetFilePath("logfiles.json");
        await File.WriteAllTextAsync(path, contents);

        var repo = new JsonLogFileRepository();

        var ex = await Assert.ThrowsAsync<PersistedStateRecoveryException>(() => repo.GetAllAsync());

        Assert.Equal("log file metadata", ex.StoreDisplayName);
        Assert.Equal(path, ex.StorePath);
        Assert.Equal(contents.ReplaceLineEndings(), (await File.ReadAllTextAsync(path)).ReplaceLineEndings());
    }

    [Fact]
    public async Task GetByIdsAndPathsAsync_ReturnMatchingEntriesWithoutWholeStoreRewrite()
    {
        var repo = new JsonLogFileRepository();
        var first = new LogFileEntry { Id = "file-1", FilePath = @"C:\logs\a.log" };
        var second = new LogFileEntry { Id = "file-2", FilePath = @"C:\logs\b.log" };
        await repo.AddAsync(first);
        await repo.AddAsync(second);

        var byId = await repo.GetByIdsAsync(new[] { second.Id, "missing" });
        var byPath = await repo.GetByPathsAsync(new[] { first.FilePath, @"C:\logs\missing.log" });

        Assert.Equal(second.FilePath, byId[second.Id].FilePath);
        Assert.Equal(first.Id, byPath[first.FilePath].Id);
        Assert.Single(byId);
        Assert.Single(byPath);
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
