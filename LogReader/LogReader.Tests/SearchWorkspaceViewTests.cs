using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core.Models;

namespace LogReader.Tests;

public class SearchWorkspaceViewTests
{
    [Fact]
    public void GetSelectedHitLineTexts_ReturnsSelectedHitsInDisplayOrder()
    {
        RunSta(() =>
        {
            var first = CreateHitRow(10, "ten");
            var second = CreateHitRow(20, "twenty");
            var third = CreateHitRow(30, "thirty");
            var listBox = CreateSearchHitsListBox(first, second, third);

            listBox.SelectedItems.Add(third);
            listBox.SelectedItems.Add(first);

            var lines = SearchWorkspaceView.GetSelectedHitLineTexts(listBox);

            Assert.Equal(new[] { "ten", "thirty" }, lines);
        });
    }

    [Fact]
    public void TryPrepareSelectionForContextMenu_SelectsOnlyClickedUnselectedHit()
    {
        RunSta(() =>
        {
            var first = CreateHitRow(10, "ten");
            var second = CreateHitRow(20, "twenty");
            var third = CreateHitRow(30, "thirty");
            var listBox = CreateSearchHitsListBox(first, second, third);
            listBox.SelectedItems.Add(first);
            listBox.SelectedItems.Add(third);

            var changed = SearchWorkspaceView.TryPrepareSelectionForContextMenu(
                listBox,
                second);

            Assert.True(changed);
            Assert.Single(listBox.SelectedItems);
            Assert.Same(second, listBox.SelectedItem);
        });
    }

    [Fact]
    public void TryPrepareSelectionForContextMenu_PreservesExistingMultiSelectionForSelectedHit()
    {
        RunSta(() =>
        {
            var first = CreateHitRow(10, "ten");
            var second = CreateHitRow(20, "twenty");
            var third = CreateHitRow(30, "thirty");
            var listBox = CreateSearchHitsListBox(first, second, third);
            listBox.SelectedItems.Add(first);
            listBox.SelectedItems.Add(third);

            var changed = SearchWorkspaceView.TryPrepareSelectionForContextMenu(
                listBox,
                third);

            Assert.False(changed);
            Assert.Equal(2, listBox.SelectedItems.Count);
            Assert.Contains(first, listBox.SelectedItems.Cast<SearchResultHitRowViewModel>());
            Assert.Contains(third, listBox.SelectedItems.Cast<SearchResultHitRowViewModel>());
        });
    }

    [Fact]
    public void GetNavigableSelectedHit_ReturnsSelectedItem()
    {
        RunSta(() =>
        {
            var first = CreateHitRow(10, "ten");
            var second = CreateHitRow(20, "twenty");
            var listBox = CreateSearchHitsListBox(first, second);
            listBox.SelectedItem = second;

            var hit = SearchWorkspaceView.GetNavigableSelectedHit(listBox);

            Assert.Same(second.Hit, hit);
        });
    }

    [Fact]
    public void TryCopySelectedHits_ReturnsFalseWhenNothingIsSelected()
    {
        RunSta(() =>
        {
            var listBox = CreateSearchHitsListBox(CreateHitRow(10, "ten"));

            var copied = SearchWorkspaceView.TryCopySelectedHits(listBox);

            Assert.False(copied);
        });
    }

    [Fact]
    public void TryCollapseCurrentResults_CollapsesSelectedHitOwner()
    {
        RunSta(() =>
        {
            var fileResult = CreateFileResult((10, "ten"), (20, "twenty"));
            fileResult.IsExpanded = true;
            var listBox = CreateSearchResultsListBox(fileResult.HeaderRow, fileResult.GetHitRow(0), fileResult.GetHitRow(1));
            listBox.SelectedItem = fileResult.GetHitRow(1);

            var collapsed = SearchWorkspaceView.TryCollapseCurrentResults(listBox);

            Assert.True(collapsed);
            Assert.False(fileResult.IsExpanded);
        });
    }

