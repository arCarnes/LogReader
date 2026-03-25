namespace LogReader.Core.Tests;

using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public class JsonLogGroupRepositoryTests : IAsyncLifetime
{
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderGroupRepoTests_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task GetAllAsync_MalformedJson_ThrowsRecoveryExceptionAndPreservesStore()
    {
        const string contents = "{ invalid json";
        var path = JsonStore.GetFilePath("loggroups.json");
        await File.WriteAllTextAsync(path, contents);

        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());

        var ex = await Assert.ThrowsAsync<PersistedStateRecoveryException>(() => repo.GetAllAsync());

        Assert.Equal("dashboard view", ex.StoreDisplayName);
        Assert.Equal(path, ex.StorePath);
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task GetAllAsync_LegacyArray_RewritesVersionedEnvelope()
    {
        var path = JsonStore.GetFilePath("loggroups.json");
        await File.WriteAllTextAsync(path, """
            [
              {
                "id": "group-1",
                "name": "Legacy Dashboard",
                "sortOrder": 0,
                "parentGroupId": null,
                "kind": "Dashboard",
                "fileIds": ["file-1"]
              }
            ]
            """);

        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());

        var groups = await repo.GetAllAsync();

        var group = Assert.Single(groups);
        Assert.Equal("group-1", group.Id);

        using var document = await LoadPersistedDocumentAsync("loggroups.json");
        var data = AssertVersionedEnvelope(document);
        Assert.Equal("Legacy Dashboard", data[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetAllAsync_MissingSchemaVersionInEnvelope_ThrowsRecoveryExceptionAndPreservesStore()
    {
        const string contents = """
            {
              "data": []
            }
            """;
        var path = JsonStore.GetFilePath("loggroups.json");
        await File.WriteAllTextAsync(path, contents);

        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());

        var ex = await Assert.ThrowsAsync<PersistedStateRecoveryException>(() => repo.GetAllAsync());

        Assert.Equal("dashboard view", ex.StoreDisplayName);
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
        var path = JsonStore.GetFilePath("loggroups.json");
        await File.WriteAllTextAsync(path, contents);

        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());

        var ex = await Assert.ThrowsAsync<PersistedStateRecoveryException>(() => repo.GetAllAsync());

        Assert.Equal("dashboard view", ex.StoreDisplayName);
        Assert.Equal(path, ex.StorePath);
        Assert.Equal(contents.ReplaceLineEndings(), (await File.ReadAllTextAsync(path)).ReplaceLineEndings());
    }

    [Fact]
    public async Task GetAllAsync_InvalidPersistedTopology_ThrowsRecoveryExceptionAndPreservesStore()
    {
        const string contents = """
            {
              "schemaVersion": 1,
              "data": [
                {
                  "id": "branch-1",
                  "name": "Broken Branch",
                  "sortOrder": 0,
                  "parentGroupId": null,
                  "kind": "Branch",
                  "fileIds": [ "file-1" ]
                }
              ]
            }
            """;
        var path = JsonStore.GetFilePath("loggroups.json");
        await File.WriteAllTextAsync(path, contents);

        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());

        var ex = await Assert.ThrowsAsync<PersistedStateRecoveryException>(() => repo.GetAllAsync());

        Assert.Equal("dashboard view", ex.StoreDisplayName);
        Assert.Equal(path, ex.StorePath);
        Assert.Contains("cannot own file IDs", ex.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(contents.ReplaceLineEndings(), (await File.ReadAllTextAsync(path)).ReplaceLineEndings());
    }

    [Fact]
    public async Task ReplaceAllAsync_ReplacesPersistedGroupsInOneSnapshot()
    {
        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());
        await repo.AddAsync(new LogGroup
        {
            Id = "old-dashboard",
            Name = "Old Dashboard",
            Kind = LogGroupKind.Dashboard,
            SortOrder = 0
        });

        await repo.ReplaceAllAsync(new List<LogGroup>
        {
            new()
            {
                Id = "folder-1",
                Name = "Imported Folder",
                Kind = LogGroupKind.Branch,
                SortOrder = 0
            },
            new()
            {
                Id = "dashboard-1",
                Name = "Imported Dashboard",
                ParentGroupId = "folder-1",
                Kind = LogGroupKind.Dashboard,
                SortOrder = 0
            }
        });

        var groups = await repo.GetAllAsync();

        Assert.Equal(2, groups.Count);
        Assert.DoesNotContain(groups, group => group.Id == "old-dashboard");
        Assert.Equal(new[] { "Imported Folder", "Imported Dashboard" }, groups.Select(group => group.Name).ToArray());
    }

    [Fact]
    public async Task DeleteAsync_RemovesDescendantsButKeepsUnrelatedGroups()
    {
        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());

        var root = new LogGroup { Id = "root", Name = "Root", Kind = LogGroupKind.Branch, SortOrder = 0 };
        var child = new LogGroup
        {
            Id = "child",
            Name = "Child Folder",
            Kind = LogGroupKind.Branch,
            ParentGroupId = root.Id,
            SortOrder = 0
        };
        var grandchild = new LogGroup
        {
            Id = "grandchild",
            Name = "Grandchild Dashboard",
            Kind = LogGroupKind.Dashboard,
            ParentGroupId = child.Id,
            SortOrder = 0
        };
        var sibling = new LogGroup
        {
            Id = "sibling",
            Name = "Sibling Root",
            Kind = LogGroupKind.Dashboard,
            SortOrder = 1
        };

        await repo.AddAsync(root);
        await repo.AddAsync(child);
        await repo.AddAsync(grandchild);
        await repo.AddAsync(sibling);

        await repo.DeleteAsync(root.Id);

        var groups = await repo.GetAllAsync();
        var remaining = Assert.Single(groups);
        Assert.Equal(sibling.Id, remaining.Id);
    }

    [Fact]
    public async Task ReorderAsync_UpdatesSortOrderAndGetAllReturnsRequestedOrder()
    {
        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());

        var first = new LogGroup { Name = "First", Kind = LogGroupKind.Dashboard, SortOrder = 0 };
        var second = new LogGroup { Name = "Second", Kind = LogGroupKind.Dashboard, SortOrder = 1 };
        var third = new LogGroup { Name = "Third", Kind = LogGroupKind.Dashboard, SortOrder = 2 };

        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await repo.AddAsync(third);

        await repo.ReorderAsync(new List<string> { third.Id, first.Id, second.Id });

        var groups = await repo.GetAllAsync();
        Assert.Equal(new[] { third.Id, first.Id, second.Id }, groups.Select(g => g.Id).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, groups.Select(g => g.SortOrder).ToArray());
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
