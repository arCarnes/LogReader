using LogReader.App.Views;
using LogReader.App.ViewModels;

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
}
