namespace LogReader.App.ViewModels;

using System.ComponentModel;
using System.IO;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Models;

public partial class MainViewModel
{
    public async Task OpenSettingsAsync()
    {
        var settingsVm = _settingsViewModelFactory(_settingsRepo);
        var loadSucceeded = await ExecuteRecoverableCommandAsync(() => settingsVm.LoadAsync());
        if (!loadSucceeded)
            return;

        if (_settingsDialogService.ShowDialog(settingsVm))
        {
            await ExecuteRecoverableCommandAsync(async () =>
            {
                await settingsVm.SaveAsync();
                await ReloadSettingsStateAsync();
            });
        }
    }

    public static string FormatModifierPatternLabel(int daysBack, ReplacementPattern pattern)
    {
        var replacePreview = ResolveModifierReplacePreview(daysBack, pattern);
        if (string.IsNullOrWhiteSpace(pattern.Name))
            return $"{pattern.FindPattern} -> {replacePreview}";

        return $"{pattern.Name} ({pattern.FindPattern} -> {replacePreview})";
    }

    public static string FormatModifierActionLabel(int daysBack, ReplacementPattern pattern)
        => $"T-{daysBack}";

    private static string ResolveModifierReplacePreview(int daysBack, ReplacementPattern pattern)
    {
        var targetDate = DateTime.Today.AddDays(-daysBack);
        if (ReplacementTokenParser.TryExpand(pattern.ReplacePattern, targetDate, out var expanded, out _))
            return expanded;

        return ReplacementTokenParser.DescribeTokens(pattern.ReplacePattern);
    }

    public IReadOnlyList<LogTabViewModel> GetAllTabs() => Tabs;

    internal IReadOnlyDictionary<string, long> TabOpenOrder => _tabWorkspace.OpenOrderSnapshot;

    internal IReadOnlyDictionary<string, long> TabPinOrder => _tabWorkspace.PinOrderSnapshot;

    internal TimeSpan FileSessionWarmRetention => _tabWorkspace.FileSessionWarmRetention;

    internal TimeSpan RecentTabStateRetention
    {
        get => _tabWorkspace.RecentTabStateRetention;
        set => _tabWorkspace.RecentTabStateRetention = value;
    }

    public IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot() => _filteredTabsSnapshot;

    public IReadOnlyList<string> GetSearchResultFileOrderSnapshot()
    {
        var filteredTabs = GetFilteredTabsSnapshot();
        if (filteredTabs.Count == 0)
            return Array.Empty<string>();

        if (string.IsNullOrEmpty(ActiveDashboardId))
            return GetVisibleTabSearchResultFileOrderSnapshot(filteredTabs);

        var activeDashboard = GetActiveDashboard();
        if (activeDashboard == null)
            return GetVisibleTabSearchResultFileOrderSnapshot(filteredTabs);

        return GetDashboardSearchResultFileOrderSnapshot(activeDashboard, filteredTabs);
    }

    public IReadOnlyList<string> GetAllOpenTabsExecutionFileOrderSnapshot(string? scopeDashboardId)
    {
        if (!string.Equals(scopeDashboardId, ActiveDashboardId, StringComparison.Ordinal))
            return Array.Empty<string>();

        return GetSearchResultFileOrderSnapshot()
            .Select(Path.GetFullPath)
            .ToList();
    }

    public Task<IReadOnlyList<string>> GetGroupFilePathsAsync(string groupId)
        => GetRecoverableGroupFilePathsAsync(groupId);

