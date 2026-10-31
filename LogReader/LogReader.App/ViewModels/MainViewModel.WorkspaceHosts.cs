namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using LogReader.App.Services;

public partial class MainViewModel
{
    bool ITabWorkspaceHost.HasStoredFilterState(LogTabViewModel tab) => this.FilterPanel.HasStoredFilterState(tab);

    bool ITabWorkspaceHost.IsShuttingDown => this.IsShuttingDown;

    bool ITabWorkspaceHost.GlobalAutoScrollEnabled => this.GlobalAutoScrollEnabled;

    ObservableCollection<LogTabViewModel> ITabWorkspaceHost.Tabs => this.Tabs;

    string? ITabWorkspaceHost.CurrentScopeDashboardId => this.ActiveDashboardId;

    LogTabViewModel? ITabWorkspaceHost.SelectedTab
    {
        get => this.SelectedTab;
        set => this.SelectedTab = value;
    }

    IReadOnlyList<LogTabViewModel> ITabWorkspaceHost.GetFilteredTabsSnapshot()
        => this.GetFilteredTabsSnapshot();

    void ITabWorkspaceHost.CaptureStoredFilterStateBeforeTabClose(LogTabViewModel tab)
        => this.FilterPanel.CaptureStoredFilterStateBeforeTabClose(tab);

    Task ITabWorkspaceHost.MaterializeStoredFilterStateAsync(LogTabViewModel tab, CancellationToken ct)
        => this.FilterPanel.MaterializeStoredFilterStateAsync(tab, ct);

    void IDashboardWorkspaceHost.NotifyDashboardLoadStateChanged() => this.NotifyDashboardLoadStateChanged();
    void IDashboardWorkspaceHost.BeginDashboardLoadCycle(string dashboardId) => this.BeginDashboardLoadCycle(dashboardId);
    Task<TabWorkspaceService.PreparedTabOpen?> IDashboardWorkspaceHost.CreateDashboardPlaceholderAsync(string path, string dashboardId, CancellationToken ct)
        => this.CreateDashboardPlaceholderAsync(path, dashboardId, ct);
    Task IDashboardWorkspaceHost.CompleteDashboardPlaceholderAsync(TabWorkspaceService.PreparedTabOpen prepared, CancellationToken ct)
        => this.CompleteDashboardPlaceholderAsync(prepared, ct);

    ObservableCollection<LogGroupViewModel> IDashboardWorkspaceHost.Groups => this.Groups;

    ObservableCollection<LogTabViewModel> IDashboardWorkspaceHost.Tabs => this.Tabs;

    LogTabViewModel? IDashboardWorkspaceHost.SelectedTab => this.SelectedTab;

    bool IDashboardWorkspaceHost.ShowFullPathsInDashboard => this.ShowFullPathsInDashboard;

    string? IDashboardWorkspaceHost.ActiveDashboardId
    {
        get => this.ActiveDashboardId;
        set => this.ActiveDashboardId = value;
    }

    string IDashboardWorkspaceHost.DashboardTreeFilter => this.DashboardTreeFilter;

    bool IDashboardWorkspaceHost.IsDashboardLoading
    {
        get => this.IsDashboardLoading;
        set => this.IsDashboardLoading = value;
    }

    string IDashboardWorkspaceHost.DashboardLoadingStatusText
    {
        get => this.DashboardLoadingStatusText;
        set => this.DashboardLoadingStatusText = value;
    }

    int IDashboardWorkspaceHost.DashboardLoadDepth
    {
        get => this.DashboardLoadDepth;
        set => this.DashboardLoadDepth = value;
    }

    void IDashboardWorkspaceHost.NotifyFilteredTabsChanged() => this.NotifyFilteredTabsChanged();

    void IDashboardWorkspaceHost.NotifyScopeMetadataChanged() => this.NotifyScopeMetadataChanged();

    void IDashboardWorkspaceHost.EnsureSelectedTabInCurrentScope() => this.EnsureSelectedTabInCurrentScope();

    void IDashboardWorkspaceHost.ExitDashboardScopeIfCurrentDashboardFinishedEmpty(string dashboardId)
        => this.ExitDashboardScopeIfCurrentDashboardFinishedEmpty(dashboardId);

    Task IDashboardWorkspaceHost.OpenFilePathInScopeAsync(
        string filePath,
        string? scopeDashboardId,
        bool reloadIfLoadError,
        bool activateTab,
        bool deferVisibilityRefresh,
        CancellationToken ct)
        => this.OpenFilePathInScopeAsync(filePath, scopeDashboardId, reloadIfLoadError, activateTab, deferVisibilityRefresh, ct);

    LogTabViewModel? IDashboardWorkspaceHost.FindTabInScope(string filePath, string? scopeDashboardId)
        => this.FindTabInScope(filePath, scopeDashboardId);
}
