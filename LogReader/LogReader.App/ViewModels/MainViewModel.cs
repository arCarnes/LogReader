namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Models;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

public partial class MainViewModel : ObservableObject, ILogWorkspaceContext, ITabWorkspaceHost, IDashboardWorkspaceHost, IDisposable
{
    private const double DefaultGroupsPanelWidth = 220;
    private const double DefaultSearchPanelWidth = 350;
    private const double MinRememberedPanelWidth = 36;
    private static readonly TimeSpan DefaultLifecycleSweepInterval = TimeSpan.FromSeconds(30);
    private readonly ILogGroupRepository _groupRepo;
    private readonly ISettingsRepository _settingsRepo;
    private readonly IFileTailService _tailService;
    private readonly ILogTimestampNavigationService _timestampNavigationService;
    private readonly IFileDialogService _fileDialogService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly ISettingsDialogService _settingsDialogService;
    private readonly IBulkOpenPathsDialogService _bulkOpenPathsDialogService;
    private readonly IPatternManagerDialogService _patternManagerDialogService;
    private readonly IReplacementPatternRepository _patternRepo;
    private readonly Func<ISettingsRepository, SettingsViewModel> _settingsViewModelFactory;
    private readonly TabWorkspaceService _tabWorkspace;
    private readonly DashboardWorkspaceService _dashboardWorkspace;
    private readonly DashboardScopeService _dashboardScope = new();
    private readonly System.Threading.Timer? _tabLifecycleTimer;

    private AppSettings _settings = new();
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
    private bool _isSearchPanelOpen = true;

    [ObservableProperty]
    private double _groupsPanelWidth = DefaultGroupsPanelWidth;

    [ObservableProperty]
    private double _searchPanelWidth = DefaultSearchPanelWidth;

    [ObservableProperty]
    private bool _globalAutoScrollEnabled = true;

    [ObservableProperty]
    private string _dashboardTreeFilter = string.Empty;

    [ObservableProperty]
    private bool _isDashboardLoading;

    [ObservableProperty]
    private string _dashboardLoadingStatusText = string.Empty;

