namespace LogReader.App.Services;

using System.IO;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class TabWorkspaceService
{
    private const int ActiveTabTailPollingMs = 250;
    private const int BackgroundTabTailPollingMs = 2000;

    private readonly ITabWorkspaceHost _host;
    private readonly LogFileCatalogService _fileCatalogService;
    private readonly ILogReaderService _logReader;
    private readonly IFileTailService _tailService;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly Dictionary<string, long> _tabOpenOrder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _tabPinOrder = new(StringComparer.Ordinal);
    private long _nextTabOpenOrder;
    private long _nextTabPinOrder;

    public TabWorkspaceService(
        ITabWorkspaceHost host,
        ILogFileRepository fileRepo,
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        LogFileCatalogService? fileCatalogService = null)
    {
        _host = host;
        _fileCatalogService = fileCatalogService ?? new LogFileCatalogService(fileRepo);
        _logReader = logReader;
        _tailService = tailService;
        _encodingDetectionService = encodingDetectionService;
    }

    internal IReadOnlyDictionary<string, long> OpenOrderSnapshot => _tabOpenOrder;

    internal IReadOnlyDictionary<string, long> PinOrderSnapshot => _tabPinOrder;

    public IReadOnlyList<LogTabViewModel> OrderTabsForDisplay(IEnumerable<LogTabViewModel> scopedTabs)
    {
        var tabList = scopedTabs.ToList();
        var pinnedLane = tabList
            .Where(t => t.IsPinned)
            .OrderBy(GetPinSortKey)
            .ThenBy(GetOpenSortKey);
        var unpinnedLane = tabList
            .Where(t => !t.IsPinned)
            .OrderBy(GetOpenSortKey);
        return pinnedLane.Concat(unpinnedLane).ToList();
    }

    public IReadOnlyList<LogTabViewModel> OrderTabsForDashboardDisplay(
        IEnumerable<LogTabViewModel> scopedTabs,
        IReadOnlyList<string> orderedFileIds)
    {
        var tabList = scopedTabs.ToList();
        var fileOrderById = orderedFileIds
            .Where(fileId => !string.IsNullOrWhiteSpace(fileId))
            .Distinct(StringComparer.Ordinal)
            .Select((fileId, index) => new { fileId, index })
            .ToDictionary(item => item.fileId, item => item.index, StringComparer.Ordinal);

        var pinnedLane = tabList
            .Where(t => t.IsPinned)
            .OrderBy(tab => GetDashboardSortKey(tab, fileOrderById))
            .ThenBy(GetPinSortKey)
            .ThenBy(GetOpenSortKey);
        var unpinnedLane = tabList
            .Where(t => !t.IsPinned)
            .OrderBy(tab => GetDashboardSortKey(tab, fileOrderById))
            .ThenBy(GetOpenSortKey);
        return pinnedLane.Concat(unpinnedLane).ToList();
    }

    public async Task OpenFilePathAsync(
        string filePath,
        AppSettings settings,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default)
    {
        if (_host.IsShuttingDown)
            return;

        ct.ThrowIfCancellationRequested();
        var existing = _host.Tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (activateTab)
                _host.SelectedTab = existing;

            if (reloadIfLoadError && existing.HasLoadError)
            {
                ct.ThrowIfCancellationRequested();
                await existing.LoadAsync();
                ct.ThrowIfCancellationRequested();
            }

            return;
        }

        await OpenFileInternalAsync(
            filePath,
            settings,
            FileEncoding.Auto,
            activateTab: activateTab,
            updateVisibilityAfterAdd: !deferVisibilityRefresh,
            ct: ct);
    }

    public async Task CloseTabAsync(LogTabViewModel? tab)
    {
        if (tab == null)
            return;

        tab.Dispose();
        RemoveTabOrdering(tab.FileId);
        _host.Tabs.Remove(tab);
        if (_host.SelectedTab == tab)
            _host.SelectedTab = _host.GetFilteredTabsSnapshot().FirstOrDefault();

        await Task.CompletedTask;
    }

    public async Task CloseAllTabsAsync()
    {
        foreach (var tab in _host.Tabs.ToList())
        {
            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
        }

        _host.Tabs.Clear();
        _host.SelectedTab = null;
        await Task.CompletedTask;
    }

    public async Task CloseOtherTabsAsync(LogTabViewModel keepTab)
    {
        foreach (var tab in _host.Tabs.Where(t => t != keepTab).ToList())
        {
            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
            _host.Tabs.Remove(tab);
        }

        _host.SelectedTab = keepTab;
        await Task.CompletedTask;
    }

    public async Task CloseAllButPinnedAsync()
    {
        foreach (var tab in _host.Tabs.Where(t => !t.IsPinned).ToList())
        {
            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
            _host.Tabs.Remove(tab);
        }

        if (_host.SelectedTab != null && !_host.Tabs.Contains(_host.SelectedTab))
            _host.SelectedTab = _host.GetFilteredTabsSnapshot().FirstOrDefault();

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
        UpdateTabVisibilityStates(_host.GetFilteredTabsSnapshot());
    }

    public void UpdateTabVisibilityStates(IReadOnlyCollection<LogTabViewModel> filteredTabs)
    {
        if (_host.IsShuttingDown)
            return;

        var visibleIds = filteredTabs.Select(t => t.FileId).ToHashSet();
        foreach (var tab in _host.Tabs)
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
        if (_host.IsShuttingDown)
            return;

        foreach (var tab in _host.Tabs)
        {
            if (!tab.IsVisible)
                continue;

            var pollingMs = tab == _host.SelectedTab ? ActiveTabTailPollingMs : BackgroundTabTailPollingMs;
            tab.ApplyVisibleTailingMode(pollingMs);
        }
    }

    public async Task RebindOpenTabsAsync()
    {
        foreach (var tab in _host.Tabs)
        {
            var previousFileId = tab.FileId;
            var entry = await _fileCatalogService.RegisterOpenAsync(tab.FilePath, DateTime.UtcNow);
            if (string.Equals(previousFileId, entry.Id, StringComparison.Ordinal))
                continue;

            RebindTabOrdering(previousFileId, entry.Id);
            tab.UpdateFileId(entry.Id);
        }
    }

    public bool RunLifecycleMaintenance()
    {
        if (_host.IsShuttingDown || _host.Tabs.Count == 0)
            return false;

        foreach (var hiddenTab in _host.Tabs.Where(t => !t.IsVisible))
            hiddenTab.SuspendTailing();

        var now = DateTime.UtcNow;
        var toPurge = _host.Tabs
            .Where(t => !t.IsVisible
                && !t.IsPinned
                && t.LastHiddenAtUtc != DateTime.MinValue
                && now - t.LastHiddenAtUtc >= _host.HiddenTabPurgeAfter)
            .ToList();

        if (toPurge.Count == 0)
            return false;

        foreach (var tab in toPurge)
        {
            if (_host.SelectedTab == tab)
                _host.SelectedTab = null;

            tab.Dispose();
            RemoveTabOrdering(tab.FileId);
            _host.Tabs.Remove(tab);
        }

        return true;
    }

    private async Task OpenFileInternalAsync(
        string filePath,
        AppSettings settings,
        FileEncoding encoding,
        bool isPinned = false,
        bool activateTab = true,
        bool updateVisibilityAfterAdd = true,
        CancellationToken ct = default)
    {
        if (_host.IsShuttingDown)
            return;

        ct.ThrowIfCancellationRequested();
        var entry = await _fileCatalogService.RegisterOpenAsync(filePath, DateTime.UtcNow);

        var tab = new LogTabViewModel(entry.Id, filePath, _logReader, _tailService, _encodingDetectionService, settings, skipInitialEncodingResolution: true)
        {
            AutoScrollEnabled = _host.GlobalAutoScrollEnabled,
            IsPinned = isPinned
        };

        tab.Encoding = encoding;
        var cancellationRegistration = ct.CanBeCanceled
            ? ct.Register(static state => ((LogTabViewModel)state!).BeginShutdown(), tab)
            : default;
        try
        {
            await tab.LoadAsync().WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            tab.BeginShutdown();
            tab.Dispose();
            throw;
        }
        finally
        {
            cancellationRegistration.Dispose();
        }

        if (_host.IsShuttingDown)
        {
            tab.BeginShutdown();
            tab.Dispose();
            return;
        }

        if (ct.IsCancellationRequested)
        {
            tab.BeginShutdown();
            tab.Dispose();
            ct.ThrowIfCancellationRequested();
        }

        _tabOpenOrder[entry.Id] = ++_nextTabOpenOrder;
        if (isPinned)
            _tabPinOrder[entry.Id] = ++_nextTabPinOrder;

        _host.Tabs.Add(tab);
        if (activateTab)
            _host.SelectedTab = tab;

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

    private static int GetDashboardSortKey(
        LogTabViewModel tab,
        IReadOnlyDictionary<string, int> fileOrderById)
        => fileOrderById.TryGetValue(tab.FileId, out var order)
            ? order
            : int.MaxValue;

    private void RemoveTabOrdering(string fileId)
    {
        _tabOpenOrder.Remove(fileId);
        _tabPinOrder.Remove(fileId);
    }

    private void RebindTabOrdering(string oldFileId, string newFileId)
    {
        if (_tabOpenOrder.Remove(oldFileId, out var openOrder))
            _tabOpenOrder[newFileId] = openOrder;

        if (_tabPinOrder.Remove(oldFileId, out var pinOrder))
            _tabPinOrder[newFileId] = pinOrder;
    }
}
