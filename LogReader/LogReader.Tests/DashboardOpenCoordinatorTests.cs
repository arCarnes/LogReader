using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Models;
using LogReader.Testing;

namespace LogReader.Tests;

public class DashboardOpenCoordinatorTests
{
    [Fact]
    public async Task CancelDashboardLoadAndWaitAsync_BlocksOverlappingInteractiveOpens()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var release = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var group = CreateGroup();
            var host = new RecordingDashboardWorkspaceHost();
            host.Groups.Add(group);
            var resolutions = 0;
            var coordinator = new DashboardOpenCoordinator(host, _ =>
            {
                resolutions++;
                return release.Task;
            });
            var load = coordinator.OpenGroupFilesAsync(group, modifierLabel: null);
            var drain = coordinator.CancelDashboardLoadAndWaitAsync(group.Id);
            var click = coordinator.OpenGroupFilesAsync(group, modifierLabel: null);
            var openedMember = false;
            var member = coordinator.OpenDashboardMemberAsync(group.Id, @"C:\fixture\member.log", _ =>
            {
                openedMember = true;
                return Task.CompletedTask;
            });
            try
            {
                Assert.Equal(1, resolutions);
                Assert.False(openedMember);
                Assert.False(drain.IsCompleted);
                release.TrySetResult(Array.Empty<string>());
                await Task.WhenAll(load, drain, click, member).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, resolutions);
                Assert.False(openedMember);
                await coordinator.OpenGroupFilesAsync(group, modifierLabel: null);
                Assert.Equal(2, resolutions);
            }
            finally { release.TrySetResult(Array.Empty<string>()); }
        });
    }

    [Fact]
    public async Task RunDashboardTransitionAsync_ReleasesAdmissionAfterFailureAndDrainsQueuedWork()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = new RecordingDashboardWorkspaceHost();
            var coordinator = new DashboardOpenCoordinator(host, _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));
            var error = new System.IO.IOException("Synthetic transition failure");
            var first = coordinator.RunDashboardTransitionAsync("dashboard", async () =>
            {
                await release.Task;
                throw error;
            });
            var queuedRan = false;
            var queued = coordinator.RunDashboardTransitionAsync("dashboard", () =>
            {
                queuedRan = true;
                return Task.CompletedTask;
            });
            var openedMember = false;
            Task OpenMember(CancellationToken _)
            {
                openedMember = true;
                return Task.CompletedTask;
            }
            var member = coordinator.OpenDashboardMemberAsync("dashboard", @"C:\fixture\member.log", OpenMember);
            var shutdown = coordinator.CancelAndDrainInteractiveLoadsAsync();
            try
            {
                Assert.False(queuedRan);
                Assert.False(openedMember);
                Assert.False(shutdown.IsCompleted);
                release.TrySetResult();
                Assert.Same(error, await Assert.ThrowsAsync<System.IO.IOException>(() => first));
                await Task.WhenAll(queued, member, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(queuedRan);
                Assert.False(openedMember);
                await coordinator.OpenDashboardMemberAsync("dashboard", @"C:\fixture\member.log", OpenMember);
                Assert.True(openedMember);
            }
            finally { release.TrySetResult(); }
        });
    }

    [Fact]
    public async Task OpenGroupFilesAsync_PublishesOrderedDistinctPlaceholdersAndExactTerminalProgress()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var paths = new[] { @"C:\fixture\a.log", @"C:\fixture\b.log", @"C:\FIXTURE\A.LOG" };
            var host = new RecordingDashboardWorkspaceHost();
            var group = CreateGroup();
            host.Groups.Add(group);
            host.ActiveDashboardId = group.Id;
            var coordinator = new DashboardOpenCoordinator(host, _ => Task.FromResult<IReadOnlyList<string>>(paths),
                (_, _) => Task.FromResult(DashboardFileProbeResult.Found));
            try
            {
                await coordinator.OpenGroupFilesAsync(group, null).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(paths.Take(2), host.Tabs.Select(tab => tab.FilePath));
                Assert.All(host.Tabs, tab => Assert.True(tab.IsIndexReady));
                Assert.Equal(2, group.LoadProgress.Total);
                Assert.Equal(2, group.LoadProgress.Ready);
                Assert.Equal(0, group.LoadProgress.Failed);
                Assert.False(group.IsDashboardLoading);
                Assert.Equal(0, host.DashboardLoadDepth);
            }
            finally { foreach (var tab in host.Tabs) tab.Dispose(); }
        });
    }

    [Fact]
    public async Task OpenGroupFilesAsync_InaccessibleTargetRetainsATruthfulFailureTab()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var host = new RecordingDashboardWorkspaceHost();
            var group = CreateGroup();
            host.Groups.Add(group);
            var coordinator = new DashboardOpenCoordinator(host,
                _ => Task.FromResult<IReadOnlyList<string>>([@"C:\fixture\denied.log"]),
                (_, _) => Task.FromResult(DashboardFileProbeResult.AccessDenied));
            try
            {
                await coordinator.OpenGroupFilesAsync(group, null).WaitAsync(TimeSpan.FromSeconds(5));
                var tab = Assert.Single(host.Tabs);
                Assert.True(tab.HasLoadError);
                Assert.False(tab.IsIndexReady);
                Assert.Equal(1, group.LoadProgress.Failed);
                Assert.False(host.IsDashboardLoading);
            }
            finally { foreach (var tab in host.Tabs) tab.Dispose(); }
        });
    }

    private static LogGroupViewModel CreateGroup()
        => new(new LogGroup { Id = "dashboard-1", Name = "Dashboard", Kind = LogGroupKind.Dashboard }, _ => Task.CompletedTask);

    private sealed class RecordingDashboardWorkspaceHost : IDashboardWorkspaceHost
    {
        public ObservableCollection<LogGroupViewModel> Groups { get; } = new();
        public ObservableCollection<LogTabViewModel> Tabs { get; } = new();
        public LogTabViewModel? SelectedTab { get; set; }
        public bool ShowFullPathsInDashboard => false;
        public string? ActiveDashboardId { get; set; }
        public string DashboardTreeFilter => string.Empty;
        public bool IsDashboardLoading { get; set; }
        public string DashboardLoadingStatusText { get; set; } = string.Empty;
        public int DashboardLoadDepth { get; set; }
        public void NotifyFilteredTabsChanged() { }
        public void NotifyScopeMetadataChanged() { }
        public void EnsureSelectedTabInCurrentScope() { }
        public void ExitDashboardScopeIfCurrentDashboardFinishedEmpty(string dashboardId) { }
        public void BeginTabCollectionNotificationSuppression() { }
        public Task EndTabCollectionNotificationSuppressionAsync() => Task.CompletedTask;
        public Task OpenFilePathInScopeAsync(string path, string? scopeDashboardId, bool reloadIfLoadError = false,
            bool activateTab = true, bool deferVisibilityRefresh = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TabWorkspaceService.PreparedTabOpen?> PrepareDashboardFileOpenAsync(string path, string scopeDashboardId,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task FinalizeDashboardFileOpenAsync(TabWorkspaceService.PreparedTabOpen prepared, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<TabWorkspaceService.PreparedTabOpen?> CreateDashboardPlaceholderAsync(string path, string dashboardId, CancellationToken ct)
        {
            var tab = new LogTabViewModel("file-" + Guid.NewGuid().ToString("N"), path, new StubLogReaderService(),
                new StubFileTailService(), new StubEncodingDetectionService(), new AppSettings(),
                skipInitialEncodingResolution: true, sessionRegistry: null, initialEncoding: FileEncoding.Auto, scopeDashboardId: dashboardId);
            tab.SetDashboardContentRestoring(true);
            Tabs.Add(tab);
            var prepared = new TabWorkspaceService.PreparedTabOpen(tab, path, dashboardId, false, false);
            prepared.MarkCommitted();
            return Task.FromResult<TabWorkspaceService.PreparedTabOpen?>(prepared);
        }
        public Task CompleteDashboardPlaceholderAsync(TabWorkspaceService.PreparedTabOpen prepared, CancellationToken ct)
        {
            prepared.Tab.SetDashboardContentRestoring(false);
            return Task.CompletedTask;
        }
        public LogTabViewModel? FindTabInScope(string path, string? dashboardId)
            => Tabs.FirstOrDefault(tab => string.Equals(tab.FilePath, path, StringComparison.OrdinalIgnoreCase) && tab.ScopeDashboardId == dashboardId);
    }
}
