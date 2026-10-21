namespace LogReader.Tests;

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Fact]
    public async Task FilterBindings_FollowSelectionTargetAndScopeWhileIndexing()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(includeSmallInFirstDashboard: true);
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = vm.Tabs.Single(tab => tab.FilePath == fixture.LargePath);
            var ready = vm.Tabs.Single(tab => tab.FilePath.EndsWith("small.log"));
            await WaitForIndexReadyAsync(ready);
            var buttons = BindFilterButtons(vm);
            await AssertFilterButtonsAsync(buttons, false);

            vm.SelectedTab = ready;
            await AssertFilterButtonsAsync(buttons, true);
            vm.FilterPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            await AssertFilterButtonsAsync(buttons, false);
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            await AssertFilterButtonsAsync(buttons, true);
            vm.ToggleGroupSelection(vm.Groups[0]);
            await AssertFilterButtonsAsync(buttons, false);
            vm.SelectedTab = ready;
            vm.FilterPanel.TargetMode = SearchFilterTargetMode.CurrentTab;
            await AssertFilterButtonsAsync(buttons, true);
            vm.SelectedTab = pending;
            await AssertFilterButtonsAsync(buttons, false);

            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertFilterButtonsAsync(buttons, true);
            vm.FilterPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            await AssertFilterButtonsAsync(buttons, true);
        });
    }

    [Fact]
    public async Task FilterBindings_FollowUnselectedMembershipLoadingAndErrorChanges()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            fixture.Reader.Release();
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            vm.FilterPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            var buttons = BindFilterButtons(vm);
            await AssertFilterButtonsAsync(buttons, true);
            var extraPath = Path.Combine(_root, "extra.log");
            await File.WriteAllTextAsync(extraPath, "extra");
            using var prepared = await vm.CreateDashboardPlaceholderAsync(extraPath, vm.Groups[0].Id, default);
            Assert.NotNull(prepared);
            vm.Groups[0].Model.FileIds.Add(prepared.Tab.FileId);
            vm.NotifyFilteredTabsChanged();
            Assert.NotSame(prepared.Tab, vm.SelectedTab);
            await AssertFilterButtonsAsync(buttons, false);
            await prepared.Tab.SetDashboardLoadFailureAsync("Fixture failure");
            await AssertFilterButtonsAsync(buttons, true);
            prepared.Tab.ActiveSession.IsLoading = true;
            await AssertFilterButtonsAsync(buttons, false);
            prepared.Tab.ActiveSession.IsLoading = false;
            await AssertFilterButtonsAsync(buttons, true);
            prepared.Tab.ActiveSession.HasLoadError = false;
            await AssertFilterButtonsAsync(buttons, false);
            await vm.CloseTabCommand.ExecuteAsync(prepared.Tab);
            await AssertFilterButtonsAsync(buttons, true);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllOpenTabsFilter_ReportsSettledFailuresAndClearsState(bool allFailed)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var search = new RecordingFilterSearchService();
            await using var fixture = await CreateAsync(includeMissing: true, allMissing: allFailed, searchService: search);
            var vm = fixture.ViewModel;
            fixture.Reader.Release();
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            var failed = vm.Tabs.Single(tab => tab.HasLoadError);
            var ready = vm.Tabs.Where(tab => tab.IsIndexReady).ToArray();
            vm.FilterPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            var buttons = BindFilterButtons(vm);
            await AssertFilterButtonsAsync(buttons, true);
            vm.FilterPanel.Query = "row";
            await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null);
            var warning = Assert.Single(vm.FilterPanel.Warnings);
            Assert.Equal(failed.FilePath, warning.FilePath);
            Assert.Contains(failed.ActiveSession.LastErrorMessage!, warning.Message);
            Assert.False(failed.IsFilterActive);
            if (allFailed)
            {
                Assert.Empty(search.Requests);
                Assert.Empty(ready);
                Assert.Contains("No readable open tabs", vm.FilterPanel.StatusText);
            }
            else
            {
                var tab = Assert.Single(ready);
                Assert.Equal(new[] { tab.FilePath }, Assert.Single(search.Requests));
                Assert.True(tab.IsFilterActive);
                Assert.Equal(new[] { 1 }, tab.CaptureActiveFilterSnapshot()!.MatchingLineNumbers);
                Assert.NotNull(vm.FilterPanel.GetApplicableAllOpenTabsFilterSnapshot(tab.FilePath, SearchDataMode.DiskSnapshot));
            }
            await vm.FilterPanel.ClearFilterCommand.ExecuteAsync(null);
            Assert.Empty(vm.FilterPanel.Warnings);
            Assert.Empty(vm.FilterPanel.Query);
            Assert.All(vm.Tabs, tab => Assert.False(tab.IsFilterActive));
            Assert.All(ready, tab => Assert.Null(vm.FilterPanel.GetApplicableAllOpenTabsFilterSnapshot(tab.FilePath, SearchDataMode.DiskSnapshot)));
            Assert.True(failed.HasLoadError);
            vm.SelectedTab = failed;
            vm.FilterPanel.TargetMode = SearchFilterTargetMode.CurrentTab;
            await AssertFilterButtonsAsync(buttons, false);
        });
    }

    private static Button[] BindFilterButtons(MainViewModel vm)
    {
        return new[] { vm.FilterPanel.ApplyFilterCommand, vm.FilterPanel.ClearFilterCommand }.Select(command =>
        {
            var button = new Button { Command = command };
            button.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(vm.FilterPanel.AreExecutionControlsEnabled))
                { Source = vm.FilterPanel });
            return button;
        }).ToArray();
    }

    private static async Task AssertFilterButtonsAsync(IEnumerable<Button> buttons, bool enabled)
    {
        await WpfTestHost.FlushAsync();
        Assert.All(buttons, button => Assert.Equal(enabled, button.IsEnabled));
    }

    private static async Task WaitForIndexReadyAsync(LogTabViewModel tab)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (tab.IsIndexReady) ready.TrySetResult();
        }
        tab.PropertyChanged += Changed;
        try
        {
            if (tab.IsIndexReady) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { tab.PropertyChanged -= Changed; }
    }

    private sealed class RecordingFilterSearchService : StubSearchService, ISearchService
    {
        public List<string[]> Requests { get; } = new();

        public Task<IReadOnlyList<FilterResult>> FilterFilesAsync(SearchRequest request,
            IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default)
        {
            Requests.Add(request.FilePaths.ToArray());
            Assert.All(request.FilePaths, path => Assert.DoesNotContain("missing.log", path));
            Assert.Equal(request.FilePaths.OrderBy(path => path), fileEncodings.Keys.OrderBy(path => path));
            return Task.FromResult<IReadOnlyList<FilterResult>>(request.FilePaths.Select(path => new FilterResult
            {
                FilePath = path, MatchingLineNumbers = [1], EvaluatedThroughLine = File.ReadLines(path).Count()
            }).ToArray());
        }
    }
}
