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

    public IEnumerable<LogTabViewModel> FilteredTabs
    {
        get
        {
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
                return "Ad Hoc";

            var activeName = Groups.FirstOrDefault(g => g.Id == ActiveDashboardId)?.Name;
            return string.IsNullOrWhiteSpace(activeName) ? "Dashboard" : activeName;
        }
    }

    public string CurrentScopeSummaryText
    {
        get
        {
            if (IsAdHocScopeActive)
                return $"Scope: Ad Hoc ({GetAdHocTabs().Count})";

            return $"Scope: {CurrentScopeLabel} ({FilteredTabs.Count()})";
        }
    }

    public string AdHocScopeChipText => $"Ad Hoc ({GetAdHocTabs().Count})";

    public string EmptyStateText
    {
        get
        {
            if (Tabs.Count == 0)
                return "Drag log files here to open them, or create a dashboard on the left and add files";

            if (IsAdHocScopeActive)
                return "No Ad Hoc tabs. Open a file that is not assigned to a dashboard, or select a dashboard on the left.";

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
                return $"{adhoc} of {total} tabs (Ad Hoc)";
            }
            var filtered = FilteredTabs.Count();
            return $"{filtered} of {total} tabs (Dashboard)";
        }
    }

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

        var result = _bulkOpenPathsDialogService.ShowDialog(
            new BulkOpenPathsDialogRequest(groupVm.Name));
        if (!result.Accepted)
            return;

        var filePaths = DashboardWorkspaceService.ParseBulkFilePaths(result.PathsText);
        if (filePaths.Count == 0)
            return;

        await _dashboardWorkspace.AddFilesToDashboardAsync(groupVm, filePaths);
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
        if (RequiresFullMemberRefresh(e))
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
        if (_pendingFullMemberRefresh || RequiresFullMemberRefresh(e))
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

    public async Task<string> NavigateToLineAsync(string lineNumberText)
    {
        if (SelectedTab == null)
            return "Select a file tab before using Go to line.";

        if (!long.TryParse(lineNumberText?.Trim(), out var lineNumber) || lineNumber <= 0)
            return "Invalid line number. Enter a whole number greater than 0.";

        var tab = SelectedTab;
        if (tab.TotalLines > 0 && lineNumber > tab.TotalLines)
            lineNumber = tab.TotalLines;

        try
        {
            await NavigateToLineAsync(tab.FilePath, lineNumber);
            var status = $"Navigated to line {lineNumber:N0}.";
            tab.StatusText = status;
            return status;
        }
        catch (Exception ex)
        {
            var message = $"Go to line error: {ex.Message}";
            tab.StatusText = message;
            return message;
        }
    }

    public async Task<string> NavigateToTimestampAsync(string timestampText)
    {
        if (SelectedTab == null)
            return "Select a file tab before using Go to timestamp.";

        if (!TimestampParser.TryParseInput(timestampText, out var targetTimestamp))
            return "Invalid timestamp. Use ISO-8601, yyyy-MM-dd HH:mm:ss, or HH:mm:ss.fff.";

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
                return result.StatusMessage;
            }

            await NavigateToLineAsync(tab.FilePath, result.LineNumber);
            tab.StatusText = result.StatusMessage;
            return result.StatusMessage;
        }
        catch (Exception ex)
        {
            var message = $"Go to timestamp error: {ex.Message}";
            tab.StatusText = message;
            return message;
        }
    }

    // ── Tree building ─────────────────────────────────────────────────────────

    // ── File ID resolution ────────────────────────────────────────────────────

    public HashSet<string> ResolveFileIds(LogGroupViewModel group)
        => _dashboardWorkspace.ResolveFileIds(group);

    private IReadOnlyList<LogTabViewModel> GetAdHocTabs()
        => _dashboardScope.GetAdHocTabs(Tabs, Groups);

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
