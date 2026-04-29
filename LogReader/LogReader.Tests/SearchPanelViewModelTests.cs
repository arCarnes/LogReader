using LogReader.App.Models;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

namespace LogReader.Tests;

public class SearchPanelViewModelTests
{
    private const string ScopeExitCancelledStatusText = "Search stopped when leaving this scope. Rerun search to refresh these results.";
    private const string SelectedTabChangedStatusText = "Search results cleared because the selected tab changed. Rerun search to refresh.";
    private const string SearchOutputStaleStatusText = "Search output is for a previous context, target, or source. Rerun search to refresh.";

    private sealed class StubLogFileRepository : ILogFileRepository
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

    private sealed class StubLogGroupRepository : ILogGroupRepository
    {
        private readonly List<LogGroup> _groups = new();

        public Task<List<LogGroup>> GetAllAsync() => Task.FromResult(_groups.ToList());
        public Task<LogGroup?> GetByIdAsync(string id) => Task.FromResult(_groups.FirstOrDefault(g => g.Id == id));
        public Task AddAsync(LogGroup group) { _groups.Add(group); return Task.CompletedTask; }
        public Task ReplaceAllAsync(IReadOnlyList<LogGroup> groups)
        {
            _groups.Clear();
            _groups.AddRange(groups);
            return Task.CompletedTask;
        }
        public Task UpdateAsync(LogGroup group) => Task.CompletedTask;
        public Task DeleteAsync(string id) { _groups.RemoveAll(g => g.Id == id); return Task.CompletedTask; }
        public Task ReorderAsync(List<string> orderedIds) => Task.CompletedTask;
        public Task ExportViewAsync(string exportPath) => Task.CompletedTask;
        public Task<ViewExport?> ImportViewAsync(string importPath) => Task.FromResult<ViewExport?>(null);
    }

    private sealed class StubSettingsRepository : ISettingsRepository
    {
        public AppSettings Settings { get; set; } = new();
        public Task<AppSettings> LoadAsync() => Task.FromResult(Settings);
        public Task SaveAsync(AppSettings settings) { Settings = settings; return Task.CompletedTask; }
    }

    private sealed class RecordingSearchService : ISearchService
    {
        public SearchRequest? LastRequest { get; private set; }
        public IDictionary<string, FileEncoding>? LastEncodings { get; private set; }
        public IReadOnlyList<SearchResult> NextResults { get; set; } = Array.Empty<SearchResult>();
        public Func<string, SearchRequest, SearchResult>? SearchFileHandler { get; set; }
        public Func<string, SearchRequest, FileEncoding, CancellationToken, Task<SearchResult>>? SearchFileAsyncHandler { get; set; }
        public Func<string, SearchRequest, FileEncoding, Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>>, CancellationToken, Task<SearchResult>>? SearchFileRangeAsyncHandler { get; set; }
        public Func<SearchRequest, IDictionary<string, FileEncoding>, CancellationToken, Task<IReadOnlyList<SearchResult>>>? SearchFilesAsyncHandler { get; set; }
        public int SearchFilesCallCount { get; private set; }
        public int SearchFileCallCount { get; private set; }
        public int SearchFileRangeCallCount { get; private set; }
        public List<SearchRequest> SearchFileRequests { get; } = new();
        public List<SearchRequest> SearchFileRangeRequests { get; } = new();

        public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
        {
            SearchFileCallCount++;
            SearchFileRequests.Add(CloneSearchRequest(request));
            if (SearchFileAsyncHandler != null)
                return SearchFileAsyncHandler(filePath, request, encoding, ct);

            if (SearchFileHandler != null)
                return Task.FromResult(SearchFileHandler(filePath, request));

            return Task.FromResult(new SearchResult { FilePath = filePath });
        }

        public Task<SearchResult> SearchFileRangeAsync(
            string filePath,
            SearchRequest request,
            FileEncoding encoding,
            Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
            CancellationToken ct = default)
        {
            SearchFileRangeCallCount++;
            SearchFileRangeRequests.Add(CloneSearchRequest(request));
            SearchFileCallCount++;
            SearchFileRequests.Add(CloneSearchRequest(request));
            if (SearchFileRangeAsyncHandler != null)
                return SearchFileRangeAsyncHandler(filePath, request, encoding, readLinesAsync, ct);

            if (SearchFileAsyncHandler != null)
                return SearchFileAsyncHandler(filePath, request, encoding, ct);

            if (SearchFileHandler != null)
                return Task.FromResult(SearchFileHandler(filePath, request));

            return Task.FromResult(new SearchResult { FilePath = filePath });
        }

        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default)
        {
            SearchFilesCallCount++;
            LastRequest = CloneSearchRequest(request);
            LastEncodings = new Dictionary<string, FileEncoding>(fileEncodings, StringComparer.OrdinalIgnoreCase);
            if (SearchFilesAsyncHandler != null)
                return SearchFilesAsyncHandler(request, LastEncodings, ct);

            return Task.FromResult(NextResults);
        }

