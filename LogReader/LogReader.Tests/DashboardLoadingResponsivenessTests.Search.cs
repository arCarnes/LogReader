namespace LogReader.Tests;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LogReader.App.ViewModels;
using LogReader.Infrastructure.Services;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_ReadyDashboardRemainsUsableWhileAnotherLoads(bool allOpenTabs)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(searchService: new SearchService());
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var panel = vm.SearchPanel;
            var execute = BindSearchControl(panel, nameof(panel.AreExecutionControlsEnabled));
            await AssertSearchControlAsync(execute, false);
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            var ready = vm.SelectedTab!;
            await WaitForIndexReadyAsync(ready);
            panel.TargetMode = allOpenTabs ? SearchFilterTargetMode.AllOpenTabs : SearchFilterTargetMode.CurrentTab;
            await AssertSearchControlAsync(execute, true);
            Assert.True(panel.AreTargetAndSourceToggleEnabled);
            Assert.True(panel.AreResultsInteractionEnabled);
            Assert.True(vm.IsDashboardLoading);
            Assert.False(load.IsCompleted);

            panel.Query = "ready";
            await panel.ExecuteSearchCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            var result = Assert.Single(panel.Results);
            Assert.Equal(ready.FilePath, result.FilePath);
            var hit = Assert.Single(result.Hits);
            await result.NavigateToHitCommand.ExecuteAsync(hit).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(ready, vm.SelectedTab);
            Assert.False(vm.GlobalAutoScrollEnabled);

            Assert.True(panel.IsMonitorNewMatchesVisible);
            panel.StartMonitoringNewMatchesCommand.Execute(null);
            Assert.True(panel.IsMonitoringNewMatches);
            panel.StopMonitoringNewMatchesCommand.Execute(null);
            Assert.False(panel.IsMonitoringNewMatches);

            panel.SearchDataMode = SearchDataMode.Tail;
            await panel.ExecuteSearchCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(panel.IsSearching);
            panel.CancelSearchCommand.Execute(null);
            Assert.False(panel.IsSearching);
            panel.ClearResultsCommand.Execute(null);
            Assert.Empty(panel.Results);
            Assert.Empty(panel.Query);
            Assert.True(vm.IsDashboardLoading);
            Assert.False(load.IsCompleted);
            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task SearchBindings_BlockUnresolvedScopeBeforePlaceholdersExist()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var files = new GatedTargetRepository();
            await using var fixture = await CreateAsync(fileRepository: files);
            var vm = fixture.ViewModel;
            files.HeldFileId = vm.Groups[0].MemberFiles.Single().FileId;
            var panel = vm.SearchPanel;
            panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            var execute = BindSearchControl(panel, nameof(panel.AreExecutionControlsEnabled));
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            try
            {
                await files.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(vm.FilteredTabs);
                await AssertSearchControlAsync(execute, false);
                panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
                await AssertSearchControlAsync(execute, false);
                Assert.True(panel.AreTargetAndSourceToggleEnabled);
                panel.Query = "row";
                await panel.ExecuteSearchCommand.ExecuteAsync(null);
                Assert.Empty(panel.Results);
                Assert.Empty(panel.StatusText);
            }
            finally
            {
                files.Release();
                fixture.Reader.Release();
                await load.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await AssertSearchControlAsync(execute, true);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_AllOpenTabsSettledFailuresRemainSearchable(bool allFailed)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(includeMissing: true, allMissing: allFailed,
                searchService: new SearchService());
            var vm = fixture.ViewModel;
            fixture.Reader.Release();
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            var panel = vm.SearchPanel;
            panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            Assert.True(panel.AreExecutionControlsEnabled);
            panel.Query = "row";
            await panel.ExecuteSearchCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            var error = Assert.Single(panel.Results, result => !string.IsNullOrEmpty(result.Error));
            Assert.Equal(vm.Tabs.Single(tab => tab.HasLoadError).FilePath, error.FilePath);
            Assert.Equal(allFailed ? 1 : 2, panel.Results.Count);
            vm.SelectedTab = vm.Tabs.Single(tab => tab.HasLoadError);
            panel.TargetMode = SearchFilterTargetMode.CurrentTab;
            Assert.False(panel.AreExecutionControlsEnabled);
        });
    }

    [Fact]
    public async Task SearchNavigation_PendingOrOutOfScopeTargetsDoNotChangeSelection()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = vm.SelectedTab;
            await vm.NavigateToLineAsync(fixture.LargePath, 1, disableAutoScroll: true, suppressDuringDashboardLoad: true);
            Assert.Same(pending, vm.SelectedTab);
            Assert.True(vm.GlobalAutoScrollEnabled);
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            var ready = vm.SelectedTab;
            await vm.NavigateToLineAsync(fixture.LargePath, 1, disableAutoScroll: true, suppressDuringDashboardLoad: true);
            Assert.Same(ready, vm.SelectedTab);
            Assert.True(vm.GlobalAutoScrollEnabled);
            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.NavigateToLineAsync(fixture.LargePath, 1, disableAutoScroll: true, suppressDuringDashboardLoad: true);
            Assert.Same(pending, vm.SelectedTab);
            Assert.False(vm.GlobalAutoScrollEnabled);
        });
    }

    [Theory]
    [InlineData(SearchDataMode.DiskSnapshot)]
    [InlineData(SearchDataMode.Tail)]
    public async Task SearchBindings_FollowTargetSelectionAndReadiness(SearchDataMode mode)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(includeSmallInFirstDashboard: true, searchService: new SearchService());
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = vm.Tabs.Single(tab => tab.FilePath == fixture.LargePath);
            var ready = vm.Tabs.Single(tab => tab.FilePath.EndsWith("small.log"));
            await WaitForIndexReadyAsync(ready);
            var panel = vm.SearchPanel;
            panel.SearchDataMode = mode;
            var execute = BindSearchControl(panel, nameof(panel.AreExecutionControlsEnabled));
            await AssertSearchControlAsync(execute, false);
            panel.Query = "ready";
            await panel.ExecuteSearchCommand.ExecuteAsync(null);
            Assert.Empty(panel.Results);
            Assert.False(panel.IsSearching);

            vm.SelectedTab = ready;
            await AssertSearchControlAsync(execute, true);
            panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            await AssertSearchControlAsync(execute, false);
            await panel.ExecuteSearchCommand.ExecuteAsync(null);
            Assert.Empty(panel.Results);
            Assert.False(panel.IsSearching);
            panel.TargetMode = SearchFilterTargetMode.CurrentTab;
            await AssertSearchControlAsync(execute, true);
            vm.SelectedTab = pending;
            await AssertSearchControlAsync(execute, false);

            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertSearchControlAsync(execute, true);
            panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            await AssertSearchControlAsync(execute, true);
        });
    }

    [Fact]
    public async Task SearchBindings_FollowUnselectedMembershipAndSettledFailures()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            fixture.Reader.Release();
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            var panel = vm.SearchPanel;
            panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            var execute = BindSearchControl(panel, nameof(panel.AreExecutionControlsEnabled));
            await AssertSearchControlAsync(execute, true);
            var extraPath = System.IO.Path.Combine(_root, "missing-search.log");
            using var prepared = await vm.CreateDashboardPlaceholderAsync(extraPath, vm.Groups[0].Id, default);
            Assert.NotNull(prepared);
            vm.Groups[0].Model.FileIds.Add(prepared.Tab.FileId);
            vm.NotifyFilteredTabsChanged();
            await AssertSearchControlAsync(execute, false);
            await prepared.Tab.SetDashboardLoadFailureAsync("Fixture search failure");
            await AssertSearchControlAsync(execute, true);
            prepared.Tab.ActiveSession.IsLoading = true;
            await AssertSearchControlAsync(execute, false);
            prepared.Tab.ActiveSession.IsLoading = false;
            await AssertSearchControlAsync(execute, true);
            prepared.Tab.ActiveSession.HasLoadError = false;
            await AssertSearchControlAsync(execute, false);
            await vm.CloseTabCommand.ExecuteAsync(prepared.Tab);
            await AssertSearchControlAsync(execute, true);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_AdHocSettledFailureDoesNotBlockHealthyTabs(bool recoverFailedTab)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(searchService: new SearchService());
            var vm = fixture.ViewModel;
            var healthyPath = System.IO.Path.Combine(_root, "healthy-adhoc.log");
            var missingPath = System.IO.Path.Combine(_root, "missing-adhoc.log");
            await System.IO.File.WriteAllTextAsync(healthyPath, "ready healthy");
            await vm.OpenFilePathAsync(healthyPath).WaitAsync(TimeSpan.FromSeconds(5));
            var healthy = Assert.Single(vm.Tabs);
            Assert.True(healthy.IsAdHocScope);
            await WaitForIndexReadyAsync(healthy);
            await vm.OpenFilePathAsync(missingPath).WaitAsync(TimeSpan.FromSeconds(5));
            var failed = Assert.Single(vm.Tabs, tab => tab.FilePath == missingPath);
            Assert.True(failed.IsAdHocScope);
            Assert.True(failed.HasLoadError);
            Assert.False(failed.IsLoading);
            Assert.False(failed.IsIndexReady);
            Assert.False(vm.IsDashboardLoading);
            vm.SelectedTab = healthy;
            var panel = vm.SearchPanel;
            panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
            var execute = BindSearchControl(panel, nameof(panel.AreExecutionControlsEnabled));
            execute.Command = panel.ExecuteSearchCommand;
            await AssertSearchControlAsync(execute, true);

            panel.Query = "ready";
            await panel.ExecuteSearchCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            var healthyResult = Assert.Single(panel.Results, result => result.FilePath == healthyPath);
            Assert.Single(healthyResult.Hits);
            Assert.False(string.IsNullOrEmpty(Assert.Single(panel.Results, result => result.FilePath == missingPath).Error));

            try
            {
                failed.ActiveSession.IsLoading = true;
                await AssertSearchControlAsync(execute, false);
                await panel.ExecuteSearchCommand.ExecuteAsync(null);
                Assert.Same(healthyResult, Assert.Single(panel.Results, result => result.FilePath == healthyPath));
            }
            finally { failed.ActiveSession.IsLoading = false; }
            await AssertSearchControlAsync(execute, true);
            vm.SelectedTab = failed;
            panel.TargetMode = SearchFilterTargetMode.CurrentTab;
            await AssertSearchControlAsync(execute, false);
            vm.SelectedTab = healthy;
            await AssertSearchControlAsync(execute, true);
            panel.TargetMode = SearchFilterTargetMode.AllOpenTabs;

            if (recoverFailedTab)
            {
                await System.IO.File.WriteAllTextAsync(missingPath, "ready recovered");
                await vm.OpenFilePathAsync(missingPath, reloadIfLoadError: true).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(failed, vm.SelectedTab);
                Assert.True(failed.IsIndexReady);
                Assert.False(failed.HasLoadError);
            }
            else
            {
                await vm.CloseTabCommand.ExecuteAsync(failed).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.DoesNotContain(failed, vm.Tabs);
            }

            await AssertSearchControlAsync(execute, true);
            await panel.ExecuteSearchCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(recoverFailedTab ? 2 : 1, panel.Results.Count);
            Assert.All(panel.Results, result =>
            {
                Assert.Single(result.Hits);
                Assert.True(string.IsNullOrEmpty(result.Error));
            });
        });
    }

    private static Button BindSearchControl(SearchPanelViewModel panel, string property)
    {
        var button = new Button();
        button.SetBinding(UIElement.IsEnabledProperty, new Binding(property) { Source = panel });
        return button;
    }

    private static async Task AssertSearchControlAsync(Button button, bool enabled)
    {
        await WpfTestHost.FlushAsync();
        Assert.Equal(enabled, button.IsEnabled);
    }
}