    private int _dashboardLoadDepth;
    private int _tabCollectionNotificationSuppressionDepth;
    private bool _tabCollectionChangePending;
    private readonly Dictionary<string, string> _pendingMemberRefreshFilePaths = new(StringComparer.Ordinal);
    private bool _pendingFullMemberRefresh;
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
                    Tabs.Where(tab => dashboardEffectivePaths.Contains(tab.FilePath)));
            }

            if (string.IsNullOrEmpty(ActiveDashboardId) &&
                _dashboardWorkspace.TryGetAdHocEffectivePaths(out var adHocEffectivePaths))
            {
                return _tabWorkspace.OrderTabsForDisplay(
                    Tabs.Where(tab => adHocEffectivePaths.Contains(tab.FilePath)));
            }

            if (string.IsNullOrEmpty(ActiveDashboardId))
                return _tabWorkspace.OrderTabsForDisplay(GetNormalAdHocTabs());

            return _dashboardScope.GetFilteredTabs(
                Tabs,
                Groups,
                ActiveDashboardId,
                _dashboardWorkspace.ResolveFileIds,
                _tabWorkspace.OrderTabsForDisplay);
        }
    }

    public bool IsAdHocScopeActive => string.IsNullOrEmpty(ActiveDashboardId);

    public bool IsCurrentScopeEmpty => !FilteredTabs.Any();

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

    public string CurrentScopeSummaryText
    {
        get
        {
            return $"Scope: {CurrentScopeLabel} ({FilteredTabs.Count()})";
        }
    }

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
        ILogTimestampNavigationService timestampNavigationService,
        bool enableLifecycleTimer = true,
        IFileDialogService? fileDialogService = null,
        IMessageBoxService? messageBoxService = null,
        ISettingsDialogService? settingsDialogService = null,
        IBulkOpenPathsDialogService? bulkOpenPathsDialogService = null,
        IPatternManagerDialogService? patternManagerDialogService = null,
        IReplacementPatternRepository? patternRepo = null,
        Func<ISettingsRepository, SettingsViewModel>? settingsViewModelFactory = null)
    {
        _groupRepo = groupRepo;
        _settingsRepo = settingsRepo;
        _tailService = tailService;
        _timestampNavigationService = timestampNavigationService;
        _fileDialogService = fileDialogService ?? new FileDialogService();
        _messageBoxService = messageBoxService ?? new MessageBoxService();
        _settingsDialogService = settingsDialogService ?? new SettingsDialogService();
        _bulkOpenPathsDialogService = bulkOpenPathsDialogService ?? new BulkOpenPathsDialogService();
        _patternManagerDialogService = patternManagerDialogService ?? new PatternManagerDialogService();
        _patternRepo = patternRepo ?? new JsonReplacementPatternRepository();
        _settingsViewModelFactory = settingsViewModelFactory ?? (static repo => new SettingsViewModel(repo));
        _tabWorkspace = new TabWorkspaceService(
            this,
            fileRepo,
            logReader,
            tailService,
            encodingDetectionService);
        _dashboardWorkspace = new DashboardWorkspaceService(this, fileRepo, groupRepo);
        SearchPanel = new SearchPanelViewModel(searchService, this);
        FilterPanel = new FilterPanelViewModel(searchService, this);
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

    public async Task InitializeAsync()
    {
        _settings = await _settingsRepo.LoadAsync();
        ApplyLogFontResource(_settings);

        var groups = await _groupRepo.GetAllAsync();
        _dashboardWorkspace.RebuildGroupsCollection(groups);

        await _dashboardWorkspace.RefreshAllMemberFilesAsync();
        NotifyFilteredTabsChanged();
        _dashboardWorkspace.ApplyDashboardTreeFilter();
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
            {
                await OpenFilePathAsync(file);
            }
        }
    }

    public async Task OpenFilePathAsync(
        string filePath,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default)
    {
        await _tabWorkspace.OpenFilePathAsync(
            filePath,
            _settings,
            reloadIfLoadError,
            activateTab,
            deferVisibilityRefresh,
            ct);

        if (!activateTab)
            return;

        var tab = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (tab != null)
            EnsureTabVisibleInCurrentScope(tab);
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

    private void ClearActiveDashboardWhenNoTabsRemain()
    {
        if (Tabs.Count > 0)
            return;

        if (string.IsNullOrEmpty(ActiveDashboardId) && Groups.All(g => !g.IsSelected))
            return;

        ActivateAdHocScope();
    }

    private void ClearActiveDashboardWhenNoScopedTabsRemain()
    {
        if (string.IsNullOrEmpty(ActiveDashboardId))
            return;

        if (FilteredTabs.Any())
            return;

        ActivateAdHocScope();
    }

    internal void EnsureSelectedTabInCurrentScope()
    {
        var scopedTabs = FilteredTabs.ToList();
        if (scopedTabs.Count == 0)
        {
            SelectedTab = null;
            return;
        }

        if (SelectedTab == null || !scopedTabs.Contains(SelectedTab))
            SelectedTab = scopedTabs[0];
    }

    internal void EnsureTabVisibleInCurrentScope(LogTabViewModel tab)
    {
        if (FilteredTabs.Contains(tab))
            return;

        var modifierDashboardId = _dashboardWorkspace.FindDashboardForModifierPath(tab.FilePath);
        if (!string.IsNullOrEmpty(modifierDashboardId))
        {
            var modifierDashboard = Groups.FirstOrDefault(group => string.Equals(group.Id, modifierDashboardId, StringComparison.Ordinal));
            if (modifierDashboard != null)
            {
                _dashboardWorkspace.CancelDashboardLoad();
                _dashboardScope.SelectDashboard(Groups, modifierDashboard);
                ActiveDashboardId = modifierDashboard.Id;
                NotifyFilteredTabsChanged();
                return;
            }
        }

        if (_dashboardWorkspace.IsAdHocModifierPath(tab.FilePath))
        {
            ActivateAdHocScope();
            return;
        }

        var containingGroup = _dashboardScope.FindContainingDashboard(Groups, tab.FileId);
        if (containingGroup != null)
        {
            _dashboardWorkspace.CancelDashboardLoad();
            _dashboardScope.SelectDashboard(Groups, containingGroup);
            ActiveDashboardId = containingGroup.Id;
            NotifyFilteredTabsChanged();
            return;
        }

        ActivateAdHocScope();
    }

    // ── Group commands ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CreateGroup()
    {
        await _dashboardWorkspace.CreateGroupAsync(LogGroupKind.Dashboard);
    }

    [RelayCommand]
    private async Task CreateContainerGroup()
    {
        await _dashboardWorkspace.CreateGroupAsync(LogGroupKind.Branch);
    }

    public async Task<bool> CreateChildGroupAsync(LogGroupViewModel parent, LogGroupKind kind = LogGroupKind.Dashboard)
    {
        return await _dashboardWorkspace.CreateChildGroupAsync(parent, kind);
    }

    [RelayCommand]
    private async Task DeleteGroup(LogGroupViewModel? groupVm)
    {
        await _dashboardWorkspace.DeleteGroupAsync(groupVm);
    }

    [RelayCommand]
    private async Task ExportView()
    {
        await TryExportCurrentViewAsync();
    }

    [RelayCommand]
    private async Task ImportView()
    {
        var result = _fileDialogService.ShowOpenFileDialog(CreateImportViewDialogRequest());
        if (result.Accepted && result.FileNames.Count > 0)
        {
            ViewExport? export;
            try
            {
                export = await _groupRepo.ImportViewAsync(result.FileNames[0]);
            }
            catch (InvalidDataException ex)
            {
                _messageBoxService.Show(
                    ex.Message,
                    "Import Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
            catch (IOException ex)
            {
                _messageBoxService.Show(
                    $"Could not read the selected view file: {ex.Message}",
                    "Import Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (export == null) return;
            if (!await ConfirmImportViewReplacementAsync())
                return;

            await _dashboardWorkspace.ApplyImportedViewAsync(export);
        }
    }

    private async Task<bool> TryExportCurrentViewAsync()
    {
        var result = _fileDialogService.ShowSaveFileDialog(CreateExportViewDialogRequest());
        if (!result.Accepted || string.IsNullOrWhiteSpace(result.FileName))
            return false;

        await _dashboardWorkspace.ExportViewAsync(result.FileName);
        return true;
    }

    private async Task<bool> ConfirmImportViewReplacementAsync()
    {
        if (Groups.Count == 0)
            return true;

        var result = _messageBoxService.Show(
            "Importing a view will replace your current dashboard view. Do you want to export it first?",
            "Export Current View?",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        return result switch
        {
            MessageBoxResult.Yes => await TryExportCurrentViewAsync(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private SaveFileDialogRequest CreateExportViewDialogRequest()
    {
        return new SaveFileDialogRequest(
            "Export View",
            "LogReader View (*.json)|*.json",
            ".json",
            AddExtension: true,
            InitialDirectory: GetViewsDirectory(),
            FileName: CreateDefaultViewExportFileName());
    }

    private OpenFileDialogRequest CreateImportViewDialogRequest()
    {
        return new OpenFileDialogRequest(
            "Import View",
            "LogReader View (*.json)|*.json",
            InitialDirectory: GetViewsDirectory());
    }

    private static string GetViewsDirectory() => AppPaths.EnsureDirectory(AppPaths.ViewsDirectory);

    private static string CreateDefaultViewExportFileName() => $"logreader-view-{DateTime.Now:yyyy-MM-dd-HHmmss}.json";

    internal Task ApplyImportedViewAsync(ViewExport export)
        => _dashboardWorkspace.ApplyImportedViewAsync(export);

    public async Task AddFilesToDashboardAsync(LogGroupViewModel groupVm)
    {
        if (!groupVm.CanManageFiles)
            return;

        var result = _fileDialogService.ShowOpenFileDialog(
            new OpenFileDialogRequest(
                "Add Files to Dashboard",
                "Log Files (*.log;*.txt)|*.log;*.txt|All Files (*.*)|*.*",
                Multiselect: true,
                InitialDirectory: GetDefaultOpenDirectory()));

        if (!result.Accepted || result.FileNames.Count == 0)
            return;

        await _dashboardWorkspace.AddFilesToDashboardAsync(groupVm, result.FileNames);
    }

    public async Task BulkAddFilesToDashboardAsync(LogGroupViewModel groupVm)
    {
        if (!groupVm.CanManageFiles)
            return;

        var result = _bulkOpenPathsDialogService.ShowDialog(
            new BulkOpenPathsDialogRequest(BulkOpenPathsScope.Dashboard, groupVm.Name));
        if (!result.Accepted)
            return;

        var filePaths = DashboardWorkspaceService.ParseBulkFilePaths(result.PathsText);
        if (filePaths.Count == 0)
            return;

        await _dashboardWorkspace.AddFilesToDashboardAsync(groupVm, filePaths);
    }

    [RelayCommand]
    private async Task BulkOpenAdHocFiles()
    {
        var result = _bulkOpenPathsDialogService.ShowDialog(
            new BulkOpenPathsDialogRequest(BulkOpenPathsScope.AdHoc));
        if (!result.Accepted)
            return;

        var filePaths = DashboardWorkspaceService.ParseBulkFilePaths(result.PathsText);
        if (filePaths.Count == 0)
            return;

        foreach (var filePath in filePaths)
            await OpenFilePathAsync(filePath);
    }

    [RelayCommand]
    private async Task BulkAddFilesToActiveDashboard()
    {
        var activeDashboard = GetActiveDashboard();
        if (activeDashboard == null)
            return;

        await BulkAddFilesToDashboardAsync(activeDashboard);
    }

    public async Task RemoveFileFromDashboardAsync(LogGroupViewModel groupVm, string fileId)
    {
        await _dashboardWorkspace.RemoveFileFromDashboardAsync(groupVm, fileId);
    }

    public async Task MoveGroupUpAsync(LogGroupViewModel group)
    {
        await _dashboardWorkspace.MoveGroupUpAsync(group);
    }

    public async Task MoveGroupDownAsync(LogGroupViewModel group)
    {
        await _dashboardWorkspace.MoveGroupDownAsync(group);
    }

    public bool CanMoveGroupTo(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
        => _dashboardWorkspace.CanMoveGroupTo(source, target, placement);

    public async Task MoveGroupToAsync(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
        => await _dashboardWorkspace.MoveGroupToAsync(source, target, placement);

    public void ToggleGroupSelection(LogGroupViewModel group)
    {
        var previousActiveDashboardId = ActiveDashboardId;
        var nextActiveDashboardId = _dashboardScope.ToggleGroupSelection(Groups, previousActiveDashboardId, group);
        if (!string.Equals(previousActiveDashboardId, nextActiveDashboardId, StringComparison.Ordinal))
            _dashboardWorkspace.CancelDashboardLoad();

        ActiveDashboardId = nextActiveDashboardId;
        NotifyFilteredTabsChanged();
    }

    public async Task OpenGroupFilesAsync(LogGroupViewModel group)
        => await _dashboardWorkspace.OpenGroupFilesAsync(group);

    private void Tabs_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (IsShuttingDown)
            return;

        if (_tabCollectionNotificationSuppressionDepth > 0)
        {
            _tabCollectionChangePending = true;
            QueuePendingMemberRefresh(e);
            return;
        }

        RefreshMemberFilesForTabCollectionChange(e);
        NotifyFilteredTabsChanged();
    }

    internal void BeginTabCollectionNotificationSuppression()
    {
        _tabCollectionNotificationSuppressionDepth++;
    }

    internal void EndTabCollectionNotificationSuppression()
    {
        _tabCollectionNotificationSuppressionDepth = Math.Max(0, _tabCollectionNotificationSuppressionDepth - 1);
        if (_tabCollectionNotificationSuppressionDepth > 0 || !_tabCollectionChangePending)
            return;

        _tabCollectionChangePending = false;
        FlushPendingMemberRefresh();
        NotifyFilteredTabsChanged();
    }

    private void RefreshMemberFilesForTabCollectionChange(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_dashboardWorkspace.HasActiveModifiers || RequiresFullMemberRefresh(e))
        {
            _ = _dashboardWorkspace.RefreshAllMemberFilesAsync();
            return;
        }

        var changedFilePaths = CollectChangedTabFilePaths(e.NewItems, e.OldItems);
        if (changedFilePaths.Count > 0)
            _ = _dashboardWorkspace.RefreshMemberFilesForFileIdsAsync(changedFilePaths);
    }

    private void QueuePendingMemberRefresh(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_pendingFullMemberRefresh || _dashboardWorkspace.HasActiveModifiers || RequiresFullMemberRefresh(e))
        {
            _pendingFullMemberRefresh = true;
            _pendingMemberRefreshFilePaths.Clear();
            return;
        }

        MergePendingMemberRefreshFilePaths(e.NewItems);
        MergePendingMemberRefreshFilePaths(e.OldItems);
    }

    private void FlushPendingMemberRefresh()
    {
        if (_pendingFullMemberRefresh)
        {
            _pendingFullMemberRefresh = false;
            _pendingMemberRefreshFilePaths.Clear();
            _ = _dashboardWorkspace.RefreshAllMemberFilesAsync();
            return;
        }

        if (_pendingMemberRefreshFilePaths.Count == 0)
            return;

        var changedFilePaths = _pendingMemberRefreshFilePaths.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        _pendingMemberRefreshFilePaths.Clear();
        _ = _dashboardWorkspace.RefreshMemberFilesForFileIdsAsync(changedFilePaths);
    }

    private static bool RequiresFullMemberRefresh(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Reset
            or System.Collections.Specialized.NotifyCollectionChangedAction.Move;

    private static Dictionary<string, string> CollectChangedTabFilePaths(
        System.Collections.IList? newItems,
        System.Collections.IList? oldItems)
    {
        var changedFilePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        AddChangedTabFilePaths(changedFilePaths, newItems);
        AddChangedTabFilePaths(changedFilePaths, oldItems);
        return changedFilePaths;
    }

    private void MergePendingMemberRefreshFilePaths(System.Collections.IList? items)
        => AddChangedTabFilePaths(_pendingMemberRefreshFilePaths, items);

    private static void AddChangedTabFilePaths(
        IDictionary<string, string> destination,
        System.Collections.IList? items)
    {
        if (items == null)
            return;

        foreach (var item in items.OfType<LogTabViewModel>())
            destination[item.FileId] = item.FilePath;
    }

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
        var anyOpen = IsGroupsPanelOpen || IsSearchPanelOpen;
        IsGroupsPanelOpen = !anyOpen;
        IsSearchPanelOpen = !anyOpen;
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
        if (width >= MinRememberedPanelWidth)
            GroupsPanelWidth = width;
    }

    public void RememberSearchPanelWidth(double width)
    {
        if (width >= MinRememberedPanelWidth)
            SearchPanelWidth = width;
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

    public async Task OpenSettingsAsync(Window? owner)
    {
        var settingsVm = _settingsViewModelFactory(_settingsRepo);
        await settingsVm.LoadAsync();

        if (_settingsDialogService.ShowDialog(settingsVm, owner))
        {
            await settingsVm.SaveAsync();
            _settings = await _settingsRepo.LoadAsync();
            ApplyLogFontResource(_settings);
            await _dashboardWorkspace.RefreshAllMemberFilesAsync();
            foreach (var tab in Tabs)
            {
                tab.UpdateSettings(_settings);
                _ = tab.RefreshViewportAsync();
                if (tab.IsVisible)
                    tab.OnBecameVisible();
                else
                    tab.OnBecameHidden();
            }
        }
    }

    public async Task OpenPatternManagerAsync(Window? owner)
    {
        var vm = new PatternManagerViewModel(_patternRepo, messageBoxService: _messageBoxService);
        try
        {
            await vm.LoadAsync();
        }
        catch (InvalidDataException ex)
        {
            ShowMessage(
                owner,
                ex.Message,
                "Date Rolling Patterns Unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        catch (IOException ex)
        {
            ShowMessage(
                owner,
                $"Could not load date rolling patterns: {ex.Message}",
                "Date Rolling Patterns Unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowMessage(
                owner,
                $"Could not load date rolling patterns: {ex.Message}",
                "Date Rolling Patterns Unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        _patternManagerDialogService.ShowDialog(vm, owner);
    }

    public async Task<IReadOnlyList<ReplacementPattern>> LoadReplacementPatternsAsync()
        => await _patternRepo.LoadAsync();

    public bool HasDashboardModifier(LogGroupViewModel group)
        => _dashboardWorkspace.HasDashboardModifier(group.Id);

    public bool HasAdHocModifier()
        => _dashboardWorkspace.HasAdHocModifier();

    public async Task ApplyDashboardModifierAsync(LogGroupViewModel group, int daysBack, ReplacementPattern pattern)
    {
        _dashboardScope.SelectDashboard(Groups, group);
        ActiveDashboardId = group.Id;
        await _dashboardWorkspace.SetDashboardModifierAsync(group, daysBack, pattern);
        NotifyFilteredTabsChanged();
        await OpenGroupFilesAsync(group);
    }

    public async Task ClearDashboardModifierAsync(LogGroupViewModel group)
    {
        var wasActiveScope = string.Equals(ActiveDashboardId, group.Id, StringComparison.Ordinal);
        await _dashboardWorkspace.ClearDashboardModifierAsync(group);
        NotifyFilteredTabsChanged();
        if (wasActiveScope)
            await OpenGroupFilesAsync(group);
    }

    public async Task ApplyAdHocModifierAsync(int daysBack, ReplacementPattern pattern)
    {
        ActivateAdHocScope();
        await _dashboardWorkspace.SetAdHocModifierAsync(daysBack, pattern);
        NotifyFilteredTabsChanged();
        if (_dashboardWorkspace.TryGetAdHocEffectivePaths(out var effectivePaths))
            await OpenPathsInCurrentScopeAsync(effectivePaths);
    }

    public async Task ClearAdHocModifierAsync()
    {
        var basePaths = _dashboardWorkspace.GetAdHocBasePathsSnapshot();
        var wasAdHocScope = IsAdHocScopeActive;
        await _dashboardWorkspace.ClearAdHocModifierAsync();
        NotifyFilteredTabsChanged();
        if (wasAdHocScope)
            await OpenPathsInCurrentScopeAsync(basePaths);
    }

    public static string FormatModifierPatternLabel(int daysBack, ReplacementPattern pattern)
    {
        var replacePreview = ResolveModifierReplacePreview(daysBack, pattern);
        if (string.IsNullOrWhiteSpace(pattern.Name))
            return $"{pattern.FindPattern} -> {replacePreview}";

        return $"{pattern.Name} ({pattern.FindPattern} -> {replacePreview})";
    }

    public static string FormatModifierActionLabel(int daysBack, ReplacementPattern pattern)
        => $"T-{daysBack} ({pattern.FindPattern} -> {ResolveModifierReplacePreview(daysBack, pattern)})";

    private static string ResolveModifierReplacePreview(int daysBack, ReplacementPattern pattern)
    {
        var targetDate = DateTime.Today.AddDays(-daysBack);
        if (ReplacementTokenParser.TryExpand(pattern.ReplacePattern, targetDate, out var expanded, out _))
            return expanded;

        return ReplacementTokenParser.DescribeTokens(pattern.ReplacePattern);
    }

    private static void ApplyLogFontResource(AppSettings settings)
    {
        if (Application.Current == null)
            return;

        var fontName = string.IsNullOrWhiteSpace(settings.LogFontFamily)
            ? "Consolas"
            : settings.LogFontFamily;
        Application.Current.Resources["LogFontFamilyResource"] = new FontFamily(fontName);
    }

    public IReadOnlyList<LogTabViewModel> GetAllTabs() => Tabs;

    internal IReadOnlyDictionary<string, long> TabOpenOrder => _tabWorkspace.OpenOrderSnapshot;

    internal IReadOnlyDictionary<string, long> TabPinOrder => _tabWorkspace.PinOrderSnapshot;

    internal IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot() => FilteredTabs.ToList();

    public Task<IReadOnlyList<string>> GetGroupFilePathsAsync(string groupId)
        => _dashboardWorkspace.GetGroupFilePathsAsync(groupId);

    public async Task NavigateToLineAsync(string filePath, long lineNumber, bool disableAutoScroll = false)
    {
        var tab = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (tab == null)
        {
            await OpenFilePathAsync(filePath);
            tab = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        }
        if (tab == null) return;

        if (!FilteredTabs.Contains(tab))
            tab.SetNavigateTargetLine((int)lineNumber);

        EnsureTabVisibleInCurrentScope(tab);

        if (disableAutoScroll)
            tab.AutoScrollEnabled = false;

        SelectedTab = tab;
        await tab.NavigateToLineAsync((int)lineNumber);
    }

    private string? GetDefaultOpenDirectory()
        => !string.IsNullOrWhiteSpace(_settings.DefaultOpenDirectory) &&
           Directory.Exists(_settings.DefaultOpenDirectory)
            ? _settings.DefaultOpenDirectory
            : null;

    public async Task<GoToCommandResult> NavigateToLineAsync(string lineNumberText)
    {
        if (SelectedTab == null)
            return GoToCommandResult.Failure("Select a file tab before using Go to line.");

        if (!long.TryParse(lineNumberText?.Trim(), out var lineNumber) || lineNumber <= 0)
            return GoToCommandResult.Failure("Invalid line number. Enter a whole number greater than 0.");

        var tab = SelectedTab;
        if (tab.TotalLines > 0 && lineNumber > tab.TotalLines)
            lineNumber = tab.TotalLines;

        try
        {
            await NavigateToLineAsync(tab.FilePath, lineNumber);
            var status = $"Navigated to line {lineNumber:N0}.";
            tab.StatusText = status;
            return GoToCommandResult.Success();
        }
        catch (Exception ex)
        {
            var message = $"Go to line error: {ex.Message}";
            tab.StatusText = message;
            return GoToCommandResult.Failure(message);
        }
    }

    public async Task<GoToCommandResult> NavigateToTimestampAsync(string timestampText)
    {
        if (SelectedTab == null)
            return GoToCommandResult.Failure("Select a file tab before using Go to timestamp.");

        if (!TimestampParser.TryParseInput(timestampText, out var targetTimestamp))
            return GoToCommandResult.Failure("Invalid timestamp. Use ISO-8601, yyyy-MM-dd HH:mm:ss, or HH:mm:ss.fff.");

        var tab = SelectedTab;
        try
        {
            var result = await _timestampNavigationService.FindNearestLineAsync(
                tab.FilePath,
                targetTimestamp,
                tab.EffectiveEncoding);

            if (!result.HasMatch)
            {
                tab.StatusText = result.StatusMessage;
                return GoToCommandResult.Failure(result.StatusMessage);
            }

            await NavigateToLineAsync(tab.FilePath, result.LineNumber);
            tab.StatusText = result.StatusMessage;
            return GoToCommandResult.Success();
        }
        catch (Exception ex)
        {
            var message = $"Go to timestamp error: {ex.Message}";
            tab.StatusText = message;
            return GoToCommandResult.Failure(message);
        }
    }

    private void ShowMessage(
        Window? owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        if (owner == null)
        {
            _messageBoxService.Show(message, caption, buttons, image);
            return;
        }

        _messageBoxService.Show(owner, message, caption, buttons, image);
    }

    // ── Tree building ─────────────────────────────────────────────────────────

    // ── File ID resolution ────────────────────────────────────────────────────

    public HashSet<string> ResolveFileIds(LogGroupViewModel group)
        => _dashboardWorkspace.ResolveFileIds(group);

    private IReadOnlyList<LogTabViewModel> GetAdHocTabs()
    {
        if (_dashboardWorkspace.TryGetAdHocEffectivePaths(out var adHocEffectivePaths))
        {
            return _tabWorkspace.OrderTabsForDisplay(
                Tabs.Where(tab => adHocEffectivePaths.Contains(tab.FilePath)));
        }

        return _tabWorkspace.OrderTabsForDisplay(GetNormalAdHocTabs());
    }

    private IReadOnlyList<LogTabViewModel> GetNormalAdHocTabs()
        => _dashboardScope.GetAdHocTabs(Tabs, Groups)
            .Where(tab => !_dashboardWorkspace.IsManagedByActiveModifier(tab.FilePath))
            .ToList();

    private string GetAdHocScopeLabel()
    {
        var modifierLabel = _dashboardWorkspace.GetAdHocModifierLabel();
        return string.IsNullOrWhiteSpace(modifierLabel)
            ? "Ad Hoc"
            : $"Ad Hoc [{modifierLabel}]";
    }

    private async Task OpenPathsInCurrentScopeAsync(IEnumerable<string> filePaths)
    {
        var paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
            return;

        BeginTabCollectionNotificationSuppression();
        try
        {
            foreach (var filePath in paths)
            {
                await OpenFilePathAsync(
                    filePath,
                    reloadIfLoadError: true,
                    activateTab: false,
                    deferVisibilityRefresh: true);
            }
        }
        finally
        {
            EndTabCollectionNotificationSuppression();
        }

        EnsureSelectedTabInCurrentScope();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal void NotifyFilteredTabsChanged()
    {
        var filteredTabs = GetFilteredTabsSnapshot();
        _tabWorkspace.UpdateTabVisibilityStates(filteredTabs);
        OnPropertyChanged(nameof(FilteredTabs));
        OnPropertyChanged(nameof(IsCurrentScopeEmpty));
        NotifyScopeMetadataChanged();

        if (SelectedTab != null && !filteredTabs.Contains(SelectedTab))
            SelectedTab = filteredTabs.FirstOrDefault();
        else if (SelectedTab == null && filteredTabs.Count > 0)
            SelectedTab = filteredTabs[0];
    }

    internal void NotifyScopeMetadataChanged()
    {
        OnPropertyChanged(nameof(CurrentScopeLabel));
        OnPropertyChanged(nameof(CurrentScopeSummaryText));
        OnPropertyChanged(nameof(AdHocScopeChipText));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(TabCountText));
    }

    internal void ActivateAdHocScope()
    {
        _dashboardWorkspace.CancelDashboardLoad();
        ActiveDashboardId = null;
        _dashboardScope.ClearSelection(Groups);
        NotifyFilteredTabsChanged();
    }

    partial void OnActiveDashboardIdChanged(string? value)
    {
        OnPropertyChanged(nameof(CanAddFilesToActiveDashboard));
        OnPropertyChanged(nameof(IsAdHocScopeActive));
        NotifyScopeMetadataChanged();
    }

    partial void OnSelectedTabChanged(LogTabViewModel? value)
    {
        if (!IsShuttingDown)
            _tabWorkspace.UpdateVisibleTabTailingModes();

        FilterPanel.OnSelectedTabChanged(value);
        _dashboardWorkspace.UpdateSelectedMemberFileHighlights(value?.FileId);
    }

    partial void OnGlobalAutoScrollEnabledChanged(bool value)
    {
        foreach (var tab in Tabs)
            tab.AutoScrollEnabled = value;
    }

    partial void OnDashboardTreeFilterChanged(string value)
        => _dashboardWorkspace.ApplyDashboardTreeFilter();

    private LogGroupViewModel? GetActiveDashboard()
    {
        if (string.IsNullOrEmpty(ActiveDashboardId))
            return null;

        return Groups.FirstOrDefault(group =>
            string.Equals(group.Id, ActiveDashboardId, StringComparison.Ordinal) &&
            group.CanManageFiles);
    }

    bool ITabWorkspaceHost.IsShuttingDown => IsShuttingDown;

    bool ITabWorkspaceHost.GlobalAutoScrollEnabled => GlobalAutoScrollEnabled;

    TimeSpan ITabWorkspaceHost.HiddenTabPurgeAfter => HiddenTabPurgeAfter;

    ObservableCollection<LogTabViewModel> ITabWorkspaceHost.Tabs => Tabs;

    LogTabViewModel? ITabWorkspaceHost.SelectedTab
    {
        get => SelectedTab;
        set => SelectedTab = value;
    }

    IReadOnlyList<LogTabViewModel> ITabWorkspaceHost.GetFilteredTabsSnapshot()
        => GetFilteredTabsSnapshot();

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

    Task IDashboardWorkspaceHost.OpenFilePathAsync(
        string filePath,
        bool reloadIfLoadError,
        bool activateTab,
        bool deferVisibilityRefresh,
        CancellationToken ct)
        => OpenFilePathAsync(filePath, reloadIfLoadError, activateTab, deferVisibilityRefresh, ct);

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

        _dashboardWorkspace.DetachGroupViewModels();
    }
}
