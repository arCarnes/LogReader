namespace LogReader.App.Services;

using System.IO;
using LogReader.App.ViewModels;

internal sealed class DashboardOpenCoordinator
{
    private readonly IDashboardWorkspaceHost _host;
    private readonly Func<LogGroupViewModel, Task<IReadOnlyList<string>>> _resolveOpenTargetsAsync;
    private CancellationTokenSource? _dashboardLoadCts;

    public DashboardOpenCoordinator(
        IDashboardWorkspaceHost host,
        Func<LogGroupViewModel, Task<IReadOnlyList<string>>> resolveOpenTargetsAsync)
    {
        _host = host;
        _resolveOpenTargetsAsync = resolveOpenTargetsAsync;
    }

    public void CancelDashboardLoad()
    {
        _dashboardLoadCts?.Cancel();
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
        var dashboardLoadCts = BeginDashboardLoad();
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

    private CancellationTokenSource BeginDashboardLoad()
    {
        var next = new CancellationTokenSource();
        var previous = _dashboardLoadCts;
        _dashboardLoadCts = next;
        previous?.Cancel();
        return next;
    }

    private void CompleteDashboardLoad(CancellationTokenSource dashboardLoadCts)
    {
        if (ReferenceEquals(_dashboardLoadCts, dashboardLoadCts))
            _dashboardLoadCts = null;

        dashboardLoadCts.Dispose();
    }

    private bool IsCurrentDashboardLoad(CancellationTokenSource dashboardLoadCts)
        => ReferenceEquals(_dashboardLoadCts, dashboardLoadCts);

    private void SetDashboardLoadingStatus(CancellationTokenSource dashboardLoadCts, string statusText)
    {
        if (!IsCurrentDashboardLoad(dashboardLoadCts) || dashboardLoadCts.IsCancellationRequested)
            return;

        _host.DashboardLoadingStatusText = statusText;
    }

    private static Task<bool> FileExistsOffUiAsync(string filePath, CancellationToken ct)
        => Task.Run(() => File.Exists(filePath)).WaitAsync(ct);
}
