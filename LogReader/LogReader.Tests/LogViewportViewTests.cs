using LogReader.App.Views;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LogReader.Tests;

public class LogViewportViewTests
{
    [Theory]
    [InlineData(nameof(MainViewModel.SelectedTab))]
    [InlineData(nameof(MainViewModel.ViewportRefreshVersion))]
    public void ShouldRefreshViewportForPropertyChange_ReturnsTrueForViewportRefreshTriggers(string propertyName)
    {
        Assert.True(LogViewportView.ShouldRefreshViewportForPropertyChange(propertyName));
    }

    [Fact]
    public void ShouldRefreshViewportForPropertyChange_ReturnsFalseForUnrelatedProperties()
    {
        Assert.False(LogViewportView.ShouldRefreshViewportForPropertyChange(nameof(MainViewModel.GlobalAutoScrollEnabled)));
        Assert.False(LogViewportView.ShouldRefreshViewportForPropertyChange(null));
    }

    [Theory]
    [InlineData(nameof(MainViewModel.SelectedTab))]
    [InlineData(nameof(MainViewModel.ViewportRefreshVersion))]
    public void ShouldForceViewportRefreshForPropertyChange_ReturnsTrueForRealizationTriggers(string propertyName)
    {
        Assert.True(LogViewportView.ShouldForceViewportRefreshForPropertyChange(propertyName));
    }

    [Fact]
    public void ShouldForceViewportRefreshForPropertyChange_ReturnsFalseForUnrelatedProperties()
    {
        Assert.False(LogViewportView.ShouldForceViewportRefreshForPropertyChange(nameof(MainViewModel.GlobalAutoScrollEnabled)));
        Assert.False(LogViewportView.ShouldForceViewportRefreshForPropertyChange(null));
    }

    [Fact]
    public void ShouldForceViewportRefreshForLoadedListBox_ReturnsTrueOnlyForSelectedTab()
    {
        var selectedTab = CreateTab("selected-loaded");

        Assert.True(LogViewportView.ShouldForceViewportRefreshForLoadedListBox(selectedTab, selectedTab));
        Assert.False(LogViewportView.ShouldForceViewportRefreshForLoadedListBox(null, selectedTab));
        Assert.False(LogViewportView.ShouldForceViewportRefreshForLoadedListBox(selectedTab, CreateTab("other-loaded")));
        Assert.False(LogViewportView.ShouldForceViewportRefreshForLoadedListBox(selectedTab, null));
    }

    [Fact]
    public void ShouldRefreshViewportForTabPropertyChange_ReturnsTrueForViewportRefreshToken()
    {
        Assert.True(LogViewportView.ShouldRefreshViewportForTabPropertyChange(nameof(LogTabViewModel.ViewportRefreshToken)));
        Assert.False(LogViewportView.ShouldRefreshViewportForTabPropertyChange(nameof(LogTabViewModel.NavigateToLineNumber)));
        Assert.False(LogViewportView.ShouldRefreshViewportForTabPropertyChange(null));
    }

    [Fact]
    public void ShouldApplyPendingLineSelection_ReturnsTrueOnlyForMatchingSelectedTabAndLine()
    {
        var tab = CreateTab("selected");
        var pending = new LogViewportView.PendingLineSelection(tab.TabInstanceId, 42);

        tab.NavigateToLineNumber = 42;
        Assert.True(LogViewportView.ShouldApplyPendingLineSelection(pending, tab, tab.NavigateToLineNumber));

        Assert.False(LogViewportView.ShouldApplyPendingLineSelection(null, tab, tab.NavigateToLineNumber));
        Assert.False(LogViewportView.ShouldApplyPendingLineSelection(pending, null, 42));
        Assert.False(LogViewportView.ShouldApplyPendingLineSelection(pending, CreateTab("other"), 42));
        Assert.False(LogViewportView.ShouldApplyPendingLineSelection(pending, tab, 41));
    }

    [Fact]
    public void TryCalculateViewportLineCount_ReturnsNullUntilARealRowHeightIsAvailable()
    {
        Assert.Null(LogViewportView.TryCalculateViewportLineCount(0, 18));
        Assert.Null(LogViewportView.TryCalculateViewportLineCount(320, null));
        Assert.Null(LogViewportView.TryCalculateViewportLineCount(320, 0));
        Assert.Equal(20, LogViewportView.TryCalculateViewportLineCount(320, 16));
    }

