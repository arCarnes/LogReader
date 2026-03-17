namespace LogReader.Tests;

using System.Text.Json;
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

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("logfiles.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
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

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("logfiles.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal("legacy-file", data[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetAllAsync_MalformedJson_ResetsStoreToEmpty()
    {
        var path = JsonStore.GetFilePath("logfiles.json");
        await File.WriteAllTextAsync(path, "{ invalid json");

        var repo = new JsonLogFileRepository();

        var entries = await repo.GetAllAsync();

        Assert.Empty(entries);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("logfiles.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Empty(data.EnumerateArray());
    }

    [Fact]
    public async Task GetAllAsync_MissingSchemaVersionInEnvelope_ResetsStoreToEmpty()
    {
        var path = JsonStore.GetFilePath("logfiles.json");
        await File.WriteAllTextAsync(path, """
            {
              "data": []
            }
            """);

        var repo = new JsonLogFileRepository();

        var entries = await repo.GetAllAsync();

        Assert.Empty(entries);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("logfiles.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Empty(data.EnumerateArray());
    }

    [Fact]
    public async Task GetAllAsync_MalformedVersionedPayload_ResetsStoreToEmpty()
    {
        var path = JsonStore.GetFilePath("logfiles.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "data": {}
            }
            """);

        var repo = new JsonLogFileRepository();

        var entries = await repo.GetAllAsync();

        Assert.Empty(entries);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("logfiles.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Empty(data.EnumerateArray());
    }
}
