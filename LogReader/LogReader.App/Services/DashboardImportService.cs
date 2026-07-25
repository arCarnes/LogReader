namespace LogReader.App.Services;

using System.IO;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed record ImportedView(string StoredPath, string? PendingPath, ViewExport Export);

internal sealed record DashboardImportResult(IReadOnlyList<LogGroup> Groups);

internal sealed class DashboardImportService
{
    private readonly ILogGroupRepository _groupRepository;
    private readonly LogFileCatalogService _fileCatalogService;
    private readonly Func<IEnumerable<LogFileEntry>, Task> _cleanupCreatedEntriesAsync;

    public DashboardImportService(
        ILogGroupRepository groupRepository,
        LogFileCatalogService fileCatalogService,
        Func<IEnumerable<LogFileEntry>, Task> cleanupCreatedEntriesAsync)
    {
        _groupRepository = groupRepository;
        _fileCatalogService = fileCatalogService;
        _cleanupCreatedEntriesAsync = cleanupCreatedEntriesAsync;
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
            return new ImportedView(storedPath, PendingPath: null, inPlaceExport);
        }

        var tempPath = CreateImportingPath(storedPath);
        try
        {
            File.Copy(importPath, tempPath, overwrite: true);
            var export = await _groupRepository.ImportViewAsync(tempPath);
            if (export == null)
                throw new InvalidDataException("The imported dashboard view could not be read from the app storage copy.");

            DashboardTopologyValidator.ValidateImportedView(export);
            return new ImportedView(storedPath, tempPath, export);
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

        var registration = await _fileCatalogService.EnsureRegisteredWithChangesAsync(
            importedGroups
                .Where(group => group.Source.Kind == LogGroupKind.Dashboard)
                .SelectMany(group => group.Source.FilePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase));

        try
        {
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
                            .Select(path => registration.EntriesByPath[path].Id)
                            .ToList()
                        : new List<string>()
                })
                .ToList();

            DashboardTopologyValidator.ValidatePersistedGroups(replacementGroups);
            await _groupRepository.ReplaceAllAsync(replacementGroups);
            await _fileCatalogService.CompleteRegistrationAsync(registration.CreatedEntries);

            return new DashboardImportResult(replacementGroups);
        }
        catch (Exception importException)
        {
            try
            {
                await _cleanupCreatedEntriesAsync(registration.CreatedEntries);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "The dashboard import failed and its newly created file metadata could not be cleaned up.",
                    importException,
                    cleanupException);
            }

            throw;
        }
    }

    public async Task<DashboardImportResult> ApplyImportedViewAsync(ImportedView importedView)
    {
        ArgumentNullException.ThrowIfNull(importedView);

        DashboardTopologyValidator.ValidateImportedView(importedView.Export);
        var promotion = BeginPendingImportPromotion(importedView);
        try
        {
            var storedExport = await _groupRepository.ImportViewAsync(importedView.StoredPath);
            if (storedExport == null)
                throw new InvalidDataException("The stored dashboard view could not be read.");

            DashboardTopologyValidator.ValidateImportedView(storedExport);
            var result = await ApplyImportedViewAsync(storedExport);
            CommitPendingImportPromotion(promotion);
            return result;
        }
        catch (Exception importException)
        {
            try
            {
                RollBackPendingImportPromotion(promotion);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "The dashboard import failed and its stored-view promotion could not be rolled back.",
                    importException,
                    rollbackException);
            }

            throw;
        }
    }

    public void DiscardImportedView(ImportedView importedView)
    {
        ArgumentNullException.ThrowIfNull(importedView);

        if (importedView.PendingPath != null)
            TryDeleteFile(importedView.PendingPath);
    }

    private sealed record PlannedImportedGroup(ViewExportGroup Source, string NewId);

    private sealed record PendingImportPromotion(
        string StoredPath,
        string PendingPath,
        string? PreviousPath);

    private static string CreateImportingPath(string storedPath)
        => storedPath + ".importing";

    private static PendingImportPromotion? BeginPendingImportPromotion(ImportedView importedView)
    {
        if (importedView.PendingPath == null)
            return null;

        string? previousPath = null;
        if (File.Exists(importedView.StoredPath))
        {
            previousPath = importedView.StoredPath + "." + Guid.NewGuid().ToString("N") + ".previous";
            File.Move(importedView.StoredPath, previousPath);
        }

        try
        {
            File.Move(importedView.PendingPath, importedView.StoredPath);
        }
        catch
        {
            if (previousPath != null && File.Exists(previousPath))
                File.Move(previousPath, importedView.StoredPath, overwrite: true);

            throw;
        }

        return new PendingImportPromotion(importedView.StoredPath, importedView.PendingPath, previousPath);
    }

    private static void CommitPendingImportPromotion(PendingImportPromotion? promotion)
    {
        if (promotion?.PreviousPath != null)
            TryDeleteFile(promotion.PreviousPath);
    }

    private static void RollBackPendingImportPromotion(PendingImportPromotion? promotion)
    {
        if (promotion == null)
            return;

        if (File.Exists(promotion.StoredPath))
            File.Move(promotion.StoredPath, promotion.PendingPath, overwrite: true);

        if (promotion.PreviousPath != null && File.Exists(promotion.PreviousPath))
            File.Move(promotion.PreviousPath, promotion.StoredPath, overwrite: true);
    }

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
