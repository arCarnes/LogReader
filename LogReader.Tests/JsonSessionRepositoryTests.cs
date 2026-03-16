using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using System.Text.Json;

namespace LogReader.Tests;

public class JsonSessionRepositoryTests : IAsyncLifetime
{
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderSessionTests_" + Guid.NewGuid().ToString("N")[..8]);
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
        var repo = new JsonSessionRepository();

        var session = await repo.LoadAsync();

        Assert.Empty(session.OpenTabs);
        Assert.Null(session.ActiveTabId);
    }

    [Fact]
    public async Task SaveLoad_RoundTrip_PersistsSessionState()
    {
        var repo = new JsonSessionRepository();
        var expected = new SessionState
        {
            ActiveTabId = "tab-2",
            OpenTabs = new List<OpenTabState>
            {
                new()
                {
                    FileId = "tab-1",
                    FilePath = @"C:\logs\a.log",
                    Encoding = FileEncoding.Utf8Bom,
                    AutoScrollEnabled = true,
                    IsPinned = false
                },
                new()
                {
                    FileId = "tab-2",
                    FilePath = @"C:\logs\b.log",
                    Encoding = FileEncoding.Utf16Be,
                    AutoScrollEnabled = false,
                    IsPinned = true
                }
            }
        };

        await repo.SaveAsync(expected);
        var loaded = await repo.LoadAsync();

        Assert.Equal(expected.ActiveTabId, loaded.ActiveTabId);
        Assert.Equal(2, loaded.OpenTabs.Count);
        Assert.Equal(FileEncoding.Utf8Bom, loaded.OpenTabs[0].Encoding);
        Assert.Equal(FileEncoding.Utf16Be, loaded.OpenTabs[1].Encoding);
        Assert.True(loaded.OpenTabs[1].IsPinned);
        Assert.False(loaded.OpenTabs[1].AutoScrollEnabled);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("session.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal("tab-2", data.GetProperty("activeTabId").GetString());
    }

    [Fact]
    public async Task LoadAsync_LegacyPayload_RewritesVersionedEnvelope()
    {
        var path = JsonStore.GetFilePath("session.json");
        await File.WriteAllTextAsync(path, """
            {
              "activeTabId": "legacy-tab",
              "openTabs": [
                {
                  "fileId": "legacy-tab",
                  "filePath": "C:\\legacy.log",
                  "encoding": "Utf8",
                  "autoScrollEnabled": true,
                  "isPinned": false
                }
              ]
            }
            """);

        var repo = new JsonSessionRepository();

        var loaded = await repo.LoadAsync();

        Assert.Equal("legacy-tab", loaded.ActiveTabId);
        Assert.Single(loaded.OpenTabs);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("session.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal("legacy-tab", data.GetProperty("activeTabId").GetString());
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_ResetsStoreToDefaults()
    {
        var path = JsonStore.GetFilePath("session.json");
        await File.WriteAllTextAsync(path, "{ invalid json");

        var repo = new JsonSessionRepository();

        var loaded = await repo.LoadAsync();

        Assert.Null(loaded.ActiveTabId);
        Assert.Empty(loaded.OpenTabs);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("session.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("activeTabId").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("openTabs").ValueKind);
        Assert.Empty(data.GetProperty("openTabs").EnumerateArray());
    }

    [Fact]
    public async Task LoadAsync_MissingSchemaVersionInEnvelope_ResetsStoreToDefaults()
    {
        var path = JsonStore.GetFilePath("session.json");
        await File.WriteAllTextAsync(path, """
            {
              "data": {
                "activeTabId": "legacy-tab",
                "openTabs": []
              }
            }
            """);

        var repo = new JsonSessionRepository();

        var loaded = await repo.LoadAsync();

        Assert.Null(loaded.ActiveTabId);
        Assert.Empty(loaded.OpenTabs);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("session.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("activeTabId").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("openTabs").ValueKind);
        Assert.Empty(data.GetProperty("openTabs").EnumerateArray());
    }

    [Fact]
    public async Task LoadAsync_MalformedVersionedPayload_ResetsStoreToDefaults()
    {
        var path = JsonStore.GetFilePath("session.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "data": []
            }
            """);

        var repo = new JsonSessionRepository();

        var loaded = await repo.LoadAsync();

        Assert.Null(loaded.ActiveTabId);
        Assert.Empty(loaded.OpenTabs);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("session.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("activeTabId").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("openTabs").ValueKind);
        Assert.Empty(data.GetProperty("openTabs").EnumerateArray());
    }

    [Fact]
    public async Task SaveAsync_ConcurrentCalls_SerializesWritesWithoutExceptions()
    {
        var repo = new JsonSessionRepository();
        var first = new SessionState
        {
            ActiveTabId = "first",
            OpenTabs = Enumerable.Range(0, 5_000).Select(i => new OpenTabState
            {
                FileId = $"first-{i}",
                FilePath = $@"C:\logs\first-{i}.log",
                Encoding = FileEncoding.Utf8,
                AutoScrollEnabled = true,
                IsPinned = false
            }).ToList()
        };
        var second = new SessionState
        {
            ActiveTabId = "second",
            OpenTabs = new List<OpenTabState>
            {
                new()
                {
                    FileId = "tab-2",
                    FilePath = @"C:\logs\second.log",
                    Encoding = FileEncoding.Utf16,
                    AutoScrollEnabled = false,
                    IsPinned = true
                }
            }
        };

        var firstSave = Task.Run(() => repo.SaveAsync(first));
        var secondSave = Task.Run(() => repo.SaveAsync(second));

        await Task.WhenAll(firstSave, secondSave);
        var loaded = await repo.LoadAsync();

        // Either write may win; assert the final state is a coherent snapshot from one input.
        if (loaded.ActiveTabId == "first")
        {
            Assert.Equal(5_000, loaded.OpenTabs.Count);
            Assert.All(loaded.OpenTabs, tab => Assert.Equal(FileEncoding.Utf8, tab.Encoding));
        }
        else
        {
            Assert.Equal("second", loaded.ActiveTabId);
            Assert.Single(loaded.OpenTabs);
            Assert.Equal(@"C:\logs\second.log", loaded.OpenTabs[0].FilePath);
            Assert.Equal(FileEncoding.Utf16, loaded.OpenTabs[0].Encoding);
        }
    }

    [Fact]
    public async Task LoadAsync_WhileSaving_CompletesWithoutInvalidState()
    {
        var repo = new JsonSessionRepository();
        var initial = new SessionState
        {
            ActiveTabId = "initial",
            OpenTabs = new List<OpenTabState>
            {
                new()
                {
                    FileId = "initial-tab",
                    FilePath = @"C:\logs\initial.log",
                    Encoding = FileEncoding.Utf8,
                    AutoScrollEnabled = true,
                    IsPinned = false
                }
            }
        };
        var updated = new SessionState
        {
            ActiveTabId = "updated",
            OpenTabs = Enumerable.Range(0, 250).Select(i => new OpenTabState
            {
                FileId = $"updated-{i}",
                FilePath = $@"C:\logs\updated-{i}.log",
                Encoding = FileEncoding.Utf16Be,
                AutoScrollEnabled = i % 2 == 0,
                IsPinned = i % 3 == 0
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
            Assert.NotNull(loaded.OpenTabs);
        }

        var final = await repo.LoadAsync();
        Assert.Equal("updated", final.ActiveTabId);
        Assert.Equal(250, final.OpenTabs.Count);
    }
}
