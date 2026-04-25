namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class MainViewModel : ObservableObject, ILogWorkspaceContext, IDisposable
{
    private const double DefaultGroupsPanelWidth = 220;
    private const double DefaultSearchPanelHeight = 260;
    internal const double CollapsedGroupsPanelWidth = 32;
    internal const double GroupsPanelSnapThreshold = 56;
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
    private readonly Func<ISettingsRepository, SettingsViewModel> _settingsViewModelFactory;
    private readonly ILogAppearanceService _logAppearanceService;
    private readonly ITabLifecycleScheduler _tabLifecycleScheduler;
    private readonly LogFileCatalogService _fileCatalogService;
    private readonly TabWorkspaceService _tabWorkspace;
    private readonly DashboardActivationService _dashboardActivation;
    private readonly DashboardWorkspaceService _dashboardWorkspace;
    private readonly RuntimePersistedStateRecoveryExecutor _runtimeRecoveryExecutor;
    private readonly TabCollectionRefreshCoordinator _tabCollectionRefreshCoordinator = new();
    private readonly SearchFilterSharedOptions _searchFilterSharedOptions = new();
    private readonly IDisposable? _tabLifecycleRegistration;
    private IReadOnlyList<LogTabViewModel> _filteredTabsSnapshot = Array.Empty<LogTabViewModel>();

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
    private bool _isAdHocExpanded;

    [ObservableProperty]
    private string _dashboardLoadingStatusText = string.Empty;

    [ObservableProperty]
    private int _viewportRefreshVersion;

    private int _dashboardLoadDepth;
    private int _shutdownStarted;
    private bool _disposed;
    private bool _isDashboardTreeRenameCommitPending;
    private LogGroupViewModel? _pendingDashboardTreeRenameGroup;

    internal bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) != 0;
    internal int DashboardLoadDepth
    {
        get => _dashboardLoadDepth;
        set => _dashboardLoadDepth = value;
    }

    public bool ShowFullPathsInDashboard => _settings.ShowFullPathsInDashboard;

    public int DashboardLoadConcurrency => SettingsViewModel.NormalizeDashboardLoadConcurrency(_settings.DashboardLoadConcurrency);

    public IEnumerable<LogTabViewModel> FilteredTabs => _filteredTabsSnapshot;

    public bool IsAdHocScopeActive => string.IsNullOrEmpty(ActiveDashboardId);

    public bool IsLoadAffectingActionFrozen => IsDashboardLoading;

    public bool AreLoadAffectingActionsEnabled => !IsLoadAffectingActionFrozen;

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
            isSelected: ReferenceEquals(tab, SelectedTab),
            fileSizeText: GroupFileMemberViewModel.CreateFileSizeText(tab)))
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
        Func<ISettingsRepository, SettingsViewModel>? settingsViewModelFactory = null)
        : this(
            groupRepo,
            settingsRepo,
            searchService,
            tailService,
            encodingDetectionService,
            enableLifecycleTimer,
            CreateShellComposition(
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
                settingsViewModelFactory,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            new PersistedStateRecoveryCoordinator())
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
        MainViewModelReference? workspaceViewModelReference,
        ILogAppearanceService? logAppearanceService,
        ITabLifecycleScheduler? tabLifecycleScheduler,
        LogFileCatalogService? fileCatalogService,
        TabWorkspaceService? tabWorkspace,
        DashboardWorkspaceService? dashboardWorkspace,
        DashboardActivationService? dashboardActivation = null)
        : this(
            groupRepo,
            settingsRepo,
            searchService,
            tailService,
            encodingDetectionService,
            enableLifecycleTimer,
            CreateShellComposition(
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
                settingsViewModelFactory,
                workspaceViewModelReference,
                logAppearanceService,
                tabLifecycleScheduler,
                fileCatalogService,
                tabWorkspace,
                dashboardWorkspace,
                dashboardActivation),
            persistedStateRecoveryCoordinator ?? new PersistedStateRecoveryCoordinator())
    {
    }

    private MainViewModel(
        ILogGroupRepository groupRepo,
        ISettingsRepository settingsRepo,
        ISearchService searchService,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        bool enableLifecycleTimer,
        MainViewModelShellComposition shellComposition,
        IPersistedStateRecoveryCoordinator persistedStateRecoveryCoordinator)
    {
        _groupRepo = groupRepo;
        _settingsRepo = settingsRepo;
        _tailService = tailService;
        _encodingDetectionService = encodingDetectionService;
        _fileDialogService = shellComposition.FileDialogService;
        _messageBoxService = shellComposition.MessageBoxService;
        _settingsDialogService = shellComposition.SettingsDialogService;
        _bulkOpenPathsDialogService = shellComposition.BulkOpenPathsDialogService;
        _settingsViewModelFactory = shellComposition.SettingsViewModelFactory;
        _logAppearanceService = shellComposition.LogAppearanceService;
        _tabLifecycleScheduler = shellComposition.TabLifecycleScheduler;
        _fileCatalogService = shellComposition.FileCatalogService;
        _tabWorkspace = shellComposition.TabWorkspace;
        _dashboardActivation = shellComposition.DashboardActivation;
        _dashboardWorkspace = shellComposition.DashboardWorkspace;
        shellComposition.ViewModelReference.Attach(this);

        _runtimeRecoveryExecutor = new RuntimePersistedStateRecoveryExecutor(
            persistedStateRecoveryCoordinator,
            _messageBoxService,
            RefreshRecoveredStoreStateAsync);
        SearchPanel = new SearchPanelViewModel(searchService, this, _searchFilterSharedOptions);
        FilterPanel = new FilterPanelViewModel(searchService, this, _searchFilterSharedOptions);
        if (enableLifecycleTimer)
        {
            _tabLifecycleRegistration = _tabLifecycleScheduler.ScheduleRecurring(
                DefaultLifecycleSweepInterval,
                DefaultLifecycleSweepInterval,
                RunTabLifecycleMaintenance);
        }

        Tabs.CollectionChanged += Tabs_CollectionChanged;
        NotifyFilteredTabsChanged();
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

    private static MainViewModelShellComposition CreateShellComposition(
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo,
        ISettingsRepository settingsRepo,
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        IFileDialogService? fileDialogService,
        IMessageBoxService? messageBoxService,
        ISettingsDialogService? settingsDialogService,
        IBulkOpenPathsDialogService? bulkOpenPathsDialogService,
        Func<ISettingsRepository, SettingsViewModel>? settingsViewModelFactory,
        MainViewModelReference? workspaceViewModelReference,
        ILogAppearanceService? logAppearanceService,
        ITabLifecycleScheduler? tabLifecycleScheduler,
        LogFileCatalogService? fileCatalogService,
        TabWorkspaceService? tabWorkspace,
        DashboardWorkspaceService? dashboardWorkspace,
        DashboardActivationService? dashboardActivation)
    {
        var resolvedFileDialogService = fileDialogService ?? new FileDialogService();
        var resolvedMessageBoxService = messageBoxService ?? new MessageBoxService();
        var resolvedSettingsDialogService = settingsDialogService ?? new SettingsDialogService();
        var resolvedBulkOpenPathsDialogService = bulkOpenPathsDialogService ?? new BulkOpenPathsDialogService();
        var resolvedSettingsViewModelFactory = settingsViewModelFactory ?? (static repo => new SettingsViewModel(repo));
        var resolvedLogAppearanceService = logAppearanceService ?? new WpfLogAppearanceService();
        var resolvedTabLifecycleScheduler = tabLifecycleScheduler ?? new WpfTabLifecycleScheduler();
        var resolvedFileCatalogService = fileCatalogService ?? new LogFileCatalogService(fileRepo);
        var resolvedViewModelReference = workspaceViewModelReference ?? new MainViewModelReference();
        var resolvedTabWorkspace = tabWorkspace ?? new TabWorkspaceService(
            new TabWorkspaceHostAdapter(resolvedViewModelReference),
            fileRepo,
            logReader,
            tailService,
            encodingDetectionService,
            resolvedFileCatalogService);
        var resolvedDashboardHost = new DashboardWorkspaceHostAdapter(resolvedViewModelReference);
        var resolvedDashboardActivation = dashboardActivation ?? new DashboardActivationService(
            resolvedDashboardHost,
            fileRepo,
            groupRepo);
        var resolvedDashboardWorkspace = dashboardWorkspace ?? new DashboardWorkspaceService(
            resolvedDashboardHost,
            fileRepo,
            groupRepo,
            resolvedFileCatalogService,
            null,
            resolvedDashboardActivation);

        return new MainViewModelShellComposition(
            resolvedFileDialogService,
            resolvedMessageBoxService,
            resolvedSettingsDialogService,
            resolvedBulkOpenPathsDialogService,
            resolvedSettingsViewModelFactory,
            resolvedLogAppearanceService,
            resolvedTabLifecycleScheduler,
            resolvedFileCatalogService,
            resolvedViewModelReference,
            resolvedTabWorkspace,
            resolvedDashboardActivation,
            resolvedDashboardWorkspace);
    }

    private sealed record MainViewModelShellComposition(
        IFileDialogService FileDialogService,
        IMessageBoxService MessageBoxService,
        ISettingsDialogService SettingsDialogService,
        IBulkOpenPathsDialogService BulkOpenPathsDialogService,
        Func<ISettingsRepository, SettingsViewModel> SettingsViewModelFactory,
        ILogAppearanceService LogAppearanceService,
        ITabLifecycleScheduler TabLifecycleScheduler,
        LogFileCatalogService FileCatalogService,
        MainViewModelReference ViewModelReference,
        TabWorkspaceService TabWorkspace,
        DashboardActivationService DashboardActivation,
        DashboardWorkspaceService DashboardWorkspace);

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
        if (ShouldIgnoreLoadAffectingAction())
            return;

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
            EndTabCollectionNotificationSuppression();
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
            EndTabCollectionNotificationSuppression();
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
            EndTabCollectionNotificationSuppression();
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
            EndTabCollectionNotificationSuppression();
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

        _tabLifecycleRegistration?.Dispose();
        SearchPanel.Dispose();
        FilterPanel.Dispose();

        foreach (var tab in Tabs.ToList())
            tab.BeginShutdown();

        _tailService.StopAll();
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
        BeginShutdown();
        Tabs.CollectionChanged -= Tabs_CollectionChanged;

        foreach (var tab in Tabs.ToList())
        {
            tab.PropertyChanged -= Tab_PropertyChanged;
            tab.Dispose();
        }

        _tabWorkspace.Dispose();
        _dashboardWorkspace.DetachGroupViewModels();
    }

    private bool ShouldIgnoreLoadAffectingAction()
        => IsLoadAffectingActionFrozen;

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
