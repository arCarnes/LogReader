using LogReader.App.Models;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

namespace LogReader.Tests;

public class SearchPanelViewModelTests : IDisposable
{
    private const string ScopeExitCancelledStatusText = "Search stopped when leaving this scope. Rerun search to refresh these results.";
    private const string SelectedTabChangedStatusText = "Search results cleared because the selected tab changed. Rerun search to refresh.";
    private const string SearchOutputStaleStatusText = "Search output is for a previous context, target, or source. Rerun search to refresh.";
    private readonly List<MainViewModel> _createdViewModels = new();

    public void Dispose()
    {
        for (var i = _createdViewModels.Count - 1; i >= 0; i--)
            _createdViewModels[i].Dispose();
    }

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
        public Task<AppSettings> LoadFromFileAsync(string filePath) => Task.FromResult(Settings);
        public Task SaveToFileAsync(string filePath, AppSettings settings) => Task.CompletedTask;
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

        public async Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
        {
            SearchFileCallCount++;
            SearchFileRequests.Add(CloneSearchRequest(request));
            if (SearchFileAsyncHandler != null)
                return CompleteRangeResult(
                    await SearchFileAsyncHandler(filePath, request, encoding, ct),
                    request);

            if (SearchFileHandler != null)
                return CompleteRangeResult(SearchFileHandler(filePath, request), request);

            return CompleteRangeResult(new SearchResult { FilePath = filePath }, request);
        }

        public async Task<SearchResult> SearchFileRangeAsync(
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
                return CompleteRangeResult(
                    await SearchFileRangeAsyncHandler(filePath, request, encoding, readLinesAsync, ct),
                    request);

            if (SearchFileAsyncHandler != null)
                return CompleteRangeResult(
                    await SearchFileAsyncHandler(filePath, request, encoding, ct),
                    request);

            if (SearchFileHandler != null)
                return CompleteRangeResult(SearchFileHandler(filePath, request), request);

            return CompleteRangeResult(new SearchResult { FilePath = filePath }, request);
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

        private static SearchResult CompleteRangeResult(SearchResult result, SearchRequest request)
        {
            if (string.IsNullOrWhiteSpace(result.Error))
                result.EvaluatedThroughLine ??= request.EndLineNumber;

            return result;
        }
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

        public Task RunViewActionAsync(Func<Task> operation, string failureCaption = "WeezTail Error")
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
        private readonly List<LogTabViewModel> _tabs;
        private WorkspaceScopeSnapshot _scopeSnapshot;
        private readonly IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> _filterSnapshots;

        public ScopeWorkspaceContextStub(
            LogTabViewModel selectedTab,
            IReadOnlyList<WorkspaceScopeMemberSnapshot> scopeMembership,
            IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot>? filterSnapshots = null)
        {
            _tabs = new List<LogTabViewModel> { selectedTab };
            _scopeSnapshot = new WorkspaceScopeSnapshot(
                WorkspaceScopeKey.FromDashboardId(null),
                _tabs.Select(tab => new WorkspaceOpenTabSnapshot(tab)).ToList(),
                scopeMembership);
            _filterSnapshots = filterSnapshots ?? new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);
            SelectedTab = selectedTab;
        }

        public string? ActiveScopeDashboardId { get; private set; }

        public bool IsDashboardLoading => false;

        public LogTabViewModel? SelectedTab { get; private set; }

        public IReadOnlyList<LogTabViewModel> GetAllTabs() => _tabs;

        public IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot() => _tabs;

        public IReadOnlyList<string> GetSearchResultFileOrderSnapshot()
            => _scopeSnapshot.EffectiveMembership.Select(member => member.FilePath).ToList();

        public IReadOnlyList<string> GetAllOpenTabsExecutionFileOrderSnapshot(string? scopeDashboardId)
            => string.Equals(scopeDashboardId, ActiveScopeDashboardId, StringComparison.Ordinal)
                ? GetSearchResultFileOrderSnapshot()
                : Array.Empty<string>();

        public WorkspaceScopeSnapshot GetActiveScopeSnapshot() => _scopeSnapshot;

        public void SwitchScope(string? dashboardId)
        {
            ActiveScopeDashboardId = dashboardId;
            _scopeSnapshot = _scopeSnapshot with
            {
                ScopeKey = WorkspaceScopeKey.FromDashboardId(dashboardId)
            };
        }

        public void ReplaceSelectedTab(LogTabViewModel selectedTab)
        {
            _tabs.Clear();
            _tabs.Add(selectedTab);
            SelectedTab = selectedTab;
            _scopeSnapshot = _scopeSnapshot with
            {
                OpenTabs = new[] { new WorkspaceOpenTabSnapshot(selectedTab) },
                EffectiveMembership = new[] { new WorkspaceScopeMemberSnapshot(selectedTab.FileId, selectedTab.FilePath) }
            };
        }

