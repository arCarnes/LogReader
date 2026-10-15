namespace LogReader.Tests;

using System.IO;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReloadPreparation_HoldsMutationLeaseAndPreservesNavigation(bool fail)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var groups = new GatedReloadRepository();
            var messages = new StubMessageBoxService();
            await using var fixture = await CreateAsync(groupRepository: groups, messageBoxService: messages);
            var vm = fixture.ViewModel;
            fixture.Reader.Release();
            var group = vm.Groups[0];
            await vm.HandleDashboardGroupInvokedAsync(group);
            var original = vm.SelectedTab!;
            groups.Hold = true;
            groups.Failure = fail ? new IOException("Synthetic reload preparation failure") : null;
            var reload = vm.ReloadDashboardAsync(group);
            try
            {
                await groups.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(vm.IsLoadAffectingActionFrozen);
                Assert.False(vm.AreLoadAffectingActionsEnabled);
                Assert.True(vm.AreDashboardNavigationActionsEnabled);
                await vm.CloseTabCommand.ExecuteAsync(original);
                Assert.Contains(original, vm.Tabs);
                await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
                var selected = vm.SelectedTab;
                Assert.True(vm.IsDashboardLoading);
                groups.Release();
                await reload.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(selected, vm.SelectedTab);
                Assert.Equal(vm.Groups[1].Id, vm.ActiveDashboardId);
                Assert.Equal(0, vm.DashboardLoadDepth);
                Assert.True(vm.AreLoadAffectingActionsEnabled);
                if (fail)
                    Assert.Contains(groups.Failure!.Message, messages.LastMessage);
                else
                    Assert.Null(messages.LastMessage);
            }
            finally
            {
                groups.Release();
                await reload.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopeTransition_DuringResolution_DoesNotAdmitAnotherLoad(bool reload)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var files = new GatedTargetRepository();
            await using var fixture = await CreateAsync(fileRepository: files);
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            var member = group.MemberFiles.Single();
            files.HeldFileId = member.FileId;
            var load = vm.HandleDashboardGroupInvokedAsync(group);
            try
            {
                await files.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var revision = group.LoadProgress.Revision;
                var transition = reload ? vm.ReloadDashboardAsync(group) : vm.UnloadDashboardAsync(group);
                Assert.False(transition.IsCompleted);
                Assert.True(vm.IsLoadAffectingActionFrozen);
                Assert.True(vm.AreDashboardNavigationActionsEnabled);
                var click = vm.HandleDashboardGroupInvokedAsync(group);
                var memberClick = vm.OpenDashboardMemberFileAsync(group, member);
                Assert.Equal(1, files.HeldRequestCount);
                Assert.Equal(revision, group.LoadProgress.Revision);

                await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
                var selected = vm.SelectedTab;
                files.Release();
                fixture.Reader.Release();
                await Task.WhenAll(load, transition, click, memberClick).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(selected, vm.SelectedTab);
                Assert.Equal(vm.Groups[1].Id, vm.ActiveDashboardId);
                if (reload)
                {
                    Assert.True(Assert.Single(vm.Tabs, tab => tab.ScopeDashboardId == group.Id).IsIndexReady);
                    Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
                }
                else
                {
                    Assert.DoesNotContain(vm.Tabs, tab => tab.ScopeDashboardId == group.Id);
                    Assert.False(fixture.Reader.BuildCounts.ContainsKey(fixture.LargePath));
                    await vm.HandleDashboardGroupInvokedAsync(group).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(vm.SelectedTab!.IsIndexReady);
                    Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
                }
            }
            finally { files.Release(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopeTransition_ReloadPreparation_BlocksDashboardAndMemberOpens(bool openMember)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var groups = new GatedReloadRepository();
            var files = new GatedTargetRepository();
            await using var fixture = await CreateAsync(includeSmallInFirstDashboard: true,
                fileRepository: files, groupRepository: groups);
            var vm = fixture.ViewModel;
            fixture.Reader.Release();
            var group = vm.Groups[0];
            await vm.HandleDashboardGroupInvokedAsync(group);
            var tab = vm.Tabs.Single(tab => tab.FilePath == fixture.LargePath);
            var session = tab.ActiveSession;
            await vm.CloseTabCommand.ExecuteAsync(tab);
            await session.ResetLineIndexAsync();
            fixture.Reader.ResetGate();
            var revision = group.LoadProgress.Revision;
            var openRequests = files.OpenRequestCount;
            groups.Hold = true;
            var reload = vm.ReloadDashboardAsync(group);
            try
            {
                await groups.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var click = openMember
                    ? vm.OpenDashboardMemberFileAsync(group, group.MemberFiles.Single(member => member.FilePath == fixture.LargePath))
                    : vm.HandleDashboardGroupInvokedAsync(group);
                Assert.Equal(revision, group.LoadProgress.Revision);
                Assert.Equal(openRequests, files.OpenRequestCount);
                Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
                Assert.Single(vm.Tabs);

                groups.Release();
                await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                fixture.Reader.Release();
                await Task.WhenAll(reload, click).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, vm.Tabs.Count);
                Assert.All(vm.Tabs, tab => Assert.True(tab.IsIndexReady));
                Assert.Equal(2, fixture.Reader.BuildCounts[fixture.LargePath]);
            }
            finally { groups.Release(); }
        });
    }

    [Fact]
    public async Task ScopeTransitions_QueuedUnload_WaitsForReloadPreparationAndLeavesNoTabs()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var groups = new GatedReloadRepository();
            await using var fixture = await CreateAsync(groupRepository: groups);
            var vm = fixture.ViewModel;
            fixture.Reader.Release();
            var group = vm.Groups[0];
            await vm.HandleDashboardGroupInvokedAsync(group);
            groups.Hold = true;
            var reload = vm.ReloadDashboardAsync(group);
            try
            {
                await groups.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var unload = vm.UnloadDashboardAsync(group);
                Assert.False(unload.IsCompleted);
                Assert.True(vm.IsLoadAffectingActionFrozen);
                Assert.Single(vm.Tabs);
                groups.Release();
                await Task.WhenAll(reload, unload).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(vm.Tabs);
                Assert.Null(vm.ActiveDashboardId);
                Assert.Equal(0, vm.DashboardLoadDepth);
                Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
            }
            finally { groups.Release(); }
        });
    }

    private sealed class GatedReloadRepository : StubLogGroupRepository, ILogGroupRepository
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hold { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();

        async Task<List<LogGroup>> ILogGroupRepository.GetAllAsync()
        {
            if (Hold)
            {
                Started.TrySetResult();
                await _release.Task;
                if (Failure != null)
                    throw Failure;
            }
            return await GetAllAsync();
        }
    }
}
