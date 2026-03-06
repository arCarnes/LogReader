namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using Microsoft.Win32;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const double DefaultGroupsPanelWidth = 220;
    private const double DefaultSearchPanelWidth = 350;
    private const double MinRememberedPanelWidth = 120;
    private static readonly TimeSpan DefaultLifecycleSweepInterval = TimeSpan.FromSeconds(30);
    private readonly ILogFileRepository _fileRepo;
    private readonly ILogGroupRepository _groupRepo;
    private readonly ISessionRepository _sessionRepo;
    private readonly ISettingsRepository _settingsRepo;
    private readonly ILogReaderService _logReader;
    private readonly ISearchService _searchService;
    private readonly IFileTailService _tailService;
    private readonly System.Threading.Timer? _tabLifecycleTimer;

    private AppSettings _settings = new();
    public TimeSpan HiddenTabPurgeAfter { get; set; } = TimeSpan.FromMinutes(20);

    public ObservableCollection<LogTabViewModel> Tabs { get; } = new();
    public ObservableCollection<LogGroupViewModel> Groups { get; } = new();
    public SearchPanelViewModel SearchPanel { get; }

    [ObservableProperty]
    private LogTabViewModel? _selectedTab;

    [ObservableProperty]
    private bool _isGroupsPanelOpen = true;

    [ObservableProperty]
    private bool _isSearchPanelOpen = true;

    [ObservableProperty]
    private double _groupsPanelWidth = DefaultGroupsPanelWidth;

    [ObservableProperty]
    private double _searchPanelWidth = DefaultSearchPanelWidth;

    public IEnumerable<LogTabViewModel> FilteredTabs
    {
        get
        {
            IEnumerable<LogTabViewModel> result = Tabs;
            var selected = Groups.Where(g => g.IsSelected).ToList();
            if (selected.Count > 0)
            {
                var fileIds = selected.SelectMany(g => ResolveFileIds(g)).ToHashSet();
                result = result.Where(t => fileIds.Contains(t.FileId));
            }
            return result.OrderByDescending(t => t.IsPinned);
        }
    }

    public string TabCountText
    {
        get
        {
            var total = Tabs.Count;
            var anySelected = Groups.Any(g => g.IsSelected);
            if (!anySelected) return $"{total} tabs open";
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
        await OpenFileInternalAsync(filePath, FileEncoding.Utf8, true);
    }

    private async Task OpenFileInternalAsync(string filePath, FileEncoding encoding, bool autoScroll, bool isPinned = false)
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
            Encoding = encoding,
            AutoScrollEnabled = autoScroll,
            IsPinned = isPinned
        };
        Tabs.Add(tab);
        SelectedTab = tab;
        await tab.LoadAsync();
        UpdateTabVisibilityStates();
    }

    [RelayCommand]
    private async Task CloseTab(LogTabViewModel? tab)
    {
        if (tab == null) return;
        tab.Dispose();
        Tabs.Remove(tab);
        if (SelectedTab == tab)
            SelectedTab = FilteredTabs.FirstOrDefault();
        await SaveSessionAsync();
    }

    public async Task CloseAllTabsAsync()
    {
        foreach (var tab in Tabs.ToList())
            tab.Dispose();
        Tabs.Clear();
        SelectedTab = null;
        await SaveSessionAsync();
    }

    public async Task CloseOtherTabsAsync(LogTabViewModel keepTab)
    {
        foreach (var tab in Tabs.Where(t => t != keepTab).ToList())
        {
            tab.Dispose();
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
            Tabs.Remove(tab);
        }
        if (SelectedTab != null && !Tabs.Contains(SelectedTab))
            SelectedTab = FilteredTabs.FirstOrDefault();
        await SaveSessionAsync();
    }

    public void TogglePinTab(LogTabViewModel tab)
    {
        tab.IsPinned = !tab.IsPinned;
        NotifyFilteredTabsChanged();
    }

    // ── Group commands ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CreateGroup()
    {
        var rootCount = Groups.Count(g => g.Model.ParentGroupId == null);
        var group = new LogGroup
        {
            Name = "New Group",
            Kind = LogGroupKind.Neutral,
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
        // Backward-compatible command: creation now always starts neutral.
        await CreateGroup();
    }

    public async Task<bool> CreateChildGroupAsync(LogGroupViewModel parent, LogGroupKind kind = LogGroupKind.Neutral)
    {
        var siblingCount = Groups.Count(g => g.Model.ParentGroupId == parent.Id);
        var group = new LogGroup
        {
            Name = "New Group",
            Kind = LogGroupKind.Neutral,
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

        // Deselect the group and any descendants
        foreach (var g in Groups.Where(g => g.IsSelected &&
            (g.Id == groupVm.Id || IsDescendantOf(g, groupVm.Id))))
        {
            g.IsSelected = false;
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
            var export = await _groupRepo.ImportGroupAsync(dialog.FileName);
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

    public async Task OpenManageGroupFilesAsync(LogGroupViewModel groupVm, Window owner)
    {
        var manageVm = new ManageGroupFilesViewModel(groupVm, Tabs);
        var window = new LogReader.App.Views.ManageGroupFilesWindow
        {
            DataContext = manageVm,
            Owner = owner
        };

        if (window.ShowDialog() == true)
        {
            var selectedIds = manageVm.GetSelectedFileIds().ToHashSet();
            var newlySelectedPaths = manageVm.GetSelectedFilePathsWithoutIds();

            foreach (var path in newlySelectedPaths)
            {
                var entry = await _fileRepo.GetByPathAsync(path);
                if (entry == null)
                {
                    entry = new LogFileEntry { FilePath = path };
                    await _fileRepo.AddAsync(entry);
                }

                selectedIds.Add(entry.Id);
            }

            var previousIds = groupVm.Model.FileIds.ToHashSet();

            var toAdd = selectedIds.Except(previousIds).ToList();
            var toRemove = previousIds.Except(selectedIds).ToList();

            foreach (var id in toAdd) groupVm.Model.FileIds.Add(id);
            foreach (var id in toRemove) groupVm.Model.FileIds.Remove(id);

            if (toAdd.Count > 0 || toRemove.Count > 0)
            {
                groupVm.NotifyStructureChanged();
                await _groupRepo.UpdateAsync(groupVm.Model);
                await RefreshAllMemberFilesAsync();
                NotifyFilteredTabsChanged();
            }
        }
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

    public void ToggleGroupSelection(LogGroupViewModel group, bool isMultiSelect = false)
    {
        var wasSelected = group.IsSelected;
        if (!isMultiSelect)
        {
            foreach (var g in Groups)
                g.IsSelected = false;
        }
        group.IsSelected = !wasSelected;
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
        Groups.Clear();
        var roots = allGroups
            .Where(g => g.ParentGroupId == null)
            .OrderBy(g => g.SortOrder);

        foreach (var root in roots)
            AddGroupToTree(root, null, 0, allGroups);
    }

    private void AddGroupToTree(LogGroup model, LogGroupViewModel? parent, int depth, List<LogGroup> allGroups)
    {
        var vm = WrapGroup(model);
        vm.Depth = depth;
        vm.Parent = parent;
        parent?.AddChild(vm);
        Groups.Add(vm);

        var children = allGroups
            .Where(g => g.ParentGroupId == model.Id)
            .OrderBy(g => g.SortOrder);
        foreach (var child in children)
            AddGroupToTree(child, vm, depth + 1, allGroups);
    }

    // ── File ID resolution ────────────────────────────────────────────────────

    public HashSet<string> ResolveFileIds(LogGroupViewModel group)
    {
        var result = new HashSet<string>();
        foreach (var id in group.Model.FileIds)
        {
            result.Add(id);
        }
        foreach (var child in Groups.Where(g => g.Model.ParentGroupId == group.Id))
        {
            result.UnionWith(ResolveFileIds(child));
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
        return new LogGroupViewModel(model, async g => await _groupRepo.UpdateAsync(g));
    }

    private List<LogGroupViewModel> GetSiblings(LogGroupViewModel group)
    {
        return Groups
            .Where(g => g.Model.ParentGroupId == group.Model.ParentGroupId && g.Depth == group.Depth)
            .ToList();
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
        UpdateTabVisibilityStates();
        OnPropertyChanged(nameof(FilteredTabs));
        OnPropertyChanged(nameof(TabCountText));

        if (SelectedTab != null && !FilteredTabs.Contains(SelectedTab))
            SelectedTab = FilteredTabs.FirstOrDefault();
    }

    private void UpdateTabVisibilityStates()
    {
        var visibleIds = FilteredTabs.Select(t => t.FileId).ToHashSet();
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