        public void ClearTabs()
        {
            _tabs.Clear();
            SelectedTab = null;
            _scopeSnapshot = _scopeSnapshot with
            {
                OpenTabs = Array.Empty<WorkspaceOpenTabSnapshot>()
            };
        }

        public void SetTabs(LogTabViewModel selectedTab, params LogTabViewModel[] tabs)
        {
            _tabs.Clear();
            _tabs.AddRange(tabs);
            SelectedTab = selectedTab;
            _scopeSnapshot = _scopeSnapshot with
            {
                OpenTabs = tabs.Select(tab => new WorkspaceOpenTabSnapshot(tab)).ToList(),
                EffectiveMembership = tabs
                    .DistinctBy(tab => tab.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(tab => new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath))
                    .ToList()
            };
        }

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

        public Task RunViewActionAsync(Func<Task> operation, string failureCaption = "WeezTail Error")
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

    private MainViewModel CreateMainViewModel(ILogFileRepository fileRepo, ILogGroupRepository groupRepo, ISettingsRepository settingsRepo, ISearchService search)
        => CreateMainViewModel(fileRepo, groupRepo, settingsRepo, search, new StubLogReaderService());

    private MainViewModel CreateMainViewModel(
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo,
        ISettingsRepository settingsRepo,
        ISearchService search,
        ILogReaderService logReader)
    {
        var viewModel = TestMainViewModelFactory.Create(
            fileRepo,
            groupRepo,
            settingsRepo,
            logReader,
            search,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            enableLifecycleTimer: false);
        _createdViewModels.Add(viewModel);
        return viewModel;
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

        public FileGenerationToken GenerationToken { get; set; } = FileGenerationToken.Unknown;

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = _lines.Count * 100,
                GenerationToken = GenerationToken
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

    private static SearchResult CreateGenerationAwareSearchResult(
        string filePath,
        int lineNumber,
        string lineText,
        FileGenerationToken generationToken)
    {
        var result = CreateSearchResult(filePath, lineNumber, lineText);
        result.GenerationEvidence = new FileScanGenerationEvidence(
            generationToken,
            FileGenerationCorrelation.Current);
        return result;
    }

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
        Assert.Equal(8_192, search.LastRequest.MaxRetainedLineTextLength);
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
    public async Task StartMonitoring_CappedSnapshotDoesNotRescanHistoricalRemainder()
    {
        var reader = new RecordingLogReaderService(new[]
        {
            "match 1",
            "match 2",
            "match 3",
            "match 4",
            "match 5"
        });
        var search = new RecordingSearchService();
        search.NextResults =
        [
            new SearchResult
            {
                FilePath = @"C:\logs\a.log",
                HitLimitExceeded = true,
                EvaluatedThroughLine = 1,
                Hits = [new SearchHit { LineNumber = 1, LineText = "match 1", MatchLength = 5 }]
            }
        ];
        var mainVm = CreateMainViewModel(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
            new StubSettingsRepository(),
            search,
            reader);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        using var panel = new SearchPanelViewModel(search, mainVm) { Query = "match" };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        panel.StartMonitoringNewMatchesCommand.Execute(null);
        await Task.Delay(100);

        Assert.True(panel.IsMonitorNewMatchesChecked);
        Assert.Equal(0, search.SearchFileRangeCallCount);
    }

    [Fact]
    public async Task ExecuteSearch_SearchWithinFilter_UsesSequentialSearchWithSnapshotLineNumbers()
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

        Assert.Equal(1, search.SearchFilesCallCount);
        Assert.Same(matchingLineNumbers, requestAllowedLines);
    }

