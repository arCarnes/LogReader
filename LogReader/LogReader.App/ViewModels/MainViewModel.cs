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
using Microsoft.Win32;

public partial class MainViewModel : ObservableObject, ILogWorkspaceContext, IDisposable
{
    private const double DefaultGroupsPanelWidth = 220;
    private const double DefaultSearchPanelWidth = 350;
    private const double MinRememberedPanelWidth = 36;
    private static readonly TimeSpan DefaultLifecycleSweepInterval = TimeSpan.FromSeconds(30);
    private readonly ILogFileRepository _fileRepo;
    private readonly ILogGroupRepository _groupRepo;
    private readonly ISettingsRepository _settingsRepo;
    private readonly ILogReaderService _logReader;
    private readonly ISearchService _searchService;
    private readonly IFileTailService _tailService;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly ILogTimestampNavigationService _timestampNavigationService;
    private readonly TabWorkspaceService _tabWorkspace;
    private readonly DashboardWorkspaceService _dashboardWorkspace;
    private readonly System.Threading.Timer? _tabLifecycleTimer;
    private readonly Dictionary<string, long> _tabOpenOrder = new();
    private readonly Dictionary<string, long> _tabPinOrder = new();

    private AppSettings _settings = new();
    public TimeSpan HiddenTabPurgeAfter { get; set; } = TimeSpan.FromMinutes(20);
    internal Func<OpenFileDialog, bool?> ShowOpenFileDialog { get; set; } = static dialog => dialog.ShowDialog();
    internal Func<SaveFileDialog, bool?> ShowSaveFileDialog { get; set; } = static dialog => dialog.ShowDialog();
    internal Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> ShowMessageBox { get; set; }
        = static (message, caption, buttons, image) => MessageBox.Show(message, caption, buttons, image);

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
            var scopedTabs = GetTabsForCurrentScope().ToList();
            return _tabWorkspace.OrderTabsForDisplay(scopedTabs);
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
                return $"Scope: Ad Hoc ({GetAdHocTabs().Count()})";

