namespace LogReader.App.Services;

using LogReader.Core.Models;

internal enum DashboardLoadPhase
{
    Idle, Resolving, Queued, Working, Ready, ReadyWithErrors, Failed, Canceled
}

internal sealed record DashboardLoadProgress(long Revision, DashboardLoadPhase Phase, int Total, int Ready, int Settled, int Failed,
    InteractiveIndexProgress? SelectedFileProgress = null)
{
    public bool IsLoading => Phase is DashboardLoadPhase.Resolving or DashboardLoadPhase.Queued or DashboardLoadPhase.Working;
    public string Status => Phase == DashboardLoadPhase.Canceled ? "Loading canceled"
        : IsLoading ? $"{Ready}/{Total} files ready; {Settled} finished"
        : $"{Ready}/{Total} files ready; {Failed} failed";
}
