namespace LogReader.Core.Tests;

using System.Text.Json;
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
    public async Task GetAllAsync_MalformedJson_ResetsStoreToEmpty()
    {
        var path = JsonStore.GetFilePath("loggroups.json");
        await File.WriteAllTextAsync(path, "{ invalid json");
        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());

        var groups = await repo.GetAllAsync();

        Assert.Empty(groups);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("loggroups.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Empty(data.EnumerateArray());
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

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("loggroups.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Equal("Legacy Dashboard", data[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetAllAsync_MissingSchemaVersionInEnvelope_ResetsStoreToEmpty()
    {
        var path = JsonStore.GetFilePath("loggroups.json");
        await File.WriteAllTextAsync(path, """
            {
              "data": [
                {
                  "id": "group-1",
                  "name": "Legacy Dashboard",
                  "sortOrder": 0,
                  "kind": "Dashboard",
                  "fileIds": []
                }
              ]
            }
            """);

        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());

        var groups = await repo.GetAllAsync();

        Assert.Empty(groups);

        using var document = await JsonRepositoryAssertions.LoadPersistedDocumentAsync("loggroups.json");
        var data = JsonRepositoryAssertions.AssertVersionedEnvelope(document);
        Assert.Empty(data.EnumerateArray());
    }

    [Fact]
    public async Task GetAllAsync_MalformedVersionedPayload_ResetsStoreToEmpty()
    {
        var path = JsonStore.GetFilePath("loggroups.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "data": {}
            }
            """);

        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());

        var groups = await repo.GetAllAsync();

        Assert.Empty(groups);
    }

    [Fact]
    public async Task GetAllAsync_NormalizeTree_ConvertsInvalidTopologyAndClearsBranchFiles()
    {
        var fileRepo = new JsonLogFileRepository();
        var repo = new JsonLogGroupRepository(fileRepo);

        var entry = new LogFileEntry { FilePath = @"C:\logs\normalize.log" };
        await fileRepo.AddAsync(entry);

        var branchWithFiles = new LogGroup
        {
            Name = "Branch With Files",
            Kind = LogGroupKind.Branch,
            SortOrder = 0,
            FileIds = new List<string> { entry.Id }
        };

        var parentDashboardWithChild = new LogGroup
        {
            Name = "Parent Dashboard",
            Kind = LogGroupKind.Dashboard,
            SortOrder = 1,
            FileIds = new List<string> { entry.Id }
        };

        var childDashboard = new LogGroup
        {
            Name = "Child Dashboard",
            Kind = LogGroupKind.Dashboard,
            ParentGroupId = parentDashboardWithChild.Id,
            SortOrder = 2,
            FileIds = new List<string> { entry.Id }
        };

        var leafDashboard = new LogGroup
        {
            Name = "Leaf Dashboard",
            Kind = LogGroupKind.Dashboard,
            SortOrder = 3,
            FileIds = new List<string> { entry.Id }
        };

        await repo.AddAsync(branchWithFiles);
        await repo.AddAsync(parentDashboardWithChild);
        await repo.AddAsync(childDashboard);
        await repo.AddAsync(leafDashboard);

        var groups = await repo.GetAllAsync();

        var normalizedBranch = groups.Single(g => g.Id == branchWithFiles.Id);
        Assert.Equal(LogGroupKind.Branch, normalizedBranch.Kind);
        Assert.Empty(normalizedBranch.FileIds);

        var normalizedParent = groups.Single(g => g.Id == parentDashboardWithChild.Id);
        Assert.Equal(LogGroupKind.Branch, normalizedParent.Kind);
        Assert.Empty(normalizedParent.FileIds);

        var normalizedChild = groups.Single(g => g.Id == childDashboard.Id);
        Assert.Equal(LogGroupKind.Dashboard, normalizedChild.Kind);
        Assert.Equal(new[] { entry.Id }, normalizedChild.FileIds);

        var normalizedLeaf = groups.Single(g => g.Id == leafDashboard.Id);
        Assert.Equal(LogGroupKind.Dashboard, normalizedLeaf.Kind);
        Assert.Equal(new[] { entry.Id }, normalizedLeaf.FileIds);

        Assert.Equal(new[] { branchWithFiles.Id, parentDashboardWithChild.Id, childDashboard.Id, leafDashboard.Id }, groups.Select(g => g.Id).ToArray());
    }

    [Fact]
    public async Task DeleteAsync_RemovesDescendantsButKeepsUnrelatedGroups()
    {
        var repo = new JsonLogGroupRepository(new JsonLogFileRepository());

        var root = new LogGroup { Name = "Root", Kind = LogGroupKind.Branch, SortOrder = 0 };
        var child = new LogGroup
        {
            Name = "Child",
            Kind = LogGroupKind.Dashboard,
            ParentGroupId = root.Id,
            SortOrder = 1
        };
        var grandchild = new LogGroup
        {
            Name = "Grandchild",
            Kind = LogGroupKind.Dashboard,
            ParentGroupId = child.Id,
            SortOrder = 2
        };
        var sibling = new LogGroup
        {
            Name = "Sibling Root",
            Kind = LogGroupKind.Dashboard,
            SortOrder = 3
        };

        await repo.AddAsync(root);
        await repo.AddAsync(child);
        await repo.AddAsync(grandchild);
        await repo.AddAsync(sibling);

        await repo.DeleteAsync(root.Id);

        var groups = await repo.GetAllAsync();
        Assert.Single(groups);
        Assert.Equal(sibling.Id, groups[0].Id);
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
}
