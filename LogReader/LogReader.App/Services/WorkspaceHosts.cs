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

    public bool HasStoredFilterState(LogTabViewModel tab) => ViewModel.FilterPanel.HasStoredFilterState(tab);

    public bool IsShuttingDown => ViewModel.IsShuttingDown;

    public bool GlobalAutoScrollEnabled => ViewModel.GlobalAutoScrollEnabled;

    public ObservableCollection<LogTabViewModel> Tabs => ViewModel.Tabs;

    public string? CurrentScopeDashboardId => ViewModel.ActiveDashboardId;

    public LogTabViewModel? SelectedTab
    {
        get => ViewModel.SelectedTab;
        set => ViewModel.SelectedTab = value;
    }

    public IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot()
        => ViewModel.GetFilteredTabsSnapshot();

    public void CaptureStoredFilterStateBeforeTabClose(LogTabViewModel tab)
        => ViewModel.FilterPanel.CaptureStoredFilterStateBeforeTabClose(tab);

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

    public void NotifyDashboardLoadStateChanged() => ViewModel.NotifyDashboardLoadStateChanged();
    public void BeginDashboardLoadCycle(string dashboardId) => ViewModel.BeginDashboardLoadCycle(dashboardId);
    public Task<TabWorkspaceService.PreparedTabOpen?> CreateDashboardPlaceholderAsync(string path, string dashboardId, CancellationToken ct)
        => ViewModel.CreateDashboardPlaceholderAsync(path, dashboardId, ct);
    public Task CompleteDashboardPlaceholderAsync(TabWorkspaceService.PreparedTabOpen prepared, CancellationToken ct)
        => ViewModel.CompleteDashboardPlaceholderAsync(prepared, ct);

    public ObservableCollection<LogGroupViewModel> Groups => ViewModel.Groups;

    public ObservableCollection<LogTabViewModel> Tabs => ViewModel.Tabs;

    public LogTabViewModel? SelectedTab => ViewModel.SelectedTab;

    public bool ShowFullPathsInDashboard => ViewModel.ShowFullPathsInDashboard;

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

    public Task OpenFilePathInScopeAsync(
        string filePath,
        string? scopeDashboardId,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default)
        => ViewModel.OpenFilePathInScopeAsync(filePath, scopeDashboardId, reloadIfLoadError, activateTab, deferVisibilityRefresh, ct);

    public LogTabViewModel? FindTabInScope(string filePath, string? scopeDashboardId)
        => ViewModel.FindTabInScope(filePath, scopeDashboardId);
}
