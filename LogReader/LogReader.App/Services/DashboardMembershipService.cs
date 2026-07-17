namespace LogReader.App.Services;

using System.IO;
using LogReader.App.Models;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class DashboardMembershipService
{
    private readonly IDashboardWorkspaceHost _host;
    private readonly LogFileCatalogService _fileCatalogService;
    private readonly ILogGroupRepository _groupRepo;
    private readonly DashboardMutationCoordinator _mutationCoordinator;

    public DashboardMembershipService(
        IDashboardWorkspaceHost host,
        LogFileCatalogService fileCatalogService,
        ILogGroupRepository groupRepo,
        DashboardMutationCoordinator mutationCoordinator)
    {
        _host = host;
        _fileCatalogService = fileCatalogService;
        _groupRepo = groupRepo;
        _mutationCoordinator = mutationCoordinator;
    }

    public async Task<bool> AddFilesToDashboardAsync(LogGroupViewModel groupVm, IReadOnlyList<string> filePaths)
    {
        if (!groupVm.CanManageFiles)
            return false;

        var parsedPaths = DistinctLiteralFilePaths(filePaths)
            .OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance)
            .ToList();
        if (parsedPaths.Count == 0)
            return false;

        var entriesByPath = await _fileCatalogService.EnsureRegisteredAsync(parsedPaths);
        return await CommitFileIdsAsync(groupVm.Id, async groupModel =>
        {
            var existingPaths = await GetExistingDashboardPathsAsync(groupModel.FileIds);
            var added = false;
            foreach (var path in parsedPaths)
            {
                if (existingPaths.Contains(path))
                    continue;

                if (!entriesByPath.TryGetValue(path, out var entry))
                    continue;

                if (!groupModel.FileIds.Contains(entry.Id))
                {
                    groupModel.FileIds.Add(entry.Id);
                    existingPaths.Add(path);
                    added = true;
                }
            }

            if (!added)
                return false;

            await ResortDashboardFileIdsAsync(groupModel, entriesByPath);
            return true;
        });
    }

    public async Task<bool> RemoveFilesFromDashboardAsync(LogGroupViewModel groupVm, IReadOnlyList<string> fileIds)
    {
        if (!groupVm.CanManageFiles || fileIds.Count == 0)
            return false;

        var distinctFileIds = fileIds
            .Where(fileId => !string.IsNullOrWhiteSpace(fileId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return await CommitFileIdsAsync(groupVm.Id, groupModel =>
        {
            var removed = false;
            foreach (var fileId in distinctFileIds)
                removed = groupModel.FileIds.Remove(fileId) || removed;

            return Task.FromResult(removed);
        });
    }

    public async Task<bool> CopyFileToDashboardAsync(LogGroupViewModel targetGroupVm, string fileId)
        => await CopyFilesToDashboardAsync(targetGroupVm, new[] { fileId });

    public async Task<bool> CopyFilePathToDashboardAsync(LogGroupViewModel targetGroupVm, string filePath)
    {
        if (!targetGroupVm.CanManageFiles || string.IsNullOrWhiteSpace(filePath))
            return false;

        var entriesByPath = await _fileCatalogService.EnsureRegisteredAsync(new[] { filePath });
        if (!entriesByPath.TryGetValue(filePath, out var entry))
            return false;

        return await CopyFileToDashboardAsync(targetGroupVm, entry.Id);
    }

    public async Task<bool> CopyFilesToDashboardAsync(LogGroupViewModel targetGroupVm, IReadOnlyList<string> fileIds)
    {
        if (!targetGroupVm.CanManageFiles || fileIds.Count == 0)
            return false;

        return await CommitFileIdsAsync(targetGroupVm.Id, groupModel =>
        {
            var added = false;
            var existing = groupModel.FileIds.ToHashSet(StringComparer.Ordinal);
            foreach (var fileId in fileIds)
            {
                if (string.IsNullOrWhiteSpace(fileId) || !existing.Add(fileId))
                    continue;

                groupModel.FileIds.Add(fileId);
                added = true;
            }

            return Task.FromResult(added);
        });
    }

    public async Task<bool> ReorderFilesInDashboardAsync(
        LogGroupViewModel groupVm,
        IReadOnlyList<string> draggedFileIds,
        string targetFileId,
        DropPlacement placement)
    {
        if (!groupVm.CanManageFiles ||
            placement == DropPlacement.Inside ||
            draggedFileIds.Count == 0 ||
            string.IsNullOrWhiteSpace(targetFileId))
        {
            return false;
        }

        var draggedFileIdSet = CreateDistinctFileIdSet(draggedFileIds);
        if (draggedFileIdSet.Count == 0 || draggedFileIdSet.Contains(targetFileId))
            return false;

        return await CommitFileIdsAsync(groupVm.Id, groupModel =>
        {
            var movingFileIds = groupModel.FileIds
                .Where(draggedFileIdSet.Contains)
                .ToList();
            if (movingFileIds.Count != draggedFileIdSet.Count)
                return Task.FromResult(false);

            var nextFileIds = groupModel.FileIds
                .Where(fileId => !draggedFileIdSet.Contains(fileId))
                .ToList();
            var targetIndex = nextFileIds.IndexOf(targetFileId);
            if (targetIndex < 0)
                return Task.FromResult(false);

            var insertIndex = placement == DropPlacement.After ? targetIndex + 1 : targetIndex;
            nextFileIds.InsertRange(insertIndex, movingFileIds);
            if (groupModel.FileIds.SequenceEqual(nextFileIds))
                return Task.FromResult(false);

            ReplaceFileIds(groupModel.FileIds, nextFileIds);
            return Task.FromResult(true);
        });
    }

    public async Task<bool> MoveFilesBetweenDashboardsAsync(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        IReadOnlyList<string> draggedFileIds,
        string? targetFileId,
        DropPlacement placement)
    {
        var sourceGroupId = sourceGroupVm.Id;
        var targetGroupId = targetGroupVm.Id;
        return await _mutationCoordinator.ExecuteAsync(async () =>
        {
            var currentSource = ResolveCurrentGroup(sourceGroupId);
            var currentTarget = ResolveCurrentGroup(targetGroupId);
            if (currentSource is not { CanManageFiles: true } ||
                currentTarget is not { CanManageFiles: true } ||
                string.Equals(sourceGroupId, targetGroupId, StringComparison.Ordinal) ||
                draggedFileIds.Count == 0)
            {
                return false;
            }

            var allModels = (await _groupRepo.GetAllAsync()).Select(CloneGroup).ToList();
            var sourceModel = allModels.FirstOrDefault(group => group.Id == sourceGroupId);
            var targetModel = allModels.FirstOrDefault(group => group.Id == targetGroupId);
            if (sourceModel == null || targetModel == null)
                return false;

            var draggedFileIdSet = CreateDistinctFileIdSet(draggedFileIds);
            if (draggedFileIdSet.Count == 0 ||
                draggedFileIdSet.Any(fileId => !sourceModel.FileIds.Contains(fileId)) ||
                draggedFileIdSet.Any(fileId => targetModel.FileIds.Contains(fileId)))
            {
                return false;
            }

            var movingFileIds = sourceModel.FileIds
                .Where(draggedFileIdSet.Contains)
                .ToList();
            if (movingFileIds.Count != draggedFileIdSet.Count)
                return false;

            var insertIndex = ResolveCrossDashboardInsertIndex(targetModel.FileIds, targetFileId, placement);
            if (insertIndex < 0)
                return false;

            sourceModel.FileIds.RemoveAll(draggedFileIdSet.Contains);
            targetModel.FileIds.InsertRange(insertIndex, movingFileIds);
            await _groupRepo.ReplaceAllAsync(allModels);

            ReplaceFileIds(currentSource.Model.FileIds, sourceModel.FileIds);
            ReplaceFileIds(currentTarget.Model.FileIds, targetModel.FileIds);
            currentSource.NotifyStructureChanged();
            currentTarget.NotifyStructureChanged();
            return true;
        });
    }

    private static HashSet<string> CreateDistinctFileIdSet(IEnumerable<string> fileIds)
        => fileIds
            .Where(fileId => !string.IsNullOrWhiteSpace(fileId))
            .ToHashSet(StringComparer.Ordinal);

    private async Task<bool> CommitFileIdsAsync(
        string groupId,
        Func<LogGroup, Task<bool>> planMutationAsync)
    {
        return await _mutationCoordinator.ExecuteAsync(async () =>
        {
            var currentGroup = ResolveCurrentGroup(groupId);
            if (currentGroup is not { CanManageFiles: true })
                return false;

            var allModels = (await _groupRepo.GetAllAsync()).Select(CloneGroup).ToList();
            var groupModel = allModels.FirstOrDefault(group => group.Id == groupId);
            if (groupModel == null || !await planMutationAsync(groupModel))
                return false;

            await _groupRepo.ReplaceAllAsync(allModels);
            ReplaceFileIds(currentGroup.Model.FileIds, groupModel.FileIds);
            currentGroup.NotifyStructureChanged();
            return true;
        });
    }

    private async Task<HashSet<string>> GetExistingDashboardPathsAsync(IEnumerable<string> groupFileIds)
    {
        var fileIds = new HashSet<string>(groupFileIds, StringComparer.Ordinal);
        if (fileIds.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var entriesById = await _fileCatalogService.GetByIdsAsync(fileIds);
        return entriesById.Values
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FilePath))
            .Select(entry => entry.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task ResortDashboardFileIdsAsync(
        LogGroup groupModel,
        IReadOnlyDictionary<string, LogFileEntry> entriesByAddedPath)
    {
        var entriesById = await _fileCatalogService.GetByIdsAsync(groupModel.FileIds);
        var addedEntriesById = entriesByAddedPath.Values.ToDictionary(entry => entry.Id, StringComparer.Ordinal);

        var sortedKnownFileIds = groupModel.FileIds
            .Where(fileId => entriesById.ContainsKey(fileId) || addedEntriesById.ContainsKey(fileId))
            .OrderBy(
                fileId => GetFileName(fileId, entriesById, addedEntriesById),
                NaturalFileNameComparer.Instance)
            .ToList();

        var unknownFileIds = groupModel.FileIds
            .Where(fileId => !entriesById.ContainsKey(fileId) && !addedEntriesById.ContainsKey(fileId))
            .ToList();

        groupModel.FileIds.Clear();
        groupModel.FileIds.AddRange(sortedKnownFileIds);
        groupModel.FileIds.AddRange(unknownFileIds);
    }

    private static string GetFileName(
        string fileId,
        IReadOnlyDictionary<string, LogFileEntry> entriesById,
        IReadOnlyDictionary<string, LogFileEntry> addedEntriesById)
    {
        if (entriesById.TryGetValue(fileId, out var existingEntry) && !string.IsNullOrWhiteSpace(existingEntry.FilePath))
            return Path.GetFileName(existingEntry.FilePath);

        if (addedEntriesById.TryGetValue(fileId, out var addedEntry) && !string.IsNullOrWhiteSpace(addedEntry.FilePath))
            return Path.GetFileName(addedEntry.FilePath);

        return string.Empty;
    }

    private static IReadOnlyList<string> DistinctLiteralFilePaths(IEnumerable<string> filePaths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinctPaths = new List<string>();
        foreach (var filePath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !seen.Add(filePath))
                continue;

            distinctPaths.Add(filePath);
        }

        return distinctPaths;
    }

    private LogGroupViewModel? ResolveCurrentGroup(string groupId)
        => _host.Groups.FirstOrDefault(group => string.Equals(group.Id, groupId, StringComparison.Ordinal));

    private static void ReplaceFileIds(List<string> destination, IReadOnlyList<string> source)
    {
        destination.Clear();
        destination.AddRange(source);
    }

    private static LogGroup CloneGroup(LogGroup group)
    {
        return new LogGroup
        {
            Id = group.Id,
            Name = group.Name,
            SortOrder = group.SortOrder,
            ParentGroupId = group.ParentGroupId,
            Kind = group.Kind,
            FileIds = group.FileIds.ToList()
        };
    }

    private static int ResolveCrossDashboardInsertIndex(
        IReadOnlyList<string> targetFileIds,
        string? targetFileId,
        DropPlacement placement)
    {
        if (string.IsNullOrWhiteSpace(targetFileId))
            return placement == DropPlacement.Inside ? targetFileIds.Count : -1;

        var targetIndex = targetFileIds.ToList().IndexOf(targetFileId);
        if (targetIndex < 0)
            return -1;

        return placement switch
        {
            DropPlacement.Before => targetIndex,
            DropPlacement.After => targetIndex + 1,
            _ => -1
        };
    }
}
