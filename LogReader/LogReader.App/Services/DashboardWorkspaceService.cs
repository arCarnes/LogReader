namespace LogReader.App.Services;

using System.IO;
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
        _groupRepo = groupRepo;
        _fileCatalogService = fileCatalogService ?? new LogFileCatalogService(fileRepo);
        _dashboardImportService = new DashboardImportService(groupRepo, _fileCatalogService);
        _dashboardActivationService = buildFileExistenceMapAsync == null
            ? new DashboardActivationService(host, fileRepo, groupRepo)
            : new DashboardActivationService(host, fileRepo, groupRepo, buildFileExistenceMapAsync);
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
        var rootCount = _host.Groups.Count(g => g.Model.ParentGroupId == null);
        var group = new LogGroup
        {
            Name = kind == LogGroupKind.Branch ? "New Folder" : "New Dashboard",
            Kind = kind,
            SortOrder = rootCount
        };
        await _groupRepo.AddAsync(group);
        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);
        var vm = _host.Groups.FirstOrDefault(g => g.Id == group.Id);
        if (vm != null)
            vm.IsExpanded = true;
    }

    public async Task<bool> CreateChildGroupAsync(LogGroupViewModel parent, LogGroupKind kind = LogGroupKind.Dashboard)
    {
        if (parent.Kind != LogGroupKind.Branch)
            return false;

        var siblingCount = _host.Groups.Count(g => g.Model.ParentGroupId == parent.Id);
        var group = new LogGroup
        {
            Name = kind == LogGroupKind.Branch ? "New Folder" : "New Dashboard",
            Kind = kind,
            ParentGroupId = parent.Id,
            SortOrder = siblingCount
        };
        await _groupRepo.AddAsync(group);
        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);

        var parentVm = _host.Groups.FirstOrDefault(g => g.Id == parent.Id);
        if (parentVm != null)
            parentVm.IsExpanded = true;

        var childVm = _host.Groups.FirstOrDefault(g => g.Id == group.Id);
        if (childVm != null)
            childVm.IsExpanded = true;

        await RefreshAllMemberFilesAsync();
        return true;
    }

    public async Task DeleteGroupAsync(LogGroupViewModel? groupVm)
    {
        if (groupVm == null)
            return;

        if (!string.IsNullOrEmpty(_host.ActiveDashboardId))
        {
            var active = _host.Groups.FirstOrDefault(g => g.Id == _host.ActiveDashboardId);
            if (active != null && (active.Id == groupVm.Id || IsDescendantOf(active, groupVm.Id)))
                _dashboardActivationService.LeaveActiveDashboardScope();
        }

        await _groupRepo.DeleteAsync(groupVm.Id);
        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);
        _host.NotifyFilteredTabsChanged();
    }

    public Task ExportViewAsync(string exportPath)
        => _dashboardImportService.ExportViewAsync(exportPath);

    public Task<ViewExport?> ImportViewAsync(string importPath)
        => _dashboardImportService.ImportViewAsync(importPath);

    public async Task ApplyImportedViewAsync(ViewExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        var result = await _dashboardImportService.ApplyImportedViewAsync(export);
        _dashboardActivationService.LeaveActiveDashboardScope();
        RebuildGroupsCollection(result.Groups.ToList());
        await RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task AddFilesToDashboardAsync(LogGroupViewModel groupVm, IReadOnlyList<string> filePaths)
    {
        if (!groupVm.CanManageFiles)
            return;

        var parsedPaths = DistinctLiteralFilePaths(filePaths)
            .OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance)
            .ToList();
        if (parsedPaths.Count == 0)
            return;

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
            return;

        groupVm.NotifyStructureChanged();
        await _groupRepo.UpdateAsync(groupVm.Model);
        await RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
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

    public async Task RemoveFileFromDashboardAsync(LogGroupViewModel groupVm, string fileId)
    {
        if (!groupVm.CanManageFiles)
            return;

        if (!groupVm.Model.FileIds.Remove(fileId))
            return;

        groupVm.NotifyStructureChanged();
        await _groupRepo.UpdateAsync(groupVm.Model);
        await RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task MoveGroupUpAsync(LogGroupViewModel group)
    {
        var siblings = GetSiblings(group);
        var idx = siblings.IndexOf(group);
        if (idx <= 0)
            return;

        var prev = siblings[idx - 1];
        (group.Model.SortOrder, prev.Model.SortOrder) = (prev.Model.SortOrder, group.Model.SortOrder);
        await _groupRepo.UpdateAsync(group.Model);
        await _groupRepo.UpdateAsync(prev.Model);

        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);
        await RefreshAllMemberFilesAsync();
    }

    public async Task MoveGroupDownAsync(LogGroupViewModel group)
    {
        var siblings = GetSiblings(group);
        var idx = siblings.IndexOf(group);
        if (idx < 0 || idx >= siblings.Count - 1)
            return;

        var next = siblings[idx + 1];
        (group.Model.SortOrder, next.Model.SortOrder) = (next.Model.SortOrder, group.Model.SortOrder);
        await _groupRepo.UpdateAsync(group.Model);
        await _groupRepo.UpdateAsync(next.Model);

        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);
        await RefreshAllMemberFilesAsync();
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

    public bool CanMoveGroupTo(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
    {
        if (source.Id == target.Id)
            return false;

        if (placement == DropPlacement.Inside && target.Kind != LogGroupKind.Branch)
            return false;

        var current = target.Parent;
        while (current != null)
        {
            if (current.Id == source.Id)
                return false;

            current = current.Parent;
        }

        var newParentId = placement == DropPlacement.Inside
            ? target.Id
            : target.Model.ParentGroupId;
        if (source.Model.ParentGroupId == newParentId)
        {
            var siblings = _host.Groups
                .Where(g => g.Model.ParentGroupId == newParentId && g.Depth == source.Depth)
                .ToList();
            var srcIdx = siblings.IndexOf(source);
            var tgtIdx = siblings.IndexOf(target);
            if (srcIdx >= 0 && tgtIdx >= 0)
            {
                if (placement == DropPlacement.Before && (tgtIdx == srcIdx + 1 || tgtIdx == srcIdx))
                    return false;
                if (placement == DropPlacement.After && (tgtIdx == srcIdx - 1 || tgtIdx == srcIdx))
                    return false;
            }
        }

        return true;
    }

    public async Task MoveGroupToAsync(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
    {
        if (!CanMoveGroupTo(source, target, placement))
            return;

        var allModels = await _groupRepo.GetAllAsync();
        var sourceModel = allModels.First(g => g.Id == source.Id);
        var targetModel = allModels.First(g => g.Id == target.Id);

        var oldParentId = sourceModel.ParentGroupId;
        var newParentId = placement == DropPlacement.Inside
            ? targetModel.Id
            : targetModel.ParentGroupId;

        var newSiblings = allModels
            .Where(g => g.ParentGroupId == newParentId && g.Id != sourceModel.Id)
            .OrderBy(g => g.SortOrder)
            .ToList();

        int insertIndex;
        if (placement == DropPlacement.Inside)
        {
            insertIndex = newSiblings.Count;
        }
        else
        {
            var targetIndex = newSiblings.FindIndex(g => g.Id == targetModel.Id);
            if (targetIndex < 0)
                targetIndex = newSiblings.Count;

            insertIndex = placement == DropPlacement.Before ? targetIndex : targetIndex + 1;
        }

        sourceModel.ParentGroupId = newParentId;

        newSiblings.Insert(insertIndex, sourceModel);
        for (var i = 0; i < newSiblings.Count; i++)
            newSiblings[i].SortOrder = i;

        if (oldParentId != newParentId)
        {
            var oldSiblings = allModels
                .Where(g => g.ParentGroupId == oldParentId && g.Id != sourceModel.Id)
                .OrderBy(g => g.SortOrder)
                .ToList();
            for (var i = 0; i < oldSiblings.Count; i++)
                oldSiblings[i].SortOrder = i;

            foreach (var sibling in oldSiblings)
                await _groupRepo.UpdateAsync(sibling);
        }

        foreach (var sibling in newSiblings)
            await _groupRepo.UpdateAsync(sibling);

        var refreshed = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(refreshed);

        if (placement == DropPlacement.Inside)
        {
            var targetVm = _host.Groups.FirstOrDefault(g => g.Id == target.Id);
            if (targetVm != null)
                targetVm.IsExpanded = true;
        }

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

    public void UpdateSelectedMemberFileHighlights(string? selectedFileId)
        => _dashboardActivationService.UpdateSelectedMemberFileHighlights(selectedFileId);

    public void ApplyDashboardTreeFilter()
    {
        var filter = _host.DashboardTreeFilter?.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            foreach (var group in _host.Groups)
                group.IsFilterVisible = true;
            return;
        }

        foreach (var root in _host.Groups.Where(g => g.Parent == null))
            ApplyDashboardTreeFilterRecursive(root, filter);
    }

    public HashSet<string> ResolveFileIds(LogGroupViewModel group)
    {
        var result = new HashSet<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<LogGroupViewModel>();
        stack.Push(group);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current.Id))
                continue;

            foreach (var id in current.Model.FileIds)
                result.Add(id);

            foreach (var child in _host.Groups.Where(g => g.Model.ParentGroupId == current.Id))
                stack.Push(child);
        }

        return result;
    }

    public void RebuildGroupsCollection(List<LogGroup> allGroups)
    {
        var expandedById = _host.Groups.ToDictionary(g => g.Id, g => g.IsExpanded);
        DetachGroupViewModels();
        _host.Groups.Clear();
        var roots = allGroups
            .Where(g => g.ParentGroupId == null)
            .OrderBy(g => g.SortOrder);
        var visitedGroupIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in roots)
            AddGroupToTree(root, null, 0, allGroups, expandedById, visitedGroupIds);

        if (!string.IsNullOrEmpty(_host.ActiveDashboardId))
        {
            var active = _host.Groups.FirstOrDefault(g => g.Id == _host.ActiveDashboardId && g.Kind == LogGroupKind.Dashboard);
            if (active == null)
                _dashboardActivationService.LeaveActiveDashboardScope();
            else
                active.IsSelected = true;
        }

        _dashboardActivationService.PruneModifierState();
        ApplyDashboardTreeFilter();
    }

    private void AddGroupToTree(
        LogGroup model,
        LogGroupViewModel? parent,
        int depth,
        List<LogGroup> allGroups,
        IReadOnlyDictionary<string, bool> expandedById,
        HashSet<string> visitedGroupIds)
    {
        if (!visitedGroupIds.Add(model.Id))
            return;

        var vm = WrapGroup(model);
        vm.Depth = depth;
        vm.Parent = parent;
        if (expandedById.TryGetValue(model.Id, out var wasExpanded))
            vm.IsExpanded = wasExpanded;
        parent?.AddChild(vm);
        _host.Groups.Add(vm);

        var children = allGroups
            .Where(g => g.ParentGroupId == model.Id)
            .OrderBy(g => g.SortOrder);
        foreach (var child in children)
            AddGroupToTree(child, vm, depth + 1, allGroups, expandedById, visitedGroupIds);
    }

    private LogGroupViewModel WrapGroup(LogGroup model)
    {
        var vm = new LogGroupViewModel(model, async group => await _groupRepo.UpdateAsync(group));
        vm.PropertyChanged += GroupVm_PropertyChanged;
        return vm;
    }

    public void DetachGroupViewModels()
    {
        foreach (var group in _host.Groups)
        {
            group.PropertyChanged -= GroupVm_PropertyChanged;
            group.Parent = null;
            group.Children.Clear();
        }
    }

    private List<LogGroupViewModel> GetSiblings(LogGroupViewModel group)
    {
        return _host.Groups
            .Where(g => g.Model.ParentGroupId == group.Model.ParentGroupId && g.Depth == group.Depth)
            .ToList();
    }

    private bool IsDescendantOf(LogGroupViewModel group, string ancestorId)
    {
        var current = group.Parent;
        while (current != null)
        {
            if (current.Id == ancestorId)
                return true;

            current = current.Parent;
        }

        return false;
    }

    private void GroupVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LogGroupViewModel.Name) or nameof(LogGroupViewModel.DisplayName))
        {
            ApplyDashboardTreeFilter();
            _host.NotifyScopeMetadataChanged();
        }
    }

    private static bool ApplyDashboardTreeFilterRecursive(LogGroupViewModel node, string filter)
    {
        var selfMatch = node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var descendantMatch = false;
        foreach (var child in node.Children)
            descendantMatch |= ApplyDashboardTreeFilterRecursive(child, filter);

        node.IsFilterVisible = selfMatch || descendantMatch;
        if (descendantMatch && !node.IsExpanded)
            node.IsExpanded = true;

        return node.IsFilterVisible;
    }
}

internal sealed record BulkFilePreviewItem(string FilePath, bool IsFound);

internal sealed record BulkFilePreview(
    IReadOnlyList<string> ParsedPaths,
    IReadOnlyList<BulkFilePreviewItem> Items)
{
    public int FoundCount => Items.Count(item => item.IsFound);

    public int MissingCount => Items.Count - FoundCount;
}
