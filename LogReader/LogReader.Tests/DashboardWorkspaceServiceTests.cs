using System.Collections.ObjectModel;
using System.Text.Json;
using LogReader.App.Models;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;
using LogReader.Testing;

namespace LogReader.Tests;

public class DashboardWorkspaceServiceTests
{
    [Fact]
    public void ParseBulkFilePaths_TrimsStripsQuotesIgnoresBlankLinesAndDeduplicatesExactPaths()
    {
        var paths = BulkFilePathHelper.Parse(
            string.Join(
                Environment.NewLine,
                "  \"C:\\logs\\app.log\"  ",
                string.Empty,
                " 'C:\\logs\\api.log' ",
                "   C:\\logs\\worker.log   ",
                "\"C:\\logs\\app.log\"",
                "\"   \""));

        Assert.Equal(
            new[]
            {
                @"C:\logs\app.log",
                @"C:\logs\api.log",
                @"C:\logs\worker.log"
            },
            paths);
    }

    [Fact]
    public void ParseBulkFilePaths_ExpandsWildcardPatternsInFileNameOnly()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderBulkWildcard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(testDir, "app-1.log"), string.Empty);
        File.WriteAllText(Path.Combine(testDir, "app-2.log"), string.Empty);
        File.WriteAllText(Path.Combine(testDir, "other.txt"), string.Empty);

        try
        {
            var paths = BulkFilePathHelper.Parse(
                string.Join(
                    Environment.NewLine,
                    $"\"{Path.Combine(testDir, "app-?.log")}\"",
                    $"\"{Path.Combine(testDir, "app-1.log")}\""));

            Assert.Equal(
                new[]
                {
                    Path.Combine(testDir, "app-1.log"),
                    Path.Combine(testDir, "app-2.log")
                },
                paths);
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void BuildBulkFilePreview_ReportsFoundAndMissingPaths()
    {
        var preview = BulkFilePathHelper.BuildPreview(
            string.Join(
                Environment.NewLine,
                @"C:\logs\app.log",
                @"C:\logs\missing.log"),
            path => string.Equals(path, @"C:\logs\app.log", StringComparison.Ordinal)
                ? DashboardFileProbeResult.Found
                : DashboardFileProbeResult.Missing);

        Assert.Equal(2, preview.ParsedPaths.Count);
        Assert.Equal(1, preview.FoundCount);
        Assert.Equal(1, preview.MissingCount);
        Assert.Collection(
            preview.Items,
            item =>
            {
                Assert.Equal(@"C:\logs\app.log", item.FilePath);
                Assert.True(item.IsFound);
                Assert.Equal(BulkFilePreviewItemStatus.Found, item.Status);
            },
            item =>
            {
                Assert.Equal(@"C:\logs\missing.log", item.FilePath);
                Assert.False(item.IsFound);
                Assert.Equal(BulkFilePreviewItemStatus.Missing, item.Status);
            });
    }

    [Theory]
    [InlineData(@"\\?\C:\logs\app.log")]
    [InlineData(@"\\?\UNC\server\share\app.log")]
    public void BuildBulkFilePreview_ExtendedPathPrefixQuestionMark_IsNotWildcard(string path)
    {
        var preview = BulkFilePathHelper.BuildPreview(
            path,
            candidate => string.Equals(candidate, path, StringComparison.Ordinal)
                ? DashboardFileProbeResult.Found
                : DashboardFileProbeResult.Missing);

        var item = Assert.Single(preview.Items);
        Assert.Equal(path, item.FilePath);
        Assert.Equal(BulkFilePreviewItemStatus.Found, item.Status);
        Assert.Equal(new[] { path }, preview.ParsedPaths);
    }

    [Fact]
    public void BuildBulkFilePreview_DoesNotExpandWildcardDirectorySegments()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderBulkPreviewDirWildcard_" + Guid.NewGuid().ToString("N"));
        var nestedDir = Path.Combine(testDir, "service-a");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(Path.Combine(nestedDir, "app.log"), string.Empty);

        try
        {
            var preview = BulkFilePathHelper.BuildPreview(
                Path.Combine(testDir, "service-*", "*.log"));

            Assert.Empty(preview.ParsedPaths);
            var item = Assert.Single(preview.Items);
            Assert.Equal(Path.Combine(testDir, "service-*", "*.log"), item.FilePath);
            Assert.Equal(BulkFilePreviewItemStatus.NoMatches, item.Status);
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void BuildBulkFilePreview_ReportsWildcardPatternsWithoutMatches()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderBulkPreview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var preview = BulkFilePathHelper.BuildPreview(
                Path.Combine(testDir, "*.log"));

            Assert.Empty(preview.ParsedPaths);
            var item = Assert.Single(preview.Items);
            Assert.Equal(Path.Combine(testDir, "*.log"), item.FilePath);
            Assert.Equal(BulkFilePreviewItemStatus.NoMatches, item.Status);
            Assert.False(item.IsFound);
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void ParseBulkFilePaths_DeduplicatesCaseInsensitivePaths()
    {
        var paths = BulkFilePathHelper.Parse(
            string.Join(
                Environment.NewLine,
                @"C:\Logs\App.log",
                @"c:\logs\app.log"));

        var path = Assert.Single(paths);
        Assert.Equal(@"C:\Logs\App.log", path);
    }

    [Fact]
    public void BuildBulkFilePreview_DeduplicatesCaseInsensitivePaths()
    {
        var preview = BulkFilePathHelper.BuildPreview(
            string.Join(
                Environment.NewLine,
                @"C:\Logs\App.log",
                @"c:\logs\app.log"),
            _ => DashboardFileProbeResult.Found);

        var item = Assert.Single(preview.Items);
        Assert.Equal(@"C:\Logs\App.log", item.FilePath);
        Assert.Equal(new[] { @"C:\Logs\App.log" }, preview.ParsedPaths);
    }

    [Fact]
    public void BuildBulkFilePreview_ReportsProbeStatuses()
    {
        var preview = BulkFilePathHelper.BuildPreview(
            string.Join(
                Environment.NewLine,
                @"C:\logs\found.log",
                @"C:\logs\missing.log",
                @"C:\logs\denied.log",
                @"C:\logs\invalid.log",
                @"C:\logs\unavailable.log"),
            path => Path.GetFileName(path) switch
            {
                "found.log" => DashboardFileProbeResult.Found,
                "missing.log" => DashboardFileProbeResult.Missing,
                "denied.log" => DashboardFileProbeResult.AccessDenied,
                "invalid.log" => DashboardFileProbeResult.InvalidPath,
                "unavailable.log" => DashboardFileProbeResult.Unavailable,
                _ => DashboardFileProbeResult.Missing
            });

        Assert.Equal(5, preview.ParsedPaths.Count);
        Assert.Equal(1, preview.FoundCount);
        Assert.Equal(1, preview.MissingCount);
        Assert.Equal(3, preview.UnavailableCount);
        Assert.Collection(
            preview.Items,
            item => Assert.Equal(BulkFilePreviewItemStatus.Found, item.Status),
            item => Assert.Equal(BulkFilePreviewItemStatus.Missing, item.Status),
            item => Assert.Equal(BulkFilePreviewItemStatus.AccessDenied, item.Status),
            item => Assert.Equal(BulkFilePreviewItemStatus.InvalidPath, item.Status),
            item => Assert.Equal(BulkFilePreviewItemStatus.Unavailable, item.Status));
    }

    [Fact]
    public void BuildBulkFilePreview_ReportsWildcardExpansionFailures()
    {
        var preview = BulkFilePathHelper.BuildPreview("bad\0path\\*.log");

        Assert.Empty(preview.ParsedPaths);
        var item = Assert.Single(preview.Items);
        Assert.Equal(BulkFilePreviewItemStatus.InvalidPath, item.Status);
    }

    [Fact]
    public async Task RemoveFileFromDashboardAsync_RemovesMembershipPersistsAndRefreshesMemberFiles()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\logs\b.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        await fileRepo.AddAsync(fileB);

        var dashboard = CreateGroup("dashboard-1", "Dashboard", fileA.Id, fileB.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(dashboard.Model);

        var host = new DashboardWorkspaceHostStub(dashboard);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);
        var activationService = new DashboardActivationService(host, fileRepo, groupRepo);

        await activationService.RefreshAllMemberFilesAsync();
        Assert.Equal(new[] { fileA.Id, fileB.Id }, dashboard.MemberFiles.Select(member => member.FileId).ToArray());

        await service.RemoveFileFromDashboardAsync(dashboard, fileA.Id);

        Assert.Equal(new[] { fileB.Id }, dashboard.Model.FileIds);
        Assert.Equal(new[] { fileB.Id }, dashboard.MemberFiles.Select(member => member.FileId).ToArray());

        var persisted = await groupRepo.GetByIdAsync(dashboard.Id);
        Assert.NotNull(persisted);
        Assert.Equal(new[] { fileB.Id }, persisted!.FileIds);
        Assert.Equal(1, groupRepo.UpdateCallCount);
    }

    [Fact]
    public async Task RemoveFilesFromDashboardAsync_RemovesMembershipsPersistsOnceAndRefreshesMemberFiles()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\logs\b.log" };
        var fileC = new LogFileEntry { FilePath = @"C:\logs\c.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        await fileRepo.AddAsync(fileB);
        await fileRepo.AddAsync(fileC);

        var dashboard = CreateGroup("dashboard-1", "Dashboard", fileA.Id, fileB.Id, fileC.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(dashboard.Model);

        var host = new DashboardWorkspaceHostStub(dashboard);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);
        var activationService = new DashboardActivationService(host, fileRepo, groupRepo);

        await activationService.RefreshAllMemberFilesAsync();

        await service.RemoveFilesFromDashboardAsync(dashboard, new[] { fileA.Id, fileC.Id, fileA.Id });

        Assert.Equal(new[] { fileB.Id }, dashboard.Model.FileIds);
        Assert.Equal(new[] { fileB.Id }, dashboard.MemberFiles.Select(member => member.FileId).ToArray());

        var persisted = await groupRepo.GetByIdAsync(dashboard.Id);
        Assert.NotNull(persisted);
        Assert.Equal(new[] { fileB.Id }, persisted!.FileIds);
        Assert.Equal(1, groupRepo.UpdateCallCount);
    }

    [Fact]
    public async Task ReorderFileInDashboardAsync_ReordersMembershipPersistsAndRefreshesMemberFiles()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\logs\b.log" };
        var fileC = new LogFileEntry { FilePath = @"C:\logs\c.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        await fileRepo.AddAsync(fileB);
        await fileRepo.AddAsync(fileC);

        var dashboard = CreateGroup("dashboard-1", "Dashboard", fileA.Id, fileB.Id, fileC.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(dashboard.Model);

        var host = new DashboardWorkspaceHostStub(dashboard);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);
        var activationService = new DashboardActivationService(host, fileRepo, groupRepo);

        await activationService.RefreshAllMemberFilesAsync();

        await service.ReorderFileInDashboardAsync(dashboard, fileC.Id, fileA.Id, DropPlacement.Before);

        Assert.Equal(new[] { fileC.Id, fileA.Id, fileB.Id }, dashboard.Model.FileIds);
        Assert.Equal(
            new[] { fileC.Id, fileA.Id, fileB.Id },
            dashboard.MemberFiles.Select(member => member.FileId).ToArray());

        var persisted = await groupRepo.GetByIdAsync(dashboard.Id);
        Assert.NotNull(persisted);
        Assert.Equal(new[] { fileC.Id, fileA.Id, fileB.Id }, persisted!.FileIds);
        Assert.Equal(1, groupRepo.UpdateCallCount);
    }

    [Fact]
    public async Task ReorderFileInDashboardAsync_NoOpDrop_DoesNotPersistChanges()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\logs\b.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        await fileRepo.AddAsync(fileB);

        var dashboard = CreateGroup("dashboard-1", "Dashboard", fileA.Id, fileB.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(dashboard.Model);

        var host = new DashboardWorkspaceHostStub(dashboard);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);
        var activationService = new DashboardActivationService(host, fileRepo, groupRepo);

        await activationService.RefreshAllMemberFilesAsync();
        await service.ReorderFileInDashboardAsync(dashboard, fileA.Id, fileB.Id, DropPlacement.Before);

        Assert.Equal(new[] { fileA.Id, fileB.Id }, dashboard.Model.FileIds);
        Assert.Equal(0, groupRepo.UpdateCallCount);
    }

    [Fact]
    public async Task ReorderFileInDashboardAsync_MissingFileEntry_RemainsPresentAtNewPosition()
    {
        var missingEntry = new LogFileEntry { FilePath = @"C:\logs\missing.log" };
        var foundEntry = new LogFileEntry { FilePath = @"C:\logs\found.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(missingEntry);
        await fileRepo.AddAsync(foundEntry);

        var dashboard = CreateGroup("dashboard-1", "Dashboard", missingEntry.Id, foundEntry.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(dashboard.Model);
        Func<IReadOnlyDictionary<string, string>, Task<Dictionary<string, bool>>> buildFileExistenceMapAsync = _ => Task.FromResult(
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [missingEntry.Id] = false,
                [foundEntry.Id] = true
            });

        var host = new DashboardWorkspaceHostStub(dashboard);
        var service = new DashboardWorkspaceService(
            host,
            fileRepo,
            groupRepo,
            buildFileExistenceMapAsync);
        var activationService = new DashboardActivationService(host, fileRepo, groupRepo, buildFileExistenceMapAsync);

        await activationService.RefreshAllMemberFilesAsync();
        await service.ReorderFileInDashboardAsync(dashboard, missingEntry.Id, foundEntry.Id, DropPlacement.After);

        Assert.Equal(new[] { foundEntry.Id, missingEntry.Id }, dashboard.Model.FileIds);
        Assert.Equal(new[] { foundEntry.Id, missingEntry.Id }, dashboard.MemberFiles.Select(member => member.FileId).ToArray());
        Assert.True(dashboard.MemberFiles.Last().HasError);
    }

    [Fact]
    public async Task RefreshAllMemberFilesAsync_InaccessibleFile_ReportsAccessDeniedInsteadOfMissing()
    {
        var deniedEntry = new LogFileEntry { FilePath = @"\\server\share\denied.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(deniedEntry);

        var dashboard = CreateGroup("dashboard-1", "Dashboard", deniedEntry.Id);
        Func<IReadOnlyDictionary<string, string>, Task<Dictionary<string, DashboardFileProbeResult>>> buildFileProbeMapAsync =
            _ => Task.FromResult(
                new Dictionary<string, DashboardFileProbeResult>(StringComparer.Ordinal)
                {
                    [deniedEntry.Id] = DashboardFileProbeResult.AccessDenied
                });

        var host = new DashboardWorkspaceHostStub(dashboard);
        var activationService = new DashboardActivationService(host, fileRepo, new StubLogGroupRepository(), buildFileProbeMapAsync);

        await activationService.RefreshAllMemberFilesAsync();

        var member = Assert.Single(dashboard.MemberFiles);
        Assert.True(member.HasError);
        Assert.Equal("File unavailable: access denied", member.ErrorMessage);
    }

    [Fact]
    public async Task MoveFileBetweenDashboardsAsync_DropOnDashboardRow_AppendsToTargetAndRemovesFromSource()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\logs\b.log" };
        var fileC = new LogFileEntry { FilePath = @"C:\logs\c.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        await fileRepo.AddAsync(fileB);
        await fileRepo.AddAsync(fileC);

        var source = CreateGroup("dashboard-1", "Source", fileA.Id, fileB.Id);
        var target = CreateGroup("dashboard-2", "Target", fileC.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(source.Model);
        await groupRepo.AddAsync(target.Model);

        var host = new DashboardWorkspaceHostStub(source, target);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);
        var activationService = new DashboardActivationService(host, fileRepo, groupRepo);

        await activationService.RefreshAllMemberFilesAsync();
        await service.MoveFileBetweenDashboardsAsync(source, target, fileA.Id, targetFileId: null, DropPlacement.Inside);

        Assert.Equal(new[] { fileB.Id }, source.Model.FileIds);
        Assert.Equal(new[] { fileC.Id, fileA.Id }, target.Model.FileIds);
        Assert.Equal(new[] { fileB.Id }, source.MemberFiles.Select(member => member.FileId).ToArray());
        Assert.Equal(new[] { fileC.Id, fileA.Id }, target.MemberFiles.Select(member => member.FileId).ToArray());
        Assert.Equal(2, groupRepo.UpdateCallCount);
    }

    [Theory]
    [InlineData(DropPlacement.Before)]
    [InlineData(DropPlacement.After)]
    public async Task MoveFileBetweenDashboardsAsync_DropOnTargetFile_InsertsAtRequestedPosition(DropPlacement placement)
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\logs\b.log" };
        var fileC = new LogFileEntry { FilePath = @"C:\logs\c.log" };
        var fileD = new LogFileEntry { FilePath = @"C:\logs\d.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        await fileRepo.AddAsync(fileB);
        await fileRepo.AddAsync(fileC);
        await fileRepo.AddAsync(fileD);

        var source = CreateGroup("dashboard-1", "Source", fileA.Id);
        var target = CreateGroup("dashboard-2", "Target", fileB.Id, fileC.Id, fileD.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(source.Model);
        await groupRepo.AddAsync(target.Model);

        var host = new DashboardWorkspaceHostStub(source, target);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);
        var activationService = new DashboardActivationService(host, fileRepo, groupRepo);

        await activationService.RefreshAllMemberFilesAsync();
        await service.MoveFileBetweenDashboardsAsync(source, target, fileA.Id, fileC.Id, placement);

        Assert.Empty(source.Model.FileIds);
        Assert.Equal(
            placement == DropPlacement.Before
                ? new[] { fileB.Id, fileA.Id, fileC.Id, fileD.Id }
                : new[] { fileB.Id, fileC.Id, fileA.Id, fileD.Id },
            target.Model.FileIds);
    }

    [Fact]
    public async Task MoveFileBetweenDashboardsAsync_WhenTargetAlreadyContainsFile_DoesNotPersistOrChangeMembership()
    {
        var shared = new LogFileEntry { FilePath = @"C:\logs\shared.log" };
        var other = new LogFileEntry { FilePath = @"C:\logs\other.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(shared);
        await fileRepo.AddAsync(other);

        var source = CreateGroup("dashboard-1", "Source", shared.Id);
        var target = CreateGroup("dashboard-2", "Target", other.Id, shared.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(source.Model);
        await groupRepo.AddAsync(target.Model);

        var host = new DashboardWorkspaceHostStub(source, target);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);
        var activationService = new DashboardActivationService(host, fileRepo, groupRepo);

        await activationService.RefreshAllMemberFilesAsync();
        await service.MoveFileBetweenDashboardsAsync(source, target, shared.Id, targetFileId: null, DropPlacement.Inside);

        Assert.Equal(new[] { shared.Id }, source.Model.FileIds);
        Assert.Equal(new[] { other.Id, shared.Id }, target.Model.FileIds);
        Assert.Equal(0, groupRepo.UpdateCallCount);
    }

    [Fact]
    public async Task AddFilesToDashboardAsync_SkipsPathsAlreadyPresentInDashboard()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\logs\b.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        await fileRepo.AddAsync(fileB);

        var dashboard = CreateGroup("dashboard-1", "Dashboard", fileA.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(dashboard.Model);

        var host = new DashboardWorkspaceHostStub(dashboard);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

        await service.AddFilesToDashboardAsync(
            dashboard,
            new[]
            {
                @"C:\logs\a.log",
                @"C:\logs\b.log",
                @"C:\logs\b.log"
            });

        Assert.Equal(new[] { fileA.Id, fileB.Id }, dashboard.Model.FileIds);
        Assert.Equal(1, groupRepo.UpdateCallCount);
    }

    [Fact]
    public async Task AddFilesToDashboardAsync_SortsAddedFilesByFileNameNaturally()
    {
        var file1 = new LogFileEntry { FilePath = @"C:\logs\instance1.log" };
        var file2 = new LogFileEntry { FilePath = @"C:\logs\instance2.log" };
        var file10 = new LogFileEntry { FilePath = @"C:\logs\instance10.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(file1);
        await fileRepo.AddAsync(file2);
        await fileRepo.AddAsync(file10);

        var dashboard = CreateGroup("dashboard-1", "Dashboard");
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(dashboard.Model);

        var host = new DashboardWorkspaceHostStub(dashboard);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

        await service.AddFilesToDashboardAsync(
            dashboard,
            new[]
            {
                @"C:\logs\instance10.log",
                @"C:\logs\instance2.log",
                @"C:\logs\instance1.log"
            });

        Assert.Equal(
            new[] { file1.Id, file2.Id, file10.Id },
            dashboard.Model.FileIds);
    }

    [Fact]
    public async Task AddFilesToDashboardAsync_ResortsExistingDashboardFilesAfterAdd()
    {
        var file1 = new LogFileEntry { FilePath = @"C:\logs\instance1.log" };
        var file2 = new LogFileEntry { FilePath = @"C:\logs\instance2.log" };
        var file10 = new LogFileEntry { FilePath = @"C:\logs\instance10.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(file1);
        await fileRepo.AddAsync(file2);
        await fileRepo.AddAsync(file10);

        var dashboard = CreateGroup("dashboard-1", "Dashboard", file10.Id, file1.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(dashboard.Model);

        var host = new DashboardWorkspaceHostStub(dashboard);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

        await service.AddFilesToDashboardAsync(
            dashboard,
            new[]
            {
                @"C:\logs\instance2.log"
            });

        Assert.Equal(
            new[] { file1.Id, file2.Id, file10.Id },
            dashboard.Model.FileIds);
    }

    [Fact]
    public async Task AddFilesToDashboardAsync_ReusesExistingLogFileEntryAcrossDashboards()
    {
        var fileRepo = new StubLogFileRepository();
        var existingEntry = await fileRepo.GetOrCreateByPathAsync(@"C:\logs\shared.log");
        var firstDashboard = CreateGroup("dashboard-1", "First Dashboard");
        var secondDashboard = CreateGroup("dashboard-2", "Second Dashboard");
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(firstDashboard.Model);
        await groupRepo.AddAsync(secondDashboard.Model);

        var host = new DashboardWorkspaceHostStub(firstDashboard, secondDashboard);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

        await service.AddFilesToDashboardAsync(firstDashboard, new[] { existingEntry.FilePath });
        await service.AddFilesToDashboardAsync(secondDashboard, new[] { existingEntry.FilePath });

        Assert.Equal(new[] { existingEntry.Id }, firstDashboard.Model.FileIds);
        Assert.Equal(new[] { existingEntry.Id }, secondDashboard.Model.FileIds);
        Assert.Single(await fileRepo.GetAllAsync());
    }

    [Fact]
    public async Task CopyFileToDashboardAsync_AddsSharedFileIdWithoutRemovingSource()
    {
        var shared = new LogFileEntry { FilePath = @"C:\logs\shared.log" };
        var other = new LogFileEntry { FilePath = @"C:\logs\other.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(shared);
        await fileRepo.AddAsync(other);
        var source = CreateGroup("dashboard-1", "Source", shared.Id);
        var target = CreateGroup("dashboard-2", "Target", other.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(source.Model);
        await groupRepo.AddAsync(target.Model);

        var host = new DashboardWorkspaceHostStub(source, target);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

        await service.CopyFileToDashboardAsync(target, shared.Id);

        Assert.Equal(new[] { shared.Id }, source.Model.FileIds);
        Assert.Equal(new[] { other.Id, shared.Id }, target.Model.FileIds);
    }

    [Fact]
    public async Task CopyFileToDashboardAsync_WhenTargetAlreadyContainsFile_DoesNotPersistOrDuplicate()
    {
        var shared = new LogFileEntry { FilePath = @"C:\logs\shared.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(shared);
        var target = CreateGroup("dashboard-1", "Target", shared.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(target.Model);

        var host = new DashboardWorkspaceHostStub(target);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

        await service.CopyFileToDashboardAsync(target, shared.Id);

        Assert.Equal(new[] { shared.Id }, target.Model.FileIds);
        Assert.Equal(0, groupRepo.UpdateCallCount);
    }

    [Fact]
    public async Task CopyFilePathToDashboardAsync_ReusesExistingCatalogEntryForAdHocPath()
    {
        var fileRepo = new StubLogFileRepository();
        var existingEntry = await fileRepo.GetOrCreateByPathAsync(@"C:\logs\adhoc.log");
        var target = CreateGroup("dashboard-1", "Target");
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(target.Model);

        var host = new DashboardWorkspaceHostStub(target);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

        await service.CopyFilePathToDashboardAsync(target, @"C:\logs\adhoc.log");

        Assert.Equal(new[] { existingEntry.Id }, target.Model.FileIds);
        Assert.Single(await fileRepo.GetAllAsync());
    }

    [Fact]
    public async Task DuplicateGroupAsync_Dashboard_CopiesFileIdsWithUniqueNameAndNewId()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\logs\b.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        await fileRepo.AddAsync(fileB);
        var existingCopy = CreateGroup("dashboard-copy", "Dashboard Copy");
        var dashboard = CreateGroup("dashboard-1", "Dashboard", fileA.Id, fileB.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(dashboard.Model);
        await groupRepo.AddAsync(existingCopy.Model);

        var host = new DashboardWorkspaceHostStub(dashboard, existingCopy);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

        await service.DuplicateGroupAsync(dashboard);

        var persisted = await groupRepo.GetAllAsync();
        var duplicate = persisted.Single(group => group.Name == "Dashboard Copy 2");
        Assert.NotEqual(dashboard.Id, duplicate.Id);
        Assert.Equal(new[] { fileA.Id, fileB.Id }, duplicate.FileIds);
        Assert.Equal(
            new[] { "Dashboard", "Dashboard Copy 2", "Dashboard Copy" },
            host.Groups.Select(group => group.Name).ToArray());
    }

    [Fact]
    public async Task DuplicateGroupAsync_Folder_CopiesSubtreeWithNewIdsAndPreservedMembership()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        var folder = CreateGroup("folder-1", "Folder", LogGroupKind.Branch);
        var dashboard = CreateGroup("dashboard-1", "Dashboard", LogGroupKind.Dashboard, fileA.Id);
        dashboard.Model.ParentGroupId = folder.Id;
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(folder.Model);
        await groupRepo.AddAsync(dashboard.Model);

        var host = new DashboardWorkspaceHostStub(folder, dashboard);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

        await service.DuplicateGroupAsync(folder);

        var persisted = await groupRepo.GetAllAsync();
        var copiedFolder = persisted.Single(group => group.Name == "Folder Copy");
        var copiedDashboard = persisted.Single(group =>
            group.Name == "Dashboard" &&
            string.Equals(group.ParentGroupId, copiedFolder.Id, StringComparison.Ordinal));
        Assert.NotEqual(folder.Id, copiedFolder.Id);
        Assert.NotEqual(dashboard.Id, copiedDashboard.Id);
        Assert.Equal(new[] { fileA.Id }, copiedDashboard.FileIds);
    }

    [Fact]
    public async Task DuplicateGroupAsync_WhenStoredTopologyCycles_SkipsRepeatedSourceIds()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        var folder = CreateGroup("folder-1", "Folder", LogGroupKind.Branch);
        var dashboard = CreateGroup("dashboard-1", "Dashboard", LogGroupKind.Dashboard, fileA.Id);
        dashboard.Model.ParentGroupId = folder.Id;
        var loopBack = CreateGroup("folder-1", "Folder Loop", LogGroupKind.Branch);
        loopBack.Model.ParentGroupId = dashboard.Id;
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(folder.Model);
        await groupRepo.AddAsync(dashboard.Model);
        await groupRepo.AddAsync(loopBack.Model);

        var host = new DashboardWorkspaceHostStub(folder, dashboard);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

        await service.DuplicateGroupAsync(folder).WaitAsync(TimeSpan.FromSeconds(2));

        var persisted = await groupRepo.GetAllAsync();
        var copiedFolder = persisted.Single(group => group.Name == "Folder Copy");
        var copiedDashboard = persisted.Single(group =>
            group.Name == "Dashboard" &&
            string.Equals(group.ParentGroupId, copiedFolder.Id, StringComparison.Ordinal));
        Assert.Equal(new[] { fileA.Id }, copiedDashboard.FileIds);
        Assert.DoesNotContain(persisted, group =>
            group.Name == "Folder Loop" &&
            string.Equals(group.ParentGroupId, copiedDashboard.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshAllMemberFilesAsync_UsesReferencedFileIdsWithoutLoadingFullRepository()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\logs\b.log" };
        var fileUnused = new LogFileEntry { FilePath = @"C:\logs\unused.log" };
        var fileRepo = new TrackingLogFileRepository(fileA, fileB, fileUnused);
        var dashboard = CreateGroup("dashboard-1", "Dashboard", fileA.Id, fileB.Id);
        var groupRepo = new RecordingLogGroupRepository();
        await groupRepo.AddAsync(dashboard.Model);

        var host = new DashboardWorkspaceHostStub(dashboard);
        var service = new DashboardActivationService(host, fileRepo, groupRepo);

        await service.RefreshAllMemberFilesAsync();

        Assert.Equal(0, fileRepo.GetAllCallCount);
        Assert.Equal(
            new[] { fileA.Id, fileB.Id }.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            fileRepo.LastRequestedIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { fileA.Id, fileB.Id },
            dashboard.MemberFiles.Select(member => member.FileId).ToArray());
    }

    [Fact]
    public async Task PartialRefresh_WhenEarlierRefreshResumesAfterLaterSelectionChange_DoesNotRestoreStaleSelection()
    {
        var fileA = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var fileB = new LogFileEntry { FilePath = @"C:\logs\b.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(fileA);
        await fileRepo.AddAsync(fileB);

        var dashboard = CreateGroup("dashboard-1", "Dashboard", fileA.Id, fileB.Id);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var existenceMapBuilder = new BlockingExistenceMapBuilder();
        var service = new DashboardActivationService(host, fileRepo, new StubLogGroupRepository(), existenceMapBuilder.InvokeAsync);

        var tabA = CreateTab(fileA.Id, fileA.FilePath, dashboard.Id);
        var tabB = CreateTab(fileB.Id, fileB.FilePath, dashboard.Id);
        host.Tabs.Add(tabA);
        host.Tabs.Add(tabB);
        host.SelectedTab = tabA;

        await service.RefreshAllMemberFilesAsync();
        service.UpdateSelectedMemberFileHighlights();
        existenceMapBuilder.EnableBlocking();

        var changedFilePaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [fileB.Id] = fileB.FilePath
        };

        var firstRefreshTask = service.RefreshMemberFilesForFileIdsAsync(changedFilePaths);
        await existenceMapBuilder.WaitForBlockedCallAsync();

        host.SelectedTab = tabB;
        service.UpdateSelectedMemberFileHighlights();

        await service.RefreshMemberFilesForFileIdsAsync(changedFilePaths);
        Assert.True(dashboard.MemberFiles.Single(member => member.FileId == fileB.Id).IsSelected);

        existenceMapBuilder.ReleaseBlockedCall();
        await firstRefreshTask;

        var memberFile = dashboard.MemberFiles.Single(member => member.FileId == fileB.Id);
        Assert.True(memberFile.IsSelected);
        Assert.False(memberFile.HasError);
    }

    [Fact]
    public async Task PartialRefresh_WhenEarlierOpenTabRefreshResumesAfterCloseRefresh_DoesNotRestoreClosedMember()
    {
        var file = new LogFileEntry { FilePath = @"C:\logs\member.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(file);

        var dashboard = CreateGroup("dashboard-1", "Dashboard", file.Id);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var existenceMapBuilder = new BlockingExistenceMapBuilder();
        var service = new DashboardActivationService(host, fileRepo, new StubLogGroupRepository(), existenceMapBuilder.InvokeAsync);

        var tab = CreateTab(file.Id, file.FilePath, dashboard.Id);
        host.Tabs.Add(tab);
        host.SelectedTab = tab;

        await service.RefreshAllMemberFilesAsync();
        service.UpdateSelectedMemberFileHighlights();
        existenceMapBuilder.EnableBlocking();

        var changedFilePaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [file.Id] = file.FilePath
        };

        var firstRefreshTask = service.RefreshMemberFilesForFileIdsAsync(changedFilePaths);
        await existenceMapBuilder.WaitForBlockedCallAsync();

        host.Tabs.Remove(tab);
        host.SelectedTab = null;
        service.UpdateSelectedMemberFileHighlights();

        await service.RefreshMemberFilesForFileIdsAsync(changedFilePaths);
        var memberFile = Assert.Single(dashboard.MemberFiles);
        Assert.True(memberFile.HasError);
        Assert.False(memberFile.IsSelected);

        existenceMapBuilder.ReleaseBlockedCall();
        await firstRefreshTask;

        memberFile = Assert.Single(dashboard.MemberFiles);
        Assert.True(memberFile.HasError);
        Assert.False(memberFile.IsSelected);
    }

    [Fact]
    public async Task ApplyImportedViewAsync_WhenReplaceFails_KeepsPersistedGroups()
    {
        var existingEntry = new LogFileEntry { FilePath = @"C:\logs\kept.log" };
        var fileRepo = new StubLogFileRepository();
        await fileRepo.AddAsync(existingEntry);

        var currentDashboard = CreateGroup("dashboard-1", "Current Dashboard", existingEntry.Id);
        var groupRepo = new RecordingLogGroupRepository
        {
            OnReplaceAllAsync = _ => throw new IOException("replace failed")
        };
        await groupRepo.AddAsync(currentDashboard.Model);

        var host = new DashboardWorkspaceHostStub(currentDashboard);
        var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

        await Assert.ThrowsAsync<IOException>(() => service.ApplyImportedViewAsync(new ViewExport
        {
            Groups = new List<ViewExportGroup>
            {
                new()
                {
                    Id = "imported-dashboard",
                    Name = "Imported Dashboard",
                    Kind = LogGroupKind.Dashboard,
                    SortOrder = 0,
                    FilePaths = new List<string> { @"C:\logs\new.log" }
                }
            }
        }));

        var persistedGroups = await groupRepo.GetAllAsync();
        Assert.Equal(new[] { "Current Dashboard" }, persistedGroups.Select(group => group.Name).ToArray());
        Assert.Equal(new[] { "Current Dashboard" }, host.Groups.Select(group => group.Name).ToArray());
    }

    [Fact]
    public async Task ImportAndApplyImportedViewAsync_UsesStoredCopyAfterOriginalFileIsRemoved()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "LogReaderDashboardImportStorage_" + Guid.NewGuid().ToString("N")[..8]);
        var sourceRoot = Path.Combine(Path.GetTempPath(), "LogReaderDashboardImportSource_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(storageRoot);
        Directory.CreateDirectory(sourceRoot);

        using var appPathsScope = AppPaths.BeginTestScope(rootPath: storageRoot);

        try
        {
            var sourcePath = Path.Combine(sourceRoot, "incoming-view.json");
            await File.WriteAllTextAsync(
                sourcePath,
                JsonSerializer.Serialize(new ViewExport
                {
                    Groups = new List<ViewExportGroup>
                    {
                        new()
                        {
                            Id = "dashboard-1",
                            Name = "Imported Dashboard",
                            Kind = LogGroupKind.Dashboard,
                            SortOrder = 0
                        }
                    }
                }));

            var fileRepo = new StubLogFileRepository();
            var groupRepo = new RecordingLogGroupRepository
            {
                ImportResult = new ViewExport
                {
                    Groups = new List<ViewExportGroup>
                    {
                        new()
                        {
                            Id = "dashboard-1",
                            Name = "Imported Dashboard",
                            Kind = LogGroupKind.Dashboard,
                            SortOrder = 0
                        }
                    }
                }
            };
            var host = new DashboardWorkspaceHostStub();
            var service = new DashboardWorkspaceService(host, fileRepo, groupRepo);

            var importedView = await service.ImportViewAsync(sourcePath);
            Assert.NotNull(importedView);

            var storedPath = Path.Combine(AppPaths.ViewsDirectory, Path.GetFileName(sourcePath));
            var tempPath = storedPath + ".importing";
            Assert.False(File.Exists(storedPath));
            Assert.True(File.Exists(tempPath));

            File.Delete(sourcePath);

            await service.ApplyImportedViewAsync(importedView!);

            Assert.True(File.Exists(storedPath));
            Assert.False(File.Exists(tempPath));
            Assert.False(File.Exists(sourcePath));

            var persistedGroups = await groupRepo.GetAllAsync();
            Assert.Single(persistedGroups);
            Assert.Equal("Imported Dashboard", persistedGroups[0].Name);
            Assert.Equal(storedPath, groupRepo.LastImportPath);
            Assert.Equal(new[] { "Imported Dashboard" }, host.Groups.Select(group => group.Name).ToArray());
        }
        finally
        {
            if (Directory.Exists(sourceRoot))
                Directory.Delete(sourceRoot, recursive: true);

            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ImportViewAsync_WhenRepositoryReturnsNull_DeletesImportingFileAndKeepsStoredView()
    {
        await RunFailedImportCleanupTestAsync(
            new RecordingLogGroupRepository
            {
                ImportResult = null
            },
            ex => Assert.Contains("could not be read", ex.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportViewAsync_WhenRepositoryThrowsInvalidData_DeletesImportingFileAndKeepsStoredView()
    {
        await RunFailedImportCleanupTestAsync(
            new RecordingLogGroupRepository
            {
                OnImportViewAsync = _ => throw new InvalidDataException("invalid import")
            },
            ex => Assert.Equal("invalid import", ex.Message));
    }

    [Fact]
    public async Task ImportViewAsync_WhenImportedTopologyIsInvalid_DeletesImportingFileAndKeepsStoredView()
    {
        await RunFailedImportCleanupTestAsync(
            new RecordingLogGroupRepository
            {
                ImportResult = new ViewExport
                {
                    Groups = new List<ViewExportGroup>
                    {
                        new()
                        {
                            Id = "branch-1",
                            Name = "Invalid Folder",
                            Kind = LogGroupKind.Branch,
                            SortOrder = 0,
                            FilePaths = new List<string> { @"C:\logs\should-not-import.log" }
                        }
                    }
                }
            },
            ex => Assert.Contains("cannot own file paths", ex.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RebuildGroupsCollection_WhileFilterActive_DiscardsCapturedExpansionSnapshot()
    {
        var host = new DashboardWorkspaceHostStub();
        var service = new DashboardWorkspaceService(host, new StubLogFileRepository(), new RecordingLogGroupRepository());
        var groups = new List<LogGroup>
        {
            new()
            {
                Id = "folder-1",
                Name = "Payments",
                Kind = LogGroupKind.Branch,
                SortOrder = 0
            },
            new()
            {
                Id = "dashboard-1",
                Name = "Payroll",
                Kind = LogGroupKind.Dashboard,
                ParentGroupId = "folder-1",
                SortOrder = 0
            }
        };

        service.RebuildGroupsCollection(groups);
        var folder = host.Groups.Single(group => group.Id == "folder-1");
        folder.IsExpanded = false;

        host.DashboardTreeFilter = "pay";
        service.ApplyDashboardTreeFilter();
        Assert.True(folder.IsExpanded);

        service.RebuildGroupsCollection(groups);
        folder = host.Groups.Single(group => group.Id == "folder-1");
        Assert.True(folder.IsExpanded);

        host.DashboardTreeFilter = string.Empty;
        service.ApplyDashboardTreeFilter();

        folder = host.Groups.Single(group => group.Id == "folder-1");
        Assert.True(folder.IsExpanded);
    }

    [Fact]
    public void RebuildGroupsCollection_NestedGroups_PreservesFlattenedSortOrderAndDepth()
    {
        var host = new DashboardWorkspaceHostStub();
        var service = new DashboardWorkspaceService(host, new StubLogFileRepository(), new RecordingLogGroupRepository());
        var groups = new List<LogGroup>
        {
            new()
            {
                Id = "dashboard-root",
                Name = "Root Dashboard",
                Kind = LogGroupKind.Dashboard,
                SortOrder = 1
            },
            new()
            {
                Id = "dashboard-child-b",
                Name = "Child B",
                Kind = LogGroupKind.Dashboard,
                ParentGroupId = "folder-root",
                SortOrder = 1
            },
            new()
            {
                Id = "folder-root",
                Name = "Root Folder",
                Kind = LogGroupKind.Branch,
                SortOrder = 0
            },
            new()
            {
                Id = "dashboard-grandchild",
                Name = "Grandchild",
                Kind = LogGroupKind.Dashboard,
                ParentGroupId = "folder-child",
                SortOrder = 0
            },
            new()
            {
                Id = "folder-child",
                Name = "Child Folder",
                Kind = LogGroupKind.Branch,
                ParentGroupId = "folder-root",
                SortOrder = 0
            }
        };

        service.RebuildGroupsCollection(groups);

        Assert.Equal(
            new[] { "folder-root", "folder-child", "dashboard-grandchild", "dashboard-child-b", "dashboard-root" },
            host.Groups.Select(group => group.Id).ToArray());
        Assert.Equal(
            new[] { 0, 1, 2, 1, 0 },
            host.Groups.Select(group => group.Depth).ToArray());
    }

    private static LogGroupViewModel CreateGroup(string id, string name, params string[] fileIds)
        => CreateGroup(id, name, LogGroupKind.Dashboard, fileIds);

    private static LogGroupViewModel CreateGroup(string id, string name, LogGroupKind kind, params string[] fileIds)
    {
        return new LogGroupViewModel(
            new LogGroup
            {
                Id = id,
                Name = name,
                Kind = kind,
                FileIds = fileIds.ToList()
            },
            _ => Task.CompletedTask);
    }

    private static LogTabViewModel CreateTab(string fileId, string filePath, string? scopeDashboardId = null)
    {
        return new LogTabViewModel(
            fileId,
            filePath,
            new StubLogReaderService(),
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings(),
            skipInitialEncodingResolution: true,
            sessionRegistry: null,
            initialEncoding: FileEncoding.Auto,
            scopeDashboardId: scopeDashboardId);
    }

    private static async Task RunFailedImportCleanupTestAsync(
        RecordingLogGroupRepository groupRepo,
        Action<InvalidDataException> assertException)
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "LogReaderDashboardImportStorage_" + Guid.NewGuid().ToString("N")[..8]);
        var sourceRoot = Path.Combine(Path.GetTempPath(), "LogReaderDashboardImportSource_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(storageRoot);
        Directory.CreateDirectory(sourceRoot);

        using var appPathsScope = AppPaths.BeginTestScope(rootPath: storageRoot);

        try
        {
            var currentDashboard = CreateGroup("dashboard-1", "Current Dashboard");
            await groupRepo.AddAsync(currentDashboard.Model);

            var sourcePath = Path.Combine(sourceRoot, "incoming-view.json");
            await File.WriteAllTextAsync(sourcePath, "{}");
            var storedPath = Path.Combine(AppPaths.ViewsDirectory, Path.GetFileName(sourcePath));
            AppPaths.EnsureDirectory(AppPaths.ViewsDirectory);
            await File.WriteAllTextAsync(storedPath, "existing stored view");

            var host = new DashboardWorkspaceHostStub(currentDashboard);
            var service = new DashboardWorkspaceService(host, new StubLogFileRepository(), groupRepo);

            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportViewAsync(sourcePath));
            assertException(ex);

            Assert.Equal("existing stored view", await File.ReadAllTextAsync(storedPath));
            Assert.False(File.Exists(storedPath + ".importing"));
            Assert.Equal(storedPath + ".importing", groupRepo.LastImportPath);

            var persistedGroups = await groupRepo.GetAllAsync();
            var persistedGroup = Assert.Single(persistedGroups);
            Assert.Equal("Current Dashboard", persistedGroup.Name);
            Assert.Equal(new[] { "Current Dashboard" }, host.Groups.Select(group => group.Name).ToArray());
        }
        finally
        {
            if (Directory.Exists(sourceRoot))
                Directory.Delete(sourceRoot, recursive: true);

            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, recursive: true);
        }
    }

    private sealed class DashboardWorkspaceHostStub : IDashboardWorkspaceHost
    {
        public DashboardWorkspaceHostStub(params LogGroupViewModel[] groups)
        {
            foreach (var group in groups)
                Groups.Add(group);
        }

        public ObservableCollection<LogGroupViewModel> Groups { get; } = new();

        public ObservableCollection<LogTabViewModel> Tabs { get; } = new();

        public LogTabViewModel? SelectedTab { get; set; }

        public bool ShowFullPathsInDashboard { get; set; }

        public string? ActiveDashboardId { get; set; }

        public string DashboardTreeFilter { get; set; } = string.Empty;

        public bool IsDashboardLoading { get; set; }

        public string DashboardLoadingStatusText { get; set; } = string.Empty;

        public int DashboardLoadDepth { get; set; }

        public void NotifyFilteredTabsChanged()
        {
        }

        public void NotifyScopeMetadataChanged()
        {
        }

        public void EnsureSelectedTabInCurrentScope()
        {
        }

        public void ExitDashboardScopeIfCurrentDashboardFinishedEmpty(string dashboardId)
        {
        }

        public void BeginTabCollectionNotificationSuppression()
        {
        }

        public void EndTabCollectionNotificationSuppression()
        {
        }

        public Task OpenFilePathInScopeAsync(
            string filePath,
            string? scopeDashboardId,
            bool reloadIfLoadError = false,
            bool activateTab = true,
            bool deferVisibilityRefresh = false,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<TabWorkspaceService.PreparedTabOpen?> PrepareDashboardFileOpenAsync(
            string filePath,
            string scopeDashboardId,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task FinalizeDashboardFileOpenAsync(
            TabWorkspaceService.PreparedTabOpen preparedTab,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public LogTabViewModel? FindTabInScope(string filePath, string? scopeDashboardId)
        {
            return Tabs.FirstOrDefault(tab =>
                string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal));
        }
    }

    private sealed class RecordingLogGroupRepository : ILogGroupRepository
    {
        private readonly List<LogGroup> _groups = new();

        public ViewExport? ImportResult { get; set; }
        public string? LastImportPath { get; private set; }
        public int UpdateCallCount { get; private set; }

        public Func<string, Task<ViewExport?>>? OnImportViewAsync { get; set; }

        public Func<IReadOnlyList<LogGroup>, Task>? OnReplaceAllAsync { get; set; }

        public Task<List<LogGroup>> GetAllAsync() => Task.FromResult(_groups.ToList());

        public Task<LogGroup?> GetByIdAsync(string id)
            => Task.FromResult(_groups.FirstOrDefault(group => group.Id == id));

        public Task AddAsync(LogGroup group)
        {
            _groups.Add(Clone(group));
            return Task.CompletedTask;
        }

        public Task ReplaceAllAsync(IReadOnlyList<LogGroup> groups)
        {
            if (OnReplaceAllAsync != null)
                return OnReplaceAllAsync(groups);

            _groups.Clear();
            _groups.AddRange(groups.Select(Clone));
            return Task.CompletedTask;
        }

        public Task UpdateAsync(LogGroup group)
        {
            UpdateCallCount++;
            var index = _groups.FindIndex(existing => existing.Id == group.Id);
            if (index >= 0)
                _groups[index] = Clone(group);

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id)
        {
            _groups.RemoveAll(group => group.Id == id);
            return Task.CompletedTask;
        }

        public Task ReorderAsync(List<string> orderedIds) => Task.CompletedTask;

        public Task ExportViewAsync(string exportPath) => Task.CompletedTask;

        public Task<ViewExport?> ImportViewAsync(string importPath)
        {
            LastImportPath = importPath;
            if (OnImportViewAsync != null)
                return OnImportViewAsync(importPath);

            return Task.FromResult(ImportResult);
        }

        private static LogGroup Clone(LogGroup group)
        {
            return new LogGroup
            {
                Id = group.Id,
                Name = group.Name,
                SortOrder = group.SortOrder,
                ParentGroupId = group.ParentGroupId,
                Kind = group.Kind,
                FileIds = group.FileIds.ToList()
            };
        }
    }

    private sealed class BlockingExistenceMapBuilder
    {
        private readonly TaskCompletionSource<bool> _blockedCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseBlockedCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public bool BlockingEnabled { get; private set; }

        public void EnableBlocking() => BlockingEnabled = true;

        public Task WaitForBlockedCallAsync() => _blockedCallStarted.Task;

        public void ReleaseBlockedCall() => _releaseBlockedCall.TrySetResult(true);

        public async Task<Dictionary<string, bool>> InvokeAsync(IReadOnlyDictionary<string, string> fileIdToPath)
        {
            if (BlockingEnabled && Interlocked.Increment(ref _callCount) == 1)
            {
                _blockedCallStarted.TrySetResult(true);
                await _releaseBlockedCall.Task;
            }

            return fileIdToPath.ToDictionary(
                kvp => kvp.Key,
                _ => false,
                StringComparer.Ordinal);
        }
    }

    private sealed class TrackingLogFileRepository : ILogFileRepository
    {
        private readonly List<LogFileEntry> _entries;

        public TrackingLogFileRepository(params LogFileEntry[] entries)
        {
            _entries = entries.ToList();
        }

        public int GetAllCallCount { get; private set; }

        public IReadOnlyList<string> LastRequestedIds { get; private set; } = Array.Empty<string>();

        public Task<List<LogFileEntry>> GetAllAsync()
        {
            GetAllCallCount++;
            return Task.FromResult(_entries.ToList());
        }

        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids)
        {
            var requestedIds = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            LastRequestedIds = requestedIds;

            var requestedIdSet = requestedIds.ToHashSet(StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(
                _entries
                    .Where(entry => requestedIdSet.Contains(entry.Id))
                    .ToDictionary(entry => entry.Id, StringComparer.Ordinal));
        }

        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByPathsAsync(IEnumerable<string> filePaths)
        {
            var pathSet = filePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(
                _entries
                    .Where(entry => pathSet.Contains(entry.FilePath))
                    .ToDictionary(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase));
        }

        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetOrCreateByPathsAsync(IEnumerable<string> filePaths)
            => throw new NotSupportedException();

        public Task<LogFileEntry> GetOrCreateByPathAsync(string filePath, DateTime? lastOpenedAtUtc = null)
            => throw new NotSupportedException();

        public Task AddAsync(LogFileEntry entry)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(LogFileEntry entry) => Task.CompletedTask;

        public Task DeleteAsync(string id)
        {
            _entries.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            return Task.CompletedTask;
        }
    }
}
