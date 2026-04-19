namespace LogReader.App.Services;

using System.IO;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

internal readonly record struct WorkspaceScopeKey(string Value)
{
    public static WorkspaceScopeKey FromDashboardId(string? dashboardId)
        => string.IsNullOrEmpty(dashboardId)
            ? new("adhoc")
            : new($"dashboard:{dashboardId}");
}

internal readonly record struct WorkspaceOpenTabSnapshot(LogTabViewModel Tab)
{
    public string TabInstanceId => Tab.TabInstanceId;

    public string FileId => Tab.FileId;

    public string FilePath => Tab.FilePath;
}

internal readonly record struct WorkspaceScopeMemberSnapshot(string FileId, string FilePath);

internal sealed record WorkspaceScopeSnapshot(
    WorkspaceScopeKey ScopeKey,
    IReadOnlyList<WorkspaceOpenTabSnapshot> OpenTabs,
    IReadOnlyList<WorkspaceScopeMemberSnapshot> EffectiveMembership);

internal static class WorkspaceScopeOrdering
{
    public static IReadOnlyList<string> GetCanonicalOrderedVisibleOpenTabPaths(WorkspaceScopeSnapshot scopeSnapshot)
    {
        var openTabs = GetDistinctOrderedOpenTabs(scopeSnapshot.OpenTabs);
        if (openTabs.Count == 0)
            return Array.Empty<string>();

        var normalizedPathsByVisiblePath = openTabs.ToDictionary(
            openTab => openTab.FilePath,
            openTab => Path.GetFullPath(openTab.FilePath),
            StringComparer.OrdinalIgnoreCase);
        var orderedPaths = new List<string>(openTabs.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in scopeSnapshot.EffectiveMembership)
        {
            if (string.IsNullOrWhiteSpace(member.FilePath) ||
                !normalizedPathsByVisiblePath.TryGetValue(member.FilePath, out var normalizedPath) ||
                !seenPaths.Add(normalizedPath))
            {
                continue;
            }

            orderedPaths.Add(normalizedPath);
        }

        foreach (var openTab in openTabs)
        {
            var normalizedPath = normalizedPathsByVisiblePath[openTab.FilePath];
            if (seenPaths.Add(normalizedPath))
                orderedPaths.Add(normalizedPath);
        }

        return orderedPaths;
    }

    public static IReadOnlyList<WorkspaceOpenTabSnapshot> GetDistinctOrderedOpenTabs(
        IReadOnlyList<WorkspaceOpenTabSnapshot> openTabs)
    {
        var orderedTabs = new List<WorkspaceOpenTabSnapshot>(openTabs.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var openTab in openTabs)
        {
            if (string.IsNullOrWhiteSpace(openTab.FilePath) || !seenPaths.Add(openTab.FilePath))
                continue;

            orderedTabs.Add(openTab);
        }

        return orderedTabs;
    }
}

internal interface ILogWorkspaceContext
{
    string? ActiveScopeDashboardId { get; }

    bool IsDashboardLoading { get; }

    LogTabViewModel? SelectedTab { get; }

    IReadOnlyList<LogTabViewModel> GetAllTabs();

    IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot();

    IReadOnlyList<string> GetSearchResultFileOrderSnapshot();

    IReadOnlyList<string> GetAllOpenTabsExecutionFileOrderSnapshot(string? scopeDashboardId);

    WorkspaceScopeSnapshot GetActiveScopeSnapshot();

    Task<FileEncoding> ResolveFilterFileEncodingAsync(string filePath, string? scopeDashboardId, CancellationToken ct = default);

    LogFilterSession.FilterSnapshot? GetApplicableCurrentTabFilterSnapshot(SearchDataMode sourceMode);

    LogFilterSession.FilterSnapshot? GetApplicableAllOpenTabsFilterSnapshot(string filePath, SearchDataMode sourceMode);

    IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableAllOpenTabsFilterSnapshots(SearchDataMode sourceMode);

    void UpdateRecentTabFilterSnapshot(string filePath, string? scopeDashboardId, LogFilterSession.FilterSnapshot? snapshot);

    Task RunViewActionAsync(Func<Task> operation, string failureCaption = "LogReader Error");

    Task NavigateToLineAsync(
        string filePath,
        long lineNumber,
        bool disableAutoScroll = false,
        bool suppressDuringDashboardLoad = false);
}
