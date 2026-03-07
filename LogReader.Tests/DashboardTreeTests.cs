using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

namespace LogReader.Tests;

public class DashboardTreeTests
{
    private class StubLogFileRepository : ILogFileRepository
    {
        private readonly List<LogFileEntry> _entries = new();

        public Task<List<LogFileEntry>> GetAllAsync() => Task.FromResult(_entries.ToList());
        public Task<LogFileEntry?> GetByIdAsync(string id)
            => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));
        public Task<LogFileEntry?> GetByPathAsync(string filePath)
            => Task.FromResult(_entries.FirstOrDefault(e =>
                string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase)));
        public Task AddAsync(LogFileEntry entry) { _entries.Add(entry); return Task.CompletedTask; }
        public Task UpdateAsync(LogFileEntry entry) => Task.CompletedTask;
        public Task DeleteAsync(string id) { _entries.RemoveAll(e => e.Id == id); return Task.CompletedTask; }
    }

    private class StubLogGroupRepository : ILogGroupRepository
    {
        private readonly List<LogGroup> _groups = new();

        public Task<List<LogGroup>> GetAllAsync()
            => Task.FromResult(_groups.OrderBy(g => g.SortOrder).ToList());
        public Task<LogGroup?> GetByIdAsync(string id)
            => Task.FromResult(_groups.FirstOrDefault(g => g.Id == id));
        public Task AddAsync(LogGroup group) { _groups.Add(group); return Task.CompletedTask; }
        public Task UpdateAsync(LogGroup group)
        {
            var idx = _groups.FindIndex(g => g.Id == group.Id);
            if (idx >= 0) _groups[idx] = group;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string id)
        {
            var toRemove = CollectDescendantIds(id);
            toRemove.Add(id);
            _groups.RemoveAll(g => toRemove.Contains(g.Id));
            return Task.CompletedTask;
        }
        public Task ReorderAsync(List<string> orderedIds) => Task.CompletedTask;
        public Task ExportGroupAsync(string groupId, string exportPath) => Task.CompletedTask;
        public Task<GroupExport?> ImportGroupAsync(string importPath) => Task.FromResult<GroupExport?>(null);

        private HashSet<string> CollectDescendantIds(string parentId)
        {
            var result = new HashSet<string>();
            foreach (var child in _groups.Where(g => g.ParentGroupId == parentId))
            {
                result.Add(child.Id);
                result.UnionWith(CollectDescendantIds(child.Id));
            }
            return result;
        }
    }

    private class StubSessionRepository : ISessionRepository
    {
        public Task<SessionState> LoadAsync() => Task.FromResult(new SessionState());
        public Task SaveAsync(SessionState state) => Task.CompletedTask;
    }

    private class StubSettingsRepository : ISettingsRepository
    {
        public Task<AppSettings> LoadAsync() => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings) => Task.CompletedTask;
    }

    private class StubSearchService : ISearchService
    {
        public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(new SearchResult { FilePath = filePath });
        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
            => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    }

    private MainViewModel CreateViewModel(
        ILogFileRepository? fileRepo = null,
        ILogGroupRepository? groupRepo = null)
    {
        return new MainViewModel(
            fileRepo ?? new StubLogFileRepository(),
            groupRepo ?? new StubLogGroupRepository(),
            new StubSessionRepository(),
            new StubSettingsRepository(),
            new StubLogReaderService(),
            new StubSearchService(),
            new StubFileTailService(),
            enableLifecycleTimer: false);
    }

    [Fact]
    public async Task CreateDashboard_DefaultsToDashboardKind()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.CreateGroupCommand.ExecuteAsync(null);

        Assert.Single(vm.Groups);
        Assert.Equal(LogGroupKind.Dashboard, vm.Groups[0].Kind);
        Assert.False(vm.Groups[0].CanAddChild);
        Assert.True(vm.Groups[0].CanManageFiles);
    }

    [Fact]
    public async Task CreateBranchCommand_CreatesBranchKind()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.CreateContainerGroupCommand.ExecuteAsync(null);

        Assert.Single(vm.Groups);
        Assert.Equal(LogGroupKind.Branch, vm.Groups[0].Kind);
        Assert.True(vm.Groups[0].CanAddChild);
        Assert.False(vm.Groups[0].CanManageFiles);
    }

    [Fact]
    public async Task Branch_CanCreateDashboardChild()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var branch = vm.Groups[0];

        var result = await vm.CreateChildGroupAsync(branch);

        Assert.True(result);
        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal(LogGroupKind.Dashboard, vm.Groups.First(g => g.Depth == 1).Kind);
    }

    [Fact]
    public async Task Branch_CanCreateFolderChild()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var branch = vm.Groups[0];

        var result = await vm.CreateChildGroupAsync(branch, LogGroupKind.Branch);

        Assert.True(result);
        Assert.Equal(LogGroupKind.Branch, vm.Groups.First(g => g.Depth == 1).Kind);
    }

    [Fact]
    public async Task CreateChild_PreservesExpandedStateOfOtherBranches()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var left = vm.Groups[0];
        var right = vm.Groups[1];

        left.IsExpanded = true;
        right.IsExpanded = false;

        await vm.CreateChildGroupAsync(left, LogGroupKind.Branch);

        left = vm.Groups.First(g => g.Id == left.Id);
        right = vm.Groups.First(g => g.Id == right.Id);
        Assert.True(left.IsExpanded);
        Assert.False(right.IsExpanded);
    }

    [Fact]
    public async Task Dashboard_CannotCreateChild()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = vm.Groups[0];
        var result = await vm.CreateChildGroupAsync(dashboard);

        Assert.False(result);
        Assert.Single(vm.Groups);
    }

    [Fact]
    public async Task ExpandAndCollapseAllFolders_UpdatesExpansionState()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var branch = vm.Groups[0];
        await vm.CreateChildGroupAsync(branch, LogGroupKind.Branch);

        var folders = vm.Groups.Where(g => g.Kind == LogGroupKind.Branch).ToList();
        vm.CollapseAllFoldersCommand.Execute(null);
        Assert.All(folders, f => Assert.False(f.IsExpanded));

        vm.ExpandAllFoldersCommand.Execute(null);
        Assert.All(folders, f => Assert.True(f.IsExpanded));
    }

    [Fact]
    public async Task DashboardTreeFilter_ShowsMatchingPath()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var root = vm.Groups[0];
        root.Name = "Prod";

        await vm.CreateChildGroupAsync(root, LogGroupKind.Dashboard);
        var dashboard = vm.Groups.First(g => g.Depth == 1);
        dashboard.Name = "Payments";

        vm.DashboardTreeFilter = "pay";

        Assert.True(root.IsFilterVisible);
        Assert.True(dashboard.IsFilterVisible);
        Assert.True(root.IsExpanded);
    }

    [Fact]
    public async Task FilteredTabs_NoActiveDashboard_ReturnsOnlyAdHocTabs()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = vm.Groups[0];
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);

        var filtered = vm.FilteredTabs.ToList();
        Assert.Single(filtered);
        Assert.Equal(@"C:\test\b.log", filtered[0].FilePath);
    }

    [Fact]
    public async Task FilteredTabs_UsesOnlyActiveDashboard()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var d1 = vm.Groups[0];
        var d2 = vm.Groups[1];
        d1.Model.FileIds.Add(vm.Tabs[0].FileId);
        d2.Model.FileIds.Add(vm.Tabs[1].FileId);

        vm.ToggleGroupSelection(d1);

        var filtered = vm.FilteredTabs.ToList();
        Assert.Single(filtered);
        Assert.Equal(@"C:\test\a.log", filtered[0].FilePath);
    }

    [Fact]
    public async Task SelectBranch_DoesNotActivateDashboard()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);

        var branch = vm.Groups[0];
        vm.ToggleGroupSelection(branch);

        Assert.Null(vm.ActiveDashboardId);
        Assert.False(branch.IsSelected);
    }

    [Fact]
    public async Task SelectDashboard_SecondClickClearsActiveDashboard()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = vm.Groups[0];
        vm.ToggleGroupSelection(dashboard);
        Assert.Equal(dashboard.Id, vm.ActiveDashboardId);

        vm.ToggleGroupSelection(dashboard);
        Assert.Null(vm.ActiveDashboardId);
    }

    [Fact]
    public async Task SelectingAnotherDashboard_SwitchesActiveDashboard()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var d1 = vm.Groups[0];
        var d2 = vm.Groups[1];

        vm.ToggleGroupSelection(d1);
        vm.ToggleGroupSelection(d2);

        Assert.False(d1.IsSelected);
        Assert.True(d2.IsSelected);
        Assert.Equal(d2.Id, vm.ActiveDashboardId);
    }

    [Fact]
    public async Task DeleteBranch_ClearsActiveDashboardInSubtree()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var branch = vm.Groups[0];
        await vm.CreateChildGroupAsync(branch);

        branch = vm.Groups[0];
        var childDashboard = vm.Groups.First(g => g.Depth == 1);
        vm.ToggleGroupSelection(childDashboard);
        Assert.Equal(childDashboard.Id, vm.ActiveDashboardId);

        await vm.DeleteGroupCommand.ExecuteAsync(branch);

        Assert.Empty(vm.Groups);
        Assert.Null(vm.ActiveDashboardId);
    }

    [Fact]
    public async Task ResolveFileIds_Branch_ResolvesDescendantDashboardFiles()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var branch = vm.Groups[0];
        await vm.CreateChildGroupAsync(branch, LogGroupKind.Dashboard);
        await vm.CreateChildGroupAsync(branch, LogGroupKind.Dashboard);

        var children = vm.Groups.Where(g => g.Depth == 1).ToList();
        children[0].Model.FileIds.Add("file-a");
        children[1].Model.FileIds.Add("file-b");

        var resolved = vm.ResolveFileIds(vm.Groups[0]);

        Assert.Equal(new HashSet<string> { "file-a", "file-b" }, resolved);
    }

    [Fact]
    public async Task CreateChild_SetsParentIdAndSiblingSortOrder()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var branch = vm.Groups[0];

        await vm.CreateChildGroupAsync(branch);
        branch = vm.Groups[0];
        await vm.CreateChildGroupAsync(branch);

        var children = vm.Groups.Where(g => g.Depth == 1).ToList();
        Assert.Equal(2, children.Count);
        Assert.Equal(branch.Id, children[0].Model.ParentGroupId);
        Assert.Equal(branch.Id, children[1].Model.ParentGroupId);
        Assert.Equal(0, children[0].Model.SortOrder);
        Assert.Equal(1, children[1].Model.SortOrder);
    }

    [Fact]
    public async Task GetDashboardFilePathsAsync_UnknownId_ReturnsEmpty()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        var paths = await vm.GetGroupFilePathsAsync("no-such-id");

        Assert.Empty(paths);
    }

    [Fact]
    public async Task MalformedTopology_CyclicGroups_DoesNotCrash()
    {
        var groupRepo = new StubLogGroupRepository();
        var x = new LogGroup { Name = "X", Kind = LogGroupKind.Dashboard };
        var y = new LogGroup { Name = "Y", Kind = LogGroupKind.Dashboard };
        x.ParentGroupId = y.Id;
        y.ParentGroupId = x.Id;
        x.FileIds.Add("file-x");
        y.FileIds.Add("file-y");
        await groupRepo.AddAsync(x);
        await groupRepo.AddAsync(y);

        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync();

        Assert.Empty(vm.Groups);

        var paths = await vm.GetGroupFilePathsAsync(x.Id);
        Assert.NotNull(paths);
    }
}
