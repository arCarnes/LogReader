namespace LogReader.App.Services;

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

internal interface ILogWorkspaceContext
{
    string? ActiveScopeDashboardId { get; }

    LogTabViewModel? SelectedTab { get; }

    IReadOnlyList<LogTabViewModel> GetAllTabs();

    IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot();

    IReadOnlyList<string> GetSearchResultFileOrderSnapshot();

    WorkspaceScopeSnapshot GetActiveScopeSnapshot();

    Task<FileEncoding> ResolveFilterFileEncodingAsync(string filePath, string? scopeDashboardId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, LogTabViewModel>> EnsureBackgroundTabsOpenAsync(
        IReadOnlyList<string> filePaths,
        string? scopeDashboardId,
        CancellationToken ct = default);

    LogFilterSession.FilterSnapshot? GetApplicableCurrentTabFilterSnapshot(SearchDataMode sourceMode);

    LogFilterSession.FilterSnapshot? GetApplicableCurrentScopeFilterSnapshot(string filePath, SearchDataMode sourceMode);

    IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableCurrentScopeFilterSnapshots(SearchDataMode sourceMode);

    void UpdateRecentTabFilterSnapshot(string filePath, string? scopeDashboardId, LogFilterSession.FilterSnapshot? snapshot);

    Task NavigateToLineAsync(string filePath, long lineNumber, bool disableAutoScroll = false);
}
