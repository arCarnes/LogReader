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
            },
            SearchResultLineOrder.Ascending);

        Assert.Equal(4, viewModel.HitCount);
        Assert.Equal(new long[] { 5, 10, 20, 30 }, viewModel.Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public void ApplyLineOrder_Descending_ReordersExistingAndFutureHits()
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

        viewModel.ApplyLineOrder(SearchResultLineOrder.Descending);
        viewModel.AddHits(
            new[]
            {
                new SearchHit { LineNumber = 15, LineText = "fifteen", MatchStart = 0, MatchLength = 7 }
            },
            SearchResultLineOrder.Descending);

        Assert.Equal(new long[] { 20, 15, 10 }, viewModel.Hits.Select(hit => hit.LineNumber).ToArray());
    }

    private sealed class WorkspaceContextStub : ILogWorkspaceContext
    {
        public string? ActiveScopeDashboardId => null;

        public LogTabViewModel? SelectedTab => null;

        public IReadOnlyList<LogTabViewModel> GetAllTabs() => Array.Empty<LogTabViewModel>();

        public IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot() => Array.Empty<LogTabViewModel>();

        public IReadOnlyList<string> GetSearchResultFileOrderSnapshot() => Array.Empty<string>();

        public WorkspaceScopeSnapshot GetActiveScopeSnapshot()
            => new(WorkspaceScopeKey.FromDashboardId(null), Array.Empty<WorkspaceOpenTabSnapshot>(), Array.Empty<WorkspaceScopeMemberSnapshot>());

        public Task<FileEncoding> ResolveFilterFileEncodingAsync(string filePath, string? scopeDashboardId, CancellationToken ct = default)
            => Task.FromResult(FileEncoding.Utf8);

        public void UpdateRecentTabFilterSnapshot(string filePath, string? scopeDashboardId, LogFilterSession.FilterSnapshot? snapshot)
        {
        }

        public Task NavigateToLineAsync(string filePath, long lineNumber, bool disableAutoScroll = false)
            => Task.CompletedTask;

    }
}
