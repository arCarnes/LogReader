using System.Windows;
using System.Windows.Controls;
using LogReader.App.Models;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

namespace LogReader.Tests;

public class DashboardTreeTests
{
    private class StubLogFileRepository : ILogFileRepository
    {
        private readonly List<LogFileEntry> _entries = new();

        public Task<List<LogFileEntry>> GetAllAsync() => Task.FromResult(_entries.ToList());
        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids)
        {
            var idSet = ids.ToHashSet(StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(
                _entries
                    .Where(entry => idSet.Contains(entry.Id))
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
        {
            var result = new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
                result[filePath] = GetOrCreateEntry(filePath);

            return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(result);
        }
        public Task<LogFileEntry> GetOrCreateByPathAsync(string filePath, DateTime? lastOpenedAtUtc = null)
        {
            var entry = GetOrCreateEntry(filePath);
            if (lastOpenedAtUtc.HasValue)
                entry.LastOpenedAt = lastOpenedAtUtc.Value;

            return Task.FromResult(entry);
        }
        private LogFileEntry GetOrCreateEntry(string filePath)
        {
            var existing = _entries.FirstOrDefault(entry =>
                string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing;

            var entry = new LogFileEntry { FilePath = filePath };
            _entries.Add(entry);
            return entry;
        }
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
        public Task ReplaceAllAsync(IReadOnlyList<LogGroup> groups)
        {
            _groups.Clear();
            _groups.AddRange(groups);
            return Task.CompletedTask;
        }
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
        public Task ExportViewAsync(string exportPath) => Task.CompletedTask;
        public Task<ViewExport?> ImportViewAsync(string importPath) => Task.FromResult<ViewExport?>(null);

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

    private class StubSettingsRepository : ISettingsRepository
    {
        public Task<AppSettings> LoadAsync() => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings) => Task.CompletedTask;
    }

    private class StubSearchService : ISearchService
    {
        public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(new SearchResult { FilePath = filePath });
        public Task<SearchResult> SearchFileRangeAsync(
            string filePath,
            SearchRequest request,
            FileEncoding encoding,
            Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
            CancellationToken ct = default)
            => Task.FromResult(new SearchResult { FilePath = filePath });
        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    }

    private MainViewModel CreateViewModel(
        ILogFileRepository? fileRepo = null,
        ILogGroupRepository? groupRepo = null,
        IMessageBoxService? messageBoxService = null)
    {
        return new MainViewModel(
            fileRepo ?? new StubLogFileRepository(),
            groupRepo ?? new StubLogGroupRepository(),
            new StubSettingsRepository(),
            new StubLogReaderService(),
            new StubSearchService(),
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            enableLifecycleTimer: false,
            messageBoxService: messageBoxService ?? new StubMessageBoxService
            {
                OnShow = static (_, _, _, _) => MessageBoxResult.Yes
            });
    }

    [Fact]
    public void OutsideClick_WhenFolderRenameIsActive_TriggersCommitPath()
    {
        WpfTestHost.Run(() =>
        {
            var folder = CreateGroupViewModel(LogGroupKind.Branch);
            folder.BeginEdit();

            var shouldCommit = DashboardTreeView.ShouldCommitEditingGroupOnMouseDown(new Border(), folder);

            Assert.True(shouldCommit);
        });
    }

    [Fact]
    public void OutsideClick_WhenDashboardRenameIsActive_TriggersCommitPath()
    {
        WpfTestHost.Run(() =>
        {
            var dashboard = CreateGroupViewModel(LogGroupKind.Dashboard);
            dashboard.BeginEdit();

            var shouldCommit = DashboardTreeView.ShouldCommitEditingGroupOnMouseDown(new Button(), dashboard);

            Assert.True(shouldCommit);
        });
    }

    [Fact]
    public void ClickInsideActiveRenameEditor_DoesNotTriggerCommitPath()
    {
        WpfTestHost.Run(() =>
        {
            var dashboard = CreateGroupViewModel(LogGroupKind.Dashboard);
            dashboard.BeginEdit();
            var editor = new TextBox
            {
                DataContext = dashboard
            };

            var shouldCommit = DashboardTreeView.ShouldCommitEditingGroupOnMouseDown(editor, dashboard);

            Assert.False(shouldCommit);
        });
    }

    [Fact]
    public void ClickInsideDifferentTextBox_StillTriggersCommitPath()
    {
        WpfTestHost.Run(() =>
        {
            var dashboard = CreateGroupViewModel(LogGroupKind.Dashboard);
            dashboard.BeginEdit();
            var otherEditor = new TextBox
            {
                DataContext = CreateGroupViewModel(LogGroupKind.Branch)
            };

            var shouldCommit = DashboardTreeView.ShouldCommitEditingGroupOnMouseDown(otherEditor, dashboard);

            Assert.True(shouldCommit);
        });
    }

    [Fact]
    public void FindEditingGroup_WhenNestedGroupIsEditing_ReturnsNestedGroup()
    {
        var folder = CreateGroupViewModel(LogGroupKind.Branch);
        var dashboard = CreateGroupViewModel(LogGroupKind.Dashboard);
        folder.Children.Add(dashboard);
        dashboard.BeginEdit();

        var editingGroup = DashboardTreeView.FindEditingGroup(new[] { folder });

        Assert.Same(dashboard, editingGroup);
    }

    [Fact]
    public void HasExceededGroupDragThreshold_OnlyReturnsTrueAfterMinimumMovement()
    {
        WpfTestHost.Run(() =>
        {
            var start = new Point(10, 10);

            Assert.False(DashboardTreeView.HasExceededGroupDragThreshold(start, start));
            Assert.True(DashboardTreeView.HasExceededGroupDragThreshold(
                start,
                new Point(10 + SystemParameters.MinimumHorizontalDragDistance + 1, 10)));
        });
    }

    [Fact]
    public void DashboardTreeInteractionDecisions_ReturnsBranchDropZones()
    {
        var branch = CreateGroupViewModel(LogGroupKind.Branch);
        const double height = 100;

        Assert.Equal(DropPlacement.Before, DashboardTreeInteractionDecisions.GetGroupDropPlacement(branch, height, 20));
        Assert.Equal(DropPlacement.Inside, DashboardTreeInteractionDecisions.GetGroupDropPlacement(branch, height, 50));
        Assert.Equal(DropPlacement.After, DashboardTreeInteractionDecisions.GetGroupDropPlacement(branch, height, 80));
    }

    [Fact]
    public void DashboardTreeInteractionDecisions_ReturnsDashboardDropZones()
    {
        var dashboard = CreateGroupViewModel(LogGroupKind.Dashboard);
        const double height = 100;

        Assert.Equal(DropPlacement.Before, DashboardTreeInteractionDecisions.GetGroupDropPlacement(dashboard, height, 49));
        Assert.Equal(DropPlacement.After, DashboardTreeInteractionDecisions.GetGroupDropPlacement(dashboard, height, 50));
        Assert.Equal(DropPlacement.Before, DashboardTreeInteractionDecisions.GetDashboardFileDropPlacement(height, 49));
        Assert.Equal(DropPlacement.After, DashboardTreeInteractionDecisions.GetDashboardFileDropPlacement(height, 50));
    }

    [Fact]
    public void IgnoredGroupRowChildGesture_ClearsPendingGesture()
    {
        WpfTestHost.Run(() =>
        {
            var row = new Grid
            {
                DataContext = CreateGroupViewModel(LogGroupKind.Dashboard)
            };
            var button = new Button();
            row.Children.Add(button);

            Assert.True(DashboardTreeView.ShouldClearIgnoredGroupRowGesture(button, row));
        });
    }

    [Fact]
    public void BeginDashboardTreeRename_WhenFolderProvided_EntersEditMode()
    {
        var vm = CreateViewModel();
        var folder = CreateGroupViewModel(LogGroupKind.Branch);

        vm.BeginDashboardTreeRename(folder);

        Assert.True(folder.IsEditing);
        Assert.Equal(folder.Name, folder.EditName);
    }

    [Fact]
    public void BeginDashboardTreeRename_WhenDashboardProvided_EntersEditMode()
    {
        var vm = CreateViewModel();
        var dashboard = CreateGroupViewModel(LogGroupKind.Dashboard);

        vm.BeginDashboardTreeRename(dashboard);

        Assert.True(dashboard.IsEditing);
        Assert.Equal(dashboard.Name, dashboard.EditName);
    }

    [Fact]
    public void BeginDashboardTreeRename_WhenAlreadyEditing_DoesNotResetPendingText()
    {
        var vm = CreateViewModel();
        var dashboard = CreateGroupViewModel(LogGroupKind.Dashboard);
        dashboard.BeginEdit();
        dashboard.EditName = "Pending Name";

        vm.BeginDashboardTreeRename(dashboard);

        Assert.True(dashboard.IsEditing);
        Assert.Equal("Pending Name", dashboard.EditName);
    }

    [Fact]
    public async Task CommitDashboardTreeRenameAsync_SuppressesDuplicateCommitForSameGroup()
    {
        var vm = CreateViewModel();
        var firstCommitStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        var dashboard = new LogGroupViewModel(
            new LogGroup
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Dashboard",
                Kind = LogGroupKind.Dashboard
            },
            async group =>
            {
                Interlocked.Increment(ref saveCount);
                firstCommitStarted.TrySetResult(true);
                await releaseCommit.Task;
            });

        vm.BeginDashboardTreeRename(dashboard);
        dashboard.EditName = "Renamed";

        var firstCommitTask = vm.CommitDashboardTreeRenameAsync(dashboard);
        await firstCommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await vm.CommitDashboardTreeRenameAsync(dashboard);

        releaseCommit.TrySetResult(true);
        await firstCommitTask;

        Assert.Equal(1, saveCount);
        Assert.Equal("Renamed", dashboard.Name);
        Assert.False(dashboard.IsEditing);
    }

    [Fact]
    public void TryGetGroupFromRenameSource_WhenButtonDataContextIsGroup_ReturnsGroup()
    {
        WpfTestHost.Run(() =>
        {
            var dashboard = CreateGroupViewModel(LogGroupKind.Dashboard);
            var button = new Button
            {
                DataContext = dashboard
            };

            var result = DashboardTreeView.TryGetGroupFromRenameSource(button, out var resolved);

            Assert.True(result);
            Assert.Same(dashboard, resolved);
        });
    }

    [Fact]
    public void TryGetGroupFromRenameSource_WhenContextMenuPlacementTargetIsGroup_ReturnsGroup()
    {
        WpfTestHost.Run(() =>
        {
            var folder = CreateGroupViewModel(LogGroupKind.Branch);
            var placementTarget = new Border
            {
                DataContext = folder
            };
            var contextMenu = new ContextMenu
            {
                PlacementTarget = placementTarget
            };
            var menuItem = new MenuItem();
            contextMenu.Items.Add(menuItem);

            var result = DashboardTreeView.TryGetGroupFromRenameSource(menuItem, out var resolved);

            Assert.True(result);
            Assert.Same(folder, resolved);
        });
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

    private static LogGroupViewModel CreateGroupViewModel(LogGroupKind kind)
    {
        return new LogGroupViewModel(
            new LogGroup
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = kind == LogGroupKind.Branch ? "Folder" : "Dashboard",
                Kind = kind
            },
            _ => Task.CompletedTask);
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
    public async Task CreateChildFolderCommand_CreatesBranchChild()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var branch = vm.Groups[0];

        await vm.CreateChildFolderCommand.ExecuteAsync(branch);

        Assert.Equal(LogGroupKind.Branch, vm.Groups.First(g => g.Depth == 1).Kind);
    }

    [Fact]
    public async Task OpenDashboardGroupCommand_EmptyDashboardFallsBackToAdHocScope()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = vm.Groups[0];

        await vm.OpenDashboardGroupCommand.ExecuteAsync(dashboard);

        Assert.Null(vm.ActiveDashboardId);
        Assert.True(vm.IsAdHocScopeActive);
        Assert.False(vm.Groups[0].IsSelected);
    }

    [Fact]
    public async Task HandleDashboardGroupInvokedAsync_EmptyDashboardFallsBackToAdHocScope()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = vm.Groups[0];

        await vm.HandleDashboardGroupInvokedAsync(dashboard);

        Assert.Null(vm.ActiveDashboardId);
        Assert.True(vm.IsAdHocScopeActive);
        Assert.False(vm.Groups[0].IsSelected);
    }

