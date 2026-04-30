namespace LogReader.App.Services;

using System.IO;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed record ImportedView(string StoredPath, ViewExport Export);

internal sealed record DashboardImportResult(IReadOnlyList<LogGroup> Groups);

internal sealed class DashboardImportService
{
    private readonly ILogGroupRepository _groupRepository;
    private readonly LogFileCatalogService _fileCatalogService;

    public DashboardImportService(
        ILogGroupRepository groupRepository,
        LogFileCatalogService fileCatalogService)
    {
        _groupRepository = groupRepository;
        _fileCatalogService = fileCatalogService;
    }

    public Task ExportViewAsync(string exportPath)
        => _groupRepository.ExportViewAsync(exportPath);

    public async Task<ImportedView?> ImportViewAsync(string importPath)
    {
        if (!File.Exists(importPath))
            return null;

        var storedPath = GetImportedViewStoragePath(importPath);
        if (PathsReferToSameFile(importPath, storedPath))
        {
            var inPlaceExport = await _groupRepository.ImportViewAsync(storedPath);
            if (inPlaceExport == null)
                throw new InvalidDataException("The imported dashboard view could not be read from the app storage copy.");

            DashboardTopologyValidator.ValidateImportedView(inPlaceExport);
            return new ImportedView(storedPath, inPlaceExport);
        }

        var tempPath = storedPath + ".importing";
        try
        {
            File.Copy(importPath, tempPath, overwrite: true);
            var export = await _groupRepository.ImportViewAsync(tempPath);
            if (export == null)
                throw new InvalidDataException("The imported dashboard view could not be read from the app storage copy.");

            DashboardTopologyValidator.ValidateImportedView(export);
            File.Move(tempPath, storedPath, overwrite: true);
            return new ImportedView(storedPath, export);
        }
        catch
        {
            TryDeleteFile(tempPath);

            throw;
        }
    }

    public async Task<DashboardImportResult> ApplyImportedViewAsync(ViewExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        DashboardTopologyValidator.ValidateImportedView(export);

        var importedGroups = (export.Groups ?? new List<ViewExportGroup>())
            .Select(group => new PlannedImportedGroup(group, Guid.NewGuid().ToString()))
            .ToList();
        var importedIdMap = importedGroups.ToDictionary(
            importedGroup => importedGroup.Source.Id,
            importedGroup => importedGroup.NewId,
            StringComparer.Ordinal);

        var fileEntriesByPath = await _fileCatalogService.EnsureRegisteredAsync(
            importedGroups
                .Where(group => group.Source.Kind == LogGroupKind.Dashboard)
                .SelectMany(group => group.Source.FilePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase));

        var replacementGroups = importedGroups
            .OrderBy(group => group.Source.SortOrder)
            .Select(group => new LogGroup
            {
                Id = group.NewId,
                Name = group.Source.Name,
                SortOrder = group.Source.SortOrder,
                ParentGroupId = string.IsNullOrWhiteSpace(group.Source.ParentGroupId)
                    ? null
                    : importedIdMap[group.Source.ParentGroupId],
                Kind = group.Source.Kind,
                FileIds = group.Source.Kind == LogGroupKind.Dashboard
                    ? group.Source.FilePaths
                        .Select(path => fileEntriesByPath[path].Id)
                        .ToList()
                    : new List<string>()
            })
            .ToList();

        DashboardTopologyValidator.ValidatePersistedGroups(replacementGroups);
        await _groupRepository.ReplaceAllAsync(replacementGroups);

        return new DashboardImportResult(replacementGroups);
    }

    public async Task<DashboardImportResult> ApplyImportedViewAsync(ImportedView importedView)
    {
        ArgumentNullException.ThrowIfNull(importedView);

        var storedExport = await _groupRepository.ImportViewAsync(importedView.StoredPath);
        if (storedExport == null)
            throw new InvalidDataException("The stored dashboard view could not be read.");

        DashboardTopologyValidator.ValidateImportedView(storedExport);
        return await ApplyImportedViewAsync(storedExport);
    }

    private sealed record PlannedImportedGroup(ViewExportGroup Source, string NewId);

    private static string GetImportedViewStoragePath(string importPath)
    {
        var viewsDirectory = AppPaths.EnsureDirectory(AppPaths.ViewsDirectory);
        var fileName = Path.GetFileName(importPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "imported-view.json";

        return Path.Combine(viewsDirectory, fileName);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static bool PathsReferToSameFile(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
