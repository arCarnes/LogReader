using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

namespace LogReader.Tests;

public class SearchPanelViewModelTests
{
    private sealed class StubLogFileRepository : ILogFileRepository
    {
        private readonly List<LogFileEntry> _entries = new();

        public Task<List<LogFileEntry>> GetAllAsync() => Task.FromResult(_entries.ToList());
        public Task<LogFileEntry?> GetByIdAsync(string id) => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));
        public Task<LogFileEntry?> GetByPathAsync(string filePath)
            => Task.FromResult(_entries.FirstOrDefault(e => string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase)));
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
        public Task UpdateAsync(LogGroup group) => Task.CompletedTask;
        public Task DeleteAsync(string id) { _groups.RemoveAll(g => g.Id == id); return Task.CompletedTask; }
        public Task ReorderAsync(List<string> orderedIds) => Task.CompletedTask;
        public Task ExportViewAsync(string exportPath) => Task.CompletedTask;
        public Task<ViewExport?> ImportViewAsync(string importPath) => Task.FromResult<ViewExport?>(null);
    }

    private sealed class StubSessionRepository : ISessionRepository
    {
        public SessionState State { get; set; } = new();
        public Task<SessionState> LoadAsync() => Task.FromResult(State);
        public Task SaveAsync(SessionState state) { State = state; return Task.CompletedTask; }
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
        public int SearchFilesCallCount { get; private set; }
        public int SearchFileCallCount { get; private set; }
        public List<SearchRequest> SearchFileRequests { get; } = new();

        public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
        {
            SearchFileCallCount++;
            SearchFileRequests.Add(new SearchRequest
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
            });
            if (SearchFileHandler != null)
                return Task.FromResult(SearchFileHandler(filePath, request));

            return Task.FromResult(new SearchResult { FilePath = filePath });
        }

        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
        {
            SearchFilesCallCount++;
            LastRequest = request;
            LastEncodings = new Dictionary<string, FileEncoding>(fileEncodings, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(NextResults);
        }
    }

    private static MainViewModel CreateMainViewModel(ILogFileRepository fileRepo, ILogGroupRepository groupRepo, ISettingsRepository settingsRepo, ISearchService search)
    {
        return new MainViewModel(
            fileRepo,
            groupRepo,
            new StubSessionRepository(),
            settingsRepo,
            new StubLogReaderService(),
            search,
            new StubFileTailService(),
            enableLifecycleTimer: false);
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
    public async Task ExecuteSearch_AllFiles_UsesAllOpenTabs()
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
            AllFiles = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastRequest);
        Assert.Equal(2, search.LastRequest!.FilePaths.Count);
        Assert.Equal(FileEncoding.Ansi, search.LastEncodings![@"C:\logs\a.log"]);
        Assert.Equal(FileEncoding.Utf16, search.LastEncodings![@"C:\logs\b.log"]);
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

        Assert.Equal("No files to search", panel.StatusText);
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
    public async Task ExecuteSearch_DiskSnapshotMode_DescendingLineOrder_SortsHitsHighestToLowest()
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
                        new() { LineNumber = 5, LineText = "line 5", MatchStart = 0, MatchLength = 4 },
                        new() { LineNumber = 1, LineText = "line 1", MatchStart = 0, MatchLength = 4 },
                        new() { LineNumber = 3, LineText = "line 3", MatchStart = 0, MatchLength = 4 }
                    }
                }
            }
        };
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "line",
            IsDescendingLineOrder = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        var fileResult = Assert.Single(panel.Results);
        Assert.Equal(new long[] { 5, 3, 1 }, fileResult.Hits.Select(h => h.LineNumber).ToArray());
    }

    [Fact]
    public async Task ExecuteSearch_SnapshotAndTailMode_DescendingLineOrder_KeepsCombinedResultsSorted()
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
            if (request.StartLineNumber == 1 && request.EndLineNumber == 10)
            {
                return new SearchResult
                {
                    FilePath = selected.FilePath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 2, LineText = "snapshot 2", MatchStart = 0, MatchLength = 8 },
                        new() { LineNumber = 10, LineText = "snapshot 10", MatchStart = 0, MatchLength = 9 }
                    }
                };
            }

            if (request.StartLineNumber == 11 && request.EndLineNumber == 12)
            {
                return new SearchResult
                {
                    FilePath = selected.FilePath,
                    Hits = new List<SearchHit>
                    {
                        new() { LineNumber = 11, LineText = "tail 11", MatchStart = 0, MatchLength = 4 },
                        new() { LineNumber = 12, LineText = "tail 12", MatchStart = 0, MatchLength = 4 }
                    }
                };
            }

            return new SearchResult { FilePath = selected.FilePath };
        };

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error",
            IsSnapshotAndTailMode = true,
            IsDescendingLineOrder = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);
        selected.TotalLines = 12;
        await WaitForConditionAsync(() => panel.Results.Count == 1 && panel.Results[0].HitCount == 4);

        var fileResult = Assert.Single(panel.Results);
        Assert.Equal(new long[] { 12, 11, 10, 2 }, fileResult.Hits.Select(h => h.LineNumber).ToArray());
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
    public async Task ExecuteSearch_InvalidTimestampRange_SetsStatusAndSkipsSearch()
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

        Assert.Equal(0, search.SearchFilesCallCount);
        Assert.Equal(0, search.SearchFileCallCount);
        Assert.Contains("Invalid 'From' timestamp", panel.StatusText);
    }

    [Fact]
    public async Task ExecuteSearch_WithTimestampRange_NoParseableTimestamps_ShowsClearStatus()
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

        Assert.Equal("No parseable timestamps found in 1 file for the selected time range.", panel.StatusText);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 4000, int pollIntervalMs = 25)
    {
        var startedAt = DateTime.UtcNow;
        while (!condition() && (DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
            await Task.Delay(pollIntervalMs);

        Assert.True(condition(), "Timed out waiting for condition.");
    }
}
