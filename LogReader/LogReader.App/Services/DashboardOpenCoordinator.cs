namespace LogReader.App.Services;

using LogReader.App.ViewModels;
using LogReader.Core;

internal sealed class DashboardOpenCoordinator
{
    private const int ProgressPublicationIntervalMs = 150;
    private const int ProgressPublicationCountThreshold = 16;

    private readonly IDashboardWorkspaceHost _host;
    private readonly Func<LogGroupViewModel, Task<IReadOnlyList<string>>> _resolveOpenTargetsAsync;
    private readonly Func<string, CancellationToken, Task<DashboardFileProbeResult>> _probeFileAsync;
    private readonly object _dashboardLoadGate = new();
    private DashboardLoadSession? _dashboardLoadSession;

    public DashboardOpenCoordinator(
        IDashboardWorkspaceHost host,
        Func<LogGroupViewModel, Task<IReadOnlyList<string>>> resolveOpenTargetsAsync,
        Func<string, CancellationToken, Task<DashboardFileProbeResult>>? probeFileAsync = null)
    {
        _host = host;
        _resolveOpenTargetsAsync = resolveOpenTargetsAsync;
        _probeFileAsync = probeFileAsync ?? DashboardFileProbe.ProbeOffUiAsync;
    }

    public void CancelDashboardLoad()
    {
        lock (_dashboardLoadGate)
            _dashboardLoadSession?.CancellationTokenSource.Cancel();
    }

    public void LeaveActiveDashboardScope()
    {
        CancelDashboardLoad();
        _host.ActiveDashboardId = null;
        foreach (var group in _host.Groups)
            group.IsSelected = false;
    }

    public DashboardLoadLease BeginDashboardLoadLease(string dashboardId)
        => AcquireLease(BeginDashboardLoad(dashboardId));

    public bool IsCurrentDashboardLoad(DashboardLoadLease dashboardLoadLease, string dashboardId)
    {
        ArgumentNullException.ThrowIfNull(dashboardLoadLease);
        return dashboardLoadLease.IsCurrentDashboardLoad(dashboardId);
    }

    public async Task OpenGroupFilesAsync(LogGroupViewModel group, string? modifierLabel)
    {
        using var dashboardLoadLease = BeginDashboardLoadLease(group.Id);
        await OpenGroupFilesAsync(group, modifierLabel, dashboardLoadLease);
    }

    public async Task OpenGroupFilesAsync(
        LogGroupViewModel group,
        string? modifierLabel,
        DashboardLoadLease dashboardLoadLease)
    {
        ArgumentNullException.ThrowIfNull(dashboardLoadLease);
        if (!dashboardLoadLease.IsCurrentDashboardLoad(group.Id))
            return;

        using var operationLease = AcquireLease(dashboardLoadLease.Session);
        dashboardLoadLease.Session.LoadTask = RunDashboardLoadAsync(group, modifierLabel, dashboardLoadLease.Session);
        await dashboardLoadLease.Session.LoadTask;
    }

