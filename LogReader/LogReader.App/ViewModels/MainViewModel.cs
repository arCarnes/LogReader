namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class MainViewModel : ObservableObject, ILogWorkspaceContext, ITabWorkspaceHost, IDashboardWorkspaceHost, IDisposable
{
    private const double DefaultGroupsPanelWidth = 220;
    private const double DefaultSearchPanelHeight = 260;
    private const double MinRememberedPanelSize = 36;
    private static readonly TimeSpan DefaultLifecycleSweepInterval = TimeSpan.FromSeconds(30);
    private readonly ILogGroupRepository _groupRepo;
    private readonly ISettingsRepository _settingsRepo;
    private readonly IFileTailService _tailService;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly ISettingsDialogService _settingsDialogService;
    private readonly IBulkOpenPathsDialogService _bulkOpenPathsDialogService;
    private readonly Func<ISettingsRepository, SettingsViewModel> _settingsViewModelFactory;
    private readonly LogFileCatalogService _fileCatalogService;
    private readonly TabWorkspaceService _tabWorkspace;
    private readonly DashboardWorkspaceService _dashboardWorkspace;
    private readonly RuntimePersistedStateRecoveryExecutor _runtimeRecoveryExecutor;
    private readonly DashboardScopeService _dashboardScope = new();
    private readonly TabCollectionRefreshCoordinator _tabCollectionRefreshCoordinator = new();
    private readonly SearchFilterSharedOptions _searchFilterSharedOptions = new();
    private readonly System.Threading.Timer? _tabLifecycleTimer;

    private AppSettings _settings = new();
    private int _autoScrollSyncVersion;
    public TimeSpan HiddenTabPurgeAfter { get; set; } = TimeSpan.FromMinutes(20);

    public ObservableCollection<LogTabViewModel> Tabs { get; } = new();
    public ObservableCollection<LogGroupViewModel> Groups { get; } = new();
    public SearchPanelViewModel SearchPanel { get; }
    public FilterPanelViewModel FilterPanel { get; }

    [ObservableProperty]
    private LogTabViewModel? _selectedTab;

    [ObservableProperty]
    private string? _activeDashboardId;

    [ObservableProperty]
    private bool _isGroupsPanelOpen = true;

    [ObservableProperty]
    private double _groupsPanelWidth = DefaultGroupsPanelWidth;

    [ObservableProperty]
    private double _searchPanelHeight = DefaultSearchPanelHeight;

    [ObservableProperty]
    private bool _globalAutoScrollEnabled = true;

    [ObservableProperty]
    private string _dashboardTreeFilter = string.Empty;

    [ObservableProperty]
    private bool _isDashboardLoading;

    [ObservableProperty]
    private string _dashboardLoadingStatusText = string.Empty;

    [ObservableProperty]
    private int _viewportRefreshVersion;

    private int _dashboardLoadDepth;
    private int _shutdownStarted;
    private bool _disposed;

    internal bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) != 0;
    internal int DashboardLoadDepth
    {
        get => _dashboardLoadDepth;
        set => _dashboardLoadDepth = value;
    }

    public bool ShowFullPathsInDashboard => _settings.ShowFullPathsInDashboard;

    public IEnumerable<LogTabViewModel> FilteredTabs
    {
        get
        {
            if (!string.IsNullOrEmpty(ActiveDashboardId) &&
                _dashboardWorkspace.TryGetDashboardEffectivePaths(ActiveDashboardId, out var dashboardEffectivePaths))
            {
                return _tabWorkspace.OrderTabsForDisplay(
                    Tabs.Where(tab =>
                        string.Equals(tab.ScopeDashboardId, ActiveDashboardId, StringComparison.Ordinal) &&
                        dashboardEffectivePaths.Contains(tab.FilePath)));
            }

            if (string.IsNullOrEmpty(ActiveDashboardId) &&
                _dashboardWorkspace.TryGetAdHocEffectivePaths(out var adHocEffectivePaths))
            {
                return _tabWorkspace.OrderTabsForDisplay(
                    Tabs.Where(tab => tab.IsAdHocScope && adHocEffectivePaths.Contains(tab.FilePath)));
            }

            if (string.IsNullOrEmpty(ActiveDashboardId))
                return _tabWorkspace.OrderTabsForDisplay(GetNormalAdHocTabs());

            var activeDashboard = GetActiveDashboard();
            if (activeDashboard == null)
            {
                return _dashboardScope.GetFilteredTabs(
                    Tabs,
                    ActiveDashboardId,
                    _tabWorkspace.OrderTabsForDisplay);
            }

            var scopedTabs = _dashboardScope.GetFilteredTabs(
                Tabs,
                ActiveDashboardId,
                scopedTabs => scopedTabs.ToList());
            return _tabWorkspace.OrderTabsForDashboardDisplay(scopedTabs, activeDashboard.Model.FileIds);
        }
    }

    public bool IsAdHocScopeActive => string.IsNullOrEmpty(ActiveDashboardId);

    public bool IsCurrentScopeEmpty => !FilteredTabs.Any();

    public bool ShouldShowEmptyState => SelectedTab == null && IsCurrentScopeEmpty;

    public string CurrentScopeLabel
    {
        get
        {
            if (IsAdHocScopeActive)
                return GetAdHocScopeLabel();

            var activeName = Groups.FirstOrDefault(g => g.Id == ActiveDashboardId)?.DisplayName;
            return string.IsNullOrWhiteSpace(activeName) ? "Dashboard" : activeName;
        }
    }

    public string CurrentScopeSummaryText => $"Scope: {CurrentScopeLabel} ({FilteredTabs.Count()})";

    public string AdHocScopeChipText => $"{GetAdHocScopeLabel()} ({GetAdHocTabs().Count})";

    public string EmptyStateText
    {
        get
        {
            if (Tabs.Count == 0)
                return "Drag log files here to open them, or create a dashboard on the left and add files";

            if (IsAdHocScopeActive)
                return $"No {CurrentScopeLabel} tabs. Open a file that is not assigned to a dashboard, or select a dashboard on the left.";

            return $"\"{CurrentScopeLabel}\" has no open tabs. Open files from the dashboard tree, or switch back to Ad Hoc.";
        }
    }

    public string TabCountText
    {
        get
        {
            var total = Tabs.Count;
            if (IsAdHocScopeActive)
            {
                var adhoc = FilteredTabs.Count();
                return $"{adhoc} of {total} tabs ({GetAdHocScopeLabel()})";
            }

            var filtered = FilteredTabs.Count();
            return $"{filtered} of {total} tabs (Dashboard)";
        }
    }

    public bool CanAddFilesToActiveDashboard => GetActiveDashboard() != null;

    public MainViewModel(
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo,
        ISettingsRepository settingsRepo,
        ILogReaderService logReader,
        ISearchService searchService,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        bool enableLifecycleTimer = true,
        IFileDialogService? fileDialogService = null,
        IMessageBoxService? messageBoxService = null,
        ISettingsDialogService? settingsDialogService = null,
        IBulkOpenPathsDialogService? bulkOpenPathsDialogService = null,
        Func<ISettingsRepository, SettingsViewModel>? settingsViewModelFactory = null)
        : this(
            fileRepo,
            groupRepo,
            settingsRepo,
            logReader,
            searchService,
            tailService,
            encodingDetectionService,
            enableLifecycleTimer,
            fileDialogService,
            messageBoxService,
            settingsDialogService,
            bulkOpenPathsDialogService,
            settingsViewModelFactory,
            null)
    {
    }

    internal MainViewModel(
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo,
        ISettingsRepository settingsRepo,
        ILogReaderService logReader,
        ISearchService searchService,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        bool enableLifecycleTimer,
        IFileDialogService? fileDialogService,
        IMessageBoxService? messageBoxService,
        ISettingsDialogService? settingsDialogService,
        IBulkOpenPathsDialogService? bulkOpenPathsDialogService,
        Func<ISettingsRepository, SettingsViewModel>? settingsViewModelFactory,
        IPersistedStateRecoveryCoordinator? persistedStateRecoveryCoordinator)
    {
        _groupRepo = groupRepo;
        _settingsRepo = settingsRepo;
        _tailService = tailService;
        _encodingDetectionService = encodingDetectionService;
        _fileDialogService = fileDialogService ?? new FileDialogService();
        _messageBoxService = messageBoxService ?? new MessageBoxService();
        _settingsDialogService = settingsDialogService ?? new SettingsDialogService();
        _bulkOpenPathsDialogService = bulkOpenPathsDialogService ?? new BulkOpenPathsDialogService();
        _settingsViewModelFactory = settingsViewModelFactory ?? (static repo => new SettingsViewModel(repo));
        _fileCatalogService = new LogFileCatalogService(fileRepo);
        _tabWorkspace = new TabWorkspaceService(
            this,
            fileRepo,
            logReader,
            tailService,
            encodingDetectionService,
            _fileCatalogService);
        _dashboardWorkspace = new DashboardWorkspaceService(this, fileRepo, groupRepo, _fileCatalogService, null);
        _runtimeRecoveryExecutor = new RuntimePersistedStateRecoveryExecutor(
            persistedStateRecoveryCoordinator ?? new PersistedStateRecoveryCoordinator(),
            _messageBoxService,
            RefreshRecoveredStoreStateAsync);
        SearchPanel = new SearchPanelViewModel(searchService, this, _searchFilterSharedOptions);
        FilterPanel = new FilterPanelViewModel(searchService, this, _searchFilterSharedOptions);
        if (enableLifecycleTimer)
        {
            _tabLifecycleTimer = new System.Threading.Timer(
                _ => RunTabLifecycleMaintenanceOnUiThread(),
                null,
                DefaultLifecycleSweepInterval,
                DefaultLifecycleSweepInterval);
        }

        Tabs.CollectionChanged += Tabs_CollectionChanged;
    }

    [RelayCommand]
    private async Task OpenFile()
    {
        var result = _fileDialogService.ShowOpenFileDialog(
            new OpenFileDialogRequest(
                "Open Log File",
                "Log Files (*.log;*.txt)|*.log;*.txt|All Files (*.*)|*.*",
                Multiselect: true,
                InitialDirectory: GetDefaultOpenDirectory()));

        if (result.Accepted)
        {
            foreach (var file in result.FileNames)
                await OpenFilePathAsync(file);
        }
    }

    public async Task OpenFilePathAsync(
        string filePath,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default)
    {
        await ExecuteRecoverableCommandAsync(async () =>
        {
            var targetScopeDashboardId = await ResolveTargetScopeDashboardIdForOpenAsync(filePath);
            await OpenFilePathInScopeAsync(
                filePath,
                targetScopeDashboardId,
                reloadIfLoadError,
                activateTab,
                deferVisibilityRefresh,
                ct);

            if (!activateTab)
                return;

            var tab = FindTabInScope(filePath, targetScopeDashboardId);
            if (tab != null)
                EnsureTabVisibleInCurrentScope(tab);
        });
    }

    [RelayCommand]
    private async Task CloseTab(LogTabViewModel? tab)
    {
        BeginTabCollectionNotificationSuppression();
        try
        {
            await _tabWorkspace.CloseTabAsync(tab);
            ClearActiveDashboardWhenNoScopedTabsRemain();
            ClearActiveDashboardWhenNoTabsRemain();
        }
        finally
        {
            EndTabCollectionNotificationSuppression();
        }
    }

    public async Task CloseAllTabsAsync()
    {
        BeginTabCollectionNotificationSuppression();
        try
        {
            await _tabWorkspace.CloseAllTabsAsync();
            ClearActiveDashboardWhenNoScopedTabsRemain();
            ClearActiveDashboardWhenNoTabsRemain();
        }
        finally
        {
            EndTabCollectionNotificationSuppression();
        }
    }

    public async Task CloseOtherTabsAsync(LogTabViewModel keepTab)
    {
        BeginTabCollectionNotificationSuppression();
        try
        {
            await _tabWorkspace.CloseOtherTabsAsync(keepTab);
            ClearActiveDashboardWhenNoScopedTabsRemain();
            ClearActiveDashboardWhenNoTabsRemain();
        }
        finally
        {
            EndTabCollectionNotificationSuppression();
        }
    }

    public async Task CloseAllButPinnedAsync()
    {
        BeginTabCollectionNotificationSuppression();
        try
        {
            await _tabWorkspace.CloseAllButPinnedAsync();
            ClearActiveDashboardWhenNoScopedTabsRemain();
            ClearActiveDashboardWhenNoTabsRemain();
        }
        finally
        {
            EndTabCollectionNotificationSuppression();
        }
    }

    [RelayCommand]
    private void SelectPreviousTab()
    {
        SelectRelativeTab(-1);
    }

    [RelayCommand]
    private void SelectNextTab()
    {
        SelectRelativeTab(1);
    }

    private void SelectRelativeTab(int delta)
    {
        var tabs = FilteredTabs.ToList();
        if (tabs.Count == 0)
            return;

        if (SelectedTab == null)
        {
            SelectedTab = delta < 0 ? tabs[^1] : tabs[0];
            return;
        }

        var index = tabs.IndexOf(SelectedTab);
        if (index < 0)
        {
            SelectedTab = tabs[0];
            return;
        }

        var targetIndex = Math.Clamp(index + delta, 0, tabs.Count - 1);
        if (targetIndex != index)
            SelectedTab = tabs[targetIndex];
    }

    public void TogglePinTab(LogTabViewModel tab)
    {
        _tabWorkspace.TogglePinTab(tab);
        NotifyFilteredTabsChanged();
    }

    [RelayCommand]
    private void ShowAdHocTabs()
    {
        ActivateAdHocScope();
    }

    [RelayCommand]
    private void ToggleGroupsPanel()
    {
        IsGroupsPanelOpen = !IsGroupsPanelOpen;
    }

    [RelayCommand]
    private void ToggleFocusMode()
    {
        IsGroupsPanelOpen = !IsGroupsPanelOpen;
    }

    [RelayCommand]
    private Task ApplySelectedTabEncodingToAll()
    {
        if (SelectedTab == null)
            return Task.CompletedTask;

        var targetEncoding = SelectedTab.Encoding;
        foreach (var tab in Tabs)
        {
            if (tab.Encoding != targetEncoding)
                tab.Encoding = targetEncoding;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ExpandAllFolders()
    {
        foreach (var group in Groups.Where(g => g.Kind == LogGroupKind.Branch))
            group.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAllFolders()
    {
        foreach (var group in Groups.Where(g => g.Kind == LogGroupKind.Branch))
            group.IsExpanded = false;
    }

    public void RememberGroupsPanelWidth(double width)
    {
        if (width >= MinRememberedPanelSize)
            GroupsPanelWidth = width;
    }

    public void RememberSearchPanelHeight(double height)
    {
        if (height >= MinRememberedPanelSize)
            SearchPanelHeight = height;
    }

    internal void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        _tabLifecycleTimer?.Dispose();
        SearchPanel.Dispose();
        FilterPanel.Dispose();

        foreach (var tab in Tabs.ToList())
            tab.BeginShutdown();

        _tailService.StopAll();
    }

    bool ITabWorkspaceHost.IsShuttingDown => IsShuttingDown;

    bool ITabWorkspaceHost.GlobalAutoScrollEnabled => GlobalAutoScrollEnabled;

    TimeSpan ITabWorkspaceHost.HiddenTabPurgeAfter => HiddenTabPurgeAfter;

    ObservableCollection<LogTabViewModel> ITabWorkspaceHost.Tabs => Tabs;

    string? ITabWorkspaceHost.CurrentScopeDashboardId => ActiveDashboardId;

    LogTabViewModel? ITabWorkspaceHost.SelectedTab
    {
        get => SelectedTab;
        set => SelectedTab = value;
    }

    IReadOnlyList<LogTabViewModel> ITabWorkspaceHost.GetFilteredTabsSnapshot()
        => GetFilteredTabsSnapshot();

    Task ITabWorkspaceHost.MaterializeStoredFilterStateAsync(LogTabViewModel tab, CancellationToken ct)
        => FilterPanel.MaterializeStoredFilterStateAsync(tab, ct);

    string? ILogWorkspaceContext.ActiveScopeDashboardId => ActiveDashboardId;

    WorkspaceScopeSnapshot ILogWorkspaceContext.GetActiveScopeSnapshot()
        => GetActiveScopeSnapshot();

    async Task<FileEncoding> ILogWorkspaceContext.ResolveFilterFileEncodingAsync(string filePath, string? scopeDashboardId, CancellationToken ct)
    {
        var openTab = FindTabInScope(filePath, scopeDashboardId);
        if (openTab != null)
            return openTab.EffectiveEncoding;

        var requestedEncoding = _tabWorkspace.TryGetRecentRequestedEncoding(filePath, scopeDashboardId) ?? FileEncoding.Auto;
        return await Task.Run(
            () => _encodingDetectionService.ResolveEncodingDecision(filePath, requestedEncoding).ResolvedEncoding,
            ct).WaitAsync(ct);
    }

    Task<IReadOnlyDictionary<string, LogTabViewModel>> ILogWorkspaceContext.EnsureBackgroundTabsOpenAsync(
        IReadOnlyList<string> filePaths,
        string? scopeDashboardId,
        CancellationToken ct)
        => EnsureBackgroundTabsOpenAsync(filePaths, scopeDashboardId, ct);

    LogFilterSession.FilterSnapshot? ILogWorkspaceContext.GetApplicableCurrentTabFilterSnapshot(SearchDataMode sourceMode)
        => FilterPanel.GetApplicableCurrentTabFilterSnapshot(sourceMode);

    LogFilterSession.FilterSnapshot? ILogWorkspaceContext.GetApplicableCurrentScopeFilterSnapshot(string filePath, SearchDataMode sourceMode)
        => FilterPanel.GetApplicableCurrentScopeFilterSnapshot(filePath, sourceMode);

    IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> ILogWorkspaceContext.GetApplicableCurrentScopeFilterSnapshots(SearchDataMode sourceMode)
        => FilterPanel.GetApplicableCurrentScopeFilterSnapshots(sourceMode);

    void ILogWorkspaceContext.UpdateRecentTabFilterSnapshot(string filePath, string? scopeDashboardId, LogFilterSession.FilterSnapshot? snapshot)
        => _tabWorkspace.UpdateRecentTabFilterSnapshot(filePath, scopeDashboardId, snapshot);

    Task ILogWorkspaceContext.RunViewActionAsync(Func<Task> operation, string failureCaption)
        => RunViewActionAsync(operation, failureCaption);

    ObservableCollection<LogGroupViewModel> IDashboardWorkspaceHost.Groups => Groups;

    ObservableCollection<LogTabViewModel> IDashboardWorkspaceHost.Tabs => Tabs;

    LogTabViewModel? IDashboardWorkspaceHost.SelectedTab => SelectedTab;

    bool IDashboardWorkspaceHost.ShowFullPathsInDashboard => ShowFullPathsInDashboard;

    string? IDashboardWorkspaceHost.ActiveDashboardId
    {
        get => ActiveDashboardId;
        set => ActiveDashboardId = value;
    }

    string IDashboardWorkspaceHost.DashboardTreeFilter => DashboardTreeFilter;

    bool IDashboardWorkspaceHost.IsDashboardLoading
    {
        get => IsDashboardLoading;
        set => IsDashboardLoading = value;
    }

    string IDashboardWorkspaceHost.DashboardLoadingStatusText
    {
        get => DashboardLoadingStatusText;
        set => DashboardLoadingStatusText = value;
    }

    int IDashboardWorkspaceHost.DashboardLoadDepth
    {
        get => DashboardLoadDepth;
        set => DashboardLoadDepth = value;
    }

    void IDashboardWorkspaceHost.NotifyFilteredTabsChanged() => NotifyFilteredTabsChanged();

    void IDashboardWorkspaceHost.NotifyScopeMetadataChanged() => NotifyScopeMetadataChanged();

    void IDashboardWorkspaceHost.EnsureSelectedTabInCurrentScope() => EnsureSelectedTabInCurrentScope();

    void IDashboardWorkspaceHost.BeginTabCollectionNotificationSuppression()
        => BeginTabCollectionNotificationSuppression();

    void IDashboardWorkspaceHost.EndTabCollectionNotificationSuppression()
        => EndTabCollectionNotificationSuppression();

    Task IDashboardWorkspaceHost.OpenFilePathInScopeAsync(
        string filePath,
        string? scopeDashboardId,
        bool reloadIfLoadError,
        bool activateTab,
        bool deferVisibilityRefresh,
        CancellationToken ct)
        => OpenFilePathInScopeAsync(filePath, scopeDashboardId, reloadIfLoadError, activateTab, deferVisibilityRefresh, ct);

    LogTabViewModel? IDashboardWorkspaceHost.FindTabInScope(string filePath, string? scopeDashboardId)
        => FindTabInScope(filePath, scopeDashboardId);

    private void RunTabLifecycleMaintenanceOnUiThread()
    {
        if (IsShuttingDown)
            return;

        var app = Application.Current;
        if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
        {
            _ = app.Dispatcher.BeginInvoke(new Action(RunTabLifecycleMaintenance));
            return;
        }

        RunTabLifecycleMaintenance();
    }

    public void RunTabLifecycleMaintenance()
    {
        BeginTabCollectionNotificationSuppression();
        try
        {
            if (!_tabWorkspace.RunLifecycleMaintenance())
                return;

            ClearActiveDashboardWhenNoScopedTabsRemain();
            ClearActiveDashboardWhenNoTabsRemain();
        }
        finally
        {
            EndTabCollectionNotificationSuppression();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        BeginShutdown();
        Tabs.CollectionChanged -= Tabs_CollectionChanged;

        foreach (var tab in Tabs.ToList())
            tab.Dispose();

        _tabWorkspace.Dispose();
        _dashboardWorkspace.DetachGroupViewModels();
    }
}
