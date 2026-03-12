using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using System.Reflection;

namespace LogReader.Tests;

public class MainViewModelTests
{
    // ─── Stubs (test-specific — shared stubs are in Stubs.cs) ────────────────

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

        public Task<List<LogGroup>> GetAllAsync() => Task.FromResult(_groups.ToList());
        public Task<LogGroup?> GetByIdAsync(string id)
            => Task.FromResult(_groups.FirstOrDefault(g => g.Id == id));
        public Task AddAsync(LogGroup group) { _groups.Add(group); return Task.CompletedTask; }
        public Task UpdateAsync(LogGroup group) => Task.CompletedTask;
        public Task DeleteAsync(string id) { _groups.RemoveAll(g => g.Id == id); return Task.CompletedTask; }
        public Task ReorderAsync(List<string> orderedIds) => Task.CompletedTask;
        public Task ExportGroupAsync(string groupId, string exportPath) => Task.CompletedTask;
        public Task<GroupExport?> ImportGroupAsync(string importPath) => Task.FromResult<GroupExport?>(null);
    }

    private class StubSessionRepository : ISessionRepository
    {
        public SessionState State { get; set; } = new();
        public Task<SessionState> LoadAsync() => Task.FromResult(State);
        public Task SaveAsync(SessionState state) { State = state; return Task.CompletedTask; }
    }

    private class StubSettingsRepository : ISettingsRepository
    {
        public AppSettings Settings { get; set; } = new();
        public Task<AppSettings> LoadAsync() => Task.FromResult(Settings);
        public Task SaveAsync(AppSettings settings)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private class StubSearchService : ISearchService
    {
        public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(new SearchResult { FilePath = filePath });
        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
            => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    }

    private class RecordingSearchService : ISearchService
    {
        public SearchResult NextResult { get; set; } = new();
        public int SearchFileCallCount { get; private set; }
        public SearchRequest? LastSearchFileRequest { get; private set; }

        public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
        {
            SearchFileCallCount++;
            LastSearchFileRequest = new SearchRequest
            {
                Query = request.Query,
                IsRegex = request.IsRegex,
                CaseSensitive = request.CaseSensitive,
                WholeWord = request.WholeWord,
                FilePaths = request.FilePaths.ToList(),
                StartLineNumber = request.StartLineNumber,
                EndLineNumber = request.EndLineNumber,
                FromTimestamp = request.FromTimestamp,
                ToTimestamp = request.ToTimestamp
            };
            return Task.FromResult(new SearchResult
            {
                FilePath = NextResult.FilePath,
                Hits = NextResult.Hits.ToList(),
                Error = NextResult.Error,
                HasParseableTimestamps = NextResult.HasParseableTimestamps
            });
        }

        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
            => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private MainViewModel CreateViewModel(
        ILogFileRepository? fileRepo = null,
        ILogGroupRepository? groupRepo = null,
        ISessionRepository? sessionRepo = null,
        ISettingsRepository? settingsRepo = null,
        IFileTailService? tailService = null,
        ILogReaderService? logReader = null,
        ISearchService? searchService = null)
    {
        return new MainViewModel(
            fileRepo ?? new StubLogFileRepository(),
            groupRepo ?? new StubLogGroupRepository(),
            sessionRepo ?? new StubSessionRepository(),
            settingsRepo ?? new StubSettingsRepository(),
            logReader ?? new StubLogReaderService(),
            searchService ?? new StubSearchService(),
            tailService ?? new StubFileTailService(),
            enableLifecycleTimer: false);
    }

    private static Dictionary<string, long> GetTabOrderMap(MainViewModel vm, string fieldName)
    {
        var field = typeof(MainViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (Dictionary<string, long>)field!.GetValue(vm)!;
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenFilePathAsync_DeduplicatesByPath()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\file.log");
        await vm.OpenFilePathAsync(@"C:\test\file.log");

        Assert.Single(vm.Tabs);
    }

    [Fact]
    public async Task InitializeAsync_DoesNotSeedRootBranch_WhenNoGroups()
    {
        var vm = new MainViewModel(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
            new StubSessionRepository(),
            new StubSettingsRepository(),
            new StubLogReaderService(),
            new StubSearchService(),
            new StubFileTailService(),
            enableLifecycleTimer: false);

        await vm.InitializeAsync();

        Assert.Empty(vm.Groups);
    }

    [Fact]
    public async Task OpenFilePathAsync_CaseInsensitiveDedupe()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\file.log");
        await vm.OpenFilePathAsync(@"C:\TEST\FILE.LOG");

        Assert.Single(vm.Tabs);
    }

    [Fact]
    public async Task OpenFilePathAsync_UsesDefaultEncodingFromSettings()
    {
        var reader = new StubLogReaderService();
        var settingsRepo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                DefaultFileEncoding = FileEncoding.Utf16
            }
        };
        var vm = CreateViewModel(settingsRepo: settingsRepo, logReader: reader);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\file.log");

        Assert.Single(vm.Tabs);
        Assert.Equal(FileEncoding.Utf16, vm.Tabs[0].Encoding);
        Assert.Equal(FileEncoding.Utf16, reader.LastBuildEncoding);
    }

    [Fact]
    public async Task OpenFilePathAsync_WhenPrimaryEncodingFails_UsesFallbackOrder()
    {
        var reader = new StubLogReaderService();
        reader.BuildFailures.Add(FileEncoding.Utf8);
        var settingsRepo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                DefaultFileEncoding = FileEncoding.Utf8,
                FileEncodingFallbacks = new List<FileEncoding> { FileEncoding.Utf16, FileEncoding.Ansi }
            }
        };
        var vm = CreateViewModel(settingsRepo: settingsRepo, logReader: reader);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\file.log");

        Assert.Single(vm.Tabs);
        Assert.Equal(FileEncoding.Utf16, vm.Tabs[0].Encoding);
        Assert.False(vm.Tabs[0].HasLoadError);
        Assert.Equal(new[] { FileEncoding.Utf8, FileEncoding.Utf16 }, reader.AttemptedBuildEncodings);
    }

    [Fact]
    public async Task CloseTab_DisposesAndRemovesTab()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\file.log");

        var tab = vm.Tabs[0];
        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.Empty(vm.Tabs);
        Assert.Null(vm.SelectedTab);
    }

    [Fact]
    public async Task CloseTab_RemovesTabOrderingMetadata()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\file.log");

        var tab = vm.Tabs[0];
        vm.TogglePinTab(tab);

        var openOrder = GetTabOrderMap(vm, "_tabOpenOrder");
        var pinOrder = GetTabOrderMap(vm, "_tabPinOrder");
        Assert.Contains(tab.FileId, openOrder.Keys);
        Assert.Contains(tab.FileId, pinOrder.Keys);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        Assert.DoesNotContain(tab.FileId, openOrder.Keys);
        Assert.DoesNotContain(tab.FileId, pinOrder.Keys);
    }

    [Fact]
    public async Task FilteredTabs_NoDashboardActive_ReturnsOnlyAdHocTabs()
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
        Assert.Equal(vm.Tabs[1].FilePath, filtered[0].FilePath);
    }

    [Fact]
    public async Task FilteredTabs_FiltersWhenDashboardIsActive()
    {
        var groupRepo = new StubLogGroupRepository();
        var fileRepo = new StubLogFileRepository();
        var vm = CreateViewModel(fileRepo: fileRepo, groupRepo: groupRepo);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        // Create a group containing only the first file
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];
        group.Model.FileIds.Add(vm.Tabs[0].FileId);

        // Select the group to enable filtering
        vm.ToggleGroupSelection(group);

        var filtered = vm.FilteredTabs.ToList();
        Assert.Single(filtered);
        Assert.Equal(vm.Tabs[0].FilePath, filtered[0].FilePath);
    }

    [Fact]
    public async Task NavigateToLineAsync_WhenHitIsInDifferentDashboard_SwitchesActiveDashboardAndTab()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        var tabA = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        var tabB = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        g1.Model.FileIds.Add(tabA.FileId);
        g2.Model.FileIds.Add(tabB.FileId);

        vm.ToggleGroupSelection(g1);
        Assert.Equal(g1.Id, vm.ActiveDashboardId);
        Assert.DoesNotContain(tabB, vm.FilteredTabs);

        await vm.NavigateToLineAsync(tabB.FilePath, 42);

        Assert.Equal(g2.Id, vm.ActiveDashboardId);
        Assert.True(g2.IsSelected);
        Assert.False(g1.IsSelected);
        Assert.Same(tabB, vm.SelectedTab);
        Assert.Single(vm.FilteredTabs);
        Assert.Contains(tabB, vm.FilteredTabs);
        Assert.Equal(42, tabB.NavigateToLineNumber);
    }

    [Fact]
    public async Task NavigateToLineAsync_WhenHitIsAdHoc_ClearsActiveDashboardAndSelectsTab()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = vm.Groups[0];
        var tabA = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        var tabB = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        dashboard.Model.FileIds.Add(tabA.FileId);

        vm.ToggleGroupSelection(dashboard);
        Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
        Assert.DoesNotContain(tabB, vm.FilteredTabs);

        await vm.NavigateToLineAsync(tabB.FilePath, 77);

        Assert.Null(vm.ActiveDashboardId);
        Assert.All(vm.Groups, g => Assert.False(g.IsSelected));
        Assert.Same(tabB, vm.SelectedTab);
        Assert.Single(vm.FilteredTabs);
        Assert.Contains(tabB, vm.FilteredTabs);
        Assert.Equal(77, tabB.NavigateToLineNumber);
    }

    [Fact]
    public async Task NavigateToTimestampAsync_ExactMatch_NavigatesToMatchingLine()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-ts-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path,
                "2026-03-09 19:49:10 INFO one\n2026-03-09 19:49:20 INFO two\n2026-03-09 19:49:30 INFO three\n");
            await vm.OpenFilePathAsync(path);

            var status = await vm.NavigateToTimestampAsync("2026-03-09 19:49:20");

            Assert.Contains("exact timestamp match", status, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(vm.SelectedTab);
            Assert.Equal(2, vm.SelectedTab!.NavigateToLineNumber);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task NavigateToTimestampAsync_NoExactMatch_NavigatesToNearestLine()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-ts-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path,
                "2026-03-09 19:49:10 INFO one\n2026-03-09 19:49:30 INFO two\n");
            await vm.OpenFilePathAsync(path);

            var status = await vm.NavigateToTimestampAsync("2026-03-09 19:49:26");

            Assert.Contains("no exact timestamp match", status, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(vm.SelectedTab);
            Assert.Equal(2, vm.SelectedTab!.NavigateToLineNumber);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task NavigateToTimestampAsync_NoParseableTimestamps_ReturnsClearStatus()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-ts-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path, "INFO one\nWARN two\nERROR three\n");
            await vm.OpenFilePathAsync(path);

            var status = await vm.NavigateToTimestampAsync("2026-03-09 19:49:26");

            Assert.Equal("No parseable timestamps found in the current file.", status);
            Assert.NotNull(vm.SelectedTab);
            Assert.Equal("No parseable timestamps found in the current file.", vm.SelectedTab!.StatusText);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task FilterPanel_ApplyFilter_CurrentTabOnly_ActivatesSnapshotFilter()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");
        Assert.NotNull(vm.SelectedTab);

        search.NextResult = new SearchResult
        {
            FilePath = vm.SelectedTab!.FilePath,
            Hits = new List<SearchHit>
            {
                new() { LineNumber = 2, LineText = "Line 2", MatchStart = 0, MatchLength = 4 },
                new() { LineNumber = 5, LineText = "Line 5", MatchStart = 0, MatchLength = 4 }
            },
            HasParseableTimestamps = true
        };

        vm.FilterPanel.Query = "Line";
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.True(vm.SelectedTab.IsFilterActive);
        Assert.Equal(2, vm.SelectedTab.FilteredLineCount);
        Assert.Equal(2, vm.SelectedTab.VisibleLines.Count);
        Assert.Equal(new[] { 2, 5 }, vm.SelectedTab.VisibleLines.Select(l => l.LineNumber).ToArray());
        Assert.Equal("Filter active: 2 matching lines.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_ClearFilter_RestoresFullSnapshotView()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");
        Assert.NotNull(vm.SelectedTab);

        search.NextResult = new SearchResult
        {
            FilePath = vm.SelectedTab!.FilePath,
            Hits = new List<SearchHit>
            {
                new() { LineNumber = 3, LineText = "Line 3", MatchStart = 0, MatchLength = 4 }
            },
            HasParseableTimestamps = true
        };

        vm.FilterPanel.Query = "Line";
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);
        Assert.True(vm.SelectedTab.IsFilterActive);

        await vm.FilterPanel.ClearFilterCommand.ExecuteAsync(null);

        Assert.False(vm.SelectedTab.IsFilterActive);
        Assert.Equal(vm.SelectedTab.TotalLines, vm.SelectedTab.DisplayLineCount);
        Assert.Equal("Filter cleared.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task FilterPanel_ApplyFilter_InvalidTimestampRange_DoesNotSearch()
    {
        var search = new RecordingSearchService();
        var vm = CreateViewModel(searchService: search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");

        vm.FilterPanel.Query = "Line";
        vm.FilterPanel.FromTimestamp = "invalid";

        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal(0, search.SearchFileCallCount);
        Assert.Contains("Invalid 'From' timestamp", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task CloseAllTabs_ClearsAllTabs()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CloseAllTabsAsync();

        Assert.Empty(vm.Tabs);
        Assert.Null(vm.SelectedTab);
    }

    [Fact]
    public async Task CloseAllTabs_ClearsActiveDashboardSelection()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var dashboard = vm.Groups[0];
        dashboard.Model.FileIds.Add(vm.Tabs[0].FileId);
        vm.ToggleGroupSelection(dashboard);
        Assert.Equal(dashboard.Id, vm.ActiveDashboardId);
        Assert.True(dashboard.IsSelected);

        await vm.CloseAllTabsAsync();

        Assert.Empty(vm.Tabs);
        Assert.Null(vm.ActiveDashboardId);
        Assert.All(vm.Groups, g => Assert.False(g.IsSelected));
    }

    [Fact]
    public async Task CloseOtherTabs_KeepsOnlySpecifiedTab()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        var keepTab = vm.Tabs[1];
        await vm.CloseOtherTabsAsync(keepTab);

        Assert.Single(vm.Tabs);
        Assert.Same(keepTab, vm.Tabs[0]);
        Assert.Same(keepTab, vm.SelectedTab);
    }

    [Fact]
    public async Task CloseAllButPinned_KeepsPinnedTabs()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.Tabs[0].IsPinned = true;
        vm.Tabs[2].IsPinned = true;

        await vm.CloseAllButPinnedAsync();

        Assert.Equal(2, vm.Tabs.Count);
        Assert.All(vm.Tabs, t => Assert.True(t.IsPinned));
    }

    [Fact]
    public async Task TogglePinTab_TogglesIsPinned()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        var tab = vm.Tabs[0];
        Assert.False(tab.IsPinned);

        vm.TogglePinTab(tab);
        Assert.True(tab.IsPinned);

        vm.TogglePinTab(tab);
        Assert.False(tab.IsPinned);
    }

    [Fact]
    public async Task FilteredTabs_SortsPinnedFirst()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        // Pin the last tab
        vm.Tabs[2].IsPinned = true;

        var filtered = vm.FilteredTabs.ToList();
        Assert.True(filtered[0].IsPinned);
        Assert.Equal(@"C:\test\c.log", filtered[0].FilePath);
    }

    [Fact]
    public async Task FilteredTabs_UsesPinnedAndUnpinnedLanes()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");
        await vm.OpenFilePathAsync(@"C:\test\d.log");

        vm.TogglePinTab(vm.Tabs[2]); // c pinned first
        vm.TogglePinTab(vm.Tabs[0]); // a pinned second

        var ordered = vm.FilteredTabs.Select(t => t.FilePath).ToList();
        Assert.Equal(
            new[] { @"C:\test\c.log", @"C:\test\a.log", @"C:\test\b.log", @"C:\test\d.log" },
            ordered);
    }

    [Fact]
    public async Task FilteredTabs_UnpinnedOrder_RemainsStableAcrossSelectionChanges()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        var initialUnpinnedOrder = vm.FilteredTabs
            .Where(t => !t.IsPinned)
            .Select(t => t.FilePath)
            .ToList();

        vm.SelectedTab = vm.Tabs[0];
        vm.SelectedTab = vm.Tabs[2];

        var afterSelectionChange = vm.FilteredTabs
            .Where(t => !t.IsPinned)
            .Select(t => t.FilePath)
            .ToList();

        Assert.Equal(initialUnpinnedOrder, afterSelectionChange);
    }

    [Fact]
    public async Task FilteredTabs_PinnedOrder_RemainsStableAcrossSelectionChanges()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.TogglePinTab(vm.Tabs[1]); // b pinned first
        vm.TogglePinTab(vm.Tabs[0]); // a pinned second

        var initialPinnedOrder = vm.FilteredTabs
            .Where(t => t.IsPinned)
            .Select(t => t.FilePath)
            .ToList();

        vm.SelectedTab = vm.Tabs[2];
        var afterSelectionChange = vm.FilteredTabs
            .Where(t => t.IsPinned)
            .Select(t => t.FilePath)
            .ToList();

        Assert.Equal(initialPinnedOrder, afterSelectionChange);
    }

    [Fact]
    public async Task TogglePinTab_RePinningTab_UpdatesPinnedOrderDeterministically()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.TogglePinTab(vm.Tabs[0]); // a
        vm.TogglePinTab(vm.Tabs[1]); // b
        vm.TogglePinTab(vm.Tabs[0]); // unpin a
        vm.TogglePinTab(vm.Tabs[0]); // re-pin a, should become after b

        var pinned = vm.FilteredTabs.Where(t => t.IsPinned).Select(t => t.FilePath).ToList();
        Assert.Equal(new[] { @"C:\test\b.log", @"C:\test\a.log" }, pinned);
    }

    [Fact]
    public async Task SelectNextTabCommand_SelectsNextTabInFilteredOrder()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.SelectedTab = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        vm.SelectNextTabCommand.Execute(null);

        Assert.Equal(@"C:\test\b.log", vm.SelectedTab!.FilePath);
    }

    [Fact]
    public async Task SelectPreviousTabCommand_SelectsPreviousTabInFilteredOrder()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.SelectedTab = vm.Tabs.First(t => t.FilePath == @"C:\test\c.log");
        vm.SelectPreviousTabCommand.Execute(null);

        Assert.Equal(@"C:\test\b.log", vm.SelectedTab!.FilePath);
    }

    [Fact]
    public async Task SelectPreviousTabCommand_WhenNoSelectedTab_SelectsLastTab()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        vm.SelectedTab = null;
        vm.SelectPreviousTabCommand.Execute(null);

        Assert.Equal(@"C:\test\b.log", vm.SelectedTab!.FilePath);
    }

    [Fact]
    public async Task SessionPersistence_PreservesIsPinned()
    {
        var sessionRepo = new StubSessionRepository();
        var vm = CreateViewModel(sessionRepo: sessionRepo);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        vm.Tabs[0].IsPinned = true;
        await vm.SaveSessionAsync();

        Assert.True(sessionRepo.State.OpenTabs[0].IsPinned);
    }

    [Fact]
    public async Task GlobalAutoScrollEnabled_UpdatesAllOpenTabs()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        vm.GlobalAutoScrollEnabled = false;

        Assert.All(vm.Tabs, tab => Assert.False(tab.AutoScrollEnabled));
    }

    [Fact]
    public async Task ApplySelectedTabEncodingToAllCommand_AppliesEncodingAcrossTabs()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await vm.OpenFilePathAsync(@"C:\test\c.log");

        vm.SelectedTab = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        vm.SelectedTab!.Encoding = FileEncoding.Utf16;

        await vm.ApplySelectedTabEncodingToAllCommand.ExecuteAsync(null);

        Assert.All(vm.Tabs, tab => Assert.Equal(FileEncoding.Utf16, tab.Encoding));
    }

    // ─── Group operation tests (#8) ───────────────────────────────────────────

    [Fact]
    public async Task MoveGroupUpAsync_MovesGroupUp()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var firstId = vm.Groups[0].Id;
        var secondId = vm.Groups[1].Id;

        await vm.MoveGroupUpAsync(vm.Groups[1]);

        Assert.Equal(secondId, vm.Groups[0].Id);
        Assert.Equal(firstId, vm.Groups[1].Id);
    }

    [Fact]
    public async Task MoveGroupUpAsync_AlreadyFirst_DoesNothing()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var first = vm.Groups[0];
        var second = vm.Groups[1];

        await vm.MoveGroupUpAsync(first);

        Assert.Same(first, vm.Groups[0]);
        Assert.Same(second, vm.Groups[1]);
    }

    [Fact]
    public async Task MoveGroupDownAsync_MovesGroupDown()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var firstId = vm.Groups[0].Id;
        var secondId = vm.Groups[1].Id;

        await vm.MoveGroupDownAsync(vm.Groups[0]);

        Assert.Equal(secondId, vm.Groups[0].Id);
        Assert.Equal(firstId, vm.Groups[1].Id);
    }

    [Fact]
    public async Task MoveGroupDownAsync_AlreadyLast_DoesNothing()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var first = vm.Groups[0];
        var last = vm.Groups[1];

        await vm.MoveGroupDownAsync(last);

        Assert.Same(first, vm.Groups[0]);
        Assert.Same(last, vm.Groups[1]);
    }

    [Fact]
    public async Task ToggleGroupSelection_SingleSelect_ClearsOthers()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];

        vm.ToggleGroupSelection(g1);
        Assert.True(g1.IsSelected);
        Assert.False(g2.IsSelected);

        vm.ToggleGroupSelection(g2); // single-select: clears g1
        Assert.False(g1.IsSelected);
        Assert.True(g2.IsSelected);
    }

    [Fact]
    public async Task ToggleGroupSelection_MultiSelectFlag_IsIgnored_ForSingleActiveDashboard()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);

        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];

        vm.ToggleGroupSelection(g1);
        vm.ToggleGroupSelection(g2, isMultiSelect: true);

        Assert.False(g1.IsSelected);
        Assert.True(g2.IsSelected);
        Assert.Equal(g2.Id, vm.ActiveDashboardId);
    }

    [Fact]
    public async Task OpenDashboardFilesAsync_SkipsMissingFiles()
    {
        var fileRepo = new StubLogFileRepository();
        var vm = CreateViewModel(fileRepo: fileRepo);
        await vm.InitializeAsync();

        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];

        // Add a file entry whose path doesn't exist on disk
        var missing = new LogReader.Core.Models.LogFileEntry { FilePath = @"C:\does-not-exist-logread-test.log" };
        await fileRepo.AddAsync(missing);
        group.Model.FileIds.Add(missing.Id);

        await vm.OpenGroupFilesAsync(group);

        Assert.Empty(vm.Tabs); // missing file must not open a tab
    }

    [Fact]
    public async Task OpenGroupFilesAsync_OpensFilesInDashboardFileOrder()
    {
        var fileRepo = new StubLogFileRepository();
        var vm = CreateViewModel(fileRepo: fileRepo);
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];

        var testDir = Path.Combine(Path.GetTempPath(), "LogReaderMainVmOrder_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);
        try
        {
            var bPath = Path.Combine(testDir, "b.log");
            var aPath = Path.Combine(testDir, "a.log");
            var cPath = Path.Combine(testDir, "c.log");
            await File.WriteAllTextAsync(bPath, "b");
            await File.WriteAllTextAsync(aPath, "a");
            await File.WriteAllTextAsync(cPath, "c");

            var entryB = new LogFileEntry { FilePath = bPath };
            var entryA = new LogFileEntry { FilePath = aPath };
            var entryC = new LogFileEntry { FilePath = cPath };
            await fileRepo.AddAsync(entryB);
            await fileRepo.AddAsync(entryA);
            await fileRepo.AddAsync(entryC);

            group.Model.FileIds.Add(entryB.Id);
            group.Model.FileIds.Add(entryA.Id);
            group.Model.FileIds.Add(entryC.Id);

            await vm.OpenGroupFilesAsync(group);

            var openedPaths = vm.Tabs.Select(t => t.FilePath).ToList();
            Assert.Equal(new[] { bPath, aPath, cPath }, openedPaths);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public async Task DashboardFilter_HidesTabs_StopsTailingForHiddenTabs()
    {
        var tailService = new StubFileTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(vm.Tabs[0].FileId);
        g2.Model.FileIds.Add(vm.Tabs[1].FileId);

        vm.ToggleGroupSelection(g1);

        var tabA = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        var tabB = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        Assert.True(tabA.IsVisible);
        Assert.False(tabA.IsSuspended);
        Assert.False(tabB.IsVisible);
        Assert.True(tabB.IsSuspended);
        Assert.DoesNotContain(@"C:\test\b.log", tailService.ActiveFiles);
    }

    [Fact]
    public async Task SelectedTabChange_VisibleBackgroundTabsRemainTailedAtBackgroundRate()
    {
        var tailService = new StubFileTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        await Task.Delay(25);

        Assert.Equal(@"C:\test\b.log", vm.SelectedTab!.FilePath);
        Assert.Contains(@"C:\test\b.log", tailService.ActiveFiles);
        Assert.Contains(@"C:\test\a.log", tailService.ActiveFiles);
        Assert.Equal(250, tailService.PollingByFile[@"C:\test\b.log"]);
        Assert.Equal(2000, tailService.PollingByFile[@"C:\test\a.log"]);
    }

    [Fact]
    public async Task SelectedTabChange_SwapsActiveAndBackgroundPollingRates()
    {
        var tailService = new StubFileTailService();
        var reader = new StubLogReaderService();
        var vm = CreateViewModel(tailService: tailService, logReader: reader);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        var tabA = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        vm.SelectedTab = tabA;
        await Task.Delay(25);

        Assert.Equal(0, reader.UpdateIndexCallCount);
        Assert.Contains(@"C:\test\a.log", tailService.ActiveFiles);
        Assert.Contains(@"C:\test\b.log", tailService.ActiveFiles);
        Assert.Equal(250, tailService.PollingByFile[@"C:\test\a.log"]);
        Assert.Equal(2000, tailService.PollingByFile[@"C:\test\b.log"]);
    }

    [Fact]
    public async Task HiddenTab_BecomesVisible_ResumesOnlyWhenGlobalAutoTailEnabled()
    {
        var tailService = new StubFileTailService();
        var settingsRepo = new StubSettingsRepository
        {
            Settings = new AppSettings { GlobalAutoTailEnabled = false }
        };
        var vm = CreateViewModel(settingsRepo: settingsRepo, tailService: tailService);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(vm.Tabs[0].FileId);
        g2.Model.FileIds.Add(vm.Tabs[1].FileId);

        vm.ToggleGroupSelection(g1);
        vm.ToggleGroupSelection(g2); // single-select switches visibility

        var tabB = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        Assert.True(tabB.IsVisible);
        Assert.True(tabB.IsSuspended);
        Assert.DoesNotContain(@"C:\test\b.log", tailService.ActiveFiles);
    }

    [Fact]
    public async Task HiddenTab_BecomesVisible_ResumesWhenGlobalAutoTailEnabled()
    {
        var tailService = new StubFileTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(vm.Tabs[0].FileId);
        g2.Model.FileIds.Add(vm.Tabs[1].FileId);

        vm.ToggleGroupSelection(g1); // hides tab b
        vm.ToggleGroupSelection(g2); // shows tab b

        var tabB = vm.Tabs.First(t => t.FilePath == @"C:\test\b.log");
        Assert.True(tabB.IsVisible);
        Assert.False(tabB.IsSuspended);
        Assert.Contains(@"C:\test\b.log", tailService.ActiveFiles);
    }

    [Fact]
    public async Task LifecycleMaintenance_PurgesOldHiddenTabs_ButKeepsPinned()
    {
        var tailService = new StubFileTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();
        vm.HiddenTabPurgeAfter = TimeSpan.Zero;

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(vm.Tabs[0].FileId);
        g2.Model.FileIds.Add(vm.Tabs[1].FileId);

        var tabA = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log");
        tabA.IsPinned = true;

        vm.ToggleGroupSelection(g2); // hides tabA
        vm.RunTabLifecycleMaintenance();

        Assert.Equal(2, vm.Tabs.Count); // pinned tab is preserved

        tabA.IsPinned = false;
        vm.RunTabLifecycleMaintenance();

        Assert.Single(vm.Tabs);
        Assert.Equal(@"C:\test\b.log", vm.Tabs[0].FilePath);
    }

    [Fact]
    public async Task LifecycleMaintenance_Purge_UpdatesSavedSessionState()
    {
        var sessionRepo = new StubSessionRepository();
        var vm = CreateViewModel(sessionRepo: sessionRepo);
        await vm.InitializeAsync();
        vm.HiddenTabPurgeAfter = TimeSpan.Zero;

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(vm.Tabs[0].FileId);
        g2.Model.FileIds.Add(vm.Tabs[1].FileId);

        vm.ToggleGroupSelection(g2); // hides tab a
        vm.RunTabLifecycleMaintenance();
        await Task.Delay(25); // SaveSessionAsync is fire-and-forget from maintenance

        Assert.Single(vm.Tabs);
        Assert.Single(sessionRepo.State.OpenTabs);
        Assert.Equal(@"C:\test\b.log", sessionRepo.State.OpenTabs[0].FilePath);
    }

    [Fact]
    public async Task LifecycleMaintenance_Purge_RemovesTabOrderingMetadata()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.HiddenTabPurgeAfter = TimeSpan.Zero;

        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];
        g1.Model.FileIds.Add(vm.Tabs[0].FileId);
        g2.Model.FileIds.Add(vm.Tabs[1].FileId);

        var tabAId = vm.Tabs.First(t => t.FilePath == @"C:\test\a.log").FileId;
        vm.ToggleGroupSelection(g2); // hides tab a

        var openOrder = GetTabOrderMap(vm, "_tabOpenOrder");
        var pinOrder = GetTabOrderMap(vm, "_tabPinOrder");
        Assert.Contains(tabAId, openOrder.Keys);

        vm.RunTabLifecycleMaintenance();

        Assert.DoesNotContain(tabAId, openOrder.Keys);
        Assert.DoesNotContain(tabAId, pinOrder.Keys);
    }

    [Fact]
    public async Task GlobalAutoTailSettingChange_IsAppliedToVisibleTabs()
    {
        var tailService = new StubFileTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];
        group.Model.FileIds.Add(vm.Tabs[0].FileId);

        var settingsField = typeof(MainViewModel).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(settingsField);
        settingsField!.SetValue(vm, new AppSettings { GlobalAutoTailEnabled = false });

        vm.ToggleGroupSelection(group); // triggers visibility refresh for visible tabs

        var tab = vm.Tabs[0];
        Assert.True(tab.IsVisible);
        Assert.True(tab.IsSuspended);
        Assert.DoesNotContain(@"C:\test\a.log", tailService.ActiveFiles);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WhenLifecycleTimerEnabled()
    {
        var vm = new MainViewModel(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
            new StubSessionRepository(),
            new StubSettingsRepository(),
            new StubLogReaderService(),
            new StubSearchService(),
            new StubFileTailService(),
            enableLifecycleTimer: true);

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public void PaneState_DefaultsToBothOpen()
    {
        var vm = CreateViewModel();

        Assert.True(vm.IsGroupsPanelOpen);
        Assert.True(vm.IsSearchPanelOpen);
    }

    [Fact]
    public void ToggleFocusMode_TogglesBothPanes()
    {
        var vm = CreateViewModel();

        vm.ToggleFocusModeCommand.Execute(null);
        Assert.False(vm.IsGroupsPanelOpen);
        Assert.False(vm.IsSearchPanelOpen);

        vm.ToggleFocusModeCommand.Execute(null);
        Assert.True(vm.IsGroupsPanelOpen);
        Assert.True(vm.IsSearchPanelOpen);
    }

    [Fact]
    public void RememberPanelWidths_IgnoresSmallValues()
    {
        var vm = CreateViewModel();

        vm.RememberGroupsPanelWidth(280);
        vm.RememberSearchPanelWidth(410);
        vm.RememberGroupsPanelWidth(30);
        vm.RememberSearchPanelWidth(20);

        Assert.Equal(280, vm.GroupsPanelWidth);
        Assert.Equal(410, vm.SearchPanelWidth);
    }
}
