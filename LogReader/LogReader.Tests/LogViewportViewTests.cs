using LogReader.App.Views;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Models;

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
