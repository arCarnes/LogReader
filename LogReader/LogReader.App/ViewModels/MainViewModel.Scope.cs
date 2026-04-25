namespace LogReader.App.ViewModels;

using System.Collections.Specialized;
using System.ComponentModel;
using LogReader.App.Services;
using LogReader.Core.Models;

public partial class MainViewModel
{
    internal void EnsureSelectedTabInCurrentScope()
    {
        var scopedTabs = GetFilteredTabsSnapshot();
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
        if (GetFilteredTabsSnapshot().Contains(tab))
            return;

        if (string.Equals(tab.ScopeDashboardId, ActiveDashboardId, StringComparison.Ordinal))
            return;

        if (!string.IsNullOrEmpty(tab.ScopeDashboardId))
        {
            var dashboard = Groups.FirstOrDefault(group => string.Equals(group.Id, tab.ScopeDashboardId, StringComparison.Ordinal));
            if (dashboard != null)
            {
                _dashboardActivation.CancelDashboardLoad();
                SelectDashboard(dashboard);
                ActiveDashboardId = dashboard.Id;
                NotifyFilteredTabsChanged();
                return;
            }
        }

        ActivateAdHocScope();
    }

    private void ClearActiveDashboardWhenNoTabsRemain()
    {
        if (IsDashboardLoading)
            return;

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

        if (IsDashboardLoading)
            return;

        if (BuildFilteredTabsSnapshot().Count > 0)
            return;

        ActivateAdHocScopeCore(cancelDashboardLoad: true);
    }

