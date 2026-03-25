namespace LogReader.App.Services;

using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

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

    public async Task<ViewExport?> ImportViewAsync(string importPath)
    {
        var export = await _groupRepository.ImportViewAsync(importPath);
        if (export != null)
            DashboardTopologyValidator.ValidateImportedView(export);

        return export;
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

    private sealed record PlannedImportedGroup(ViewExportGroup Source, string NewId);
}
