using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

namespace LogReader.Tests;

public class NestedGroupTests
{
    // ─── Stubs ────────────────────────────────────────────────────────────────

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
            // Cascade delete descendants (mirrors JsonLogGroupRepository behavior)
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

    // ─── Helpers ──────────────────────────────────────────────────────────────

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
            new StubFileTailService());
    }

    // ─── Migration ────────────────────────────────────────────────────────────

    [Fact]
    public void OldGroupLoadsAsRootNeutral()
    {
        // A LogGroup created without setting ParentGroupId or Kind
        // should default to root Neutral.
        var group = new LogGroup { Name = "Old Group" };

        Assert.Null(group.ParentGroupId);
        Assert.Equal(LogGroupKind.Neutral, group.Kind);
    }

    // ─── Kind invariants ──────────────────────────────────────────────────────

    [Fact]
    public async Task FileGroupCanHaveChildren()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null); // root neutral
        var group = vm.Groups[0];
        group.Model.FileIds.Add("file-1"); // becomes file-group by structure
        group.NotifyStructureChanged();

        var result = await vm.CreateChildGroupAsync(group);

        Assert.True(result);
        Assert.Equal(2, vm.Groups.Count); // root + child
    }

    [Fact]
    public async Task ContainerCanManageFiles()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var root = vm.Groups[0];
        await vm.CreateChildGroupAsync(root);
        var container = vm.Groups.First(g => g.Depth == 0);

        Assert.Equal(LogGroupKind.Container, container.Kind);
        Assert.True(container.CanManageFiles);
    }

    // ─── Depth cap ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DepthCap_AllowsFileSetAtDepth2()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        // Root container (depth 0)
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var root = vm.Groups[0];

        // Child container (depth 1)
        await vm.CreateChildGroupAsync(root, LogGroupKind.Container);
        var child = vm.Groups.First(g => g.Depth == 1);

        // Grandchild FileSet (depth 2) — should succeed
        var result = await vm.CreateChildGroupAsync(child, LogGroupKind.FileSet);

        Assert.True(result);
        Assert.Equal(3, vm.Groups.Count);
        Assert.Equal(2, vm.Groups.Last().Depth);
    }

    [Fact]
    public async Task DepthCap_AllowsChildAtDepth2()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        // Root container (depth 0)
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var root = vm.Groups[0];

        // Child container (depth 1)
        await vm.CreateChildGroupAsync(root, LogGroupKind.Container);
        var child = vm.Groups.First(g => g.Depth == 1);

        // Grandchild at depth 2 should be allowed.
        var result = await vm.CreateChildGroupAsync(child, LogGroupKind.Container);

        Assert.True(result);
        Assert.Equal(3, vm.Groups.Count); // root + child + grandchild
    }

    [Fact]
    public async Task DepthCap_AllowsChildBeyondDepth3()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        // Root container (depth 0)
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var root = vm.Groups[0];

        // Child container (depth 1)
        await vm.CreateChildGroupAsync(root, LogGroupKind.Container);
        var child = vm.Groups.First(g => g.Depth == 1);

        // Grandchild FileSet (depth 2)
        await vm.CreateChildGroupAsync(child, LogGroupKind.FileSet);
        var grandchild = vm.Groups.First(g => g.Depth == 2);

        // Great-grandchild should now be allowed.
        var result = await vm.CreateChildGroupAsync(grandchild, LogGroupKind.FileSet);

        Assert.True(result);
        Assert.Equal(4, vm.Groups.Count);
    }

    // ─── ResolveFileIds ───────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveFileIds_FileSet_ReturnsOwnFiles()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];
        group.Model.FileIds.Add("file-1");
        group.Model.FileIds.Add("file-2");

        var resolved = vm.ResolveFileIds(group);

        Assert.Equal(new HashSet<string> { "file-1", "file-2" }, resolved);
    }

    [Fact]
    public async Task ResolveFileIds_Container_ReturnsDescendantFiles()
    {
        var groupRepo = new StubLogGroupRepository();
        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync();

        // Create container with two FileSet children
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var container = vm.Groups[0];
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);

        var children = vm.Groups.Where(g => g.Depth == 1).ToList();
        var child1 = children[0];
        var child2 = children[1];
        child1.Model.FileIds.Add("file-a");
        child2.Model.FileIds.Add("file-b");
        child2.Model.FileIds.Add("file-c");

        var resolved = vm.ResolveFileIds(container);

        Assert.Equal(new HashSet<string> { "file-a", "file-b", "file-c" }, resolved);
    }

    [Fact]
    public async Task ResolveFileIds_DeepNesting()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        // Container > Container > FileSet
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var root = vm.Groups[0];
        await vm.CreateChildGroupAsync(root, LogGroupKind.Container);
        var mid = vm.Groups.First(g => g.Depth == 1);
        await vm.CreateChildGroupAsync(mid, LogGroupKind.FileSet);
        var leaf = vm.Groups.First(g => g.Depth == 2);
        leaf.Model.FileIds.Add("deep-file");

        var resolved = vm.ResolveFileIds(root);

        Assert.Single(resolved);
        Assert.Contains("deep-file", resolved);
    }

    [Fact]
    public async Task ResolveFileIds_Deduplicates()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var container = vm.Groups[0];
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);

        var children = vm.Groups.Where(g => g.Depth == 1).ToList();
        var child1 = children[0];
        var child2 = children[1];
        // Same file in both children
        child1.Model.FileIds.Add("shared-file");
        child2.Model.FileIds.Add("shared-file");

        var resolved = vm.ResolveFileIds(container);

        Assert.Single(resolved);
        Assert.Contains("shared-file", resolved);
    }

    [Fact]
    public async Task ResolveFileIds_MixedGroup_ReturnsOwnAndDescendantFiles()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.CreateGroupCommand.ExecuteAsync(null);
        var root = vm.Groups[0];
        root.Model.FileIds.Add("root-file");

        await vm.CreateChildGroupAsync(root, LogGroupKind.FileSet);
        root = vm.Groups.First(g => g.Depth == 0);
        var child = vm.Groups.First(g => g.Depth == 1);
        child.Model.FileIds.Add("child-file");

        var resolved = vm.ResolveFileIds(root);

        Assert.Equal(2, resolved.Count);
        Assert.Contains("root-file", resolved);
        Assert.Contains("child-file", resolved);
    }

    // ─── FilteredTabs with hierarchy ──────────────────────────────────────────

    [Fact]
    public async Task FilteredTabs_SelectedContainer_IncludesDescendants()
    {
        var fileRepo = new StubLogFileRepository();
        var vm = CreateViewModel(fileRepo: fileRepo);
        await vm.InitializeAsync();

        // Open two files
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        var fileIdA = vm.Tabs[0].FileId;

        // Create container with a FileSet child containing first file
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var container = vm.Groups.First(g => g.Depth == 0);
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);

        // Re-fetch after rebuild
        container = vm.Groups.First(g => g.Depth == 0);
        var child = vm.Groups.First(g => g.Depth == 1);
        child.Model.FileIds.Add(fileIdA);

        // Select the container (not the child)
        vm.ToggleGroupSelection(container);

        var filtered = vm.FilteredTabs.ToList();
        Assert.Single(filtered);
        Assert.Equal(@"C:\test\a.log", filtered[0].FilePath);
    }

    // ─── Sibling-scoped reorder ───────────────────────────────────────────────

    [Fact]
    public async Task MoveGroupUp_WithinSiblings()
    {
        var groupRepo = new StubLogGroupRepository();
        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync();

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var first = vm.Groups[0];
        var second = vm.Groups[1];
        var secondId = second.Id;

        await vm.MoveGroupUpAsync(second);

        Assert.Equal(secondId, vm.Groups[0].Id);
    }

    [Fact]
    public async Task MoveGroupUp_FirstSibling_NoOp()
    {
        var groupRepo = new StubLogGroupRepository();
        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync();

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var firstId = vm.Groups[0].Id;
        var secondId = vm.Groups[1].Id;

        await vm.MoveGroupUpAsync(vm.Groups[0]);

        Assert.Equal(firstId, vm.Groups[0].Id);
        Assert.Equal(secondId, vm.Groups[1].Id);
    }

    // ─── Cascade delete ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteContainer_CascadesDescendants()
    {
        var groupRepo = new StubLogGroupRepository();
        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync();

        // Create a container with children
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var container = vm.Groups[0];
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);

        Assert.Equal(3, vm.Groups.Count);

        // Delete the container — children should also be gone
        await vm.DeleteGroupCommand.ExecuteAsync(container);

        Assert.Empty(vm.Groups);
    }

    // ─── Tree structure ───────────────────────────────────────────────────────

    [Fact]
    public async Task TreeBuilds_CorrectDepthFirstOrder()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        // Create: Container1 > Child1, Container2
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var c1 = vm.Groups[0];
        await vm.CreateChildGroupAsync(c1, LogGroupKind.FileSet);
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);

        // Should be: Container1, Child1, Container2
        Assert.Equal(3, vm.Groups.Count);
        Assert.Equal(0, vm.Groups[0].Depth); // Container1
        Assert.Equal(1, vm.Groups[1].Depth); // Child1
        Assert.Equal(0, vm.Groups[2].Depth); // Container2
    }

    [Fact]
    public async Task IsTreeVisible_HiddenWhenParentCollapsed()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var container = vm.Groups[0];
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);

        // Rebuild to get fresh VMs with parent references
        container = vm.Groups[0];
        var child = vm.Groups[1];

        // Container starts expanded (set in CreateContainerGroupCommand)
        Assert.True(child.IsTreeVisible);

        // Collapse parent
        container.IsExpanded = false;
        Assert.False(child.IsTreeVisible);

        // Re-expand
        container.IsExpanded = true;
        Assert.True(child.IsTreeVisible);
    }

    // ─── Additional recursive resolution ──────────────────────────────────────

    [Fact]
    public async Task ResolveFileIds_EmptyContainer_ReturnsEmpty()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var container = vm.Groups[0];

        var resolved = vm.ResolveFileIds(container);

        Assert.Empty(resolved);
    }

    // ─── CanManageFiles complement ────────────────────────────────────────────

    [Fact]
    public async Task FileGroup_CanManageFiles_IsTrue()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];
        group.Model.FileIds.Add("file-1");
        group.NotifyStructureChanged();

        Assert.Equal(LogGroupKind.FileSet, group.Kind);
        Assert.True(group.CanManageFiles);
    }

    // ─── Multi-select and deselect filtering ──────────────────────────────────

    [Fact]
    public async Task FilteredTabs_MultiSelect_UnionsFileSets()
    {
        var fileRepo = new StubLogFileRepository();
        var vm = CreateViewModel(fileRepo: fileRepo);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        var fileIdA = vm.Tabs[0].FileId;
        var fileIdB = vm.Tabs[1].FileId;

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(fileIdA);
        g2.Model.FileIds.Add(fileIdB);

        vm.ToggleGroupSelection(g1);
        vm.ToggleGroupSelection(g2, isMultiSelect: true);

        var filtered = vm.FilteredTabs.ToList();
        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, t => t.FilePath == @"C:\test\a.log");
        Assert.Contains(filtered, t => t.FilePath == @"C:\test\b.log");
    }

    [Fact]
    public async Task FilteredTabs_Deselect_RestoresAllTabs()
    {
        var fileRepo = new StubLogFileRepository();
        var vm = CreateViewModel(fileRepo: fileRepo);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        var fileIdA = vm.Tabs[0].FileId;

        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];
        group.Model.FileIds.Add(fileIdA);

        // Select → filter to 1 tab
        vm.ToggleGroupSelection(group);
        Assert.Single(vm.FilteredTabs);

        // Deselect → restore all tabs
        vm.ToggleGroupSelection(group);
        Assert.Equal(2, vm.FilteredTabs.Count());
    }

    // ─── Reorder: sibling-scoped, does not affect other branches ─────────────

    [Fact]
    public async Task MoveGroupDown_DoesNotAffectChildrenOfOtherBranch()
    {
        var groupRepo = new StubLogGroupRepository();
        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync();

        // Create Container A with a child
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var containerA = vm.Groups[0];
        var containerAId = containerA.Id;
        await vm.CreateChildGroupAsync(containerA, LogGroupKind.FileSet);

        // Create Container B (sibling of A)
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);

        // Re-fetch after rebuild
        containerA = vm.Groups.First(g => g.Id == containerAId);
        var child = vm.Groups.First(g => g.Depth == 1);
        var childParentId = child.Model.ParentGroupId;

        // Move A down (swaps A and B)
        await vm.MoveGroupDownAsync(containerA);

        // Container B is now first, but child's ParentGroupId must still be A
        var childAfter = vm.Groups.First(g => g.Depth == 1);
        Assert.Equal(childParentId, childAfter.Model.ParentGroupId);
        Assert.Equal(containerAId, childAfter.Model.ParentGroupId);
    }

    [Fact]
    public async Task Reorder_PersistsAfterViewModelReload()
    {
        var groupRepo = new StubLogGroupRepository();
        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync();

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var firstId = vm.Groups[0].Id;
        var secondId = vm.Groups[1].Id;

        // Move second up → becomes first
        await vm.MoveGroupUpAsync(vm.Groups[1]);

        // New VM with the same repo
        var vm2 = CreateViewModel(groupRepo: groupRepo);
        await vm2.InitializeAsync();

        Assert.Equal(secondId, vm2.Groups[0].Id);
        Assert.Equal(firstId, vm2.Groups[1].Id);
    }

    // ─── Delete: deselects subtree and updates tabs ───────────────────────────

    [Fact]
    public async Task DeleteGroup_DeselectsDeletedSubtree()
    {
        var groupRepo = new StubLogGroupRepository();
        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync();

        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var container = vm.Groups[0];
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);

        // Re-fetch after rebuild
        container = vm.Groups[0];
        var children = vm.Groups.Where(g => g.Depth == 1).ToList();
        var child1 = children[0];
        var child2 = children[1];
        vm.ToggleGroupSelection(child1);
        vm.ToggleGroupSelection(child2, isMultiSelect: true);
        Assert.Equal(2, vm.Groups.Count(g => g.IsSelected));

        // Delete container — should deselect children and remove all
        await vm.DeleteGroupCommand.ExecuteAsync(container);

        Assert.Empty(vm.Groups);
        // No exception means deselect logic ran without error
    }

    [Fact]
    public async Task DeleteGroup_UpdatesFilteredTabs()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        var fileIdA = vm.Tabs[0].FileId;

        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];
        group.Model.FileIds.Add(fileIdA);
        vm.ToggleGroupSelection(group);
        Assert.Single(vm.FilteredTabs);

        await vm.DeleteGroupCommand.ExecuteAsync(group);

        // No groups selected → all tabs shown
        Assert.Equal(2, vm.FilteredTabs.Count());
    }

    // ─── Child creation: ParentGroupId and SortOrder ──────────────────────────

    [Fact]
    public async Task CreateChild_SetsParentGroupId()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var container = vm.Groups[0];
        var containerId = container.Id;

        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);

        var child = vm.Groups.First(g => g.Depth == 1);
        Assert.Equal(containerId, child.Model.ParentGroupId);
    }

    [Fact]
    public async Task CreateChild_SortOrder_IsLastSibling()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var container = vm.Groups[0];

        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);
        // Re-fetch container after rebuild
        container = vm.Groups[0];
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);

        var children = vm.Groups.Where(g => g.Depth == 1).ToList();
        Assert.Equal(2, children.Count);
        Assert.Equal(0, children[0].Model.SortOrder);
        Assert.Equal(1, children[1].Model.SortOrder);
    }

    // ─── Import contract (behavior via direct group setup) ────────────────────

    [Fact]
    public async Task ImportedGroup_ParticipatesInFiltering()
    {
        // Simulate what MainViewModel.ImportGroup produces: a root FileSet with file IDs
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        var fileIdA = vm.Tabs[0].FileId;

        // Seed a root FileSet directly — same structure ImportGroup produces
        var importedGroup = new LogGroup
        {
            Name = "Imported",
            Kind = LogGroupKind.FileSet,
            FileIds = new List<string> { fileIdA }
        };
        await groupRepo.AddAsync(importedGroup);
        var allGroups = await groupRepo.GetAllAsync();
        // Trigger rebuild via a second VM reusing the same repo
        var vm2 = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm2.InitializeAsync();

        // Tabs were opened in vm, not vm2 — set them up in vm2 too
        await vm2.OpenFilePathAsync(@"C:\test\a.log");
        await vm2.OpenFilePathAsync(@"C:\test\b.log");

        var importedVm = vm2.Groups.First(g => g.Model.Id == importedGroup.Id);
        vm2.ToggleGroupSelection(importedVm);

        var filtered = vm2.FilteredTabs.ToList();
        Assert.Single(filtered);
        Assert.Equal(@"C:\test\a.log", filtered[0].FilePath);
    }

    // ─── API consistency ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetGroupFilePathsAsync_MatchesRecursiveResolution()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        // Add file entries with known paths
        var entryA = new LogFileEntry { FilePath = @"C:\test\a.log" };
        var entryB = new LogFileEntry { FilePath = @"C:\test\b.log" };
        await fileRepo.AddAsync(entryA);
        await fileRepo.AddAsync(entryB);

        // Create container with two FileSet children
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var container = vm.Groups[0];
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);

        container = vm.Groups[0];
        var children = vm.Groups.Where(g => g.Depth == 1).ToList();
        var child1 = children[0];
        var child2 = children[1];
        child1.Model.FileIds.Add(entryA.Id);
        child2.Model.FileIds.Add(entryB.Id);

        var paths = (await vm.GetGroupFilePathsAsync(container.Id)).OrderBy(p => p).ToList();

        Assert.Equal(2, paths.Count);
        Assert.Contains(@"C:\test\a.log", paths);
        Assert.Contains(@"C:\test\b.log", paths);
    }

    [Fact]
    public async Task GetGroupFilePathsAsync_UnknownGroupId_ReturnsEmpty()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        var paths = await vm.GetGroupFilePathsAsync("no-such-id");

        Assert.Empty(paths);
    }

    // ─── Safety / malformed topology ─────────────────────────────────────────

    [Fact]
    public async Task MalformedTopology_OrphanedParent_NotInTree()
    {
        // A group whose ParentGroupId points to a non-existent group
        // should be silently excluded from the tree (never reachable from any root).
        var groupRepo = new StubLogGroupRepository();
        var orphan = new LogGroup
        {
            Name = "Orphan",
            ParentGroupId = "ghost-parent-that-does-not-exist"
        };
        await groupRepo.AddAsync(orphan);

        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync();

        Assert.Empty(vm.Groups);
    }

    [Fact]
    public async Task MalformedTopology_CyclicGroups_DoesNotCrash()
    {
        // X.ParentGroupId = Y.Id and Y.ParentGroupId = X.Id — neither is a root.
        // Tree build is safe (starts from null-parent roots, so X and Y are skipped).
        // GetGroupFilePathsAsync must terminate without stack overflow (cycle guard).
        var groupRepo = new StubLogGroupRepository();
        var x = new LogGroup { Name = "X", Kind = LogGroupKind.FileSet };
        var y = new LogGroup { Name = "Y", Kind = LogGroupKind.FileSet };
        x.ParentGroupId = y.Id;
        y.ParentGroupId = x.Id;
        x.FileIds.Add("file-x");
        y.FileIds.Add("file-y");
        await groupRepo.AddAsync(x);
        await groupRepo.AddAsync(y);

        var vm = CreateViewModel(groupRepo: groupRepo);
        await vm.InitializeAsync();

        // Neither X nor Y appears in the tree (no null-parent root in the cycle)
        Assert.Empty(vm.Groups);

        // GetGroupFilePathsAsync must terminate (cycle guard in ResolveFileIdsFromModels)
        var paths = await vm.GetGroupFilePathsAsync(x.Id);
        // X is a FileSet; Y is visited next but then X would recur — guard breaks it.
        // X's own file-x is collected; file-y from Y may or may not be collected
        // depending on traversal order, but no crash is the critical guarantee.
        Assert.NotNull(paths);
    }
}