    [Fact]
    public void TryCollapseCurrentResults_CollapsesSelectedHeaderOwner()
    {
        RunSta(() =>
        {
            var fileResult = CreateFileResult((10, "ten"));
            fileResult.IsExpanded = true;
            var listBox = CreateSearchResultsListBox(fileResult.HeaderRow, fileResult.GetHitRow(0));
            listBox.SelectedItem = fileResult.HeaderRow;

            var collapsed = SearchWorkspaceView.TryCollapseCurrentResults(listBox);

            Assert.True(collapsed);
            Assert.False(fileResult.IsExpanded);
        });
    }

    [Fact]
    public void CollapseAllResults_CollapsesEveryExpandedGroup()
    {
        RunSta(() =>
        {
            var first = CreateFileResult((10, "ten"), (20, "twenty"));
            var second = CreateFileResult((30, "thirty"));
            first.IsExpanded = true;
            second.IsExpanded = true;
            var listBox = CreateSearchResultsListBox(
                first.HeaderRow,
                first.GetHitRow(0),
                first.GetHitRow(1),
                second.HeaderRow,
                second.GetHitRow(0));

            var collapsed = SearchWorkspaceView.CollapseAllResults(listBox);

            Assert.True(collapsed);
            Assert.False(first.IsExpanded);
            Assert.False(second.IsExpanded);
        });
    }

    [Fact]
    public void GetParentObject_ReturnsContentParentForRunSources()
    {
        RunSta(() =>
        {
            var textBlock = new TextBlock();
            var run = new Run("ten");
            textBlock.Inlines.Add(run);

            var parent = SearchWorkspaceView.GetParentObject(run);

            Assert.Same(textBlock, parent);
        });
    }

    private static SearchResultHitRowViewModel CreateHitRow(long lineNumber, string lineText)
    {
        var fileResult = CreateFileResult((lineNumber, lineText));

        return fileResult.GetHitRow(0);
    }

    private static ListBox CreateSearchHitsListBox(params SearchResultHitRowViewModel[] hits)
        => CreateSearchResultsListBox(hits);

    private static ListBox CreateSearchResultsListBox(params SearchResultsRowViewModel[] rows)
    {
        var listBox = new ListBox
        {
            ItemsSource = rows,
            SelectionMode = SelectionMode.Extended,
            Width = 400,
            Height = 200
        };

        listBox.ApplyTemplate();
        listBox.Measure(new Size(400, 200));
        listBox.Arrange(new Rect(0, 0, 400, 200));
        listBox.UpdateLayout();

        return listBox;
    }

    private static FileSearchResultViewModel CreateFileResult(params (long LineNumber, string LineText)[] hits)
    {
        return new FileSearchResultViewModel(
            new SearchResult
            {
                FilePath = $@"C:\logs\{Guid.NewGuid():N}.log",
                Hits = hits
                    .Select(hit => new SearchHit
                    {
                        LineNumber = hit.LineNumber,
                        LineText = hit.LineText,
                        MatchStart = 0,
                        MatchLength = hit.LineText.Length
                    })
                    .ToList()
            },
            new WorkspaceContextStub());
    }

    private static void RunSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
            throw exception;
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

        public Task<IReadOnlyDictionary<string, LogTabViewModel>> EnsureBackgroundTabsOpenAsync(
            IReadOnlyList<string> filePaths,
            string? scopeDashboardId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, LogTabViewModel>>(
                new Dictionary<string, LogTabViewModel>(StringComparer.OrdinalIgnoreCase));

        public LogFilterSession.FilterSnapshot? GetApplicableCurrentTabFilterSnapshot(SearchDataMode sourceMode)
            => null;

        public LogFilterSession.FilterSnapshot? GetApplicableCurrentScopeFilterSnapshot(string filePath, SearchDataMode sourceMode)
            => null;

        public IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableCurrentScopeFilterSnapshots(SearchDataMode sourceMode)
            => new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);

        public void UpdateRecentTabFilterSnapshot(string filePath, string? scopeDashboardId, LogFilterSession.FilterSnapshot? snapshot)
        {
        }

        public Task NavigateToLineAsync(string filePath, long lineNumber, bool disableAutoScroll = false)
            => Task.CompletedTask;
    }
}
