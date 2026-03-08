namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Models;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using Microsoft.Win32;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const double DefaultGroupsPanelWidth = 220;
    private const double DefaultSearchPanelWidth = 350;
    private const double MinRememberedPanelWidth = 36;
    private static readonly TimeSpan DefaultLifecycleSweepInterval = TimeSpan.FromSeconds(30);
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
    private string _dashboardTreeFilter = string.Empty;

    public IEnumerable<LogTabViewModel> FilteredTabs
    {
        get
        {
            IEnumerable<LogTabViewModel> result = Tabs;
            if (!string.IsNullOrEmpty(ActiveDashboardId))
            {
                var active = Groups.FirstOrDefault(g => g.Id == ActiveDashboardId);
                if (active != null)
                {
                    var fileIds = ResolveFileIds(active);
                    result = result.Where(t => fileIds.Contains(t.FileId));
                }
            }
            else
            {
                var assignedFileIds = Groups
                    .Where(g => g.Kind == LogGroupKind.Dashboard)
                    .SelectMany(g => g.Model.FileIds)
                    .ToHashSet();
                result = result.Where(t => !assignedFileIds.Contains(t.FileId));
            }
            return result
                .OrderByDescending(t => t.IsPinned)
                .ThenBy(GetPinSortKey)
                .ThenBy(GetOpenSortKey);
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
        if (enableLifecycleTimer)
        {
            _tabLifecycleTimer = new System.Threading.Timer(
                _ => RunTabLifecycleMaintenanceOnUiThread(),
                null,
                DefaultLifecycleSweepInterval,
                DefaultLifecycleSweepInterval);
        }

        Tabs.CollectionChanged += (_, _) =>
        {
            _ = RefreshAllMemberFilesAsync();
            NotifyFilteredTabsChanged();
        };
    }

    public async Task InitializeAsync()
    {
        _settings = await _settingsRepo.LoadAsync();
        ApplyLogFontResource(_settings);

        var groups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(groups);

        var session = await _sessionRepo.LoadAsync();
        foreach (var tab in session.OpenTabs)
        {
            if (File.Exists(tab.FilePath))
            {
                await OpenFileInternalAsync(tab.FilePath, tab.Encoding, tab.AutoScrollEnabled, tab.IsPinned);
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
                AutoScrollEnabled = t.AutoScrollEnabled,
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

    public async Task OpenFilePathAsync(string filePath)
    {
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectedTab = existing;
            return;
        }
        await OpenFileInternalAsync(filePath, _settings.DefaultFileEncoding, true, useFallbacks: true);
    }

    private async Task OpenFileInternalAsync(string filePath, FileEncoding encoding, bool autoScroll, bool isPinned = false, bool useFallbacks = false)
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
            AutoScrollEnabled = autoScroll,
            IsPinned = isPinned
        };

        var attemptedEncodings = useFallbacks
            ? GetEncodingAttemptOrder(encoding)
            : new[] { encoding };
        foreach (var candidate in attemptedEncodings)
        {
            tab.Encoding = candidate;
            await tab.LoadAsync();
            if (!tab.HasLoadError)
                break;
        }

        _tabOpenOrder[entry.Id] = ++_nextTabOpenOrder;
        if (isPinned)
            _tabPinOrder[entry.Id] = ++_nextTabPinOrder;

        Tabs.Add(tab);
        SelectedTab = tab;
        UpdateTabVisibilityStates();
    }

    private IReadOnlyList<FileEncoding> GetEncodingAttemptOrder(FileEncoding primaryEncoding)
    {
        var order = new List<FileEncoding> { primaryEncoding };
        foreach (var fallback in _settings.FileEncodingFallbacks ?? new List<FileEncoding>())
        {
            if (!order.Contains(fallback))
                order.Add(fallback);
        }
        return order;
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
        await SaveSessionAsync();
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
        var fileIds = ResolveFileIds(group);
        foreach (var fileId in fileIds)
        {
            var entry = await _fileRepo.GetByIdAsync(fileId);
            if (entry != null && File.Exists(entry.FilePath))
                await OpenFilePathAsync(entry.FilePath);
        }
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

    public async Task NavigateToLineAsync(string filePath, long lineNumber)
    {
        var tab = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (tab == null)
        {
            await OpenFilePathAsync(filePath);
            tab = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        }
        if (tab != null)
        {
            SelectedTab = tab;
            await tab.NavigateToLineAsync((int)lineNumber);
        }
    }

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
        foreach (var group in Groups)
            group.RefreshMemberFiles(Tabs, fileIdToPath);
    }

    private void NotifyFilteredTabsChanged()
    {
        var filteredTabs = FilteredTabs.ToList();
        UpdateTabVisibilityStates(filteredTabs);
        OnPropertyChanged(nameof(FilteredTabs));
        OnPropertyChanged(nameof(TabCountText));

        if (SelectedTab != null && !filteredTabs.Contains(SelectedTab))
            SelectedTab = filteredTabs.FirstOrDefault();
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

        NotifyFilteredTabsChanged();
        _ = SaveSessionAsync();
    }

    public void Dispose()
    {
        _tabLifecycleTimer?.Dispose();
    }
}
