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
        var fileResult = new FileSearchResultViewModel(
            new SearchResult
            {
                FilePath = @"C:\logs\app.log",
                Hits = new List<SearchHit>
                {
                    new()
                    {
                        LineNumber = lineNumber,
                        LineText = lineText,
                        MatchStart = 0,
                        MatchLength = lineText.Length
                    }
                }
            },
            new WorkspaceContextStub());

        return fileResult.GetHitRow(0);
    }

    private static ListBox CreateSearchHitsListBox(params SearchResultHitRowViewModel[] hits)
    {
        var listBox = new ListBox
        {
            ItemsSource = hits,
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
