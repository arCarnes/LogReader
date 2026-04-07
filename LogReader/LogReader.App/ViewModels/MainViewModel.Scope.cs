namespace LogReader.App.ViewModels;

using System.Collections.Specialized;
using LogReader.App.Services;

public partial class MainViewModel
{
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

        if (!string.IsNullOrEmpty(tab.ScopeDashboardId))
        {
            var dashboard = Groups.FirstOrDefault(group => string.Equals(group.Id, tab.ScopeDashboardId, StringComparison.Ordinal));
            if (dashboard != null)
            {
                _dashboardWorkspace.CancelDashboardLoad();
                _dashboardScope.SelectDashboard(Groups, dashboard);
                ActiveDashboardId = dashboard.Id;
                NotifyFilteredTabsChanged();
                return;
            }
        }

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

    public HashSet<string> ResolveFileIds(LogGroupViewModel group)
        => _dashboardWorkspace.ResolveFileIds(group);

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsShuttingDown)
            return;

        if (!_tabCollectionRefreshCoordinator.TryHandleCollectionChanged(
                e,
                _dashboardWorkspace.HasActiveModifiers,
                out var request))
        {
            return;
        }

        if (request != null)
            RunTabMemberRefreshRequest(request);

        NotifyFilteredTabsChanged();
    }

    internal void BeginTabCollectionNotificationSuppression()
    {
        _tabCollectionRefreshCoordinator.Begin();
    }

    internal void EndTabCollectionNotificationSuppression()
    {
        var request = _tabCollectionRefreshCoordinator.End(_dashboardWorkspace.HasActiveModifiers);
        if (request == null)
            return;

        RunTabMemberRefreshRequest(request);
        NotifyFilteredTabsChanged();
    }

    private void RunTabMemberRefreshRequest(TabMemberRefreshRequest request)
    {
        if (request.RequiresFullRefresh)
        {
            RunRecoverableBackgroundCommand(() => _dashboardWorkspace.RefreshAllMemberFilesAsync());
            return;
        }

        if (request.ChangedFilePaths.Count > 0)
            RunRecoverableBackgroundCommand(() => _dashboardWorkspace.RefreshMemberFilesForFileIdsAsync(request.ChangedFilePaths));
    }

    private IReadOnlyList<LogTabViewModel> GetAdHocTabs()
    {
        if (_dashboardWorkspace.TryGetAdHocEffectivePaths(out var adHocEffectivePaths))
        {
            return _tabWorkspace.OrderTabsForDisplay(
                Tabs.Where(tab => tab.IsAdHocScope && adHocEffectivePaths.Contains(tab.FilePath)));
        }

        return _tabWorkspace.OrderTabsForDisplay(GetNormalAdHocTabs());
    }

    private IReadOnlyList<LogTabViewModel> GetNormalAdHocTabs()
        => _dashboardScope.GetAdHocTabs(Tabs);

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
                await OpenFilePathInScopeAsync(
                    filePath,
                    ActiveDashboardId,
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

    internal async Task<IReadOnlyDictionary<string, LogTabViewModel>> EnsureBackgroundTabsOpenAsync(
        IReadOnlyList<string> filePaths,
        string? scopeDashboardId,
        CancellationToken ct = default)
    {
        var normalizedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var tabsByPath = new Dictionary<string, LogTabViewModel>(StringComparer.OrdinalIgnoreCase);
        if (normalizedPaths.Count == 0)
            return tabsByPath;

        var pathsToOpen = new List<string>();
        foreach (var filePath in normalizedPaths)
        {
            var existingTab = FindTabInScope(filePath, scopeDashboardId);
            if (existingTab != null)
            {
                tabsByPath[filePath] = existingTab;
                continue;
            }

            pathsToOpen.Add(filePath);
        }

        if (pathsToOpen.Count == 0)
            return tabsByPath;

        BeginTabCollectionNotificationSuppression();
        try
        {
            foreach (var filePath in pathsToOpen)
            {
                await OpenFilePathInScopeAsync(
                    filePath,
                    scopeDashboardId,
                    reloadIfLoadError: true,
                    activateTab: false,
                    deferVisibilityRefresh: true,
                    ct);
            }
        }
        finally
        {
            EndTabCollectionNotificationSuppression();
        }

        foreach (var filePath in pathsToOpen)
        {
            var tab = FindTabInScope(filePath, scopeDashboardId);
            if (tab != null)
                tabsByPath[filePath] = tab;
        }

        return tabsByPath;
    }

    internal void NotifyFilteredTabsChanged()
    {
        var filteredTabs = GetFilteredTabsSnapshot();
        _tabWorkspace.UpdateTabVisibilityStates(filteredTabs);
        OnPropertyChanged(nameof(FilteredTabs));
        OnPropertyChanged(nameof(IsCurrentScopeEmpty));
        OnPropertyChanged(nameof(ShouldShowEmptyState));
        NotifyScopeMetadataChanged();

        if (SelectedTab != null && !filteredTabs.Contains(SelectedTab))
            SelectedTab = filteredTabs.FirstOrDefault();
        else if (SelectedTab == null && filteredTabs.Count > 0)
            SelectedTab = filteredTabs[0];

        SearchPanel.OnScopeContextChanged();
        FilterPanel.OnScopeContextChanged();
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

    partial void OnActiveDashboardIdChanging(string? value)
    {
        var nextScopeKey = WorkspaceScopeKey.FromDashboardId(value);
        SearchPanel.OnScopeChanging(nextScopeKey);
        FilterPanel.OnScopeChanging(nextScopeKey);
    }

    partial void OnSelectedTabChanged(LogTabViewModel? value)
    {
        if (!IsShuttingDown)
            _tabWorkspace.UpdateVisibleTabTailingModes();

        OnPropertyChanged(nameof(ShouldShowEmptyState));
        SearchPanel.OnSelectedTabChanged(value);
        FilterPanel.OnSelectedTabChanged(value);
        _dashboardWorkspace.UpdateSelectedMemberFileHighlights();
    }

    internal WorkspaceScopeSnapshot GetActiveScopeSnapshot()
    {
        var openTabs = GetFilteredTabsSnapshot()
            .Select(tab => new WorkspaceOpenTabSnapshot(tab))
            .ToList();

        return new WorkspaceScopeSnapshot(
            WorkspaceScopeKey.FromDashboardId(ActiveDashboardId),
            openTabs,
            GetEffectiveScopeMembershipSnapshot(openTabs));
    }

    private IReadOnlyList<WorkspaceScopeMemberSnapshot> GetEffectiveScopeMembershipSnapshot(
        IReadOnlyList<WorkspaceOpenTabSnapshot> openTabs)
    {
        if (string.IsNullOrEmpty(ActiveDashboardId))
            return BuildAdHocScopeMembershipSnapshot(openTabs);

        var activeDashboard = GetActiveDashboard();
        if (activeDashboard != null && activeDashboard.MemberFiles.Count > 0)
        {
            return activeDashboard.MemberFiles
                .Select(member => new WorkspaceScopeMemberSnapshot(member.FileId, member.FilePath))
                .ToList();
        }

        if (_dashboardWorkspace.TryGetDashboardEffectivePaths(ActiveDashboardId, out var effectivePaths))
            return BuildMembershipSnapshotFromPaths(effectivePaths, openTabs);

        return BuildMembershipSnapshotFromOpenTabs(openTabs);
    }

    private IReadOnlyList<WorkspaceScopeMemberSnapshot> BuildAdHocScopeMembershipSnapshot(
        IReadOnlyList<WorkspaceOpenTabSnapshot> openTabs)
    {
        if (_dashboardWorkspace.TryGetAdHocEffectivePaths(out var effectivePaths))
            return BuildMembershipSnapshotFromPaths(effectivePaths, openTabs);

        return BuildMembershipSnapshotFromOpenTabs(openTabs);
    }

    private static IReadOnlyList<WorkspaceScopeMemberSnapshot> BuildMembershipSnapshotFromOpenTabs(
        IReadOnlyList<WorkspaceOpenTabSnapshot> openTabs)
    {
        return openTabs
            .Select(tab => new WorkspaceScopeMemberSnapshot(tab.FileId, tab.FilePath))
            .ToList();
    }

    private static IReadOnlyList<WorkspaceScopeMemberSnapshot> BuildMembershipSnapshotFromPaths(
        IReadOnlySet<string> effectivePaths,
        IReadOnlyList<WorkspaceOpenTabSnapshot> openTabs)
    {
        var membership = new List<WorkspaceScopeMemberSnapshot>(effectivePaths.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var openTabsByPath = openTabs
            .GroupBy(tab => tab.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var openTab in openTabs)
        {
            if (effectivePaths.Contains(openTab.FilePath) && seenPaths.Add(openTab.FilePath))
                membership.Add(new WorkspaceScopeMemberSnapshot(openTab.FileId, openTab.FilePath));
        }

        foreach (var effectivePath in effectivePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!seenPaths.Add(effectivePath))
                continue;

            if (openTabsByPath.TryGetValue(effectivePath, out var openTab))
                membership.Add(new WorkspaceScopeMemberSnapshot(openTab.FileId, openTab.FilePath));
            else
                membership.Add(new WorkspaceScopeMemberSnapshot(string.Empty, effectivePath));
        }

        return membership;
    }

    partial void OnGlobalAutoScrollEnabledChanged(bool value)
    {
        var syncVersion = Interlocked.Increment(ref _autoScrollSyncVersion);
        foreach (var tab in Tabs)
            tab.AutoScrollEnabled = value;

        if (value)
            _ = SyncTabsToAutoScrollBottomAsync(syncVersion);
    }

    partial void OnDashboardTreeFilterChanged(string value)
        => _dashboardWorkspace.ApplyDashboardTreeFilter();

    internal LogTabViewModel? FindTabInScope(string filePath, string? scopeDashboardId)
    {
        return Tabs.FirstOrDefault(tab =>
            string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal));
    }

    internal Task OpenFilePathInScopeAsync(
        string filePath,
        string? scopeDashboardId,
        bool reloadIfLoadError = false,
        bool activateTab = true,
        bool deferVisibilityRefresh = false,
        CancellationToken ct = default)
    {
        return _tabWorkspace.OpenFilePathAsync(
            filePath,
            scopeDashboardId,
            _settings,
            reloadIfLoadError,
            activateTab,
            deferVisibilityRefresh,
            ct);
    }

    private async Task<string?> ResolveTargetScopeDashboardIdForOpenAsync(string filePath)
    {
        var fileId = await TryResolveFileIdByPathAsync(filePath);
        if (!string.IsNullOrEmpty(ActiveDashboardId))
        {
            if (_dashboardWorkspace.TryGetDashboardEffectivePaths(ActiveDashboardId, out var effectivePaths) &&
                effectivePaths.Contains(filePath))
            {
                return ActiveDashboardId;
            }

            if (!string.IsNullOrEmpty(fileId) &&
                _dashboardScope.DashboardContainsFile(Groups, ActiveDashboardId, fileId))
            {
                return ActiveDashboardId;
            }
        }

        var modifierDashboardId = _dashboardWorkspace.FindDashboardForModifierPath(filePath);
        if (!string.IsNullOrEmpty(modifierDashboardId))
            return modifierDashboardId;

        if (_dashboardWorkspace.IsAdHocModifierPath(filePath))
            return null;

        if (!string.IsNullOrEmpty(fileId))
        {
            var containingGroup = _dashboardScope.FindContainingDashboard(Groups, fileId);
            if (containingGroup != null)
                return containingGroup.Id;
        }

        return null;
    }

    private async Task<string?> ResolveTargetScopeDashboardIdForNavigationAsync(string filePath)
    {
        if (FindTabInScope(filePath, ActiveDashboardId) != null)
            return ActiveDashboardId;

        var fileId = await TryResolveFileIdByPathAsync(filePath);
        if (!string.IsNullOrEmpty(ActiveDashboardId))
        {
            if (_dashboardWorkspace.TryGetDashboardEffectivePaths(ActiveDashboardId, out var effectivePaths) &&
                effectivePaths.Contains(filePath))
            {
                return ActiveDashboardId;
            }

            if (!string.IsNullOrEmpty(fileId) &&
                _dashboardScope.DashboardContainsFile(Groups, ActiveDashboardId, fileId))
            {
                return ActiveDashboardId;
            }
        }
        else if (_dashboardWorkspace.TryGetAdHocEffectivePaths(out var adHocEffectivePaths) &&
                 adHocEffectivePaths.Contains(filePath))
        {
            return null;
        }

        var modifierDashboardId = _dashboardWorkspace.FindDashboardForModifierPath(filePath);
        if (!string.IsNullOrEmpty(modifierDashboardId))
            return modifierDashboardId;

        if (_dashboardWorkspace.IsAdHocModifierPath(filePath))
            return null;

        if (!string.IsNullOrEmpty(fileId))
        {
            var containingGroup = _dashboardScope.FindContainingDashboard(Groups, fileId);
            if (containingGroup != null)
                return containingGroup.Id;
        }

        return null;
    }

    private async Task<string?> TryResolveFileIdByPathAsync(string filePath)
    {
        var entriesByPath = await _fileCatalogService.GetByPathsAsync(new[] { filePath });
        return entriesByPath.TryGetValue(filePath, out var entry)
            ? entry.Id
            : null;
    }

    private async Task SyncTabsToAutoScrollBottomAsync(int syncVersion)
    {
        var tabs = Tabs.ToList();
        foreach (var tab in tabs)
        {
            if (IsShuttingDown ||
                !GlobalAutoScrollEnabled ||
                Volatile.Read(ref _autoScrollSyncVersion) != syncVersion)
            {
                return;
            }

            if (tab.IsShutdownOrDisposed || tab.IsLoading || tab.HasNoLineIndex)
                continue;

            await tab.MoveViewportToBottomAsync();
        }
    }
}
