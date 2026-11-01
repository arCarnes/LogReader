namespace LogReader.Tests;

using LogReader.App.ViewModels;
using LogReader.Core.Models;

public partial class SearchPanelViewModelTests
{
    [Fact]
    public async Task Shutdown_RetainsAnOldCancellationInsensitiveSnapshotUntilItReturns()
    {
        await RunSearchTestAsync(async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var search = new RecordingSearchService
            {
                SearchFilesAsyncHandler = async (request, _, _) =>
                {
                    if (request.Query == "old")
                    {
                        started.TrySetResult();
                        await release.Task;
                    }
                    return Array.Empty<SearchResult>();
                }
            };
            var vm = CreateMainViewModel(new StubLogFileRepository(), new StubLogGroupRepository(), new StubSettingsRepository(), search);
            await vm.InitializeAsync();
            await vm.OpenFilePathAsync(@"C:\logs\owned-snapshot.log");
            var panel = vm.SearchPanel;
            panel.Query = "old";
            var old = panel.ExecuteSearchCommand.ExecuteAsync(null);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                panel.CancelSearchCommand.Execute(null);
                panel.Query = "new";
                await panel.ExecuteSearchCommand.ExecuteAsync(null);
                vm.Dispose();
                Assert.False(vm.ShutdownTask.IsCompleted);
                release.TrySetResult();
                await Task.WhenAll(old, vm.ShutdownTask).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(panel.Results);
                Assert.Equal("new", panel.Query);
            }
            finally
            {
                release.TrySetResult();
                vm.Dispose();
                await Task.WhenAll(old, vm.ShutdownTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PanelShutdown_RetainsRetiredRangeAndProtectsNewerResults(bool diskMonitoring)
    {
        await RunSearchTestAsync(async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var search = new RecordingSearchService
            {
                SearchFileRangeAsyncHandler = async (path, request, _, _, _) =>
                {
                    if (request.Query == "old")
                    {
                        started.TrySetResult();
                        await release.Task;
                    }
                    return new SearchResult
                    {
                        FilePath = path,
                        EvaluatedThroughLine = request.EndLineNumber,
                        Hits = [new SearchHit { LineNumber = request.EndLineNumber ?? 1, LineText = request.Query, MatchLength = 3 }]
                    };
                }
            };
            var vm = CreateMainViewModel(new StubLogFileRepository(), new StubLogGroupRepository(), new StubSettingsRepository(), search);
            await vm.InitializeAsync();
            await vm.OpenFilePathAsync(@"C:\logs\owned-range.log");
            var tab = vm.SelectedTab!;
            var panel = vm.SearchPanel;
            search.NextResults = [new SearchResult { FilePath = tab.FilePath }];
            panel.Query = "old";
            panel.SearchDataMode = diskMonitoring ? SearchDataMode.DiskSnapshot : SearchDataMode.Tail;
            await panel.ExecuteSearchCommand.ExecuteAsync(null);
            if (diskMonitoring)
            {
                panel.StartMonitoringNewMatchesCommand.Execute(null);
                Assert.True(panel.IsMonitoringNewMatches);
            }
            tab.TotalLines++;
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                panel.CancelSearchCommand.Execute(null);
                panel.Query = "new";
                panel.SearchDataMode = SearchDataMode.Tail;
                await panel.ExecuteSearchCommand.ExecuteAsync(null);
                tab.TotalLines++;
                await WaitForConditionAsync(() => panel.Results.Count == 1 && panel.Results[0].HitCount == 1);
                var visible = panel.Results[0];
                panel.Dispose();
                var shutdown = GetPanelShutdown(panel);
                Assert.False(shutdown.IsCompleted);
                panel.Dispose();
                release.TrySetResult();
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(visible, Assert.Single(panel.Results));
                Assert.All(visible.Hits, hit => Assert.Equal("new", hit.LineText));
                var calls = search.SearchFileCallCount;
                tab.TotalLines++;
                Assert.Equal(calls, search.SearchFileCallCount);
            }
            finally
            {
                release.TrySetResult();
                panel.Dispose();
                await GetPanelShutdown(panel).WaitAsync(TimeSpan.FromSeconds(5));
                vm.Dispose();
                await vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    private static Task GetPanelShutdown(SearchPanelViewModel panel)
        => panel.ShutdownTask;
}
