namespace LogReader.Core.Tests;

using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public class DashboardPersistenceTests : IAsyncLifetime
{
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderTests_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task ExportImport_ViewRoundTrip_PreservesHierarchyAndMembership()
    {
        var fileRepo = new JsonLogFileRepository();
        var groupRepo = new JsonLogGroupRepository(fileRepo);

        var file1 = new LogFileEntry { FilePath = @"C:\logs\app.log" };
        var file2 = new LogFileEntry { FilePath = @"C:\logs\error.log" };
        var file3 = new LogFileEntry { FilePath = @"C:\logs\audit.log" };
        await fileRepo.AddAsync(file1);
        await fileRepo.AddAsync(file2);
        await fileRepo.AddAsync(file3);

        var root = new LogGroup
        {
            Name = "Prod",
            Kind = LogGroupKind.Branch,
            SortOrder = 0
        };
        var dashboard = new LogGroup
        {
            Name = "API Dashboard",
            Kind = LogGroupKind.Dashboard,
            ParentGroupId = root.Id,
            SortOrder = 0,
            FileIds = new List<string> { file1.Id, file2.Id }
        };
        var nestedDashboard = new LogGroup
        {
            Name = "Worker Dashboard",
            Kind = LogGroupKind.Dashboard,
            ParentGroupId = root.Id,
            SortOrder = 1,
            FileIds = new List<string> { file2.Id, file3.Id }
        };
        await groupRepo.AddAsync(root);
        await groupRepo.AddAsync(dashboard);
        await groupRepo.AddAsync(nestedDashboard);

        var exportPath = Path.Combine(_testDir, "view-export.json");
        await groupRepo.ExportViewAsync(exportPath);

        Assert.True(File.Exists(exportPath));

        var imported = await groupRepo.ImportViewAsync(exportPath);

        Assert.NotNull(imported);
        Assert.Equal(3, imported!.Groups.Count);

        var importedRoot = imported.Groups.Single(g => g.Name == "Prod");
        Assert.Equal(LogGroupKind.Branch, importedRoot.Kind);
        Assert.Empty(importedRoot.FilePaths);

        var importedDashboard = imported.Groups.Single(g => g.Name == "API Dashboard");
        Assert.Equal(importedRoot.Id, importedDashboard.ParentGroupId);
        Assert.Equal(LogGroupKind.Dashboard, importedDashboard.Kind);
        Assert.Equal(2, importedDashboard.FilePaths.Count);
        Assert.Contains(@"C:\logs\app.log", importedDashboard.FilePaths);
        Assert.Contains(@"C:\logs\error.log", importedDashboard.FilePaths);

        var importedNestedDashboard = imported.Groups.Single(g => g.Name == "Worker Dashboard");
        Assert.Equal(importedRoot.Id, importedNestedDashboard.ParentGroupId);
        Assert.Equal(2, importedNestedDashboard.FilePaths.Count);
        Assert.Contains(@"C:\logs\error.log", importedNestedDashboard.FilePaths);
        Assert.Contains(@"C:\logs\audit.log", importedNestedDashboard.FilePaths);
    }

    [Fact]
    public async Task ImportView_NonExistentFile_ReturnsNull()
    {
        var fileRepo = new JsonLogFileRepository();
        var groupRepo = new JsonLogGroupRepository(fileRepo);

        var result = await groupRepo.ImportViewAsync(Path.Combine(_testDir, "nonexistent.json"));

        Assert.Null(result);
    }

    [Fact]
    public async Task ImportView_MalformedJson_ThrowsInvalidData()
    {
        var fileRepo = new JsonLogFileRepository();
        var groupRepo = new JsonLogGroupRepository(fileRepo);
        var importPath = Path.Combine(_testDir, "malformed.json");
        await File.WriteAllTextAsync(importPath, "{ \"groupName\": ");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => groupRepo.ImportViewAsync(importPath));

        Assert.Contains("not valid dashboard view json", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DashboardFileMembership_ManyToMany_IsSupported()
    {
        var file1Id = Guid.NewGuid().ToString();
        var file2Id = Guid.NewGuid().ToString();

        var dashboard1 = new LogGroup { Name = "Dashboard A", Kind = LogGroupKind.Dashboard, FileIds = new List<string> { file1Id, file2Id } };
        var dashboard2 = new LogGroup { Name = "Dashboard B", Kind = LogGroupKind.Dashboard, FileIds = new List<string> { file1Id } };

        // File1 belongs to both dashboards (many-to-many)
        Assert.Contains(file1Id, dashboard1.FileIds);
        Assert.Contains(file1Id, dashboard2.FileIds);

        // File2 belongs only to dashboard1
        Assert.Contains(file2Id, dashboard1.FileIds);
        Assert.DoesNotContain(file2Id, dashboard2.FileIds);
    }

    [Fact]
    public void ViewExport_HasCorrectDefaults()
    {
        var export = new ViewExport
        {
            Groups = new List<ViewExportGroup>
            {
                new()
                {
                    Name = "Test Dashboard",
                    FilePaths = new List<string> { @"C:\path1.log", @"C:\path2.log" }
                }
            }
        };

        Assert.Single(export.Groups);
        Assert.Equal("Test Dashboard", export.Groups[0].Name);
        Assert.Equal(2, export.Groups[0].FilePaths.Count);
        Assert.True(export.ExportedAt <= DateTime.UtcNow);
    }

}
