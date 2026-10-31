namespace LogReader.App.Services;

using System.Collections.ObjectModel;
using LogReader.App.ViewModels;

internal interface ITabWorkspaceHost
{
    bool IsShuttingDown { get; }

    bool GlobalAutoScrollEnabled { get; }

    ObservableCollection<LogTabViewModel> Tabs { get; }

    string? CurrentScopeDashboardId { get; }

    LogTabViewModel? SelectedTab { get; set; }

    IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot();

    void CaptureStoredFilterStateBeforeTabClose(LogTabViewModel tab);

    Task MaterializeStoredFilterStateAsync(LogTabViewModel tab, CancellationToken ct = default);

    bool HasStoredFilterState(LogTabViewModel tab) => false;
}

internal interface IDashboardWorkspaceHost
{
    void NotifyDashboardLoadStateChanged() { }

    void BeginDashboardLoadCycle(string dashboardId) { }

    Task<TabWorkspaceService.PreparedTabOpen?> CreateDashboardPlaceholderAsync(string path, string dashboardId, CancellationToken ct);

    Task CompleteDashboardPlaceholderAsync(TabWorkspaceService.PreparedTabOpen prepared, CancellationToken ct);

    ObservableCollection<LogGroupViewModel> Groups { get; }

    ObservableCollection<LogTabViewModel> Tabs { get; }

    LogTabViewModel? SelectedTab { get; }

    bool ShowFullPathsInDashboard { get; }

    string? ActiveDashboardId { get; set; }

    string DashboardTreeFilter { get; }

    bool IsDashboardLoading { get; set; }

    string DashboardLoadingStatusText { get; set; }

    int DashboardLoadDepth { get; set; }

    void NotifyFilteredTabsChanged();

    void NotifyScopeMetadataChanged();

    void EnsureSelectedTabInCurrentScope();

    void ExitDashboardScopeIfCurrentDashboardFinishedEmpty(string dashboardId);

    Task OpenFilePathInScopeAsync(
        string filePath,
        string? scopeDashboardId,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default);

    LogTabViewModel? FindTabInScope(string filePath, string? scopeDashboardId);
}
