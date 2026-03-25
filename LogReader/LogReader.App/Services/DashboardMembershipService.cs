namespace LogReader.App.Services;

using System.IO;
using LogReader.App.Models;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;

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

    internal static IReadOnlyList<string> ParseBulkFilePaths(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return Array.Empty<string>();

        var parsedPaths = new List<string>();
        using var reader = new StringReader(rawInput);
        while (reader.ReadLine() is { } line)
        {
            var parsedPath = ParseBulkFilePathLine(line);
            if (parsedPath == null)
                continue;

            parsedPaths.Add(parsedPath);
        }

        return DistinctLiteralFilePaths(parsedPaths);
    }

    internal static BulkFilePreview BuildBulkFilePreview(
        string? rawInput,
        Func<string, bool>? fileExists = null)
    {
        var parsedPaths = ParseBulkFilePaths(rawInput);
        var fileExistsEvaluator = fileExists ?? File.Exists;
        var items = parsedPaths
            .Select(path => new BulkFilePreviewItem(path, fileExistsEvaluator(path)))
            .ToList();

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
}
