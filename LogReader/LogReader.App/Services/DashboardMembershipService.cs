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
    {
        if (!groupVm.CanManageFiles)
            return false;

        if (!groupVm.Model.FileIds.Remove(fileId))
            return false;

        groupVm.NotifyStructureChanged();
        await _groupRepo.UpdateAsync(groupVm.Model);
        return true;
    }

    public async Task<bool> ReorderFileInDashboardAsync(
        LogGroupViewModel groupVm,
        string draggedFileId,
        string targetFileId,
        DropPlacement placement)
    {
        if (!groupVm.CanManageFiles ||
            placement == DropPlacement.Inside ||
            string.IsNullOrWhiteSpace(draggedFileId) ||
            string.IsNullOrWhiteSpace(targetFileId) ||
            string.Equals(draggedFileId, targetFileId, StringComparison.Ordinal))
        {
            return false;
        }

        var sourceIndex = groupVm.Model.FileIds.IndexOf(draggedFileId);
        var targetIndex = groupVm.Model.FileIds.IndexOf(targetFileId);
        if (sourceIndex < 0 || targetIndex < 0)
            return false;

        var insertIndex = targetIndex;
        if (sourceIndex < targetIndex)
            insertIndex--;

        if (placement == DropPlacement.After)
            insertIndex++;

        if (insertIndex == sourceIndex)
            return false;

        groupVm.Model.FileIds.RemoveAt(sourceIndex);
        groupVm.Model.FileIds.Insert(insertIndex, draggedFileId);
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
    {
        if (!sourceGroupVm.CanManageFiles ||
            !targetGroupVm.CanManageFiles ||
            string.Equals(sourceGroupVm.Id, targetGroupVm.Id, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(draggedFileId) ||
            !sourceGroupVm.Model.FileIds.Contains(draggedFileId) ||
            targetGroupVm.Model.FileIds.Contains(draggedFileId))
        {
            return false;
        }

        var insertIndex = ResolveCrossDashboardInsertIndex(targetGroupVm, targetFileId, placement);
        if (insertIndex < 0)
            return false;

        if (!sourceGroupVm.Model.FileIds.Remove(draggedFileId))
            return false;

        targetGroupVm.Model.FileIds.Insert(insertIndex, draggedFileId);
        sourceGroupVm.NotifyStructureChanged();
        targetGroupVm.NotifyStructureChanged();
        await _groupRepo.UpdateAsync(sourceGroupVm.Model);
        await _groupRepo.UpdateAsync(targetGroupVm.Model);
        return true;
    }

    internal static IReadOnlyList<string> ParseBulkFilePaths(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return Array.Empty<string>();

        var parsedPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(rawInput);
        while (reader.ReadLine() is { } line)
        {
            var parsedPath = ParseBulkFilePathLine(line);
            if (parsedPath == null)
                continue;

            foreach (var resolvedPath in ExpandBulkFilePathPattern(parsedPath))
            {
                if (!seenPaths.Add(resolvedPath))
                    continue;

                parsedPaths.Add(resolvedPath);
            }
        }

        return parsedPaths;
    }

    internal static BulkFilePreview BuildBulkFilePreview(
        string? rawInput,
        Func<string, bool>? fileExists = null)
    {
        var fileExistsEvaluator = fileExists ?? File.Exists;
        var parsedPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<BulkFilePreviewItem>();
        var seenUnmatchedPatterns = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(rawInput))
        {
            using var reader = new StringReader(rawInput);
            while (reader.ReadLine() is { } line)
            {
                var parsedPath = ParseBulkFilePathLine(line);
                if (parsedPath == null)
                    continue;

                if (ContainsWildcard(parsedPath))
                {
                    var resolvedPaths = ExpandWildcardFilePaths(parsedPath);
                    if (resolvedPaths.Count == 0)
                    {
                        if (seenUnmatchedPatterns.Add(parsedPath))
                            items.Add(new BulkFilePreviewItem(parsedPath, BulkFilePreviewItemStatus.NoMatches));

                        continue;
                    }

                    foreach (var resolvedPath in resolvedPaths)
                    {
                        if (!seenPaths.Add(resolvedPath))
                            continue;

                        parsedPaths.Add(resolvedPath);
                        items.Add(new BulkFilePreviewItem(
                            resolvedPath,
                            fileExistsEvaluator(resolvedPath) ? BulkFilePreviewItemStatus.Found : BulkFilePreviewItemStatus.Missing));
                    }

                    continue;
                }

                if (!seenPaths.Add(parsedPath))
                    continue;

                parsedPaths.Add(parsedPath);
                items.Add(new BulkFilePreviewItem(
                    parsedPath,
                    fileExistsEvaluator(parsedPath) ? BulkFilePreviewItemStatus.Found : BulkFilePreviewItemStatus.Missing));
            }
        }

        return new BulkFilePreview(parsedPaths, items);
    }

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

    private static string? ParseBulkFilePathLine(string line)
    {
        var trimmedLine = line.Trim();
        if (trimmedLine.Length == 0)
            return null;

        if (trimmedLine.Length >= 2 &&
            (trimmedLine[0] == '"' || trimmedLine[0] == '\'') &&
            trimmedLine[0] == trimmedLine[^1])
        {
            trimmedLine = trimmedLine[1..^1];
        }

        return string.IsNullOrWhiteSpace(trimmedLine) ? null : trimmedLine;
    }

    private static IReadOnlyList<string> ExpandBulkFilePathPattern(string pathOrPattern)
    {
        if (!ContainsWildcard(pathOrPattern))
            return new[] { pathOrPattern };

        return ExpandWildcardFilePaths(pathOrPattern);
    }

    private static List<string> ExpandWildcardFilePaths(string pathPattern)
    {
        try
        {
            var directory = Path.GetDirectoryName(pathPattern);
            var fileSegment = Path.GetFileName(pathPattern);
            if (string.IsNullOrWhiteSpace(fileSegment))
                return new List<string>();

            var resolvedDirectory = string.IsNullOrWhiteSpace(directory)
                ? Environment.CurrentDirectory
                : directory;
            if (ContainsWildcard(resolvedDirectory) || !Directory.Exists(resolvedDirectory))
                return new List<string>();

            if (!ContainsWildcard(fileSegment))
            {
                var candidatePath = Path.Combine(resolvedDirectory, fileSegment);
                return File.Exists(candidatePath)
                    ? new List<string> { candidatePath }
                    : new List<string>();
            }

            return Directory
                .EnumerateFiles(resolvedDirectory, fileSegment, SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (ArgumentException)
        {
            return new List<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return new List<string>();
        }
        catch (IOException)
        {
            return new List<string>();
        }
        catch (NotSupportedException)
        {
            return new List<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static bool ContainsWildcard(string path)
        => path.IndexOfAny(['*', '?']) >= 0;

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
