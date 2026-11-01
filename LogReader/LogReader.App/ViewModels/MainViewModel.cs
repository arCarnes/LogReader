namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reflection;
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
    internal const double CollapsedGroupsPanelWidth = 32;
    internal const double CollapsedSearchPanelHeight = 32;
    internal const double GroupsPanelSnapThreshold = 56;
    internal const double SearchPanelSnapThreshold = 56;
    private const double MinRememberedSearchPanelHeight = 36;
    private static readonly TimeSpan DefaultLifecycleSweepInterval = TimeSpan.FromSeconds(30);
    private readonly ILogGroupRepository _groupRepo;
    private readonly ISettingsRepository _settingsRepo;
    private readonly IFileTailService _tailService;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly ISettingsDialogService _settingsDialogService;
    private readonly IBulkOpenPathsDialogService _bulkOpenPathsDialogService;
    private readonly IDashboardTargetPickerDialogService _dashboardTargetPickerDialogService;
    private readonly IMcpHelpDialogService _mcpHelpDialogService;
    private readonly Func<ISettingsRepository, SettingsViewModel> _settingsViewModelFactory;
    private readonly ILogAppearanceService _logAppearanceService;
    private readonly ITabLifecycleScheduler _tabLifecycleScheduler;
    private readonly WorkspaceServices _workspaceServices;
    internal WorkspaceServices WorkspaceServices => _workspaceServices;
    private readonly LogFileCatalogService _fileCatalogService;
    private readonly TabWorkspaceService _tabWorkspace;
    private readonly DashboardActivationService _dashboardActivation;
    private readonly DashboardWorkspaceService _dashboardWorkspace;
    private readonly RuntimePersistedStateRecoveryExecutor _runtimeRecoveryExecutor;
    private readonly TabCollectionRefreshCoordinator _tabCollectionRefreshCoordinator = new();
    private readonly TabMemberRefreshScheduler _tabMemberRefreshScheduler;
    private readonly SearchFilterSharedOptions _searchFilterSharedOptions = new();
    private readonly IDisposable? _tabLifecycleRegistration;
    private IReadOnlyList<LogTabViewModel> _filteredTabsSnapshot = Array.Empty<LogTabViewModel>();

    private AppSettings _settings = new();
    private int _autoScrollSyncVersion;
    internal Task AutoScrollSyncTask { get; private set; } = Task.CompletedTask;
    private TailingActivityState _tailingActivityState;
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
    private bool _isSearchPanelOpen = true;

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
    private bool _isAdHocExpanded;

    [ObservableProperty]
    private string _dashboardLoadingStatusText = string.Empty;

    [ObservableProperty]
    private int _viewportRefreshVersion;

    private int _dashboardLoadDepth;
    private long _scopeNavigationRevision;
    private int _shutdownStarted;
    private bool _disposed;
    private Task _shutdownCleanupTask = Task.CompletedTask;
    private Task _disposeCleanupTask = Task.CompletedTask;
    internal Task ShutdownTask => Task.WhenAll(_workspaceServices.ShutdownTask, _tabMemberRefreshScheduler.ShutdownTask,
        SearchPanel.ShutdownTask, _shutdownCleanupTask, _disposeCleanupTask);
    private bool _isDashboardTreeRenameCommitPending;
    private LogGroupViewModel? _pendingDashboardTreeRenameGroup;

    internal bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) != 0;
    internal int DashboardLoadDepth
    {
        get => _dashboardLoadDepth;
        set => _dashboardLoadDepth = value;
    }

    public bool ShowFullPathsInDashboard => _settings.ShowFullPathsInDashboard;

    public IEnumerable<LogTabViewModel> FilteredTabs => _filteredTabsSnapshot;

    public bool IsAdHocScopeActive => string.IsNullOrEmpty(ActiveDashboardId);

    public bool IsLoadAffectingActionFrozen => IsDashboardLoading;

    public bool AreLoadAffectingActionsEnabled => !IsLoadAffectingActionFrozen;

    public bool AreDashboardNavigationActionsEnabled => !IsShuttingDown;

    public bool IsCurrentDashboardLoading => GetActiveDashboard()?.IsDashboardLoading == true;

    public bool IsCurrentScopeEmpty => _filteredTabsSnapshot.Count == 0;

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

    public string CurrentScopeSummaryText => $"Scope: {CurrentScopeLabel} ({_filteredTabsSnapshot.Count})";

    public string AppVersionText => GetAppVersion();

    public string AdHocScopeChipText => $"{GetAdHocScopeLabel()} ({GetAdHocTabs().Count})";

    public IReadOnlyList<LogTabViewModel> AdHocMemberTabs => GetAdHocTabs();

    public IReadOnlyList<GroupFileMemberViewModel> AdHocMemberFiles => AdHocMemberTabs
        .Select(tab => new GroupFileMemberViewModel(
            tab.FileId,
            tab.FileName,
            tab.FilePath,
            ShowFullPathsInDashboard,
            isActiveDisplayed: ReferenceEquals(tab, SelectedTab),
            fileSizeText: GroupFileMemberViewModel.CreateFileSizeText(tab),
            customDisplayName: tab.CustomDisplayName))
        .ToList();

    public bool CanExpandAdHoc => AdHocMemberTabs.Count > 0;

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
                return $"{_filteredTabsSnapshot.Count} of {total} tabs ({GetAdHocScopeLabel()})";

            return $"{_filteredTabsSnapshot.Count} of {total} tabs (Dashboard)";
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
        Func<ISettingsRepository, SettingsViewModel>? settingsViewModelFactory = null,
        IDashboardTargetPickerDialogService? dashboardTargetPickerDialogService = null,
        IMcpHelpDialogService? mcpHelpDialogService = null,
        IUiStateRepository? uiStateRepository = null)
        : this(fileRepo, groupRepo, settingsRepo, logReader, searchService, tailService, encodingDetectionService,
            enableLifecycleTimer, fileDialogService, messageBoxService, settingsDialogService, bulkOpenPathsDialogService,
            settingsViewModelFactory, persistedStateRecoveryCoordinator: null, workspaceFactory: null,
            logAppearanceService: null, tabLifecycleScheduler: null, fileCatalogService: null,
            tabWorkspace: null, dashboardWorkspace: null, dashboardActivation: null,
            dashboardTargetPickerDialogService, mcpHelpDialogService, uiDispatcher: null, uiStateRepository)
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
        IPersistedStateRecoveryCoordinator? persistedStateRecoveryCoordinator,
        IWorkspaceServicesFactory? workspaceFactory,
        ILogAppearanceService? logAppearanceService,
        ITabLifecycleScheduler? tabLifecycleScheduler,
        LogFileCatalogService? fileCatalogService,
        TabWorkspaceService? tabWorkspace,
        DashboardWorkspaceService? dashboardWorkspace,
        DashboardActivationService? dashboardActivation = null,
        IDashboardTargetPickerDialogService? dashboardTargetPickerDialogService = null,
        IMcpHelpDialogService? mcpHelpDialogService = null,
        IUiDispatcher? uiDispatcher = null,
        IUiStateRepository? uiStateRepository = null)
        : this(
            groupRepo,
            settingsRepo,
            searchService,
            tailService,
            encodingDetectionService,
            enableLifecycleTimer,
            MainViewModelCompositionBuilder.Create(
                fileRepo,
                groupRepo,
                settingsRepo,
                logReader,
                tailService,
                encodingDetectionService,
                fileDialogService,
                messageBoxService,
                settingsDialogService,
                bulkOpenPathsDialogService,
                dashboardTargetPickerDialogService,
                mcpHelpDialogService,
                settingsViewModelFactory,
                workspaceFactory,
                logAppearanceService,
                tabLifecycleScheduler,
                fileCatalogService,
                tabWorkspace,
                dashboardWorkspace,
                dashboardActivation,
                persistedStateRecoveryCoordinator,
                uiDispatcher,
                uiStateRepository))
    {
    }

    private MainViewModel(
        ILogGroupRepository groupRepo,
        ISettingsRepository settingsRepo,
        ISearchService searchService,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        bool enableLifecycleTimer,
        MainViewModelComposition composition)
    {
        _groupRepo = groupRepo;
        _settingsRepo = settingsRepo;
        _tailService = tailService;
        _encodingDetectionService = encodingDetectionService;
        _fileDialogService = composition.FileDialogService;
        _messageBoxService = composition.MessageBoxService;
        _settingsDialogService = composition.SettingsDialogService;
        _bulkOpenPathsDialogService = composition.BulkOpenPathsDialogService;
        _dashboardTargetPickerDialogService = composition.DashboardTargetPickerDialogService;
        _mcpHelpDialogService = composition.McpHelpDialogService;
        _settingsViewModelFactory = composition.SettingsViewModelFactory;
        _logAppearanceService = composition.LogAppearanceService;
        _tabLifecycleScheduler = composition.TabLifecycleScheduler;
        _workspaceServices = composition.WorkspaceFactory.Create(this, this);
        _fileCatalogService = _workspaceServices.Catalog;
        _tabWorkspace = _workspaceServices.Tabs;
        _dashboardActivation = _workspaceServices.Activation;
        _dashboardWorkspace = _workspaceServices.Dashboards;
        var priorRefresh = _dashboardActivation.QueueMemberRefresh;
        var priorDispatcher = _dashboardActivation.MembershipDispatcher;
        SearchPanelViewModel? searchPanel = null;
        FilterPanelViewModel? filterPanel = null;
        TabMemberRefreshScheduler? memberRefresh = null;
        IDisposable? registration = null;
        try
        {
            ConfigureUiState(composition.UiStateRepository, composition.UiDispatcher);
            _runtimeRecoveryExecutor = new RuntimePersistedStateRecoveryExecutor(
                composition.RecoveryCoordinator, _messageBoxService, RefreshRecoveredStoreStateAsync);
            _tabMemberRefreshScheduler = memberRefresh = new TabMemberRefreshScheduler(RunTabMemberRefreshAsync);
            _dashboardActivation.QueueMemberRefresh = QueueTabMemberRefreshRequest;
            _dashboardActivation.MembershipDispatcher = composition.UiDispatcher;
            SearchPanel = searchPanel = new SearchPanelViewModel(searchService, this, _searchFilterSharedOptions, composition.UiDispatcher);
            FilterPanel = filterPanel = new FilterPanelViewModel(searchService, this, _searchFilterSharedOptions);
            FilterPanel.FilterApplicabilityChanged += FilterPanel_FilterApplicabilityChanged;
            Tabs.CollectionChanged += Tabs_CollectionChanged;
            NotifyFilteredTabsChanged();

            // Construction barrier: graph, panels, persistence and host forwarding are valid.
            // Even a scheduler that invokes the callback synchronously sees a complete shell.
            if (enableLifecycleTimer)
            {
                _tabLifecycleRegistration = registration = _tabLifecycleScheduler.ScheduleRecurring(
                    DefaultLifecycleSweepInterval, DefaultLifecycleSweepInterval, RunTabLifecycleMaintenance);
                if (IsShuttingDown)
                {
                    var stoppedRegistration = registration;
                    registration = null;
                    _tabLifecycleRegistration = null;
                    stoppedRegistration.Dispose();
                }
            }
        }
        catch (Exception constructionError)
        {
            var cleanupErrors = new List<Exception>();
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception error) { cleanupErrors.Add(error); }
            }
            Cleanup(() => registration?.Dispose());
            Tabs.CollectionChanged -= Tabs_CollectionChanged;
            if (filterPanel != null) filterPanel.FilterApplicabilityChanged -= FilterPanel_FilterApplicabilityChanged;
            Cleanup(() => searchPanel?.Dispose());
            Cleanup(() => filterPanel?.Dispose());
            Cleanup(() => memberRefresh?.Shutdown());
            _dashboardWorkspace.ExpansionStateChanged -= DashboardExpansionStateChanged;
            Cleanup(() => _uiStatePersistence?.Dispose());
            _dashboardActivation.QueueMemberRefresh = priorRefresh;
            _dashboardActivation.MembershipDispatcher = priorDispatcher;
            Cleanup(_workspaceServices.AbortConstruction);
            if (cleanupErrors.Count > 0)
                throw new AggregateException("Shell construction failed and cleanup also reported errors.", new[] { constructionError }.Concat(cleanupErrors));
            throw;
        }
    }

    private IReadOnlyList<LogTabViewModel> BuildFilteredTabsSnapshot()
    {
        if (!string.IsNullOrEmpty(ActiveDashboardId) &&
            _dashboardActivation.TryGetDashboardEffectivePaths(ActiveDashboardId, out var dashboardEffectivePaths))
        {
            return _tabWorkspace.OrderTabsForDisplay(
                Tabs.Where(tab =>
                    string.Equals(tab.ScopeDashboardId, ActiveDashboardId, StringComparison.Ordinal) &&
                    dashboardEffectivePaths.Contains(tab.FilePath)));
        }

        if (string.IsNullOrEmpty(ActiveDashboardId) &&
            _dashboardActivation.TryGetAdHocEffectivePaths(out var adHocEffectivePaths))
        {
            return _tabWorkspace.OrderTabsForDisplay(
                Tabs.Where(tab => tab.IsAdHocScope && adHocEffectivePaths.Contains(tab.FilePath)));
        }

        if (string.IsNullOrEmpty(ActiveDashboardId))
            return _tabWorkspace.OrderTabsForDisplay(GetNormalAdHocTabs());

        var activeDashboard = GetActiveDashboard();
        if (activeDashboard == null)
            return _tabWorkspace.OrderTabsForDisplay(GetTabsForCurrentScope());

        var scopedTabs = GetTabsForCurrentScope();
        var memberFiles = activeDashboard.MemberFiles.ToList();
        var memberFileIds = activeDashboard.Model.FileIds.ToHashSet(StringComparer.Ordinal);
        if (memberFiles.Count > 0)
        {
            var memberPaths = memberFiles
                .Select(member => member.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            scopedTabs = scopedTabs
                .Where(tab => memberFileIds.Contains(tab.FileId) || memberPaths.Contains(tab.FilePath))
                .ToList();
        }
        else
        {
            scopedTabs = scopedTabs
                .Where(tab => memberFileIds.Contains(tab.FileId))
                .ToList();
        }

        return _tabWorkspace.OrderTabsForDashboardDisplay(scopedTabs, activeDashboard.Model.FileIds);
    }

    [RelayCommand]
    private async Task OpenFile()
    {
        if (ShouldIgnoreLoadAffectingAction())
            return;

        var result = _fileDialogService.ShowOpenFileDialog(
            new OpenFileDialogRequest(
                "Open Log File",
                "Log Files (*.log;*.txt)|*.log;*.txt|All Files (*.*)|*.*",
                Multiselect: true,
                InitialDirectory: GetDefaultOpenDirectory()));

        if (result.Accepted)
            await OpenFilePathsAsync(result.FileNames);
    }

    public async Task OpenFilePathAsync(
        string filePath,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default)
    {
        if (ShouldIgnoreLoadAffectingAction())
            return;

        await ExecuteRecoverableCommandAsync(async () =>
        {
            var activatedTab = await OpenFilePathCoreAsync(
                filePath,
                reloadIfLoadError,
                activateTab,
                deferVisibilityRefresh,
                ct);
            if (activateTab && activatedTab != null)
                EnsureTabVisibleInCurrentScope(activatedTab);
        });
    }

    internal async Task OpenFilePathsAsync(
        IEnumerable<string> filePaths,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default)
    {
        if (ShouldIgnoreLoadAffectingAction())
            return;

        await ExecuteRecoverableCommandAsync(() => OpenFilePathsCoreAsync(
            filePaths,
            reloadIfLoadError,
            activateTab,
            deferVisibilityRefresh,
            ct));
    }

    private async Task OpenFilePathsCoreAsync(
        IEnumerable<string> filePaths,
        bool reloadIfLoadError,
        bool activateTab,
        bool deferVisibilityRefresh,
        CancellationToken ct)
    {
        var paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
            return;

        LogTabViewModel? activatedTab = null;
        var navigationRevision = _scopeNavigationRevision;
        var selectionRevision = _dashboardMemberSelectionRevision;
        using (var loadingLease = BeginWorkspaceLoading(BuildOpenFilesLoadingStatus(processedCount: 0, paths.Count)))
        {
            for (var i = 0; i < paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                activatedTab = await OpenFilePathCoreAsync(
                    paths[i],
                    reloadIfLoadError,
                    activateTab && navigationRevision == _scopeNavigationRevision && selectionRevision == _dashboardMemberSelectionRevision,
                    deferVisibilityRefresh,
                    ct);
                loadingLease.SetStatus(BuildOpenFilesLoadingStatus(i + 1, paths.Count));
            }
        }

        if (activateTab && activatedTab != null && navigationRevision == _scopeNavigationRevision &&
            selectionRevision == _dashboardMemberSelectionRevision)
            EnsureTabVisibleInCurrentScope(activatedTab);
    }

    private async Task<LogTabViewModel?> OpenFilePathCoreAsync(
        string filePath,
        bool reloadIfLoadError,
        bool activateTab,
        bool deferVisibilityRefresh,
        CancellationToken ct)
    {
        var navigationRevision = _scopeNavigationRevision;
        var selectionRevision = _dashboardMemberSelectionRevision;
        var targetScopeDashboardId = await ResolveTargetScopeDashboardIdForOpenAsync(filePath);
        await OpenFilePathInScopeAsync(
            filePath,
            targetScopeDashboardId,
            reloadIfLoadError,
            activateTab: false,
            deferVisibilityRefresh,
            ct);

        if (!activateTab || IsShuttingDown || navigationRevision != _scopeNavigationRevision ||
            selectionRevision != _dashboardMemberSelectionRevision)
            return null;
        var tab = FindTabInScope(filePath, targetScopeDashboardId);
        if (tab != null)
            SelectedTab = tab;
        return tab;
    }

    private WorkspaceLoadingLease BeginWorkspaceLoading(string statusText)
    {
        DashboardLoadDepth++;
        IsDashboardLoading = true;
        DashboardLoadingStatusText = statusText;
        return new WorkspaceLoadingLease(this);
    }

    private void EndWorkspaceLoading()
    {
        DashboardLoadDepth = Math.Max(0, DashboardLoadDepth - 1);
        if (DashboardLoadDepth == 0)
            IsDashboardLoading = false;
    }

    private static string BuildOpenFilesLoadingStatus(int processedCount, int totalCount)
        => totalCount == 1
            ? "Opening file..."
            : $"Opening files ({processedCount}/{totalCount})...";

    private sealed class WorkspaceLoadingLease : IDisposable
    {
        private readonly MainViewModel _owner;
        private bool _disposed;

        public WorkspaceLoadingLease(MainViewModel owner)
        {
            _owner = owner;
        }

        public void SetStatus(string statusText)
        {
            if (!_disposed)
                _owner.DashboardLoadingStatusText = statusText;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner.EndWorkspaceLoading();
        }
    }

    [RelayCommand]
    private async Task CloseTab(LogTabViewModel? tab)
    {
        if (ShouldIgnoreLoadAffectingAction())
            return;

        BeginTabCollectionNotificationSuppression();
        try
        {
            await _tabWorkspace.CloseTabAsync(tab);
            ClearActiveDashboardWhenNoScopedTabsRemain();
            ClearActiveDashboardWhenNoTabsRemain();
        }
        finally
        {
            await EndTabCollectionNotificationSuppressionAsync();
        }
    }

    public async Task CloseAllTabsAsync()
    {
        if (ShouldIgnoreLoadAffectingAction())
            return;

        BeginTabCollectionNotificationSuppression();
        try
        {
            await _tabWorkspace.CloseAllTabsAsync();
            ClearActiveDashboardWhenNoScopedTabsRemain();
            ClearActiveDashboardWhenNoTabsRemain();
        }
        finally
        {
            await EndTabCollectionNotificationSuppressionAsync();
        }
    }

    public async Task CloseOtherTabsAsync(LogTabViewModel keepTab)
    {
        if (ShouldIgnoreLoadAffectingAction())
            return;

        BeginTabCollectionNotificationSuppression();
        try
        {
            await _tabWorkspace.CloseOtherTabsAsync(keepTab);
            ClearActiveDashboardWhenNoScopedTabsRemain();
            ClearActiveDashboardWhenNoTabsRemain();
        }
        finally
        {
            await EndTabCollectionNotificationSuppressionAsync();
        }
    }

    public async Task CloseAllButPinnedAsync()
    {
        if (ShouldIgnoreLoadAffectingAction())
            return;

        BeginTabCollectionNotificationSuppression();
        try
        {
            await _tabWorkspace.CloseAllButPinnedAsync();
            ClearActiveDashboardWhenNoScopedTabsRemain();
            ClearActiveDashboardWhenNoTabsRemain();
        }
        finally
        {
            await EndTabCollectionNotificationSuppressionAsync();
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
        var tabs = GetFilteredTabsSnapshot();
        if (tabs.Count == 0)
            return;

        if (SelectedTab == null)
        {
            SelectedTab = delta < 0 ? tabs[^1] : tabs[0];
            return;
        }

        var index = IndexOfTab(tabs, SelectedTab);
        if (index < 0)
        {
            SelectedTab = tabs[0];
            return;
        }

        var targetIndex = Math.Clamp(index + delta, 0, tabs.Count - 1);
        if (targetIndex != index)
            SelectedTab = tabs[targetIndex];
    }

    private static int IndexOfTab(IReadOnlyList<LogTabViewModel> tabs, LogTabViewModel tab)
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            if (ReferenceEquals(tabs[i], tab))
                return i;
        }

        return -1;
    }

    public void TogglePinTab(LogTabViewModel tab)
    {
        if (ShouldIgnoreLoadAffectingAction())
            return;

        _tabWorkspace.TogglePinTab(tab);
        NotifyFilteredTabsChanged();
    }

    [RelayCommand]
    private void ShowAdHocTabs()
    {
        ActivateAdHocScope();
    }

    [RelayCommand]
    private void ToggleAdHocExpanded()
    {
        if (!CanExpandAdHoc)
            return;

        IsAdHocExpanded = !IsAdHocExpanded;
    }

    internal void OpenAdHocMemberFile(LogTabViewModel? tab)
    {
        if (tab == null)
            return;

        ActivateAdHocScope();
        SelectedTab = tab;
    }

    internal async Task ClearAdHocTabsAsync()
    {
        if (ShouldIgnoreLoadAffectingAction())
            return;

        var adHocTabs = GetAdHocTabs().ToList();
        if (adHocTabs.Count == 0)
            return;

        BeginTabCollectionNotificationSuppression();
        try
        {
            foreach (var tab in adHocTabs)
                await _tabWorkspace.CloseTabAsync(tab);

            ClearActiveDashboardWhenNoTabsRemain();
        }
        finally
        {
            await EndTabCollectionNotificationSuppressionAsync();
        }
    }

    [RelayCommand]
    private Task ClearAdHocTabs()
        => ClearAdHocTabsAsync();

    [RelayCommand]
    private void ToggleGroupsPanel()
    {
        IsGroupsPanelOpen = !IsGroupsPanelOpen;
    }

    [RelayCommand]
    private void ToggleSearchPanel()
    {
        IsSearchPanelOpen = !IsSearchPanelOpen;
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
        if (width >= GroupsPanelSnapThreshold)
            GroupsPanelWidth = width;
    }

    public void RememberSearchPanelHeight(double height)
    {
        if (height >= MinRememberedSearchPanelHeight)
            SearchPanelHeight = height;
    }

    internal void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutdownCleanupTask = completion.Task;
        var errors = new List<Exception>();
        CaptureCleanup(_workspaceServices.BeginShutdown, errors);
        CaptureCleanup(_tabMemberRefreshScheduler.Shutdown, errors);
        CaptureCleanup(() => _tabLifecycleRegistration?.Dispose(), errors);
        FilterPanel.FilterApplicabilityChanged -= FilterPanel_FilterApplicabilityChanged;
        CaptureCleanup(SearchPanel.Dispose, errors);
        CaptureCleanup(FilterPanel.Dispose, errors);

        foreach (var tab in Tabs.ToList())
            CaptureCleanup(tab.BeginShutdown, errors);

        CaptureCleanup(_tailService.StopAll, errors);
        CompleteCleanup(completion, errors);
    }

    string? ILogWorkspaceContext.ActiveScopeDashboardId => ActiveDashboardId;

    bool ILogWorkspaceContext.IsDashboardLoading => IsDashboardLoading;

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

    LogFilterSession.FilterSnapshot? ILogWorkspaceContext.GetApplicableCurrentTabFilterSnapshot(SearchDataMode sourceMode)
        => FilterPanel.GetApplicableCurrentTabFilterSnapshot(sourceMode);

    LogFilterSession.FilterSnapshot? ILogWorkspaceContext.GetApplicableAllOpenTabsFilterSnapshot(string filePath, SearchDataMode sourceMode)
        => FilterPanel.GetApplicableAllOpenTabsFilterSnapshot(filePath, sourceMode);

    IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> ILogWorkspaceContext.GetApplicableAllOpenTabsFilterSnapshots(SearchDataMode sourceMode)
        => FilterPanel.GetApplicableAllOpenTabsFilterSnapshots(sourceMode);

    void ILogWorkspaceContext.UpdateRecentTabFilterSnapshot(string filePath, string? scopeDashboardId, LogFilterSession.FilterSnapshot? snapshot)
        => _tabWorkspace.UpdateRecentTabFilterSnapshot(filePath, scopeDashboardId, snapshot);

    private void FilterPanel_FilterApplicabilityChanged(object? sender, EventArgs e)
        => SearchPanel.RefreshFilterApplicabilityState();

    Task ILogWorkspaceContext.RunViewActionAsync(Func<Task> operation, string failureCaption)
        => RunViewActionAsync(operation, failureCaption);

    public void RunTabLifecycleMaintenance()
    {
        if (IsShuttingDown || IsDashboardLoading)
            return;

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
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disposeCleanupTask = completion.Task;
        var errors = new List<Exception>();
        BeginShutdown();
        Tabs.CollectionChanged -= Tabs_CollectionChanged;

        foreach (var tab in Tabs.ToList())
        {
            tab.PropertyChanged -= Tab_PropertyChanged;
            CaptureCleanup(tab.Dispose, errors);
        }

        _dashboardWorkspace.ExpansionStateChanged -= DashboardExpansionStateChanged;
        CaptureCleanup(_uiStatePersistence.Dispose, errors);
        CaptureCleanup(_workspaceServices.Dispose, errors);
        CompleteCleanup(completion, errors);
    }

    private static void CaptureCleanup(Action action, List<Exception> errors)
    {
        try { action(); }
        catch (Exception error) { errors.Add(error); }
    }

    private static void CompleteCleanup(TaskCompletionSource completion, List<Exception> errors)
    {
        if (errors.Count == 0) completion.TrySetResult();
        else
        {
            completion.TrySetException(errors);
            _ = completion.Task.Exception;
        }
    }

    private bool ShouldIgnoreLoadAffectingAction()
        => IsShuttingDown || IsLoadAffectingActionFrozen;

    private static string GetAppVersion()
    {
        var assembly = typeof(MainViewModel).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
            return assembly.GetName().Version?.ToString() ?? "unknown";

        var buildMetadataStart = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return buildMetadataStart < 0
            ? informationalVersion
            : informationalVersion[..buildMetadataStart];
    }
}
