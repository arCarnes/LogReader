namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Models;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using Microsoft.Win32;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const double DefaultGroupsPanelWidth = 220;
    private const double DefaultSearchPanelWidth = 350;
    private const double MinRememberedPanelWidth = 36;
    private static readonly TimeSpan DefaultLifecycleSweepInterval = TimeSpan.FromSeconds(30);
    private const int ActiveTabTailPollingMs = 250;
    private const int BackgroundTabTailPollingMs = 2000;
    private readonly ILogFileRepository _fileRepo;
    private readonly ILogGroupRepository _groupRepo;
    private readonly ISessionRepository _sessionRepo;
    private readonly ISettingsRepository _settingsRepo;
    private readonly ILogReaderService _logReader;
    private readonly ISearchService _searchService;
    private readonly IFileTailService _tailService;
    private readonly System.Threading.Timer? _tabLifecycleTimer;
    private readonly Dictionary<string, long> _tabOpenOrder = new();
    private readonly Dictionary<string, long> _tabPinOrder = new();
    private long _nextTabOpenOrder;
    private long _nextTabPinOrder;

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

    public IEnumerable<LogTabViewModel> FilteredTabs
    {
        get
        {
            var scopedTabs = GetTabsForCurrentScope().ToList();
            var pinnedLane = scopedTabs
                .Where(t => t.IsPinned)
                .OrderBy(GetPinSortKey)
                .ThenBy(GetOpenSortKey);
            var unpinnedLane = scopedTabs
                .Where(t => !t.IsPinned)
                .OrderBy(GetOpenSortKey);
            return pinnedLane.Concat(unpinnedLane);
        }
    }

    public string TabCountText
    {
        get
        {
            var total = Tabs.Count;
            if (string.IsNullOrEmpty(ActiveDashboardId))
            {
                var adhoc = FilteredTabs.Count();
                return $"{adhoc} of {total} tabs (ad-hoc)";
            }
            var filtered = FilteredTabs.Count();
            return $"{filtered} of {total} tabs (filtered)";
        }
    }

    public MainViewModel(
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo,
        ISessionRepository sessionRepo,
        ISettingsRepository settingsRepo,
        ILogReaderService logReader,
        ISearchService searchService,
        IFileTailService tailService,
        bool enableLifecycleTimer = true)
    {
        _fileRepo = fileRepo;
        _groupRepo = groupRepo;
        _sessionRepo = sessionRepo;
        _settingsRepo = settingsRepo;
        _logReader = logReader;
        _searchService = searchService;
        _tailService = tailService;
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
        RebuildGroupsCollection(groups);

        var session = await _sessionRepo.LoadAsync();
        GlobalAutoScrollEnabled = session.OpenTabs.FirstOrDefault()?.AutoScrollEnabled ?? true;
        foreach (var tab in session.OpenTabs)
        {
            if (File.Exists(tab.FilePath))
            {
                await OpenFileInternalAsync(tab.FilePath, tab.Encoding, tab.IsPinned);
            }
        }

        if (session.ActiveTabId != null)
        {
            SelectedTab = Tabs.FirstOrDefault(t => t.FileId == session.ActiveTabId);
        }
        SelectedTab ??= Tabs.FirstOrDefault();

        await RefreshAllMemberFilesAsync();
        NotifyFilteredTabsChanged();
        ApplyDashboardTreeFilter();
    }

    public async Task SaveSessionAsync()
    {
        var state = new SessionState
        {
            ActiveTabId = SelectedTab?.FileId,
            OpenTabs = Tabs.Select(t => new OpenTabState
            {
                FileId = t.FileId,
                FilePath = t.FilePath,
                Encoding = t.Encoding,
                AutoScrollEnabled = GlobalAutoScrollEnabled,
                IsPinned = t.IsPinned
            }).ToList()
        };
        await _sessionRepo.SaveAsync(state).ConfigureAwait(false);
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
        bool deferVisibilityRefresh = false)
    {
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (activateTab)
                SelectedTab = existing;

            if (reloadIfLoadError && existing.HasLoadError)
                await existing.LoadAsync();

            return;
        }

        await OpenFileInternalAsync(
            filePath,
            FileEncoding.Auto,
            activateTab: activateTab,
            updateVisibilityAfterAdd: !deferVisibilityRefresh);
    }

    private async Task OpenFileInternalAsync(
        string filePath,
        FileEncoding encoding,
        bool isPinned = false,
        bool activateTab = true,
        bool updateVisibilityAfterAdd = true)
    {
        var entry = await _fileRepo.GetByPathAsync(filePath);
        if (entry == null)
        {
            entry = new LogFileEntry { FilePath = filePath };
            await _fileRepo.AddAsync(entry);
        }
        else
        {
            entry.LastOpenedAt = DateTime.UtcNow;
            await _fileRepo.UpdateAsync(entry);
        }

        var tab = new LogTabViewModel(entry.Id, filePath, _logReader, _tailService, _settings)
        {
            AutoScrollEnabled = GlobalAutoScrollEnabled,
            IsPinned = isPinned
        };

        tab.Encoding = encoding;
        await tab.LoadAsync();

        _tabOpenOrder[entry.Id] = ++_nextTabOpenOrder;
        if (isPinned)
            _tabPinOrder[entry.Id] = ++_nextTabPinOrder;

        Tabs.Add(tab);
        if (activateTab)
            SelectedTab = tab;

        if (updateVisibilityAfterAdd)
            UpdateTabVisibilityStates();
    }

    [RelayCommand]
    private async Task CloseTab(LogTabViewModel? tab)
    {
        if (tab == null) return;
        tab.Dispose();
        RemoveTabOrdering(tab.FileId);
        Tabs.Remove(tab);
        if (SelectedTab == tab)
            SelectedTab = FilteredTabs.FirstOrDefault();
        ClearActiveDashboardWhenNoScopedTabsRemain();
        ClearActiveDashboardWhenNoTabsRemain();
        await SaveSessionAsync();
    }

    public async Task CloseAllTabsAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
        }
        Tabs.Clear();
        SelectedTab = null;
        ClearActiveDashboardWhenNoScopedTabsRemain();
        ClearActiveDashboardWhenNoTabsRemain();
        await SaveSessionAsync();
    }

    public async Task CloseOtherTabsAsync(LogTabViewModel keepTab)
    {
        foreach (var tab in Tabs.Where(t => t != keepTab).ToList())
        {
            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
            Tabs.Remove(tab);
        }
        SelectedTab = keepTab;
        ClearActiveDashboardWhenNoScopedTabsRemain();
        ClearActiveDashboardWhenNoTabsRemain();
        await SaveSessionAsync();
    }

    public async Task CloseAllButPinnedAsync()
    {
        foreach (var tab in Tabs.Where(t => !t.IsPinned).ToList())
        {
            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
            Tabs.Remove(tab);
        }
        if (SelectedTab != null && !Tabs.Contains(SelectedTab))
            SelectedTab = FilteredTabs.FirstOrDefault();
        ClearActiveDashboardWhenNoScopedTabsRemain();
        ClearActiveDashboardWhenNoTabsRemain();
        await SaveSessionAsync();
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
        tab.IsPinned = !tab.IsPinned;
        if (tab.IsPinned)
            _tabPinOrder[tab.FileId] = ++_nextTabPinOrder;
        else
            _tabPinOrder.Remove(tab.FileId);
        NotifyFilteredTabsChanged();
    }

    private void ClearActiveDashboardWhenNoTabsRemain()
    {
        if (Tabs.Count > 0)
            return;

        if (string.IsNullOrEmpty(ActiveDashboardId) && Groups.All(g => !g.IsSelected))
            return;

        ActiveDashboardId = null;
        foreach (var group in Groups)
            group.IsSelected = false;
        NotifyFilteredTabsChanged();
    }

    private void ClearActiveDashboardWhenNoScopedTabsRemain()
    {
        if (string.IsNullOrEmpty(ActiveDashboardId))
            return;

        if (FilteredTabs.Any())
            return;

        ActiveDashboardId = null;
        foreach (var group in Groups)
            group.IsSelected = false;
        NotifyFilteredTabsChanged();
    }

    private void EnsureSelectedTabInCurrentScope()
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
        var rootCount = Groups.Count(g => g.Model.ParentGroupId == null);
        var group = new LogGroup
        {
            Name = "New Dashboard",
            Kind = LogGroupKind.Dashboard,
            SortOrder = rootCount
        };
        await _groupRepo.AddAsync(group);
        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);
        var vm = Groups.FirstOrDefault(g => g.Id == group.Id);
        if (vm != null) vm.IsExpanded = true;
    }

    [RelayCommand]
    private async Task CreateContainerGroup()
    {
        var rootCount = Groups.Count(g => g.Model.ParentGroupId == null);
        var group = new LogGroup
        {
            Name = "New Folder",
            Kind = LogGroupKind.Branch,
            SortOrder = rootCount
        };
        await _groupRepo.AddAsync(group);
        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);
        var vm = Groups.FirstOrDefault(g => g.Id == group.Id);
        if (vm != null) vm.IsExpanded = true;
    }

    public async Task<bool> CreateChildGroupAsync(LogGroupViewModel parent, LogGroupKind kind = LogGroupKind.Dashboard)
    {
        if (parent.Kind != LogGroupKind.Branch)
            return false;

        var siblingCount = Groups.Count(g => g.Model.ParentGroupId == parent.Id);
        var group = new LogGroup
        {
            Name = kind == LogGroupKind.Branch ? "New Folder" : "New Dashboard",
            Kind = kind,
            ParentGroupId = parent.Id,
            SortOrder = siblingCount
        };
        await _groupRepo.AddAsync(group);
        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);

        // Ensure parent is expanded to show new child
        var parentVm = Groups.FirstOrDefault(g => g.Id == parent.Id);
        if (parentVm != null) parentVm.IsExpanded = true;
        var childVm = Groups.FirstOrDefault(g => g.Id == group.Id);
        if (childVm != null) childVm.IsExpanded = true;

        await RefreshAllMemberFilesAsync();
        return true;
    }

    [RelayCommand]
    private async Task DeleteGroup(LogGroupViewModel? groupVm)
    {
        if (groupVm == null) return;

        if (!string.IsNullOrEmpty(ActiveDashboardId))
        {
            var active = Groups.FirstOrDefault(g => g.Id == ActiveDashboardId);
            if (active != null && (active.Id == groupVm.Id || IsDescendantOf(active, groupVm.Id)))
                ActiveDashboardId = null;
        }

        await _groupRepo.DeleteAsync(groupVm.Id);
        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);
        NotifyFilteredTabsChanged();
    }

    [RelayCommand]
    private async Task ExportGroup(LogGroupViewModel? groupVm)
    {
        if (groupVm == null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Group",
            Filter = "JSON Files (*.json)|*.json",
            FileName = $"{groupVm.Name}.json"
        };
        if (dialog.ShowDialog() == true)
        {
            await _groupRepo.ExportGroupAsync(groupVm.Id, dialog.FileName);
        }
    }

    [RelayCommand]
    private async Task ImportGroup()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Group",
            Filter = "JSON Files (*.json)|*.json"
        };
        if (dialog.ShowDialog() == true)
        {
            GroupExport? export;
            try
            {
                export = await _groupRepo.ImportGroupAsync(dialog.FileName);
            }
            catch (InvalidDataException ex)
            {
                MessageBox.Show(
                    ex.Message,
                    "Import Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
            catch (IOException ex)
            {
                MessageBox.Show(
                    $"Could not read the selected file: {ex.Message}",
                    "Import Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (export == null) return;

            var group = new LogGroup { Name = export.GroupName };
            foreach (var path in export.FilePaths)
            {
                var entry = await _fileRepo.GetByPathAsync(path);
                if (entry == null)
                {
                    entry = new LogFileEntry { FilePath = path };
                    await _fileRepo.AddAsync(entry);
                }
                group.FileIds.Add(entry.Id);
            }
            await _groupRepo.AddAsync(group);
            var allGroups = await _groupRepo.GetAllAsync();
            RebuildGroupsCollection(allGroups);
            var vm = Groups.FirstOrDefault(g => g.Id == group.Id);
            if (vm != null) vm.IsExpanded = true;
        }
    }

    public async Task AddFilesToDashboardAsync(LogGroupViewModel groupVm, Window _)
    {
        if (!groupVm.CanManageFiles)
            return;
        var added = false;
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

        foreach (var path in dialog.FileNames)
        {
            var entry = await _fileRepo.GetByPathAsync(path);
            if (entry == null)
            {
                entry = new LogFileEntry { FilePath = path };
                await _fileRepo.AddAsync(entry);
            }

            if (!groupVm.Model.FileIds.Contains(entry.Id))
            {
                groupVm.Model.FileIds.Add(entry.Id);
                added = true;
            }
        }

        if (!added) return;

        groupVm.NotifyStructureChanged();
        await _groupRepo.UpdateAsync(groupVm.Model);
        await RefreshAllMemberFilesAsync();
        NotifyFilteredTabsChanged();
    }

    public async Task RemoveFileFromDashboardAsync(LogGroupViewModel groupVm, string fileId)
    {
        if (!groupVm.CanManageFiles)
            return;

        if (!groupVm.Model.FileIds.Remove(fileId))
            return;

        groupVm.NotifyStructureChanged();
        await _groupRepo.UpdateAsync(groupVm.Model);
        await RefreshAllMemberFilesAsync();
        NotifyFilteredTabsChanged();
    }

    public async Task MoveGroupUpAsync(LogGroupViewModel group)
    {
        var siblings = GetSiblings(group);
        var idx = siblings.IndexOf(group);
        if (idx <= 0) return;

        // Swap SortOrder values
        var prev = siblings[idx - 1];
        (group.Model.SortOrder, prev.Model.SortOrder) = (prev.Model.SortOrder, group.Model.SortOrder);
        await _groupRepo.UpdateAsync(group.Model);
        await _groupRepo.UpdateAsync(prev.Model);

        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);
        await RefreshAllMemberFilesAsync();
    }

    public async Task MoveGroupDownAsync(LogGroupViewModel group)
    {
        var siblings = GetSiblings(group);
        var idx = siblings.IndexOf(group);
        if (idx < 0 || idx >= siblings.Count - 1) return;

        // Swap SortOrder values
        var next = siblings[idx + 1];
        (group.Model.SortOrder, next.Model.SortOrder) = (next.Model.SortOrder, group.Model.SortOrder);
        await _groupRepo.UpdateAsync(group.Model);
        await _groupRepo.UpdateAsync(next.Model);

        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);
        await RefreshAllMemberFilesAsync();
    }

    public bool CanMoveGroupTo(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
    {
        if (source.Id == target.Id)
            return false;

        if (placement == DropPlacement.Inside && target.Kind != LogGroupKind.Branch)
            return false;

        // Cannot drop a parent into its own descendant
        var current = target.Parent;
        while (current != null)
        {
            if (current.Id == source.Id) return false;
            current = current.Parent;
        }

        // Check for no-op: same parent and position unchanged
        var newParentId = placement == DropPlacement.Inside
            ? target.Id
            : target.Model.ParentGroupId;
        if (source.Model.ParentGroupId == newParentId)
        {
            var siblings = Groups
                .Where(g => g.Model.ParentGroupId == newParentId && g.Depth == source.Depth)
                .ToList();
            var srcIdx = siblings.IndexOf(source);
            var tgtIdx = siblings.IndexOf(target);
            if (srcIdx >= 0 && tgtIdx >= 0)
            {
                if (placement == DropPlacement.Before && (tgtIdx == srcIdx + 1 || tgtIdx == srcIdx))
                    return false;
                if (placement == DropPlacement.After && (tgtIdx == srcIdx - 1 || tgtIdx == srcIdx))
                    return false;
            }
        }

        return true;
    }

    public async Task MoveGroupToAsync(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
    {
        if (!CanMoveGroupTo(source, target, placement))
            return;

        var allModels = await _groupRepo.GetAllAsync();
        var sourceModel = allModels.First(g => g.Id == source.Id);
        var targetModel = allModels.First(g => g.Id == target.Id);

        var oldParentId = sourceModel.ParentGroupId;
        var newParentId = placement == DropPlacement.Inside
            ? targetModel.Id
            : targetModel.ParentGroupId;

        // Build new sibling list (excluding source)
        var newSiblings = allModels
            .Where(g => g.ParentGroupId == newParentId && g.Id != sourceModel.Id)
            .OrderBy(g => g.SortOrder)
            .ToList();

        // Determine insertion index
        int insertIndex;
        if (placement == DropPlacement.Inside)
        {
            insertIndex = newSiblings.Count;
        }
        else
        {
            var targetIndex = newSiblings.FindIndex(g => g.Id == targetModel.Id);
            if (targetIndex < 0)
                targetIndex = newSiblings.Count;
            insertIndex = placement == DropPlacement.Before ? targetIndex : targetIndex + 1;
        }

        // Update parent
        sourceModel.ParentGroupId = newParentId;

        // Insert and re-sequence new siblings
        newSiblings.Insert(insertIndex, sourceModel);
        for (int i = 0; i < newSiblings.Count; i++)
            newSiblings[i].SortOrder = i;

        // Re-sequence old siblings if parent changed
        if (oldParentId != newParentId)
        {
            var oldSiblings = allModels
                .Where(g => g.ParentGroupId == oldParentId && g.Id != sourceModel.Id)
                .OrderBy(g => g.SortOrder)
                .ToList();
            for (int i = 0; i < oldSiblings.Count; i++)
                oldSiblings[i].SortOrder = i;

            foreach (var s in oldSiblings)
                await _groupRepo.UpdateAsync(s);
        }

        foreach (var s in newSiblings)
            await _groupRepo.UpdateAsync(s);

        var refreshed = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(refreshed);

        // Expand the target branch so the user can see where the item landed
        if (placement == DropPlacement.Inside)
        {
            var targetVm = Groups.FirstOrDefault(g => g.Id == target.Id);
            if (targetVm != null) targetVm.IsExpanded = true;
        }

        await RefreshAllMemberFilesAsync();
        NotifyFilteredTabsChanged();
    }

    public void ToggleGroupSelection(LogGroupViewModel group, bool isMultiSelect = false)
    {
        var wasActive = ActiveDashboardId == group.Id;
        foreach (var g in Groups)
            g.IsSelected = false;

        if (group.Kind != LogGroupKind.Dashboard || wasActive)
        {
            ActiveDashboardId = null;
            NotifyFilteredTabsChanged();
            return;
        }

        group.IsSelected = true;
        ActiveDashboardId = group.Id;
        NotifyFilteredTabsChanged();
    }

    public async Task OpenGroupFilesAsync(LogGroupViewModel group)
    {
        _dashboardLoadDepth++;
        IsDashboardLoading = true;
        BeginTabCollectionNotificationSuppression();

        var fileIds = ResolveFileIdsInDisplayOrder(group);
        DashboardLoadingStatusText = fileIds.Count == 0
            ? $"Loading \"{group.Name}\"..."
            : $"Loading \"{group.Name}\" (0/{fileIds.Count})...";

        // Let the dispatcher render the loading indicator before slow file I/O begins.
        await Task.Yield();

        try
        {
            var loadedCount = 0;
            const int maxOpenAttempts = 3;
            for (var index = 0; index < fileIds.Count; index++)
            {
                await Task.Yield();
                var fileId = fileIds[index];
                var entry = await _fileRepo.GetByIdAsync(fileId);
                if (entry != null)
                {
                    if (!File.Exists(entry.FilePath))
                    {
                        DashboardLoadingStatusText = $"Loading \"{group.Name}\" ({index + 1}/{fileIds.Count}, opened {loadedCount})...";
                        continue;
                    }

                    var opened = false;
                    for (var attempt = 1; attempt <= maxOpenAttempts; attempt++)
                    {
                        await OpenFilePathAsync(
                            entry.FilePath,
                            reloadIfLoadError: true,
                            activateTab: false,
                            deferVisibilityRefresh: true);
                        var tab = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
                        if (tab != null && !tab.HasLoadError)
                        {
                            opened = true;
                            break;
                        }

                        if (attempt < maxOpenAttempts)
                            await Task.Delay(400);
                    }

                    if (opened)
                        loadedCount++;
                }

                DashboardLoadingStatusText = $"Loading \"{group.Name}\" ({index + 1}/{fileIds.Count}, opened {loadedCount})...";
            }

            DashboardLoadingStatusText = $"Loaded \"{group.Name}\" ({loadedCount}/{fileIds.Count} opened).";
        }
        finally
        {
            EndTabCollectionNotificationSuppression();
            EnsureSelectedTabInCurrentScope();

            _dashboardLoadDepth = Math.Max(0, _dashboardLoadDepth - 1);
            if (_dashboardLoadDepth == 0)
                IsDashboardLoading = false;
        }
    }

    private void Tabs_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_tabCollectionNotificationSuppressionDepth > 0)
        {
            _tabCollectionChangePending = true;
            return;
        }

        _ = RefreshAllMemberFilesAsync();
        NotifyFilteredTabsChanged();
    }

    private void BeginTabCollectionNotificationSuppression()
    {
        _tabCollectionNotificationSuppressionDepth++;
    }

    private void EndTabCollectionNotificationSuppression()
    {
        _tabCollectionNotificationSuppressionDepth = Math.Max(0, _tabCollectionNotificationSuppressionDepth - 1);
        if (_tabCollectionNotificationSuppressionDepth > 0 || !_tabCollectionChangePending)
            return;

        _tabCollectionChangePending = false;
        _ = RefreshAllMemberFilesAsync();
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
    private async Task ApplySelectedTabEncodingToAll()
    {
        if (SelectedTab == null)
            return;

        var targetEncoding = SelectedTab.Encoding;
        foreach (var tab in Tabs)
        {
            if (tab.Encoding != targetEncoding)
                tab.Encoding = targetEncoding;
        }

        await SaveSessionAsync();
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
                    tab.OnBecameVisible(_settings.GlobalAutoTailEnabled);
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

    public async Task<IReadOnlyList<string>> GetGroupFilePathsAsync(string groupId)
    {
        var allGroups = await _groupRepo.GetAllAsync();
        var resolvedIds = ResolveFileIdsFromModels(allGroups, groupId);
        var allFiles = await _fileRepo.GetAllAsync();
        return allFiles
            .Where(f => resolvedIds.Contains(f.Id))
            .Select(f => f.FilePath)
            .ToList()
            .AsReadOnly();
    }

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
            foreach (var g in Groups)
                g.IsSelected = false;
            if (containingGroup != null)
            {
                containingGroup.IsSelected = true;
                ActiveDashboardId = containingGroup.Id;
            }
            else
            {
                ActiveDashboardId = null;
            }
            NotifyFilteredTabsChanged();
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
            var encoding = EncodingHelper.GetEncoding(tab.EffectiveEncoding);
            await using var stream = new FileStream(
                tab.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 256 * 1024,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 256 * 1024);

            long lineNumber = 0;
            long bestLineNumber = 0;
            long bestDeltaTicks = long.MaxValue;
            bool hasParseableTimestamp = false;
            string? line;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                lineNumber++;
                if (!TimestampParser.TryParseFromLogLine(line, out var lineTimestamp))
                    continue;

                hasParseableTimestamp = true;
                var deltaTicks = ComputeTimestampDistanceTicks(targetTimestamp, lineTimestamp);
                if (deltaTicks >= bestDeltaTicks)
                    continue;

                bestDeltaTicks = deltaTicks;
                bestLineNumber = lineNumber;
                if (deltaTicks == 0)
                    break;
            }

            if (!hasParseableTimestamp)
            {
                const string noTimestampMessage = "No parseable timestamps found in the current file.";
                tab.StatusText = noTimestampMessage;
                return noTimestampMessage;
            }

            await NavigateToLineAsync(tab.FilePath, bestLineNumber);

            var status = bestDeltaTicks == 0
                ? $"Navigated to exact timestamp match at line {bestLineNumber:N0}."
                : $"No exact timestamp match. Navigated to nearest timestamp at line {bestLineNumber:N0}.";
            tab.StatusText = status;
            return status;
        }
        catch (Exception ex)
        {
            var message = $"Go to timestamp error: {ex.Message}";
            tab.StatusText = message;
            return message;
        }
    }

    private static long ComputeTimestampDistanceTicks(ParsedTimestamp target, ParsedTimestamp candidate)
    {
        if (!target.IsTimeOnly && !candidate.IsTimeOnly)
            return AbsTicks((candidate.Value - target.Value).Ticks);

        return ComputeTimeOfDayDistanceTicks(target.TimeOfDay, candidate.TimeOfDay);
    }

    private static long ComputeTimeOfDayDistanceTicks(TimeSpan left, TimeSpan right)
    {
        var directDifference = AbsTicks((left - right).Ticks);
        var wrappedDifference = TimeSpan.TicksPerDay - directDifference;
        return Math.Min(directDifference, wrappedDifference);
    }

    private static long AbsTicks(long ticks)
        => ticks == long.MinValue ? long.MaxValue : Math.Abs(ticks);

    // ── Tree building ─────────────────────────────────────────────────────────

    private void RebuildGroupsCollection(List<LogGroup> allGroups)
    {
        var expandedById = Groups.ToDictionary(g => g.Id, g => g.IsExpanded);
        Groups.Clear();
        var roots = allGroups
            .Where(g => g.ParentGroupId == null)
            .OrderBy(g => g.SortOrder);
        var visitedGroupIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in roots)
            AddGroupToTree(root, null, 0, allGroups, expandedById, visitedGroupIds);

        if (!string.IsNullOrEmpty(ActiveDashboardId))
        {
            var active = Groups.FirstOrDefault(g => g.Id == ActiveDashboardId && g.Kind == LogGroupKind.Dashboard);
            if (active == null)
            {
                ActiveDashboardId = null;
            }
            else
            {
                active.IsSelected = true;
            }
        }

        ApplyDashboardTreeFilter();
    }

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
    {
        var result = new HashSet<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<LogGroupViewModel>();
        stack.Push(group);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current.Id))
                continue;

            foreach (var id in current.Model.FileIds)
                result.Add(id);

            foreach (var child in Groups.Where(g => g.Model.ParentGroupId == current.Id))
                stack.Push(child);
        }

        return result;
    }

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
            var assignedFileIds = Groups
                .Where(g => g.Kind == LogGroupKind.Dashboard)
                .SelectMany(g => g.Model.FileIds)
                .ToHashSet();
            tabs = tabs.Where(t => !assignedFileIds.Contains(t.FileId));
        }

        return tabs;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private LogGroupViewModel WrapGroup(LogGroup model)
    {
        var vm = new LogGroupViewModel(model, async g => await _groupRepo.UpdateAsync(g));
        vm.PropertyChanged += GroupVm_PropertyChanged;
        return vm;
    }

    private List<LogGroupViewModel> GetSiblings(LogGroupViewModel group)
    {
        return Groups
            .Where(g => g.Model.ParentGroupId == group.Model.ParentGroupId && g.Depth == group.Depth)
            .ToList();
    }

    private long GetOpenSortKey(LogTabViewModel tab)
    {
        if (_tabOpenOrder.TryGetValue(tab.FileId, out var order))
            return order;

        var assigned = ++_nextTabOpenOrder;
        _tabOpenOrder[tab.FileId] = assigned;
        return assigned;
    }

    private void RemoveTabOrdering(string fileId)
    {
        _tabOpenOrder.Remove(fileId);
        _tabPinOrder.Remove(fileId);
    }

    private long GetPinSortKey(LogTabViewModel tab)
    {
        if (!tab.IsPinned)
            return long.MaxValue;

        if (_tabPinOrder.TryGetValue(tab.FileId, out var order))
            return order;

        var assigned = ++_nextTabPinOrder;
        _tabPinOrder[tab.FileId] = assigned;
        return assigned;
    }

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

    private async Task RefreshAllMemberFilesAsync()
    {
        var allFiles = await _fileRepo.GetAllAsync();
        var fileIdToPath = allFiles.ToDictionary(f => f.Id, f => f.FilePath);
        var selectedFileId = SelectedTab?.FileId;
        foreach (var group in Groups)
            group.RefreshMemberFiles(Tabs, fileIdToPath, selectedFileId);
    }

    private void NotifyFilteredTabsChanged()
    {
        var filteredTabs = FilteredTabs.ToList();
        UpdateTabVisibilityStates(filteredTabs);
        OnPropertyChanged(nameof(FilteredTabs));
        OnPropertyChanged(nameof(TabCountText));

        if (SelectedTab != null && !filteredTabs.Contains(SelectedTab))
            SelectedTab = filteredTabs.FirstOrDefault();
        else if (SelectedTab == null && filteredTabs.Count > 0)
            SelectedTab = filteredTabs[0];
    }

    partial void OnSelectedTabChanged(LogTabViewModel? value)
    {
        UpdateVisibleTabTailingModes();
        FilterPanel.OnSelectedTabChanged(value);
        UpdateSelectedMemberFileHighlights(value?.FileId);
    }

    private void UpdateSelectedMemberFileHighlights(string? selectedFileId)
    {
        foreach (var group in Groups)
            group.SetSelectedMemberFile(selectedFileId);
    }

    partial void OnGlobalAutoScrollEnabledChanged(bool value)
    {
        foreach (var tab in Tabs)
            tab.AutoScrollEnabled = value;
    }

    partial void OnDashboardTreeFilterChanged(string value)
    {
        ApplyDashboardTreeFilter();
    }

    private void GroupVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogGroupViewModel.Name))
            ApplyDashboardTreeFilter();
    }

    private void ApplyDashboardTreeFilter()
    {
        var filter = DashboardTreeFilter?.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            foreach (var group in Groups)
                group.IsFilterVisible = true;
            return;
        }

        foreach (var root in Groups.Where(g => g.Parent == null))
            ApplyDashboardTreeFilterRecursive(root, filter);
    }

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
    {
        UpdateTabVisibilityStates(FilteredTabs.ToList());
    }

    private void UpdateTabVisibilityStates(IReadOnlyCollection<LogTabViewModel> filteredTabs)
    {
        var visibleIds = filteredTabs.Select(t => t.FileId).ToHashSet();
        foreach (var tab in Tabs)
        {
            if (visibleIds.Contains(tab.FileId))
            {
                tab.OnBecameVisible(_settings.GlobalAutoTailEnabled);
            }
            else
            {
                tab.OnBecameHidden();
            }
        }

        UpdateVisibleTabTailingModes();
    }

    private void UpdateVisibleTabTailingModes()
    {
        foreach (var tab in Tabs)
        {
            if (!tab.IsVisible)
                continue;

            var pollingMs = tab == SelectedTab ? ActiveTabTailPollingMs : BackgroundTabTailPollingMs;
            tab.ApplyVisibleTailingMode(_settings.GlobalAutoTailEnabled, pollingMs);
        }
    }

    private void RunTabLifecycleMaintenanceOnUiThread()
    {
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
        if (Tabs.Count == 0) return;

        foreach (var hiddenTab in Tabs.Where(t => !t.IsVisible))
            hiddenTab.SuspendTailing();

        var now = DateTime.UtcNow;
        var toPurge = Tabs
            .Where(t => !t.IsVisible
                && !t.IsPinned
                && t.LastHiddenAtUtc != DateTime.MinValue
                && now - t.LastHiddenAtUtc >= HiddenTabPurgeAfter)
            .ToList();

        if (toPurge.Count == 0) return;

        foreach (var tab in toPurge)
        {
            if (SelectedTab == tab)
                SelectedTab = null;
            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
            Tabs.Remove(tab);
        }

        ClearActiveDashboardWhenNoScopedTabsRemain();
        ClearActiveDashboardWhenNoTabsRemain();
        NotifyFilteredTabsChanged();
        _ = SaveSessionAsync();
    }

    public void Dispose()
    {
        _tabLifecycleTimer?.Dispose();
    }
}
