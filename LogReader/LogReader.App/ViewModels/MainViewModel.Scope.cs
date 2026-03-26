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

        var modifierDashboardId = _dashboardWorkspace.FindDashboardForModifierPath(tab.FilePath);
        if (!string.IsNullOrEmpty(modifierDashboardId))
        {
            var modifierDashboard = Groups.FirstOrDefault(group => string.Equals(group.Id, modifierDashboardId, StringComparison.Ordinal));
            if (modifierDashboard != null)
            {
                _dashboardWorkspace.CancelDashboardLoad();
                _dashboardScope.SelectDashboard(Groups, modifierDashboard);
                ActiveDashboardId = modifierDashboard.Id;
                NotifyFilteredTabsChanged();
                return;
            }
        }

        if (_dashboardWorkspace.IsAdHocModifierPath(tab.FilePath))
        {
            ActivateAdHocScope();
            return;
        }

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
                Tabs.Where(tab => adHocEffectivePaths.Contains(tab.FilePath)));
        }

        return _tabWorkspace.OrderTabsForDisplay(GetNormalAdHocTabs());
    }

    private IReadOnlyList<LogTabViewModel> GetNormalAdHocTabs()
        => _dashboardScope.GetAdHocTabs(Tabs, Groups)
            .Where(tab => !_dashboardWorkspace.IsManagedByActiveModifier(tab.FilePath))
            .ToList();

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
                await OpenFilePathAsync(
                    filePath,
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
        OnPropertyChanged(nameof(CanAddFilesToActiveDashboard));
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
        var syncVersion = Interlocked.Increment(ref _autoScrollSyncVersion);
        foreach (var tab in Tabs)
            tab.AutoScrollEnabled = value;

        if (value)
            _ = SyncTabsToAutoScrollBottomAsync(syncVersion);
    }

    partial void OnDashboardTreeFilterChanged(string value)
        => _dashboardWorkspace.ApplyDashboardTreeFilter();

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
