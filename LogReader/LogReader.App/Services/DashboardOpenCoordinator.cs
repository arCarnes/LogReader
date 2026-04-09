namespace LogReader.App.Services;

using System.IO;
using LogReader.App.ViewModels;

internal sealed class DashboardOpenCoordinator
{
    private readonly IDashboardWorkspaceHost _host;
    private readonly Func<LogGroupViewModel, Task<IReadOnlyList<string>>> _resolveOpenTargetsAsync;
    private DashboardLoadSession? _dashboardLoadSession;

    public DashboardOpenCoordinator(
        IDashboardWorkspaceHost host,
        Func<LogGroupViewModel, Task<IReadOnlyList<string>>> resolveOpenTargetsAsync)
    {
        _host = host;
        _resolveOpenTargetsAsync = resolveOpenTargetsAsync;
    }

    public void CancelDashboardLoad()
    {
        _dashboardLoadSession?.CancellationTokenSource.Cancel();
    }

    public void LeaveActiveDashboardScope()
    {
        CancelDashboardLoad();
        _host.ActiveDashboardId = null;
        foreach (var group in _host.Groups)
            group.IsSelected = false;
    }

    public async Task OpenGroupFilesAsync(LogGroupViewModel group, string? modifierLabel)
    {
        var dashboardLoadSession = BeginDashboardLoad(group.Id, excludedPaths: null, reuseCurrentDashboardLoad: false);
        dashboardLoadSession.LoadTask = RunDashboardLoadAsync(group, modifierLabel, dashboardLoadSession);
        await dashboardLoadSession.LoadTask;
    }

    public async Task EnsureGroupFilesLoadedAsync(
        LogGroupViewModel group,
        string? modifierLabel,
        IReadOnlyCollection<string> excludedPaths)
    {
        var dashboardLoadSession = BeginDashboardLoad(group.Id, excludedPaths, reuseCurrentDashboardLoad: true);
        if (dashboardLoadSession.LoadTask == null)
            dashboardLoadSession.LoadTask = RunDashboardLoadAsync(group, modifierLabel, dashboardLoadSession);

        await dashboardLoadSession.LoadTask;
    }

