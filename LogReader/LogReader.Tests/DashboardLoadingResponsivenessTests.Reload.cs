namespace LogReader.Tests;

using LogReader.App.Services;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Fact]
    public async Task ReloadAnotherDashboard_PreservesPendingProgressAndNavigation()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var pendingGroup = vm.Groups[0];
            var first = vm.HandleDashboardGroupInvokedAsync(pendingGroup);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pendingTab = vm.SelectedTab;
            pendingGroup.IsExpanded = true;
            var progress = pendingGroup.LoadProgress;
            Assert.True(pendingGroup.IsDashboardLoading);
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            var reloadedId = vm.Groups[1].Id;
            var oldRevision = vm.Groups[1].LoadProgress.Revision;
            var oldTab = vm.SelectedTab;

            await vm.ReloadDashboardAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));

            var retainedGroup = vm.Groups.Single(group => group.Id == pendingGroup.Id);
            Assert.True(retainedGroup.IsDashboardLoading);
            Assert.Equal(progress, retainedGroup.LoadProgress);
            Assert.Same(pendingGroup, retainedGroup);
            Assert.True(retainedGroup.IsExpanded);
            Assert.Contains(pendingTab!, vm.Tabs);
            Assert.DoesNotContain(oldTab!, vm.Tabs);
            Assert.Equal(reloadedId, vm.ActiveDashboardId);
            Assert.Equal(reloadedId, vm.SelectedTab!.ScopeDashboardId);
            Assert.True(vm.Groups.Single(group => group.Id == reloadedId).LoadProgress.Revision > oldRevision);
            Assert.False(first.IsCompleted);

            var returned = vm.HandleDashboardGroupInvokedAsync(retainedGroup);
            Assert.True(vm.IsCurrentDashboardLoading);
            Assert.Contains(retainedGroup.DashboardLoadStatus, vm.DashboardLoadingStatusText);
            Assert.Same(pendingTab, vm.SelectedTab);
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
            fixture.Reader.Release();
            await Task.WhenAll(first, returned).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DashboardLoadPhase.Ready, retainedGroup.LoadProgress.Phase);
            Assert.False(retainedGroup.IsDashboardLoading);
            Assert.Equal(0, retainedGroup.DashboardReadyRevision);
            Assert.Same(pendingTab, vm.SelectedTab);
            Assert.True(pendingTab!.IsIndexReady);
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
        });
    }
}
