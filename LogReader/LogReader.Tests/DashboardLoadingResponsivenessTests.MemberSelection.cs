namespace LogReader.Tests;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task MemberScopeEntry_DuringResolutionPreservesTheLatestClick(int olderIndex, int newerIndex)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var files = new GatedTargetRepository();
            await using var fixture = await CreateAsync(includeSmallInFirstDashboard: true, fileRepository: files);
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            files.HeldFileId = group.MemberFiles[0].FileId;
            var olderClick = vm.OpenDashboardMemberFileAsync(group, group.MemberFiles[olderIndex]);
            try
            {
                await files.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var newerClick = vm.OpenDashboardMemberFileAsync(group, group.MemberFiles[newerIndex]);
                files.Release();
                await Task.WhenAll(olderClick, newerClick).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(group.MemberFiles[newerIndex].FilePath, vm.SelectedTab!.FilePath);
                fixture.Reader.Release();
                await vm.OpenGroupFilesAsync(group).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(group.MemberFiles[newerIndex].FilePath, vm.SelectedTab!.FilePath);
                Assert.Equal(2, vm.Tabs.Count);
            }
            finally { files.Release(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberScopeEntry_RestoresInclusiveRangeAnchor(bool alreadyLoaded)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateMemberSelectionFixtureAsync(alreadyLoaded);
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            Assert.Equal(3, group.MemberFiles.Count);
            var member = group.MemberFiles[0];
            vm.PrepareDashboardMemberNormalSelection(group, member);
            await vm.OpenDashboardMemberFileAsync(group, member).WaitAsync(TimeSpan.FromSeconds(5));
            if (!alreadyLoaded)
                await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Reader.Release();
            await vm.OpenGroupFilesAsync(group).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(group.Id, vm.ActiveDashboardId);
            Assert.Equal(member.FilePath, vm.SelectedTab!.FilePath);
            Assert.All(group.MemberFiles, row => Assert.False(row.IsBatchSelected));
            vm.ApplyDashboardMemberBatchSelection(group, group.MemberFiles[2], isShiftSelection: true, isToggleSelection: false);
            Assert.Equal(new[] { true, true, true }, group.MemberFiles.Select(row => row.IsBatchSelected));
        });
    }

    [Fact]
    public async Task MemberScopeEntry_LoadCompletionPreservesNewerRangeAnchorAndSelectedTab()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateMemberSelectionFixtureAsync(alreadyLoaded: false);
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            vm.PrepareDashboardMemberNormalSelection(group, group.MemberFiles[0]);
            await vm.OpenDashboardMemberFileAsync(group, group.MemberFiles[0]).WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var newerMember = group.MemberFiles[1];
            vm.PrepareDashboardMemberNormalSelection(group, newerMember);
            await vm.OpenDashboardMemberFileAsync(group, newerMember).WaitAsync(TimeSpan.FromSeconds(5));
            var selected = vm.SelectedTab;
            fixture.Reader.Release();
            await vm.OpenGroupFilesAsync(group).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(selected, vm.SelectedTab);
            Assert.Equal(newerMember.FilePath, vm.SelectedTab!.FilePath);
            vm.ApplyDashboardMemberBatchSelection(group, group.MemberFiles[2], isShiftSelection: true, isToggleSelection: false);
            Assert.Equal(new[] { false, true, true }, group.MemberFiles.Select(row => row.IsBatchSelected));
        });
    }

    [Fact]
    public async Task MemberReopen_CompletionPreservesANewerMemberSelection()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var (fixture, group, member) = await PrepareMemberReopenAsync();
            await using var owned = fixture;
            var vm = fixture.ViewModel;
            var olderClick = vm.OpenDashboardMemberFileAsync(group, member);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var newerMember = group.MemberFiles.Single(row => row.FilePath.EndsWith("small.log"));
            await vm.OpenDashboardMemberFileAsync(group, newerMember).WaitAsync(TimeSpan.FromSeconds(5));
            var selected = vm.SelectedTab;
            fixture.Reader.Release();
            await olderClick.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(newerMember.FilePath, vm.SelectedTab!.FilePath);
            Assert.Same(selected, vm.SelectedTab);
            Assert.True(vm.FindTabInScope(member.FilePath, group.Id)!.IsIndexReady);
        });
    }

    private async Task<Fixture> CreateMemberSelectionFixtureAsync(bool alreadyLoaded)
    {
        var fixture = await CreateAsync(includeMissing: true, includeSmallInFirstDashboard: true);
        try
        {
            if (alreadyLoaded)
            {
                fixture.Reader.Release();
                await fixture.ViewModel.HandleDashboardGroupInvokedAsync(fixture.ViewModel.Groups[0]);
            }
            await fixture.ViewModel.HandleDashboardGroupInvokedAsync(fixture.ViewModel.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }
}
