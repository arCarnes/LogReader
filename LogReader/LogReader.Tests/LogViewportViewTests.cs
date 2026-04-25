using LogReader.App.Views;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Models;
using System.Windows.Controls;
using System.Windows.Input;

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
}
