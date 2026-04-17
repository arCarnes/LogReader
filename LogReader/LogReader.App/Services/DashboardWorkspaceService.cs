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
        Func<IReadOnlyDictionary<string, string>, Task<Dictionary<string, bool>>>? buildFileExistenceMapAsync)
    {
        _host = host;
        _fileCatalogService = fileCatalogService ?? new LogFileCatalogService(fileRepo);
        _dashboardImportService = new DashboardImportService(groupRepo, _fileCatalogService);
        _dashboardActivationService = buildFileExistenceMapAsync == null
            ? new DashboardActivationService(host, fileRepo, groupRepo)
            : new DashboardActivationService(host, fileRepo, groupRepo, buildFileExistenceMapAsync);
        _dashboardTreeService = new DashboardTreeService(
            host,
            groupRepo,
            _dashboardActivationService.LeaveActiveDashboardScope,
            _dashboardActivationService.PruneModifierState);
        _dashboardMembershipService = new DashboardMembershipService(_fileCatalogService, groupRepo);
    }

    public bool HasActiveModifiers => _dashboardActivationService.HasActiveModifiers;

    public bool HasDashboardModifier(string dashboardId)
        => _dashboardActivationService.HasDashboardModifier(dashboardId);

    public bool HasAdHocModifier()
        => _dashboardActivationService.HasAdHocModifier();

    public string? GetDashboardModifierLabel(string dashboardId)
        => _dashboardActivationService.GetDashboardModifierLabel(dashboardId);

    public string? GetAdHocModifierLabel()
        => _dashboardActivationService.GetAdHocModifierLabel();

    public bool TryGetDashboardEffectivePaths(string dashboardId, out IReadOnlySet<string> effectivePaths)
        => _dashboardActivationService.TryGetDashboardEffectivePaths(dashboardId, out effectivePaths);

    public bool TryGetAdHocEffectivePaths(out IReadOnlySet<string> effectivePaths)
        => _dashboardActivationService.TryGetAdHocEffectivePaths(out effectivePaths);

    public bool IsManagedByActiveModifier(string filePath)
        => _dashboardActivationService.IsManagedByActiveModifier(filePath);

    public string? FindDashboardForModifierPath(string filePath)
        => _dashboardActivationService.FindDashboardForModifierPath(filePath);

    public bool IsAdHocModifierPath(string filePath)
        => _dashboardActivationService.IsAdHocModifierPath(filePath);

    public IReadOnlyList<string> GetAdHocBasePathsSnapshot()
        => _dashboardActivationService.GetAdHocBasePathsSnapshot();

    public Task SetDashboardModifierAsync(LogGroupViewModel group, int daysBack, IReadOnlyList<ReplacementPattern> patterns)
        => _dashboardActivationService.SetDashboardModifierAsync(group, daysBack, patterns);

    public Task ClearDashboardModifierAsync(LogGroupViewModel group)
        => _dashboardActivationService.ClearDashboardModifierAsync(group);

    public Task SetAdHocModifierAsync(int daysBack, IReadOnlyList<ReplacementPattern> patterns)
        => _dashboardActivationService.SetAdHocModifierAsync(daysBack, patterns);

    public Task ClearAdHocModifierAsync()
        => _dashboardActivationService.ClearAdHocModifierAsync();

    public async Task CreateGroupAsync(LogGroupKind kind)
    {
        await _dashboardTreeService.CreateGroupAsync(kind);
        await RefreshAllMemberFilesAsync();
    }

    public async Task<bool> CreateChildGroupAsync(LogGroupViewModel parent, LogGroupKind kind = LogGroupKind.Dashboard)
    {
        var created = await _dashboardTreeService.CreateChildGroupAsync(parent, kind);
        if (created)
            await RefreshAllMemberFilesAsync();

        return created;
    }

    public async Task DeleteGroupAsync(LogGroupViewModel? groupVm)
    {
        await _dashboardTreeService.DeleteGroupAsync(groupVm);
        await RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public Task ExportViewAsync(string exportPath)
        => _dashboardImportService.ExportViewAsync(exportPath);

    public Task<ViewExport?> ImportViewAsync(string importPath)
        => _dashboardImportService.ImportViewAsync(importPath);

    public async Task ApplyImportedViewAsync(ViewExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        _dashboardActivationService.CancelDashboardLoad();
        var result = await _dashboardImportService.ApplyImportedViewAsync(export);
        _dashboardActivationService.LeaveActiveDashboardScope();
        RebuildGroupsCollection(result.Groups.ToList());
        await RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task AddFilesToDashboardAsync(LogGroupViewModel groupVm, IReadOnlyList<string> filePaths)
    {
        if (!await _dashboardMembershipService.AddFilesToDashboardAsync(groupVm, filePaths))
            return;

        await RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    internal static IReadOnlyList<string> ParseBulkFilePaths(string? rawInput)
        => DashboardMembershipService.ParseBulkFilePaths(rawInput);

    internal static BulkFilePreview BuildBulkFilePreview(
        string? rawInput,
        Func<string, bool>? fileExists = null)
        => DashboardMembershipService.BuildBulkFilePreview(rawInput, fileExists);

    public async Task RemoveFileFromDashboardAsync(LogGroupViewModel groupVm, string fileId)
    {
        if (!await _dashboardMembershipService.RemoveFileFromDashboardAsync(groupVm, fileId))
            return;

        await RefreshAllMemberFilesAsync();
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

        await RefreshAllMemberFilesAsync();
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

        await RefreshAllMemberFilesAsync();
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
        await RefreshAllMemberFilesAsync();
    }

    public async Task MoveGroupDownAsync(LogGroupViewModel group)
    {
        await _dashboardTreeService.MoveGroupDownAsync(group);
        await RefreshAllMemberFilesAsync();
    }

    public bool CanMoveGroupTo(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
        => _dashboardTreeService.CanMoveGroupTo(source, target, placement);

    public async Task MoveGroupToAsync(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
    {
        await _dashboardTreeService.MoveGroupToAsync(source, target, placement);
        await RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public void CancelDashboardLoad()
        => _dashboardActivationService.CancelDashboardLoad();

    public Task OpenGroupFilesAsync(LogGroupViewModel group)
        => _dashboardActivationService.OpenGroupFilesAsync(group);

    public Task<IReadOnlyList<string>> GetGroupFilePathsAsync(string groupId)
        => _dashboardActivationService.GetGroupFilePathsAsync(groupId);

    public Task RefreshAllMemberFilesAsync()
        => _dashboardActivationService.RefreshAllMemberFilesAsync();

    public Task RefreshMemberFilesForFileIdsAsync(IReadOnlyDictionary<string, string> changedFilePathsById)
        => _dashboardActivationService.RefreshMemberFilesForFileIdsAsync(changedFilePathsById);

    public void UpdateSelectedMemberFileHighlights()
        => _dashboardActivationService.UpdateSelectedMemberFileHighlights();

    public void ApplyDashboardTreeFilter()
        => _dashboardTreeService.ApplyDashboardTreeFilter();

    public HashSet<string> ResolveFileIds(LogGroupViewModel group)
        => _dashboardTreeService.ResolveFileIds(group);

    public void RebuildGroupsCollection(List<LogGroup> allGroups)
        => _dashboardTreeService.RebuildGroupsCollection(allGroups);

    public void DetachGroupViewModels()
        => _dashboardTreeService.DetachGroupViewModels();
}

internal enum BulkFilePreviewItemStatus
{
    Found,
    Missing,
    NoMatches
}

internal sealed record BulkFilePreviewItem(string FilePath, BulkFilePreviewItemStatus Status)
{
    public bool IsFound => Status == BulkFilePreviewItemStatus.Found;
}

internal sealed record BulkFilePreview(
    IReadOnlyList<string> ParsedPaths,
    IReadOnlyList<BulkFilePreviewItem> Items)
{
    public int FoundCount => Items.Count(item => item.IsFound);

    public int MissingCount => Items.Count - FoundCount;
}
