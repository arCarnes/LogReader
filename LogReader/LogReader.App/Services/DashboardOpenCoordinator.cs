namespace LogReader.App.Services;

using LogReader.App.ViewModels;

internal sealed partial class DashboardOpenCoordinator
{
    private const int ProgressPublicationIntervalMs = 150;

    private readonly IDashboardWorkspaceHost _host;
    private readonly Func<LogGroupViewModel, Task<IReadOnlyList<string>>> _resolveOpenTargetsAsync;
    private readonly Func<string, CancellationToken, Task<DashboardFileProbeResult>> _probeFileAsync;

    public DashboardOpenCoordinator(
        IDashboardWorkspaceHost host,
        Func<LogGroupViewModel, Task<IReadOnlyList<string>>> resolveOpenTargetsAsync,
        Func<string, CancellationToken, Task<DashboardFileProbeResult>>? probeFileAsync = null)
    {
        _host = host;
        _resolveOpenTargetsAsync = resolveOpenTargetsAsync;
        _probeFileAsync = probeFileAsync ?? DashboardFileProbe.ProbeOffUiAsync;
    }

    public void CancelDashboardLoad() => CancelInteractiveLoads();

    public void LeaveActiveDashboardScope()
    {
        _host.ActiveDashboardId = null;
        foreach (var group in _host.Groups)
            group.IsSelected = false;
    }

    public Task OpenGroupFilesAsync(LogGroupViewModel group, string? modifierLabel)
        => OpenInteractiveDashboardAsync(group);
}
