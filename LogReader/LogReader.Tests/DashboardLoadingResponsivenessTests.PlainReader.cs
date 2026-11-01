namespace LogReader.Tests;

using LogReader.App.ViewModels;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Fact]
    public async Task PlainReader_PublishesCanonicalPlaceholderAndAllowsBackgroundNavigation()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(plainReader: true);
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            var load = vm.HandleDashboardGroupInvokedAsync(group);
            try
            {
                await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var pending = Assert.Single(vm.Tabs);
                Assert.False(pending.IsIndexReady);
                Assert.Empty(pending.PreviewLines);
                Assert.True(vm.AreDashboardNavigationActionsEnabled);
                Assert.False(vm.SearchPanel.AreExecutionControlsEnabled);
                var member = vm.OpenDashboardMemberFileAsync(group, group.MemberFiles[0]);
                await member.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(pending, Assert.Single(vm.Tabs));
                await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
                var selected = vm.SelectedTab;
                Assert.False(load.IsCompleted);
                fixture.Reader.Release();
                await load.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(selected, vm.SelectedTab);
                Assert.True(pending.IsIndexReady);
                Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
                Assert.False(fixture.Reader.ViewportReadCounts.ContainsKey(fixture.LargePath));
                Assert.False(vm.IsDashboardLoading);
            }
            finally
            {
                fixture.Reader.Release();
                await load.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainReader_TransitionAndShutdownRetainCancellationInsensitiveBuild(bool shutdown)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(plainReader: true);
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            var load = vm.HandleDashboardGroupInvokedAsync(group);
            Task drain = Task.CompletedTask;
            try
            {
                await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Single(vm.Tabs);
                if (shutdown)
                {
                    vm.Dispose();
                    drain = vm.ShutdownTask;
                }
                else
                    drain = vm.UnloadDashboardAsync(group);
                Assert.False(drain.IsCompleted);
                fixture.Reader.Release();
                await Task.WhenAll(load, drain).WaitAsync(TimeSpan.FromSeconds(5));
                if (shutdown)
                    Assert.All(vm.Tabs, tab => Assert.True(tab.IsShutdownOrDisposed));
                else
                    Assert.Empty(vm.Tabs);
                Assert.False(vm.IsDashboardLoading);
            }
            finally
            {
                fixture.Reader.Release();
                await Task.WhenAll(load, drain).WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }
    [Fact]
    public async Task PlainReader_QueuedReloadAndUnloadDrainTheOldBuildBeforeReplacingTabs()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(plainReader: true);
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            var load = vm.HandleDashboardGroupInvokedAsync(group);
            Task reload = Task.CompletedTask;
            Task unload = Task.CompletedTask;
            try
            {
                await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var old = Assert.Single(vm.Tabs);
                reload = vm.ReloadDashboardAsync(group);
                unload = vm.UnloadDashboardAsync(group);
                Assert.False(reload.IsCompleted);
                Assert.False(unload.IsCompleted);
                Assert.Same(old, Assert.Single(vm.Tabs));
                fixture.Reader.Release();
                await Task.WhenAll(load, reload, unload).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(vm.Tabs);
                Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
                Assert.Equal(0, vm.DashboardLoadDepth);
            }
            finally
            {
                fixture.Reader.Release();
                await Task.WhenAll(load, reload, unload).WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task PlainReader_InactiveMemberClickSelectsItsPlaceholderAndKeepsTheCycleOwned()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(plainReader: true, includeSmallInFirstDashboard: true);
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            var member = group.MemberFiles.Single(row => row.FilePath == fixture.LargePath);
            await vm.OpenDashboardMemberFileAsync(group, member).WaitAsync(TimeSpan.FromSeconds(5));
            var load = vm.OpenGroupFilesAsync(group);
            try
            {
                await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(group.Id, vm.ActiveDashboardId);
                Assert.Equal(member.FilePath, vm.SelectedTab!.FilePath);
                Assert.False(load.IsCompleted);
                vm.ShowAdHocTabsCommand.Execute(null);
                Assert.Null(vm.ActiveDashboardId);
                Assert.True(vm.IsDashboardLoading);
                Assert.False(vm.AreLoadAffectingActionsEnabled);
                fixture.Reader.Release();
                await load.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(vm.SelectedTab);
                Assert.False(vm.IsDashboardLoading);
                Assert.All(vm.Tabs, tab => Assert.True(tab.IsIndexReady));
            }
            finally
            {
                fixture.Reader.Release();
                await load.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

}