        private static SearchRequest CloneSearchRequest(SearchRequest request)
            => request.Clone();
    }

    private sealed class TailScopeLookupWorkspaceContextStub : ILogWorkspaceContext
    {
        private readonly LogTabViewModel _tab;
        private readonly WorkspaceScopeSnapshot _scopeSnapshot;
        private readonly LogFilterSession.FilterSnapshot? _scopeSnapshotForFile;

        public TailScopeLookupWorkspaceContextStub(LogTabViewModel tab, LogFilterSession.FilterSnapshot? scopeSnapshotForFile)
        {
            _tab = tab;
            _scopeSnapshotForFile = scopeSnapshotForFile;
            _scopeSnapshot = new WorkspaceScopeSnapshot(
                WorkspaceScopeKey.FromDashboardId(null),
                new[] { new WorkspaceOpenTabSnapshot(tab) },
                new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        }

        public string? ActiveScopeDashboardId => null;

        public bool IsDashboardLoading => false;

        public LogTabViewModel? SelectedTab => _tab;

        public IReadOnlyList<LogTabViewModel> GetAllTabs() => new[] { _tab };

        public IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot() => new[] { _tab };

        public IReadOnlyList<string> GetSearchResultFileOrderSnapshot() => new[] { _tab.FilePath };

        public IReadOnlyList<string> GetAllOpenTabsExecutionFileOrderSnapshot(string? scopeDashboardId)
            => string.Equals(scopeDashboardId, ActiveScopeDashboardId, StringComparison.Ordinal)
                ? GetSearchResultFileOrderSnapshot()
                : Array.Empty<string>();

        public WorkspaceScopeSnapshot GetActiveScopeSnapshot() => _scopeSnapshot;

        public Task<FileEncoding> ResolveFilterFileEncodingAsync(string filePath, string? scopeDashboardId, CancellationToken ct = default)
            => Task.FromResult(FileEncoding.Utf8);

        public LogFilterSession.FilterSnapshot? GetApplicableCurrentTabFilterSnapshot(SearchDataMode sourceMode)
            => null;

        public LogFilterSession.FilterSnapshot? GetApplicableAllOpenTabsFilterSnapshot(string filePath, SearchDataMode sourceMode)
        {
            if (!string.Equals(filePath, _tab.FilePath, StringComparison.OrdinalIgnoreCase))
                return null;

            return _scopeSnapshotForFile == null
                ? null
                : LogFilterSession.CloneSnapshot(_scopeSnapshotForFile);
        }

        public IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableAllOpenTabsFilterSnapshots(SearchDataMode sourceMode)
            => throw new InvalidOperationException("Bulk all-open-tabs snapshot lookup should not be used by tail search single-file refresh.");

        public void UpdateRecentTabFilterSnapshot(string filePath, string? scopeDashboardId, LogFilterSession.FilterSnapshot? snapshot)
        {
        }

        public Task RunViewActionAsync(Func<Task> operation, string failureCaption = "LogReader Error")
            => operation();

        public Task NavigateToLineAsync(
            string filePath,
            long lineNumber,
            bool disableAutoScroll = false,
            bool suppressDuringDashboardLoad = false)
            => Task.CompletedTask;
    }

    private sealed class ScopeWorkspaceContextStub : ILogWorkspaceContext
    {
        private readonly IReadOnlyList<LogTabViewModel> _tabs;
        private readonly WorkspaceScopeSnapshot _scopeSnapshot;
        private readonly IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> _filterSnapshots;

        public ScopeWorkspaceContextStub(
            LogTabViewModel selectedTab,
            IReadOnlyList<WorkspaceScopeMemberSnapshot> scopeMembership,
            IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot>? filterSnapshots = null)
        {
            _tabs = new[] { selectedTab };
            _scopeSnapshot = new WorkspaceScopeSnapshot(
                WorkspaceScopeKey.FromDashboardId(null),
                _tabs.Select(tab => new WorkspaceOpenTabSnapshot(tab)).ToList(),
                scopeMembership);
            _filterSnapshots = filterSnapshots ?? new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);
            SelectedTab = selectedTab;
        }

        public string? ActiveScopeDashboardId => null;

        public bool IsDashboardLoading => false;

        public LogTabViewModel? SelectedTab { get; }

        public IReadOnlyList<LogTabViewModel> GetAllTabs() => _tabs;

        public IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot() => _tabs;

        public IReadOnlyList<string> GetSearchResultFileOrderSnapshot()
            => _scopeSnapshot.EffectiveMembership.Select(member => member.FilePath).ToList();

        public IReadOnlyList<string> GetAllOpenTabsExecutionFileOrderSnapshot(string? scopeDashboardId)
            => string.Equals(scopeDashboardId, ActiveScopeDashboardId, StringComparison.Ordinal)
                ? GetSearchResultFileOrderSnapshot()
                : Array.Empty<string>();

        public WorkspaceScopeSnapshot GetActiveScopeSnapshot() => _scopeSnapshot;

        public Task<FileEncoding> ResolveFilterFileEncodingAsync(string filePath, string? scopeDashboardId, CancellationToken ct = default)
            => Task.FromResult(FileEncoding.Utf8);

        public LogFilterSession.FilterSnapshot? GetApplicableCurrentTabFilterSnapshot(SearchDataMode sourceMode)
            => SelectedTab != null && _filterSnapshots.TryGetValue(SelectedTab.FilePath, out var snapshot)
                ? snapshot
                : null;

        public LogFilterSession.FilterSnapshot? GetApplicableAllOpenTabsFilterSnapshot(string filePath, SearchDataMode sourceMode)
            => _filterSnapshots.TryGetValue(filePath, out var snapshot)
                ? snapshot
                : null;

        public IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableAllOpenTabsFilterSnapshots(SearchDataMode sourceMode)
            => _filterSnapshots;

        public void UpdateRecentTabFilterSnapshot(string filePath, string? scopeDashboardId, LogFilterSession.FilterSnapshot? snapshot)
        {
        }

        public Task RunViewActionAsync(Func<Task> operation, string failureCaption = "LogReader Error")
            => operation();

        public Task NavigateToLineAsync(
            string filePath,
            long lineNumber,
            bool disableAutoScroll = false,
            bool suppressDuringDashboardLoad = false)
            => Task.CompletedTask;
    }

    private static LogTabViewModel CreateTab(string fileId, string filePath)
    {
        return new LogTabViewModel(
            fileId,
            filePath,
            new StubLogReaderService(),
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());
    }

    private static MainViewModel CreateMainViewModel(ILogFileRepository fileRepo, ILogGroupRepository groupRepo, ISettingsRepository settingsRepo, ISearchService search)
        => CreateMainViewModel(fileRepo, groupRepo, settingsRepo, search, new StubLogReaderService());

    private static MainViewModel CreateMainViewModel(
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo,
        ISettingsRepository settingsRepo,
        ISearchService search,
        ILogReaderService logReader)
    {
        return new MainViewModel(
            fileRepo,
            groupRepo,
            settingsRepo,
            logReader,
            search,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            enableLifecycleTimer: false);
    }

    [Fact]
    public void SearchAndFilterPanels_KeepCheckboxOptionFlagsIndependent()
    {
        var tab = CreateTab("file-1", @"C:\logs\app.log");
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var sharedOptions = new SearchFilterSharedOptions();
        using var search = new SearchPanelViewModel(new RecordingSearchService(), workspace, sharedOptions);
        using var filter = new FilterPanelViewModel(new RecordingSearchService(), workspace, sharedOptions);

        search.IsRegex = true;
        search.CaseSensitive = true;
        filter.IsRegex = false;
        filter.CaseSensitive = false;

        Assert.True(search.IsRegex);
        Assert.True(search.CaseSensitive);
        Assert.False(filter.IsRegex);
        Assert.False(filter.CaseSensitive);

        filter.IsRegex = true;
        filter.CaseSensitive = true;

        Assert.True(search.IsRegex);
        Assert.True(search.CaseSensitive);
        Assert.True(filter.IsRegex);
        Assert.True(filter.CaseSensitive);
    }

    private sealed class RecordingLogReaderService : ILogReaderService
    {
        private readonly List<string> _lines;

        public RecordingLogReaderService(IEnumerable<string> lines)
        {
            _lines = lines.ToList();
        }

        public List<(int StartLine, int Count)> ReadLinesRequests { get; } = new();

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = _lines.Count * 100
            };

            for (var i = 0; i < _lines.Count; i++)
                index.LineOffsets.Add(i * 100L);

            return Task.FromResult(index);
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
        {
            ReadLinesRequests.Add((startLine, count));
            var lines = _lines.Skip(Math.Max(0, startLine)).Take(Math.Max(0, count)).ToList();
            return Task.FromResult<IReadOnlyList<string>>(lines);
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(_lines[Math.Max(0, Math.Min(_lines.Count - 1, lineNumber))]);
    }

    private static Task InvokeExecuteSearchAsync(SearchPanelViewModel panel)
    {
        var method = typeof(SearchPanelViewModel).GetMethod("ExecuteSearch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task)method!.Invoke(panel, null)!;
    }

    private static Task InvokeNavigateToHitAsync(FileSearchResultViewModel fileResult, SearchHitViewModel hit)
    {
        var method = typeof(FileSearchResultViewModel).GetMethod("NavigateToHit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task)method!.Invoke(fileResult, new object?[] { hit })!;
    }

    private static SearchResult CreateSearchResult(string filePath, int lineNumber, string lineText)
        => new()
        {
            FilePath = filePath,
            Hits = new List<SearchHit>
            {
                new()
                {
                    LineNumber = lineNumber,
                    LineText = lineText,
                    MatchStart = 0,
                    MatchLength = 1
                }
            }
        };

    private static LogTabViewModel FindScopedTab(MainViewModel viewModel, string filePath, string? scopeDashboardId)
    {
        return viewModel.Tabs.Single(tab =>
            string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteSearch_CurrentFile_UsesSelectedTabPathAndEncoding()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");
        mainVm.SelectedTab!.Encoding = FileEncoding.Utf16Be;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error"
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastRequest);
        Assert.Equal(new[] { @"C:\logs\b.log" }, search.LastRequest!.FilePaths);
        Assert.Equal(10_000, search.LastRequest.MaxHitsPerFile);
        Assert.Null(search.LastRequest.MaxRetainedLineTextLength);
        Assert.NotNull(search.LastEncodings);
        Assert.Equal(FileEncoding.Utf16Be, search.LastEncodings![@"C:\logs\b.log"]);
    }

    [Fact]
    public async Task ExecuteSearch_CappedResult_ShowsCapStatus()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    HitLimitExceeded = true,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "error", MatchStart = 0, MatchLength = 5 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error"
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Contains("Results capped", panel.ResultsHeaderText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSearch_SearchWithinFilter_ReusesSnapshotLineNumberList()
    {
        var tab = CreateTab("file-1", @"C:\logs\a.log");
        var matchingLineNumbers = Enumerable.Range(1, 10_000).ToArray();
        var snapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = matchingLineNumbers,
            StatusText = "Filter active",
            FilterRequest = new SearchRequest
            {
                Query = "WARN",
                FilePaths = new List<string> { tab.FilePath },
                SourceMode = SearchRequestSourceMode.DiskSnapshot,
                Usage = SearchRequestUsage.FilterApply
            }
        };
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) },
            new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [tab.FilePath] = snapshot
            });
        IReadOnlyList<int>? requestAllowedLines = null;
        var search = new RecordingSearchService
        {
            SearchFilesAsyncHandler = (request, _, _) =>
            {
                requestAllowedLines = request.AllowedLineNumbersByFilePath[tab.FilePath];
                return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
            }
        };
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "error",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Same(matchingLineNumbers, requestAllowedLines);
    }

    [Fact]
    public async Task ExecuteSearch_AllOpenTabs_UsesAllOpenTabs()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");
        mainVm.Tabs[0].Encoding = FileEncoding.Ansi;
        mainVm.Tabs[1].Encoding = FileEncoding.Utf16;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "warn",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastRequest);
        Assert.Equal(2, search.LastRequest!.FilePaths.Count);
        Assert.Equal(FileEncoding.Ansi, search.LastEncodings![@"C:\logs\a.log"]);
        Assert.Equal(FileEncoding.Utf16, search.LastEncodings![@"C:\logs\b.log"]);
    }

    [Fact]
    public async Task ExecuteSearch_AllOpenTabs_UsesOnlyTabsVisibleInActiveScope()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        await mainVm.CreateGroupCommand.ExecuteAsync(null);

        var dashboardA = mainVm.Groups[0];
        var dashboardB = mainVm.Groups[1];
        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");
        dashboardA.Model.FileIds.Add(tabA.FileId);
        dashboardB.Model.FileIds.Add(tabB.FileId);

        mainVm.ToggleGroupSelection(dashboardB);
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "warn",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastRequest);
        Assert.Equal(new[] { @"C:\logs\b.log" }, search.LastRequest!.FilePaths);
    }

    [Fact]
    public async Task ExecuteSearch_AllOpenTabs_DashboardScope_OrdersResultsByDashboardMemberOrder()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }
                    }
                },
                new SearchResult
                {
                    FilePath = @"C:\logs\b.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "B hit", MatchStart = 0, MatchLength = 1 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = Assert.Single(mainVm.Groups);
        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");
        dashboard.Model.FileIds.Add(tabB.FileId);
        dashboard.Model.FileIds.Add(tabA.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (tabB.FileId, tabB.FilePath),
            (tabA.FileId, tabA.FilePath));

        mainVm.ToggleGroupSelection(dashboard);
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "warn",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { @"C:\logs\b.log", @"C:\logs\a.log" },
            panel.Results.Select(result => result.FilePath).ToArray());
    }

    [Fact]
    public async Task ExecuteSearch_AllOpenTabs_DashboardScope_PinnedTabs_DoNotChangeDashboardMemberOrder()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }
                    }
                },
                new SearchResult
                {
                    FilePath = @"C:\logs\b.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "B hit", MatchStart = 0, MatchLength = 1 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = Assert.Single(mainVm.Groups);
        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");
        dashboard.Model.FileIds.Add(tabB.FileId);
        dashboard.Model.FileIds.Add(tabA.FileId);
        RefreshDashboardMemberFiles(
            dashboard,
            (tabB.FileId, tabB.FilePath),
            (tabA.FileId, tabA.FilePath));

        mainVm.ToggleGroupSelection(dashboard);
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");
        mainVm.TogglePinTab(mainVm.Tabs.First(tab =>
            string.Equals(tab.ScopeDashboardId, dashboard.Id, StringComparison.Ordinal) &&
            string.Equals(tab.FilePath, @"C:\logs\a.log", StringComparison.OrdinalIgnoreCase)));

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "warn",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { @"C:\logs\b.log", @"C:\logs\a.log" },
            panel.Results.Select(result => result.FilePath).ToArray());
    }

    [Fact]
    public async Task ExecuteSearch_AllOpenTabs_ModifierDashboard_OrdersResultsByResolvedMemberOrder()
    {
        var dateSuffix = DateTime.Today.AddDays(-1).ToString("yyyyMMdd");
        var modifiedPathA = $@"C:\logs\a.log.{dateSuffix}";
        var modifiedPathB = $@"C:\logs\b.log.{dateSuffix}";
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = modifiedPathB,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "B hit", MatchStart = 0, MatchLength = 1 }
                    }
                },
                new SearchResult
                {
                    FilePath = modifiedPathA,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }
                    }
                }
            }
        };
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        await fileRepo.AddAsync(new LogFileEntry { FilePath = @"C:\logs\a.log" });
        await fileRepo.AddAsync(new LogFileEntry { FilePath = @"C:\logs\b.log" });
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = Assert.Single(mainVm.Groups);
        var fileA = (await fileRepo.GetByPathsAsync(new[] { @"C:\logs\a.log" }))[@"C:\logs\a.log"];
        var fileB = (await fileRepo.GetByPathsAsync(new[] { @"C:\logs\b.log" }))[@"C:\logs\b.log"];
        dashboard.Model.FileIds.Add(fileB.Id);
        dashboard.Model.FileIds.Add(fileA.Id);
        await mainVm.ApplyDashboardModifierAsync(
            dashboard,
            daysBack: 1,
            new ReplacementPattern
            {
                Id = "pattern-1",
                FindPattern = ".log",
                ReplacePattern = ".log.{yyyyMMdd}"
            });

        mainVm.ToggleGroupSelection(dashboard);
        await mainVm.OpenFilePathAsync(modifiedPathA);
        await mainVm.OpenFilePathAsync(modifiedPathB);
        mainVm.TogglePinTab(mainVm.Tabs.First(tab =>
            string.Equals(tab.ScopeDashboardId, dashboard.Id, StringComparison.Ordinal) &&
            string.Equals(tab.FilePath, modifiedPathA, StringComparison.OrdinalIgnoreCase)));

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "warn",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { modifiedPathB, modifiedPathA },
            panel.Results.Select(result => result.FilePath).ToArray());
    }

    [Fact]
    public async Task ExecuteSearch_AllOpenTabs_AdHocScope_OrdersResultsByVisibleTabOrder()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }
                    }
                },
                new SearchResult
                {
                    FilePath = @"C:\logs\b.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "B hit", MatchStart = 0, MatchLength = 1 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "warn",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { @"C:\logs\b.log", @"C:\logs\a.log" },
            panel.Results.Select(result => result.FilePath).ToArray());
    }

    [Fact]
    public async Task ExecuteSearch_AllOpenTabs_BatchesVisibleRowsForSnapshotResultPayload()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                CreateSearchResult(@"C:\logs\c.log", 3, "C hit"),
                CreateSearchResult(@"C:\logs\a.log", 1, "A hit"),
                CreateSearchResult(@"C:\logs\b.log", 2, "B hit")
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\c.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "warn",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };
        var collectionChanges = 0;
        var resultCollectionChanges = 0;
        panel.VisibleRows.CollectionChanged += (_, _) => collectionChanges++;
        panel.Results.CollectionChanged += (_, _) => resultCollectionChanges++;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { @"C:\logs\a.log", @"C:\logs\b.log", @"C:\logs\c.log" },
            panel.Results.Select(result => result.FilePath).ToArray());
        Assert.Equal(3, panel.VisibleRows.Count);
        Assert.Equal(
            new[] { @"C:\logs\a.log", @"C:\logs\b.log", @"C:\logs\c.log" },
            panel.VisibleRows
                .Cast<object>()
                .OfType<SearchResultFileHeaderRowViewModel>()
                .Select(row => row.FileResult.FilePath)
                .ToArray());
        Assert.Equal(2, collectionChanges);
        Assert.Equal(2, resultCollectionChanges);
    }

    [Fact]
    public async Task ExecuteSearch_CurrentFile_DoesNotIncludeOtherOpenTabs()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "fatal"
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastRequest);
        Assert.Equal(new[] { @"C:\logs\b.log" }, search.LastRequest!.FilePaths);
    }

    [Fact]
    public async Task SearchScratchpad_ScopeSwitch_RestoresPerScopeInputsAndResults()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = Assert.Single(mainVm.Groups);
        var adHocTabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);

        var panel = mainVm.SearchPanel;
        search.NextResults = new[]
        {
            new SearchResult
            {
                FilePath = adHocTabB.FilePath,
                HasParseableTimestamps = true,
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 12, LineText = "adhoc hit", MatchStart = 0, MatchLength = 5 }
                }
            }
        };
        panel.Query = "adhoc-state";
        panel.IsRegex = true;
        panel.CaseSensitive = true;
        panel.FromTimestamp = "2026-03-09 19:49:10";
        panel.ToTimestamp = "2026-03-09 19:49:20";

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal("1 in 1 file(s)", panel.ResultsHeaderText);

        mainVm.ToggleGroupSelection(dashboard);
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");
        var dashboardTabB = FindScopedTab(mainVm, @"C:\logs\b.log", dashboard.Id);

        search.NextResults = new[]
        {
            new SearchResult
            {
                FilePath = dashboardTabB.FilePath,
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 33, LineText = "dashboard hit", MatchStart = 0, MatchLength = 9 }
                }
            }
        };
        panel.Query = "dashboard-state";
        panel.IsRegex = false;
        panel.CaseSensitive = false;
        panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
        panel.FromTimestamp = string.Empty;
        panel.ToTimestamp = string.Empty;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal("1 in 1 file(s)", panel.ResultsHeaderText);

        mainVm.ToggleGroupSelection(dashboard);

        Assert.Equal("adhoc-state", panel.Query);
        Assert.True(panel.IsRegex);
        Assert.True(panel.CaseSensitive);
        Assert.Equal(SearchFilterTargetMode.CurrentTab, panel.TargetMode);
        Assert.Equal("2026-03-09 19:49:10", panel.FromTimestamp);
        Assert.Equal("2026-03-09 19:49:20", panel.ToTimestamp);
        Assert.Equal(SelectedTabChangedStatusText, panel.ResultsHeaderText);
        Assert.Empty(panel.Results);

        mainVm.SelectedTab = adHocTabB;
        Assert.Equal(SelectedTabChangedStatusText, panel.ResultsHeaderText);
        Assert.Empty(panel.Results);

        mainVm.ToggleGroupSelection(dashboard);

        Assert.Equal("dashboard-state", panel.Query);
        Assert.False(panel.IsRegex);
        Assert.False(panel.CaseSensitive);
        Assert.Equal(SearchFilterTargetMode.AllOpenTabs, panel.TargetMode);
        Assert.Equal(string.Empty, panel.FromTimestamp);
        Assert.Equal(string.Empty, panel.ToTimestamp);
        Assert.Equal("1 in 1 file(s)", panel.ResultsHeaderText);
        Assert.Equal(new long[] { 33 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public async Task SearchScratchpad_CurrentFile_SelectedTabChangesClearResultsUntilOriginalTabReturns()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\b.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 7, LineText = "selected hit", MatchStart = 0, MatchLength = 6 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var panel = mainVm.SearchPanel;
        panel.Query = "selected";

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var originalTab = mainVm.SelectedTab!;
        mainVm.SelectedTab = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        Assert.Equal(SelectedTabChangedStatusText, panel.ResultsHeaderText);
        Assert.Empty(panel.Results);

        mainVm.SelectedTab = originalTab;
        Assert.Equal(SelectedTabChangedStatusText, panel.ResultsHeaderText);
        Assert.Empty(panel.Results);
    }

    [Fact]
    public async Task SearchScratchpad_DiskResults_TargetChange_KeepsVisibleResults()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\b.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 7, LineText = "selected hit", MatchStart = 0, MatchLength = 6 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var panel = mainVm.SearchPanel;
        panel.Query = "selected";

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var baseStatus = panel.ResultsHeaderText;

        panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
        Assert.Equal(new long[] { 7 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());

        panel.TargetMode = SearchFilterTargetMode.CurrentTab;
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
        Assert.Equal(new long[] { 7 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public async Task SearchScratchpad_DiskResults_SourceModeChange_KeepsVisibleResults()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\b.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 7, LineText = "selected hit", MatchStart = 0, MatchLength = 6 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var panel = mainVm.SearchPanel;
        panel.Query = "selected";

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var baseStatus = panel.ResultsHeaderText;

        panel.IsTailMode = true;
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
        Assert.Equal(new long[] { 7 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());

        panel.IsDiskSnapshotMode = true;
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
        Assert.Equal(new long[] { 7 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public async Task SearchScratchpad_TailSelectedTabChange_RestoresResultsWithRerunStatus()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");
        mainVm.SelectedTab = tabA;
        tabA.TotalLines = 10;

        search.SearchFileHandler = (_, request) => new SearchResult
        {
            FilePath = tabA.FilePath,
            Hits = new List<SearchHit>
            {
                new()
                {
                    LineNumber = request.EndLineNumber ?? -1,
                    LineText = "tail hit",
                    MatchStart = 0,
                    MatchLength = 4
                }
            }
        };

        var panel = mainVm.SearchPanel;
        panel.Query = "tail-hit";
        panel.IsTailMode = true;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        tabA.TotalLines = 11;
        await WaitForConditionAsync(() =>
            search.SearchFileCallCount == 1 &&
            panel.Results.Count == 1 &&
            panel.Results[0].Hits[0].LineNumber == 11);

        var searchCallsAfterTabAHit = search.SearchFileCallCount;

        mainVm.SelectedTab = tabB;
        tabA.TotalLines = 12;
        await Task.Delay(500);
        Assert.Equal(searchCallsAfterTabAHit, search.SearchFileCallCount);

        mainVm.SelectedTab = tabA;

        Assert.False(panel.IsSearching);
        Assert.Equal(SelectedTabChangedStatusText, panel.ResultsHeaderText);
        Assert.Equal(string.Empty, panel.StatusText);
        Assert.Empty(panel.Results);

        tabA.TotalLines = 13;
        await Task.Delay(500);
        Assert.Equal(searchCallsAfterTabAHit, search.SearchFileCallCount);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_AllOpenTabs_DoesNotBackgroundOpenMissingScopeMembers()
    {
        using var selectedTab = CreateTab("selected", @"C:\logs\selected.log");
        using var scopeTab = CreateTab("scope", @"C:\logs\scope.log");
        var workspace = new ScopeWorkspaceContextStub(
            selectedTab,
            new[] { new WorkspaceScopeMemberSnapshot(scopeTab.FileId, scopeTab.FilePath) });
        var search = new RecordingSearchService();
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "scope",
            TargetMode = SearchFilterTargetMode.AllOpenTabs,
            SearchDataMode = SearchDataMode.Tail
        };

        await InvokeExecuteSearchAsync(panel);

        Assert.Equal(0, search.SearchFilesCallCount);
        Assert.Equal(0, search.SearchFileCallCount);

        scopeTab.TotalLines = 10;
        await Task.Delay(300);
        Assert.Equal(0, search.SearchFileCallCount);

        selectedTab.TotalLines = 10;
        await WaitForConditionAsync(() => search.SearchFileCallCount == 1);

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task SearchScratchpad_AllOpenTabs_UnrelatedOpenTabChangesDoNotClearResults()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }
                    }
                },
                new SearchResult
                {
                    FilePath = @"C:\logs\b.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var panel = mainVm.SearchPanel;
        panel.Query = "scope";
        panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var baseStatus = panel.ResultsHeaderText;

        await mainVm.OpenFilePathAsync(@"C:\logs\c.log");
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
        Assert.Equal(2, panel.Results.Count);

        await mainVm.CloseTabCommand.ExecuteAsync(mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\c.log" && tab.IsAdHocScope));
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
    }

    [Fact]
    public async Task SearchScratchpad_AllOpenTabs_DashboardPinningDoesNotClearResults()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }
                    }
                },
                new SearchResult
                {
                    FilePath = @"C:\logs\b.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = Assert.Single(mainVm.Groups);
        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");
        dashboard.Model.FileIds.Add(tabB.FileId);
        dashboard.Model.FileIds.Add(tabA.FileId);

        mainVm.ToggleGroupSelection(dashboard);
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var panel = mainVm.SearchPanel;
        panel.Query = "scope";
        panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var baseStatus = panel.ResultsHeaderText;
        var baseResultPaths = panel.Results.Select(result => result.FilePath).ToArray();
        var dashboardTabA = mainVm.Tabs.First(tab =>
            string.Equals(tab.ScopeDashboardId, dashboard.Id, StringComparison.Ordinal) &&
            string.Equals(tab.FilePath, @"C:\logs\a.log", StringComparison.OrdinalIgnoreCase));

        mainVm.TogglePinTab(dashboardTabA);
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
        Assert.Equal(new[] { @"C:\logs\a.log", @"C:\logs\b.log" }, mainVm.FilteredTabs.Select(tab => tab.FilePath).ToArray());
        Assert.Equal(baseResultPaths, panel.Results.Select(result => result.FilePath).ToArray());

        mainVm.TogglePinTab(dashboardTabA);
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
        Assert.Equal(baseResultPaths, panel.Results.Select(result => result.FilePath).ToArray());
    }

    [Fact]
    public async Task SearchScratchpad_AllOpenTabs_DashboardReorderDoesNotClearResults()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "A hit", MatchStart = 0, MatchLength = 1 }
                    }
                },
                new SearchResult
                {
                    FilePath = @"C:\logs\b.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 2, LineText = "B hit", MatchStart = 0, MatchLength = 1 }
                    }
                },
                new SearchResult
                {
                    FilePath = @"C:\logs\c.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 3, LineText = "C hit", MatchStart = 0, MatchLength = 1 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\c.log");

        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");
        var tabC = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\c.log");

        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = Assert.Single(mainVm.Groups);
        dashboard.Model.FileIds.Add(tabA.FileId);
        dashboard.Model.FileIds.Add(tabB.FileId);
        dashboard.Model.FileIds.Add(tabC.FileId);
        mainVm.ToggleGroupSelection(dashboard);
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\c.log");

        var panel = mainVm.SearchPanel;
        panel.Query = "scope";
        panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var baseStatus = panel.ResultsHeaderText;

        await mainVm.ReorderDashboardFileAsync(dashboard, tabC.FileId, tabA.FileId, DropPlacement.Before);
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
        Assert.Equal(3, panel.Results.Count);

        await mainVm.ReorderDashboardFileAsync(dashboard, tabC.FileId, tabB.FileId, DropPlacement.After);
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_AllOpenTabs_UsesSingleFileAllOpenTabsSnapshotLookup()
    {
        using var tab = new LogTabViewModel(
            "file-a",
            @"C:\logs\a.log",
            new StubLogReaderService(),
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());
        var snapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = new[] { 2, 5 },
            StatusText = "Filter active",
            FilterRequest = new SearchRequest
            {
                Query = "filter",
                FilePaths = new List<string> { tab.FilePath },
                SourceMode = SearchRequestSourceMode.DiskSnapshot
            },
            HasSeenParseableTimestamp = true,
            LastEvaluatedLine = 10
        };
        var workspace = new TailScopeLookupWorkspaceContextStub(tab, snapshot);
        var search = new RecordingSearchService
        {
            SearchFileHandler = (filePath, request) =>
            {
                Assert.Equal(tab.FilePath, filePath, ignoreCase: true);
                Assert.True(request.AllowedLineNumbersByFilePath.TryGetValue(tab.FilePath, out var allowedLines));
                Assert.Equal(new[] { 2, 5 }, allowedLines);
                Assert.Single(request.AllowedLineNumbersByFilePath);
                return new SearchResult { FilePath = filePath };
            }
        };
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "search",
            TargetMode = SearchFilterTargetMode.AllOpenTabs,
            SearchDataMode = SearchDataMode.Tail
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        tab.TotalLines = 6;

        await WaitForConditionAsync(() => search.SearchFileCallCount == 1);

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task SearchScratchpad_LeavingScope_CancelsLiveSearchAndRestoresResultsWithRerunStatus()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = Assert.Single(mainVm.Groups);
        var adHocTabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);

        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log" && tab.IsAdHocScope);
        mainVm.SelectedTab = tabA;
        tabA.TotalLines = 10;
        search.SearchFileHandler = (_, request) => new SearchResult
        {
            FilePath = tabA.FilePath,
            Hits = new List<SearchHit>
            {
                new()
                {
                    LineNumber = request.EndLineNumber ?? -1,
                    LineText = "tail hit",
                    MatchStart = 0,
                    MatchLength = 4
                }
            }
        };

        var panel = mainVm.SearchPanel;
        panel.Query = "tail-hit";
        panel.IsTailMode = true;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        tabA.TotalLines = 11;
        await WaitForConditionAsync(() =>
            search.SearchFileCallCount == 1 &&
            panel.Results.Count == 1 &&
            panel.Results[0].Hits[0].LineNumber == 11);

        mainVm.ToggleGroupSelection(dashboard);

        var searchCallsAfterScopeExit = search.SearchFileCallCount;
        tabA.TotalLines = 12;
        await Task.Delay(500);

        Assert.Equal(searchCallsAfterScopeExit, search.SearchFileCallCount);

        mainVm.ToggleGroupSelection(dashboard);

        Assert.False(panel.IsSearching);
        Assert.Equal(ScopeExitCancelledStatusText, panel.ResultsHeaderText);
        Assert.Equal(new long[] { 11 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public async Task SearchScratchpad_ClearResults_OnlyClearsActiveScopeState()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = Assert.Single(mainVm.Groups);
        var adHocTabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log" && tab.IsAdHocScope);
        dashboard.Model.FileIds.Add(adHocTabB.FileId);

        var panel = mainVm.SearchPanel;
        search.NextResults = new[]
        {
            new SearchResult
            {
                FilePath = adHocTabB.FilePath,
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 12, LineText = "adhoc hit", MatchStart = 0, MatchLength = 5 }
                }
            }
        };
        panel.Query = "adhoc-state";
        panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal("1 in 1 file(s)", panel.ResultsHeaderText);

        mainVm.ToggleGroupSelection(dashboard);
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");
        var dashboardTabB = FindScopedTab(mainVm, @"C:\logs\b.log", dashboard.Id);

        search.NextResults = new[]
        {
            new SearchResult
            {
                FilePath = dashboardTabB.FilePath,
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 33, LineText = "dashboard hit", MatchStart = 0, MatchLength = 9 }
                }
            }
        };
        panel.Query = "dashboard-state";
        panel.TargetMode = SearchFilterTargetMode.CurrentTab;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal("1 in 1 file(s)", panel.ResultsHeaderText);

        mainVm.ToggleGroupSelection(dashboard);

        Assert.Equal("adhoc-state", panel.Query);
        Assert.Equal(SearchFilterTargetMode.AllOpenTabs, panel.TargetMode);
        Assert.Equal(new long[] { 12 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());

        panel.ClearResultsCommand.Execute(null);

        Assert.Equal("adhoc-state", panel.Query);
        Assert.Equal(SearchFilterTargetMode.AllOpenTabs, panel.TargetMode);
        Assert.Empty(panel.Results);
        Assert.Equal(string.Empty, panel.StatusText);

        mainVm.ToggleGroupSelection(dashboard);

        Assert.Equal("dashboard-state", panel.Query);
        Assert.Equal(SearchFilterTargetMode.CurrentTab, panel.TargetMode);
        Assert.Equal("1 in 1 file(s)", panel.ResultsHeaderText);
        Assert.Equal(new long[] { 33 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());

        mainVm.ToggleGroupSelection(dashboard);

        Assert.Equal("adhoc-state", panel.Query);
        Assert.Equal(SearchFilterTargetMode.AllOpenTabs, panel.TargetMode);
        Assert.Empty(panel.Results);
        Assert.Equal(string.Empty, panel.StatusText);
    }

    [Fact]
    public async Task SearchScratchpad_RestoredResults_NavigateToHitUsesAllOpenTabsTabInstance()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\shared.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 42, LineText = "shared hit", MatchStart = 0, MatchLength = 6 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\shared.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\other.log");

        var adHocSharedTab = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\shared.log" && tab.IsAdHocScope);
        var adHocOtherTab = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\other.log" && tab.IsAdHocScope);

        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = Assert.Single(mainVm.Groups);
        dashboard.Model.FileIds.Add(adHocSharedTab.FileId);
        dashboard.Model.FileIds.Add(adHocOtherTab.FileId);

        mainVm.ToggleGroupSelection(dashboard);
        await mainVm.OpenFilePathAsync(@"C:\logs\shared.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\other.log");

        var dashboardSharedTab = FindScopedTab(mainVm, @"C:\logs\shared.log", dashboard.Id);
        var dashboardOtherTab = FindScopedTab(mainVm, @"C:\logs\other.log", dashboard.Id);

        var panel = mainVm.SearchPanel;
        panel.Query = "shared";
        panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        mainVm.ToggleGroupSelection(dashboard);
        mainVm.ToggleGroupSelection(dashboard);

        mainVm.SelectedTab = dashboardOtherTab;

        var fileResult = Assert.Single(panel.Results);
        var hit = Assert.Single(fileResult.Hits);

        await InvokeNavigateToHitAsync(fileResult, hit);

        Assert.Same(dashboardSharedTab, mainVm.SelectedTab);
        Assert.NotSame(adHocSharedTab, mainVm.SelectedTab);
    }

    [Fact]
    public async Task ExecuteSearch_NoFilesInScope_SetsStatusAndSkipsSearch()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "anything"
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal("No files to search", panel.ResultsHeaderText);
        Assert.Null(search.LastRequest);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_StartsMonitoringWithoutDiskSnapshotSearch()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "tail-error",
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.True(panel.IsSearching);
        Assert.Equal(0, search.SearchFilesCallCount);
        Assert.Equal(0, search.SearchFileCallCount);
        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task SearchActionButton_UsesClearWhenIdleAndCancelWhileSearching()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "tail-error",
            IsTailMode = true
        };

        Assert.Equal("Clear", panel.SearchActionButtonText);
        Assert.Same(panel.ClearResultsCommand, panel.SearchActionButtonCommand);

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal("Cancel", panel.SearchActionButtonText);
        Assert.Same(panel.CancelSearchCommand, panel.SearchActionButtonCommand);

        panel.CancelSearchCommand.Execute(null);

        Assert.Equal("Clear", panel.SearchActionButtonText);
        Assert.Same(panel.ClearResultsCommand, panel.SearchActionButtonCommand);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_TotalLineChangesTriggerSearchWithoutPolling()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        search.SearchFileHandler = (_, request) => new SearchResult
        {
            FilePath = selected.FilePath,
            Hits = new List<SearchHit>
            {
                new() { LineNumber = request.EndLineNumber ?? -1, LineText = "tail hit", MatchStart = 0, MatchLength = 4 }
            }
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "tail-hit",
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        selected.TotalLines = 11;
        await WaitForConditionAsync(() =>
            search.SearchFileCallCount == 1 &&
            panel.Results.Count == 1 &&
            panel.Results[0].Hits[0].LineNumber == 11);

        Assert.Contains(search.SearchFileRequests, request =>
            request.StartLineNumber == 11 &&
            request.EndLineNumber == 11);
        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_AllOpenTabs_OrdersNewResultGroupsByDashboardMemberOrder()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");

        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = Assert.Single(mainVm.Groups);
        dashboard.Model.FileIds.Add(tabB.FileId);
        dashboard.Model.FileIds.Add(tabA.FileId);
        mainVm.ToggleGroupSelection(dashboard);
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        tabA = FindScopedTab(mainVm, @"C:\logs\a.log", dashboard.Id);
        tabB = FindScopedTab(mainVm, @"C:\logs\b.log", dashboard.Id);
        dashboard.RefreshMemberFiles(
            mainVm.Tabs,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [tabB.FileId] = tabB.FilePath,
                [tabA.FileId] = tabA.FilePath
            },
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [tabB.FileId] = true,
                [tabA.FileId] = true
            },
            selectedFileId: null,
            showFullPath: false);
        tabA.TotalLines = 10;
        tabB.TotalLines = 10;

        search.SearchFileHandler = (filePath, request) => new SearchResult
        {
            FilePath = filePath,
            Hits = new List<SearchHit>
            {
                new()
                {
                    LineNumber = request.EndLineNumber ?? -1,
                    LineText = Path.GetFileName(filePath),
                    MatchStart = 0,
                    MatchLength = 1
                }
            }
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "tail-hit",
            TargetMode = SearchFilterTargetMode.AllOpenTabs,
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        tabA.TotalLines = 11;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].FilePath == tabA.FilePath);

        tabB.TotalLines = 11;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 2 &&
            panel.Results.Select(result => result.FilePath).SequenceEqual(new[] { tabB.FilePath, tabA.FilePath }));

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_DiskSnapshot_ShowsAdaptiveSearchStatusAndRequestUsage()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var searchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSearch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        search.SearchFilesAsyncHandler = async (request, _, _) =>
        {
            searchStarted.TrySetResult(true);
            await releaseSearch.Task;
            return request.FilePaths.Select(filePath => new SearchResult { FilePath = filePath }).ToArray();
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error"
        };

        var searchTask = InvokeExecuteSearchAsync(panel);
        await searchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Searching 1 file with 1 worker across 1 local root...", panel.StatusText);
        Assert.Equal(SearchRequestUsage.DiskSearch, search.LastRequest?.Usage);

        releaseSearch.SetResult(true);
        await searchTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteSearch_NewerSearch_IgnoresLateResultsFromCanceledSession()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var firstSearchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSearch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectedPath = mainVm.SelectedTab!.FilePath;
        var callCount = 0;
        search.SearchFilesAsyncHandler = async (request, _, _) =>
        {
            var callNumber = Interlocked.Increment(ref callCount);
            if (callNumber == 1)
            {
                firstSearchStarted.TrySetResult(true);
                await releaseFirstSearch.Task;
                return new[]
                {
                    new SearchResult
                    {
                        FilePath = selectedPath,
                        Hits = new List<SearchHit>
                        {
                            new() { LineNumber = 1, LineText = "stale result", MatchStart = 0, MatchLength = 5 }
                        }
                    }
                };
            }

            return new[]
            {
                new SearchResult
                {
                    FilePath = selectedPath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 2, LineText = "fresh result", MatchStart = 0, MatchLength = 5 }
                    }
                }
            };
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "first"
        };

        var firstTask = InvokeExecuteSearchAsync(panel);
        await firstSearchStarted.Task;

        panel.Query = "second";
        var secondTask = InvokeExecuteSearchAsync(panel);
        await secondTask;

        releaseFirstSearch.TrySetResult(true);
        await firstTask;

        var fileResult = Assert.Single(panel.Results);
        var hit = Assert.Single(fileResult.Hits);
        Assert.Equal(2, search.SearchFilesCallCount);
        Assert.Equal(2, hit.LineNumber);
        Assert.Equal("fresh result", hit.LineText);
    }

    [Fact]
    public async Task ExecuteSearch_AllOpenTabs_NewerSearchIgnoresLateSnapshotPreparation()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var firstSearchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSearch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectedPath = mainVm.SelectedTab!.FilePath;
        var callCount = 0;
        search.SearchFilesAsyncHandler = async (_, _, _) =>
        {
            var callNumber = Interlocked.Increment(ref callCount);
            if (callNumber == 1)
            {
                firstSearchStarted.TrySetResult(true);
                await releaseFirstSearch.Task;
                return Enumerable.Range(0, 5_000)
                    .Select(index => new SearchResult
                    {
                        FilePath = $@"C:\logs\stale-{index}.log",
                        Hits = new List<SearchHit>
                        {
                            new() { LineNumber = index + 1, LineText = "stale result", MatchStart = 0, MatchLength = 5 }
                        }
                    })
                    .ToArray();
            }

            return new[]
            {
                new SearchResult
                {
                    FilePath = selectedPath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 2, LineText = "fresh result", MatchStart = 0, MatchLength = 5 }
                    }
                }
            };
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "first",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        var firstTask = InvokeExecuteSearchAsync(panel);
        await firstSearchStarted.Task;

        releaseFirstSearch.TrySetResult(true);
        panel.Query = "second";
        var secondTask = InvokeExecuteSearchAsync(panel);
        await Task.WhenAll(firstTask, secondTask);

        var fileResult = Assert.Single(panel.Results);
        var hit = Assert.Single(fileResult.Hits);
        Assert.Equal(2, search.SearchFilesCallCount);
        Assert.Equal(2, hit.LineNumber);
        Assert.Equal("fresh result", hit.LineText);
    }

    [Fact]
    public async Task ExecuteSearch_EmptyQuery_CancelsActiveTailSessionBeforeReturningValidationError()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsTailMode = true
        };

        await InvokeExecuteSearchAsync(panel);
        Assert.True(panel.IsSearching);

        panel.Query = string.Empty;
        await InvokeExecuteSearchAsync(panel);

        selected.TotalLines = 12;
        await Task.Delay(700);

        Assert.False(panel.IsSearching);
        Assert.Equal(0, search.SearchFileCallCount);
        Assert.Equal("Enter a search query.", panel.StatusText);
    }

    [Fact]
    public async Task ExecuteSearch_SupersededTailSession_IgnoresLateResultsFromPriorSession()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        var firstTailSearchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTailSearch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchCallCount = 0;
        search.SearchFileAsyncHandler = async (_, _, _, _) =>
        {
            var callNumber = Interlocked.Increment(ref searchCallCount);
            if (callNumber == 1)
            {
                firstTailSearchStarted.TrySetResult(true);
                await releaseFirstTailSearch.Task;
                return new SearchResult
                {
                    FilePath = selected.FilePath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 11, LineText = "stale tail hit", MatchStart = 0, MatchLength = 4 }
                    }
                };
            }

            return new SearchResult
            {
                FilePath = selected.FilePath,
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 12, LineText = "fresh tail hit", MatchStart = 0, MatchLength = 4 }
                }
            };
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "first",
            IsTailMode = true
        };

        await InvokeExecuteSearchAsync(panel);

        selected.TotalLines = 11;
        await firstTailSearchStarted.Task;

        panel.Query = "second";
        await InvokeExecuteSearchAsync(panel);

        selected.TotalLines = 12;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 1 &&
            panel.Results[0].Hits[0].LineNumber == 12);

        releaseFirstTailSearch.TrySetResult(true);
        await Task.Delay(400);

        var fileResult = Assert.Single(panel.Results);
        Assert.Equal(new long[] { 12 }, fileResult.Hits.Select(hit => hit.LineNumber).ToArray());
        Assert.Equal("fresh tail hit", fileResult.Hits[0].LineText);
        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_QueryEditsDuringLiveSession_UseCapturedSearchCriteria()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        search.SearchFileHandler = (_, request) => new SearchResult
        {
            FilePath = selected.FilePath,
            Hits = new List<SearchHit>
            {
                new()
                {
                    LineNumber = request.EndLineNumber ?? -1,
                    LineText = $"hit {request.Query}",
                    MatchStart = 0,
                    MatchLength = 4
                }
            }
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "first",
            IsRegex = true,
            CaseSensitive = true,
            FromTimestamp = " 2026-03-09 19:49:10 ",
            ToTimestamp = " 2026-03-09 19:49:20 ",
            IsTailMode = true
        };

        await InvokeExecuteSearchAsync(panel);

        selected.TotalLines = 11;
        await WaitForConditionAsync(() =>
            search.SearchFileRequests.Count == 1 &&
            panel.Results.Count == 1 &&
            panel.Results[0].Hits[0].LineNumber == 11);

        panel.Query = "second";
        panel.IsRegex = false;
        panel.CaseSensitive = false;
        panel.FromTimestamp = "2026-03-09 19:50:10";
        panel.ToTimestamp = "2026-03-09 19:50:20";

        selected.TotalLines = 12;
        await WaitForConditionAsync(() =>
            search.SearchFileRequests.Count == 2 &&
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 2);

        AssertRequestMatchesCriteria(search.SearchFileRequests[0], "first", true, true, "2026-03-09 19:49:10", "2026-03-09 19:49:20");
        AssertRequestMatchesCriteria(search.SearchFileRequests[1], "first", true, true, "2026-03-09 19:49:10", "2026-03-09 19:49:20");

        var fileResult = Assert.Single(panel.Results);
        Assert.Equal(new long[] { 11, 12 }, fileResult.Hits.Select(hit => hit.LineNumber).ToArray());
        Assert.Equal(new[] { "hit first", "hit first" }, fileResult.Hits.Select(hit => hit.LineText).ToArray());
        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_RotationReset_ClearsStaleHitsAndReprocessesCurrentFile()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        search.SearchFileHandler = (_, request) =>
        {
            if (request.StartLineNumber == 11 && request.EndLineNumber == 12)
            {
                return new SearchResult
                {
                    FilePath = selected.FilePath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 11, LineText = "old generation 11", MatchStart = 0, MatchLength = 3 },
                        new() { LineNumber = 12, LineText = "old generation 12", MatchStart = 0, MatchLength = 3 }
                    }
                };
            }

            if (request.StartLineNumber == 1 && request.EndLineNumber == 12)
            {
                return new SearchResult
                {
                    FilePath = selected.FilePath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "new generation 1", MatchStart = 0, MatchLength = 3 },
                        new() { LineNumber = 2, LineText = "new generation 2", MatchStart = 0, MatchLength = 3 }
                    }
                };
            }

            return new SearchResult { FilePath = selected.FilePath };
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsTailMode = true
        };

        await InvokeExecuteSearchAsync(panel);

        selected.TotalLines = 12;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 2 &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 11, 12 }));

        await selected.ResetLineIndexAsync();
        selected.TotalLines = 12;

        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 2 &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 1, 2 }));

        var fileResult = Assert.Single(panel.Results);
        Assert.Contains(search.SearchFileRequests, request =>
            request.StartLineNumber == 1 &&
            request.EndLineNumber == 12);
        Assert.Equal(new long[] { 1, 2 }, fileResult.Hits.Select(hit => hit.LineNumber).ToArray());
        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_RotationReset_EmptyStateClearsStaleHitsBeforeNextAppend()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        search.SearchFileHandler = (_, request) =>
        {
            if (request.StartLineNumber == 11 && request.EndLineNumber == 12)
            {
                return new SearchResult
                {
                    FilePath = selected.FilePath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 11, LineText = "old generation 11", MatchStart = 0, MatchLength = 3 },
                        new() { LineNumber = 12, LineText = "old generation 12", MatchStart = 0, MatchLength = 3 }
                    }
                };
            }

            if (request.StartLineNumber == 1 && request.EndLineNumber == 2)
            {
                return new SearchResult
                {
                    FilePath = selected.FilePath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "new generation 1", MatchStart = 0, MatchLength = 3 },
                        new() { LineNumber = 2, LineText = "new generation 2", MatchStart = 0, MatchLength = 3 }
                    }
                };
            }

            return new SearchResult { FilePath = selected.FilePath };
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsTailMode = true
        };

        await InvokeExecuteSearchAsync(panel);

        selected.TotalLines = 12;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 2 &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 11, 12 }));

        await selected.ResetLineIndexAsync();
        selected.TotalLines = 0;

        await WaitForConditionAsync(() => panel.Results.Count == 0);

        selected.TotalLines = 2;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 2 &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 1, 2 }));

        var fileResult = Assert.Single(panel.Results);
        Assert.Contains(search.SearchFileRequests, request =>
            request.StartLineNumber == 1 &&
            request.EndLineNumber == 2);
        Assert.Equal(new long[] { 1, 2 }, fileResult.Hits.Select(hit => hit.LineNumber).ToArray());
        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_AutomaticRetryWithoutAnotherSignal_ClearsPreviousFileError()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        var searchAttempt = 0;
        search.SearchFileHandler = (_, request) =>
        {
            searchAttempt++;
            return searchAttempt == 1
                ? new SearchResult
                {
                    FilePath = selected.FilePath,
                    Error = "temporary tail failure"
                }
                : new SearchResult
                {
                    FilePath = selected.FilePath,
                    Hits = new List<SearchHit>
                    {
                        new()
                        {
                            LineNumber = request.EndLineNumber ?? -1,
                            LineText = "recovered hit",
                            MatchStart = 0,
                            MatchLength = 3
                        }
                    }
                };
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsTailMode = true
        };

        await InvokeExecuteSearchAsync(panel);

        selected.TotalLines = 11;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].Error == "temporary tail failure");

        await WaitForConditionAsync(() =>
            search.SearchFileCallCount == 2 &&
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 1 &&
            panel.Results[0].Error == null &&
            panel.Results[0].Hits[0].LineNumber == 11);

        var fileResult = Assert.Single(panel.Results);
        Assert.Equal(2, search.SearchFileCallCount);
        Assert.Equal(2, search.SearchFileRequests.Count(request =>
            request.StartLineNumber == 11 &&
            request.EndLineNumber == 11));
        Assert.Null(fileResult.Error);
        Assert.Single(fileResult.Hits);
        Assert.Equal(11, fileResult.Hits[0].LineNumber);
        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_PendingRetry_CoalescesNewAppendIntoSingleCatchUpSearch()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        var firstAttemptStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchAttempt = 0;
        search.SearchFileHandler = (_, request) =>
        {
            searchAttempt++;
            if (searchAttempt == 1)
            {
                firstAttemptStarted.TrySetResult(true);
                return new SearchResult
                {
                    FilePath = selected.FilePath,
                    Error = "temporary tail failure"
                };
            }

            return new SearchResult
            {
                FilePath = selected.FilePath,
                Hits = new List<SearchHit>
                {
                    new()
                    {
                        LineNumber = request.EndLineNumber ?? -1,
                        LineText = "coalesced hit",
                        MatchStart = 0,
                        MatchLength = 3
                    }
                }
            };
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsTailMode = true
        };

        await InvokeExecuteSearchAsync(panel);

        selected.TotalLines = 11;
        await firstAttemptStarted.Task;
        selected.TotalLines = 13;

        await WaitForConditionAsync(() =>
            search.SearchFileCallCount == 2 &&
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 1 &&
            panel.Results[0].Error == null &&
            panel.Results[0].Hits[0].LineNumber == 13);

        Assert.Equal(2, search.SearchFileCallCount);
        Assert.Collection(
            search.SearchFileRequests,
            request =>
            {
                Assert.Equal(11, request.StartLineNumber);
                Assert.Equal(11, request.EndLineNumber);
            },
            request =>
            {
                Assert.Equal(11, request.StartLineNumber);
                Assert.Equal(13, request.EndLineNumber);
            });
        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_PendingRetry_ResetAbandonsStaleRangeBeforeRetrying()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        search.SearchFileHandler = (_, request) =>
        {
            if (request.StartLineNumber == 11 && request.EndLineNumber == 11)
            {
                return new SearchResult
                {
                    FilePath = selected.FilePath,
                    Error = "temporary tail failure"
                };
            }

            if (request.StartLineNumber == 1 && request.EndLineNumber == 2)
            {
                return new SearchResult
                {
                    FilePath = selected.FilePath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "new generation 1", MatchStart = 0, MatchLength = 3 },
                        new() { LineNumber = 2, LineText = "new generation 2", MatchStart = 0, MatchLength = 3 }
                    }
                };
            }

            return new SearchResult { FilePath = selected.FilePath };
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsTailMode = true
        };

        await InvokeExecuteSearchAsync(panel);

        selected.TotalLines = 11;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].Error == "temporary tail failure");

        await selected.ResetLineIndexAsync();
        selected.TotalLines = 2;

        await WaitForConditionAsync(() =>
            search.SearchFileCallCount == 2 &&
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 2 &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 1, 2 }));

        Assert.Equal(2, search.SearchFileCallCount);
        Assert.Collection(
            search.SearchFileRequests,
            request =>
            {
                Assert.Equal(11, request.StartLineNumber);
                Assert.Equal(11, request.EndLineNumber);
            },
            request =>
            {
                Assert.Equal(1, request.StartLineNumber);
                Assert.Equal(2, request.EndLineNumber);
            });
        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task CancelSearch_TailMode_CancelsPendingRetry()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        search.SearchFileHandler = (_, _) => new SearchResult
        {
            FilePath = selected.FilePath,
            Error = "temporary tail failure"
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsTailMode = true
        };

        await InvokeExecuteSearchAsync(panel);

        selected.TotalLines = 11;
        await WaitForConditionAsync(() =>
            search.SearchFileCallCount == 1 &&
            panel.Results.Count == 1 &&
            panel.Results[0].Error == "temporary tail failure");

        panel.CancelSearchCommand.Execute(null);
        await Task.Delay(450);

        Assert.Equal(1, search.SearchFileCallCount);
        Assert.False(panel.IsSearching);
        Assert.Equal("Search cancelled", panel.StatusText);
        Assert.Equal("Clear", panel.SearchActionButtonText);
        Assert.Same(panel.ClearResultsCommand, panel.SearchActionButtonCommand);
    }

    [Fact]
    public async Task CancelSearch_TailMode_UnsubscribesFromTabChanges()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "tail-hit",
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        panel.CancelSearchCommand.Execute(null);

        selected.TotalLines = 11;
        await Task.Delay(150);

        Assert.Equal(0, search.SearchFileCallCount);
        Assert.Empty(panel.Results);
        Assert.False(panel.IsSearching);
    }

    [Fact]
    public async Task ClearResults_TailMode_SilentlyCancelsAndStopsFurtherUpdates()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        search.SearchFileHandler = (_, request) => new SearchResult
        {
            FilePath = selected.FilePath,
            Hits = new List<SearchHit>
            {
                new()
                {
                    LineNumber = request.EndLineNumber ?? -1,
                    LineText = "tail hit",
                    MatchStart = 0,
                    MatchLength = 4
                }
            }
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "tail-hit",
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        selected.TotalLines = 11;
        await WaitForConditionAsync(() =>
            search.SearchFileCallCount == 1 &&
            panel.Results.Count == 1 &&
            panel.Results[0].Hits[0].LineNumber == 11);

        panel.ClearResultsCommand.Execute(null);

        Assert.False(panel.IsSearching);
        Assert.Equal("tail-hit", panel.Query);
        Assert.Empty(panel.Results);
        Assert.Equal(string.Empty, panel.StatusText);
        Assert.Equal("Clear", panel.SearchActionButtonText);
        Assert.Same(panel.ClearResultsCommand, panel.SearchActionButtonCommand);

        selected.TotalLines = 12;
        await Task.Delay(150);

        Assert.Equal(1, search.SearchFileCallCount);
        Assert.Empty(panel.Results);
        Assert.Equal(string.Empty, panel.StatusText);
    }

    [Fact]
    public async Task TailSearch_CollapsedResultGrowth_DoesNotRefreshVisibleRowsForHiddenHits()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        search.SearchFileHandler = (_, request) => new SearchResult
        {
            FilePath = selected.FilePath,
            Hits = new List<SearchHit>
            {
                new()
                {
                    LineNumber = request.EndLineNumber ?? -1,
                    LineText = $"tail hit {request.EndLineNumber}",
                    MatchStart = 0,
                    MatchLength = 4
                }
            }
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "tail-hit",
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        selected.TotalLines = 11;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 1 &&
            panel.VisibleRows.Count == 1);

        var collectionChanges = 0;
        panel.VisibleRows.CollectionChanged += (_, _) => collectionChanges++;

        selected.TotalLines = 12;
        await WaitForConditionAsync(() => panel.Results[0].HitCount == 2);

        Assert.Single(panel.VisibleRows.Cast<object>());
        Assert.Equal(0, collectionChanges);

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task TailSearch_ExpandedResultGrowth_RefreshesVisibleRowsOnce()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;
        search.SearchFileHandler = (_, request) => new SearchResult
        {
            FilePath = selected.FilePath,
            Hits = new List<SearchHit>
            {
                new()
                {
                    LineNumber = request.EndLineNumber ?? -1,
                    LineText = $"tail hit {request.EndLineNumber}",
                    MatchStart = 0,
                    MatchLength = 4
                }
            }
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "tail-hit",
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        selected.TotalLines = 11;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 1 &&
            panel.VisibleRows.Count == 1);

        panel.Results[0].IsExpanded = true;
        await WaitForConditionAsync(() => panel.VisibleRows.Count == 2);

        var collectionChanges = 0;
        panel.VisibleRows.CollectionChanged += (_, _) => collectionChanges++;

        selected.TotalLines = 12;
        await WaitForConditionAsync(() =>
            panel.Results[0].HitCount == 2 &&
            panel.VisibleRows.Count == 3);

        Assert.Equal(1, collectionChanges);

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_PublishesVisibleRowsOnUiThreadAfterBackgroundSearch()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var fileRepo = new StubLogFileRepository();
            var groupRepo = new StubLogGroupRepository();
            var search = new RecordingSearchService();
            var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
            await mainVm.InitializeAsync();
            await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

            var selected = mainVm.SelectedTab!;
            selected.TotalLines = 10;

            var uiThreadId = Environment.CurrentManagedThreadId;
            var searchWorkThreadId = 0;
            var collectionChangedThreadId = 0;
            search.SearchFileAsyncHandler = (filePath, request, encoding, ct) => Task.Run(() =>
            {
                searchWorkThreadId = Environment.CurrentManagedThreadId;
                return new SearchResult
                {
                    FilePath = filePath,
                    Hits = new List<SearchHit>
                    {
                        new()
                        {
                            LineNumber = request.EndLineNumber ?? -1,
                            LineText = "tail hit",
                            MatchStart = 0,
                            MatchLength = 4
                        }
                    }
                };
            }, ct);
            search.SearchFileRangeAsyncHandler = (filePath, request, encoding, readLinesAsync, ct) => Task.Run(() =>
            {
                searchWorkThreadId = Environment.CurrentManagedThreadId;
                return new SearchResult
                {
                    FilePath = filePath,
                    Hits = new List<SearchHit>
                    {
                        new()
                        {
                            LineNumber = request.EndLineNumber ?? -1,
                            LineText = "tail hit",
                            MatchStart = 0,
                            MatchLength = 4
                        }
                    }
                };
            }, ct);

            var panel = new SearchPanelViewModel(search, mainVm)
            {
                Query = "tail-hit",
                IsTailMode = true
            };

            await panel.ExecuteSearchCommand.ExecuteAsync(null);

            panel.VisibleRows.CollectionChanged += (_, _) => collectionChangedThreadId = Environment.CurrentManagedThreadId;

            selected.TotalLines = 11;
            await WaitForConditionAsync(() =>
                panel.Results.Count == 1 &&
                panel.Results[0].HitCount == 1 &&
                panel.VisibleRows.Count == 1);

            Assert.NotEqual(0, searchWorkThreadId);
            Assert.NotEqual(uiThreadId, searchWorkThreadId);
            Assert.Equal(uiThreadId, collectionChangedThreadId);

            panel.CancelSearchCommand.Execute(null);
        });
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_UsesIndexedRangeReaderForAppendedLinesOnly()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var logReader = new RecordingLogReaderService(new[]
        {
            "line 1",
            "line 2",
            "line 3",
            "line 4",
            "line 5",
            "line 6",
            "line 7",
            "line 8",
            "line 9",
            "line 10",
            "error line 11",
            "error line 12"
        });
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), new SearchService(), logReader);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 10;

        var panel = new SearchPanelViewModel(new SearchService(), mainVm)
        {
            Query = "error",
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        selected.TotalLines = 12;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 2 &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 11, 12 }));

        Assert.Equal(1, logReader.ReadLinesRequests.Count(request => request == (10, 2)));

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_ProcessesLargeAppendedRangeInBoundedChunks()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var logReader = new RecordingLogReaderService(
            Enumerable.Range(1, 5_000).Select(i => i % 2 == 0 ? $"error line {i}" : $"line {i}"));
        var search = new RecordingSearchService();
        search.SearchFileRangeAsyncHandler = (filePath, request, encoding, readLinesAsync, ct) =>
            Task.FromResult(new SearchResult
            {
                FilePath = filePath,
                Hits = new List<SearchHit>
                {
                    new()
                    {
                        LineNumber = request.EndLineNumber ?? 0,
                        LineText = "chunk hit",
                        MatchStart = 0,
                        MatchLength = 5
                    }
                }
            });
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search, logReader);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 0;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        selected.TotalLines = 5_000;
        await WaitForConditionAsync(() => search.SearchFileRangeRequests.Count >= 3);

        Assert.Equal(new long?[] { 1, 2_001, 4_001 }, search.SearchFileRangeRequests.Take(3).Select(request => request.StartLineNumber).ToArray());
        Assert.Equal(new long?[] { 2_000, 4_000, 5_000 }, search.SearchFileRangeRequests.Take(3).Select(request => request.EndLineNumber).ToArray());

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_EnforcesHitCapAcrossChunks()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var logReader = new RecordingLogReaderService(
            Enumerable.Range(1, 12_000).Select(i => $"error line {i}"));
        var search = new RecordingSearchService();
        search.SearchFileRangeAsyncHandler = (filePath, request, encoding, readLinesAsync, ct) =>
        {
            var hitCount = Math.Min(2_000, request.MaxHitsPerFile ?? 2_000);
            return Task.FromResult(new SearchResult
            {
                FilePath = filePath,
                Hits = Enumerable.Range(0, hitCount)
                    .Select(offset => new SearchHit
                    {
                        LineNumber = (request.StartLineNumber ?? 1) + offset,
                        LineText = $"chunk hit {request.StartLineNumber}-{offset}",
                        MatchStart = 0,
                        MatchLength = 5
                    })
                    .ToList()
            });
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search, logReader);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 0;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        selected.TotalLines = 12_000;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 10_000 &&
            panel.ResultsHeaderText.Contains("Results capped", StringComparison.Ordinal));

        Assert.Equal(10_000, panel.Results[0].HitCount);
        Assert.Equal(new int?[] { 10_000, 8_000, 6_000, 4_000, 2_000 },
            search.SearchFileRangeRequests.Select(request => request.MaxHitsPerFile).ToArray());
        Assert.Equal(new long?[] { 1, 2_001, 4_001, 6_001, 8_001 },
            search.SearchFileRangeRequests.Select(request => request.StartLineNumber).ToArray());

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_StopsSearchingChunksAfterHitCap()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var logReader = new RecordingLogReaderService(
            Enumerable.Range(1, 5_000).Select(i => $"error line {i}"));
        var search = new RecordingSearchService();
        search.SearchFileRangeAsyncHandler = (filePath, request, encoding, readLinesAsync, ct) =>
        {
            var returnedHitCount = (request.MaxHitsPerFile ?? 10_000) + 500;
            return Task.FromResult(new SearchResult
            {
                FilePath = filePath,
                Hits = Enumerable.Range(1, returnedHitCount)
                    .Select(lineNumber => new SearchHit
                    {
                        LineNumber = lineNumber,
                        LineText = $"over cap hit {lineNumber}",
                        MatchStart = 0,
                        MatchLength = 8
                    })
                    .ToList()
            });
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search, logReader);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 0;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        selected.TotalLines = 5_000;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 10_000 &&
            panel.ResultsHeaderText.Contains("Results capped", StringComparison.Ordinal));

        Assert.Equal(10_000, panel.Results[0].HitCount);
        Assert.Single(search.SearchFileRangeRequests);
        Assert.Equal(10_000, search.SearchFileRangeRequests[0].MaxHitsPerFile);

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_DiskSnapshotCancellation_SuppressesLateResults()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var fileRepo = new StubLogFileRepository();
            var groupRepo = new StubLogGroupRepository();
            var search = new RecordingSearchService();
            var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
            await mainVm.InitializeAsync();
            await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

            var selected = mainVm.SelectedTab!;
            var searchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            search.SearchFilesAsyncHandler = async (request, encodings, ct) =>
            {
                searchEntered.TrySetResult();
                await releaseSearch.Task;
                return new[]
                {
                    new SearchResult
                    {
                        FilePath = selected.FilePath,
                        Hits = new List<SearchHit>
                        {
                            new()
                            {
                                LineNumber = 1,
                                LineText = "late hit",
                                MatchStart = 0,
                                MatchLength = 4
                            }
                        }
                    }
                };
            };

            var panel = new SearchPanelViewModel(search, mainVm)
            {
                Query = "late-hit"
            };

            var executeSearchTask = panel.ExecuteSearchCommand.ExecuteAsync(null);
            await searchEntered.Task;

            panel.CancelSearchCommand.Execute(null);
            releaseSearch.TrySetResult();
            await executeSearchTask;

            Assert.False(panel.IsSearching);
            Assert.Empty(panel.Results);
            Assert.Empty(panel.VisibleRows.Cast<object>());
        });
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_ContentResetRemovesVisibleRowsCleanly()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var fileRepo = new StubLogFileRepository();
            var groupRepo = new StubLogGroupRepository();
            var search = new RecordingSearchService();
            var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
            await mainVm.InitializeAsync();
            await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

            var selected = mainVm.SelectedTab!;
            selected.TotalLines = 10;
            search.SearchFileHandler = (_, request) => new SearchResult
            {
                FilePath = selected.FilePath,
                Hits = new List<SearchHit>
                {
                    new()
                    {
                        LineNumber = request.EndLineNumber ?? -1,
                        LineText = "tail hit",
                        MatchStart = 0,
                        MatchLength = 4
                    }
                }
            };

            var panel = new SearchPanelViewModel(search, mainVm)
            {
                Query = "tail-hit",
                IsTailMode = true
            };

            await panel.ExecuteSearchCommand.ExecuteAsync(null);

            selected.TotalLines = 11;
            await WaitForConditionAsync(() =>
                panel.Results.Count == 1 &&
                panel.Results[0].HitCount == 1 &&
                panel.VisibleRows.Count == 1);

            await selected.ResetLineIndexAsync();
            selected.TotalLines = 0;
            await WaitForConditionAsync(() => panel.Results.Count == 0 && panel.VisibleRows.Count == 0);

            Assert.Empty(panel.VisibleRows.Cast<object>());

            panel.CancelSearchCommand.Execute(null);
        });
    }

    [Fact]
    public async Task NavigateToHit_MultiTabWorkspace_DisablesGlobalAutoScroll()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 42, LineText = "hit line", MatchStart = 0, MatchLength = 3 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");
        Assert.True(mainVm.GlobalAutoScrollEnabled);
        Assert.True(tabA.AutoScrollEnabled);
        Assert.True(tabB.AutoScrollEnabled);

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await InvokeExecuteSearchAsync(panel);

        var fileResult = Assert.Single(panel.Results);
        var hit = Assert.Single(fileResult.Hits);

        await InvokeNavigateToHitAsync(fileResult, hit);

        Assert.False(mainVm.GlobalAutoScrollEnabled);
        Assert.All(mainVm.Tabs, tab => Assert.False(tab.AutoScrollEnabled));
        Assert.Same(tabA, mainVm.SelectedTab);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_AllOpenTabs_ReinsertedFileReturnsToCanonicalDashboardPosition()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");

        await mainVm.CreateGroupCommand.ExecuteAsync(null);
        var dashboard = Assert.Single(mainVm.Groups);
        dashboard.Model.FileIds.Add(tabB.FileId);
        dashboard.Model.FileIds.Add(tabA.FileId);
        mainVm.ToggleGroupSelection(dashboard);
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        tabA = FindScopedTab(mainVm, @"C:\logs\a.log", dashboard.Id);
        tabB = FindScopedTab(mainVm, @"C:\logs\b.log", dashboard.Id);
        dashboard.RefreshMemberFiles(
            mainVm.Tabs,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [tabB.FileId] = tabB.FilePath,
                [tabA.FileId] = tabA.FilePath
            },
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [tabB.FileId] = true,
                [tabA.FileId] = true
            },
            selectedFileId: null,
            showFullPath: false);
        tabA.TotalLines = 10;
        tabB.TotalLines = 10;

        search.SearchFileHandler = (filePath, request) =>
        {
            if (string.Equals(filePath, tabA.FilePath, StringComparison.OrdinalIgnoreCase) &&
                request.StartLineNumber == 11 &&
                request.EndLineNumber == 11)
            {
                return new SearchResult
                {
                    FilePath = filePath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 11, LineText = "A tail", MatchStart = 0, MatchLength = 1 }
                    }
                };
            }

            if (string.Equals(filePath, tabB.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                if (request.StartLineNumber == 11 && request.EndLineNumber == 11)
                {
                    return new SearchResult
                    {
                        FilePath = filePath,
                        Hits = new List<SearchHit>
                        {
                            new() { LineNumber = 11, LineText = "B old", MatchStart = 0, MatchLength = 1 }
                        }
                    };
                }

                if (request.StartLineNumber == 1 && request.EndLineNumber == 2)
                {
                    return new SearchResult
                    {
                        FilePath = filePath,
                        Hits = new List<SearchHit>
                        {
                            new() { LineNumber = 1, LineText = "B new 1", MatchStart = 0, MatchLength = 1 },
                            new() { LineNumber = 2, LineText = "B new 2", MatchStart = 0, MatchLength = 1 }
                        }
                    };
                }
            }

            return new SearchResult { FilePath = filePath };
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            TargetMode = SearchFilterTargetMode.AllOpenTabs,
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        tabA.TotalLines = 11;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].FilePath == tabA.FilePath);

        tabB.TotalLines = 11;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 2 &&
            panel.Results.Select(result => result.FilePath).SequenceEqual(new[] { tabB.FilePath, tabA.FilePath }));

        await tabB.ResetLineIndexAsync();
        tabB.TotalLines = 0;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].FilePath == tabA.FilePath);

        tabB.TotalLines = 2;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 2 &&
            panel.Results.Select(result => result.FilePath).SequenceEqual(new[] { tabB.FilePath, tabA.FilePath }) &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 1, 2 }));

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_WithTimestampRange_PassesRangeToSearchRequest()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            FromTimestamp = "2026-03-09 19:49:10",
            ToTimestamp = "2026-03-09 19:49:20"
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastRequest);
        Assert.Equal("2026-03-09 19:49:10", search.LastRequest!.FromTimestamp);
        Assert.Equal("2026-03-09 19:49:20", search.LastRequest.ToTimestamp);
    }

    [Fact]
    public async Task ExecuteSearch_InvalidTimestampRange_IsIgnoredAndSearchRuns()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            FromTimestamp = "invalid"
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.True(search.SearchFileCallCount + search.SearchFilesCallCount > 0);
        Assert.DoesNotContain("Invalid", panel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteSearch_WithTimestampRange_NoParseableTimestamps_ShowsGenericStatus()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>(),
                    HasParseableTimestamps = false
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            FromTimestamp = "19:49:10.000",
            ToTimestamp = "19:50:00.000"
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal("0 in 0 file(s)", panel.ResultsHeaderText);
    }

    [Fact]
    public async Task DiskSearch_StartMonitoringNewMatches_AppendsOnlyNewMatches()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 5, LineText = "old match", MatchStart = 0, MatchLength = 3 }
                    }
                }
            }
        };
        search.SearchFileAsyncHandler = (filePath, request, _, _) =>
        {
            var startLine = request.StartLineNumber.GetValueOrDefault();
            var endLine = request.EndLineNumber.GetValueOrDefault();
            var hits = new List<SearchHit>();
            if (startLine <= 11 && endLine >= 11)
                hits.Add(new SearchHit { LineNumber = 11, LineText = "new match", MatchStart = 0, MatchLength = 3 });
            if (startLine <= 12 && endLine >= 12)
                hits.Add(new SearchHit { LineNumber = 12, LineText = "gap match", MatchStart = 0, MatchLength = 3 });
            if (startLine <= 13 && endLine >= 13)
                hits.Add(new SearchHit { LineNumber = 13, LineText = "reenabled match", MatchStart = 0, MatchLength = 3 });

            return Task.FromResult(new SearchResult
            {
                FilePath = filePath,
                Hits = hits
            });
        };

        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        var tab = Assert.Single(mainVm.Tabs);
        tab.TotalLines = 10;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "match",
            SearchDataMode = SearchDataMode.DiskSnapshot
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.True(panel.IsMonitorNewMatchesVisible);
        Assert.True(panel.IsMonitorNewMatchesControlVisible);
        Assert.False(panel.IsMonitorNewMatchesChecked);

        tab.TotalLines = 12;

        panel.StartMonitoringNewMatchesCommand.Execute(null);

        Assert.True(panel.IsMonitorNewMatchesChecked);
        Assert.True(panel.IsMonitorNewMatchesControlVisible);
        Assert.Contains("Monitoring new matches", panel.ResultsHeaderText, StringComparison.Ordinal);

        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 3 &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 5, 11, 12 }));

        Assert.Contains(search.SearchFileRequests, request =>
            request.StartLineNumber == 11 && request.EndLineNumber == 12);

        panel.StopMonitoringNewMatchesCommand.Execute(null);
        Assert.False(panel.IsMonitorNewMatchesChecked);
        Assert.True(panel.IsMonitorNewMatchesControlVisible);

        tab.TotalLines = 13;
        await Task.Delay(250);
        Assert.Equal(new long[] { 5, 11, 12 }, panel.Results[0].Hits.Select(hit => hit.LineNumber));

        panel.StartMonitoringNewMatchesCommand.Execute(null);

        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 4 &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 5, 11, 12, 13 }));

        Assert.Contains(search.SearchFileRequests, request =>
            request.StartLineNumber == 11 && request.EndLineNumber == 13);
    }

    [Fact]
    public async Task StartMonitoringNewMatches_IsNotAvailableAfterTailSearch()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "match",
            SearchDataMode = SearchDataMode.Tail
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.False(panel.IsMonitorNewMatchesVisible);
        Assert.False(panel.IsMonitorNewMatchesControlVisible);
        Assert.False(panel.IsMonitorNewMatchesChecked);
    }

    [Fact]
    public async Task StartMonitoringNewMatches_ContentChangedAfterDiskSearch_DoesNotStart()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 5, LineText = "old match", MatchStart = 0, MatchLength = 3 }
                    }
                }
            }
        };

        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        var tab = Assert.Single(mainVm.Tabs);
        tab.TotalLines = 10;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "match",
            SearchDataMode = SearchDataMode.DiskSnapshot
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        await tab.ResetLineIndexAsync();

        panel.StartMonitoringNewMatchesCommand.Execute(null);

        Assert.False(panel.IsMonitorNewMatchesChecked);
        Assert.True(panel.IsMonitorNewMatchesControlVisible);
        Assert.Equal("Monitoring could not start because file content changed.", panel.ResultsHeaderText);
    }

    [Fact]
    public async Task DiskSearch_CurrentTabResults_ShowsMonitorNewMatches()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "match", MatchStart = 0, MatchLength = 5 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "match",
            SearchDataMode = SearchDataMode.DiskSnapshot,
            TargetMode = SearchFilterTargetMode.CurrentTab
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.True(panel.IsMonitorNewMatchesVisible);
        Assert.True(panel.IsMonitorNewMatchesControlVisible);
        Assert.False(panel.IsMonitorNewMatchesChecked);
    }

    [Fact]
    public async Task DiskSearch_AllOpenTabsResults_ShowsMonitorNewMatches()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "match a", MatchStart = 0, MatchLength = 5 }
                    }
                },
                new SearchResult
                {
                    FilePath = @"C:\logs\b.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 2, LineText = "match b", MatchStart = 0, MatchLength = 5 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "match",
            SearchDataMode = SearchDataMode.DiskSnapshot,
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.True(panel.IsMonitorNewMatchesVisible);
        Assert.True(panel.IsMonitorNewMatchesControlVisible);
        Assert.False(panel.IsMonitorNewMatchesChecked);
    }

    [Fact]
    public async Task StartMonitoringNewMatches_AllOpenTabs_MonitorsAllFilesFromSearch()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "match a", MatchStart = 0, MatchLength = 5 }
                    }
                }
            }
        };
        search.SearchFileAsyncHandler = (filePath, request, _, _) =>
        {
            var startLine = request.StartLineNumber.GetValueOrDefault();
            var endLine = request.EndLineNumber.GetValueOrDefault();
            var hits = new List<SearchHit>();
            if (filePath.EndsWith("a.log", StringComparison.OrdinalIgnoreCase) &&
                startLine <= 11 &&
                endLine >= 11)
            {
                hits.Add(new SearchHit { LineNumber = 11, LineText = "tail a", MatchStart = 0, MatchLength = 4 });
            }

            if (filePath.EndsWith("b.log", StringComparison.OrdinalIgnoreCase) &&
                startLine <= 11 &&
                endLine >= 11)
            {
                hits.Add(new SearchHit { LineNumber = 11, LineText = "tail b", MatchStart = 0, MatchLength = 4 });
            }

            return Task.FromResult(new SearchResult
            {
                FilePath = filePath,
                Hits = hits
            });
        };

        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");
        tabA.TotalLines = 10;
        tabB.TotalLines = 10;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "match",
            SearchDataMode = SearchDataMode.DiskSnapshot,
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(new[] { @"C:\logs\a.log" }, panel.Results.Select(result => result.FilePath).ToArray());

        tabA.TotalLines = 12;
        panel.StartMonitoringNewMatchesCommand.Execute(null);
        Assert.True(panel.IsMonitorNewMatchesChecked);
        Assert.True(panel.IsMonitorNewMatchesControlVisible);
        Assert.Equal("Monitor new matches in the files from this search.", panel.MonitorNewMatchesToolTip);

        tabB.TotalLines = 11;

        await WaitForConditionAsync(() =>
            panel.Results.Count == 2 &&
            panel.Results.Select(result => result.FilePath).SequenceEqual(new[] { @"C:\logs\a.log", @"C:\logs\b.log" }) &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 1, 11 }) &&
            panel.Results[1].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 11 }));
        Assert.Contains(search.SearchFileRequests, request =>
            request.StartLineNumber == 11 && request.EndLineNumber == 12);
        Assert.Contains(search.SearchFileRequests, request =>
            request.StartLineNumber == 11 && request.EndLineNumber == 11);
    }

    [Fact]
    public async Task StartMonitoringNewMatches_ZeroHitDiskSearch_AddsResultWhenNewMatchAppears()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        search.SearchFileAsyncHandler = (filePath, request, _, _) =>
        {
            var startLine = request.StartLineNumber.GetValueOrDefault();
            var endLine = request.EndLineNumber.GetValueOrDefault();
            var hits = new List<SearchHit>();
            if (filePath.EndsWith("b.log", StringComparison.OrdinalIgnoreCase) &&
                startLine <= 6 &&
                endLine >= 6)
            {
                hits.Add(new SearchHit { LineNumber = 6, LineText = "new match", MatchStart = 4, MatchLength = 5 });
            }

            return Task.FromResult(new SearchResult
            {
                FilePath = filePath,
                Hits = hits
            });
        };

        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");
        tabA.TotalLines = 5;
        tabB.TotalLines = 5;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "match",
            SearchDataMode = SearchDataMode.DiskSnapshot,
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Empty(panel.Results);
        Assert.True(panel.IsMonitorNewMatchesVisible);
        Assert.True(panel.IsMonitorNewMatchesControlVisible);
        Assert.False(panel.IsMonitorNewMatchesChecked);

        panel.StartMonitoringNewMatchesCommand.Execute(null);
        Assert.True(panel.IsMonitorNewMatchesChecked);

        tabB.TotalLines = 6;

        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].FilePath == @"C:\logs\b.log" &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 6 }));
        Assert.Contains(search.SearchFileRequests, request =>
            request.StartLineNumber == 6 && request.EndLineNumber == 6);
    }

    [Fact]
    public async Task DiskSearch_ResultSetMonitoring_RemainsAvailableWhenTargetAndSourceChange()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "old", MatchStart = 0, MatchLength = 3 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "old",
            SearchDataMode = SearchDataMode.DiskSnapshot
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
        Assert.True(panel.IsMonitorNewMatchesVisible);
        Assert.True(panel.IsMonitorNewMatchesControlVisible);
        Assert.False(panel.IsMonitorNewMatchesChecked);
        Assert.Equal("Monitor new matches in the file from this search.", panel.MonitorNewMatchesToolTip);

        panel.SearchDataMode = SearchDataMode.Tail;
        Assert.True(panel.IsMonitorNewMatchesVisible);
        Assert.True(panel.IsMonitorNewMatchesControlVisible);

        panel.StartMonitoringNewMatchesCommand.Execute(null);
        Assert.True(panel.IsMonitorNewMatchesChecked);

        panel.TargetMode = SearchFilterTargetMode.CurrentTab;
        panel.SearchDataMode = SearchDataMode.DiskSnapshot;

        Assert.True(panel.IsMonitorNewMatchesChecked);
        Assert.Contains("Monitoring new matches", panel.ResultsHeaderText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchScratchpad_RestoreCachedResults_DoesNotEagerlyMaterializeHitViewModels()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService
        {
            NextResults = new[]
            {
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 10, LineText = "ten", MatchStart = 0, MatchLength = 3 },
                        new() { LineNumber = 20, LineText = "twenty", MatchStart = 0, MatchLength = 6 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var tabA = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\b.log");
        mainVm.SelectedTab = tabA;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "ten"
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var initialResult = Assert.Single(panel.Results);
        initialResult.IsExpanded = true;
        Assert.False(initialResult.HasMaterializedHits);

        mainVm.SelectedTab = tabB;
        panel.OnSelectedTabChanged(tabB);
        Assert.Empty(panel.Results);
        Assert.Equal(SelectedTabChangedStatusText, panel.ResultsHeaderText);

        mainVm.SelectedTab = tabA;
        panel.OnSelectedTabChanged(tabA);

        Assert.Empty(panel.Results);
        Assert.Equal(SelectedTabChangedStatusText, panel.ResultsHeaderText);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 4000, int pollIntervalMs = 25)
    {
        var startedAt = DateTime.UtcNow;
        while (!condition() && (DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
            await Task.Delay(pollIntervalMs);

        Assert.True(condition(), "Timed out waiting for condition.");
    }

    private static void AssertRequestMatchesCriteria(
        SearchRequest request,
        string query,
        bool isRegex,
        bool caseSensitive,
        string? fromTimestamp,
        string? toTimestamp)
    {
        Assert.Equal(query, request.Query);
        Assert.Equal(isRegex, request.IsRegex);
        Assert.Equal(caseSensitive, request.CaseSensitive);
        Assert.Equal(fromTimestamp, request.FromTimestamp);
        Assert.Equal(toTimestamp, request.ToTimestamp);
    }

    private static void RefreshDashboardMemberFiles(
        LogGroupViewModel dashboard,
        params (string FileId, string FilePath)[] files)
    {
        dashboard.ReplaceMemberFiles(files.Select(file => new GroupFileMemberViewModel(
            file.FileId,
            Path.GetFileName(file.FilePath),
            file.FilePath,
            showFullPath: false)));
    }
}
