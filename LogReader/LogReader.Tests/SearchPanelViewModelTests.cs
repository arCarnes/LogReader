using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

namespace LogReader.Tests;

public class SearchPanelViewModelTests
{
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
                WholeWord = request.WholeWord,
                FilePaths = request.FilePaths.ToList(),
                StartLineNumber = request.StartLineNumber,
                EndLineNumber = request.EndLineNumber,
                FromTimestamp = request.FromTimestamp,
                ToTimestamp = request.ToTimestamp
            };
        }
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
            new LogTimestampNavigationService(),
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
    public async Task ExecuteSearch_InvalidReplacement_CancelsActiveTailSessionBeforeReturningValidationError()
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

        panel.FromTimestamp = "invalid";
        await InvokeExecuteSearchAsync(panel);

        selected.TotalLines = 12;
        await Task.Delay(700);

        Assert.False(panel.IsSearching);
        Assert.Equal(0, search.SearchFileCallCount);
        Assert.Contains("Invalid 'From' timestamp", panel.StatusText);
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
    public async Task ExecuteSearch_TailMode_SuccessfulRetry_ClearsPreviousFileError()
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
        search.SearchFileHandler = (_, _) =>
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
                        new() { LineNumber = 12, LineText = "recovered hit", MatchStart = 0, MatchLength = 3 }
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

        selected.TotalLines = 12;
        await WaitForConditionAsync(() =>
            panel.Results.Count == 1 &&
            panel.Results[0].HitCount == 1 &&
            panel.Results[0].Error == null);

        var fileResult = Assert.Single(panel.Results);
        Assert.Null(fileResult.Error);
        Assert.Single(fileResult.Hits);
        panel.CancelSearchCommand.Execute(null);
    }

    [Fact]
    public async Task NavigateToHit_MultiTabWorkspace_DisablesOnlyTargetTabAutoScroll()
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
            AllFiles = true
        };

        await InvokeExecuteSearchAsync(panel);

        var fileResult = Assert.Single(panel.Results);
        var hit = Assert.Single(fileResult.Hits);

        await InvokeNavigateToHitAsync(fileResult, hit);

        Assert.True(mainVm.GlobalAutoScrollEnabled);
        Assert.False(tabA.AutoScrollEnabled);
        Assert.True(tabB.AutoScrollEnabled);
        Assert.Same(tabA, mainVm.SelectedTab);
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

    [Fact]
    public async Task GoToLine_InvalidInput_SetsGoToErrorText_AndLeavesSearchStatusUntouched()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            StatusText = "3 in 1 file(s)",
            NavigateLineNumber = "abc"
        };

        await panel.GoToLineCommand.ExecuteAsync(null);

        Assert.Equal("Invalid line number. Enter a whole number greater than 0.", panel.GoToErrorText);
        Assert.Equal("3 in 1 file(s)", panel.StatusText);
    }

    [Fact]
    public async Task GoToTimestamp_InvalidInput_SetsGoToErrorText()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            NavigateTimestamp = "not a timestamp"
        };

        await panel.GoToTimestampCommand.ExecuteAsync(null);

        Assert.Equal("Invalid timestamp. Use ISO-8601, yyyy-MM-dd HH:mm:ss, or HH:mm:ss.fff.", panel.GoToErrorText);
    }

    [Fact]
    public async Task GoToTimestamp_NoParseableTimestamps_SetsGoToErrorText()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-searchpanel-ts-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path, "INFO one\nWARN two\nERROR three\n");
            await mainVm.OpenFilePathAsync(path);

            var panel = new SearchPanelViewModel(search, mainVm)
            {
                NavigateTimestamp = "2026-03-09 19:49:26"
            };

            await panel.GoToTimestampCommand.ExecuteAsync(null);

            Assert.Equal("No parseable timestamps found in the current file.", panel.GoToErrorText);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task GoToTimestamp_ExactMatch_LeavesGoToErrorTextEmpty()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-searchpanel-ts-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path,
                "2026-03-09 19:49:10 INFO one\n2026-03-09 19:49:20 INFO two\n2026-03-09 19:49:30 INFO three\n");
            await mainVm.OpenFilePathAsync(path);

            var panel = new SearchPanelViewModel(search, mainVm)
            {
                StatusText = "3 in 1 file(s)",
                GoToErrorText = "stale error",
                NavigateTimestamp = "2026-03-09 19:49:20"
            };

            await panel.GoToTimestampCommand.ExecuteAsync(null);

            Assert.Equal(string.Empty, panel.GoToErrorText);
            Assert.Equal("3 in 1 file(s)", panel.StatusText);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task GoToTimestamp_NearestMatch_LeavesGoToErrorTextEmpty()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();

        var path = Path.Combine(Path.GetTempPath(), $"logreader-searchpanel-ts-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path,
                "2026-03-09 19:49:10 INFO one\n2026-03-09 19:49:30 INFO two\n");
            await mainVm.OpenFilePathAsync(path);

            var panel = new SearchPanelViewModel(search, mainVm)
            {
                GoToErrorText = "stale error",
                NavigateTimestamp = "2026-03-09 19:49:26"
            };

            await panel.GoToTimestampCommand.ExecuteAsync(null);

            Assert.Equal(string.Empty, panel.GoToErrorText);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task NavigateTimestamp_Edit_ClearsStaleGoToErrorText()
    {
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(new StubLogFileRepository(), new StubLogGroupRepository(), new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            GoToErrorText = "stale error",
            NavigateTimestamp = "2026-03-09 19:49:20"
        };

        panel.GoToErrorText = "stale error";
        panel.NavigateTimestamp = "2026-03-09 19:49:21";

        Assert.Equal(string.Empty, panel.GoToErrorText);
    }

    [Fact]
    public async Task NavigateLineNumber_Edit_ClearsStaleGoToErrorText()
    {
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(new StubLogFileRepository(), new StubLogGroupRepository(), new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            GoToErrorText = "stale error",
            NavigateLineNumber = "42"
        };

        panel.GoToErrorText = "stale error";
        panel.NavigateLineNumber = "43";

        Assert.Equal(string.Empty, panel.GoToErrorText);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 4000, int pollIntervalMs = 25)
    {
        var startedAt = DateTime.UtcNow;
        while (!condition() && (DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
            await Task.Delay(pollIntervalMs);

        Assert.True(condition(), "Timed out waiting for condition.");
    }
}