    [Fact]
    public void ResolveViewportHeightForLineCount_UsesContentViewportHeightWhenAvailable()
    {
        Assert.Equal(304, LogViewportView.ResolveViewportHeightForLineCount(320, 304));
        Assert.Equal(19, LogViewportView.TryCalculateViewportLineCount(
            LogViewportView.ResolveViewportHeightForLineCount(320, 304),
            16));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ResolveViewportHeightForLineCount_FallsBackToListBoxHeightWhenContentViewportHeightIsUnavailable(double? contentViewportHeight)
    {
        Assert.Equal(320, LogViewportView.ResolveViewportHeightForLineCount(320, contentViewportHeight));
    }

    [Fact]
    public void ApplyForcedLayoutIfRequested_AllowsForcedAndLightweightRefreshPaths()
    {
        WpfTestHost.Run(() =>
        {
            var listBox = new ListBox();
            var forceLayoutCallCount = 0;

            LogViewportView.ApplyForcedLayoutIfRequested(
                listBox,
                forceLayout: false,
                _ => forceLayoutCallCount++);
            Assert.Equal(0, forceLayoutCallCount);

            LogViewportView.ApplyForcedLayoutIfRequested(
                listBox,
                forceLayout: true,
                _ => forceLayoutCallCount++);
            Assert.Equal(1, forceLayoutCallCount);
        });
    }

    [Fact]
    public async Task ForceLayout_RealizesFirstViewportItemContainer()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var listBox = CreateLogListBox(1, 2, 3);
            var window = new Window
            {
                Style = new Style(typeof(Window)),
                Content = listBox,
                Width = 320,
                Height = 180,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };

            try
            {
                WpfTestHost.ShowHidden(window);
                await WpfTestHost.FlushAsync();

                LogViewportView.ForceLayout(listBox);

                Assert.True(LogViewportView.IsFirstVisibleItemContainerRealized(listBox));
                Assert.False(LogViewportView.ShouldRetryVisibleItemRealization(listBox));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MeasureWidestRealizedRowWidth_IgnoresEmptyList()
    {
        WpfTestHost.Run(() =>
        {
            var listBox = new ListBox();

            Assert.Null(LogViewportView.MeasureWidestRealizedRowWidth(listBox));
        });
    }

    [Fact]
    public void HorizontalContentMinWidth_GrowsMonotonicallyAndResets()
    {
        var tab = CreateTab("horizontal-width");

        tab.GrowHorizontalContentMinWidth(120);
        tab.GrowHorizontalContentMinWidth(80);

        Assert.Equal(120, tab.HorizontalContentMinWidth);

        tab.GrowHorizontalContentMinWidth(180);

        Assert.Equal(180, tab.HorizontalContentMinWidth);

        tab.ResetHorizontalContentMinWidth();

        Assert.Equal(0, tab.HorizontalContentMinWidth);
    }

    [Fact]
    public void TryMoveSelectionByLine_DownWithinVisibleLines_ChangesSelectionWithoutScrolling()
    {
        WpfTestHost.Run(() =>
        {
            var tab = CreateTab("selection-down");
            tab.TotalLines = 100;
            tab.ScrollPosition = 9;
            var listBox = CreateLogListBox(10, 11, 12);
            listBox.SelectedItem = listBox.Items[1];

            var handled = LogViewportView.TryMoveSelectionByLine(listBox, tab, Key.Down, ModifierKeys.None);

            Assert.True(handled);
            Assert.Equal(9, tab.ScrollPosition);
            Assert.Equal(12, Assert.IsType<LogLineViewModel>(listBox.SelectedItem).LineNumber);
        });
    }

    [Fact]
    public void TryMoveSelectionByLine_UpWithinVisibleLines_ChangesSelectionWithoutScrolling()
    {
        WpfTestHost.Run(() =>
        {
            var tab = CreateTab("selection-up");
            tab.TotalLines = 100;
            tab.ScrollPosition = 9;
            var listBox = CreateLogListBox(10, 11, 12);
            listBox.SelectedItem = listBox.Items[1];

            var handled = LogViewportView.TryMoveSelectionByLine(listBox, tab, Key.Up, ModifierKeys.None);

            Assert.True(handled);
            Assert.Equal(9, tab.ScrollPosition);
            Assert.Equal(10, Assert.IsType<LogLineViewModel>(listBox.SelectedItem).LineNumber);
        });
    }

    [Fact]
    public void TryMoveSelectionByLine_NoSelection_SelectsFirstVisibleLine()
    {
        WpfTestHost.Run(() =>
        {
            var tab = CreateTab("selection-empty");
            tab.TotalLines = 100;
            var listBox = CreateLogListBox(10, 11, 12);

            var handled = LogViewportView.TryMoveSelectionByLine(listBox, tab, Key.Down, ModifierKeys.None);

            Assert.True(handled);
            Assert.Equal(10, Assert.IsType<LogLineViewModel>(listBox.SelectedItem).LineNumber);
        });
    }

    [Fact]
    public void TryMoveSelectionByLine_TargetBelowVisibleLines_ScrollsOneLineAndClearsVisibleSelection()
    {
        WpfTestHost.Run(() =>
        {
            var tab = CreateTab("selection-edge");
            tab.TotalLines = 100;
            tab.AutoScrollEnabled = false;
            tab.ScrollPosition = 9;
            var listBox = CreateLogListBox(10, 11);
            listBox.SelectedItem = listBox.Items[1];

            var targetLineNumber = LogViewportView.GetSelectionMoveTargetLineNumber(
                listBox,
                tab,
                Key.Down,
                ModifierKeys.None);
            var handled = LogViewportView.TryMoveSelectionByLine(listBox, tab, Key.Down, ModifierKeys.None);

            Assert.Equal(12, targetLineNumber);
            Assert.True(handled);
            Assert.Equal(10, tab.ScrollPosition);
            Assert.Empty(listBox.SelectedItems);
        });
    }

    [Fact]
    public void TryMoveSelectionByLine_TargetAboveVisibleLines_ScrollsOneLineAndClearsVisibleSelection()
    {
        WpfTestHost.Run(() =>
        {
            var tab = CreateTab("selection-up-edge");
            tab.TotalLines = 100;
            tab.AutoScrollEnabled = false;
            tab.ScrollPosition = 10;
            var listBox = CreateLogListBox(11, 12);
            listBox.SelectedItem = listBox.Items[0];

            var targetLineNumber = LogViewportView.GetSelectionMoveTargetLineNumber(
                listBox,
                tab,
                Key.Up,
                ModifierKeys.None);
            var handled = LogViewportView.TryMoveSelectionByLine(listBox, tab, Key.Up, ModifierKeys.None);

            Assert.Equal(10, targetLineNumber);
            Assert.True(handled);
            Assert.Equal(9, tab.ScrollPosition);
            Assert.Empty(listBox.SelectedItems);
        });
    }

    [Fact]
    public void TryMoveSelectionByLine_DownAtLastLine_KeepsSelectionAndDoesNotScroll()
    {
        WpfTestHost.Run(() =>
        {
            var tab = CreateTab("selection-bottom");
            tab.TotalLines = 12;
            tab.ScrollPosition = 9;
            var listBox = CreateLogListBox(10, 11, 12);
            listBox.SelectedItem = listBox.Items[2];

            var handled = LogViewportView.TryMoveSelectionByLine(listBox, tab, Key.Down, ModifierKeys.None);

            Assert.True(handled);
            Assert.Equal(9, tab.ScrollPosition);
            Assert.Single(listBox.SelectedItems);
            Assert.Equal(12, Assert.IsType<LogLineViewModel>(listBox.SelectedItem).LineNumber);
        });
    }

    [Fact]
    public void TryMoveSelectionByLine_UpAtFirstLine_KeepsSelectionAndDoesNotScroll()
    {
        WpfTestHost.Run(() =>
        {
            var tab = CreateTab("selection-top");
            tab.TotalLines = 100;
            tab.ScrollPosition = 0;
            var listBox = CreateLogListBox(1, 2, 3);
            listBox.SelectedItem = listBox.Items[0];

            var handled = LogViewportView.TryMoveSelectionByLine(listBox, tab, Key.Up, ModifierKeys.None);

            Assert.True(handled);
            Assert.Equal(0, tab.ScrollPosition);
            Assert.Single(listBox.SelectedItems);
            Assert.Equal(1, Assert.IsType<LogLineViewModel>(listBox.SelectedItem).LineNumber);
        });
    }

    [Fact]
    public void TryMoveSelectionByLine_PendingSelectionBelowVisibleLines_ContinuesScrollingFromPendingLine()
    {
        WpfTestHost.Run(() =>
        {
            var tab = CreateTab("selection-pending-down");
            tab.TotalLines = 100;
            tab.AutoScrollEnabled = false;
            tab.ScrollPosition = 9;
            var listBox = CreateLogListBox(10, 11);

            var targetLineNumber = LogViewportView.GetSelectionMoveTargetLineNumber(
                listBox,
                tab,
                Key.Down,
                ModifierKeys.None,
                pendingSelectionLineNumber: 12);
            var handled = LogViewportView.TryMoveSelectionByLine(
                listBox,
                tab,
                Key.Down,
                ModifierKeys.None,
                pendingSelectionLineNumber: 12);

            Assert.Equal(13, targetLineNumber);
            Assert.True(handled);
            Assert.Equal(10, tab.ScrollPosition);
            Assert.Empty(listBox.SelectedItems);
        });
    }

    [Fact]
    public void TryMoveSelectionByLine_PendingSelectionIgnoresStaleVisibleSelection()
    {
        WpfTestHost.Run(() =>
        {
            var tab = CreateTab("selection-pending-stale-visible");
            tab.TotalLines = 100;
            tab.AutoScrollEnabled = false;
            tab.ScrollPosition = 10;
            var listBox = CreateLogListBox(10, 11);
            listBox.SelectedItem = listBox.Items[0];

            var targetLineNumber = LogViewportView.GetSelectionMoveTargetLineNumber(
                listBox,
                tab,
                Key.Down,
                ModifierKeys.None,
                pendingSelectionLineNumber: 12);
            var handled = LogViewportView.TryMoveSelectionByLine(
                listBox,
                tab,
                Key.Down,
                ModifierKeys.None,
                pendingSelectionLineNumber: 12);

            Assert.Equal(13, targetLineNumber);
            Assert.True(handled);
            Assert.Equal(11, tab.ScrollPosition);
            Assert.Empty(listBox.SelectedItems);
        });
    }

    [Fact]
    public void TryMoveSelectionByLine_PendingSelectionAtLastLine_DoesNotSelectFirstVisibleLine()
    {
        WpfTestHost.Run(() =>
        {
            var tab = CreateTab("selection-pending-bottom");
            tab.TotalLines = 12;
            tab.ScrollPosition = 9;
            var listBox = CreateLogListBox(10, 11);

            var handled = LogViewportView.TryMoveSelectionByLine(
                listBox,
                tab,
                Key.Down,
                ModifierKeys.None,
                pendingSelectionLineNumber: 12);

            Assert.True(handled);
            Assert.Equal(9, tab.ScrollPosition);
            Assert.Empty(listBox.SelectedItems);
            Assert.Null(listBox.SelectedItem);
        });
    }

    [Fact]
    public async Task TryMoveSelectionByLine_FilteredLines_MoveByDisplayOrder()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var tab = CreateTab("selection-filtered");
            await tab.LoadAsync();
            tab.AutoScrollEnabled = false;
            await tab.ApplyFilterAsync(
                matchingLineNumbers: new[] { 10, 20, 30 },
                statusText: "Filter active: 3 matching lines.");

            var listBox = CreateLogListBox(10, 20, 30);
            listBox.SelectedItem = listBox.Items[0];

            var targetLineNumber = LogViewportView.GetSelectionMoveTargetLineNumber(
                listBox,
                tab,
                Key.Down,
                ModifierKeys.None);
            var handled = LogViewportView.TryMoveSelectionByLine(listBox, tab, Key.Down, ModifierKeys.None);

            Assert.Equal(20, targetLineNumber);
            Assert.True(handled);
            Assert.Equal(20, Assert.IsType<LogLineViewModel>(listBox.SelectedItem).LineNumber);
        });
    }

    [Fact]
    public void RestoreSelectionByLineNumber_PreservesOnlyMatchingVisibleLineNumbers()
    {
        WpfTestHost.Run(() =>
        {
            var listBox = CreateLogListBox(11, 12, 13);
            listBox.SelectedItem = listBox.Items[0];

            var restored = LogViewportView.RestoreSelectionByLineNumber(listBox, new[] { 12, 99 });

            Assert.True(restored);
            Assert.Single(listBox.SelectedItems);
            Assert.Equal(12, Assert.IsType<LogLineViewModel>(listBox.SelectedItem).LineNumber);
        });
    }

    [Fact]
    public void RestoreSelectionByLineNumber_ClearsSelectionWhenSelectedLineScrolledOut()
    {
        WpfTestHost.Run(() =>
        {
            var listBox = CreateLogListBox(20, 21, 22);
            listBox.SelectedItem = listBox.Items[0];

            var restored = LogViewportView.RestoreSelectionByLineNumber(listBox, new[] { 12 });

            Assert.False(restored);
            Assert.Empty(listBox.SelectedItems);
            Assert.Null(listBox.SelectedItem);
        });
    }

    [Fact]
    public void ResolveSelectionRestoreForViewportChange_KeepsOffscreenSelectionAcrossRepeatedScrollCaptures()
    {
        var tab = CreateTab("selection-repeat");
        var pending = new LogViewportView.PendingSelectionRestore(tab.TabInstanceId, new[] { 12 });

        var resolved = LogViewportView.ResolveSelectionRestoreForViewportChange(
            pending,
            tab,
            new[] { 20 });

        Assert.NotNull(resolved);
        Assert.Equal(tab.TabInstanceId, resolved.Value.TabInstanceId);
        Assert.Equal(new[] { 12 }, resolved.Value.LineNumbers);
    }

    [Fact]
    public void ResolveSelectionRestoreForViewportChange_CapturesVisibleSelectionWhenNoPendingSelectionExists()
    {
        var tab = CreateTab("selection-visible");

        var resolved = LogViewportView.ResolveSelectionRestoreForViewportChange(
            null,
            tab,
            new[] { 20 });

        Assert.NotNull(resolved);
        Assert.Equal(tab.TabInstanceId, resolved.Value.TabInstanceId);
        Assert.Equal(new[] { 20 }, resolved.Value.LineNumbers);
    }

    [Fact]
    public void ResolveSelectionRestoreForViewportChange_PreservesNavigationSelectionIntent()
    {
        var tab = CreateTab("selection-navigation");
        var pending = new LogViewportView.PendingSelectionRestore(
            tab.TabInstanceId,
            new[] { 42 },
            PreserveAcrossViewportChanges: true);

        var resolved = LogViewportView.ResolveSelectionRestoreForViewportChange(
            pending,
            tab,
            new[] { 20 });

        Assert.NotNull(resolved);
        Assert.True(resolved.Value.PreserveAcrossViewportChanges);
        Assert.Equal(new[] { 42 }, resolved.Value.LineNumbers);
    }

    [Fact]
    public void RestorePendingSelection_ForNavigation_ReplacesExistingSelection()
    {
        WpfTestHost.Run(() =>
        {
            var listBox = CreateLogListBox(41, 42, 43);
            listBox.SelectedItems.Add(listBox.Items[0]);
            listBox.SelectedItems.Add(listBox.Items[2]);
            var restore = new LogViewportView.PendingSelectionRestore(
                "navigation",
                new[] { 42 },
                PreserveAcrossViewportChanges: true);

            var selected = LogViewportView.RestorePendingSelection(listBox, restore);

            Assert.True(selected);
            Assert.Single(listBox.SelectedItems);
            Assert.Equal(42, Assert.IsType<LogLineViewModel>(listBox.SelectedItem).LineNumber);
        });
    }

    [Fact]
    public async Task NavigationSelection_RemainsBlueAfterViewportReplacement()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = TestMainViewModelFactory.Create(
                new StubLogFileRepository(),
                new StubLogGroupRepository(),
                new StubSettingsRepository(),
                new StubLogReaderService(),
                new StubSearchService(),
                new StubFileTailService(),
                new StubEncodingDetectionService(),
                enableLifecycleTimer: false);
            var tab = CreateTab("navigation-blue");
            await tab.LoadAsync();
            viewModel.Tabs.Add(tab);
            viewModel.SelectedTab = tab;

            var view = new LogViewportView { DataContext = viewModel };
            var window = new Window
            {
                Style = new Style(typeof(Window)),
                Content = view,
                Width = 640,
                Height = 320,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };

            try
            {
                WpfTestHost.ShowHidden(window);
                await WpfTestHost.FlushAsync();

                await viewModel.NavigateToLineAsync(tab.FilePath, 42, disableAutoScroll: true);
                await WpfTestHost.FlushAsync();

                var listBox = FindDescendant<ListBox>(view, "LogListBox");
                Assert.NotNull(listBox);
                AssertSelectedBlueLine(listBox, 42);

                tab.ApplyVisibleLines(tab.VisibleLines
                    .Select(line => new LogLineViewModel
                    {
                        LineNumber = line.LineNumber,
                        Text = line.Text,
                        HighlightColor = line.HighlightColor
                    })
                    .ToList());
                await WpfTestHost.FlushAsync();

                AssertSelectedBlueLine(listBox, 42);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task NavigationSelection_IsBlueWhenNavigationSwitchesTabs()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = TestMainViewModelFactory.Create(
                new StubLogFileRepository(),
                new StubLogGroupRepository(),
                new StubSettingsRepository(),
                new StubLogReaderService(),
                new StubSearchService(),
                new StubFileTailService(),
                new StubEncodingDetectionService(),
                enableLifecycleTimer: false);
            var sourceTab = CreateTab("navigation-source");
            var targetTab = CreateTab("navigation-target");
            await sourceTab.LoadAsync();
            await targetTab.LoadAsync();
            viewModel.Tabs.Add(sourceTab);
            viewModel.Tabs.Add(targetTab);
            viewModel.SelectedTab = sourceTab;

            var view = new LogViewportView { DataContext = viewModel };
            var window = new Window
            {
                Style = new Style(typeof(Window)),
                Content = view,
                Width = 640,
                Height = 320,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };

            try
            {
                WpfTestHost.ShowHidden(window);
                await WpfTestHost.FlushAsync();

                await viewModel.NavigateToLineAsync(targetTab.FilePath, 42, disableAutoScroll: true);
                await WpfTestHost.FlushAsync();
                await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
                    static () => { },
                    System.Windows.Threading.DispatcherPriority.ContextIdle);

                Assert.Same(targetTab, viewModel.SelectedTab);
                var listBox = FindDescendant<ListBox>(view, "LogListBox");
                Assert.NotNull(listBox);
                Assert.Same(targetTab, listBox.DataContext);
                AssertSelectedBlueLine(listBox, 42);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task SearchHitNavigation_IsBlueInCurrentTabAfterViewportCapacityChanges()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = TestMainViewModelFactory.Create(
                new StubLogFileRepository(),
                new StubLogGroupRepository(),
                new StubSettingsRepository(),
                new StubLogReaderService(),
                new StubSearchService(),
                new StubFileTailService(),
                new StubEncodingDetectionService(),
                enableLifecycleTimer: false);
            var tab = CreateTab("search-current-tab");
            await tab.LoadAsync();
            viewModel.Tabs.Add(tab);
            viewModel.SelectedTab = tab;

            var fileResult = new FileSearchResultViewModel(
                new SearchResult
                {
                    FilePath = tab.FilePath,
                    Hits =
                    [
                        new SearchHit
                        {
                            LineNumber = 42,
                            LineText = "Line 42 content",
                            MatchStart = 5,
                            MatchLength = 2
                        }
                    ]
                },
                viewModel);
            var hitRow = fileResult.GetHitRow(0);
            var searchResultsList = new ListBox
            {
                ItemsSource = new[] { hitRow },
                SelectedItem = hitRow,
                Width = 320,
                Height = 120
            };
            var viewport = new LogViewportView { DataContext = viewModel };
            var window = new Window
            {
                Style = new Style(typeof(Window)),
                Content = new Grid
                {
                    RowDefinitions =
                    {
                        new RowDefinition(),
                        new RowDefinition { Height = new GridLength(120) }
                    },
                    Children =
                    {
                        viewport,
                        searchResultsList
                    }
                },
                Width = 640,
                Height = 440,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };
            Grid.SetRow(searchResultsList, 1);

            try
            {
                WpfTestHost.ShowHidden(window);
                await WpfTestHost.FlushAsync();
                searchResultsList.Focus();

                await fileResult.NavigateToHitCommand.ExecuteAsync(hitRow.Hit);
                tab.UpdateViewportLineCount(14);
                await WpfTestHost.FlushAsync();
                await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
                    static () => { },
                    System.Windows.Threading.DispatcherPriority.ContextIdle);

                Assert.Same(tab, viewModel.SelectedTab);
                var listBox = FindDescendant<ListBox>(viewport, "LogListBox");
                Assert.NotNull(listBox);
                AssertSelectedBlueLine(listBox, 42);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task SearchNavigation_AcrossTabs_ShiftsKeyboardFocusToViewport()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = TestMainViewModelFactory.Create(
                new StubLogFileRepository(),
                new StubLogGroupRepository(),
                new StubSettingsRepository(),
                new StubLogReaderService(),
                new StubSearchService(),
                new StubFileTailService(),
                new StubEncodingDetectionService(),
                enableLifecycleTimer: false);
            var sourceTab = CreateTab("search-focus-source");
            var tab = CreateTab("search-focus-target");
            await sourceTab.LoadAsync();
            await tab.LoadAsync();
            viewModel.Tabs.Add(sourceTab);
            viewModel.Tabs.Add(tab);
            viewModel.SelectedTab = sourceTab;

            var fileResult = new FileSearchResultViewModel(
                new SearchResult
                {
                    FilePath = tab.FilePath,
                    Hits =
                    [
                        new SearchHit
                        {
                            LineNumber = 42,
                            LineText = "Line 42 content",
                            MatchStart = 5,
                            MatchLength = 2
                        }
                    ]
                },
                viewModel);
            var hitRow = fileResult.GetHitRow(0);
            var searchResultsList = new ListBox
            {
                ItemsSource = new[] { hitRow },
                SelectedItem = hitRow,
                Width = 320,
                Height = 120
            };
            var viewport = new LogViewportView { DataContext = viewModel };
            var window = new Window
            {
                Style = new Style(typeof(Window)),
                Content = new Grid
                {
                    RowDefinitions =
                    {
                        new RowDefinition(),
                        new RowDefinition { Height = new GridLength(120) }
                    },
                    Children =
                    {
                        viewport,
                        searchResultsList
                    }
                },
                Width = 640,
                Height = 440,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };
            Grid.SetRow(searchResultsList, 1);

            try
            {
                WpfTestHost.ShowHidden(window);
                await WpfTestHost.FlushAsync();
                searchResultsList.Focus();
                Assert.True(searchResultsList.IsKeyboardFocusWithin);

                await fileResult.NavigateToHitCommand.ExecuteAsync(hitRow.Hit);
                tab.UpdateViewportLineCount(14);
                await WpfTestHost.FlushAsync();
                await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
                    static () => { },
                    System.Windows.Threading.DispatcherPriority.ContextIdle);

                var listBox = FindDescendant<ListBox>(viewport, "LogListBox");
                Assert.NotNull(listBox);
                Assert.True(listBox!.IsKeyboardFocusWithin, "Expected keyboard focus to move into the viewport list box.");
                Assert.False(searchResultsList.IsKeyboardFocusWithin, "Expected the search results list to lose keyboard focus.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ScrollBar_TracksNavigationAndRemainsBottomPinnedAcrossAutoScrollTransitions()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = TestMainViewModelFactory.Create(
                new StubLogFileRepository(),
                new StubLogGroupRepository(),
                new StubSettingsRepository(),
                new StubLogReaderService(),
                new StubSearchService(),
                new StubFileTailService(),
                new StubEncodingDetectionService(),
                enableLifecycleTimer: false);
            var tab = CreateTab("scrollbar-navigation");
            var otherTab = CreateTab("scrollbar-navigation-other");
            await tab.LoadAsync();
            await otherTab.LoadAsync();
            viewModel.Tabs.Add(tab);
            viewModel.Tabs.Add(otherTab);
            viewModel.SelectedTab = tab;

            var viewport = new LogViewportView { DataContext = viewModel };
            var window = new Window
            {
                Style = new Style(typeof(Window)),
                Content = viewport,
                Width = 640,
                Height = 320,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };

            try
            {
                WpfTestHost.ShowHidden(window);
                await WpfTestHost.FlushAsync();

                var scrollBar = FindDescendant<ScrollBar>(viewport, "VerticalScrollBar");
                Assert.NotNull(scrollBar);
                Assert.Equal(tab.MaxScrollPosition, scrollBar!.Maximum);
                AssertScrollBarThumbAtBottom(scrollBar);

                LogViewportView.HandleMouseWheel(viewModel, tab, 120);
                await WpfTestHost.FlushAsync();

                Assert.False(tab.AutoScrollEnabled);
                Assert.Equal(tab.ScrollPosition, scrollBar.Value);

                await viewModel.NavigateToLineAsync(tab.FilePath, 42, disableAutoScroll: true);
                await WpfTestHost.FlushAsync();

                Assert.Equal(tab.ScrollPosition, scrollBar.Value);

                for (var iteration = 0; iteration < 5; iteration++)
                {
                    viewModel.GlobalAutoScrollEnabled = true;
                    await tab.MoveViewportToBottomAsync();
                    await WpfTestHost.FlushAsync();

                    Assert.Equal(tab.MaxScrollPosition, scrollBar.Maximum);
                    Assert.Equal(tab.ViewportLineCount, scrollBar.ViewportSize);
                    AssertScrollBarThumbAtBottom(scrollBar);

                    viewModel.GlobalAutoScrollEnabled = false;
                    await tab.LoadViewportAsync(
                        Math.Max(0, tab.MaxScrollPosition - 10 - iteration),
                        tab.ViewportLineCount);
                    await WpfTestHost.FlushAsync();

                    Assert.Equal(tab.ScrollPosition, scrollBar.Value);
                }

                viewModel.GlobalAutoScrollEnabled = true;
                await tab.MoveViewportToBottomAsync();
                await otherTab.MoveViewportToBottomAsync();
                foreach (var selectedTab in new[] { otherTab, tab, otherTab, tab })
                {
                    viewModel.SelectedTab = selectedTab;
                    await WpfTestHost.FlushAsync();

                    scrollBar = FindDescendant<ScrollBar>(viewport, "VerticalScrollBar");
                    Assert.NotNull(scrollBar);
                    Assert.Same(selectedTab, scrollBar!.DataContext);
                    Assert.Equal(selectedTab.MaxScrollPosition, scrollBar.Maximum);
                    Assert.Equal(selectedTab.ViewportLineCount, scrollBar.ViewportSize);
                    AssertScrollBarThumbAtBottom(scrollBar);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ArrowSelectionPastViewportEdge_ExitsAutoScrollAndMovesScrollBar()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = TestMainViewModelFactory.Create(
                new StubLogFileRepository(),
                new StubLogGroupRepository(),
                new StubSettingsRepository(),
                new StubLogReaderService(),
                new StubSearchService(),
                new StubFileTailService(),
                new StubEncodingDetectionService(),
                enableLifecycleTimer: false);
            var tab = CreateTab("scrollbar-arrow");
            await tab.LoadAsync();
            viewModel.Tabs.Add(tab);
            viewModel.SelectedTab = tab;

            var viewport = new LogViewportView { DataContext = viewModel };
            var window = new Window
            {
                Style = new Style(typeof(Window)),
                Content = viewport,
                Width = 640,
                Height = 320,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };

            try
            {
                WpfTestHost.ShowHidden(window);
                await WpfTestHost.FlushAsync();

                var listBox = FindDescendant<ListBox>(viewport, "LogListBox");
                var scrollBar = FindDescendant<ScrollBar>(viewport, "VerticalScrollBar");
                Assert.NotNull(listBox);
                Assert.NotNull(scrollBar);
                listBox!.SelectedItem = listBox.Items[0];
                var startingScrollPosition = tab.ScrollPosition;

                var handled = LogViewportView.HandleKeyboardNavigation(
                    listBox,
                    viewModel,
                    tab,
                    Key.Up,
                    ModifierKeys.None);
                await WpfTestHost.FlushAsync();

                Assert.True(handled);
                Assert.False(tab.AutoScrollEnabled);
                Assert.Equal(startingScrollPosition - 1, tab.ScrollPosition);
                Assert.Equal(tab.ScrollPosition, scrollBar!.Value);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static LogTabViewModel CreateTab(string fileName)
    {
        return new LogTabViewModel(
            fileId: Guid.NewGuid().ToString("N"),
            filePath: $@"C:\test\{fileName}.log",
            logReader: new StubLogReaderService(),
            tailService: new StubFileTailService(),
            encodingDetectionService: new StubEncodingDetectionService(),
            settings: new AppSettings());
    }

    private static ListBox CreateLogListBox(params int[] lineNumbers)
    {
        var listBox = new ListBox
        {
            SelectionMode = SelectionMode.Extended,
            ItemsSource = lineNumbers
                .Select(lineNumber => new LogLineViewModel
                {
                    LineNumber = lineNumber,
                    Text = $"Line {lineNumber}"
                })
                .ToArray()
        };

        listBox.ApplyTemplate();
        listBox.UpdateLayout();
        return listBox;
    }

    private static void AssertSelectedBlueLine(ListBox listBox, int lineNumber)
    {
        var selectedLine = Assert.IsType<LogLineViewModel>(listBox.SelectedItem);
        Assert.Equal(lineNumber, selectedLine.LineNumber);

        listBox.UpdateLayout();
        var container = Assert.IsType<ListBoxItem>(listBox.ItemContainerGenerator.ContainerFromItem(selectedLine));
        var background = Assert.IsType<SolidColorBrush>(container.Background);
        Assert.Equal(Color.FromRgb(0xB0, 0xD4, 0xFF), background.Color);
        Assert.Equal(Color.FromRgb(0xB0, 0xD4, 0xFF), RenderBackgroundColor(container));
    }

    private static void AssertScrollBarThumbAtBottom(ScrollBar scrollBar)
    {
        scrollBar.ApplyTemplate();
        scrollBar.UpdateLayout();
        var track = FindDescendant<Track>(scrollBar);
        var thumb = track?.Thumb;
        Assert.NotNull(track);
        Assert.NotNull(thumb);
        var thumbBottom = thumb!.TranslatePoint(new Point(0, thumb.ActualHeight), track).Y;
        Assert.InRange(Math.Abs(track!.ActualHeight - thumbBottom), 0, 1);
        Assert.Equal(scrollBar.Maximum, scrollBar.Value);
    }

    private static Color RenderBackgroundColor(ListBoxItem container)
    {
        var window = Window.GetWindow(container) ??
            throw new InvalidOperationException("Expected the selected row to be hosted in a window.");
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var samplePoint = container.TranslatePoint(
            new Point(Math.Max(0, container.ActualWidth - 5), container.ActualHeight / 2),
            window);
        var pixel = new byte[4];
        bitmap.CopyPixels(
            new Int32Rect(
                Math.Clamp((int)Math.Floor(samplePoint.X), 0, width - 1),
                Math.Clamp((int)Math.Floor(samplePoint.Y), 0, height - 1),
                1,
                1),
            pixel,
            stride: 4,
            offset: 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static T? FindDescendant<T>(DependencyObject parent, string? name = null)
        where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && (name == null || match.Name == name))
                return match;

            var descendant = FindDescendant<T>(child, name);
            if (descendant != null)
                return descendant;
        }

        return null;
    }
}
