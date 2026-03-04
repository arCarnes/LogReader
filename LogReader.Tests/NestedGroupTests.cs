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
    public void OldGroupLoadsAsRootFileSet()
    {
        // A LogGroup created without setting ParentGroupId or Kind
        // should default to root FileSet
        var group = new LogGroup { Name = "Old Group" };

        Assert.Null(group.ParentGroupId);
        Assert.Equal(LogGroupKind.FileSet, group.Kind);
    }

    // ─── Kind invariants ──────────────────────────────────────────────────────

    [Fact]
    public async Task FileSetCannotHaveChildren()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null); // root FileSet
        var fileSet = vm.Groups[0];

        var result = await vm.CreateChildGroupAsync(fileSet, LogGroupKind.FileSet);

        Assert.False(result);
        Assert.Single(vm.Groups); // no child was added
    }

    [Fact]
    public async Task ContainerCannotManageFiles()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var container = vm.Groups[0];

        Assert.Equal(LogGroupKind.Container, container.Kind);
        Assert.False(container.CanManageFiles);
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
    public async Task DepthCap_RejectsContainerAtDepth2()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        // Root container (depth 0)
        await vm.CreateContainerGroupCommand.ExecuteAsync(null);
        var root = vm.Groups[0];

        // Child container (depth 1)
        await vm.CreateChildGroupAsync(root, LogGroupKind.Container);
        var child = vm.Groups.First(g => g.Depth == 1);

        // Grandchild Container (depth 2) — should be rejected (no room for its children)
        var result = await vm.CreateChildGroupAsync(child, LogGroupKind.Container);

        Assert.False(result);
        Assert.Equal(2, vm.Groups.Count); // only root + child
    }

    [Fact]
    public async Task DepthCap_RejectsChildAtDepth3()
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

        // Great-grandchild — should be rejected (FileSet can't have children anyway)
        var result = await vm.CreateChildGroupAsync(grandchild, LogGroupKind.FileSet);

        Assert.False(result);
        Assert.Equal(3, vm.Groups.Count);
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

        var child1 = vm.Groups.Where(g => g.Kind == LogGroupKind.FileSet).First();
        var child2 = vm.Groups.Where(g => g.Kind == LogGroupKind.FileSet).Last();
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

        var child1 = vm.Groups.Where(g => g.Kind == LogGroupKind.FileSet).First();
        var child2 = vm.Groups.Where(g => g.Kind == LogGroupKind.FileSet).Last();
        // Same file in both children
        child1.Model.FileIds.Add("shared-file");
        child2.Model.FileIds.Add("shared-file");

        var resolved = vm.ResolveFileIds(container);

        Assert.Single(resolved);
        Assert.Contains("shared-file", resolved);
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
        var container = vm.Groups.First(g => g.Kind == LogGroupKind.Container);
        await vm.CreateChildGroupAsync(container, LogGroupKind.FileSet);

        // Re-fetch after rebuild
        container = vm.Groups.First(g => g.Kind == LogGroupKind.Container);
        var child = vm.Groups.First(g => g.Kind == LogGroupKind.FileSet);
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
}