    private async Task RunDashboardLoadAsync(
        LogGroupViewModel group,
        string? modifierLabel,
        DashboardLoadSession dashboardLoadSession)
    {
        var dashboardLoadCts = dashboardLoadSession.CancellationTokenSource;
        var ct = dashboardLoadCts.Token;
        _host.BeginTabCollectionNotificationSuppression();

        var canceled = false;
        var completed = false;
        var results = Array.Empty<TargetOpenResult?>();
        try
        {
            var scopeDisplayName = string.IsNullOrWhiteSpace(modifierLabel)
                ? group.Name
                : $"{group.Name} [{modifierLabel}]";
            var targets = await _resolveOpenTargetsAsync(group);
            var parallelismPlan = AdaptiveParallelismPolicy.CreatePlan(
                AdaptiveParallelismOperation.DashboardLoad,
                targets);
            AdaptiveParallelismDiagnostics.WritePlan(parallelismPlan);

            SetDashboardLoadingStatus(
                dashboardLoadSession,
                targets.Count == 0
                    ? $"Loading \"{scopeDisplayName}\"..."
                    : BuildLoadingStatus(parallelismPlan, processedCount: 0, loadedCount: 0),
                processedCount: 0,
                force: true);

            await Task.Yield();

            results = new TargetOpenResult?[targets.Count];
            var loadedCount = 0;
            var processedCount = 0;
            var nextCommitIndex = 0;
            var claimedPaths = new HashSet<string>(
                _host.Tabs
                    .Where(tab => string.Equals(tab.ScopeDashboardId, group.Id, StringComparison.Ordinal))
                    .Select(tab => tab.FilePath),
                StringComparer.OrdinalIgnoreCase);
            var claimGate = new object();
            var commitGate = new SemaphoreSlim(1, 1);
            using var adaptiveGates = AdaptiveParallelismGateSet.Create(parallelismPlan);
            var workOrder = AdaptiveParallelismScheduler.BuildInterleavedWorkOrder(parallelismPlan);
            var nextIndex = -1;
            var workerCount = Math.Min(parallelismPlan.GlobalLimit, targets.Count);
            var workers = Enumerable.Range(0, workerCount)
                .Select(_ => RunWorkerAsync())
                .ToArray();
            await Task.WhenAll(workers);
            await CommitReadyResultsAsync();

            SetDashboardLoadingStatus(dashboardLoadSession, $"Loaded \"{scopeDisplayName}\" ({loadedCount}/{targets.Count} opened).");
            completed = true;

            async Task RunWorkerAsync()
            {
                while (true)
                {
                    var workOrderIndex = Interlocked.Increment(ref nextIndex);
                    if (workOrderIndex >= workOrder.Count)
                        return;

                    var index = workOrder[workOrderIndex];
                    var result = await ProcessTargetAsync(index);
                    results[index] = result;
                    await CommitReadyResultsAsync();
                }
            }

            async Task<TargetOpenResult> ProcessTargetAsync(int index)
            {
                const int maxOpenAttempts = 3;
                TabWorkspaceService.PreparedTabOpen? preparedTab = null;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                    ct.ThrowIfCancellationRequested();
                    var filePath = targets[index];
                    if (ShouldSkipTarget(filePath))
                        return ReportResult(filePath, opened: false, preparedTab: null);

                    using (await adaptiveGates.AcquireAsync(parallelismPlan.Targets[index], ct))
                    {
                        var fileStatus = await _probeFileAsync(filePath, ct);
                        ct.ThrowIfCancellationRequested();
                        if (!fileStatus.IsFound)
                            return ReportResult(filePath, opened: false, preparedTab: null);

                        for (var attempt = 1; attempt <= maxOpenAttempts; attempt++)
                        {
                            ct.ThrowIfCancellationRequested();
                            preparedTab = await _host.PrepareDashboardFileOpenAsync(filePath, group.Id, ct);
                            ct.ThrowIfCancellationRequested();
                            if (preparedTab != null && !preparedTab.Tab.HasLoadError)
                                return ReportResult(filePath, opened: true, preparedTab);

                            preparedTab?.Dispose();
                            preparedTab = null;
                            if (attempt < maxOpenAttempts)
                                await Task.Delay(400, ct);
                        }
                    }

                    return ReportResult(filePath, opened: false, preparedTab: null);
                }
                catch
                {
                    preparedTab?.Dispose();
                    throw;
                }
            }

            TargetOpenResult ReportResult(string filePath, bool opened, TabWorkspaceService.PreparedTabOpen? preparedTab)
            {
                if (opened)
                    Interlocked.Increment(ref loadedCount);

                var processed = Interlocked.Increment(ref processedCount);
                SetDashboardLoadingStatus(
                    dashboardLoadSession,
                    BuildLoadingStatus(
                        parallelismPlan,
                        processed,
                        Volatile.Read(ref loadedCount)),
                    processed,
                    force: false);
                return new TargetOpenResult(filePath, opened, preparedTab);
            }

            bool ShouldSkipTarget(string filePath)
            {
                lock (claimGate)
                {
                    return !claimedPaths.Add(filePath);
                }
            }

            async Task CommitReadyResultsAsync()
            {
                await commitGate.WaitAsync(ct);
                try
                {
                    while (nextCommitIndex < results.Length)
                    {
                        ct.ThrowIfCancellationRequested();
                        var readyResult = results[nextCommitIndex];
                        if (readyResult == null)
                            break;

                        if (readyResult.Opened && readyResult.PreparedTab != null)
                            await _host.FinalizeDashboardFileOpenAsync(readyResult.PreparedTab, ct);

                        nextCommitIndex++;
                    }
                }
                finally
                {
                    commitGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        finally
        {
            foreach (var result in results)
                result?.PreparedTab?.Dispose();

            await _host.EndTabCollectionNotificationSuppressionAsync();
            _host.EnsureSelectedTabInCurrentScope();

            if (completed &&
                !canceled &&
                IsCurrentDashboardLoad(dashboardLoadSession))
            {
                _host.ExitDashboardScopeIfCurrentDashboardFinishedEmpty(group.Id);
            }

            if (canceled)
                SetDashboardLoadingStatus(
                    dashboardLoadSession,
                    string.Empty,
                    processedCount: null,
                    force: true,
                    allowCanceled: true);
        }
    }

    private DashboardLoadSession BeginDashboardLoad(string dashboardId)
    {
        var next = new DashboardLoadSession(dashboardId);
        lock (_dashboardLoadGate)
        {
            var previous = _dashboardLoadSession;
            _dashboardLoadSession = next;
            previous?.CancellationTokenSource.Cancel();
        }

        return next;
    }

    private DashboardLoadLease AcquireLease(DashboardLoadSession dashboardLoadSession)
    {
        dashboardLoadSession.ActiveLeaseCount++;
        _host.DashboardLoadDepth++;
        _host.IsDashboardLoading = true;
        return new DashboardLoadLease(this, dashboardLoadSession);
    }

    private void ReleaseLease(DashboardLoadSession dashboardLoadSession)
    {
        dashboardLoadSession.ActiveLeaseCount = Math.Max(0, dashboardLoadSession.ActiveLeaseCount - 1);
        _host.DashboardLoadDepth = Math.Max(0, _host.DashboardLoadDepth - 1);
        if (_host.DashboardLoadDepth == 0)
            _host.IsDashboardLoading = false;

        if (dashboardLoadSession.ActiveLeaseCount != 0)
            return;

        lock (_dashboardLoadGate)
        {
            if (ReferenceEquals(_dashboardLoadSession, dashboardLoadSession))
                _dashboardLoadSession = null;
        }

        dashboardLoadSession.CancellationTokenSource.Dispose();
    }

    private bool IsCurrentDashboardLoad(DashboardLoadSession dashboardLoadSession)
    {
        lock (_dashboardLoadGate)
            return ReferenceEquals(_dashboardLoadSession, dashboardLoadSession);
    }

    private void SetDashboardLoadingStatus(
        DashboardLoadSession dashboardLoadSession,
        string statusText,
        int? processedCount = null,
        bool force = true,
        bool allowCanceled = false)
    {
        lock (_dashboardLoadGate)
        {
            if (!ReferenceEquals(_dashboardLoadSession, dashboardLoadSession) ||
                (!allowCanceled && dashboardLoadSession.CancellationTokenSource.IsCancellationRequested))
            {
                return;
            }

            if (!force && processedCount.HasValue)
            {
                var elapsedMs = Environment.TickCount64 - dashboardLoadSession.LastProgressPublicationTick;
                var processedSinceLastPublication =
                    processedCount.Value - dashboardLoadSession.LastPublishedProcessedCount;
                if (elapsedMs < ProgressPublicationIntervalMs &&
                    processedSinceLastPublication < ProgressPublicationCountThreshold)
                {
                    return;
                }
            }

            _host.DashboardLoadingStatusText = statusText;
            if (processedCount.HasValue)
            {
                dashboardLoadSession.LastPublishedProcessedCount = processedCount.Value;
                dashboardLoadSession.LastProgressPublicationTick = Environment.TickCount64;
            }
        }
    }

    private static string BuildLoadingStatus(
        ParallelismPlan parallelismPlan,
        int processedCount,
        int loadedCount)
    {
        var workerCount = Math.Min(parallelismPlan.GlobalLimit, parallelismPlan.TargetCount);
        return $"({processedCount}/{parallelismPlan.TargetCount}) opened: {loadedCount} workers: {workerCount}";
    }

    internal sealed class DashboardLoadSession
    {
        public DashboardLoadSession(string dashboardId)
        {
            DashboardId = dashboardId;
            CancellationTokenSource = new CancellationTokenSource();
        }

        public string DashboardId { get; }

        public CancellationTokenSource CancellationTokenSource { get; }

        public int ActiveLeaseCount { get; set; }

        public Task? LoadTask { get; set; }

        public int LastPublishedProcessedCount { get; set; }

        public long LastProgressPublicationTick { get; set; }
    }

    internal sealed class DashboardLoadLease : IDisposable
    {
        private readonly DashboardOpenCoordinator _owner;
        private bool _disposed;

        internal DashboardLoadLease(DashboardOpenCoordinator owner, DashboardLoadSession session)
        {
            _owner = owner;
            Session = session;
        }

        internal DashboardLoadSession Session { get; }

        public bool IsCurrentDashboardLoad(string dashboardId)
        {
            return !_disposed &&
                   !Session.CancellationTokenSource.IsCancellationRequested &&
                   string.Equals(Session.DashboardId, dashboardId, StringComparison.Ordinal) &&
                   _owner.IsCurrentDashboardLoad(Session);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner.ReleaseLease(Session);
        }
    }

    private sealed record TargetOpenResult(
        string FilePath,
        bool Opened,
        TabWorkspaceService.PreparedTabOpen? PreparedTab);

}