    public HashSet<string> ResolveFileIds(LogGroupViewModel group)
        => _dashboardWorkspace.ResolveFileIds(group);

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsShuttingDown)
            return;

        UpdateTabSubscriptions(e);

        if (!_tabCollectionRefreshCoordinator.TryHandleCollectionChanged(
                e,
                _dashboardActivation.HasActiveModifiers,
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
        var request = _tabCollectionRefreshCoordinator.End(_dashboardActivation.HasActiveModifiers);
        if (request == null)
            return;

        RunTabMemberRefreshRequest(request);
        NotifyFilteredTabsChanged();
    }

    private void RunTabMemberRefreshRequest(TabMemberRefreshRequest request)
    {
        if (request.RequiresFullRefresh)
        {
            RunRecoverableBackgroundCommand(() => _dashboardActivation.RefreshAllMemberFilesAsync());
            return;
        }

        if (request.ChangedFilePaths.Count > 0)
            RunRecoverableBackgroundCommand(() => _dashboardActivation.RefreshMemberFilesForFileIdsAsync(request.ChangedFilePaths));
    }

    private void UpdateTabSubscriptions(NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var tab in e.OldItems.OfType<LogTabViewModel>())
                tab.PropertyChanged -= Tab_PropertyChanged;
        }

        if (e.NewItems != null)
        {
            foreach (var tab in e.NewItems.OfType<LogTabViewModel>())
            {
                tab.PropertyChanged -= Tab_PropertyChanged;
                tab.PropertyChanged += Tab_PropertyChanged;
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var tab in Tabs)
            {
                tab.PropertyChanged -= Tab_PropertyChanged;
                tab.PropertyChanged += Tab_PropertyChanged;
            }
        }
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogTabViewModel.IsPinned))
            NotifyFilteredTabsChanged();

        if (sender is not LogTabViewModel tab)
            return;

        if (e.PropertyName is nameof(LogTabViewModel.TotalLines) or
            nameof(LogTabViewModel.FileSizeBytes) or
            nameof(LogTabViewModel.LastModifiedLocal))
        {
            OnPropertyChanged(nameof(AdHocMemberFiles));
            RunRecoverableBackgroundCommand(() => _dashboardActivation.RefreshMemberFilesForFileIdsAsync(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [tab.FileId] = tab.FilePath
                }));
        }
    }

    private IReadOnlyList<LogTabViewModel> GetAdHocTabs()
    {
        if (_dashboardActivation.TryGetAdHocEffectivePaths(out var adHocEffectivePaths))
        {
            return _tabWorkspace.OrderTabsForDisplay(
                Tabs.Where(tab => tab.IsAdHocScope && adHocEffectivePaths.Contains(tab.FilePath)));
        }

        return _tabWorkspace.OrderTabsForDisplay(GetNormalAdHocTabs());
    }

    private IReadOnlyList<LogTabViewModel> GetNormalAdHocTabs()
        => Tabs
            .Where(tab => tab.IsAdHocScope)
            .ToList();

    private IReadOnlyList<LogTabViewModel> GetTabsForCurrentScope()
    {
        if (string.IsNullOrEmpty(ActiveDashboardId))
            return GetNormalAdHocTabs();

        return Tabs
            .Where(tab => string.Equals(tab.ScopeDashboardId, ActiveDashboardId, StringComparison.Ordinal))
            .ToList();
    }

    private string GetAdHocScopeLabel()
    {
        var modifierLabel = _dashboardActivation.GetAdHocModifierLabel();
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

    internal void NotifyFilteredTabsChanged()
    {
        var filteredTabs = BuildFilteredTabsSnapshot();
        _filteredTabsSnapshot = filteredTabs;
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
        if (!CanExpandAdHoc && IsAdHocExpanded)
            IsAdHocExpanded = false;

        OnPropertyChanged(nameof(AdHocMemberTabs));
        OnPropertyChanged(nameof(AdHocMemberFiles));
        OnPropertyChanged(nameof(CanExpandAdHoc));
        OnPropertyChanged(nameof(CurrentScopeLabel));
        OnPropertyChanged(nameof(CurrentScopeSummaryText));
        OnPropertyChanged(nameof(AdHocScopeChipText));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(TabCountText));
    }

    internal void ActivateAdHocScope()
    {
        if (ShouldIgnoreLoadAffectingAction())
            return;

        ActivateAdHocScopeCore(cancelDashboardLoad: true);
    }

    internal void ExitDashboardScopeIfCurrentDashboardFinishedEmpty(string dashboardId)
    {
        if (!string.Equals(ActiveDashboardId, dashboardId, StringComparison.Ordinal))
            return;

        if (BuildFilteredTabsSnapshot().Count > 0)
            return;

        ActivateAdHocScopeCore(cancelDashboardLoad: false);
    }

    private void ActivateAdHocScopeCore(bool cancelDashboardLoad)
    {
        if (cancelDashboardLoad)
            _dashboardActivation.CancelDashboardLoad();

        ActiveDashboardId = null;
        ClearGroupSelection();
        NotifyFilteredTabsChanged();
    }

    private void ClearGroupSelection()
    {
        foreach (var group in Groups)
            group.IsSelected = false;
    }

    private void SelectDashboard(LogGroupViewModel dashboard)
    {
        ClearGroupSelection();
        dashboard.IsSelected = true;
    }

    private bool DashboardContainsFile(string dashboardId, string fileId)
    {
        return Groups.Any(group =>
            group.Kind == LogGroupKind.Dashboard &&
            string.Equals(group.Id, dashboardId, StringComparison.Ordinal) &&
            group.Model.FileIds.Contains(fileId));
    }

    private LogGroupViewModel? FindContainingDashboard(string fileId)
    {
        return Groups.FirstOrDefault(group =>
            group.Kind == LogGroupKind.Dashboard &&
            group.Model.FileIds.Contains(fileId));
    }

    partial void OnIsDashboardLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLoadAffectingActionFrozen));
        OnPropertyChanged(nameof(AreLoadAffectingActionsEnabled));
        SearchPanel.RefreshLoadFreezeState();
        FilterPanel.RefreshLoadFreezeState();
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
        OnPropertyChanged(nameof(AdHocMemberFiles));
        SearchPanel.OnSelectedTabChanged(value);
        FilterPanel.OnSelectedTabChanged(value);
        _dashboardActivation.UpdateSelectedMemberFileHighlights();
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

        if (_dashboardActivation.TryGetDashboardEffectivePaths(ActiveDashboardId, out var effectivePaths))
            return BuildMembershipSnapshotFromPaths(effectivePaths, openTabs);

        return BuildMembershipSnapshotFromOpenTabs(openTabs);
    }

    private IReadOnlyList<WorkspaceScopeMemberSnapshot> BuildAdHocScopeMembershipSnapshot(
        IReadOnlyList<WorkspaceOpenTabSnapshot> openTabs)
    {
        if (_dashboardActivation.TryGetAdHocEffectivePaths(out var effectivePaths))
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

    internal Task<TabWorkspaceService.PreparedTabOpen?> PrepareDashboardFileOpenAsync(
        string filePath,
        string scopeDashboardId,
        CancellationToken ct = default)
    {
        return _tabWorkspace.PrepareFileOpenAsync(
            filePath,
            scopeDashboardId,
            _settings,
            FileEncoding.Auto,
            isPinned: false,
            ct);
    }

    internal Task FinalizeDashboardFileOpenAsync(
        TabWorkspaceService.PreparedTabOpen preparedTab,
        CancellationToken ct = default)
        => _tabWorkspace.FinalizePreparedFileOpenAsync(
            preparedTab,
            activateTab: false,
            updateVisibilityAfterAdd: true,
            ct);

    private Task<string?> ResolveTargetScopeDashboardIdForOpenAsync(string filePath)
        => ResolveTargetScopeDashboardIdAsync(filePath, ScopeResolutionMode.Open);

    private Task<string?> ResolveTargetScopeDashboardIdForNavigationAsync(string filePath)
        => ResolveTargetScopeDashboardIdAsync(filePath, ScopeResolutionMode.Navigation);

    private async Task<string?> ResolveTargetScopeDashboardIdAsync(string filePath, ScopeResolutionMode mode)
    {
        if (mode == ScopeResolutionMode.Navigation &&
            FindTabInScope(filePath, ActiveDashboardId) != null)
        {
            return ActiveDashboardId;
        }

        var fileId = await TryResolveFileIdByPathAsync(filePath);
        if (!string.IsNullOrEmpty(ActiveDashboardId))
        {
            if (_dashboardActivation.TryGetDashboardEffectivePaths(ActiveDashboardId, out var effectivePaths) &&
                effectivePaths.Contains(filePath))
            {
                return ActiveDashboardId;
            }

            if (!string.IsNullOrEmpty(fileId) &&
                DashboardContainsFile(ActiveDashboardId, fileId))
            {
                return ActiveDashboardId;
            }
        }
        else if (mode == ScopeResolutionMode.Navigation &&
                 _dashboardActivation.TryGetAdHocEffectivePaths(out var adHocEffectivePaths) &&
                 adHocEffectivePaths.Contains(filePath))
        {
            return null;
        }

        var modifierDashboardId = _dashboardActivation.FindDashboardForModifierPath(filePath);
        if (!string.IsNullOrEmpty(modifierDashboardId))
            return modifierDashboardId;

        if (_dashboardActivation.IsAdHocModifierPath(filePath))
            return null;

        if (!string.IsNullOrEmpty(fileId))
        {
            var containingGroup = FindContainingDashboard(fileId);
            if (containingGroup != null)
                return containingGroup.Id;
        }

        return null;
    }

    private enum ScopeResolutionMode
    {
        Open,
        Navigation
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
