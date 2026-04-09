namespace LogReader.App.Services;

using System.IO;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class DashboardActivationService
{
    private readonly IDashboardWorkspaceHost _host;
    private readonly ILogFileRepository _fileRepo;
    private readonly ILogGroupRepository _groupRepo;
    private readonly Func<IReadOnlyDictionary<string, string>, Task<Dictionary<string, bool>>> _buildFileExistenceMapAsync;
    private readonly DashboardModifierService _modifierService = new();
    private readonly DashboardOpenCoordinator _openCoordinator;

    public DashboardActivationService(
        IDashboardWorkspaceHost host,
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo)
        : this(host, fileRepo, groupRepo, BuildFileExistenceMapAsync)
    {
    }

    internal DashboardActivationService(
        IDashboardWorkspaceHost host,
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo,
        Func<IReadOnlyDictionary<string, string>, Task<Dictionary<string, bool>>> buildFileExistenceMapAsync)
    {
        _host = host;
        _fileRepo = fileRepo;
        _groupRepo = groupRepo;
        _buildFileExistenceMapAsync = buildFileExistenceMapAsync;
        _openCoordinator = new DashboardOpenCoordinator(host, ResolveOpenTargetsAsync);
    }

    public bool HasActiveModifiers => _modifierService.HasActiveModifiers;

    public bool HasDashboardModifier(string dashboardId)
        => _modifierService.HasDashboardModifier(dashboardId);

    public bool HasAdHocModifier()
        => _modifierService.HasAdHocModifier();

    public string? GetDashboardModifierLabel(string dashboardId)
        => _modifierService.GetDashboardModifierLabel(dashboardId);

    public string? GetAdHocModifierLabel()
        => _modifierService.GetAdHocModifierLabel();

    public bool TryGetDashboardEffectivePaths(string dashboardId, out IReadOnlySet<string> effectivePaths)
        => _modifierService.TryGetDashboardEffectivePaths(dashboardId, out effectivePaths);

    public bool TryGetAdHocEffectivePaths(out IReadOnlySet<string> effectivePaths)
        => _modifierService.TryGetAdHocEffectivePaths(out effectivePaths);

    public bool IsManagedByActiveModifier(string filePath)
        => _modifierService.IsManagedByActiveModifier(filePath);

    public string? FindDashboardForModifierPath(string filePath)
        => _modifierService.FindDashboardForModifierPath(filePath);

    public bool IsAdHocModifierPath(string filePath)
        => _modifierService.IsAdHocModifierPath(filePath);

    public IReadOnlyList<string> GetAdHocBasePathsSnapshot()
        => _modifierService.GetAdHocBasePathsSnapshot();

    public async Task SetDashboardModifierAsync(LogGroupViewModel group, int daysBack, IReadOnlyList<ReplacementPattern> patterns)
    {
        _modifierService.SetDashboardModifier(group.Id, daysBack, patterns);
        await RefreshAllMemberFilesAsync();
    }

    public async Task ClearDashboardModifierAsync(LogGroupViewModel group)
    {
        if (_modifierService.ClearDashboardModifier(group.Id))
            await RefreshAllMemberFilesAsync();
    }

    public async Task SetAdHocModifierAsync(int daysBack, IReadOnlyList<ReplacementPattern> patterns)
    {
        var basePaths = _modifierService.GetAdHocBasePathsSnapshot();
        if (basePaths.Count == 0)
            basePaths = ResolveCurrentAdHocBasePaths();

        _modifierService.SetAdHocModifier(daysBack, patterns, basePaths);
        await RefreshAllMemberFilesAsync();
    }

    public async Task ClearAdHocModifierAsync()
    {
        if (_modifierService.ClearAdHocModifier())
            await RefreshAllMemberFilesAsync();
    }

    public void CancelDashboardLoad()
    {
        _openCoordinator.CancelDashboardLoad();
    }

    public void LeaveActiveDashboardScope()
    {
        _openCoordinator.LeaveActiveDashboardScope();
    }

    public void PruneModifierState()
    {
        _modifierService.PruneModifierState(_host.Groups);
    }

    public Task OpenGroupFilesAsync(LogGroupViewModel group)
        => _openCoordinator.OpenGroupFilesAsync(group, _modifierService.GetDashboardModifierLabel(group.Id));

    public Task EnsureGroupFilesLoadedAsync(LogGroupViewModel group, IReadOnlyCollection<string> excludedPaths)
        => _openCoordinator.EnsureGroupFilesLoadedAsync(group, _modifierService.GetDashboardModifierLabel(group.Id), excludedPaths);

    public async Task<IReadOnlyList<string>> GetGroupFilePathsAsync(string groupId)
    {
        var allGroups = await _groupRepo.GetAllAsync();
        var resolvedIds = ResolveFileIdsFromModels(allGroups, groupId);
        var entriesById = await _fileRepo.GetByIdsAsync(resolvedIds);
        return resolvedIds
            .Where(entriesById.ContainsKey)
            .Select(fileId => entriesById[fileId].FilePath)
            .ToList()
            .AsReadOnly();
    }

    public async Task RefreshAllMemberFilesAsync()
    {
        var trackedFileIds = ResolveTrackedFileIdSnapshot();
        var entriesById = await _fileRepo.GetByIdsAsync(trackedFileIds);
        var fileIdToPath = entriesById.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.FilePath,
            StringComparer.Ordinal);
        var fileExistenceById = await _buildFileExistenceMapAsync(fileIdToPath);
        var selectedTab = _host.SelectedTab;
        var openTabsByPath = _host.Tabs
            .GroupBy(tab => tab.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var modifierSnapshot = _modifierService.ResolveRefreshSnapshot(_host.Groups, fileIdToPath);
        var modifiedPathExistence = await BuildPathExistenceMapAsync(modifierSnapshot.ModifiedPaths);

        foreach (var group in _host.Groups)
        {
            if (group.Kind == LogGroupKind.Dashboard &&
                modifierSnapshot.DashboardMembers.TryGetValue(group.Id, out var resolvedMembers))
            {
                group.ReplaceMemberFiles(DashboardModifierService.BuildModifierMemberViewModels(
                    resolvedMembers,
                    openTabsByPath,
                    modifiedPathExistence,
                    GetSelectedFilePathForGroup(group, selectedTab),
                    _host.ShowFullPathsInDashboard));
                continue;
            }

            group.RefreshMemberFiles(
                _host.Tabs,
                fileIdToPath,
                fileExistenceById,
                GetSelectedFileIdForGroup(group, selectedTab),
                _host.ShowFullPathsInDashboard);
        }

        _modifierService.SyncModifierLabels(_host.Groups);
    }

    public async Task RefreshMemberFilesForFileIdsAsync(IReadOnlyDictionary<string, string> changedFilePathsById)
    {
        if (changedFilePathsById.Count == 0)
            return;

        if (HasActiveModifiers)
        {
            await RefreshAllMemberFilesAsync();
            return;
        }

        var changedFileIds = changedFilePathsById.Keys.ToHashSet(StringComparer.Ordinal);
        var fileExistenceById = await _buildFileExistenceMapAsync(changedFilePathsById);
        var openTabsByFileId = _host.Tabs
            .Where(tab => changedFileIds.Contains(tab.FileId))
            .ToDictionary(tab => tab.FileId, StringComparer.Ordinal);
        var selectedTab = _host.SelectedTab;
        var affectedGroups = _host.Groups
            .Where(group => group.Kind == LogGroupKind.Dashboard &&
                            group.Model.FileIds.Any(changedFileIds.Contains))
            .ToList();

        foreach (var group in affectedGroups)
        {
            foreach (var fileId in group.Model.FileIds.Where(changedFileIds.Contains))
            {
                openTabsByFileId.TryGetValue(fileId, out var openTab);
                changedFilePathsById.TryGetValue(fileId, out var storedFilePath);
                var fileExists = fileExistenceById.TryGetValue(fileId, out var exists) && exists;
                group.RefreshMemberFile(
                    fileId,
                    openTab,
                    storedFilePath,
                    fileExists,
                    GetSelectedFileIdForGroup(group, selectedTab),
                    _host.ShowFullPathsInDashboard);
            }
        }
    }

    public void UpdateSelectedMemberFileHighlights()
    {
        foreach (var group in _host.Groups)
        {
            if (group.Kind == LogGroupKind.Dashboard && HasDashboardModifier(group.Id))
                group.SetSelectedMemberFilePath(GetSelectedFilePathForGroup(group, _host.SelectedTab));
            else
                group.SetSelectedMemberFile(GetSelectedFileIdForGroup(group, _host.SelectedTab));
        }
    }

    private async Task<IReadOnlyList<string>> ResolveOpenTargetsAsync(LogGroupViewModel group)
    {
        if (_modifierService.HasDashboardModifier(group.Id))
            return _modifierService.GetDashboardOpenTargets(group.Id);

        var fileIds = ResolveFileIdsInDisplayOrder(group);
        var entriesById = await _fileRepo.GetByIdsAsync(fileIds);
        return fileIds
            .Where(entriesById.ContainsKey)
            .Select(fileId => entriesById[fileId].FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
    }

    private IReadOnlyList<string> ResolveCurrentAdHocBasePaths()
        => _host.Tabs
            .Where(tab => tab.IsAdHocScope)
            .Select(tab => tab.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task<Dictionary<string, bool>> BuildPathExistenceMapAsync(IEnumerable<string> filePaths)
    {
        var distinctPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path => path, StringComparer.OrdinalIgnoreCase);
        if (distinctPaths.Count == 0)
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        return await _buildFileExistenceMapAsync(distinctPaths);
    }

    private HashSet<string> ResolveTrackedFileIdSnapshot()
        => _host.Groups
            .SelectMany(group => group.Model.FileIds)
            .Where(fileId => !string.IsNullOrWhiteSpace(fileId))
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<string> ResolveFileIdsInDisplayOrder(LogGroupViewModel group)
    {
        var orderedFileIds = new List<string>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        var seenFileIds = new HashSet<string>(StringComparer.Ordinal);
        CollectFileIdsInDisplayOrder(group, seenGroups, seenFileIds, orderedFileIds);
        return orderedFileIds;
    }

    private static void CollectFileIdsInDisplayOrder(
        LogGroupViewModel group,
        HashSet<string> seenGroups,
        HashSet<string> seenFileIds,
        List<string> orderedFileIds)
    {
        if (!seenGroups.Add(group.Id))
            return;

        foreach (var fileId in group.Model.FileIds)
        {
            if (seenFileIds.Add(fileId))
                orderedFileIds.Add(fileId);
        }

        foreach (var child in group.Children.OrderBy(c => c.Model.SortOrder))
            CollectFileIdsInDisplayOrder(child, seenGroups, seenFileIds, orderedFileIds);
    }

    private static HashSet<string> ResolveFileIdsFromModels(
        List<LogGroup> allGroups,
        string groupId,
        HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();
        if (!visited.Add(groupId))
            return new HashSet<string>();

        var result = new HashSet<string>();
        var group = allGroups.FirstOrDefault(g => g.Id == groupId);
        if (group == null)
            return result;

        result.UnionWith(group.FileIds);
        foreach (var child in allGroups.Where(g => g.ParentGroupId == groupId))
            result.UnionWith(ResolveFileIdsFromModels(allGroups, child.Id, visited));
        return result;
    }

    private static Task<Dictionary<string, bool>> BuildFileExistenceMapAsync(IReadOnlyDictionary<string, string> fileIdToPath)
        => Task.Run(() => fileIdToPath.ToDictionary(
            kvp => kvp.Key,
            kvp => File.Exists(kvp.Value),
            StringComparer.Ordinal));

    private static string? GetSelectedFileIdForGroup(LogGroupViewModel group, LogTabViewModel? selectedTab)
    {
        return selectedTab != null && string.Equals(selectedTab.ScopeDashboardId, group.Id, StringComparison.Ordinal)
            ? selectedTab.FileId
            : null;
    }

    private static string? GetSelectedFilePathForGroup(LogGroupViewModel group, LogTabViewModel? selectedTab)
    {
        return selectedTab != null && string.Equals(selectedTab.ScopeDashboardId, group.Id, StringComparison.Ordinal)
            ? selectedTab.FilePath
            : null;
    }
}
