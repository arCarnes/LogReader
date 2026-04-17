using LogReader.App.Views;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Models;
using System.Windows.Controls;

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
        RunSta(() =>
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

}
