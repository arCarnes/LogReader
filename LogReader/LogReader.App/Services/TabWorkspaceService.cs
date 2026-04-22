namespace LogReader.App.Services;

using System.IO;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class TabWorkspaceService
{
    private const int ActiveTabTailPollingMs = 250;
    private const int BackgroundTabTailPollingMs = 2000;
    private static readonly TimeSpan DefaultRecentTabStateRetention = TimeSpan.FromMinutes(2);

    internal sealed class PreparedTabOpen : IDisposable
    {
        private int _isCommitted;
        private int _isDisposed;

        public PreparedTabOpen(
            LogTabViewModel tab,
            string filePath,
            string? scopeDashboardId,
            bool shouldStartPinned,
            bool shouldClearRecentState)
        {
            Tab = tab;
            FilePath = filePath;
            ScopeDashboardId = scopeDashboardId;
            ShouldStartPinned = shouldStartPinned;
            ShouldClearRecentState = shouldClearRecentState;
        }

        public LogTabViewModel Tab { get; }

        public string FilePath { get; }

        public string? ScopeDashboardId { get; }

        public bool ShouldStartPinned { get; }

        public bool ShouldClearRecentState { get; }

        public void MarkCommitted()
        {
            Interlocked.Exchange(ref _isCommitted, 1);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0 ||
                Volatile.Read(ref _isCommitted) != 0)
            {
                return;
            }

            Tab.BeginShutdown();
            Tab.Dispose();
        }
    }

    private readonly ITabWorkspaceHost _host;
    private readonly LogFileCatalogService _fileCatalogService;
    private readonly ILogReaderService _logReader;
    private readonly IFileTailService _tailService;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly FileSessionRegistry _fileSessionRegistry;
    private readonly Dictionary<RecentTabStateKey, RecentTabStateEntry> _recentClosedTabs = new();
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
        _fileSessionRegistry = new FileSessionRegistry(logReader, tailService, encodingDetectionService);
    }

    internal IReadOnlyDictionary<string, long> OpenOrderSnapshot => _tabOpenOrder;

    internal IReadOnlyDictionary<string, long> PinOrderSnapshot => _tabPinOrder;

    internal TimeSpan RecentTabStateRetention { get; set; } = DefaultRecentTabStateRetention;

    internal TimeSpan FileSessionWarmRetention
    {
        get => _fileSessionRegistry.WarmRetentionDuration;
        set => _fileSessionRegistry.WarmRetentionDuration = value;
    }

    public LogTabViewModel? FindOpenTab(string filePath, string? scopeDashboardId)
    {
        return _host.Tabs.FirstOrDefault(tab =>
            string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
            MatchesScope(tab, scopeDashboardId));
    }

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
        string? scopeDashboardId,
        AppSettings settings,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default)
    {
        if (_host.IsShuttingDown)
            return;

        ct.ThrowIfCancellationRequested();
        var existing = FindOpenTab(filePath, scopeDashboardId);
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
            scopeDashboardId,
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

        CacheRecentTabState(tab);
        tab.Dispose();
        RemoveTabOrdering(tab);
        _host.Tabs.Remove(tab);
        if (_host.SelectedTab == tab)
            _host.SelectedTab = _host.GetFilteredTabsSnapshot().FirstOrDefault();

        await Task.CompletedTask;
    }

    public async Task CloseAllTabsAsync()
    {
        foreach (var tab in _host.Tabs.Where(tab => MatchesScope(tab, _host.CurrentScopeDashboardId)).ToList())
        {
            CacheRecentTabState(tab);
            tab.Dispose();
            RemoveTabOrdering(tab);
            _host.Tabs.Remove(tab);
        }

        if (_host.SelectedTab != null && !_host.Tabs.Contains(_host.SelectedTab))
            _host.SelectedTab = _host.GetFilteredTabsSnapshot().FirstOrDefault();

        await Task.CompletedTask;
    }

    public async Task CloseOtherTabsAsync(LogTabViewModel keepTab)
    {
        foreach (var tab in _host.Tabs.Where(tab => tab != keepTab && MatchesScope(tab, keepTab.ScopeDashboardId)).ToList())
        {
            CacheRecentTabState(tab);
            tab.Dispose();
            RemoveTabOrdering(tab);
            _host.Tabs.Remove(tab);
        }

        _host.SelectedTab = keepTab;
        await Task.CompletedTask;
    }

    public async Task CloseAllButPinnedAsync()
    {
        foreach (var tab in _host.Tabs
                     .Where(tab => !tab.IsPinned && MatchesScope(tab, _host.CurrentScopeDashboardId))
                     .ToList())
        {
            CacheRecentTabState(tab);
            tab.Dispose();
            RemoveTabOrdering(tab);
            _host.Tabs.Remove(tab);
        }

        if (_host.SelectedTab != null && !_host.Tabs.Contains(_host.SelectedTab))
            _host.SelectedTab = _host.GetFilteredTabsSnapshot().FirstOrDefault();

        await Task.CompletedTask;
    }

    internal IReadOnlyDictionary<string, RecentTabState> CaptureScopeTabStates(
        string? scopeDashboardId,
        bool preserveFilterSnapshots)
    {
        var capturedStates = new Dictionary<string, RecentTabState>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in _host.Tabs.Where(tab => MatchesScope(tab, scopeDashboardId)))
        {
            capturedStates[tab.FilePath] = CloneRecentTabState(
                tab.CaptureRecentState(),
                preserveFilterSnapshots);
        }

        return capturedStates;
    }

    internal Task FlushScopeTabsAsync(string? scopeDashboardId)
    {
        foreach (var tab in _host.Tabs.Where(tab => MatchesScope(tab, scopeDashboardId)).ToList())
        {
            if (_host.SelectedTab == tab)
                _host.SelectedTab = null;

            CacheRecentTabState(tab);
            tab.Dispose();
            RemoveTabOrdering(tab);
            _host.Tabs.Remove(tab);
        }

        ClearRecentTabStateForScope(scopeDashboardId);
        if (_host.SelectedTab != null && !_host.Tabs.Contains(_host.SelectedTab))
            _host.SelectedTab = _host.GetFilteredTabsSnapshot().FirstOrDefault();

        return Task.CompletedTask;
    }

    internal void SeedRecentTabStatesForScope(
        string? scopeDashboardId,
        IReadOnlyDictionary<string, RecentTabState> recentStates,
        IEnumerable<string> allowedFilePaths)
    {
        ClearRecentTabStateForScope(scopeDashboardId);
        if (recentStates.Count == 0)
            return;

        var allowedPaths = allowedFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedPaths.Count == 0)
            return;

        var capturedAtUtc = DateTime.UtcNow;
        foreach (var (filePath, state) in recentStates)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            if (!allowedPaths.Contains(normalizedPath))
                continue;

            _recentClosedTabs[new RecentTabStateKey(normalizedPath, scopeDashboardId)] = new RecentTabStateEntry(
                CloneRecentTabState(state, preserveFilterSnapshots: false),
                capturedAtUtc);
        }
    }

    public void TogglePinTab(LogTabViewModel tab)
    {
        tab.IsPinned = !tab.IsPinned;
        if (tab.IsPinned)
            _tabPinOrder[tab.TabInstanceId] = ++_nextTabPinOrder;
        else
            _tabPinOrder.Remove(tab.TabInstanceId);
    }

    public void UpdateTabVisibilityStates()
    {
        UpdateTabVisibilityStates(_host.GetFilteredTabsSnapshot());
    }

    public void UpdateTabVisibilityStates(IReadOnlyCollection<LogTabViewModel> filteredTabs)
    {
        if (_host.IsShuttingDown)
            return;

        var visibleIds = filteredTabs.Select(tab => tab.TabInstanceId).ToHashSet(StringComparer.Ordinal);
        foreach (var tab in _host.Tabs)
        {
            if (visibleIds.Contains(tab.TabInstanceId))
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

            tab.UpdateFileId(entry.Id);
        }
    }

    public bool RunLifecycleMaintenance()
    {
        PruneExpiredRecentTabStates();
        _fileSessionRegistry.SweepExpiredSessions();

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

            ClearRecentTabState(tab.FilePath, tab.ScopeDashboardId);
            tab.Dispose();
            RemoveTabOrdering(tab);
            _host.Tabs.Remove(tab);
        }

        return true;
    }

    internal FileEncoding? TryGetRecentRequestedEncoding(string filePath, string? scopeDashboardId)
    {
        PruneExpiredRecentTabStates();
        return _recentClosedTabs.TryGetValue(new RecentTabStateKey(filePath, scopeDashboardId), out var entry)
            ? entry.State.RequestedEncoding
            : null;
    }

    internal LogFilterSession.FilterSnapshot? TryGetRecentFilterSnapshot(string filePath, string? scopeDashboardId)
    {
        PruneExpiredRecentTabStates();
        return _recentClosedTabs.TryGetValue(new RecentTabStateKey(filePath, scopeDashboardId), out var entry)
            ? entry.State.FilterSnapshot == null ? null : LogFilterSession.CloneSnapshot(entry.State.FilterSnapshot)
            : null;
    }

    internal void UpdateRecentTabFilterSnapshot(string filePath, string? scopeDashboardId, LogFilterSession.FilterSnapshot? snapshot)
    {
        var key = new RecentTabStateKey(filePath, scopeDashboardId);
        if (!_recentClosedTabs.TryGetValue(key, out var entry))
            return;

        var state = entry.State;
        _recentClosedTabs[key] = new RecentTabStateEntry(
            new RecentTabState
            {
                RequestedEncoding = state.RequestedEncoding,
                IsPinned = state.IsPinned,
                ViewportStartLine = state.ViewportStartLine,
                NavigateToLineNumber = state.NavigateToLineNumber,
                FilterSnapshot = snapshot == null ? null : LogFilterSession.CloneSnapshot(snapshot)
            },
            entry.CapturedAtUtc);
    }

    public void Dispose()
    {
        _recentClosedTabs.Clear();
        _fileSessionRegistry.Dispose();
    }

    private async Task OpenFileInternalAsync(
        string filePath,
        string? scopeDashboardId,
        AppSettings settings,
        FileEncoding encoding,
        bool isPinned = false,
        bool activateTab = true,
        bool updateVisibilityAfterAdd = true,
        CancellationToken ct = default)
    {
        using var prepared = await PrepareFileOpenAsync(
            filePath,
            scopeDashboardId,
            settings,
            encoding,
            isPinned,
            ct);
        if (prepared == null)
            return;

        await FinalizePreparedFileOpenAsync(prepared, activateTab, updateVisibilityAfterAdd, ct);
    }

    internal async Task<PreparedTabOpen?> PrepareFileOpenAsync(
        string filePath,
        string? scopeDashboardId,
        AppSettings settings,
        FileEncoding encoding,
        bool isPinned = false,
        CancellationToken ct = default)
    {
        if (_host.IsShuttingDown)
            return null;

        ct.ThrowIfCancellationRequested();
        var entry = await _fileCatalogService.RegisterOpenAsync(filePath, DateTime.UtcNow);
        var recentState = GetRecentTabState(filePath, scopeDashboardId);
        var initialEncoding = recentState?.RequestedEncoding ?? encoding;
        var shouldStartPinned = isPinned || recentState?.IsPinned == true;

        var tab = new LogTabViewModel(
            entry.Id,
            filePath,
            _logReader,
            _tailService,
            _encodingDetectionService,
            settings,
            skipInitialEncodingResolution: true,
            _fileSessionRegistry,
            initialEncoding,
            scopeDashboardId)
        {
            AutoScrollEnabled = _host.GlobalAutoScrollEnabled,
            IsPinned = shouldStartPinned
        };

        var prepared = new PreparedTabOpen(
            tab,
            filePath,
            scopeDashboardId,
            shouldStartPinned,
            recentState != null);
        var cancellationRegistration = ct.CanBeCanceled
            ? ct.Register(static state => ((LogTabViewModel)state!).BeginShutdown(), tab)
            : default;
        try
        {
            await tab.LoadAsync().WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (recentState != null)
                await tab.RestoreRecentStateAsync(recentState).WaitAsync(ct);

            if (_host.IsShuttingDown)
            {
                prepared.Dispose();
                return null;
            }

            ct.ThrowIfCancellationRequested();
            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }

    internal async Task FinalizePreparedFileOpenAsync(
        PreparedTabOpen prepared,
        bool activateTab = true,
        bool updateVisibilityAfterAdd = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        if (_host.IsShuttingDown)
            return;

        ct.ThrowIfCancellationRequested();
        var tab = prepared.Tab;

        _tabOpenOrder[tab.TabInstanceId] = ++_nextTabOpenOrder;
        if (prepared.ShouldStartPinned)
            _tabPinOrder[tab.TabInstanceId] = ++_nextTabPinOrder;

        try
        {
            _host.Tabs.Add(tab);
            await _host.MaterializeStoredFilterStateAsync(tab, ct).WaitAsync(ct);
            if (prepared.ShouldClearRecentState)
                ClearRecentTabState(prepared.FilePath, prepared.ScopeDashboardId);

            if (activateTab)
                _host.SelectedTab = tab;

            if (updateVisibilityAfterAdd)
                UpdateTabVisibilityStates();

            prepared.MarkCommitted();
        }
        catch
        {
            _host.Tabs.Remove(tab);
            _tabOpenOrder.Remove(tab.TabInstanceId);
            _tabPinOrder.Remove(tab.TabInstanceId);
            throw;
        }
    }

    private long GetOpenSortKey(LogTabViewModel tab)
    {
        if (_tabOpenOrder.TryGetValue(tab.TabInstanceId, out var order))
            return order;

        var assigned = ++_nextTabOpenOrder;
        _tabOpenOrder[tab.TabInstanceId] = assigned;
        return assigned;
    }

    private long GetPinSortKey(LogTabViewModel tab)
    {
        if (!tab.IsPinned)
            return long.MaxValue;

        if (_tabPinOrder.TryGetValue(tab.TabInstanceId, out var order))
            return order;

        var assigned = ++_nextTabPinOrder;
        _tabPinOrder[tab.TabInstanceId] = assigned;
        return assigned;
    }

    private static int GetDashboardSortKey(
        LogTabViewModel tab,
        IReadOnlyDictionary<string, int> fileOrderById)
        => fileOrderById.TryGetValue(tab.FileId, out var order)
            ? order
            : int.MaxValue;

    private void RemoveTabOrdering(LogTabViewModel tab)
    {
        _tabOpenOrder.Remove(tab.TabInstanceId);
        _tabPinOrder.Remove(tab.TabInstanceId);
    }

    private static bool MatchesScope(LogTabViewModel tab, string? scopeDashboardId)
    {
        return string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal);
    }

    private void CacheRecentTabState(LogTabViewModel tab)
    {
        var key = new RecentTabStateKey(tab.FilePath, tab.ScopeDashboardId);
        if (RecentTabStateRetention <= TimeSpan.Zero)
        {
            _recentClosedTabs.Remove(key);
            return;
        }

        _recentClosedTabs[key] = new RecentTabStateEntry(
            tab.CaptureRecentState(),
            DateTime.UtcNow);
    }

    private static RecentTabState CloneRecentTabState(RecentTabState state, bool preserveFilterSnapshots)
    {
        return new RecentTabState
        {
            RequestedEncoding = state.RequestedEncoding,
            IsPinned = state.IsPinned,
            ViewportStartLine = state.ViewportStartLine,
            NavigateToLineNumber = state.NavigateToLineNumber,
            FilterSnapshot = preserveFilterSnapshots && state.FilterSnapshot != null
                ? LogFilterSession.CloneSnapshot(state.FilterSnapshot)
                : null
        };
    }

    private RecentTabState? GetRecentTabState(string filePath, string? scopeDashboardId)
    {
        PruneExpiredRecentTabStates();
        return _recentClosedTabs.TryGetValue(new RecentTabStateKey(filePath, scopeDashboardId), out var entry)
            ? entry.State
            : null;
    }

    private void ClearRecentTabState(string filePath, string? scopeDashboardId)
        => _recentClosedTabs.Remove(new RecentTabStateKey(filePath, scopeDashboardId));

    private void ClearRecentTabStateForScope(string? scopeDashboardId)
    {
        foreach (var key in _recentClosedTabs.Keys
                     .Where(key => string.Equals(key.ScopeDashboardId, scopeDashboardId ?? string.Empty, StringComparison.Ordinal))
                     .ToList())
        {
            _recentClosedTabs.Remove(key);
        }
    }

    private void PruneExpiredRecentTabStates()
    {
        if (_recentClosedTabs.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var (key, entry) in _recentClosedTabs.ToList())
        {
            if (now - entry.CapturedAtUtc < RecentTabStateRetention)
                continue;

            _recentClosedTabs.Remove(key);
        }
    }

    private sealed class RecentTabStateEntry
    {
        public RecentTabStateEntry(RecentTabState state, DateTime capturedAtUtc)
        {
            State = state;
            CapturedAtUtc = capturedAtUtc;
        }

        public RecentTabState State { get; }

        public DateTime CapturedAtUtc { get; }
    }
}
