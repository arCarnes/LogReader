namespace LogReader.App.Services;

using LogReader.App.Models;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class DashboardWorkspaceService
{
    private readonly IDashboardWorkspaceHost _host;
    private readonly ILogGroupRepository _groupRepo;
    private readonly LogFileCatalogService _fileCatalogService;
    private readonly DashboardImportService _dashboardImportService;
    private readonly DashboardActivationService _dashboardActivationService;
    private readonly DashboardTreeService _dashboardTreeService;
    private readonly DashboardMembershipService _dashboardMembershipService;
    private readonly DashboardMutationCoordinator _mutationCoordinator;

    public DashboardWorkspaceService(IDashboardWorkspaceHost host, ILogFileRepository fileRepo, ILogGroupRepository groupRepo)
        : this(host, fileRepo, groupRepo, null, null)
    {
    }

    internal DashboardWorkspaceService(
        IDashboardWorkspaceHost host,
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo,
        Func<IReadOnlyDictionary<string, string>, Task<Dictionary<string, bool>>> buildFileExistenceMapAsync)
        : this(host, fileRepo, groupRepo, null, buildFileExistenceMapAsync)
    {
    }

    internal DashboardWorkspaceService(
        IDashboardWorkspaceHost host,
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo,
        LogFileCatalogService? fileCatalogService,
        Func<IReadOnlyDictionary<string, string>, Task<Dictionary<string, bool>>>? buildFileExistenceMapAsync,
        DashboardActivationService? dashboardActivationService = null)
    {
        _host = host;
        _groupRepo = groupRepo;
        _mutationCoordinator = new DashboardMutationCoordinator();
        _fileCatalogService = fileCatalogService ?? new LogFileCatalogService(fileRepo);
        _dashboardImportService = new DashboardImportService(groupRepo, _fileCatalogService, CleanupCreatedEntriesAsync);
        _dashboardActivationService = dashboardActivationService ?? (buildFileExistenceMapAsync == null
            ? new DashboardActivationService(host, fileRepo, groupRepo)
            : new DashboardActivationService(host, fileRepo, groupRepo, buildFileExistenceMapAsync));
        _dashboardTreeService = new DashboardTreeService(
            host,
            groupRepo,
            _mutationCoordinator,
            _dashboardActivationService.LeaveActiveDashboardScope,
            _dashboardActivationService.PruneModifierState);
        _dashboardMembershipService = new DashboardMembershipService(host, _fileCatalogService, groupRepo, _mutationCoordinator);
    }

    public async Task CreateGroupAsync(LogGroupKind kind)
    {
        await _dashboardTreeService.CreateGroupAsync(kind);
        await _dashboardActivationService.RefreshAllMemberFilesAsync();
    }

    public async Task<bool> CreateChildGroupAsync(LogGroupViewModel parent, LogGroupKind kind = LogGroupKind.Dashboard)
    {
        var created = await _dashboardTreeService.CreateChildGroupAsync(parent, kind);
        if (created)
            await _dashboardActivationService.RefreshAllMemberFilesAsync();

        return created;
    }

    public async Task DeleteGroupAsync(LogGroupViewModel? groupVm)
    {
        await _dashboardTreeService.DeleteGroupAsync(groupVm);
        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public Task ExportViewAsync(string exportPath)
        => _dashboardImportService.ExportViewAsync(exportPath);

    public Task<ImportedView?> ImportViewAsync(string importPath)
        => _dashboardImportService.ImportViewAsync(importPath);

    public void DiscardImportedView(ImportedView importedView)
        => _dashboardImportService.DiscardImportedView(importedView);

    public async Task ApplyImportedViewAsync(ViewExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        _dashboardActivationService.CancelDashboardLoad();
        await _mutationCoordinator.ExecuteAsync(async () =>
        {
            var result = await _dashboardImportService.ApplyImportedViewAsync(export);
            _dashboardActivationService.LeaveActiveDashboardScope();
            RebuildGroupsCollection(result.Groups.ToList());
        });
        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task ApplyImportedViewAsync(ImportedView importedView)
    {
        ArgumentNullException.ThrowIfNull(importedView);

        _dashboardActivationService.CancelDashboardLoad();
        await _mutationCoordinator.ExecuteAsync(async () =>
        {
            var result = await _dashboardImportService.ApplyImportedViewAsync(importedView);
            _dashboardActivationService.LeaveActiveDashboardScope();
            RebuildGroupsCollection(result.Groups.ToList());
        });
        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task<bool> AddFilesToDashboardAsync(LogGroupViewModel groupVm, IReadOnlyList<string> filePaths)
    {
        if (!await _dashboardMembershipService.AddFilesToDashboardAsync(groupVm, filePaths))
            return false;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
        return true;
    }

    internal async Task RepairDashboardFileIdsAsync(IReadOnlyDictionary<string, string> knownPathsByOldId)
    {
        if (knownPathsByOldId.Count == 0)
            return;

        await _mutationCoordinator.ExecuteAsync(async () =>
        {
            if (_host.Groups.Count == 0)
                return;

            var registration = await _fileCatalogService.EnsureRegisteredWithChangesAsync(
                knownPathsByOldId.Values.Distinct(StringComparer.OrdinalIgnoreCase));
            try
            {
                var plannedGroups = _host.Groups.Select(group => CloneGroup(group.Model)).ToList();
                var changedFileIdsByGroupId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                foreach (var group in plannedGroups)
                {
                    if (group.FileIds.Count == 0)
                        continue;

                    var replacementIds = new List<string>(group.FileIds.Count);
                    var seenReplacementIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var fileId in group.FileIds)
                    {
                        if (!knownPathsByOldId.TryGetValue(fileId, out var filePath) ||
                            !registration.EntriesByPath.TryGetValue(filePath, out var entry) ||
                            !seenReplacementIds.Add(entry.Id))
                        {
                            continue;
                        }

                        replacementIds.Add(entry.Id);
                    }

                    if (group.FileIds.SequenceEqual(replacementIds))
                        continue;

                    group.FileIds = replacementIds;
                    changedFileIdsByGroupId.Add(group.Id, replacementIds);
                }

                if (changedFileIdsByGroupId.Count == 0)
                {
                    await CleanupCreatedEntriesAsync(registration.CreatedEntries);
                    return;
                }

                await _groupRepo.ReplaceAllAsync(plannedGroups);
                foreach (var (groupId, replacementIds) in changedFileIdsByGroupId)
                {
                    var groupVm = _host.Groups.FirstOrDefault(group => group.Id == groupId);
                    if (groupVm == null)
                        continue;

                    groupVm.Model.FileIds.Clear();
                    groupVm.Model.FileIds.AddRange(replacementIds);
                    groupVm.NotifyStructureChanged();
                }

                await _fileCatalogService.CompleteRegistrationAsync(registration.CreatedEntries);
            }
            catch (Exception repairException)
            {
                try
                {
                    await CleanupCreatedEntriesAsync(registration.CreatedEntries);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "Dashboard recovery failed and its newly created file metadata could not be cleaned up.",
                        repairException,
                        cleanupException);
                }

                throw;
            }
        });
    }

    public async Task<bool> RemoveFileFromDashboardAsync(LogGroupViewModel groupVm, string fileId)
    {
        return await RemoveFilesFromDashboardAsync(groupVm, new[] { fileId });
    }

    public async Task<bool> RemoveFilesFromDashboardAsync(LogGroupViewModel groupVm, IReadOnlyList<string> fileIds)
    {
        if (!await _dashboardMembershipService.RemoveFilesFromDashboardAsync(groupVm, fileIds))
            return false;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
        return true;
    }

    public async Task<bool> CopyFileToDashboardAsync(LogGroupViewModel targetGroupVm, string fileId)
    {
        if (!await _dashboardMembershipService.CopyFileToDashboardAsync(targetGroupVm, fileId))
            return false;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
        return true;
    }

    public async Task<bool> CopyFilePathToDashboardAsync(LogGroupViewModel targetGroupVm, string filePath)
    {
        if (!await _dashboardMembershipService.CopyFilePathToDashboardAsync(targetGroupVm, filePath))
            return false;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
        return true;
    }

    public async Task<bool> CopyFilesToDashboardAsync(LogGroupViewModel targetGroupVm, IReadOnlyList<string> fileIds)
    {
        if (!await _dashboardMembershipService.CopyFilesToDashboardAsync(targetGroupVm, fileIds))
            return false;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
        return true;
    }

    public async Task<bool> ReorderFilesInDashboardAsync(
        LogGroupViewModel groupVm,
        IReadOnlyList<string> draggedFileIds,
        string targetFileId,
        DropPlacement placement)
    {
        if (!await _dashboardMembershipService.ReorderFilesInDashboardAsync(groupVm, draggedFileIds, targetFileId, placement))
            return false;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
        return true;
    }

    public async Task<bool> MoveFilesBetweenDashboardsAsync(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        IReadOnlyList<string> draggedFileIds,
        string? targetFileId,
        DropPlacement placement)
    {
        if (!await _dashboardMembershipService.MoveFilesBetweenDashboardsAsync(
                sourceGroupVm,
                targetGroupVm,
                draggedFileIds,
                targetFileId,
                placement))
        {
            return false;
        }

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
        return true;
    }

    public bool CanDropDashboardFilesOnFile(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        IReadOnlyList<string> draggedFileIds,
        string targetFileId,
        DropPlacement placement)
    {
        if (!targetGroupVm.CanManageFiles || placement == DropPlacement.Inside)
            return false;

        var draggedFileIdSet = CreateDistinctFileIdSet(draggedFileIds);
        if (draggedFileIdSet.Count == 0)
            return false;

        var isSameDashboard = string.Equals(sourceGroupVm.Id, targetGroupVm.Id, StringComparison.Ordinal);
        if (isSameDashboard)
            return draggedFileIdSet.All(fileId => sourceGroupVm.Model.FileIds.Contains(fileId)) &&
                !draggedFileIdSet.Contains(targetFileId) &&
                WouldReorderFilesChange(sourceGroupVm.Model.FileIds, draggedFileIdSet, targetFileId, placement);

        return draggedFileIdSet.All(fileId => sourceGroupVm.Model.FileIds.Contains(fileId)) &&
            draggedFileIdSet.All(fileId => !targetGroupVm.Model.FileIds.Contains(fileId));
    }

    public bool CanDropDashboardFilesOnGroup(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        IReadOnlyList<string> draggedFileIds)
    {
        if (!targetGroupVm.CanManageFiles)
            return false;

        if (string.Equals(targetGroupVm.Id, sourceGroupVm.Id, StringComparison.Ordinal))
            return false;

        var draggedFileIdSet = CreateDistinctFileIdSet(draggedFileIds);
        return draggedFileIdSet.Count > 0 &&
            draggedFileIdSet.All(fileId => sourceGroupVm.Model.FileIds.Contains(fileId)) &&
            draggedFileIdSet.All(fileId => !targetGroupVm.Model.FileIds.Contains(fileId));
    }

    public Task<bool> ApplyDashboardFilesDropAsync(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        IReadOnlyList<string> draggedFileIds,
        string? targetFileId,
        DropPlacement placement)
    {
        return string.Equals(sourceGroupVm.Id, targetGroupVm.Id, StringComparison.Ordinal)
            ? ReorderFilesInDashboardAsync(targetGroupVm, draggedFileIds, targetFileId!, placement)
            : MoveFilesBetweenDashboardsAsync(sourceGroupVm, targetGroupVm, draggedFileIds, targetFileId, placement);
    }

    private static HashSet<string> CreateDistinctFileIdSet(IEnumerable<string> fileIds)
        => fileIds
            .Where(fileId => !string.IsNullOrWhiteSpace(fileId))
            .ToHashSet(StringComparer.Ordinal);

    private static bool WouldReorderFilesChange(
        IReadOnlyList<string> currentFileIds,
        HashSet<string> draggedFileIdSet,
        string targetFileId,
        DropPlacement placement)
    {
        var movingFileIds = currentFileIds
            .Where(draggedFileIdSet.Contains)
            .ToList();
        if (movingFileIds.Count != draggedFileIdSet.Count)
            return false;

        var nextFileIds = currentFileIds
            .Where(fileId => !draggedFileIdSet.Contains(fileId))
            .ToList();
        var targetIndex = nextFileIds.IndexOf(targetFileId);
        if (targetIndex < 0)
            return false;

        var insertIndex = placement == DropPlacement.After ? targetIndex + 1 : targetIndex;
        nextFileIds.InsertRange(insertIndex, movingFileIds);
        return !currentFileIds.SequenceEqual(nextFileIds);
    }

    public async Task MoveGroupUpAsync(LogGroupViewModel group)
    {
        await _dashboardTreeService.MoveGroupUpAsync(group);
        await _dashboardActivationService.RefreshAllMemberFilesAsync();
    }

    public async Task MoveGroupDownAsync(LogGroupViewModel group)
    {
        await _dashboardTreeService.MoveGroupDownAsync(group);
        await _dashboardActivationService.RefreshAllMemberFilesAsync();
    }

    public bool CanMoveGroupTo(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
        => _dashboardTreeService.CanMoveGroupTo(source, target, placement);

    public async Task MoveGroupToAsync(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
    {
        await _dashboardTreeService.MoveGroupToAsync(source, target, placement);
        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task DuplicateGroupAsync(LogGroupViewModel source)
    {
        if (!await _dashboardTreeService.DuplicateGroupAsync(source))
            return;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public void ApplyDashboardTreeFilter()
        => _dashboardTreeService.ApplyDashboardTreeFilter();

    public HashSet<string> ResolveFileIds(LogGroupViewModel group)
        => _dashboardTreeService.ResolveFileIds(group);

    public void RebuildGroupsCollection(List<LogGroup> allGroups)
        => _dashboardTreeService.RebuildGroupsCollection(allGroups);

    public void DetachGroupViewModels()
        => _dashboardTreeService.DetachGroupViewModels();

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

    private Task CleanupCreatedEntriesAsync(IEnumerable<LogFileEntry> createdEntries)
        => _fileCatalogService.RemoveCreatedEntriesIfUnreferencedAsync(
            createdEntries,
            GetReferencedFileIdsAsync);

    private async Task<IReadOnlySet<string>> GetReferencedFileIdsAsync()
    {
        var referencedIds = (await _groupRepo.GetAllAsync())
            .SelectMany(group => group.FileIds)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var tab in _host.Tabs)
        {
            if (!string.IsNullOrWhiteSpace(tab.FileId))
                referencedIds.Add(tab.FileId);
        }

        return referencedIds;
    }
}
