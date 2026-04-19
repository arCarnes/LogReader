namespace LogReader.App.Services;

using System.Collections.ObjectModel;
using LogReader.App.ViewModels;

internal interface ITabWorkspaceHost
{
    bool IsShuttingDown { get; }

    bool GlobalAutoScrollEnabled { get; }

    TimeSpan HiddenTabPurgeAfter { get; }

    ObservableCollection<LogTabViewModel> Tabs { get; }

    string? CurrentScopeDashboardId { get; }

    LogTabViewModel? SelectedTab { get; set; }

    IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot();

    Task MaterializeStoredFilterStateAsync(LogTabViewModel tab, CancellationToken ct = default);
}

internal interface IDashboardWorkspaceHost
{
    ObservableCollection<LogGroupViewModel> Groups { get; }

    ObservableCollection<LogTabViewModel> Tabs { get; }

    LogTabViewModel? SelectedTab { get; }

    bool ShowFullPathsInDashboard { get; }

    int DashboardLoadConcurrency { get; }

    string? ActiveDashboardId { get; set; }

    string DashboardTreeFilter { get; }

    bool IsDashboardLoading { get; set; }

    string DashboardLoadingStatusText { get; set; }

    int DashboardLoadDepth { get; set; }

    void NotifyFilteredTabsChanged();

    void NotifyScopeMetadataChanged();

    void EnsureSelectedTabInCurrentScope();

    void ExitDashboardScopeIfCurrentDashboardFinishedEmpty(string dashboardId);

    void BeginTabCollectionNotificationSuppression();

    void EndTabCollectionNotificationSuppression();

    Task OpenFilePathInScopeAsync(
        string filePath,
        string? scopeDashboardId,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default);

    Task<TabWorkspaceService.PreparedTabOpen?> PrepareDashboardFileOpenAsync(
        string filePath,
        string scopeDashboardId,
        CancellationToken ct = default);

    Task FinalizeDashboardFileOpenAsync(
        TabWorkspaceService.PreparedTabOpen preparedTab,
        CancellationToken ct = default);

    LogTabViewModel? FindTabInScope(string filePath, string? scopeDashboardId);
}

internal sealed class MainViewModelReference
{
    private MainViewModel? _viewModel;

    public void Attach(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_viewModel != null)
            throw new InvalidOperationException("MainViewModel reference has already been attached.");

        _viewModel = viewModel;
    }

    public MainViewModel GetRequired()
        => _viewModel ?? throw new InvalidOperationException("MainViewModel reference has not been attached.");
}

internal sealed class TabWorkspaceHostAdapter : ITabWorkspaceHost
{
    private readonly MainViewModelReference _viewModelReference;

    public TabWorkspaceHostAdapter(MainViewModelReference viewModelReference)
    {
        _viewModelReference = viewModelReference;
    }

    private MainViewModel ViewModel => _viewModelReference.GetRequired();

    public bool IsShuttingDown => ViewModel.IsShuttingDown;

    public bool GlobalAutoScrollEnabled => ViewModel.GlobalAutoScrollEnabled;

    public TimeSpan HiddenTabPurgeAfter => ViewModel.HiddenTabPurgeAfter;

    public ObservableCollection<LogTabViewModel> Tabs => ViewModel.Tabs;

    public string? CurrentScopeDashboardId => ViewModel.ActiveDashboardId;

    public LogTabViewModel? SelectedTab
    {
        get => ViewModel.SelectedTab;
        set => ViewModel.SelectedTab = value;
    }

    public IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot()
        => ViewModel.GetFilteredTabsSnapshot();

    public Task MaterializeStoredFilterStateAsync(LogTabViewModel tab, CancellationToken ct = default)
        => ViewModel.FilterPanel.MaterializeStoredFilterStateAsync(tab, ct);
}

internal sealed class DashboardWorkspaceHostAdapter : IDashboardWorkspaceHost
{
    private readonly MainViewModelReference _viewModelReference;

    public DashboardWorkspaceHostAdapter(MainViewModelReference viewModelReference)
    {
        _viewModelReference = viewModelReference;
    }

    private MainViewModel ViewModel => _viewModelReference.GetRequired();

    public ObservableCollection<LogGroupViewModel> Groups => ViewModel.Groups;

    public ObservableCollection<LogTabViewModel> Tabs => ViewModel.Tabs;

    public LogTabViewModel? SelectedTab => ViewModel.SelectedTab;

    public bool ShowFullPathsInDashboard => ViewModel.ShowFullPathsInDashboard;

    public int DashboardLoadConcurrency => ViewModel.DashboardLoadConcurrency;

    public string? ActiveDashboardId
    {
        get => ViewModel.ActiveDashboardId;
        set => ViewModel.ActiveDashboardId = value;
    }

    public string DashboardTreeFilter => ViewModel.DashboardTreeFilter;

    public bool IsDashboardLoading
    {
        get => ViewModel.IsDashboardLoading;
        set => ViewModel.IsDashboardLoading = value;
    }

    public string DashboardLoadingStatusText
    {
        get => ViewModel.DashboardLoadingStatusText;
        set => ViewModel.DashboardLoadingStatusText = value;
    }

    public int DashboardLoadDepth
    {
        get => ViewModel.DashboardLoadDepth;
        set => ViewModel.DashboardLoadDepth = value;
    }

    public void NotifyFilteredTabsChanged() => ViewModel.NotifyFilteredTabsChanged();

    public void NotifyScopeMetadataChanged() => ViewModel.NotifyScopeMetadataChanged();

    public void EnsureSelectedTabInCurrentScope() => ViewModel.EnsureSelectedTabInCurrentScope();

    public void ExitDashboardScopeIfCurrentDashboardFinishedEmpty(string dashboardId)
        => ViewModel.ExitDashboardScopeIfCurrentDashboardFinishedEmpty(dashboardId);

    public void BeginTabCollectionNotificationSuppression() => ViewModel.BeginTabCollectionNotificationSuppression();

    public void EndTabCollectionNotificationSuppression() => ViewModel.EndTabCollectionNotificationSuppression();

    public Task OpenFilePathInScopeAsync(
        string filePath,
        string? scopeDashboardId,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default)
        => ViewModel.OpenFilePathInScopeAsync(filePath, scopeDashboardId, reloadIfLoadError, activateTab, deferVisibilityRefresh, ct);

    public Task<TabWorkspaceService.PreparedTabOpen?> PrepareDashboardFileOpenAsync(
        string filePath,
        string scopeDashboardId,
        CancellationToken ct = default)
        => ViewModel.PrepareDashboardFileOpenAsync(filePath, scopeDashboardId, ct);

    public Task FinalizeDashboardFileOpenAsync(
        TabWorkspaceService.PreparedTabOpen preparedTab,
        CancellationToken ct = default)
        => ViewModel.FinalizeDashboardFileOpenAsync(preparedTab, ct);

    public LogTabViewModel? FindTabInScope(string filePath, string? scopeDashboardId)
        => ViewModel.FindTabInScope(filePath, scopeDashboardId);
}
