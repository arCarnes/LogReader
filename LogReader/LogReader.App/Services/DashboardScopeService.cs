namespace LogReader.App.Services;

using LogReader.App.ViewModels;
using LogReader.Core.Models;

internal sealed class DashboardScopeService
{
    public IReadOnlyList<LogTabViewModel> GetFilteredTabs(
        IReadOnlyCollection<LogTabViewModel> tabs,
        string? activeDashboardId,
        Func<IEnumerable<LogTabViewModel>, IReadOnlyList<LogTabViewModel>> orderTabsForDisplay)
    {
        var scopedTabs = GetTabsForCurrentScope(tabs, activeDashboardId);
        return orderTabsForDisplay(scopedTabs);
    }

    public IReadOnlyList<LogTabViewModel> GetAdHocTabs(IReadOnlyCollection<LogTabViewModel> tabs)
    {
        return tabs
            .Where(tab => tab.IsAdHocScope)
            .ToList();
    }

    public string? ToggleGroupSelection(
        IReadOnlyCollection<LogGroupViewModel> groups,
        string? activeDashboardId,
        LogGroupViewModel group)
    {
        var wasActive = string.Equals(activeDashboardId, group.Id, StringComparison.Ordinal);
        ClearSelection(groups);

        if (group.Kind != LogGroupKind.Dashboard || wasActive)
            return null;

        group.IsSelected = true;
        return group.Id;
    }

    public void SelectDashboard(IReadOnlyCollection<LogGroupViewModel> groups, LogGroupViewModel dashboard)
    {
        ClearSelection(groups);
        dashboard.IsSelected = true;
    }

    public void ClearSelection(IEnumerable<LogGroupViewModel> groups)
    {
        foreach (var group in groups)
            group.IsSelected = false;
    }

    public LogGroupViewModel? FindContainingDashboard(
        IReadOnlyCollection<LogGroupViewModel> groups,
        string fileId)
    {
        return groups.FirstOrDefault(group =>
            group.Kind == LogGroupKind.Dashboard &&
            group.Model.FileIds.Contains(fileId));
    }

    public bool DashboardContainsFile(
        IReadOnlyCollection<LogGroupViewModel> groups,
        string dashboardId,
        string fileId)
    {
        return groups.Any(group =>
            group.Kind == LogGroupKind.Dashboard &&
            string.Equals(group.Id, dashboardId, StringComparison.Ordinal) &&
            group.Model.FileIds.Contains(fileId));
    }

    private IReadOnlyList<LogTabViewModel> GetTabsForCurrentScope(
        IReadOnlyCollection<LogTabViewModel> tabs,
        string? activeDashboardId)
    {
        if (string.IsNullOrEmpty(activeDashboardId))
            return GetAdHocTabs(tabs);

        return tabs
            .Where(tab => string.Equals(tab.ScopeDashboardId, activeDashboardId, StringComparison.Ordinal))
            .ToList();
    }
}
