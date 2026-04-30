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

    public async Task ReorderFileInDashboardAsync(
        LogGroupViewModel groupVm,
        string draggedFileId,
        string targetFileId,
        DropPlacement placement)
    {
        if (!await _dashboardMembershipService.ReorderFileInDashboardAsync(groupVm, draggedFileId, targetFileId, placement))
            return;

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task MoveFileBetweenDashboardsAsync(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId,
        string? targetFileId,
        DropPlacement placement)
    {
        if (!await _dashboardMembershipService.MoveFileBetweenDashboardsAsync(
                sourceGroupVm,
                targetGroupVm,
                draggedFileId,
                targetFileId,
                placement))
        {
            return;
        }

        await _dashboardActivationService.RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public bool CanDropDashboardFileOnFile(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId,
        string targetFileId,
        DropPlacement placement)
    {
        if (!targetGroupVm.CanManageFiles || placement == DropPlacement.Inside)
            return false;

        var isSameDashboard = string.Equals(sourceGroupVm.Id, targetGroupVm.Id, StringComparison.Ordinal);
        if (isSameDashboard)
            return !string.Equals(draggedFileId, targetFileId, StringComparison.Ordinal);

        return !targetGroupVm.Model.FileIds.Contains(draggedFileId);
    }

    public bool CanDropDashboardFileOnGroup(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId)
    {
        if (!targetGroupVm.CanManageFiles)
            return false;

        if (string.Equals(targetGroupVm.Id, sourceGroupVm.Id, StringComparison.Ordinal))
            return false;

        return !targetGroupVm.Model.FileIds.Contains(draggedFileId);
    }

    public Task ApplyDashboardFileDropAsync(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId,
        string? targetFileId,
        DropPlacement placement)
    {
        return string.Equals(sourceGroupVm.Id, targetGroupVm.Id, StringComparison.Ordinal)
            ? ReorderFileInDashboardAsync(targetGroupVm, draggedFileId, targetFileId!, placement)
            : MoveFileBetweenDashboardsAsync(sourceGroupVm, targetGroupVm, draggedFileId, targetFileId, placement);
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

    public void ApplyDashboardTreeFilter()
        => _dashboardTreeService.ApplyDashboardTreeFilter();

    public HashSet<string> ResolveFileIds(LogGroupViewModel group)
        => _dashboardTreeService.ResolveFileIds(group);

    public void RebuildGroupsCollection(List<LogGroup> allGroups)
        => _dashboardTreeService.RebuildGroupsCollection(allGroups);

    public void DetachGroupViewModels()
        => _dashboardTreeService.DetachGroupViewModels();
}
