namespace LogReader.App.ViewModels;

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

        return GetVisibleTabSearchResultFileOrderSnapshot(filteredTabs);
    }

    public Task<IReadOnlyList<string>> GetGroupFilePathsAsync(string groupId)
        => GetRecoverableGroupFilePathsAsync(groupId);

    public async Task NavigateToLineAsync(string filePath, long lineNumber, bool disableAutoScroll = false)
    {
        var targetScopeDashboardId = await ResolveTargetScopeDashboardIdForNavigationAsync(filePath);
        var tab = FindTabInScope(filePath, targetScopeDashboardId);
        if (tab == null)
        {
            await OpenFilePathInScopeAsync(filePath, targetScopeDashboardId);
            tab = FindTabInScope(filePath, targetScopeDashboardId);
        }

        if (tab == null)
            return;

        if (!GetFilteredTabsSnapshot().Contains(tab))
            tab.SetNavigateTargetLine((int)lineNumber);

        EnsureTabVisibleInCurrentScope(tab);

        if (disableAutoScroll)
            GlobalAutoScrollEnabled = false;

        SelectedTab = tab;
        await tab.NavigateToLineAsync((int)lineNumber);
    }

    private string? GetDefaultOpenDirectory()
        => !string.IsNullOrWhiteSpace(_settings.DefaultOpenDirectory) &&
           Directory.Exists(_settings.DefaultOpenDirectory)
            ? _settings.DefaultOpenDirectory
            : null;

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
