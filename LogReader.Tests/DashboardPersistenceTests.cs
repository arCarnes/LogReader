namespace LogReader.Tests;

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
    public async Task ExportImport_DashboardRoundTrip()
    {
        var fileRepo = new JsonLogFileRepository();
        var groupRepo = new JsonLogGroupRepository(fileRepo);

        // Add test file entries
        var file1 = new LogFileEntry { FilePath = @"C:\logs\app.log" };
        var file2 = new LogFileEntry { FilePath = @"C:\logs\error.log" };
        await fileRepo.AddAsync(file1);
        await fileRepo.AddAsync(file2);

        // Create a dashboard
        var dashboard = new LogGroup
        {
            Name = "Production Dashboard",
            Kind = LogGroupKind.Dashboard,
            FileIds = new List<string> { file1.Id, file2.Id }
        };
        await groupRepo.AddAsync(dashboard);

        try
        {
            // Export
            var exportPath = Path.Combine(_testDir, "export.json");
            await groupRepo.ExportGroupAsync(dashboard.Id, exportPath);

            Assert.True(File.Exists(exportPath));

            // Import
            var imported = await groupRepo.ImportGroupAsync(exportPath);

            Assert.NotNull(imported);
            Assert.Equal("Production Dashboard", imported!.GroupName);
            Assert.Equal(2, imported.FilePaths.Count);
            Assert.Contains(@"C:\logs\app.log", imported.FilePaths);
            Assert.Contains(@"C:\logs\error.log", imported.FilePaths);
        }
        finally
        {
            // Cleanup: remove test data from real AppData store
            await fileRepo.DeleteAsync(file1.Id);
            await fileRepo.DeleteAsync(file2.Id);
            await groupRepo.DeleteAsync(dashboard.Id);
        }
    }

    [Fact]
    public async Task ImportDashboard_NonExistentFile_ReturnsNull()
    {
        var fileRepo = new JsonLogFileRepository();
        var groupRepo = new JsonLogGroupRepository(fileRepo);

        var result = await groupRepo.ImportGroupAsync(Path.Combine(_testDir, "nonexistent.json"));

        Assert.Null(result);
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
    public void DashboardExport_HasCorrectDefaults()
    {
        var export = new GroupExport
        {
            GroupName = "Test Dashboard",
            FilePaths = new List<string> { @"C:\path1.log", @"C:\path2.log" }
        };

        Assert.Equal("Test Dashboard", export.GroupName);
        Assert.Equal(2, export.FilePaths.Count);
        Assert.True(export.ExportedAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task Export_Branch_FlattensDescendantDashboardFilePaths()
    {
        // Exporting a Branch should recursively resolve all descendant Dashboard paths.
        var fileRepo = new JsonLogFileRepository();
        var groupRepo = new JsonLogGroupRepository(fileRepo);

        var file1 = new LogFileEntry { FilePath = @"C:\logs\x.log" };
        var file2 = new LogFileEntry { FilePath = @"C:\logs\y.log" };
        var file3 = new LogFileEntry { FilePath = @"C:\logs\z.log" };
        await fileRepo.AddAsync(file1);
        await fileRepo.AddAsync(file2);
        await fileRepo.AddAsync(file3);

        var branch = new LogGroup { Name = "Root Branch", Kind = LogGroupKind.Branch };
        await groupRepo.AddAsync(branch);

        var childA = new LogGroup
        {
            Name = "Child A",
            Kind = LogGroupKind.Dashboard,
            ParentGroupId = branch.Id,
            FileIds = new List<string> { file1.Id, file2.Id }
        };
        var childB = new LogGroup
        {
            Name = "Child B",
            Kind = LogGroupKind.Dashboard,
            ParentGroupId = branch.Id,
            FileIds = new List<string> { file3.Id }
        };
        await groupRepo.AddAsync(childA);
        await groupRepo.AddAsync(childB);

        try
        {
            var exportPath = Path.Combine(_testDir, "branch-export.json");
            await groupRepo.ExportGroupAsync(branch.Id, exportPath);

            Assert.True(File.Exists(exportPath));

            var imported = await groupRepo.ImportGroupAsync(exportPath);

            Assert.NotNull(imported);
            Assert.Equal("Root Branch", imported!.GroupName);
            Assert.Equal(3, imported.FilePaths.Count);
            Assert.Contains(@"C:\logs\x.log", imported.FilePaths);
            Assert.Contains(@"C:\logs\y.log", imported.FilePaths);
            Assert.Contains(@"C:\logs\z.log", imported.FilePaths);
        }
        finally
        {
            await fileRepo.DeleteAsync(file1.Id);
            await fileRepo.DeleteAsync(file2.Id);
            await fileRepo.DeleteAsync(file3.Id);
            await groupRepo.DeleteAsync(branch.Id); // cascades childA and childB
        }
    }
}