    [Fact]
    public async Task TryGetDashboardFileMenuContext_WhenDashboardFileMenuItemUsed_ResolvesDashboardAndMember()
    {
        WpfTestHost.Run(() =>
        {
            var groupVm = CreateGroupViewModel(LogGroupKind.Dashboard);
            var fileVm = new GroupFileMemberViewModel("file-1", "app.log", @"C:\logs\app.log", showFullPath: false);
            var placementTarget = new Border
            {
                DataContext = fileVm,
                Tag = groupVm
            };
            var contextMenu = new ContextMenu
            {
                PlacementTarget = placementTarget
            };
            var menuItem = new MenuItem { Header = "Remove from Dashboard" };
            contextMenu.Items.Add(menuItem);

            var resolved = DashboardTreeView.TryGetDashboardFileMenuContext(menuItem, out var resolvedFileVm, out var resolvedGroupVm);

            Assert.True(resolved);
            Assert.Same(fileVm, resolvedFileVm);
            Assert.Same(groupVm, resolvedGroupVm);
        });
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MoveDashboardGroupUpCommand_ReordersTopLevelDashboards()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var originalFirstId = vm.Groups[0].Id;
        var secondDashboard = vm.Groups[1];

        await vm.MoveDashboardGroupUpCommand.ExecuteAsync(secondDashboard);

        Assert.Equal(secondDashboard.Id, vm.Groups[0].Id);
        Assert.Equal(originalFirstId, vm.Groups[1].Id);
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
    public async Task EmptyRootItems_DoNotExposeExpandAffordance()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);

