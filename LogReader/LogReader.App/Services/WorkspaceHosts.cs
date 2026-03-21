namespace LogReader.App.Services;

using System.Collections.ObjectModel;
using LogReader.App.ViewModels;

internal interface ITabWorkspaceHost
{
    bool IsShuttingDown { get; }

    bool GlobalAutoScrollEnabled { get; }

    TimeSpan HiddenTabPurgeAfter { get; }

    ObservableCollection<LogTabViewModel> Tabs { get; }

    LogTabViewModel? SelectedTab { get; set; }

    IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot();
}

internal interface IDashboardWorkspaceHost
{
    ObservableCollection<LogGroupViewModel> Groups { get; }

    ObservableCollection<LogTabViewModel> Tabs { get; }

    LogTabViewModel? SelectedTab { get; }

    string? ActiveDashboardId { get; set; }

    string DashboardTreeFilter { get; }

    bool IsDashboardLoading { get; set; }

    string DashboardLoadingStatusText { get; set; }

    int DashboardLoadDepth { get; set; }

    void NotifyFilteredTabsChanged();

    void NotifyScopeMetadataChanged();

    void EnsureSelectedTabInCurrentScope();

    void BeginTabCollectionNotificationSuppression();

    void EndTabCollectionNotificationSuppression();

    Task OpenFilePathAsync(
        string filePath,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default);
}
