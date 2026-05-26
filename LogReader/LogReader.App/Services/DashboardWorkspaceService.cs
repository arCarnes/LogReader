namespace LogReader.App.Services;

using LogReader.App.Models;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class DashboardWorkspaceService
{
    private readonly IDashboardWorkspaceHost _host;
    private readonly LogFileCatalogService _fileCatalogService;
    private readonly DashboardImportService _dashboardImportService;
    private readonly DashboardActivationService _dashboardActivationService;
    private readonly DashboardTreeService _dashboardTreeService;
    private readonly DashboardMembershipService _dashboardMembershipService;

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
        _fileCatalogService = fileCatalogService ?? new LogFileCatalogService(fileRepo);
        _dashboardImportService = new DashboardImportService(groupRepo, _fileCatalogService);
        _dashboardActivationService = dashboardActivationService ?? (buildFileExistenceMapAsync == null
            ? new DashboardActivationService(host, fileRepo, groupRepo)
            : new DashboardActivationService(host, fileRepo, groupRepo, buildFileExistenceMapAsync));
        _dashboardTreeService = new DashboardTreeService(
            host,
            groupRepo,
            _dashboardActivationService.LeaveActiveDashboardScope,
            _dashboardActivationService.PruneModifierState);
        _dashboardMembershipService = new DashboardMembershipService(_fileCatalogService, groupRepo);
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
        var result = await _dashboardImportService.ApplyImportedViewAsync(export);
        _dashboardActivationService.LeaveActiveDashboardScope();
        RebuildGroupsCollection(result.Groups.ToList());
        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task ApplyImportedViewAsync(ImportedView importedView)
    {
        ArgumentNullException.ThrowIfNull(importedView);

        _dashboardActivationService.CancelDashboardLoad();
        var result = await _dashboardImportService.ApplyImportedViewAsync(importedView);
        _dashboardActivationService.LeaveActiveDashboardScope();
        RebuildGroupsCollection(result.Groups.ToList());
        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task AddFilesToDashboardAsync(LogGroupViewModel groupVm, IReadOnlyList<string> filePaths)
    {
        if (!await _dashboardMembershipService.AddFilesToDashboardAsync(groupVm, filePaths))
            return;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task RemoveFileFromDashboardAsync(LogGroupViewModel groupVm, string fileId)
    {
        if (!await _dashboardMembershipService.RemoveFileFromDashboardAsync(groupVm, fileId))
            return;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task RemoveFilesFromDashboardAsync(LogGroupViewModel groupVm, IReadOnlyList<string> fileIds)
    {
        if (!await _dashboardMembershipService.RemoveFilesFromDashboardAsync(groupVm, fileIds))
            return;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task CopyFileToDashboardAsync(LogGroupViewModel targetGroupVm, string fileId)
    {
        if (!await _dashboardMembershipService.CopyFileToDashboardAsync(targetGroupVm, fileId))
            return;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task CopyFilePathToDashboardAsync(LogGroupViewModel targetGroupVm, string filePath)
    {
        if (!await _dashboardMembershipService.CopyFilePathToDashboardAsync(targetGroupVm, filePath))
            return;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task CopyFilesToDashboardAsync(LogGroupViewModel targetGroupVm, IReadOnlyList<string> fileIds)
    {
        if (!await _dashboardMembershipService.CopyFilesToDashboardAsync(targetGroupVm, fileIds))
            return;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task ReorderFileInDashboardAsync(
        LogGroupViewModel groupVm,
        string draggedFileId,
        string targetFileId,
        DropPlacement placement)
    {
        await ReorderFilesInDashboardAsync(groupVm, new[] { draggedFileId }, targetFileId, placement);
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

    public async Task MoveFileBetweenDashboardsAsync(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId,
        string? targetFileId,
        DropPlacement placement)
    {
        await MoveFilesBetweenDashboardsAsync(
            sourceGroupVm,
            targetGroupVm,
            new[] { draggedFileId },
            targetFileId,
            placement);
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

    public bool CanDropDashboardFileOnFile(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId,
        string targetFileId,
        DropPlacement placement)
        => CanDropDashboardFilesOnFile(sourceGroupVm, targetGroupVm, new[] { draggedFileId }, targetFileId, placement);

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

    public bool CanDropDashboardFileOnGroup(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId)
        => CanDropDashboardFilesOnGroup(sourceGroupVm, targetGroupVm, new[] { draggedFileId });

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

    public Task<bool> ApplyDashboardFileDropAsync(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId,
        string? targetFileId,
        DropPlacement placement)
        => ApplyDashboardFilesDropAsync(sourceGroupVm, targetGroupVm, new[] { draggedFileId }, targetFileId, placement);

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
}
