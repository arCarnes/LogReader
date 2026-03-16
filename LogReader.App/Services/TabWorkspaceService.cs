namespace LogReader.App.Services;

using System.IO;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class TabWorkspaceService
{
    private const int ActiveTabTailPollingMs = 250;
    private const int BackgroundTabTailPollingMs = 2000;

    private readonly MainViewModel _owner;
    private readonly ILogFileRepository _fileRepo;
    private readonly ISessionRepository _sessionRepo;
    private readonly ILogReaderService _logReader;
    private readonly IFileTailService _tailService;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly Dictionary<string, long> _tabOpenOrder;
    private readonly Dictionary<string, long> _tabPinOrder;
    private long _nextTabOpenOrder;
    private long _nextTabPinOrder;

    public TabWorkspaceService(
        MainViewModel owner,
        ILogFileRepository fileRepo,
        ISessionRepository sessionRepo,
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        Dictionary<string, long> tabOpenOrder,
        Dictionary<string, long> tabPinOrder)
    {
        _owner = owner;
        _fileRepo = fileRepo;
        _sessionRepo = sessionRepo;
        _logReader = logReader;
        _tailService = tailService;
        _encodingDetectionService = encodingDetectionService;
        _tabOpenOrder = tabOpenOrder;
        _tabPinOrder = tabPinOrder;
    }

    public IEnumerable<LogTabViewModel> OrderTabsForDisplay(IEnumerable<LogTabViewModel> scopedTabs)
    {
        var tabList = scopedTabs.ToList();
        var pinnedLane = tabList
            .Where(t => t.IsPinned)
            .OrderBy(GetPinSortKey)
            .ThenBy(GetOpenSortKey);
        var unpinnedLane = tabList
            .Where(t => !t.IsPinned)
            .OrderBy(GetOpenSortKey);
        return pinnedLane.Concat(unpinnedLane);
    }

    public async Task RestoreSessionAsync(SessionState session, AppSettings settings)
    {
        _owner.GlobalAutoScrollEnabled = session.OpenTabs.FirstOrDefault()?.AutoScrollEnabled ?? true;
        foreach (var tab in session.OpenTabs)
        {
            if (File.Exists(tab.FilePath))
                await OpenFileInternalAsync(tab.FilePath, settings, tab.Encoding, tab.IsPinned).ConfigureAwait(false);
        }

        if (session.ActiveTabId != null)
            _owner.SelectedTab = _owner.Tabs.FirstOrDefault(t => t.FileId == session.ActiveTabId);
    }

    public async Task SaveSessionAsync()
    {
        var state = new SessionState
        {
            ActiveTabId = _owner.SelectedTab?.FileId,
            OpenTabs = _owner.Tabs.Select(t => new OpenTabState
            {
                FileId = t.FileId,
                FilePath = t.FilePath,
                Encoding = t.Encoding,
                AutoScrollEnabled = _owner.GlobalAutoScrollEnabled,
                IsPinned = t.IsPinned
            }).ToList()
        };
        await _sessionRepo.SaveAsync(state).ConfigureAwait(false);
    }

    public async Task OpenFilePathAsync(
        string filePath,
        AppSettings settings,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false)
    {
        if (_owner.IsShuttingDown)
            return;

        var existing = _owner.Tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (activateTab)
                _owner.SelectedTab = existing;

            if (reloadIfLoadError && existing.HasLoadError)
                await existing.LoadAsync().ConfigureAwait(false);

            return;
        }

        await OpenFileInternalAsync(
            filePath,
            settings,
            FileEncoding.Auto,
            activateTab: activateTab,
            updateVisibilityAfterAdd: !deferVisibilityRefresh).ConfigureAwait(false);
    }

    public async Task CloseTabAsync(LogTabViewModel? tab)
    {
        if (tab == null)
            return;

        tab.Dispose();
        RemoveTabOrdering(tab.FileId);
        _owner.Tabs.Remove(tab);
        if (_owner.SelectedTab == tab)
            _owner.SelectedTab = _owner.FilteredTabs.FirstOrDefault();

        await Task.CompletedTask;
    }

    public async Task CloseAllTabsAsync()
    {
        foreach (var tab in _owner.Tabs.ToList())
        {
            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
        }

        _owner.Tabs.Clear();
        _owner.SelectedTab = null;
        await Task.CompletedTask;
    }

    public async Task CloseOtherTabsAsync(LogTabViewModel keepTab)
    {
        foreach (var tab in _owner.Tabs.Where(t => t != keepTab).ToList())
        {
            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
            _owner.Tabs.Remove(tab);
        }

        _owner.SelectedTab = keepTab;
        await Task.CompletedTask;
    }

    public async Task CloseAllButPinnedAsync()
    {
        foreach (var tab in _owner.Tabs.Where(t => !t.IsPinned).ToList())
        {
            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
            _owner.Tabs.Remove(tab);
        }

        if (_owner.SelectedTab != null && !_owner.Tabs.Contains(_owner.SelectedTab))
            _owner.SelectedTab = _owner.FilteredTabs.FirstOrDefault();

        await Task.CompletedTask;
    }

    public void TogglePinTab(LogTabViewModel tab)
    {
        tab.IsPinned = !tab.IsPinned;
        if (tab.IsPinned)
            _tabPinOrder[tab.FileId] = ++_nextTabPinOrder;
        else
            _tabPinOrder.Remove(tab.FileId);
    }

    public void UpdateTabVisibilityStates()
    {
        UpdateTabVisibilityStates(_owner.FilteredTabs.ToList());
    }

    public void UpdateTabVisibilityStates(IReadOnlyCollection<LogTabViewModel> filteredTabs)
    {
        if (_owner.IsShuttingDown)
            return;

        var visibleIds = filteredTabs.Select(t => t.FileId).ToHashSet();
        foreach (var tab in _owner.Tabs)
        {
            if (visibleIds.Contains(tab.FileId))
                tab.OnBecameVisible();
            else
                tab.OnBecameHidden();
        }

        UpdateVisibleTabTailingModes();
    }

    public void UpdateVisibleTabTailingModes()
    {
        if (_owner.IsShuttingDown)
            return;

        foreach (var tab in _owner.Tabs)
        {
            if (!tab.IsVisible)
                continue;

            var pollingMs = tab == _owner.SelectedTab ? ActiveTabTailPollingMs : BackgroundTabTailPollingMs;
            tab.ApplyVisibleTailingMode(pollingMs);
        }
    }

    public bool RunLifecycleMaintenance()
    {
        if (_owner.IsShuttingDown || _owner.Tabs.Count == 0)
            return false;

        foreach (var hiddenTab in _owner.Tabs.Where(t => !t.IsVisible))
            hiddenTab.SuspendTailing();

        var now = DateTime.UtcNow;
        var toPurge = _owner.Tabs
            .Where(t => !t.IsVisible
                && !t.IsPinned
                && t.LastHiddenAtUtc != DateTime.MinValue
                && now - t.LastHiddenAtUtc >= _owner.HiddenTabPurgeAfter)
            .ToList();

        if (toPurge.Count == 0)
            return false;

        foreach (var tab in toPurge)
        {
            if (_owner.SelectedTab == tab)
                _owner.SelectedTab = null;

            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
            _owner.Tabs.Remove(tab);
        }

        return true;
    }

    private async Task OpenFileInternalAsync(
        string filePath,
        AppSettings settings,
        FileEncoding encoding,
        bool isPinned = false,
        bool activateTab = true,
        bool updateVisibilityAfterAdd = true)
    {
        if (_owner.IsShuttingDown)
            return;

        var entry = await _fileRepo.GetByPathAsync(filePath).ConfigureAwait(false);
        if (entry == null)
        {
            entry = new LogFileEntry { FilePath = filePath };
            await _fileRepo.AddAsync(entry).ConfigureAwait(false);
        }
        else
        {
            entry.LastOpenedAt = DateTime.UtcNow;
            await _fileRepo.UpdateAsync(entry).ConfigureAwait(false);
        }

        var tab = new LogTabViewModel(entry.Id, filePath, _logReader, _tailService, _encodingDetectionService, settings)
        {
            AutoScrollEnabled = _owner.GlobalAutoScrollEnabled,
            IsPinned = isPinned
        };

        tab.Encoding = encoding;
        await tab.LoadAsync().ConfigureAwait(false);
        if (_owner.IsShuttingDown)
        {
            tab.BeginShutdown();
            tab.Dispose();
            return;
        }

        _tabOpenOrder[entry.Id] = ++_nextTabOpenOrder;
        if (isPinned)
            _tabPinOrder[entry.Id] = ++_nextTabPinOrder;

        _owner.Tabs.Add(tab);
        if (activateTab)
            _owner.SelectedTab = tab;

        if (updateVisibilityAfterAdd)
            UpdateTabVisibilityStates();
    }

    private long GetOpenSortKey(LogTabViewModel tab)
    {
        if (_tabOpenOrder.TryGetValue(tab.FileId, out var order))
            return order;

        var assigned = ++_nextTabOpenOrder;
        _tabOpenOrder[tab.FileId] = assigned;
        return assigned;
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

    private void RemoveTabOrdering(string fileId)
    {
        _tabOpenOrder.Remove(fileId);
        _tabPinOrder.Remove(fileId);
    }
}