        Assert.All(vm.Groups, group => Assert.False(group.CanExpand));
    }

    [Fact]
    public async Task FolderWithChild_AndDashboardWithFiles_CanExpand()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var folder = vm.Groups.First(g => g.Kind == LogGroupKind.Branch);

        await vm.CreateChildGroupAsync(folder, LogGroupKind.Branch);
        await vm.OpenFilePathAsync(@"C:\test\dashboard.log");

        // Re-fetch after CreateChildGroupAsync which rebuilds all group VMs
        folder = vm.Groups.First(g => g.Kind == LogGroupKind.Branch);
        var dashboard = vm.Groups.First(g => g.Kind == LogGroupKind.Dashboard);

        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);
        dashboard.RefreshMemberFiles(
            vm.Tabs,
            new Dictionary<string, string> { [vm.Tabs[0].FileId] = vm.Tabs[0].FilePath },
            new Dictionary<string, bool> { [vm.Tabs[0].FileId] = true },
            selectedFileId: null,
            showFullPath: false);

        Assert.True(folder.CanExpand);
        Assert.True(dashboard.CanExpand);
    }

    [Fact]
    public async Task CreateRootItem_PreservesExpandedStateAndMemberFilesOfExistingDashboard()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = Assert.Single(vm.Groups);
        await SeedDashboardWithFileAsync(vm, dashboard, @"C:\test\preserved.log");
        dashboard.IsExpanded = true;
        var dashboardId = dashboard.Id;

        await vm.CreateContainerGroupCommand.ExecuteAsync(null);

        dashboard = vm.Groups.Single(g => g.Id == dashboardId);
        Assert.True(dashboard.IsExpanded);
        Assert.True(dashboard.CanExpand);
        Assert.Single(dashboard.MemberFiles);
    }

    [Fact]
    public async Task DeleteUnrelatedItem_PreservesExpandedStateAndMemberFilesOfExistingDashboard()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var folder = vm.Groups.First(g => g.Kind == LogGroupKind.Branch);
        var dashboard = vm.Groups.First(g => g.Kind == LogGroupKind.Dashboard);
        await SeedDashboardWithFileAsync(vm, dashboard, @"C:\test\still-there.log");
        dashboard.IsExpanded = true;
        var dashboardId = dashboard.Id;

        await vm.DeleteGroupCommand.ExecuteAsync(folder);

        dashboard = Assert.Single(vm.Groups);
        Assert.Equal(dashboardId, dashboard.Id);
        Assert.True(dashboard.IsExpanded);
        Assert.True(dashboard.CanExpand);
        Assert.Single(dashboard.MemberFiles);
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
    public async Task DashboardTreeFilter_ClearRestoresExpansionStateAcrossRepeatedFilterChanges()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var paymentsFolder = vm.Groups[0];
        var opsFolder = vm.Groups[1];
        paymentsFolder.Name = "Payments";
        opsFolder.Name = "Operations";

        await vm.CreateChildGroupAsync(paymentsFolder, LogGroupKind.Dashboard);
        await vm.CreateChildGroupAsync(opsFolder, LogGroupKind.Dashboard);

        paymentsFolder = vm.Groups.First(group => group.Id == paymentsFolder.Id);
        opsFolder = vm.Groups.First(group => group.Id == opsFolder.Id);
        paymentsFolder.IsExpanded = false;
        opsFolder.IsExpanded = true;
        vm.Groups.First(group => group.Parent?.Id == paymentsFolder.Id).Name = "Payroll";
        vm.Groups.First(group => group.Parent?.Id == opsFolder.Id).Name = "Alerts";

        vm.DashboardTreeFilter = "pay";
        Assert.True(paymentsFolder.IsExpanded);
        Assert.True(opsFolder.IsExpanded);

        vm.DashboardTreeFilter = "alerts";
        Assert.True(paymentsFolder.IsExpanded);
        Assert.True(opsFolder.IsExpanded);

        vm.DashboardTreeFilter = string.Empty;

        paymentsFolder = vm.Groups.First(group => group.Id == paymentsFolder.Id);
        opsFolder = vm.Groups.First(group => group.Id == opsFolder.Id);
        Assert.False(paymentsFolder.IsExpanded);
        Assert.True(opsFolder.IsExpanded);
    }

    [Fact]
    public async Task ApplyDashboardFileDropAsync_SameDashboardReordersFiles()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = vm.Groups[0];

        await SeedDashboardWithFileAsync(vm, dashboard, @"C:\test\a.log");
        await SeedDashboardWithFileAsync(vm, dashboard, @"C:\test\b.log");
        await SeedDashboardWithFileAsync(vm, dashboard, @"C:\test\c.log");

        dashboard = vm.Groups[0];
        await vm.ApplyDashboardFileDropAsync(
            dashboard,
            dashboard,
            dashboard.Model.FileIds[2],
            dashboard.Model.FileIds[0],
            DropPlacement.Before);

        Assert.Equal(
            new[] { @"C:\test\c.log", @"C:\test\a.log", @"C:\test\b.log" },
            dashboard.MemberFiles.Select(member => member.FilePath).ToArray());
    }

    [Fact]
    public async Task ApplyDashboardFileDropAsync_CrossDashboardMovesFiles()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var source = vm.Groups[0];
        var target = vm.Groups[1];

        await SeedDashboardWithFileAsync(vm, source, @"C:\test\source-a.log");
        await SeedDashboardWithFileAsync(vm, source, @"C:\test\source-b.log");
        await SeedDashboardWithFileAsync(vm, target, @"C:\test\target.log");

        source = vm.Groups.First(group => group.Id == source.Id);
        target = vm.Groups.First(group => group.Id == target.Id);
        await vm.ApplyDashboardFileDropAsync(
            source,
            target,
            source.Model.FileIds[0],
            target.Model.FileIds[0],
            DropPlacement.Before);

        Assert.Equal(new[] { @"C:\test\source-b.log" }, source.MemberFiles.Select(member => member.FilePath).ToArray());
        Assert.Equal(
            new[] { @"C:\test\source-a.log", @"C:\test\target.log" },
            target.MemberFiles.Select(member => member.FilePath).ToArray());
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
        Assert.Equal(
            new[] { @"C:\test\a.log", @"C:\test\b.log" },
            filtered.Select(tab => tab.FilePath).ToArray());
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
        await vm.OpenFilePathAsync(@"C:\test\a.log");

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

    // ── MoveGroupTo tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task MoveGroupTo_Before_ReordersWithinSameParent()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var a = vm.Groups[0];
        var b = vm.Groups[1];
        var c = vm.Groups[2];
        var aId = a.Id;
        var bId = b.Id;
        var cId = c.Id;

        // Move C before A
        await vm.MoveGroupToAsync(c, a, DropPlacement.Before);

        Assert.Equal(new[] { cId, aId, bId }, vm.Groups.Select(g => g.Id).ToArray());
    }

    [Fact]
    public async Task MoveGroupTo_After_ReordersWithinSameParent()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var a = vm.Groups[0];
        var b = vm.Groups[1];
        var c = vm.Groups[2];
        var aId = a.Id;
        var bId = b.Id;
        var cId = c.Id;

        // Move A after C
        await vm.MoveGroupToAsync(a, c, DropPlacement.After);

        Assert.Equal(new[] { bId, cId, aId }, vm.Groups.Select(g => g.Id).ToArray());
    }

    [Fact]
    public async Task MoveGroupTo_Inside_MovesDashboardIntoBranch()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var branch = vm.Groups[0];
        var dashboard = vm.Groups[1];
        var branchId = branch.Id;
        var dashboardId = dashboard.Id;

        await vm.MoveGroupToAsync(dashboard, branch, DropPlacement.Inside);

        Assert.Equal(2, vm.Groups.Count);
        var movedDashboard = vm.Groups.First(g => g.Id == dashboardId);
        Assert.Equal(branchId, movedDashboard.Model.ParentGroupId);
        Assert.Equal(1, movedDashboard.Depth);
    }

    [Fact]
    public async Task MoveGroupTo_BetweenFolders_MovesAndResequences()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        // Create two folders
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var folderA = vm.Groups[0];
        var folderB = vm.Groups[1];

        // Add a child to folder A
        await vm.CreateChildGroupAsync(folderA);
        folderA = vm.Groups.First(g => g.Id == folderA.Id);
        var child = vm.Groups.First(g => g.Model.ParentGroupId == folderA.Id);
        var childId = child.Id;

        // Move child into folder B
        await vm.MoveGroupToAsync(child, folderB, DropPlacement.Inside);

        var moved = vm.Groups.First(g => g.Id == childId);
        Assert.Equal(folderB.Id, moved.Model.ParentGroupId);
    }

    [Fact]
    public async Task CanMoveGroupTo_SelfDrop_ReturnsFalse()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var group = vm.Groups[0];
        Assert.False(vm.CanMoveGroupTo(group, group, DropPlacement.Before));
    }

    [Fact]
    public async Task CanMoveGroupTo_ParentIntoDescendant_ReturnsFalse()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var branch = vm.Groups[0];
        await vm.CreateChildGroupAsync(branch);
        branch = vm.Groups[0];
        var child = vm.Groups.First(g => g.Depth == 1);

        Assert.False(vm.CanMoveGroupTo(branch, child, DropPlacement.Before));
    }

    [Fact]
    public async Task CanMoveGroupTo_InsideDashboard_ReturnsFalse()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        Assert.False(vm.CanMoveGroupTo(vm.Groups[0], vm.Groups[1], DropPlacement.Inside));
    }

    [Fact]
    public async Task MoveGroupTo_InsideBranch_ExpandsTarget()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var branch = vm.Groups[0];
        branch.IsExpanded = false;
        var dashboard = vm.Groups[1];

        await vm.MoveGroupToAsync(dashboard, branch, DropPlacement.Inside);

        var branchAfter = vm.Groups.First(g => g.Id == branch.Id);
        Assert.True(branchAfter.IsExpanded);
    }

    [Fact]
    public async Task MoveGroupTo_PreservesExpandedState()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var folderA = vm.Groups[0];
        var folderB = vm.Groups[1];
        folderA.IsExpanded = true;
        folderB.IsExpanded = false;
        var dashboard = vm.Groups[2];

        await vm.MoveGroupToAsync(dashboard, folderA, DropPlacement.Inside);

        var afterA = vm.Groups.First(g => g.Id == folderA.Id);
        var afterB = vm.Groups.First(g => g.Id == folderB.Id);
        Assert.True(afterA.IsExpanded);
        Assert.False(afterB.IsExpanded);
    }

    [Fact]
    public async Task MoveGroupTo_NestedChild_BeforeRoot_BecomesRoot()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var branch = vm.Groups[0];
        var rootDash = vm.Groups[1];
        await vm.CreateChildGroupAsync(branch);
        branch = vm.Groups[0];
        var child = vm.Groups.First(g => g.Model.ParentGroupId == branch.Id);
        var childId = child.Id;

        // Move the nested child before the root dashboard
        await vm.MoveGroupToAsync(child, rootDash, DropPlacement.Before);

        var moved = vm.Groups.First(g => g.Id == childId);
        Assert.Null(moved.Model.ParentGroupId);
        Assert.Equal(0, moved.Depth);
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

    [Fact]
    public async Task MalformedTopology_DuplicateIdCycle_SkipsRepeatedRuntimeNodes()
    {
        var groupRepo = new StubLogGroupRepository();
        var root = new LogGroup
        {
            Id = "root",
            Name = "Root",
            Kind = LogGroupKind.Branch
        };
        var child = new LogGroup
        {
            Id = "child",
            Name = "Child",
            Kind = LogGroupKind.Dashboard,
            ParentGroupId = "root",
            FileIds = new List<string> { "file-child" }
        };
        var loopBack = new LogGroup
        {
            Id = "root",
            Name = "LoopBack",
            Kind = LogGroupKind.Dashboard,
            ParentGroupId = "child",
            FileIds = new List<string> { "file-cycle" }
        };
        await groupRepo.AddAsync(root);
        await groupRepo.AddAsync(child);
        await groupRepo.AddAsync(loopBack);

        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal(new[] { "root", "child" }, vm.Groups.Select(g => g.Id).ToArray());

        var resolvedFileIds = vm.ResolveFileIds(vm.Groups[0]);
        Assert.Equal(new HashSet<string> { "file-child" }, resolvedFileIds);
    }

    private static async Task SeedDashboardWithFileAsync(MainViewModel vm, LogGroupViewModel dashboard, string filePath)
    {
        await vm.OpenFilePathAsync(filePath);

        var tab = vm.Tabs.Last();
        dashboard.Model.FileIds.Add(tab.FileId);
        dashboard.NotifyStructureChanged();
        dashboard.RefreshMemberFiles(
            vm.Tabs,
            new Dictionary<string, string> { [tab.FileId] = tab.FilePath },
            new Dictionary<string, bool> { [tab.FileId] = true },
            selectedFileId: null,
            showFullPath: false);
    }
}
