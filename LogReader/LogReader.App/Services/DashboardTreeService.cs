namespace LogReader.App.Services;

using LogReader.App.Models;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class DashboardTreeService
{
    private readonly IDashboardWorkspaceHost _host;
    private readonly ILogGroupRepository _groupRepo;
    private readonly DashboardMutationCoordinator _mutationCoordinator;
    private readonly Action _leaveActiveDashboardScope;
    private readonly Action _pruneModifierState;
    private Dictionary<string, bool>? _filterExpansionStateById;

    public DashboardTreeService(
        IDashboardWorkspaceHost host,
        ILogGroupRepository groupRepo,
        DashboardMutationCoordinator mutationCoordinator,
        Action leaveActiveDashboardScope,
        Action pruneModifierState)
    {
        _host = host;
        _groupRepo = groupRepo;
        _mutationCoordinator = mutationCoordinator;
        _leaveActiveDashboardScope = leaveActiveDashboardScope;
        _pruneModifierState = pruneModifierState;
    }

    public async Task CreateGroupAsync(LogGroupKind kind)
    {
        await _mutationCoordinator.ExecuteAsync(async () =>
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
        });
    }

    public async Task<bool> CreateChildGroupAsync(LogGroupViewModel parent, LogGroupKind kind = LogGroupKind.Dashboard)
    {
        var parentId = parent.Id;
        return await _mutationCoordinator.ExecuteAsync(async () =>
        {
            var currentParent = ResolveCurrentGroup(parentId);
            if (currentParent?.Kind != LogGroupKind.Branch)
                return false;

            var siblingCount = _host.Groups.Count(g => g.Model.ParentGroupId == parentId);
            var group = new LogGroup
            {
                Name = kind == LogGroupKind.Branch ? "New Folder" : "New Dashboard",
                Kind = kind,
                ParentGroupId = parentId,
                SortOrder = siblingCount
            };

            await _groupRepo.AddAsync(group);
            var allGroups = await _groupRepo.GetAllAsync();
            RebuildGroupsCollection(allGroups);

            var parentVm = ResolveCurrentGroup(parentId);
            if (parentVm != null)
                parentVm.IsExpanded = true;

            var childVm = ResolveCurrentGroup(group.Id);
            if (childVm != null)
                childVm.IsExpanded = true;

            return true;
        });
    }

    public async Task DeleteGroupAsync(LogGroupViewModel? groupVm)
    {
        if (groupVm == null)
            return;

        var groupId = groupVm.Id;
        await _mutationCoordinator.ExecuteAsync(async () =>
        {
            var currentGroup = ResolveCurrentGroup(groupId);
            if (currentGroup == null)
                return;

            var leavesActiveScope = false;
            if (!string.IsNullOrEmpty(_host.ActiveDashboardId))
            {
                var active = ResolveCurrentGroup(_host.ActiveDashboardId);
                leavesActiveScope = active != null && (active.Id == groupId || IsDescendantOf(active, groupId));
            }

            await _groupRepo.DeleteAsync(groupId);
            if (leavesActiveScope)
                _leaveActiveDashboardScope();

            var allGroups = await _groupRepo.GetAllAsync();
            RebuildGroupsCollection(allGroups);
        });
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
        var sourceId = source.Id;
        var targetId = target.Id;
        await _mutationCoordinator.ExecuteAsync(async () =>
        {
            var currentSource = ResolveCurrentGroup(sourceId);
            var currentTarget = ResolveCurrentGroup(targetId);
            if (currentSource == null || currentTarget == null || !CanMoveGroupTo(currentSource, currentTarget, placement))
                return;

            var allModels = (await _groupRepo.GetAllAsync()).Select(CloneGroup).ToList();
            var sourceModel = allModels.FirstOrDefault(g => g.Id == sourceId);
            var targetModel = allModels.FirstOrDefault(g => g.Id == targetId);
            if (sourceModel == null || targetModel == null)
                return;

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
            }

            await _groupRepo.ReplaceAllAsync(allModels);
            RebuildGroupsCollection(allModels);

            if (placement == DropPlacement.Inside)
            {
                var targetVm = ResolveCurrentGroup(targetId);
                if (targetVm != null)
                    targetVm.IsExpanded = true;
            }
        });
    }

    public async Task<bool> DuplicateGroupAsync(LogGroupViewModel source)
    {
        var sourceId = source.Id;
        return await _mutationCoordinator.ExecuteAsync(async () =>
        {
            if (ResolveCurrentGroup(sourceId) == null)
                return false;

            var allModels = (await _groupRepo.GetAllAsync()).Select(CloneGroup).ToList();
            var sourceModel = allModels.FirstOrDefault(group => group.Id == sourceId);
            if (sourceModel == null)
                return false;

            var childrenByParentId = BuildChildrenByParentId(allModels);
            var siblingNames = allModels
                .Where(group => group.ParentGroupId == sourceModel.ParentGroupId && group.Id != sourceModel.Id)
                .Select(group => group.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var clonedModels = new List<LogGroup>();
            var visitedGroupIds = new HashSet<string>(StringComparer.Ordinal);
            var clonedRoot = CloneGroupSubtree(
                sourceModel,
                parentGroupId: sourceModel.ParentGroupId,
                childrenByParentId,
                clonedModels,
                visitedGroupIds,
                rootName: CreateCopyName(sourceModel.Name, siblingNames));
            if (clonedRoot == null)
                return false;

            allModels.AddRange(clonedModels);
            ReorderSiblingsAfterDuplicate(allModels, sourceModel, clonedRoot);

            await _groupRepo.ReplaceAllAsync(allModels);
            RebuildGroupsCollection(allModels);
            return true;
        });
    }

    public async Task MoveGroupUpAsync(LogGroupViewModel group)
    {
        var groupId = group.Id;
        await _mutationCoordinator.ExecuteAsync(async () =>
        {
            var currentGroup = ResolveCurrentGroup(groupId);
            if (currentGroup == null)
                return;

            var siblings = GetSiblings(currentGroup);
            var idx = siblings.IndexOf(currentGroup);
            if (idx <= 0)
                return;

            var previousId = siblings[idx - 1].Id;
            var allModels = (await _groupRepo.GetAllAsync()).Select(CloneGroup).ToList();
            var groupModel = allModels.FirstOrDefault(model => model.Id == groupId);
            var previousModel = allModels.FirstOrDefault(model => model.Id == previousId);
            if (groupModel == null || previousModel == null)
                return;

            (groupModel.SortOrder, previousModel.SortOrder) = (previousModel.SortOrder, groupModel.SortOrder);
            await _groupRepo.ReplaceAllAsync(allModels);
            RebuildGroupsCollection(allModels);
        });
    }

    public async Task MoveGroupDownAsync(LogGroupViewModel group)
    {
        var groupId = group.Id;
        await _mutationCoordinator.ExecuteAsync(async () =>
        {
            var currentGroup = ResolveCurrentGroup(groupId);
            if (currentGroup == null)
                return;

            var siblings = GetSiblings(currentGroup);
            var idx = siblings.IndexOf(currentGroup);
            if (idx < 0 || idx >= siblings.Count - 1)
                return;

            var nextId = siblings[idx + 1].Id;
            var allModels = (await _groupRepo.GetAllAsync()).Select(CloneGroup).ToList();
            var groupModel = allModels.FirstOrDefault(model => model.Id == groupId);
            var nextModel = allModels.FirstOrDefault(model => model.Id == nextId);
            if (groupModel == null || nextModel == null)
                return;

            (groupModel.SortOrder, nextModel.SortOrder) = (nextModel.SortOrder, groupModel.SortOrder);
            await _groupRepo.ReplaceAllAsync(allModels);
            RebuildGroupsCollection(allModels);
        });
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
        _filterExpansionStateById = null;
        var expandedById = _host.Groups.ToDictionary(g => g.Id, g => g.IsExpanded, StringComparer.Ordinal);
        DetachGroupViewModels();
        _host.Groups.Clear();
        var childrenByParentId = BuildChildrenByParentId(allGroups);
        var roots = allGroups
            .Where(g => g.ParentGroupId == null)
            .OrderBy(g => g.SortOrder);
        var visitedGroupIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in roots)
            AddGroupToTree(root, null, 0, childrenByParentId, expandedById, visitedGroupIds);

        if (!string.IsNullOrEmpty(_host.ActiveDashboardId))
        {
            var active = _host.Groups.FirstOrDefault(g => g.Id == _host.ActiveDashboardId && g.Kind == LogGroupKind.Dashboard);
            if (active == null)
                _leaveActiveDashboardScope();
            else
                active.IsSelected = true;
        }

        _pruneModifierState();
        ApplyDashboardTreeFilter();
    }

    public void ApplyDashboardTreeFilter()
    {
        var filter = _host.DashboardTreeFilter?.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            RestoreFilterExpansionState();
            foreach (var group in _host.Groups)
                group.IsFilterVisible = true;
            return;
        }

        CaptureFilterExpansionStateIfNeeded();

        foreach (var root in _host.Groups.Where(g => g.Parent == null))
            ApplyDashboardTreeFilterRecursive(root, filter);
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

    private void AddGroupToTree(
        LogGroup model,
        LogGroupViewModel? parent,
        int depth,
        IReadOnlyDictionary<string, List<LogGroup>> childrenByParentId,
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

        if (!childrenByParentId.TryGetValue(model.Id, out var children))
            return;

        foreach (var child in children)
            AddGroupToTree(child, vm, depth + 1, childrenByParentId, expandedById, visitedGroupIds);
    }

    private static LogGroup? CloneGroupSubtree(
        LogGroup source,
        string? parentGroupId,
        IReadOnlyDictionary<string, List<LogGroup>> childrenByParentId,
        List<LogGroup> clonedModels,
        HashSet<string> visitedGroupIds,
        string? rootName = null)
    {
        if (!visitedGroupIds.Add(source.Id))
            return null;

        var clone = new LogGroup
        {
            Id = Guid.NewGuid().ToString(),
            Name = rootName ?? source.Name,
            Kind = source.Kind,
            ParentGroupId = parentGroupId,
            SortOrder = source.SortOrder,
            FileIds = source.FileIds.ToList()
        };
        clonedModels.Add(clone);

        if (!childrenByParentId.TryGetValue(source.Id, out var children))
            return clone;

        foreach (var child in children)
            _ = CloneGroupSubtree(child, clone.Id, childrenByParentId, clonedModels, visitedGroupIds);

        return clone;
    }

    private static void ReorderSiblingsAfterDuplicate(List<LogGroup> allModels, LogGroup sourceModel, LogGroup clonedRoot)
    {
        var siblings = allModels
            .Where(group => group.ParentGroupId == sourceModel.ParentGroupId && group.Id != clonedRoot.Id)
            .OrderBy(group => group.SortOrder)
            .ToList();
        var sourceIndex = siblings.FindIndex(group => group.Id == sourceModel.Id);
        var insertIndex = sourceIndex < 0 ? siblings.Count : sourceIndex + 1;
        siblings.Insert(insertIndex, clonedRoot);
        for (var i = 0; i < siblings.Count; i++)
            siblings[i].SortOrder = i;
    }

    private static string CreateCopyName(string sourceName, HashSet<string> siblingNames)
    {
        var baseName = $"{sourceName} Copy";
        if (siblingNames.Add(baseName))
            return baseName;

        var suffix = 2;
        while (true)
        {
            var candidate = $"{baseName} {suffix}";
            if (siblingNames.Add(candidate))
                return candidate;

            suffix++;
        }
    }

    private static IReadOnlyDictionary<string, List<LogGroup>> BuildChildrenByParentId(IEnumerable<LogGroup> allGroups)
    {
        var childrenByParentId = new Dictionary<string, List<LogGroup>>(StringComparer.Ordinal);
        foreach (var group in allGroups)
        {
            if (group.ParentGroupId == null)
                continue;

            if (!childrenByParentId.TryGetValue(group.ParentGroupId, out var children))
            {
                children = new List<LogGroup>();
                childrenByParentId.Add(group.ParentGroupId, children);
            }

            children.Add(group);
        }

        foreach (var parentId in childrenByParentId.Keys.ToArray())
            childrenByParentId[parentId] = childrenByParentId[parentId]
                .OrderBy(group => group.SortOrder)
                .ToList();

        return childrenByParentId;
    }

    private LogGroupViewModel WrapGroup(LogGroup model)
    {
        var vm = new LogGroupViewModel(model, PersistGroupUpdateAsync);
        vm.PropertyChanged += GroupVm_PropertyChanged;
        return vm;
    }

    private async Task PersistGroupUpdateAsync(LogGroup pendingGroup)
    {
        await _mutationCoordinator.ExecuteAsync(async () =>
        {
            var allModels = (await _groupRepo.GetAllAsync()).Select(CloneGroup).ToList();
            var index = allModels.FindIndex(group => group.Id == pendingGroup.Id);
            if (index < 0)
                return;

            allModels[index] = CloneGroup(pendingGroup);
            await _groupRepo.ReplaceAllAsync(allModels);

            var currentGroup = ResolveCurrentGroup(pendingGroup.Id);
            if (currentGroup != null)
            {
                currentGroup.Model.Name = pendingGroup.Name;
                currentGroup.Name = pendingGroup.Name;
            }
        });
    }

    private LogGroupViewModel? ResolveCurrentGroup(string groupId)
        => _host.Groups.FirstOrDefault(group => string.Equals(group.Id, groupId, StringComparison.Ordinal));

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

    private void CaptureFilterExpansionStateIfNeeded()
    {
        _filterExpansionStateById ??= _host.Groups.ToDictionary(
            group => group.Id,
            group => group.IsExpanded,
            StringComparer.Ordinal);
    }

    private void RestoreFilterExpansionState()
    {
        if (_filterExpansionStateById == null)
            return;

        foreach (var group in _host.Groups)
        {
            if (_filterExpansionStateById.TryGetValue(group.Id, out var isExpanded))
                group.IsExpanded = isExpanded;
        }

        _filterExpansionStateById = null;
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
