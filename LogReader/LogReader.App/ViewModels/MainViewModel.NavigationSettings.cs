namespace LogReader.App.ViewModels;

using System.IO;
using System.Windows;
using System.Windows.Media;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Models;

public partial class MainViewModel
{
    public async Task OpenSettingsAsync(Window? owner)
    {
        var settingsVm = _settingsViewModelFactory(_settingsRepo);
        var loadSucceeded = await ExecuteRecoverableCommandAsync(() => settingsVm.LoadAsync());
        if (!loadSucceeded)
            return;

        if (_settingsDialogService.ShowDialog(settingsVm, owner))
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

    private static void ApplyLogFontResource(AppSettings settings)
    {
        if (Application.Current == null)
            return;

        var fontName = string.IsNullOrWhiteSpace(settings.LogFontFamily)
            ? "Consolas"
            : settings.LogFontFamily;
        Application.Current.Resources["LogFontFamilyResource"] = new FontFamily(fontName);
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

    public IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot() => FilteredTabs.ToList();

    public IReadOnlyList<string> GetSearchResultFileOrderSnapshot()
    {
        var filteredTabs = GetFilteredTabsSnapshot();
        if (filteredTabs.Count == 0)
            return Array.Empty<string>();

        if (string.IsNullOrEmpty(ActiveDashboardId))
        {
            return filteredTabs
                .Select(tab => tab.FilePath)
                .ToList();
        }

        var activeDashboard = GetActiveDashboard();
        if (activeDashboard == null)
        {
            return filteredTabs
                .Select(tab => tab.FilePath)
                .ToList();
        }

        return _dashboardWorkspace.HasDashboardModifier(activeDashboard.Id)
            ? GetModifiedDashboardSearchResultFileOrderSnapshot(activeDashboard, filteredTabs)
            : GetDashboardSearchResultFileOrderSnapshot(activeDashboard, filteredTabs);
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

        if (!FilteredTabs.Contains(tab))
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

    public async Task<GoToCommandResult> NavigateToLineAsync(string lineNumberText)
    {
        if (SelectedTab == null)
            return GoToCommandResult.Failure("Select a file tab before using Go to line.");

        if (!long.TryParse(lineNumberText?.Trim(), out var lineNumber) || lineNumber <= 0)
            return GoToCommandResult.Failure("Invalid line number. Enter a whole number greater than 0.");

        var tab = SelectedTab;
        if (tab.TotalLines > 0 && lineNumber > tab.TotalLines)
            lineNumber = tab.TotalLines;

        try
        {
            await NavigateToLineAsync(tab.FilePath, lineNumber, disableAutoScroll: true);
            var status = $"Navigated to line {lineNumber:N0}.";
            tab.StatusText = status;
            return GoToCommandResult.Success();
        }
        catch (Exception ex)
        {
            var message = $"Go to line error: {ex.Message}";
            tab.StatusText = message;
            return GoToCommandResult.Failure(message);
        }
    }

    private static IReadOnlyList<string> GetDashboardSearchResultFileOrderSnapshot(
        LogGroupViewModel activeDashboard,
        IReadOnlyList<LogTabViewModel> filteredTabs)
    {
        var orderedPaths = new List<string>(filteredTabs.Count);
        var visibleTabsByFileId = filteredTabs.ToDictionary(tab => tab.FileId, StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileId in activeDashboard.Model.FileIds)
        {
            if (visibleTabsByFileId.TryGetValue(fileId, out var tab) &&
                seenPaths.Add(tab.FilePath))
            {
                orderedPaths.Add(tab.FilePath);
            }
        }

        foreach (var tab in filteredTabs)
        {
            if (seenPaths.Add(tab.FilePath))
                orderedPaths.Add(tab.FilePath);
        }

        return orderedPaths;
    }

    private static IReadOnlyList<string> GetModifiedDashboardSearchResultFileOrderSnapshot(
        LogGroupViewModel activeDashboard,
        IReadOnlyList<LogTabViewModel> filteredTabs)
    {
        var orderedPaths = new List<string>(filteredTabs.Count);
        var visiblePaths = filteredTabs
            .Select(tab => tab.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in activeDashboard.MemberFiles)
        {
            if (visiblePaths.Contains(member.FilePath) &&
                seenPaths.Add(member.FilePath))
            {
                orderedPaths.Add(member.FilePath);
            }
        }

        foreach (var tab in filteredTabs)
        {
            if (seenPaths.Add(tab.FilePath))
                orderedPaths.Add(tab.FilePath);
        }

        return orderedPaths;
    }

    public async Task<GoToCommandResult> NavigateToTimestampAsync(string timestampText)
    {
        if (SelectedTab == null)
            return GoToCommandResult.Failure("Select a file tab before using Go to timestamp.");

        if (!TimestampParser.TryParseInput(timestampText, out var targetTimestamp))
            return GoToCommandResult.Failure("Invalid timestamp. Use ISO-8601, yyyy-MM-dd HH:mm:ss, or HH:mm:ss.fff.");

        var tab = SelectedTab;
        try
        {
            var result = await _timestampNavigationService.FindNearestLineAsync(
                tab.FilePath,
                targetTimestamp,
                tab.EffectiveEncoding);

            if (!result.HasMatch)
            {
                tab.StatusText = result.StatusMessage;
                return GoToCommandResult.Failure(result.StatusMessage);
            }

            await NavigateToLineAsync(tab.FilePath, result.LineNumber, disableAutoScroll: true);
            tab.StatusText = result.StatusMessage;
            return GoToCommandResult.Success();
        }
        catch (Exception ex)
        {
            var message = $"Go to timestamp error: {ex.Message}";
            tab.StatusText = message;
            return GoToCommandResult.Failure(message);
        }
    }

    private void ShowMessage(
        Window? owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        if (owner == null)
        {
            _messageBoxService.Show(message, caption, buttons, image);
            return;
        }

        _messageBoxService.Show(owner, message, caption, buttons, image);
    }
}
