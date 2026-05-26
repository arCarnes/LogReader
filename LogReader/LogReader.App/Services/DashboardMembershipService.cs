namespace LogReader.App.Services;

using System.IO;
using LogReader.App.Models;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class DashboardMembershipService
{
    private readonly LogFileCatalogService _fileCatalogService;
    private readonly ILogGroupRepository _groupRepo;

    public DashboardMembershipService(
        LogFileCatalogService fileCatalogService,
        ILogGroupRepository groupRepo)
    {
        _fileCatalogService = fileCatalogService;
        _groupRepo = groupRepo;
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

        var existingPaths = await GetExistingDashboardPathsAsync(groupVm);
        var entriesByPath = await _fileCatalogService.EnsureRegisteredAsync(parsedPaths);
        var added = false;
        foreach (var path in parsedPaths)
        {
            if (existingPaths.Contains(path))
                continue;

            if (!entriesByPath.TryGetValue(path, out var entry))
                continue;

            if (!groupVm.Model.FileIds.Contains(entry.Id))
            {
                groupVm.Model.FileIds.Add(entry.Id);
                existingPaths.Add(path);
                added = true;
            }
        }

        if (!added)
            return false;

        await ResortDashboardFileIdsAsync(groupVm, entriesByPath);
        groupVm.NotifyStructureChanged();
        await _groupRepo.UpdateAsync(groupVm.Model);
        return true;
    }

    public async Task<bool> RemoveFileFromDashboardAsync(LogGroupViewModel groupVm, string fileId)
        => await RemoveFilesFromDashboardAsync(groupVm, new[] { fileId });

    public async Task<bool> RemoveFilesFromDashboardAsync(LogGroupViewModel groupVm, IReadOnlyList<string> fileIds)
    {
        if (!groupVm.CanManageFiles || fileIds.Count == 0)
            return false;

        var removed = false;
        var distinctFileIds = fileIds
            .Where(fileId => !string.IsNullOrWhiteSpace(fileId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var fileId in distinctFileIds)
            removed = groupVm.Model.FileIds.Remove(fileId) || removed;

        if (!removed)
            return false;

        groupVm.NotifyStructureChanged();
        await _groupRepo.UpdateAsync(groupVm.Model);
        return true;
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

        var added = false;
        var existing = targetGroupVm.Model.FileIds.ToHashSet(StringComparer.Ordinal);
        foreach (var fileId in fileIds)
        {
            if (string.IsNullOrWhiteSpace(fileId) || !existing.Add(fileId))
                continue;

            targetGroupVm.Model.FileIds.Add(fileId);
            added = true;
        }

        if (!added)
            return false;

        targetGroupVm.NotifyStructureChanged();
        await _groupRepo.UpdateAsync(targetGroupVm.Model);
        return true;
    }

    public async Task<bool> ReorderFileInDashboardAsync(
        LogGroupViewModel groupVm,
        string draggedFileId,
        string targetFileId,
        DropPlacement placement)
        => await ReorderFilesInDashboardAsync(groupVm, new[] { draggedFileId }, targetFileId, placement);

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

        var movingFileIds = groupVm.Model.FileIds
            .Where(draggedFileIdSet.Contains)
            .ToList();
        if (movingFileIds.Count != draggedFileIdSet.Count)
            return false;

        var nextFileIds = groupVm.Model.FileIds
            .Where(fileId => !draggedFileIdSet.Contains(fileId))
            .ToList();
        var targetIndex = nextFileIds.IndexOf(targetFileId);
        if (targetIndex < 0)
            return false;

        var insertIndex = placement == DropPlacement.After ? targetIndex + 1 : targetIndex;
        nextFileIds.InsertRange(insertIndex, movingFileIds);

        if (groupVm.Model.FileIds.SequenceEqual(nextFileIds))
            return false;

        groupVm.Model.FileIds.Clear();
        groupVm.Model.FileIds.AddRange(nextFileIds);
        groupVm.NotifyStructureChanged();
        await _groupRepo.UpdateAsync(groupVm.Model);
        return true;
    }

    public async Task<bool> MoveFileBetweenDashboardsAsync(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId,
        string? targetFileId,
        DropPlacement placement)
        => await MoveFilesBetweenDashboardsAsync(sourceGroupVm, targetGroupVm, new[] { draggedFileId }, targetFileId, placement);

    public async Task<bool> MoveFilesBetweenDashboardsAsync(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        IReadOnlyList<string> draggedFileIds,
        string? targetFileId,
        DropPlacement placement)
    {
        if (!sourceGroupVm.CanManageFiles ||
            !targetGroupVm.CanManageFiles ||
            string.Equals(sourceGroupVm.Id, targetGroupVm.Id, StringComparison.Ordinal) ||
            draggedFileIds.Count == 0)
        {
            return false;
        }

        var draggedFileIdSet = CreateDistinctFileIdSet(draggedFileIds);
        if (draggedFileIdSet.Count == 0 ||
            draggedFileIdSet.Any(fileId => !sourceGroupVm.Model.FileIds.Contains(fileId)) ||
            draggedFileIdSet.Any(fileId => targetGroupVm.Model.FileIds.Contains(fileId)))
        {
            return false;
        }

        var movingFileIds = sourceGroupVm.Model.FileIds
            .Where(draggedFileIdSet.Contains)
            .ToList();
        if (movingFileIds.Count != draggedFileIdSet.Count)
            return false;

        var insertIndex = ResolveCrossDashboardInsertIndex(targetGroupVm, targetFileId, placement);
        if (insertIndex < 0)
            return false;

        sourceGroupVm.Model.FileIds.RemoveAll(draggedFileIdSet.Contains);
        targetGroupVm.Model.FileIds.InsertRange(insertIndex, movingFileIds);
        sourceGroupVm.NotifyStructureChanged();
        targetGroupVm.NotifyStructureChanged();
        await _groupRepo.UpdateAsync(sourceGroupVm.Model);
        await _groupRepo.UpdateAsync(targetGroupVm.Model);
        return true;
    }

    private static HashSet<string> CreateDistinctFileIdSet(IEnumerable<string> fileIds)
        => fileIds
            .Where(fileId => !string.IsNullOrWhiteSpace(fileId))
            .ToHashSet(StringComparer.Ordinal);

    private async Task<HashSet<string>> GetExistingDashboardPathsAsync(LogGroupViewModel groupVm)
    {
        var fileIds = new HashSet<string>(groupVm.Model.FileIds, StringComparer.Ordinal);
        if (fileIds.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var entriesById = await _fileCatalogService.GetByIdsAsync(fileIds);
        return entriesById.Values
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FilePath))
            .Select(entry => entry.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task ResortDashboardFileIdsAsync(
        LogGroupViewModel groupVm,
        IReadOnlyDictionary<string, LogFileEntry> entriesByAddedPath)
    {
        var entriesById = await _fileCatalogService.GetByIdsAsync(groupVm.Model.FileIds);
        var addedEntriesById = entriesByAddedPath.Values.ToDictionary(entry => entry.Id, StringComparer.Ordinal);

        var sortedKnownFileIds = groupVm.Model.FileIds
            .Where(fileId => entriesById.ContainsKey(fileId) || addedEntriesById.ContainsKey(fileId))
            .OrderBy(
                fileId => GetFileName(fileId, entriesById, addedEntriesById),
                NaturalFileNameComparer.Instance)
            .ToList();

        var unknownFileIds = groupVm.Model.FileIds
            .Where(fileId => !entriesById.ContainsKey(fileId) && !addedEntriesById.ContainsKey(fileId));

        groupVm.Model.FileIds.Clear();
        groupVm.Model.FileIds.AddRange(sortedKnownFileIds);
        groupVm.Model.FileIds.AddRange(unknownFileIds);
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

    private static int ResolveCrossDashboardInsertIndex(
        LogGroupViewModel targetGroupVm,
        string? targetFileId,
        DropPlacement placement)
    {
        if (string.IsNullOrWhiteSpace(targetFileId))
            return placement == DropPlacement.Inside ? targetGroupVm.Model.FileIds.Count : -1;

        var targetIndex = targetGroupVm.Model.FileIds.IndexOf(targetFileId);
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
