using System.Collections.ObjectModel;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;
using LogReader.Testing;

namespace LogReader.Tests;

public class DashboardWorkspaceServiceTests
{
    [Fact]
    public void ParseBulkFilePaths_TrimsStripsQuotesIgnoresBlankLinesAndDeduplicatesExactPaths()
    {
        var paths = DashboardWorkspaceService.ParseBulkFilePaths(
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
    public void BuildBulkFilePreview_ReportsFoundAndMissingPaths()
    {
        var preview = DashboardWorkspaceService.BuildBulkFilePreview(
            string.Join(
                Environment.NewLine,
                @"C:\logs\app.log",
                @"C:\logs\missing.log"),
            path => string.Equals(path, @"C:\logs\app.log", StringComparison.Ordinal));

        Assert.Equal(2, preview.ParsedPaths.Count);
        Assert.Equal(1, preview.FoundCount);
        Assert.Equal(1, preview.MissingCount);
        Assert.Collection(
            preview.Items,
            item =>
            {
                Assert.Equal(@"C:\logs\app.log", item.FilePath);
                Assert.True(item.IsFound);
            },
            item =>
            {
                Assert.Equal(@"C:\logs\missing.log", item.FilePath);
                Assert.False(item.IsFound);
            });
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

        await service.RefreshAllMemberFilesAsync();
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
        var service = new DashboardWorkspaceService(host, fileRepo, new StubLogGroupRepository(), existenceMapBuilder.InvokeAsync);

        var tabA = CreateTab(fileA.Id, fileA.FilePath);
        var tabB = CreateTab(fileB.Id, fileB.FilePath);
        host.Tabs.Add(tabA);
        host.Tabs.Add(tabB);
        host.SelectedTab = tabA;

        await service.RefreshAllMemberFilesAsync();
        service.UpdateSelectedMemberFileHighlights(fileA.Id);
        existenceMapBuilder.EnableBlocking();

        var changedFilePaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [fileB.Id] = fileB.FilePath
        };

        var firstRefreshTask = service.RefreshMemberFilesForFileIdsAsync(changedFilePaths);
        await existenceMapBuilder.WaitForBlockedCallAsync();

        host.SelectedTab = tabB;
        service.UpdateSelectedMemberFileHighlights(fileB.Id);

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
        var service = new DashboardWorkspaceService(host, fileRepo, new StubLogGroupRepository(), existenceMapBuilder.InvokeAsync);

        var tab = CreateTab(file.Id, file.FilePath);
        host.Tabs.Add(tab);
        host.SelectedTab = tab;

        await service.RefreshAllMemberFilesAsync();
        service.UpdateSelectedMemberFileHighlights(file.Id);
        existenceMapBuilder.EnableBlocking();

        var changedFilePaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [file.Id] = file.FilePath
        };

        var firstRefreshTask = service.RefreshMemberFilesForFileIdsAsync(changedFilePaths);
        await existenceMapBuilder.WaitForBlockedCallAsync();

        host.Tabs.Remove(tab);
        host.SelectedTab = null;
        service.UpdateSelectedMemberFileHighlights(null);

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

    private static LogGroupViewModel CreateGroup(string id, string name, params string[] fileIds)
    {
        return new LogGroupViewModel(
            new LogGroup
            {
                Id = id,
                Name = name,
                Kind = LogGroupKind.Dashboard,
                FileIds = fileIds.ToList()
            },
            _ => Task.CompletedTask);
    }

    private static LogTabViewModel CreateTab(string fileId, string filePath)
    {
        return new LogTabViewModel(
            fileId,
            filePath,
            new StubLogReaderService(),
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings(),
            skipInitialEncodingResolution: true);
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

        public void BeginTabCollectionNotificationSuppression()
        {
        }

        public void EndTabCollectionNotificationSuppression()
        {
        }

        public Task OpenFilePathAsync(
            string filePath,
            bool reloadIfLoadError = false,
            bool activateTab = true,
            bool deferVisibilityRefresh = false,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingLogGroupRepository : ILogGroupRepository
    {
        private readonly List<LogGroup> _groups = new();

        public int UpdateCallCount { get; private set; }

        public Task<List<LogGroup>> GetAllAsync() => Task.FromResult(_groups.ToList());

        public Task<LogGroup?> GetByIdAsync(string id)
            => Task.FromResult(_groups.FirstOrDefault(group => group.Id == id));

        public Task AddAsync(LogGroup group)
        {
            _groups.Add(Clone(group));
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

        public Task<ViewExport?> ImportViewAsync(string importPath) => Task.FromResult<ViewExport?>(null);

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
}
