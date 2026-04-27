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
    public async Task OpenGroupFilesAsync_OneUncHost_IsBoundedByHostAndShareLimits()
    {
        var targets = Enumerable.Range(1, 5)
            .Select(index => UncPath("server", "share", $"file{index}.log"))
            .ToArray();
        var host = new RecordingDashboardWorkspaceHost(prepareDelay: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateCoordinator(host, targets);

        await coordinator.OpenGroupFilesAsync(CreateGroup(), modifierLabel: null);

        Assert.Equal(targets, host.FinalizedPaths.ToArray());
        Assert.True(host.MaxActivePrepareCount <= 2);
        Assert.Equal(2, host.MaxActivePrepareCount);
        Assert.All(host.MaxActivePrepareCountByHost.Values, maxActive => Assert.True(maxActive <= 2));
    }

    [Fact]
    public async Task OpenGroupFilesAsync_MultipleUncHosts_CanUseMoreThanHistoricalEightWorkers()
    {
        var targets = Enumerable.Range(1, 5)
            .SelectMany(hostIndex => new[]
            {
                UncPath($"server{hostIndex}", "share", $"a{hostIndex}.log"),
                UncPath($"server{hostIndex}", "share", $"b{hostIndex}.log")
            })
            .ToArray();
        var host = new RecordingDashboardWorkspaceHost(prepareDelay: TimeSpan.FromMilliseconds(75));
        var coordinator = CreateCoordinator(host, targets);

        await coordinator.OpenGroupFilesAsync(CreateGroup("dashboard-1", "Operations"), modifierLabel: null);

        Assert.Equal(targets, host.FinalizedPaths.ToArray());
        Assert.True(host.MaxActivePrepareCount > 8);
        Assert.All(host.MaxActivePrepareCountByHost.Values, maxActive => Assert.True(maxActive <= 2));
        Assert.Contains(
            host.StatusHistory,
            status => status.Contains("Loading \"Operations\" with 10 workers across 5 hosts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenGroupFilesAsync_ClusteredUncHosts_StartsLaterHostBeforeFirstClusterDrains()
    {
        var targets = Enumerable.Range(1, 6)
            .Select(index => UncPath("server-a", "share", $"a{index}.log"))
            .Concat(Enumerable.Range(1, 2)
                .Select(index => UncPath("server-b", "share", $"b{index}.log")))
            .ToArray();
        var host = new RecordingDashboardWorkspaceHost(prepareDelay: TimeSpan.FromMilliseconds(75));
        var coordinator = CreateCoordinator(host, targets);

        await coordinator.OpenGroupFilesAsync(CreateGroup(), modifierLabel: null);

        var prepareStartHosts = host.PrepareStartHosts.ToArray();
        var firstServerBStart = Array.FindIndex(
            prepareStartHosts,
            prepareHost => string.Equals(prepareHost, "server-b", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(targets, host.FinalizedPaths.ToArray());
        Assert.InRange(firstServerBStart, 0, 5);
        Assert.All(host.MaxActivePrepareCountByHost.Values, maxActive => Assert.True(maxActive <= 2));
    }

    [Fact]
    public async Task OpenGroupFilesAsync_DuplicateTargets_AreSuppressedBeforePrepare()
    {
        var target = UncPath("server", "share", "duplicate.log");
        var targets = new[] { target, target.ToUpperInvariant() };
        var host = new RecordingDashboardWorkspaceHost();
        var coordinator = CreateCoordinator(host, targets);

        await coordinator.OpenGroupFilesAsync(CreateGroup(), modifierLabel: null);

        Assert.Equal(1, host.PrepareCallCount);
        Assert.Equal(new[] { target }, host.FinalizedPaths.ToArray());
    }

    private static DashboardOpenCoordinator CreateCoordinator(
        RecordingDashboardWorkspaceHost host,
        IReadOnlyList<string> targets)
        => new(
            host,
            _ => Task.FromResult(targets),
            (_, _) => Task.FromResult(true));

    private static LogGroupViewModel CreateGroup(
        string id = "dashboard-1",
        string name = "Dashboard")
        => new(
            new LogGroup
            {
                Id = id,
                Name = name,
                Kind = LogGroupKind.Dashboard
            },
            _ => Task.CompletedTask);

    private static string UncPath(string host, string share, string fileName)
        => $@"\\{host}\{share}\{fileName}";

    private sealed class RecordingDashboardWorkspaceHost : IDashboardWorkspaceHost
    {
        private readonly TimeSpan _prepareDelay;
        private readonly object _sync = new();
        private readonly Dictionary<string, int> _activePrepareCountByHost = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _maxActivePrepareCountByHost = new(StringComparer.OrdinalIgnoreCase);
        private int _activePrepareCount;
        private int _maxActivePrepareCount;

        public RecordingDashboardWorkspaceHost(TimeSpan? prepareDelay = null)
        {
            _prepareDelay = prepareDelay ?? TimeSpan.Zero;
        }

        public ObservableCollection<LogGroupViewModel> Groups { get; } = new();

        public ObservableCollection<LogTabViewModel> Tabs { get; } = new();

        public LogTabViewModel? SelectedTab { get; set; }

        public bool ShowFullPathsInDashboard => false;

        public string? ActiveDashboardId { get; set; }

        public string DashboardTreeFilter => string.Empty;

        public bool IsDashboardLoading { get; set; }

        public string DashboardLoadingStatusText
        {
            get => StatusHistory.LastOrDefault() ?? string.Empty;
            set => StatusHistory.Add(value);
        }

        public int DashboardLoadDepth { get; set; }

        public List<string> StatusHistory { get; } = new();

        public ConcurrentQueue<string> FinalizedPaths { get; } = new();

        public ConcurrentQueue<string> PrepareStartHosts { get; } = new();

        public IReadOnlyDictionary<string, int> MaxActivePrepareCountByHost => _maxActivePrepareCountByHost;

        public int MaxActivePrepareCount => Volatile.Read(ref _maxActivePrepareCount);

        public int PrepareCallCount { get; private set; }

        public void NotifyFilteredTabsChanged()
        {
        }

        public void NotifyScopeMetadataChanged()
        {
        }

        public void EnsureSelectedTabInCurrentScope()
        {
        }

        public void ExitDashboardScopeIfCurrentDashboardFinishedEmpty(string dashboardId)
        {
        }

        public void BeginTabCollectionNotificationSuppression()
        {
        }

        public void EndTabCollectionNotificationSuppression()
        {
        }

        public Task OpenFilePathInScopeAsync(
            string filePath,
            string? scopeDashboardId,
            bool reloadIfLoadError = false,
            bool activateTab = true,
            bool deferVisibilityRefresh = false,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public async Task<TabWorkspaceService.PreparedTabOpen?> PrepareDashboardFileOpenAsync(
            string filePath,
            string scopeDashboardId,
            CancellationToken ct = default)
        {
            PrepareCallCount++;
            var host = GetUncHost(filePath);
            PrepareStartHosts.Enqueue(host);
            IncrementPrepareCount(host);
            try
            {
                if (_prepareDelay > TimeSpan.Zero)
                    await Task.Delay(_prepareDelay, ct);

                return CreatePreparedTab(filePath, scopeDashboardId);
            }
            finally
            {
                DecrementPrepareCount(host);
            }
        }

        public Task FinalizeDashboardFileOpenAsync(
            TabWorkspaceService.PreparedTabOpen preparedTab,
            CancellationToken ct = default)
        {
            Tabs.Add(preparedTab.Tab);
            FinalizedPaths.Enqueue(preparedTab.FilePath);
            preparedTab.MarkCommitted();
            return Task.CompletedTask;
        }

        public LogTabViewModel? FindTabInScope(string filePath, string? scopeDashboardId)
            => Tabs.FirstOrDefault(tab =>
                string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal));

        private void IncrementPrepareCount(string host)
        {
            var active = Interlocked.Increment(ref _activePrepareCount);
            UpdateMaxObserved(ref _maxActivePrepareCount, active);
            lock (_sync)
            {
                _activePrepareCountByHost.TryGetValue(host, out var activeForHost);
                activeForHost++;
                _activePrepareCountByHost[host] = activeForHost;
                _maxActivePrepareCountByHost.TryGetValue(host, out var maxForHost);
                if (activeForHost > maxForHost)
                    _maxActivePrepareCountByHost[host] = activeForHost;
            }
        }

        private void DecrementPrepareCount(string host)
        {
            Interlocked.Decrement(ref _activePrepareCount);
            lock (_sync)
            {
                _activePrepareCountByHost[host]--;
            }
        }

        private static void UpdateMaxObserved(ref int maxObserved, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref maxObserved);
                if (value <= current)
                    return;

                if (Interlocked.CompareExchange(ref maxObserved, value, current) == current)
                    return;
            }
        }

        private static TabWorkspaceService.PreparedTabOpen CreatePreparedTab(
            string filePath,
            string scopeDashboardId)
        {
            var tab = new LogTabViewModel(
                "file-" + Guid.NewGuid().ToString("N"),
                filePath,
                new StubLogReaderService(),
                new StubFileTailService(),
                new StubEncodingDetectionService(),
                new AppSettings(),
                skipInitialEncodingResolution: true,
                sessionRegistry: null,
                initialEncoding: FileEncoding.Auto,
                scopeDashboardId: scopeDashboardId);
            return new TabWorkspaceService.PreparedTabOpen(
                tab,
                filePath,
                scopeDashboardId,
                shouldStartPinned: false,
                shouldClearRecentState: false);
        }

        private static string GetUncHost(string filePath)
        {
            var trimmed = filePath.TrimStart('\\');
            var separator = trimmed.IndexOf('\\', StringComparison.Ordinal);
            return separator < 0 ? trimmed : trimmed[..separator];
        }
    }
}
