namespace LogReader.Tests;

using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;

public class FilterPanelTimeOnlyTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "WeezTailFilterPanelTimeOnlyTests_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly IDisposable _appPathsScope;

    public FilterPanelTimeOnlyTests()
    {
        _appPathsScope = AppPaths.BeginTestScope(rootPath: _testRoot);
    }

    public void Dispose()
    {
        _appPathsScope.Dispose();

        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, true);
    }

    [Fact]
    public async Task ApplyFilter_TimeOnlyCurrentTab_ActivatesSnapshotFilter()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");
        Assert.NotNull(vm.SelectedTab);

        search.NextResult = new SearchResult
        {
            FilePath = vm.SelectedTab!.FilePath,
            Hits = new List<SearchHit>
            {
                new() { LineNumber = 2 },
                new() { LineNumber = 5 }
            },
            HasParseableTimestamps = true
        };

        vm.FilterPanel.Query = string.Empty;
        vm.FilterPanel.FromTimestamp = "2026-03-09 19:49:10";
        vm.FilterPanel.ToTimestamp = "2026-03-09 19:49:20";

        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.True(vm.SelectedTab.IsFilterActive);
        Assert.Equal(2, vm.SelectedTab.FilteredLineCount);
        Assert.Equal(new[] { 2, 5 }, vm.SelectedTab.VisibleLines.Select(line => line.LineNumber).ToArray());
        Assert.Equal(string.Empty, search.LastSearchFileRequest!.Query);
        Assert.Equal("2026-03-09 19:49:10", search.LastSearchFileRequest.FromTimestamp);
        Assert.Equal("2026-03-09 19:49:20", search.LastSearchFileRequest.ToTimestamp);
        Assert.Equal("Filter active: 2 matching lines.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task ApplyFilter_EmptyQueryAndEmptyTimestamp_DoesNotSearch()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");

        vm.FilterPanel.Query = string.Empty;
        vm.FilterPanel.FromTimestamp = string.Empty;
        vm.FilterPanel.ToTimestamp = string.Empty;

        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal(0, search.SearchFileCallCount);
        Assert.Equal("Enter filter text or time range.", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task ApplyFilter_InvalidTimestampRangeWithEmptyQuery_DoesNotSearch()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");

        vm.FilterPanel.Query = string.Empty;
        vm.FilterPanel.FromTimestamp = "invalid";

        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal(0, search.SearchFileCallCount);
        Assert.Contains("Invalid 'From' timestamp", vm.FilterPanel.StatusText);
    }

    [Fact]
    public async Task ApplyFilter_StaleGenerationPreservesPreviouslyCommittedFilter()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\filtered.log");
        var tab = Assert.IsType<LogTabViewModel>(vm.SelectedTab);
        vm.FilterPanel.Query = "ERROR";
        search.NextResult = new SearchResult
        {
            FilePath = tab.FilePath,
            Hits = new List<SearchHit> { new() { LineNumber = 2 } }
        };
        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);
        Assert.Equal(new[] { 2 }, tab.CaptureActiveFilterSnapshot()!.MatchingLineNumbers);

        var staleToken = FileGenerationToken.Create(9, 901);
        search.NextResult = new SearchResult
        {
            FilePath = tab.FilePath,
            GenerationEvidence = new FileScanGenerationEvidence(staleToken, FileGenerationCorrelation.Stale),
            Hits = new List<SearchHit> { new() { LineNumber = 5 } }
        };

        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 2 }, tab.CaptureActiveFilterSnapshot()!.MatchingLineNumbers);
        Assert.Contains("content changed", vm.FilterPanel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyFilter_AllOpenTabs_CountsOnlyGenerationCompatibleCommits()
    {
        var search = new RecordingSearchService();
        using var vm = CreateViewModel(search);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");
        await vm.OpenFilePathAsync(@"C:\test\b.log");
        var tabA = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\a.log");
        var tabB = vm.Tabs.Single(tab => tab.FilePath == @"C:\test\b.log");
        var tokenA = FileGenerationToken.Create(10, 1001);
        var tokenB = FileGenerationToken.Create(10, 1002);
        tabA.ActiveSession.DebugLineIndex!.GenerationToken = tokenA;
        tabB.ActiveSession.DebugLineIndex!.GenerationToken = tokenB;
        search.NextResults =
        [
            new SearchResult
            {
                FilePath = tabA.FilePath,
                GenerationEvidence = new FileScanGenerationEvidence(tokenA, FileGenerationCorrelation.Current),
                Hits = new List<SearchHit> { new() { LineNumber = 2 } }
            },
            new SearchResult
            {
                FilePath = tabB.FilePath,
                GenerationEvidence = new FileScanGenerationEvidence(tokenB, FileGenerationCorrelation.Stale),
                Hits = new List<SearchHit> { new() { LineNumber = 5 } }
            }
        ];
        vm.FilterPanel.Query = "ERROR";
        vm.FilterPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;

        await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);

        Assert.True(tabA.IsFilterActive);
        Assert.Equal(new[] { 2 }, tabA.CaptureActiveFilterSnapshot()!.MatchingLineNumbers);
        Assert.False(tabB.IsFilterActive);
        Assert.Single(vm.FilterPanel.Warnings);
        Assert.Contains("across 1 open tab", vm.FilterPanel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 warning", vm.FilterPanel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    private static MainViewModel CreateViewModel(ISearchService searchService)
    {
        return TestMainViewModelFactory.Create(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
            new StubSettingsRepository(),
            new StubLogReaderService(),
            searchService,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            enableLifecycleTimer: false,
            fileDialogService: null,
            messageBoxService: null,
            settingsDialogService: null,
            bulkOpenPathsDialogService: null,
            settingsViewModelFactory: null,
            persistedStateRecoveryCoordinator: null,
            workspaceViewModelReference: null,
            logAppearanceService: null,
            tabLifecycleScheduler: null,
            fileCatalogService: null,
            tabWorkspace: null,
            dashboardWorkspace: null);
    }

    private sealed class RecordingSearchService : ISearchService
    {
        public SearchResult NextResult { get; set; } = new();
        public IReadOnlyList<SearchResult>? NextResults { get; set; }
        public int SearchFileCallCount { get; private set; }
        public SearchRequest? LastSearchFileRequest { get; private set; }

        public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
        {
            SearchFileCallCount++;
            LastSearchFileRequest = request.Clone();

            return Task.FromResult(new SearchResult
            {
                FilePath = NextResult.FilePath,
                Hits = NextResult.Hits.ToList(),
                Error = NextResult.Error,
                HasParseableTimestamps = NextResult.HasParseableTimestamps,
                GenerationEvidence = NextResult.GenerationEvidence,
                EvaluatedThroughLine = NextResult.EvaluatedThroughLine
            });
        }

        public Task<SearchResult> SearchFileRangeAsync(
            string filePath,
            SearchRequest request,
            FileEncoding encoding,
            Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
            CancellationToken ct = default)
            => SearchFileAsync(filePath, request, encoding, ct);

        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default)
            => Task.FromResult(NextResults ?? (IReadOnlyList<SearchResult>)Array.Empty<SearchResult>());
    }
}