            return $"Scope: {CurrentScopeLabel} ({FilteredTabs.Count()})";
        }
    }

    public string AdHocScopeChipText => $"Ad Hoc ({GetAdHocTabs().Count()})";

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
        bool enableLifecycleTimer = true)
    {
        _fileRepo = fileRepo;
        _groupRepo = groupRepo;
        _settingsRepo = settingsRepo;
        _logReader = logReader;
        _searchService = searchService;
        _tailService = tailService;
        _encodingDetectionService = encodingDetectionService;
        _timestampNavigationService = timestampNavigationService;
        _tabWorkspace = new TabWorkspaceService(
            this,
            fileRepo,
            logReader,
            tailService,
            encodingDetectionService,
            _tabOpenOrder,
            _tabPinOrder);
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
        var dialog = new OpenFileDialog
        {
            Title = "Open Log File",
            Filter = "Log Files (*.log;*.txt)|*.log;*.txt|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (!string.IsNullOrEmpty(_settings.DefaultOpenDirectory) && Directory.Exists(_settings.DefaultOpenDirectory))
        {
            dialog.InitialDirectory = _settings.DefaultOpenDirectory;
        }

        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
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
    }

    [RelayCommand]
    private async Task CloseTab(LogTabViewModel? tab)
    {
        await _tabWorkspace.CloseTabAsync(tab);
        ClearActiveDashboardWhenNoScopedTabsRemain();
        ClearActiveDashboardWhenNoTabsRemain();
    }

    public async Task CloseAllTabsAsync()
    {
        await _tabWorkspace.CloseAllTabsAsync();
        ClearActiveDashboardWhenNoScopedTabsRemain();
        ClearActiveDashboardWhenNoTabsRemain();
    }

    public async Task CloseOtherTabsAsync(LogTabViewModel keepTab)
    {
        await _tabWorkspace.CloseOtherTabsAsync(keepTab);
        ClearActiveDashboardWhenNoScopedTabsRemain();
        ClearActiveDashboardWhenNoTabsRemain();
    }

    public async Task CloseAllButPinnedAsync()
    {
        await _tabWorkspace.CloseAllButPinnedAsync();
        ClearActiveDashboardWhenNoScopedTabsRemain();
        ClearActiveDashboardWhenNoTabsRemain();
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
        var dialog = CreateImportViewDialog();
        if (ShowOpenFileDialog(dialog) == true)
        {
            ViewExport? export;
            try
            {
                export = await _groupRepo.ImportViewAsync(dialog.FileName);
            }
            catch (InvalidDataException ex)
            {
                ShowMessageBox(
                    ex.Message,
                    "Import Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
            catch (IOException ex)
            {
                ShowMessageBox(
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
        var dialog = CreateExportViewDialog();
        if (ShowSaveFileDialog(dialog) != true)
            return false;

        await _dashboardWorkspace.ExportViewAsync(dialog.FileName);
        return true;
    }

    private async Task<bool> ConfirmImportViewReplacementAsync()
    {
        if (Groups.Count == 0)
            return true;

        var result = ShowMessageBox(
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

    private SaveFileDialog CreateExportViewDialog()
    {
        return new SaveFileDialog
        {
            Title = "Export View",
            Filter = "LogReader View (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            InitialDirectory = GetViewsDirectory(),
            FileName = CreateDefaultViewExportFileName()
        };
    }

    private OpenFileDialog CreateImportViewDialog()
    {
        return new OpenFileDialog
        {
            Title = "Import View",
            Filter = "LogReader View (*.json)|*.json",
            InitialDirectory = GetViewsDirectory()
        };
    }

    private static string GetViewsDirectory() => AppPaths.EnsureDirectory(AppPaths.ViewsDirectory);

    private static string CreateDefaultViewExportFileName() => $"logreader-view-{DateTime.Now:yyyy-MM-dd-HHmmss}.json";

    internal Task ApplyImportedViewAsync(ViewExport export)
        => _dashboardWorkspace.ApplyImportedViewAsync(export);

    public async Task AddFilesToDashboardAsync(LogGroupViewModel groupVm, Window _)
    {
        if (!groupVm.CanManageFiles)
            return;
        string? initialDirectory = !string.IsNullOrWhiteSpace(_settings.DefaultOpenDirectory) &&
                                   Directory.Exists(_settings.DefaultOpenDirectory)
            ? _settings.DefaultOpenDirectory
            : null;

        var dialog = new OpenFileDialog
        {
            Title = "Add Files to Dashboard",
            Filter = "Log Files (*.log;*.txt)|*.log;*.txt|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
            return;

        await _dashboardWorkspace.AddFilesToDashboardAsync(groupVm, dialog.FileNames);
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

    public void ToggleGroupSelection(LogGroupViewModel group, bool isMultiSelect = false)
        => _dashboardWorkspace.ToggleGroupSelection(group, isMultiSelect);

    public async Task OpenGroupFilesAsync(LogGroupViewModel group)
        => await _dashboardWorkspace.OpenGroupFilesAsync(group);

    private void Tabs_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (IsShuttingDown)
            return;

        if (_tabCollectionNotificationSuppressionDepth > 0)
        {
            _tabCollectionChangePending = true;
            return;
        }

        _ = _dashboardWorkspace.RefreshAllMemberFilesAsync();
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
        _ = _dashboardWorkspace.RefreshAllMemberFilesAsync();
        NotifyFilteredTabsChanged();
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

    public async Task OpenSettingsAsync(Window owner)
    {
        var settingsVm = new SettingsViewModel(_settingsRepo);
        await settingsVm.LoadAsync();

        var settingsWindow = new LogReader.App.Views.SettingsWindow
        {
            DataContext = settingsVm,
            Owner = owner
        };

        if (settingsWindow.ShowDialog() == true)
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
        {
            var containingGroup = Groups.FirstOrDefault(
                g => g.Kind == LogGroupKind.Dashboard && g.Model.FileIds.Contains(tab.FileId));
            if (containingGroup != null)
            {
                foreach (var g in Groups)
                    g.IsSelected = false;
                containingGroup.IsSelected = true;
                ActiveDashboardId = containingGroup.Id;
                NotifyFilteredTabsChanged();
            }
            else
            {
                ActivateAdHocScope();
            }
        }

        if (disableAutoScroll && GlobalAutoScrollEnabled)
            GlobalAutoScrollEnabled = false;

        SelectedTab = tab;
        await tab.NavigateToLineAsync((int)lineNumber);
    }

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

    private void RebuildGroupsCollection(List<LogGroup> allGroups)
        => _dashboardWorkspace.RebuildGroupsCollection(allGroups);

    private void AddGroupToTree(
        LogGroup model,
        LogGroupViewModel? parent,
        int depth,
        List<LogGroup> allGroups,
        IReadOnlyDictionary<string, bool> expandedById,
        HashSet<string> visitedGroupIds)
    {
        if (!visitedGroupIds.Add(model.Id))
            return;

        var vm = WrapGroup(model);
        vm.Depth = depth;
        vm.Parent = parent;
        if (expandedById.TryGetValue(model.Id, out var wasExpanded))
            vm.IsExpanded = wasExpanded;
        parent?.AddChild(vm);
        Groups.Add(vm);

        var children = allGroups
            .Where(g => g.ParentGroupId == model.Id)
            .OrderBy(g => g.SortOrder);
        foreach (var child in children)
            AddGroupToTree(child, vm, depth + 1, allGroups, expandedById, visitedGroupIds);
    }

    // ── File ID resolution ────────────────────────────────────────────────────

    public HashSet<string> ResolveFileIds(LogGroupViewModel group)
        => _dashboardWorkspace.ResolveFileIds(group);

    private IReadOnlyList<string> ResolveFileIdsInDisplayOrder(LogGroupViewModel group)
    {
        var orderedFileIds = new List<string>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        var seenFileIds = new HashSet<string>(StringComparer.Ordinal);
        CollectFileIdsInDisplayOrder(group, seenGroups, seenFileIds, orderedFileIds);
        return orderedFileIds;
    }

    private static void CollectFileIdsInDisplayOrder(
        LogGroupViewModel group,
        HashSet<string> seenGroups,
        HashSet<string> seenFileIds,
        List<string> orderedFileIds)
    {
        if (!seenGroups.Add(group.Id))
            return;

        foreach (var fileId in group.Model.FileIds)
        {
            if (seenFileIds.Add(fileId))
                orderedFileIds.Add(fileId);
        }

        foreach (var child in group.Children.OrderBy(c => c.Model.SortOrder))
            CollectFileIdsInDisplayOrder(child, seenGroups, seenFileIds, orderedFileIds);
    }

    private static HashSet<string> ResolveFileIdsFromModels(
        List<LogGroup> allGroups, string groupId, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();
        if (!visited.Add(groupId)) return new HashSet<string>(); // cycle detected
        var result = new HashSet<string>();
        var group = allGroups.FirstOrDefault(g => g.Id == groupId);
        if (group == null) return result;
        result.UnionWith(group.FileIds);
        foreach (var child in allGroups.Where(g => g.ParentGroupId == groupId))
            result.UnionWith(ResolveFileIdsFromModels(allGroups, child.Id, visited));
        return result;
    }

    private IEnumerable<LogTabViewModel> GetTabsForCurrentScope()
    {
        IEnumerable<LogTabViewModel> tabs = Tabs;
        if (!string.IsNullOrEmpty(ActiveDashboardId))
        {
            var active = Groups.FirstOrDefault(g => g.Id == ActiveDashboardId);
            if (active != null)
            {
                var fileIds = ResolveFileIds(active);
                tabs = tabs.Where(t => fileIds.Contains(t.FileId));
            }
        }
        else
        {
            tabs = GetAdHocTabs();
        }

        return tabs;
    }

    private IEnumerable<LogTabViewModel> GetAdHocTabs()
    {
        var assignedFileIds = Groups
            .Where(g => g.Kind == LogGroupKind.Dashboard)
            .SelectMany(g => g.Model.FileIds)
            .ToHashSet();
        return Tabs.Where(t => !assignedFileIds.Contains(t.FileId));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private LogGroupViewModel WrapGroup(LogGroup model)
    {
        var vm = new LogGroupViewModel(model, async g => await _groupRepo.UpdateAsync(g));
        vm.PropertyChanged += GroupVm_PropertyChanged;
        return vm;
    }

    private void DetachGroupViewModels()
        => _dashboardWorkspace.DetachGroupViewModels();

    private List<LogGroupViewModel> GetSiblings(LogGroupViewModel group)
    {
        return Groups
            .Where(g => g.Model.ParentGroupId == group.Model.ParentGroupId && g.Depth == group.Depth)
            .ToList();
    }

    private long GetOpenSortKey(LogTabViewModel tab) => 0;

    private void RemoveTabOrdering(string fileId)
    {
    }

    private long GetPinSortKey(LogTabViewModel tab) => tab.IsPinned ? 0 : long.MaxValue;

    private bool IsDescendantOf(LogGroupViewModel group, string ancestorId)
    {
        var current = group.Parent;
        while (current != null)
        {
            if (current.Id == ancestorId) return true;
            current = current.Parent;
        }
        return false;
    }

    private Task RefreshAllMemberFilesAsync()
        => _dashboardWorkspace.RefreshAllMemberFilesAsync();

    internal void NotifyFilteredTabsChanged()
    {
        var filteredTabs = FilteredTabs.ToList();
        _tabWorkspace.UpdateTabVisibilityStates(filteredTabs);
        OnPropertyChanged(nameof(FilteredTabs));
        OnPropertyChanged(nameof(IsCurrentScopeEmpty));
        OnPropertyChanged(nameof(CurrentScopeLabel));
        OnPropertyChanged(nameof(CurrentScopeSummaryText));
        OnPropertyChanged(nameof(AdHocScopeChipText));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(TabCountText));

        if (SelectedTab != null && !filteredTabs.Contains(SelectedTab))
            SelectedTab = filteredTabs.FirstOrDefault();
        else if (SelectedTab == null && filteredTabs.Count > 0)
            SelectedTab = filteredTabs[0];
    }

    internal void ActivateAdHocScope()
    {
        _dashboardWorkspace.CancelDashboardLoad();
        ActiveDashboardId = null;
        foreach (var group in Groups)
            group.IsSelected = false;
        NotifyFilteredTabsChanged();
    }

    partial void OnActiveDashboardIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsAdHocScopeActive));
        OnPropertyChanged(nameof(CurrentScopeLabel));
        OnPropertyChanged(nameof(CurrentScopeSummaryText));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(TabCountText));
    }

    partial void OnSelectedTabChanged(LogTabViewModel? value)
    {
        if (!IsShuttingDown)
            _tabWorkspace.UpdateVisibleTabTailingModes();

        FilterPanel.OnSelectedTabChanged(value);
        _dashboardWorkspace.UpdateSelectedMemberFileHighlights(value?.FileId);
    }

    private void UpdateSelectedMemberFileHighlights(string? selectedFileId)
        => _dashboardWorkspace.UpdateSelectedMemberFileHighlights(selectedFileId);

    partial void OnGlobalAutoScrollEnabledChanged(bool value)
    {
        foreach (var tab in Tabs)
            tab.AutoScrollEnabled = value;
    }

    partial void OnDashboardTreeFilterChanged(string value)
        => _dashboardWorkspace.ApplyDashboardTreeFilter();

    private void GroupVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogGroupViewModel.Name))
        {
            _dashboardWorkspace.ApplyDashboardTreeFilter();
            OnPropertyChanged(nameof(CurrentScopeLabel));
            OnPropertyChanged(nameof(CurrentScopeSummaryText));
            OnPropertyChanged(nameof(EmptyStateText));
        }
    }

    private void ApplyDashboardTreeFilter()
        => _dashboardWorkspace.ApplyDashboardTreeFilter();

    private static bool ApplyDashboardTreeFilterRecursive(LogGroupViewModel node, string filter)
    {
        var selfMatch = node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var descendantMatch = false;
        foreach (var child in node.Children)
            descendantMatch |= ApplyDashboardTreeFilterRecursive(child, filter);

        node.IsFilterVisible = selfMatch || descendantMatch;
        if (descendantMatch && !node.IsExpanded)
            node.IsExpanded = true;

        return node.IsFilterVisible;
    }

    private void UpdateTabVisibilityStates()
        => _tabWorkspace.UpdateTabVisibilityStates();

    private void UpdateTabVisibilityStates(IReadOnlyCollection<LogTabViewModel> filteredTabs)
        => _tabWorkspace.UpdateTabVisibilityStates(filteredTabs);

    private void UpdateVisibleTabTailingModes()
        => _tabWorkspace.UpdateVisibleTabTailingModes();

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
        if (!_tabWorkspace.RunLifecycleMaintenance())
            return;

        ClearActiveDashboardWhenNoScopedTabsRemain();
        ClearActiveDashboardWhenNoTabsRemain();
        NotifyFilteredTabsChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        BeginShutdown();
        Tabs.CollectionChanged -= Tabs_CollectionChanged;

        var disposeTasks = Tabs
            .ToList()
            .Select(tab => Task.Run(tab.Dispose))
            .ToArray();

        try
        {
            Task.WaitAll(disposeTasks, TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static inner =>
            inner is OperationCanceledException or ObjectDisposedException))
        {
        }

        _dashboardWorkspace.DetachGroupViewModels();
    }
}
