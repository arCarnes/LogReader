using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

namespace LogReader.Tests;

public class FileSearchResultViewModelTests
{
    [Fact]
    public void AddHits_DedupesAcrossBatches_AndKeepsAscendingOrder()
    {
        var viewModel = new FileSearchResultViewModel(
            new SearchResult
            {
                FilePath = @"C:\logs\app.log",
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 10, LineText = "ten", MatchStart = 0, MatchLength = 3 },
                    new() { LineNumber = 20, LineText = "twenty", MatchStart = 0, MatchLength = 6 }
                }
            },
            new WorkspaceContextStub());

        viewModel.AddHits(
            new[]
            {
                new SearchHit { LineNumber = 20, LineText = "twenty", MatchStart = 0, MatchLength = 6 },
                new SearchHit { LineNumber = 5, LineText = "five", MatchStart = 0, MatchLength = 4 },
                new SearchHit { LineNumber = 30, LineText = "thirty", MatchStart = 0, MatchLength = 6 }
            });

        Assert.Equal(4, viewModel.HitCount);
        Assert.Equal(new long[] { 5, 10, 20, 30 }, viewModel.Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public void GetHitRow_LazilyBuildsRowsFromSortedHits()
    {
        var viewModel = new FileSearchResultViewModel(
            new SearchResult
            {
                FilePath = @"C:\logs\app.log",
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 30, LineText = "thirty", MatchStart = 0, MatchLength = 6 },
                    new() { LineNumber = 10, LineText = "ten", MatchStart = 0, MatchLength = 3 },
                    new() { LineNumber = 20, LineText = "twenty", MatchStart = 0, MatchLength = 6 }
                }
            },
            new WorkspaceContextStub());

        var first = viewModel.GetHitRow(0);
        var second = viewModel.GetHitRow(1);
        var firstAgain = viewModel.GetHitRow(0);

        Assert.Equal(10, first.Hit.LineNumber);
        Assert.Equal(20, second.Hit.LineNumber);
        Assert.Same(first, firstAgain);
    }

    [Fact]
    public async Task NavigateToHit_UsesViewActionWrapper_ForNavigationFailures()
    {
        var workspaceContext = new WorkspaceContextStub
        {
            ThrowOnNavigate = true
        };
        var viewModel = new FileSearchResultViewModel(
            new SearchResult
            {
                FilePath = @"C:\logs\app.log",
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 42, LineText = "forty-two", MatchStart = 0, MatchLength = 9 }
                }
            },
            workspaceContext);

        var hit = Assert.Single(viewModel.Hits);
        var navigateToHit = typeof(FileSearchResultViewModel).GetMethod(
            "NavigateToHit",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(navigateToHit);

        var exception = await Record.ExceptionAsync(
            () => (Task)navigateToHit!.Invoke(viewModel, new object?[] { hit })!);

        Assert.Null(exception);
        Assert.True(workspaceContext.RunViewActionCalled);
        Assert.Equal("Search Result Navigation Failed", workspaceContext.LastFailureCaption);
        Assert.True(workspaceContext.NavigateToLineCalled);
        Assert.NotNull(workspaceContext.CapturedNavigationException);
    }

    [Fact]
    public async Task NavigateToHit_DuringDashboardLoad_DoesNotInvokeNavigation()
    {
        var workspaceContext = new WorkspaceContextStub
        {
            IsDashboardLoading = true
        };
        var viewModel = new FileSearchResultViewModel(
            new SearchResult
            {
                FilePath = @"C:\logs\app.log",
                Hits = new List<SearchHit>
                {
                    new() { LineNumber = 42, LineText = "forty-two", MatchStart = 0, MatchLength = 9 }
                }
            },
            workspaceContext);

        var hit = Assert.Single(viewModel.Hits);
        var navigateToHit = typeof(FileSearchResultViewModel).GetMethod(
            "NavigateToHit",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(navigateToHit);

        var exception = await Record.ExceptionAsync(
            () => (Task)navigateToHit!.Invoke(viewModel, new object?[] { hit })!);

        Assert.Null(exception);
        Assert.False(workspaceContext.RunViewActionCalled);
        Assert.False(workspaceContext.NavigateToLineCalled);
    }

    private sealed class WorkspaceContextStub : ILogWorkspaceContext
    {
        public bool ThrowOnNavigate { get; set; }

        public bool IsDashboardLoading { get; set; }

        public bool NavigateToLineCalled { get; private set; }

        public bool RunViewActionCalled { get; private set; }

        public string? LastFailureCaption { get; private set; }

        public Exception? CapturedNavigationException { get; private set; }

        public string? ActiveScopeDashboardId => null;

        public LogTabViewModel? SelectedTab => null;

        public IReadOnlyList<LogTabViewModel> GetAllTabs() => Array.Empty<LogTabViewModel>();

        public IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot() => Array.Empty<LogTabViewModel>();

        public IReadOnlyList<string> GetSearchResultFileOrderSnapshot() => Array.Empty<string>();

        public IReadOnlyList<string> GetAllOpenTabsExecutionFileOrderSnapshot(string? scopeDashboardId)
            => Array.Empty<string>();

        public WorkspaceScopeSnapshot GetActiveScopeSnapshot()
            => new(WorkspaceScopeKey.FromDashboardId(null), Array.Empty<WorkspaceOpenTabSnapshot>(), Array.Empty<WorkspaceScopeMemberSnapshot>());

        public Task<FileEncoding> ResolveFilterFileEncodingAsync(string filePath, string? scopeDashboardId, CancellationToken ct = default)
            => Task.FromResult(FileEncoding.Utf8);

        public LogFilterSession.FilterSnapshot? GetApplicableCurrentTabFilterSnapshot(SearchDataMode sourceMode)
            => null;

        public LogFilterSession.FilterSnapshot? GetApplicableAllOpenTabsFilterSnapshot(string filePath, SearchDataMode sourceMode)
            => null;

        public IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableAllOpenTabsFilterSnapshots(SearchDataMode sourceMode)
            => new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);

        public void UpdateRecentTabFilterSnapshot(string filePath, string? scopeDashboardId, LogFilterSession.FilterSnapshot? snapshot)
        {
        }

        public async Task RunViewActionAsync(Func<Task> operation, string failureCaption = "LogReader Error")
        {
            RunViewActionCalled = true;
            LastFailureCaption = failureCaption;
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                CapturedNavigationException = ex;
            }
        }

        public Task NavigateToLineAsync(
            string filePath,
            long lineNumber,
            bool disableAutoScroll = false,
            bool suppressDuringDashboardLoad = false)
        {
            NavigateToLineCalled = true;
            if (ThrowOnNavigate)
                throw new InvalidOperationException("boom");

            return Task.CompletedTask;
        }

    }
}
