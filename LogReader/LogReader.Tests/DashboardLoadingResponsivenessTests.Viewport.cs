namespace LogReader.Tests;

using LogReader.App.ViewModels;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenIndexCompletion_PreservesInitialTailOrManualPreviewPosition(bool rememberPreview)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            vm.GlobalAutoScrollEnabled = false;
            await vm.AutoScrollSyncTask;
            var group = vm.Groups[0];
            var load = vm.HandleDashboardGroupInvokedAsync(group);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tab = vm.SelectedTab!;
            long? anchor = null;
            if (rememberPreview)
            {
                await WaitForPreviewAsync(tab);
                anchor = tab.PreviewLines[20].ByteOffset;
                tab.RememberPreviewPosition(anchor, [tab.PreviewLines[22].ByteOffset]);
            }
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            var selected = vm.SelectedTab;
            var reads = fixture.Reader.ViewportReadCounts.GetValueOrDefault(fixture.LargePath);
            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(tab.IsIndexReady);
            Assert.Same(selected, vm.SelectedTab);
            Assert.Empty(tab.VisibleLines);
            Assert.Equal(reads, fixture.Reader.ViewportReadCounts.GetValueOrDefault(fixture.LargePath));
            var index = tab.ActiveSession.DebugLineIndex!;
            var expectedStart = anchor is { } offset
                ? Enumerable.Range(0, index.LineCount).Single(i => index.LineOffsets[i] == offset)
                : tab.MaxScrollPosition;
            Assert.Equal(expectedStart, tab.ViewportStartLine);
            Assert.Equal(expectedStart, tab.ScrollPosition);
            await vm.HandleDashboardGroupInvokedAsync(group).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForVisibleLinesAsync(tab);
            Assert.Equal(expectedStart, tab.ViewportStartLine);
            Assert.Equal($"row {expectedStart}", tab.VisibleLines[0].Text);
            if (rememberPreview)
                Assert.Contains(expectedStart + 3, tab.SelectedLineNumbers);
            await tab.LoadViewportAsync(10, tab.ViewportLineCount);
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]);
            await vm.HandleDashboardGroupInvokedAsync(group);
            await WaitForVisibleLinesAsync(tab);
            Assert.Equal(10, tab.ViewportStartLine);
            Assert.Equal("row 10", tab.VisibleLines[0].Text);
        });
    }
}
