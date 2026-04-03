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
    private const string SearchResultsClearedStatusText = "Results cleared because context, target, or source changed. Return to the original context to restore them or rerun search.";

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
        public Func<SearchRequest, IDictionary<string, FileEncoding>, CancellationToken, Task<IReadOnlyList<SearchResult>>>? SearchFilesAsyncHandler { get; set; }
        public int SearchFilesCallCount { get; private set; }
        public int SearchFileCallCount { get; private set; }
        public List<SearchRequest> SearchFileRequests { get; } = new();

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

        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
        {
            SearchFilesCallCount++;
            LastRequest = CloneSearchRequest(request);
            LastEncodings = new Dictionary<string, FileEncoding>(fileEncodings, StringComparer.OrdinalIgnoreCase);
            if (SearchFilesAsyncHandler != null)
                return SearchFilesAsyncHandler(request, LastEncodings, ct);

            return Task.FromResult(NextResults);
        }

        private static SearchRequest CloneSearchRequest(SearchRequest request)
        {
            return new SearchRequest
            {
                Query = request.Query,
                IsRegex = request.IsRegex,
                CaseSensitive = request.CaseSensitive,
                FilePaths = request.FilePaths.ToList(),
                AllowedLineNumbersByFilePath = request.AllowedLineNumbersByFilePath.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase),
                StartLineNumber = request.StartLineNumber,
                EndLineNumber = request.EndLineNumber,
                FromTimestamp = request.FromTimestamp,
                ToTimestamp = request.ToTimestamp,
                SourceMode = request.SourceMode
            };
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

        public LogTabViewModel? SelectedTab => _tab;

        public IReadOnlyList<LogTabViewModel> GetAllTabs() => new[] { _tab };

        public IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot() => new[] { _tab };

        public IReadOnlyList<string> GetSearchResultFileOrderSnapshot() => new[] { _tab.FilePath };

        public WorkspaceScopeSnapshot GetActiveScopeSnapshot() => _scopeSnapshot;

        public Task<FileEncoding> ResolveFilterFileEncodingAsync(string filePath, string? scopeDashboardId, CancellationToken ct = default)
            => Task.FromResult(FileEncoding.Utf8);

        public Task<IReadOnlyDictionary<string, LogTabViewModel>> EnsureBackgroundTabsOpenAsync(
            IReadOnlyList<string> filePaths,
            string? scopeDashboardId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, LogTabViewModel>>(
                new Dictionary<string, LogTabViewModel>(StringComparer.OrdinalIgnoreCase)
                {
                    [_tab.FilePath] = _tab
                });

        public LogFilterSession.FilterSnapshot? GetApplicableCurrentTabFilterSnapshot(SearchDataMode sourceMode)
            => null;

        public LogFilterSession.FilterSnapshot? GetApplicableCurrentScopeFilterSnapshot(string filePath, SearchDataMode sourceMode)
        {
            if (!string.Equals(filePath, _tab.FilePath, StringComparison.OrdinalIgnoreCase))
                return null;

            return _scopeSnapshotForFile == null
                ? null
                : LogFilterSession.CloneSnapshot(_scopeSnapshotForFile);
        }

        public IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableCurrentScopeFilterSnapshots(SearchDataMode sourceMode)
            => throw new InvalidOperationException("Bulk scope snapshot lookup should not be used by tail search single-file refresh.");

        public void UpdateRecentTabFilterSnapshot(string filePath, string? scopeDashboardId, LogFilterSession.FilterSnapshot? snapshot)
        {
        }

        public Task NavigateToLineAsync(string filePath, long lineNumber, bool disableAutoScroll = false)
            => Task.CompletedTask;
    }

    private static MainViewModel CreateMainViewModel(ILogFileRepository fileRepo, ILogGroupRepository groupRepo, ISettingsRepository settingsRepo, ISearchService search)
    {
        return new MainViewModel(
            fileRepo,
            groupRepo,
            settingsRepo,
            new StubLogReaderService(),
            search,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            enableLifecycleTimer: false);
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
        Assert.NotNull(search.LastEncodings);
        Assert.Equal(FileEncoding.Utf16Be, search.LastEncodings![@"C:\logs\b.log"]);
    }

    [Fact]
    public async Task ExecuteSearch_CurrentScope_UsesAllOpenTabs()
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
            TargetMode = SearchFilterTargetMode.CurrentScope
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastRequest);
        Assert.Equal(2, search.LastRequest!.FilePaths.Count);
        Assert.Equal(FileEncoding.Ansi, search.LastEncodings![@"C:\logs\a.log"]);
        Assert.Equal(FileEncoding.Utf16, search.LastEncodings![@"C:\logs\b.log"]);
    }

    [Fact]
    public async Task ExecuteSearch_CurrentScope_UsesOnlyTabsVisibleInCurrentScope()
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
            TargetMode = SearchFilterTargetMode.CurrentScope
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastRequest);
        Assert.Equal(new[] { @"C:\logs\b.log" }, search.LastRequest!.FilePaths);
    }

    [Fact]
    public async Task ExecuteSearch_CurrentScope_DashboardScope_OrdersResultsByDashboardMemberOrder()
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

        mainVm.ToggleGroupSelection(dashboard);
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "warn",
            TargetMode = SearchFilterTargetMode.CurrentScope
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { @"C:\logs\b.log", @"C:\logs\a.log" },
            panel.Results.Select(result => result.FilePath).ToArray());
    }

    [Fact]
    public async Task ExecuteSearch_CurrentScope_AdHocScope_OrdersResultsByVisibleTabOrder()
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
            TargetMode = SearchFilterTargetMode.CurrentScope
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { @"C:\logs\b.log", @"C:\logs\a.log" },
            panel.Results.Select(result => result.FilePath).ToArray());
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
        panel.TargetMode = SearchFilterTargetMode.CurrentScope;
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
        Assert.Equal(SearchResultsClearedStatusText, panel.ResultsHeaderText);
        Assert.Empty(panel.Results);

        mainVm.SelectedTab = adHocTabB;
        Assert.Equal("1 in 1 file(s)", panel.ResultsHeaderText);
        Assert.Equal(new long[] { 12 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());

        mainVm.ToggleGroupSelection(dashboard);

        Assert.Equal("dashboard-state", panel.Query);
        Assert.False(panel.IsRegex);
        Assert.False(panel.CaseSensitive);
        Assert.Equal(SearchFilterTargetMode.CurrentScope, panel.TargetMode);
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
        var baseStatus = panel.ResultsHeaderText;

        mainVm.SelectedTab = mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\a.log");
        Assert.Equal(SearchResultsClearedStatusText, panel.ResultsHeaderText);
        Assert.Empty(panel.Results);

        mainVm.SelectedTab = originalTab;
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
    }

    [Fact]
    public async Task SearchScratchpad_TargetChange_ClearsResultsUntilOriginalTargetReturns()
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

        panel.TargetMode = SearchFilterTargetMode.CurrentScope;
        Assert.Equal(SearchResultsClearedStatusText, panel.ResultsHeaderText);
        Assert.Empty(panel.Results);

        panel.TargetMode = SearchFilterTargetMode.CurrentTab;
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
        Assert.Equal(new long[] { 7 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public async Task SearchScratchpad_SourceModeChange_ClearsResultsUntilOriginalSourceReturns()
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
        Assert.Equal(SearchResultsClearedStatusText, panel.ResultsHeaderText);
        Assert.Empty(panel.Results);

        panel.IsDiskSnapshotMode = true;
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
        Assert.Equal(new long[] { 7 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public async Task SearchScratchpad_CurrentScope_OpenTabChangesClearResultsUntilOriginalSetReturns()
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
        panel.TargetMode = SearchFilterTargetMode.CurrentScope;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var baseStatus = panel.ResultsHeaderText;

        await mainVm.OpenFilePathAsync(@"C:\logs\c.log");
        Assert.Equal(SearchResultsClearedStatusText, panel.ResultsHeaderText);
        Assert.Empty(panel.Results);

        await mainVm.CloseTabCommand.ExecuteAsync(mainVm.Tabs.First(tab => tab.FilePath == @"C:\logs\c.log" && tab.IsAdHocScope));
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
    }

    [Fact]
    public async Task SearchScratchpad_CurrentScope_DashboardReorderClearsResultsUntilOriginalOrderReturns()
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
        panel.TargetMode = SearchFilterTargetMode.CurrentScope;

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var baseStatus = panel.ResultsHeaderText;

        await mainVm.ReorderDashboardFileAsync(dashboard, tabC.FileId, tabA.FileId, DropPlacement.Before);
        Assert.Equal(SearchResultsClearedStatusText, panel.ResultsHeaderText);
        Assert.Empty(panel.Results);

        await mainVm.ReorderDashboardFileAsync(dashboard, tabC.FileId, tabB.FileId, DropPlacement.After);
        Assert.Equal(baseStatus, panel.ResultsHeaderText);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_CurrentScope_UsesSingleFileScopeSnapshotLookup()
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
            TargetMode = SearchFilterTargetMode.CurrentScope,
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
        panel.TargetMode = SearchFilterTargetMode.CurrentScope;

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
        Assert.Equal(SearchFilterTargetMode.CurrentScope, panel.TargetMode);
        Assert.Equal(new long[] { 12 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());

        panel.ClearResultsCommand.Execute(null);

        Assert.Equal("adhoc-state", panel.Query);
        Assert.Equal(SearchFilterTargetMode.CurrentScope, panel.TargetMode);
        Assert.Empty(panel.Results);
        Assert.Equal(string.Empty, panel.StatusText);

        mainVm.ToggleGroupSelection(dashboard);

        Assert.Equal("dashboard-state", panel.Query);
        Assert.Equal(SearchFilterTargetMode.CurrentTab, panel.TargetMode);
        Assert.Equal("1 in 1 file(s)", panel.ResultsHeaderText);
        Assert.Equal(new long[] { 33 }, Assert.Single(panel.Results).Hits.Select(hit => hit.LineNumber).ToArray());

        mainVm.ToggleGroupSelection(dashboard);

        Assert.Equal("adhoc-state", panel.Query);
        Assert.Equal(SearchFilterTargetMode.CurrentScope, panel.TargetMode);
        Assert.Empty(panel.Results);
        Assert.Equal(string.Empty, panel.StatusText);
    }

    [Fact]
    public async Task SearchScratchpad_RestoredResults_NavigateToHitUsesCurrentScopeTabInstance()
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
        panel.TargetMode = SearchFilterTargetMode.CurrentScope;

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
    public async Task ExecuteSearch_TailMode_CurrentScope_OrdersNewResultGroupsByDashboardMemberOrder()
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
            TargetMode = SearchFilterTargetMode.CurrentScope,
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
    public async Task ExecuteSearch_SnapshotAndTailMode_BackfillsSnapshotRange()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var selected = mainVm.SelectedTab!;
        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsSnapshotAndTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.True(panel.IsSearching);
        Assert.Contains(search.SearchFileRequests, r =>
            r.StartLineNumber == 1 &&
            r.EndLineNumber == selected.TotalLines &&
            r.FilePaths.SequenceEqual(new[] { selected.FilePath }));
        panel.CancelSearchCommand.Execute(null);
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

        selected.TotalLines = 12;
        await Task.Delay(150);

        Assert.Equal(1, search.SearchFileCallCount);
        Assert.Empty(panel.Results);
        Assert.Equal(string.Empty, panel.StatusText);
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
            TargetMode = SearchFilterTargetMode.CurrentScope
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
    public async Task ExecuteSearch_SnapshotAndTailMode_CurrentScope_KeepsDashboardMemberOrderAcrossSnapshotAndTail()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\c.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

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

        tabA = FindScopedTab(mainVm, @"C:\logs\a.log", dashboard.Id);
        tabB = FindScopedTab(mainVm, @"C:\logs\b.log", dashboard.Id);
        tabC = FindScopedTab(mainVm, @"C:\logs\c.log", dashboard.Id);
        tabA.TotalLines = 10;
        tabB.TotalLines = 10;
        tabC.TotalLines = 10;

        search.SearchFileHandler = (filePath, request) =>
        {
            if (request.StartLineNumber == 1 && request.EndLineNumber == 10)
            {
                if (string.Equals(filePath, tabA.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    return new SearchResult
                    {
                        FilePath = filePath,
                        Hits = new List<SearchHit>
                        {
                            new() { LineNumber = 10, LineText = "A snapshot", MatchStart = 0, MatchLength = 1 }
                        }
                    };
                }

                if (string.Equals(filePath, tabC.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    return new SearchResult
                    {
                        FilePath = filePath,
                        Hits = new List<SearchHit>
                        {
                            new() { LineNumber = 10, LineText = "C snapshot", MatchStart = 0, MatchLength = 1 }
                        }
                    };
                }
            }

            if (request.StartLineNumber == 11 &&
                request.EndLineNumber == 11 &&
                string.Equals(filePath, tabB.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return new SearchResult
                {
                    FilePath = filePath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 11, LineText = "B tail", MatchStart = 0, MatchLength = 1 }
                    }
                };
            }

            return new SearchResult { FilePath = filePath };
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            TargetMode = SearchFilterTargetMode.CurrentScope,
            IsSnapshotAndTailMode = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        await WaitForConditionAsync(() =>
            panel.Results.Count == 2 &&
            panel.Results.Select(result => result.FilePath).SequenceEqual(new[] { tabA.FilePath, tabC.FilePath }));

        tabB.TotalLines = 11;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 3 &&
            panel.Results.Select(result => result.FilePath).SequenceEqual(new[] { tabA.FilePath, tabB.FilePath, tabC.FilePath }));

        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task ExecuteSearch_TailMode_CurrentScope_ReinsertedFileReturnsToCanonicalDashboardPosition()
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
            TargetMode = SearchFilterTargetMode.CurrentScope,
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

        mainVm.SelectedTab = tabA;
        panel.OnSelectedTabChanged(tabA);

        var restoredResult = Assert.Single(panel.Results);
        Assert.True(restoredResult.IsExpanded);
        Assert.False(restoredResult.HasMaterializedHits);
        Assert.Equal(3, panel.VisibleRows.Count);

        var hitRow = Assert.IsType<SearchResultHitRowViewModel>(panel.VisibleRows[1]);
        Assert.Equal(10, hitRow.Hit.LineNumber);
        Assert.False(restoredResult.HasMaterializedHits);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 4000, int pollIntervalMs = 25)
    {
        var startedAt = DateTime.UtcNow;
        while (!condition() && (DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
            await Task.Delay(pollIntervalMs);

        Assert.True(condition(), "Timed out waiting for condition.");
    }
}
