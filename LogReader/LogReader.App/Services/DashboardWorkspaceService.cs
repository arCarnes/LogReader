namespace LogReader.App.Services;

using System.IO;
using LogReader.App.Models;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class DashboardWorkspaceService
{
    private readonly IDashboardWorkspaceHost _host;
    private readonly ILogFileRepository _fileRepo;
    private readonly ILogGroupRepository _groupRepo;
    private readonly Func<IReadOnlyDictionary<string, string>, Task<Dictionary<string, bool>>> _buildFileExistenceMapAsync;
    private CancellationTokenSource? _dashboardLoadCts;

    public DashboardWorkspaceService(IDashboardWorkspaceHost host, ILogFileRepository fileRepo, ILogGroupRepository groupRepo)
        : this(host, fileRepo, groupRepo, BuildFileExistenceMapAsync)
    {
    }

    internal DashboardWorkspaceService(
        IDashboardWorkspaceHost host,
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo,
        Func<IReadOnlyDictionary<string, string>, Task<Dictionary<string, bool>>> buildFileExistenceMapAsync)
    {
        _host = host;
        _fileRepo = fileRepo;
        _groupRepo = groupRepo;
        _buildFileExistenceMapAsync = buildFileExistenceMapAsync;
    }

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
                LeaveActiveDashboardScope();
        }

        await _groupRepo.DeleteAsync(groupVm.Id);
        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);
        _host.NotifyFilteredTabsChanged();
    }

    public Task ExportViewAsync(string exportPath)
        => _groupRepo.ExportViewAsync(exportPath);

    public async Task ApplyImportedViewAsync(ViewExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        var fileEntries = await _fileRepo.GetAllAsync();
        var fileEntriesByPath = new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileEntry in fileEntries.Where(fileEntry => !string.IsNullOrWhiteSpace(fileEntry.FilePath)))
            fileEntriesByPath.TryAdd(fileEntry.FilePath, fileEntry);

        var importedGroups = (export.Groups ?? new List<ViewExportGroup>())
            .Select((group, index) => new
            {
                Group = group,
                OriginalId = string.IsNullOrWhiteSpace(group.Id)
                    ? $"imported-group-{index}"
                    : group.Id,
                NewId = Guid.NewGuid().ToString()
            })
            .ToList();
        var importedIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var importedGroup in importedGroups)
            importedIdMap.TryAdd(importedGroup.OriginalId, importedGroup.NewId);

        LeaveActiveDashboardScope();

        var existingGroups = await _groupRepo.GetAllAsync();
        foreach (var existingGroup in existingGroups)
            await _groupRepo.DeleteAsync(existingGroup.Id);

        foreach (var importedGroup in importedGroups.OrderBy(group => group.Group.SortOrder))
        {
            var group = importedGroup.Group;
            var fileIds = new List<string>();
            if (group.Kind == LogGroupKind.Dashboard)
            {
                foreach (var path in (group.FilePaths ?? new List<string>())
                             .Where(path => !string.IsNullOrWhiteSpace(path))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!fileEntriesByPath.TryGetValue(path, out var entry))
                    {
                        entry = new LogFileEntry { FilePath = path };
                        await _fileRepo.AddAsync(entry);
                        fileEntriesByPath[path] = entry;
                    }

                    fileIds.Add(entry.Id);
                }
            }

            await _groupRepo.AddAsync(new LogGroup
            {
                Id = importedGroup.NewId,
                Name = string.IsNullOrWhiteSpace(group.Name)
                    ? group.Kind == LogGroupKind.Branch ? "Imported Folder" : "Imported Dashboard"
                    : group.Name,
                SortOrder = group.SortOrder,
                ParentGroupId = !string.IsNullOrWhiteSpace(group.ParentGroupId) &&
                                importedIdMap.TryGetValue(group.ParentGroupId, out var parentId)
                    ? parentId
                    : null,
                Kind = group.Kind,
                FileIds = fileIds
            });
        }

        var allGroups = await _groupRepo.GetAllAsync();
        RebuildGroupsCollection(allGroups);
        await RefreshAllMemberFilesAsync();
        _host.NotifyFilteredTabsChanged();
    }

    public async Task AddFilesToDashboardAsync(LogGroupViewModel groupVm, IReadOnlyList<string> filePaths)
    {
        if (!groupVm.CanManageFiles)
            return;

        var parsedPaths = DistinctLiteralFilePaths(filePaths);
        if (parsedPaths.Count == 0)
            return;

        var existingPaths = await GetExistingDashboardPathsAsync(groupVm);
        var added = false;
        foreach (var path in parsedPaths)
        {
            if (existingPaths.Contains(path))
                continue;

            var entry = await _fileRepo.GetByPathAsync(path);
            if (entry == null)
            {
                entry = new LogFileEntry { FilePath = path };
                await _fileRepo.AddAsync(entry);
            }

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

        var allEntries = await _fileRepo.GetAllAsync();
        return allEntries
            .Where(entry => fileIds.Contains(entry.Id) && !string.IsNullOrWhiteSpace(entry.FilePath))
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
    {
        _dashboardLoadCts?.Cancel();
    }

    public async Task OpenGroupFilesAsync(LogGroupViewModel group)
    {
        var dashboardLoadCts = BeginDashboardLoad();
        var ct = dashboardLoadCts.Token;
        _host.DashboardLoadDepth++;
        _host.IsDashboardLoading = true;
        _host.BeginTabCollectionNotificationSuppression();

        var fileIds = ResolveFileIdsInDisplayOrder(group);
        SetDashboardLoadingStatus(dashboardLoadCts, fileIds.Count == 0
            ? $"Loading \"{group.Name}\"..."
            : $"Loading \"{group.Name}\" (0/{fileIds.Count})...");

        await Task.Yield();

        var canceled = false;
        try
        {
            var loadedCount = 0;
            const int maxOpenAttempts = 3;
            for (var index = 0; index < fileIds.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                var fileId = fileIds[index];
                var entry = await _fileRepo.GetByIdAsync(fileId);
                ct.ThrowIfCancellationRequested();
                if (entry != null)
                {
                    var fileExists = await FileExistsOffUiAsync(entry.FilePath, ct);
                    ct.ThrowIfCancellationRequested();
                    if (!fileExists)
                    {
                        SetDashboardLoadingStatus(dashboardLoadCts, $"Loading \"{group.Name}\" ({index + 1}/{fileIds.Count}, opened {loadedCount})...");
                        continue;
                    }

                    var opened = false;
                    for (var attempt = 1; attempt <= maxOpenAttempts; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        await _host.OpenFilePathAsync(
                            entry.FilePath,
                            reloadIfLoadError: true,
                            activateTab: false,
                            deferVisibilityRefresh: true,
                            ct: ct);
                        ct.ThrowIfCancellationRequested();
                        var tab = _host.Tabs.FirstOrDefault(t => string.Equals(t.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
                        if (tab != null && !tab.HasLoadError)
                        {
                            opened = true;
                            break;
                        }

                        if (attempt < maxOpenAttempts)
                            await Task.Delay(400, ct);
                    }

                    if (opened)
                        loadedCount++;
                }

                SetDashboardLoadingStatus(dashboardLoadCts, $"Loading \"{group.Name}\" ({index + 1}/{fileIds.Count}, opened {loadedCount})...");
            }

            SetDashboardLoadingStatus(dashboardLoadCts, $"Loaded \"{group.Name}\" ({loadedCount}/{fileIds.Count} opened).");
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        finally
        {
            _host.EndTabCollectionNotificationSuppression();
            _host.EnsureSelectedTabInCurrentScope();

            _host.DashboardLoadDepth = Math.Max(0, _host.DashboardLoadDepth - 1);
            if (_host.DashboardLoadDepth == 0)
                _host.IsDashboardLoading = false;

            if (canceled && IsCurrentDashboardLoad(dashboardLoadCts))
                _host.DashboardLoadingStatusText = string.Empty;

            CompleteDashboardLoad(dashboardLoadCts);
        }
    }

    public async Task<IReadOnlyList<string>> GetGroupFilePathsAsync(string groupId)
    {
        var allGroups = await _groupRepo.GetAllAsync();
        var resolvedIds = ResolveFileIdsFromModels(allGroups, groupId);
        var allFiles = await _fileRepo.GetAllAsync();
        return allFiles
            .Where(f => resolvedIds.Contains(f.Id))
            .Select(f => f.FilePath)
            .ToList()
            .AsReadOnly();
    }

    public async Task RefreshAllMemberFilesAsync()
    {
        var allFiles = await _fileRepo.GetAllAsync();
        var fileIdToPath = allFiles.ToDictionary(f => f.Id, f => f.FilePath);
        var fileExistenceById = await _buildFileExistenceMapAsync(fileIdToPath);
        var selectedFileId = _host.SelectedTab?.FileId;
        foreach (var group in _host.Groups)
            group.RefreshMemberFiles(_host.Tabs, fileIdToPath, fileExistenceById, selectedFileId);
    }

    public async Task RefreshMemberFilesForFileIdsAsync(IReadOnlyDictionary<string, string> changedFilePathsById)
    {
        if (changedFilePathsById.Count == 0)
            return;

        var changedFileIds = changedFilePathsById.Keys.ToHashSet(StringComparer.Ordinal);
        var fileExistenceById = await _buildFileExistenceMapAsync(changedFilePathsById);
        var openTabsByFileId = _host.Tabs
            .Where(tab => changedFileIds.Contains(tab.FileId))
            .ToDictionary(tab => tab.FileId, StringComparer.Ordinal);
        var selectedFileId = _host.SelectedTab?.FileId;
        var affectedGroups = _host.Groups
            .Where(group => group.Kind == LogGroupKind.Dashboard &&
                            group.Model.FileIds.Any(changedFileIds.Contains))
            .ToList();

        foreach (var group in affectedGroups)
        {
            foreach (var fileId in group.Model.FileIds.Where(changedFileIds.Contains))
            {
                openTabsByFileId.TryGetValue(fileId, out var openTab);
                changedFilePathsById.TryGetValue(fileId, out var storedFilePath);
                var fileExists = fileExistenceById.TryGetValue(fileId, out var exists) && exists;
                group.RefreshMemberFile(fileId, openTab, storedFilePath, fileExists, selectedFileId);
            }
        }
    }

    public void UpdateSelectedMemberFileHighlights(string? selectedFileId)
    {
        foreach (var group in _host.Groups)
            group.SetSelectedMemberFile(selectedFileId);
    }

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
                LeaveActiveDashboardScope();
            else
                active.IsSelected = true;
        }

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

    private IReadOnlyList<string> ResolveFileIdsInDisplayOrder(LogGroupViewModel group)
    {
        var orderedFileIds = new List<string>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        var seenFileIds = new HashSet<string>(StringComparer.Ordinal);
        CollectFileIdsInDisplayOrder(group, seenGroups, seenFileIds, orderedFileIds);
        return orderedFileIds;
    }

    private static void CollectFileIdsInDisplayOrder(
        LogGroupViewModel group,
        HashSet<string> seenGroups,
        HashSet<string> seenFileIds,
        List<string> orderedFileIds)
    {
        if (!seenGroups.Add(group.Id))
            return;

        foreach (var fileId in group.Model.FileIds)
        {
            if (seenFileIds.Add(fileId))
                orderedFileIds.Add(fileId);
        }

        foreach (var child in group.Children.OrderBy(c => c.Model.SortOrder))
            CollectFileIdsInDisplayOrder(child, seenGroups, seenFileIds, orderedFileIds);
    }

    private static HashSet<string> ResolveFileIdsFromModels(
        List<LogGroup> allGroups,
        string groupId,
        HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();
        if (!visited.Add(groupId))
            return new HashSet<string>();

        var result = new HashSet<string>();
        var group = allGroups.FirstOrDefault(g => g.Id == groupId);
        if (group == null)
            return result;

        result.UnionWith(group.FileIds);
        foreach (var child in allGroups.Where(g => g.ParentGroupId == groupId))
            result.UnionWith(ResolveFileIdsFromModels(allGroups, child.Id, visited));
        return result;
    }

    private void GroupVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogGroupViewModel.Name))
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

    private CancellationTokenSource BeginDashboardLoad()
    {
        var next = new CancellationTokenSource();
        var previous = _dashboardLoadCts;
        _dashboardLoadCts = next;
        previous?.Cancel();
        return next;
    }

    private void CompleteDashboardLoad(CancellationTokenSource dashboardLoadCts)
    {
        if (ReferenceEquals(_dashboardLoadCts, dashboardLoadCts))
            _dashboardLoadCts = null;

        dashboardLoadCts.Dispose();
    }

    private bool IsCurrentDashboardLoad(CancellationTokenSource dashboardLoadCts)
        => ReferenceEquals(_dashboardLoadCts, dashboardLoadCts);

    private void SetDashboardLoadingStatus(CancellationTokenSource dashboardLoadCts, string statusText)
    {
        if (!IsCurrentDashboardLoad(dashboardLoadCts) || dashboardLoadCts.IsCancellationRequested)
            return;

        _host.DashboardLoadingStatusText = statusText;
    }

    private void LeaveActiveDashboardScope()
    {
        CancelDashboardLoad();
        _host.ActiveDashboardId = null;
        foreach (var group in _host.Groups)
            group.IsSelected = false;
    }

    private static Task<bool> FileExistsOffUiAsync(string filePath, CancellationToken ct)
        => Task.Run(() => File.Exists(filePath)).WaitAsync(ct);

    private static Task<Dictionary<string, bool>> BuildFileExistenceMapAsync(IReadOnlyDictionary<string, string> fileIdToPath)
        => Task.Run(() => fileIdToPath.ToDictionary(
            kvp => kvp.Key,
            kvp => File.Exists(kvp.Value),
            StringComparer.Ordinal));
}
