namespace LogReader.Tests;

using LogReader.App.ViewModels;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(false, false, false, false, true)]
    public async Task HiddenRestoration_DefersViewportUntilActivation(
        bool reload, bool filtered, bool autoScroll, bool storedScopeFilter, bool previewAnchor)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(searchService: new RecordingFilterSearchService());
            var vm = fixture.ViewModel;
            fixture.Reader.Release();
            var group = vm.Groups[0];
            await vm.HandleDashboardGroupInvokedAsync(group);
            var original = vm.SelectedTab!;
            vm.GlobalAutoScrollEnabled = autoScroll;
            await vm.AutoScrollSyncTask;
            if (storedScopeFilter)
            {
                vm.FilterPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
                vm.FilterPanel.Query = "row";
                await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);
            }
            else if (filtered)
                await original.ApplyFilterAsync(Enumerable.Range(1, 200).ToArray(), "Saved filter");
            if (!autoScroll)
                await original.LoadViewportAsync(10, original.ViewportLineCount);
            var savedStart = original.ViewportStartLine;
            var navigateTarget = storedScopeFilter ? 1 : 15;
            original.SetNavigateTargetLine(navigateTarget);
            var session = original.ActiveSession;
            if (!reload)
                await vm.CloseTabCommand.ExecuteAsync(original);
            await session.ResetLineIndexAsync();
            fixture.Reader.ResetGate();
            var load = reload ? vm.ReloadDashboardAsync(group) : vm.HandleDashboardGroupInvokedAsync(group);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = vm.SelectedTab!;
            long? anchor = null;
            if (previewAnchor)
            {
                await WaitForPreviewAsync(replacement);
                anchor = replacement.PreviewLines[20].ByteOffset;
                replacement.RememberPreviewPosition(anchor, [replacement.PreviewLines[22].ByteOffset]);
            }
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            var selected = vm.SelectedTab;
            var reads = fixture.Reader.ViewportReadCounts.GetValueOrDefault(fixture.LargePath);
            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(replacement.IsIndexReady);
            Assert.False(replacement.IsVisible);
            Assert.Equal(filtered, replacement.IsFilterActive);
            Assert.Equal(reads, fixture.Reader.ViewportReadCounts.GetValueOrDefault(fixture.LargePath));
            Assert.Empty(replacement.VisibleLines);
            Assert.Same(selected, vm.SelectedTab);
            var index = replacement.ActiveSession.DebugLineIndex!;
            var expectedStart = anchor is { } offset
                ? Enumerable.Range(0, index.LineCount).Single(line => index.LineOffsets[line] == offset)
                : autoScroll ? replacement.MaxScrollPosition : savedStart;
            Assert.Equal(expectedStart, replacement.ViewportStartLine);
            Assert.Equal(expectedStart, replacement.ScrollPosition);
            Assert.Equal(navigateTarget, replacement.NavigateToLineNumber);
            await vm.HandleDashboardGroupInvokedAsync(group).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForVisibleLinesAsync(replacement);
            Assert.Equal(expectedStart, replacement.ViewportStartLine);
            Assert.Equal($"row {expectedStart}", replacement.VisibleLines[0].Text);
            if (previewAnchor)
                Assert.Contains(expectedStart + 3, replacement.SelectedLineNumbers);
        });
    }
}
