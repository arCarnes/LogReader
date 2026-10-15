namespace LogReader.Tests;

using System.IO;
using LogReader.App.ViewModels;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberReopen_OpensOnlyRequestedFileAndCoalescesClicks(bool restoreFilter)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var (fixture, group, member) = await PrepareMemberReopenAsync(restoreFilter);
            await using var owned = fixture;
            var vm = fixture.ViewModel;
            var revision = group.LoadProgress.Revision;
            var first = vm.OpenDashboardMemberFileAsync(group, member);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var repeatedMember = new GroupFileMemberViewModel(member.FileId, member.FileName,
                member.FilePath.ToUpperInvariant(), showFullPath: false);
            var repeated = vm.OpenDashboardMemberFileAsync(group, repeatedMember);
            Assert.Equal(2, vm.Tabs.Count);
            Assert.False(vm.FindTabInScope(member.FilePath, group.Id)!.IsIndexReady);
            Assert.Equal(2, fixture.Reader.BuildCounts[fixture.LargePath]);
            fixture.Reader.Release();
            await Task.WhenAll(first, repeated).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, vm.Tabs.Count);
            Assert.DoesNotContain(vm.Tabs, tab => tab.FilePath.EndsWith("missing.log"));
            var reopened = Assert.Single(vm.Tabs, tab => tab.FilePath == fixture.LargePath);
            Assert.Same(reopened, vm.SelectedTab);
            Assert.True(reopened.IsIndexReady);
            Assert.Equal(restoreFilter, reopened.IsFilterActive);
            if (restoreFilter)
                Assert.Equal(new[] { 1 }, reopened.CaptureActiveFilterSnapshot()!.MatchingLineNumbers);
            Assert.Equal(revision, group.LoadProgress.Revision);
            Assert.Equal(2, fixture.Reader.BuildCounts[fixture.LargePath]);
        });
    }

    [Fact]
    public async Task MemberReopen_BackgroundCompletionKeepsTheNewScopeSelection()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var (fixture, group, member) = await PrepareMemberReopenAsync();
            await using var owned = fixture;
            var vm = fixture.ViewModel;
            var open = vm.OpenDashboardMemberFileAsync(group, member);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            var selected = vm.SelectedTab;
            fixture.Reader.Release();
            await open.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(vm.Groups[1].Id, vm.ActiveDashboardId);
            Assert.Same(selected, vm.SelectedTab);
            Assert.Single(vm.Tabs, tab => tab.FilePath == fixture.LargePath && tab.ScopeDashboardId == group.Id);
            Assert.DoesNotContain(vm.Tabs, tab => tab.FilePath.EndsWith("missing.log"));
        });
    }

    [Theory]
    [InlineData("unload")]
    [InlineData("reload")]
    [InlineData("shutdown")]
    public async Task MemberReopen_CancelsAndDrainsWithDashboardLifetime(string action)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var (fixture, group, member) = await PrepareMemberReopenAsync();
            await using var owned = fixture;
            var vm = fixture.ViewModel;
            var oldRevision = group.LoadProgress.Revision;
            var open = vm.OpenDashboardMemberFileAsync(group, member);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (action == "reload")
            {
                fixture.Reader.ResetGate();
                var reload = vm.ReloadDashboardAsync(group);
                await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(3, fixture.Reader.BuildCounts[fixture.LargePath]);
                fixture.Reader.Release();
                await Task.WhenAll(open, reload).WaitAsync(TimeSpan.FromSeconds(5));
                var tab = Assert.Single(vm.Tabs, tab => tab.FilePath == fixture.LargePath);
                Assert.True(tab.IsIndexReady);
                Assert.True(vm.Groups[0].LoadProgress.Revision > oldRevision);
            }
            else
            {
                if (action == "unload")
                    await vm.UnloadDashboardAsync(group).WaitAsync(TimeSpan.FromSeconds(5));
                else
                {
                    vm.Dispose();
                    await vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                await open.WaitAsync(TimeSpan.FromSeconds(5));
                await fixture.Reader.BlockedBuildDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.DoesNotContain(vm.Tabs, tab => tab.FilePath == fixture.LargePath);
                if (action == "unload")
                    Assert.DoesNotContain(vm.Tabs, tab => tab.ScopeDashboardId == group.Id);
            }
        });
    }

    [Fact]
    public async Task DashboardCycle_JoinsAnExistingMemberReopenWithoutDuplicateTabsOrIndexing()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var (fixture, group, member) = await PrepareMemberReopenAsync();
            await using var owned = fixture;
            var vm = fixture.ViewModel;
            var open = vm.OpenDashboardMemberFileAsync(group, member);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var laterPath = Path.Combine(_root, "missing.log");
            await File.WriteAllTextAsync(laterPath, "now available");
            var cycle = vm.HandleDashboardGroupInvokedAsync(group);
            Assert.False(cycle.IsCompleted);
            Assert.Equal(2, fixture.Reader.BuildCounts[fixture.LargePath]);
            var later = vm.FindTabInScope(laterPath, group.Id);
            Assert.NotNull(later);
            await WaitForIndexReadyAsync(later);
            Assert.False(open.IsCompleted);
            Assert.False(cycle.IsCompleted);
            fixture.Reader.Release();
            await Task.WhenAll(open, cycle).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(vm.Tabs, tab => tab.FilePath == fixture.LargePath);
            Assert.Equal(2, fixture.Reader.BuildCounts[fixture.LargePath]);
            Assert.Equal(3, vm.Tabs.Count);
        });
    }

    [Fact]
    public async Task MemberScopeEntry_LoadsTheWholeDashboardAndSelectsTheRequestedPlaceholder()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(includeSmallInFirstDashboard: true);
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            var member = group.MemberFiles.Single(member => member.FilePath == fixture.LargePath);
            await vm.OpenDashboardMemberFileAsync(group, member).WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(group.Id, vm.ActiveDashboardId);
            Assert.Equal(2, vm.Tabs.Count);
            Assert.Equal(fixture.LargePath, vm.SelectedTab!.FilePath);
            Assert.False(vm.SelectedTab.IsIndexReady);
            fixture.Reader.Release();
            await vm.OpenGroupFilesAsync(group).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
            Assert.All(vm.Tabs, tab => Assert.True(tab.IsIndexReady));
        });
    }

    private async Task<(Fixture Fixture, LogGroupViewModel Group, GroupFileMemberViewModel Member)> PrepareMemberReopenAsync(
        bool restoreFilter = false)
    {
        var fixture = await CreateAsync(includeMissing: true, includeSmallInFirstDashboard: true);
        try
        {
            var vm = fixture.ViewModel;
            fixture.Reader.Release();
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            var group = vm.Groups[0];
            var tab = vm.Tabs.Single(tab => tab.FilePath == fixture.LargePath);
            if (restoreFilter)
                await tab.ApplyFilterAsync([1], "One filtered line");
            var session = tab.ActiveSession;
            await vm.CloseTabCommand.ExecuteAsync(tab);
            await vm.CloseTabCommand.ExecuteAsync(vm.Tabs.Single(tab => tab.HasLoadError));
            await session.ResetLineIndexAsync();
            fixture.Reader.ResetGate();
            Assert.Equal(group.Id, vm.ActiveDashboardId);
            Assert.Single(vm.Tabs);
            return (fixture, group, group.MemberFiles.Single(member => member.FilePath == fixture.LargePath));
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }
}