    [Theory]
    [InlineData(FilterLineSetMode.IncludeMatching, SearchLineScopeMode.IncludeOnly)]
    [InlineData(FilterLineSetMode.ExcludeMatching, SearchLineScopeMode.Exclude)]
    public async Task ExecuteSearch_FilterScopeGenerationMismatchRejectsRows(
        FilterLineSetMode filterMode,
        SearchLineScopeMode expectedScopeMode)
    {
        using var tab = CreateTab("file-generation-scope", @"C:\logs\a.log");
        var filterToken = FileGenerationToken.Create(21, 201);
        var resultToken = FileGenerationToken.Create(21, 202);
        var snapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = new[] { 1 },
            LineSetMode = filterMode,
            GenerationEvidence = new FileScanGenerationEvidence(
                filterToken,
                FileGenerationCorrelation.Current),
            CorrelatedTabInstanceId = tab.TabInstanceId,
            CorrelatedSearchContentVersion = tab.SearchContentVersion,
            EvaluatedEncoding = tab.EffectiveEncoding,
            FilterRequest = new SearchRequest
            {
                Query = "filter",
                FilePaths = new List<string> { tab.FilePath },
                SourceMode = SearchRequestSourceMode.DiskSnapshot
            }
        };
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) },
            new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [tab.FilePath] = snapshot
            });
        var searchResult = new SearchResult
        {
            FilePath = tab.FilePath,
            Hits = [new SearchHit { LineNumber = 1, LineText = "match", MatchLength = 5 }],
            GenerationEvidence = new FileScanGenerationEvidence(
                resultToken,
                FileGenerationCorrelation.Current),
            EvaluatedThroughLine = 1
        };
        var search = new RecordingSearchService { NextResults = [searchResult] };
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "match",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(expectedScopeMode, search.LastRequest!.LineScopesByFilePath[tab.FilePath].Mode);
        var rejectedResult = Assert.Single(panel.Results);
        Assert.Equal(0, rejectedResult.HitCount);
        Assert.Contains("different file contents or encoding", rejectedResult.Error, StringComparison.Ordinal);
        Assert.Equal(FileGenerationCorrelation.Stale, rejectedResult.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task ExecuteSearch_FilterScopeRejectsRowsWhenTabChangesBeforeResultsPublish()
    {
        using var tab = CreateTab("file-live-generation-scope", @"C:\logs\a.log");
        var snapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = new[] { 1 },
            GenerationEvidence = FileScanGenerationEvidence.Unknown,
            CorrelatedTabInstanceId = tab.TabInstanceId,
            CorrelatedSearchContentVersion = tab.SearchContentVersion,
            EvaluatedEncoding = tab.EffectiveEncoding,
            FilterRequest = new SearchRequest
            {
                Query = "filter",
                FilePaths = new List<string> { tab.FilePath },
                SourceMode = SearchRequestSourceMode.DiskSnapshot
            }
        };
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) },
            new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [tab.FilePath] = snapshot
            });
        var scanStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new RecordingSearchService
        {
            SearchFilesAsyncHandler = async (_, _, _) =>
            {
                scanStarted.TrySetResult(true);
                await releaseScan.Task;
                return
                [
                    new SearchResult
                    {
                        FilePath = tab.FilePath,
                        Hits = [new SearchHit { LineNumber = 1, LineText = "match", MatchLength = 5 }],
                        EvaluatedThroughLine = 1
                    }
                ];
            }
        };
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "match",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };

        var executeTask = panel.ExecuteSearchCommand.ExecuteAsync(null);
        await scanStarted.Task;
        await tab.ResetLineIndexAsync();
        releaseScan.TrySetResult(true);
        await executeTask;

        var rejectedResult = Assert.Single(panel.Results);
        Assert.Equal(0, rejectedResult.HitCount);
        Assert.Contains("different file contents or encoding", rejectedResult.Error, StringComparison.Ordinal);
        Assert.Equal(FileGenerationCorrelation.Stale, rejectedResult.GenerationEvidence.Correlation);
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

        Assert.Equal("1 line(s) in 1 file(s)", panel.ResultsHeaderText);

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

        Assert.Equal("1 line(s) in 1 file(s)", panel.ResultsHeaderText);

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
        Assert.Equal("1 line(s) in 1 file(s)", panel.ResultsHeaderText);
        Assert.Equal(new long[] { 33 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public async Task SearchScratchpad_ScopeSwitch_RestoresResultsBeyondFormerInactiveBudget()
    {
        using var tab = CreateTab("file-1", @"C:\logs\app.log");
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults =
            [
                new SearchResult
                {
                    FilePath = tab.FilePath,
                    Hits = Enumerable.Range(1, 20_001)
                        .Select(lineNumber => new SearchHit
                        {
                            LineNumber = lineNumber,
                            LineText = "needle",
                            MatchStart = 0,
                            MatchLength = 6
                        })
                        .ToList()
                }
            ]
        };
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "needle",
            IsRegex = true,
            CaseSensitive = true,
            FromTimestamp = "2026-07-14 10:00:00",
            ToTimestamp = "2026-07-14 11:00:00"
        };
        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        Assert.Equal(20_001, Assert.Single(panel.Results).HitCount);

        var dashboardScope = WorkspaceScopeKey.FromDashboardId("other");
        panel.OnScopeChanging(dashboardScope);
        workspace.SwitchScope("other");
        panel.OnScopeContextChanged();
        panel.Query = "other";

        var adHocScope = WorkspaceScopeKey.FromDashboardId(null);
        panel.OnScopeChanging(adHocScope);
        workspace.SwitchScope(null);
        panel.OnScopeContextChanged();

        Assert.Equal("needle", panel.Query);
        Assert.True(panel.IsRegex);
        Assert.True(panel.CaseSensitive);
        Assert.Equal("2026-07-14 10:00:00", panel.FromTimestamp);
        Assert.Equal("2026-07-14 11:00:00", panel.ToTimestamp);
        Assert.Equal(20_001, Assert.Single(panel.Results).HitCount);
        Assert.Equal("20,001 line(s) in 1 file(s)", panel.ResultsHeaderText);
        Assert.True(panel.IsMonitorNewMatchesVisible);
    }

    [Fact]
    public async Task ExecuteSearch_AllOpenTabs_ContentResetMarksOnlyAffectedResultStale()
    {
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
            new StubSettingsRepository(),
            search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var tabA = mainVm.Tabs.Single(tab => tab.FilePath == @"C:\logs\a.log");
        var tabB = mainVm.Tabs.Single(tab => tab.FilePath == @"C:\logs\b.log");
        var tokenA = FileGenerationToken.Create(1, 101);
        var tokenB = FileGenerationToken.Create(1, 102);
        Assert.NotNull(tabA.ActiveSession.DebugLineIndex);
        Assert.NotNull(tabB.ActiveSession.DebugLineIndex);
        tabA.ActiveSession.DebugLineIndex!.GenerationToken = tokenA;
        tabB.ActiveSession.DebugLineIndex!.GenerationToken = tokenB;
        search.NextResults =
        [
            CreateGenerationAwareSearchResult(tabA.FilePath, 10, "captured-a", tokenA),
            CreateGenerationAwareSearchResult(tabB.FilePath, 20, "captured-b", tokenB)
        ];

        var panel = mainVm.SearchPanel;
        panel.Query = "captured";
        panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var resultA = panel.Results.Single(result => result.FilePath == tabA.FilePath);
        var resultB = panel.Results.Single(result => result.FilePath == tabB.FilePath);
        var stableRowA = resultA.GetHitRow(0);
        Assert.Equal(FileGenerationCorrelation.Current, resultA.GenerationEvidence.Correlation);
        Assert.Equal(FileGenerationCorrelation.Current, resultB.GenerationEvidence.Correlation);

        await tabA.ResetLineIndexAsync();

        Assert.Equal(FileGenerationCorrelation.Stale, resultA.GenerationEvidence.Correlation);
        Assert.Equal(FileGenerationCorrelation.Current, resultB.GenerationEvidence.Correlation);
        Assert.Same(stableRowA, resultA.GetHitRow(0));
        Assert.Equal("captured-a", resultA.GetHitRow(0).Hit.LineText);
        Assert.Equal("captured-b", resultB.GetHitRow(0).Hit.LineText);
    }

    [Fact]
    public async Task SearchScratchpad_InactiveContentResetMarksRestoredResultStale()
    {
        using var tab = CreateTab("file-1", @"C:\logs\app.log");
        await tab.LoadAsync();
        var token = FileGenerationToken.Create(2, 201);
        Assert.NotNull(tab.ActiveSession.DebugLineIndex);
        tab.ActiveSession.DebugLineIndex!.GenerationToken = token;
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults = [CreateGenerationAwareSearchResult(tab.FilePath, 12, "retained text", token)]
        };
        using var panel = new SearchPanelViewModel(search, workspace) { Query = "retained" };
        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        Assert.Equal(FileGenerationCorrelation.Current, Assert.Single(panel.Results).GenerationEvidence.Correlation);

        var otherScope = WorkspaceScopeKey.FromDashboardId("other");
        panel.OnScopeChanging(otherScope);
        workspace.SwitchScope("other");
        panel.OnScopeContextChanged();
        await tab.ResetLineIndexAsync();

        var originalScope = WorkspaceScopeKey.FromDashboardId(null);
        panel.OnScopeChanging(originalScope);
        workspace.SwitchScope(null);
        panel.OnScopeContextChanged();

        var restored = Assert.Single(panel.Results);
        Assert.Equal(FileGenerationCorrelation.Stale, restored.GenerationEvidence.Correlation);
        var hit = Assert.Single(restored.Hits);
        Assert.Equal(12, hit.LineNumber);
        Assert.Equal("retained text", hit.LineText);
    }

    [Fact]
    public async Task SearchScratchpad_ReopenedTabWithMatchingGenerationRestoresResultAsUnknown()
    {
        using var originalTab = CreateTab("file-1", @"C:\logs\app.log");
        using var reopenedTab = CreateTab("file-1", @"C:\logs\app.log");
        await originalTab.LoadAsync();
        await reopenedTab.LoadAsync();
        var token = FileGenerationToken.Create(3, 301);
        originalTab.ActiveSession.DebugLineIndex!.GenerationToken = token;
        reopenedTab.ActiveSession.DebugLineIndex!.GenerationToken = token;
        var workspace = new ScopeWorkspaceContextStub(
            originalTab,
            new[] { new WorkspaceScopeMemberSnapshot(originalTab.FileId, originalTab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults = [CreateGenerationAwareSearchResult(originalTab.FilePath, 8, "snapshot", token)]
        };
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "snapshot",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };
        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var otherScope = WorkspaceScopeKey.FromDashboardId("other");
        panel.OnScopeChanging(otherScope);
        workspace.SwitchScope("other");
        panel.OnScopeContextChanged();
        workspace.ReplaceSelectedTab(reopenedTab);

        var originalScope = WorkspaceScopeKey.FromDashboardId(null);
        panel.OnScopeChanging(originalScope);
        workspace.SwitchScope(null);
        panel.OnScopeContextChanged();

        var restored = Assert.Single(panel.Results);
        Assert.Equal(FileGenerationCorrelation.Unknown, restored.GenerationEvidence.Correlation);
        Assert.Equal(reopenedTab.TabInstanceId, restored.CorrelatedTabInstanceId);
        Assert.Equal("snapshot", Assert.Single(restored.Hits).LineText);
    }

    [Fact]
    public async Task SearchScratchpad_NoOpenTabPreservesEncodingEvidenceForLaterReopen()
    {
        using var originalTab = CreateTab("file-1", @"C:\logs\app.log");
        using var reopenedTab = CreateTab("file-1", @"C:\logs\app.log");
        await originalTab.LoadAsync();
        await reopenedTab.LoadAsync();
        var token = FileGenerationToken.Create(3, 302);
        originalTab.ActiveSession.DebugLineIndex!.GenerationToken = token;
        reopenedTab.ActiveSession.DebugLineIndex!.GenerationToken = token;
        var workspace = new ScopeWorkspaceContextStub(
            originalTab,
            new[] { new WorkspaceScopeMemberSnapshot(originalTab.FileId, originalTab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults = [CreateGenerationAwareSearchResult(originalTab.FilePath, 9, "snapshot", token)]
        };
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "snapshot",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };
        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        workspace.ClearTabs();
        panel.OnScopeContextChanged();
        var withoutTab = Assert.Single(panel.Results);
        Assert.Equal(FileGenerationCorrelation.Unknown, withoutTab.GenerationEvidence.Correlation);
        Assert.Equal(FileEncoding.Utf8, withoutTab.CorrelatedEncoding);

        reopenedTab.ActiveSession.EffectiveEncoding = FileEncoding.Utf16;
        workspace.ReplaceSelectedTab(reopenedTab);
        panel.OnScopeContextChanged();

        Assert.Equal(FileGenerationCorrelation.Stale, Assert.Single(panel.Results).GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task SearchScratchpad_DuplicatePathPrefersOriginalTabInstanceForCorrelation()
    {
        using var originalTab = CreateTab("file-1", @"C:\logs\app.log");
        using var duplicateTab = CreateTab("file-2", @"C:\logs\app.log");
        await originalTab.LoadAsync();
        await duplicateTab.LoadAsync();
        var token = FileGenerationToken.Create(3, 303);
        originalTab.ActiveSession.DebugLineIndex!.GenerationToken = token;
        duplicateTab.ActiveSession.DebugLineIndex!.GenerationToken = FileGenerationToken.Create(3, 304);
        duplicateTab.ActiveSession.EffectiveEncoding = FileEncoding.Utf16;
        var workspace = new ScopeWorkspaceContextStub(
            originalTab,
            new[] { new WorkspaceScopeMemberSnapshot(originalTab.FileId, originalTab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults = [CreateGenerationAwareSearchResult(originalTab.FilePath, 10, "snapshot", token)]
        };
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "snapshot",
            TargetMode = SearchFilterTargetMode.AllOpenTabs
        };
        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        workspace.SetTabs(originalTab, duplicateTab, originalTab);
        panel.OnScopeContextChanged();

        var restored = Assert.Single(panel.Results);
        Assert.Equal(FileGenerationCorrelation.Current, restored.GenerationEvidence.Correlation);
        Assert.Equal(originalTab.TabInstanceId, restored.CorrelatedTabInstanceId);
    }

    [Fact]
    public async Task ExecuteSearch_ServiceUnknownCorrelationIsNotPromotedByMatchingTabToken()
    {
        var token = FileGenerationToken.Create(3, 305);
        var reader = new RecordingLogReaderService(new[] { "match" }) { GenerationToken = token };
        using var tab = new LogTabViewModel(
            "file-1",
            @"C:\logs\app.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());
        await tab.LoadAsync();
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var result = CreateSearchResult(tab.FilePath, 1, "match");
        result.GenerationEvidence = new FileScanGenerationEvidence(token, FileGenerationCorrelation.Unknown);
        var search = new RecordingSearchService { NextResults = [result] };
        using var panel = new SearchPanelViewModel(search, workspace) { Query = "match" };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(FileGenerationCorrelation.Unknown, Assert.Single(panel.Results).GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task ExecuteSearch_LateIndexTokenPublicationMarksKnownMismatchStale()
    {
        var scannedToken = FileGenerationToken.Create(3, 306);
        var indexedToken = FileGenerationToken.Create(3, 307);
        var reader = new RecordingLogReaderService(new[] { "match" }) { GenerationToken = indexedToken };
        using var tab = new LogTabViewModel(
            "file-1",
            @"C:\logs\app.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults = [CreateGenerationAwareSearchResult(tab.FilePath, 1, "match", scannedToken)]
        };
        using var panel = new SearchPanelViewModel(search, workspace) { Query = "match" };
        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        var result = Assert.Single(panel.Results);
        Assert.Equal(FileGenerationCorrelation.Unknown, result.GenerationEvidence.Correlation);

        await tab.LoadAsync();

        Assert.Equal(FileGenerationCorrelation.Stale, result.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task ExecuteSearch_UnknownGeneration_LaterContentResetBecomesStale()
    {
        using var tab = CreateTab("file-1", @"C:\logs\app.log");
        await tab.LoadAsync();
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults = [CreateSearchResult(tab.FilePath, 4, "unknown snapshot")]
        };
        using var panel = new SearchPanelViewModel(search, workspace) { Query = "unknown" };
        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        var result = Assert.Single(panel.Results);
        Assert.Equal(FileGenerationCorrelation.Unknown, result.GenerationEvidence.Correlation);

        await tab.ResetLineIndexAsync();

        Assert.Equal(FileGenerationCorrelation.Stale, result.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task ExecuteSearch_EncodingChangesDuringScan_CommitsResultAsStale()
    {
        using var tab = CreateTab("file-1", @"C:\logs\app.log");
        await tab.LoadAsync();
        var token = FileGenerationToken.Create(6, 601);
        tab.ActiveSession.DebugLineIndex!.GenerationToken = token;
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var searchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSearch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new RecordingSearchService
        {
            SearchFilesAsyncHandler = async (_, _, ct) =>
            {
                searchStarted.TrySetResult(true);
                await releaseSearch.Task.WaitAsync(ct);
                return new[] { CreateGenerationAwareSearchResult(tab.FilePath, 7, "old decoding", token) };
            }
        };
        using var panel = new SearchPanelViewModel(search, workspace) { Query = "old" };

        var searchTask = InvokeExecuteSearchAsync(panel);
        await searchStarted.Task;
        tab.ActiveSession.EffectiveEncoding = FileEncoding.Utf16;
        releaseSearch.TrySetResult(true);
        await searchTask;

        var result = Assert.Single(panel.Results);
        Assert.Equal(FileGenerationCorrelation.Stale, result.GenerationEvidence.Correlation);
        Assert.Equal("old decoding", Assert.Single(result.Hits).LineText);
    }

    [Fact]
    public async Task ClearResults_DetachesGenerationTracking()
    {
        using var tab = CreateTab("file-1", @"C:\logs\app.log");
        await tab.LoadAsync();
        var token = FileGenerationToken.Create(4, 401);
        tab.ActiveSession.DebugLineIndex!.GenerationToken = token;
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults = [CreateGenerationAwareSearchResult(tab.FilePath, 2, "detached", token)]
        };
        using var panel = new SearchPanelViewModel(search, workspace) { Query = "detached" };
        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        var detachedResult = Assert.Single(panel.Results);

        panel.ClearResultsCommand.Execute(null);
        await tab.ResetLineIndexAsync();

        Assert.Empty(panel.Results);
        Assert.Equal(FileGenerationCorrelation.Current, detachedResult.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task Dispose_DetachesGenerationTracking()
    {
        using var tab = CreateTab("file-1", @"C:\logs\app.log");
        await tab.LoadAsync();
        var token = FileGenerationToken.Create(5, 501);
        tab.ActiveSession.DebugLineIndex!.GenerationToken = token;
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults = [CreateGenerationAwareSearchResult(tab.FilePath, 3, "disposed", token)]
        };
        var panel = new SearchPanelViewModel(search, workspace) { Query = "disposed" };
        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        var detachedResult = Assert.Single(panel.Results);

        panel.Dispose();
        await tab.ResetLineIndexAsync();

        Assert.Equal(FileGenerationCorrelation.Current, detachedResult.GenerationEvidence.Correlation);
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

        Assert.Equal("1 line(s) in 1 file(s)", panel.ResultsHeaderText);

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

        Assert.Equal("1 line(s) in 1 file(s)", panel.ResultsHeaderText);

        mainVm.ToggleGroupSelection(dashboard);

        Assert.Equal("adhoc-state", panel.Query);
        Assert.Equal(SearchFilterTargetMode.AllOpenTabs, panel.TargetMode);
        Assert.Equal(new long[] { 12 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());

        panel.ClearResultsCommand.Execute(null);

        Assert.Equal(string.Empty, panel.Query);
        Assert.Equal(SearchFilterTargetMode.AllOpenTabs, panel.TargetMode);
        Assert.Empty(panel.Results);
        Assert.Equal(string.Empty, panel.StatusText);

        mainVm.ToggleGroupSelection(dashboard);

        Assert.Equal("dashboard-state", panel.Query);
        Assert.Equal(SearchFilterTargetMode.CurrentTab, panel.TargetMode);
        Assert.Equal("1 line(s) in 1 file(s)", panel.ResultsHeaderText);
        Assert.Equal(new long[] { 33 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());

        mainVm.ToggleGroupSelection(dashboard);

        Assert.Equal(string.Empty, panel.Query);
        Assert.Equal(SearchFilterTargetMode.AllOpenTabs, panel.TargetMode);
        Assert.Empty(panel.Results);
        Assert.Equal(string.Empty, panel.StatusText);
    }

    [Fact]
    public async Task ClearResults_WithNoResults_ClearsQuery()
    {
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(new StubLogFileRepository(), new StubLogGroupRepository(), new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();

        var panel = mainVm.SearchPanel;
        panel.Query = "stale search";

        panel.ClearResultsCommand.Execute(null);

        Assert.Equal(string.Empty, panel.Query);
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
    public void SearchExecuteButtonText_ReflectsApplicableFilter()
    {
        var tab = CreateTab("file-1", @"C:\logs\a.log");
        var snapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = new[] { 1, 3, 5 },
            FilterRequest = new SearchRequest()
        };
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) },
            new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [tab.FilePath] = snapshot
            });
        using var panel = new SearchPanelViewModel(new RecordingSearchService(), workspace);

        Assert.Equal("Search (filtered)", panel.SearchExecuteButtonText);

        panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;

        Assert.Equal("Search (filtered)", panel.SearchExecuteButtonText);
    }

    [Fact]
    public void SearchExecuteButtonText_UsesDefaultWhenNoApplicableFilter()
    {
        var tab = CreateTab("file-1", @"C:\logs\a.log");
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        using var panel = new SearchPanelViewModel(new RecordingSearchService(), workspace);

        Assert.Equal("Search", panel.SearchExecuteButtonText);
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
        Assert.Equal(string.Empty, panel.Query);
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
        Assert.All(search.SearchFileRangeRequests.Take(3), request => Assert.Equal(8_192, request.MaxRetainedLineTextLength));

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_ShortRangeReadRetriesUnreadSuffixWithoutSkipping()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        search.SearchFileRangeAsyncHandler = (filePath, request, encoding, readLinesAsync, ct) =>
        {
            var isFirstRange = request.StartLineNumber == 1;
            return Task.FromResult(new SearchResult
            {
                FilePath = filePath,
                EvaluatedThroughLine = isFirstRange ? 2 : request.EndLineNumber,
                Hits = new List<SearchHit>
                {
                    new()
                    {
                        LineNumber = isFirstRange ? 2 : 4,
                        LineText = isFirstRange ? "hit two" : "hit four",
                        MatchStart = 0,
                        MatchLength = 3
                    }
                }
            });
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        selected.TotalLines = 0;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "hit",
            IsTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        selected.TotalLines = 4;
        await WaitForConditionAsync(() =>
            search.SearchFileRangeRequests.Count >= 2 &&
            panel.Results.Count == 1 &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 2, 4 }));

        Assert.Equal(new long?[] { 1, 3 },
            search.SearchFileRangeRequests.Take(2).Select(request => request.StartLineNumber));
        Assert.Equal(new long?[] { 4, 4 },
            search.SearchFileRangeRequests.Take(2).Select(request => request.EndLineNumber));

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

        Assert.Equal("0 line(s) in 0 file(s)", panel.ResultsHeaderText);
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
    public async Task StartMonitoringNewMatches_KnownGenerationMismatchDoesNotStart()
    {
        using var tab = CreateTab("file-1", @"C:\logs\app.log");
        await tab.LoadAsync();
        var searchedToken = FileGenerationToken.Create(8, 801);
        tab.ActiveSession.DebugLineIndex!.GenerationToken = searchedToken;
        tab.TotalLines = 10;
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults = [CreateGenerationAwareSearchResult(tab.FilePath, 5, "old match", searchedToken)]
        };
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "match",
            SearchDataMode = SearchDataMode.DiskSnapshot
        };
        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        tab.ActiveSession.DebugLineIndex.GenerationToken = FileGenerationToken.Create(8, 802);

        panel.StartMonitoringNewMatchesCommand.Execute(null);

        Assert.False(panel.IsMonitorNewMatchesChecked);
        Assert.Equal("Monitoring could not start because file content changed.", panel.ResultsHeaderText);
    }

    [Fact]
    public async Task MonitoringNewMatches_RequestedEncodingChangeStopsBeforeMixingResults()
    {
        using var tab = CreateTab("file-1", @"C:\logs\app.log");
        await tab.LoadAsync();
        var token = FileGenerationToken.Create(8, 803);
        tab.ActiveSession.DebugLineIndex!.GenerationToken = token;
        tab.TotalLines = 10;
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults = [CreateGenerationAwareSearchResult(tab.FilePath, 5, "old match", token)]
        };
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "match",
            SearchDataMode = SearchDataMode.DiskSnapshot
        };
        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        panel.StartMonitoringNewMatchesCommand.Execute(null);
        Assert.True(panel.IsMonitorNewMatchesChecked);

        tab.Encoding = FileEncoding.Utf16;

        await WaitForConditionAsync(() => !panel.IsMonitorNewMatchesChecked);
        Assert.Contains("content or encoding changed", panel.ResultsHeaderText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new long[] { 5 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber));
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
    public async Task DiskSearch_ErrorResult_DoesNotOfferMonitoring()
    {
        var search = new RecordingSearchService
        {
            NextResults =
            [
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Error = "The file changed repeatedly while it was being searched."
                }
            ]
        };
        var mainVm = CreateMainViewModel(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
            new StubSettingsRepository(),
            search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        using var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "match",
            SearchDataMode = SearchDataMode.DiskSnapshot
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        panel.StartMonitoringNewMatchesCommand.Execute(null);

        Assert.False(panel.IsMonitorNewMatchesVisible);
        Assert.False(panel.IsMonitorNewMatchesControlVisible);
        Assert.False(panel.IsMonitorNewMatchesChecked);
        Assert.Equal("The file changed repeatedly while it was being searched.", Assert.Single(panel.Results).Error);
    }

    [Fact]
    public async Task StartMonitoringNewMatches_AllOpenTabsUnknownGenerationAfterReopenDoesNotStart()
    {
        var search = new RecordingSearchService
        {
            NextResults =
            [
                new SearchResult
                {
                    FilePath = @"C:\logs\a.log",
                    Hits = [new SearchHit { LineNumber = 1, LineText = "old match", MatchLength = 5 }]
                }
            ]
        };
        var mainVm = CreateMainViewModel(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
            new StubSettingsRepository(),
            search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        var originalTab = Assert.Single(mainVm.Tabs);
        var panel = mainVm.SearchPanel;
        panel.Query = "match";
        panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        await mainVm.CloseTabCommand.ExecuteAsync(originalTab);
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        var reopenedTab = Assert.Single(mainVm.Tabs);
        Assert.NotEqual(originalTab.TabInstanceId, reopenedTab.TabInstanceId);

        panel.StartMonitoringNewMatchesCommand.Execute(null);

        Assert.False(panel.IsMonitorNewMatchesChecked);
        Assert.Contains("file content changed", panel.ResultsHeaderText, StringComparison.OrdinalIgnoreCase);
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
                    EvaluatedThroughLine = 10,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 1, LineText = "match a", MatchStart = 0, MatchLength = 5 }
                    }
                },
                new SearchResult
                {
                    FilePath = @"C:\logs\b.log",
                    EvaluatedThroughLine = 10
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
        var search = new RecordingSearchService
        {
            NextResults =
            [
                new SearchResult { FilePath = @"C:\logs\a.log", EvaluatedThroughLine = 5 },
                new SearchResult { FilePath = @"C:\logs\b.log", EvaluatedThroughLine = 5 }
            ]
        };
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
    public async Task StartMonitoringNewMatches_ZeroHitSnapshot_FirstResultTracksGenerationRollover()
    {
        using var tab = CreateTab("file-1", @"C:\logs\app.log");
        await tab.LoadAsync();
        var token = FileGenerationToken.Create(9, 901);
        tab.ActiveSession.DebugLineIndex!.GenerationToken = token;
        tab.TotalLines = 5;
        var workspace = new ScopeWorkspaceContextStub(
            tab,
            new[] { new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath) });
        var search = new RecordingSearchService
        {
            NextResults =
            [
                new SearchResult
                {
                    FilePath = tab.FilePath,
                    GenerationEvidence = new FileScanGenerationEvidence(token, FileGenerationCorrelation.Current),
                    EvaluatedThroughLine = 5
                }
            ]
        };
        search.SearchFileRangeAsyncHandler = (filePath, request, encoding, readLinesAsync, ct) =>
            Task.FromResult(new SearchResult
            {
                FilePath = filePath,
                Hits =
                [
                    new SearchHit
                    {
                        LineNumber = 6,
                        LineText = "new match",
                        MatchStart = 4,
                        MatchLength = 5
                    }
                ]
            });
        using var panel = new SearchPanelViewModel(search, workspace)
        {
            Query = "match",
            SearchDataMode = SearchDataMode.DiskSnapshot
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        Assert.Empty(panel.Results);

        panel.StartMonitoringNewMatchesCommand.Execute(null);
        tab.TotalLines = 6;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].Hits.Select(hit => hit.LineNumber).SequenceEqual(new long[] { 6 }));

        var monitoredResult = Assert.Single(panel.Results);
        Assert.Equal(FileGenerationCorrelation.Current, monitoredResult.GenerationEvidence.Correlation);
        Assert.Equal(token, monitoredResult.GenerationEvidence.Token);
        Assert.Equal(tab.TabInstanceId, monitoredResult.CorrelatedTabInstanceId);

        await tab.ResetLineIndexAsync();

        Assert.Equal(FileGenerationCorrelation.Stale, monitoredResult.GenerationEvidence.Correlation);
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
