namespace LogReader.Tests;

using System.IO;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Fact]
    public async Task BulkScopeResolutionOverlap_JoinsAnInFlightPlaceholderCreation()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var files = new GatedBulkScopeRepository();
            await using var fixture = await CreateAsync(fileRepository: files);
            var vm = fixture.ViewModel;
            files.Hold = true;
            var bulk = vm.OpenFilePathsAsync([fixture.LargePath]);
            Task? cycle = null;
            try
            {
                await files.ScopeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cycle = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
                await files.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                files.ReleaseScope.TrySetResult();
                await WpfTestHost.FlushAsync();
                files.ReleaseOpen.TrySetResult();
                await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                fixture.Reader.Release();
                await Task.WhenAll(bulk, cycle).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(Assert.Single(vm.Tabs).IsIndexReady);
                Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
            }
            finally
            {
                files.ReleaseScope.TrySetResult();
                files.ReleaseOpen.TrySetResult();
                fixture.Reader.Release();
                await Task.WhenAll(bulk, cycle ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task DashboardCycleFirst_StandaloneOpenReusesItsPlaceholder()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            var cycle = vm.HandleDashboardGroupInvokedAsync(group);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tab = Assert.Single(vm.Tabs);
            await vm.OpenFilePathInScopeAsync(fixture.LargePath.ToUpperInvariant(), group.Id, activateTab: false);
            Assert.Same(tab, Assert.Single(vm.Tabs));
            fixture.Reader.Release();
            await cycle.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
            Assert.True(tab.IsIndexReady);
        });
    }

    [Fact]
    public async Task ScopedOpen_CallerCancellationPreservesTheJoinedDashboard()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            using var cancellation = new CancellationTokenSource();
            var open = vm.OpenFilePathInScopeAsync(fixture.LargePath, group.Id, activateTab: false, ct: cancellation.Token);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cycle = vm.HandleDashboardGroupInvokedAsync(group);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
            Assert.False(cycle.IsCompleted);
            fixture.Reader.Release();
            await cycle.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Assert.Single(vm.Tabs).IsIndexReady);
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
        });
    }

    [Fact]
    public async Task ScopedOpen_FailureReleasesAdmissionForReload()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(includeMissing: true, allMissing: true);
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            var path = group.MemberFiles.Single().FilePath;
            await vm.OpenFilePathInScopeAsync(path, group.Id, activateTab: false).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Assert.Single(vm.Tabs).HasLoadError);
            await vm.UnloadDashboardAsync(group).WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(path, "available");
            await vm.OpenFilePathInScopeAsync(path, group.Id, activateTab: false).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Assert.Single(vm.Tabs).IsIndexReady);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkDashboardOverlap_PublishesOneTabAndPreservesNewerNavigation(bool uppercase)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            var path = uppercase ? fixture.LargePath.ToUpperInvariant() : fixture.LargePath;
            var bulk = vm.OpenFilePathsAsync([path]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cycle = vm.HandleDashboardGroupInvokedAsync(group);
            var tab = Assert.Single(vm.Tabs, tab => tab.ScopeDashboardId == group.Id);
            Assert.False(tab.IsIndexReady);
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            var selected = vm.SelectedTab;
            fixture.Reader.Release();
            await Task.WhenAll(bulk, cycle).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(tab, Assert.Single(vm.Tabs, tab => tab.ScopeDashboardId == group.Id));
            Assert.True(tab.IsIndexReady);
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
            Assert.Equal(vm.Groups[1].Id, vm.ActiveDashboardId);
            Assert.Same(selected, vm.SelectedTab);
            Assert.Equal(0, vm.DashboardLoadDepth);
        });
    }

    [Theory]
    [InlineData("unload")]
    [InlineData("reload")]
    [InlineData("shutdown")]
    public async Task BulkDashboardOverlap_DrainsWithTheDashboardLifetime(string action)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            var bulk = vm.OpenFilePathsAsync([fixture.LargePath]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (action == "shutdown")
            {
                vm.Dispose();
                await vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else if (action == "reload")
            {
                fixture.Reader.ResetGate();
                var reload = vm.ReloadDashboardAsync(group);
                await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                fixture.Reader.Release();
                await reload.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
                await vm.UnloadDashboardAsync(group).WaitAsync(TimeSpan.FromSeconds(5));

            fixture.Reader.Release();
            await bulk.WaitAsync(TimeSpan.FromSeconds(5));
            if (action == "reload")
                Assert.True(Assert.Single(vm.Tabs).IsIndexReady);
            else
                Assert.Empty(vm.Tabs);
            Assert.Equal(0, vm.DashboardLoadDepth);
        });
    }
    private sealed class GatedBulkScopeRepository : StubLogFileRepository, ILogFileRepository
    {
        public bool Hold { get; set; }
        public TaskCompletionSource ScopeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseScope { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IReadOnlyDictionary<string, LogFileEntry>> ILogFileRepository.GetByPathsAsync(IEnumerable<string> paths)
        {
            if (Hold)
            {
                ScopeStarted.TrySetResult();
                await ReleaseScope.Task;
            }
            return await GetByPathsAsync(paths);
        }

        async Task<LogFileEntry> ILogFileRepository.GetOrCreateByPathAsync(string path, DateTime? openedAt)
        {
            if (Hold)
            {
                OpenStarted.TrySetResult();
                await ReleaseOpen.Task;
            }
            return await GetOrCreateByPathAsync(path, openedAt);
        }
    }
}
