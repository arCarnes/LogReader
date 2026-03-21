namespace LogReader.App.Services;

using LogReader.App.ViewModels;
using LogReader.Core.Models;

internal sealed class DashboardScopeService
{
    public IReadOnlyList<LogTabViewModel> GetFilteredTabs(
        IReadOnlyCollection<LogTabViewModel> tabs,
        IReadOnlyCollection<LogGroupViewModel> groups,
        string? activeDashboardId,
        Func<LogGroupViewModel, HashSet<string>> resolveFileIds,
        Func<IEnumerable<LogTabViewModel>, IReadOnlyList<LogTabViewModel>> orderTabsForDisplay)
    {
        var scopedTabs = GetTabsForCurrentScope(tabs, groups, activeDashboardId, resolveFileIds);
        return orderTabsForDisplay(scopedTabs);
    }

    public IReadOnlyList<LogTabViewModel> GetAdHocTabs(
        IReadOnlyCollection<LogTabViewModel> tabs,
        IReadOnlyCollection<LogGroupViewModel> groups)
    {
        var assignedFileIds = groups
            .Where(group => group.Kind == LogGroupKind.Dashboard)
            .SelectMany(group => group.Model.FileIds)
            .ToHashSet(StringComparer.Ordinal);

        return tabs
            .Where(tab => !assignedFileIds.Contains(tab.FileId))
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

    private IReadOnlyList<LogTabViewModel> GetTabsForCurrentScope(
        IReadOnlyCollection<LogTabViewModel> tabs,
        IReadOnlyCollection<LogGroupViewModel> groups,
        string? activeDashboardId,
        Func<LogGroupViewModel, HashSet<string>> resolveFileIds)
    {
        if (string.IsNullOrEmpty(activeDashboardId))
            return GetAdHocTabs(tabs, groups);

        var active = groups.FirstOrDefault(group => group.Id == activeDashboardId);
        if (active == null)
            return GetAdHocTabs(tabs, groups);

        var fileIds = resolveFileIds(active);
        return tabs
            .Where(tab => fileIds.Contains(tab.FileId))
            .ToList();
    }
}