    private async Task RunDashboardLoadAsync(
        LogGroupViewModel group,
        string? modifierLabel,
        DashboardLoadSession dashboardLoadSession)
    {
        var dashboardLoadCts = dashboardLoadSession.CancellationTokenSource;
        var ct = dashboardLoadCts.Token;
        _host.DashboardLoadDepth++;
        _host.IsDashboardLoading = true;
        _host.BeginTabCollectionNotificationSuppression();

        var scopeDisplayName = string.IsNullOrWhiteSpace(modifierLabel)
            ? group.Name
            : $"{group.Name} [{modifierLabel}]";
        var targets = await _resolveOpenTargetsAsync(group);
        SetDashboardLoadingStatus(dashboardLoadCts, targets.Count == 0
            ? $"Loading \"{scopeDisplayName}\"..."
            : $"Loading \"{scopeDisplayName}\" (0/{targets.Count})...");

        await Task.Yield();

        var canceled = false;
        try
        {
            var loadedCount = 0;
            const int maxOpenAttempts = 3;
            for (var index = 0; index < targets.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                var filePath = targets[index];
                if (ShouldSkipTarget(dashboardLoadSession, filePath, group.Id))
                {
                    SetDashboardLoadingStatus(dashboardLoadCts, $"Loading \"{scopeDisplayName}\" ({index + 1}/{targets.Count}, opened {loadedCount})...");
                    continue;
                }

                var fileExists = await FileExistsOffUiAsync(filePath, ct);
                ct.ThrowIfCancellationRequested();
                if (!fileExists)
                {
                    SetDashboardLoadingStatus(dashboardLoadCts, $"Loading \"{scopeDisplayName}\" ({index + 1}/{targets.Count}, opened {loadedCount})...");
                    continue;
                }

                var opened = false;
                for (var attempt = 1; attempt <= maxOpenAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    await _host.OpenFilePathInScopeAsync(
                        filePath,
                        group.Id,
                        reloadIfLoadError: true,
                        activateTab: false,
                        deferVisibilityRefresh: true,
                        ct: ct);
                    ct.ThrowIfCancellationRequested();
                    var tab = _host.FindTabInScope(filePath, group.Id);
                    if (tab != null && !tab.HasLoadError)
                    {
                        opened = true;
                        break;
                    }

                    if (attempt < maxOpenAttempts)
                        await Task.Delay(400, ct);
                }

                if (opened)
                    loadedCount++;

                SetDashboardLoadingStatus(dashboardLoadCts, $"Loading \"{scopeDisplayName}\" ({index + 1}/{targets.Count}, opened {loadedCount})...");
            }

            SetDashboardLoadingStatus(dashboardLoadCts, $"Loaded \"{scopeDisplayName}\" ({loadedCount}/{targets.Count} opened).");
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        finally
        {
            _host.EndTabCollectionNotificationSuppression();
            _host.EnsureSelectedTabInCurrentScope();

            _host.DashboardLoadDepth = Math.Max(0, _host.DashboardLoadDepth - 1);
            if (_host.DashboardLoadDepth == 0)
                _host.IsDashboardLoading = false;

            if (canceled && IsCurrentDashboardLoad(dashboardLoadCts))
                _host.DashboardLoadingStatusText = string.Empty;

            CompleteDashboardLoad(dashboardLoadCts);
        }
    }

    private DashboardLoadSession BeginDashboardLoad(
        string dashboardId,
        IReadOnlyCollection<string>? excludedPaths,
        bool reuseCurrentDashboardLoad)
    {
        if (reuseCurrentDashboardLoad &&
            _dashboardLoadSession != null &&
            string.Equals(_dashboardLoadSession.DashboardId, dashboardId, StringComparison.Ordinal))
        {
            _dashboardLoadSession.AddExcludedPaths(excludedPaths);
            return _dashboardLoadSession;
        }

        var next = new DashboardLoadSession(dashboardId, excludedPaths);
        var previous = _dashboardLoadSession;
        _dashboardLoadSession = next;
        previous?.CancellationTokenSource.Cancel();
        return next;
    }

    private void CompleteDashboardLoad(CancellationTokenSource dashboardLoadCts)
    {
        if (_dashboardLoadSession != null &&
            ReferenceEquals(_dashboardLoadSession.CancellationTokenSource, dashboardLoadCts))
        {
            _dashboardLoadSession = null;
        }

        dashboardLoadCts.Dispose();
    }

    private bool IsCurrentDashboardLoad(CancellationTokenSource dashboardLoadCts)
        => _dashboardLoadSession != null &&
           ReferenceEquals(_dashboardLoadSession.CancellationTokenSource, dashboardLoadCts);

    private void SetDashboardLoadingStatus(CancellationTokenSource dashboardLoadCts, string statusText)
    {
        if (!IsCurrentDashboardLoad(dashboardLoadCts) || dashboardLoadCts.IsCancellationRequested)
            return;

        _host.DashboardLoadingStatusText = statusText;
    }

    private static Task<bool> FileExistsOffUiAsync(string filePath, CancellationToken ct)
        => Task.Run(() => File.Exists(filePath)).WaitAsync(ct);

    private bool ShouldSkipTarget(DashboardLoadSession dashboardLoadSession, string filePath, string dashboardId)
    {
        if (dashboardLoadSession.IsExcluded(filePath))
            return true;

        return _host.FindTabInScope(filePath, dashboardId) != null;
    }

    private sealed class DashboardLoadSession
    {
        private readonly HashSet<string> _excludedPaths = new(StringComparer.OrdinalIgnoreCase);

        public DashboardLoadSession(string dashboardId, IReadOnlyCollection<string>? excludedPaths)
        {
            DashboardId = dashboardId;
            CancellationTokenSource = new CancellationTokenSource();
            AddExcludedPaths(excludedPaths);
        }

        public string DashboardId { get; }

        public CancellationTokenSource CancellationTokenSource { get; }

        public Task? LoadTask { get; set; }

        public void AddExcludedPaths(IReadOnlyCollection<string>? excludedPaths)
        {
            if (excludedPaths == null)
                return;

            foreach (var path in excludedPaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    _excludedPaths.Add(path);
            }
        }

        public bool IsExcluded(string filePath)
            => _excludedPaths.Contains(filePath);
    }
}