    public async Task NavigateToLineAsync(
        string filePath,
        long lineNumber,
        bool disableAutoScroll = false,
        bool suppressDuringDashboardLoad = false)
    {
        if (suppressDuringDashboardLoad && IsDashboardLoading)
            return;

        CancellationTokenSource? dashboardLoadSuppressionCts = null;
        PropertyChangedEventHandler? dashboardLoadingChanged = null;

        try
        {
            if (suppressDuringDashboardLoad)
            {
                dashboardLoadSuppressionCts = new CancellationTokenSource();
                dashboardLoadingChanged = (_, e) =>
                {
                    if (e.PropertyName == nameof(IsDashboardLoading) && IsDashboardLoading)
                        dashboardLoadSuppressionCts.Cancel();
                };
                PropertyChanged += dashboardLoadingChanged;
                ThrowIfDashboardLoadSuppressed();
            }

            var targetScopeDashboardId = await ResolveTargetScopeDashboardIdForNavigationAsync(filePath);
            ThrowIfDashboardLoadSuppressed();

            var tab = FindTabInScope(filePath, targetScopeDashboardId);
            if (tab == null)
            {
                await OpenFilePathInScopeAsync(
                    filePath,
                    targetScopeDashboardId,
                    ct: dashboardLoadSuppressionCts?.Token ?? default);
                ThrowIfDashboardLoadSuppressed();
                tab = FindTabInScope(filePath, targetScopeDashboardId);
            }

            if (tab == null)
                return;

            ThrowIfDashboardLoadSuppressed();

            if (!GetFilteredTabsSnapshot().Contains(tab))
                tab.SetNavigateTargetLine((int)lineNumber);

            EnsureTabVisibleInCurrentScope(tab);

            if (disableAutoScroll)
                GlobalAutoScrollEnabled = false;

            SelectedTab = tab;
            await tab.NavigateToLineAsync((int)lineNumber);
        }
        catch (OperationCanceledException) when (suppressDuringDashboardLoad &&
                                                dashboardLoadSuppressionCts?.IsCancellationRequested == true)
        {
            return;
        }
        finally
        {
            if (dashboardLoadingChanged != null)
                PropertyChanged -= dashboardLoadingChanged;

            dashboardLoadSuppressionCts?.Dispose();
        }

        void ThrowIfDashboardLoadSuppressed()
        {
            if (!suppressDuringDashboardLoad)
                return;

            if (IsDashboardLoading)
                dashboardLoadSuppressionCts!.Cancel();

            dashboardLoadSuppressionCts!.Token.ThrowIfCancellationRequested();
        }
    }

    private string? GetDefaultOpenDirectory()
        => !string.IsNullOrWhiteSpace(_settings.DefaultOpenDirectory) &&
           Directory.Exists(_settings.DefaultOpenDirectory)
            ? _settings.DefaultOpenDirectory
            : null;

    private static IReadOnlyList<string> GetDashboardSearchResultFileOrderSnapshot(
        LogGroupViewModel activeDashboard,
        IReadOnlyList<LogTabViewModel> filteredTabs)
    {
        var memberFiles = activeDashboard.MemberFiles.ToList();
        if (memberFiles.Count == 0)
            return GetDashboardSearchResultFileOrderFromFileIds(activeDashboard.Model.FileIds.ToList(), filteredTabs);

        var visiblePaths = filteredTabs
            .Select(tab => tab.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orderedPaths = new List<string>(filteredTabs.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in memberFiles)
        {
            if (!visiblePaths.Contains(member.FilePath) || !seenPaths.Add(member.FilePath))
                continue;

            orderedPaths.Add(member.FilePath);
        }

        foreach (var tab in filteredTabs)
        {
            if (seenPaths.Add(tab.FilePath))
                orderedPaths.Add(tab.FilePath);
        }

        return orderedPaths;
    }

    private static IReadOnlyList<string> GetDashboardSearchResultFileOrderFromFileIds(
        IReadOnlyList<string> dashboardFileIds,
        IReadOnlyList<LogTabViewModel> filteredTabs)
    {
        var visibleTabsByFileId = filteredTabs
            .GroupBy(tab => tab.FileId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var orderedPaths = new List<string>(filteredTabs.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileId in dashboardFileIds)
        {
            if (!visibleTabsByFileId.TryGetValue(fileId, out var tab) || !seenPaths.Add(tab.FilePath))
                continue;

            orderedPaths.Add(tab.FilePath);
        }

        foreach (var tab in filteredTabs)
        {
            if (seenPaths.Add(tab.FilePath))
                orderedPaths.Add(tab.FilePath);
        }

        return orderedPaths;
    }

    private static IReadOnlyList<string> GetVisibleTabSearchResultFileOrderSnapshot(
        IReadOnlyList<LogTabViewModel> filteredTabs)
    {
        var orderedPaths = new List<string>(filteredTabs.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tab in filteredTabs)
        {
            if (seenPaths.Add(tab.FilePath))
                orderedPaths.Add(tab.FilePath);
        }

        return orderedPaths;
    }
}
