namespace LogReader.Tests;

using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public class GroupPersistenceTests : IAsyncLifetime
{
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ExportImport_RoundTrip()
    {
        var fileRepo = new JsonLogFileRepository();
        var groupRepo = new JsonLogGroupRepository(fileRepo);

        // Add test file entries
        var file1 = new LogFileEntry { FilePath = @"C:\logs\app.log" };
        var file2 = new LogFileEntry { FilePath = @"C:\logs\error.log" };
        await fileRepo.AddAsync(file1);
        await fileRepo.AddAsync(file2);

        // Create a group
        var group = new LogGroup
        {
            Name = "Production Logs",
            FileIds = new List<string> { file1.Id, file2.Id }
        };
        await groupRepo.AddAsync(group);

        try
        {
            // Export
            var exportPath = Path.Combine(_testDir, "export.json");
            await groupRepo.ExportGroupAsync(group.Id, exportPath);

            Assert.True(File.Exists(exportPath));

            // Import
            var imported = await groupRepo.ImportGroupAsync(exportPath);

            Assert.NotNull(imported);
            Assert.Equal("Production Logs", imported!.GroupName);
            Assert.Equal(2, imported.FilePaths.Count);
            Assert.Contains(@"C:\logs\app.log", imported.FilePaths);
            Assert.Contains(@"C:\logs\error.log", imported.FilePaths);
        }
        finally
        {
            // Cleanup: remove test data from real AppData store
            await fileRepo.DeleteAsync(file1.Id);
            await fileRepo.DeleteAsync(file2.Id);
            await groupRepo.DeleteAsync(group.Id);
        }
    }

    [Fact]
    public async Task ImportGroup_NonExistentFile_ReturnsNull()
    {
        var fileRepo = new JsonLogFileRepository();
        var groupRepo = new JsonLogGroupRepository(fileRepo);

        var result = await groupRepo.ImportGroupAsync(Path.Combine(_testDir, "nonexistent.json"));

        Assert.Null(result);
    }

    [Fact]
    public void LogGroup_ManyToMany_MultipleGroups()
    {
        var file1Id = Guid.NewGuid().ToString();
        var file2Id = Guid.NewGuid().ToString();

        var group1 = new LogGroup { Name = "Group A", FileIds = new List<string> { file1Id, file2Id } };
        var group2 = new LogGroup { Name = "Group B", FileIds = new List<string> { file1Id } };

        // File1 belongs to both groups (many-to-many)
        Assert.Contains(file1Id, group1.FileIds);
        Assert.Contains(file1Id, group2.FileIds);

        // File2 belongs only to group1
        Assert.Contains(file2Id, group1.FileIds);
        Assert.DoesNotContain(file2Id, group2.FileIds);
    }

    [Fact]
    public void GroupExport_HasCorrectDefaults()
    {
        var export = new GroupExport
        {
            GroupName = "Test Group",
            FilePaths = new List<string> { @"C:\path1.log", @"C:\path2.log" }
        };

        Assert.Equal("Test Group", export.GroupName);
        Assert.Equal(2, export.FilePaths.Count);
        Assert.True(export.ExportedAt <= DateTime.UtcNow);
    }
}
