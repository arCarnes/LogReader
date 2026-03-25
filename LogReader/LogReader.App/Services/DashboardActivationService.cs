namespace LogReader.App.Services;

using System.IO;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class DashboardActivationService
{
    private readonly IDashboardWorkspaceHost _host;
    private readonly ILogFileRepository _fileRepo;
    private readonly ILogGroupRepository _groupRepo;
    private readonly Func<IReadOnlyDictionary<string, string>, Task<Dictionary<string, bool>>> _buildFileExistenceMapAsync;
    private readonly Dictionary<string, AppliedDashboardModifierState> _dashboardModifiers = new(StringComparer.Ordinal);
    private AppliedAdHocModifierState? _adHocModifier;
    private CancellationTokenSource? _dashboardLoadCts;

    public DashboardActivationService(
        IDashboardWorkspaceHost host,
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo)
        : this(host, fileRepo, groupRepo, BuildFileExistenceMapAsync)
    {
    }

    internal DashboardActivationService(
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

    public bool HasActiveModifiers => _dashboardModifiers.Count > 0 || _adHocModifier != null;

    public bool HasDashboardModifier(string dashboardId)
        => _dashboardModifiers.ContainsKey(dashboardId);

    public bool HasAdHocModifier()
        => _adHocModifier != null;

    public string? GetDashboardModifierLabel(string dashboardId)
        => _dashboardModifiers.TryGetValue(dashboardId, out var modifier)
            ? modifier.Label
            : null;

    public string? GetAdHocModifierLabel()
        => _adHocModifier?.Label;

    public bool TryGetDashboardEffectivePaths(string dashboardId, out IReadOnlySet<string> effectivePaths)
    {
        if (_dashboardModifiers.TryGetValue(dashboardId, out var modifier))
        {
            effectivePaths = modifier.EffectivePaths;
            return true;
        }

        effectivePaths = EmptyPathSet.Instance;
        return false;
    }

    public bool TryGetAdHocEffectivePaths(out IReadOnlySet<string> effectivePaths)
    {
        if (_adHocModifier != null)
        {
            effectivePaths = _adHocModifier.EffectivePaths;
            return true;
        }

        effectivePaths = EmptyPathSet.Instance;
        return false;
    }

    public bool IsManagedByActiveModifier(string filePath)
    {
        foreach (var modifier in _dashboardModifiers.Values)
        {
            if (modifier.EffectivePaths.Contains(filePath))
                return true;
        }

        return _adHocModifier?.EffectivePaths.Contains(filePath) == true;
    }

    public string? FindDashboardForModifierPath(string filePath)
    {
        foreach (var (dashboardId, modifier) in _dashboardModifiers)
        {
            if (modifier.EffectivePaths.Contains(filePath))
                return dashboardId;
        }

        return null;
    }

    public bool IsAdHocModifierPath(string filePath)
        => _adHocModifier?.EffectivePaths.Contains(filePath) == true;

    public IReadOnlyList<string> GetAdHocBasePathsSnapshot()
        => _adHocModifier?.BasePaths ?? Array.Empty<string>();

    public async Task SetDashboardModifierAsync(LogGroupViewModel group, int daysBack, IReadOnlyList<ReplacementPattern> patterns)
    {
        _dashboardModifiers[group.Id] = new AppliedDashboardModifierState(daysBack, ClonePatterns(patterns));
        await RefreshAllMemberFilesAsync();
    }

    public async Task ClearDashboardModifierAsync(LogGroupViewModel group)
    {
        if (_dashboardModifiers.Remove(group.Id))
            await RefreshAllMemberFilesAsync();
    }

    public async Task SetAdHocModifierAsync(int daysBack, IReadOnlyList<ReplacementPattern> patterns)
    {
        var basePaths = _adHocModifier?.BasePaths ?? ResolveCurrentAdHocBasePaths();
        _adHocModifier = new AppliedAdHocModifierState(daysBack, ClonePatterns(patterns), basePaths);
        await RefreshAllMemberFilesAsync();
    }

    public async Task ClearAdHocModifierAsync()
    {
        if (_adHocModifier == null)
            return;

        _adHocModifier = null;
        await RefreshAllMemberFilesAsync();
    }

    public void CancelDashboardLoad()
    {
        _dashboardLoadCts?.Cancel();
    }

    public void LeaveActiveDashboardScope()
    {
        CancelDashboardLoad();
        _host.ActiveDashboardId = null;
        foreach (var group in _host.Groups)
            group.IsSelected = false;
    }

    public void PruneModifierState()
    {
        var validDashboardIds = _host.Groups
            .Where(group => group.Kind == LogGroupKind.Dashboard)
            .Select(group => group.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var dashboardId in _dashboardModifiers.Keys.ToList())
        {
            if (!validDashboardIds.Contains(dashboardId))
                _dashboardModifiers.Remove(dashboardId);
        }
    }

    public async Task OpenGroupFilesAsync(LogGroupViewModel group)
    {
        var dashboardLoadCts = BeginDashboardLoad();
        var ct = dashboardLoadCts.Token;
        _host.DashboardLoadDepth++;
        _host.IsDashboardLoading = true;
        _host.BeginTabCollectionNotificationSuppression();

        var modifierLabel = GetDashboardModifierLabel(group.Id);
        var scopeDisplayName = string.IsNullOrWhiteSpace(modifierLabel)
            ? group.Name
            : $"{group.Name} [{modifierLabel}]";
        var targets = await ResolveOpenTargetsAsync(group);
        SetDashboardLoadingStatus(dashboardLoadCts, targets.Count == 0
            ? $"Loading \"{scopeDisplayName}\"..."
            : $"Loading \"{scopeDisplayName}\" (0/{targets.Count})...");

        await Task.Yield();

        var canceled = false;
        try
        {
            var loadedCount = 0;
            const int maxOpenAttempts = 3;
            for (var index = 0; index < targets.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                var filePath = targets[index];
                var fileExists = await FileExistsOffUiAsync(filePath, ct);
                ct.ThrowIfCancellationRequested();
                if (!fileExists)
                {
                    SetDashboardLoadingStatus(dashboardLoadCts, $"Loading \"{scopeDisplayName}\" ({index + 1}/{targets.Count}, opened {loadedCount})...");
                    continue;
                }

                var opened = false;
                for (var attempt = 1; attempt <= maxOpenAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    await _host.OpenFilePathAsync(
                        filePath,
                        reloadIfLoadError: true,
                        activateTab: false,
                        deferVisibilityRefresh: true,
                        ct: ct);
                    ct.ThrowIfCancellationRequested();
                    var tab = _host.Tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
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

                SetDashboardLoadingStatus(dashboardLoadCts, $"Loading \"{scopeDisplayName}\" ({index + 1}/{targets.Count}, opened {loadedCount})...");
            }

            SetDashboardLoadingStatus(dashboardLoadCts, $"Loaded \"{scopeDisplayName}\" ({loadedCount}/{targets.Count} opened).");
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
        var entriesById = await _fileRepo.GetByIdsAsync(resolvedIds);
        return resolvedIds
            .Where(entriesById.ContainsKey)
            .Select(fileId => entriesById[fileId].FilePath)
            .ToList()
            .AsReadOnly();
    }

    public async Task RefreshAllMemberFilesAsync()
    {
        var allFiles = await _fileRepo.GetAllAsync();
        var fileIdToPath = allFiles.ToDictionary(f => f.Id, f => f.FilePath, StringComparer.Ordinal);
        var fileExistenceById = await _buildFileExistenceMapAsync(fileIdToPath);
        var selectedFileId = _host.SelectedTab?.FileId;
        var selectedFilePath = _host.SelectedTab?.FilePath;
        var openTabsByPath = _host.Tabs
            .GroupBy(tab => tab.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var resolvedModifierMembersByDashboard = ResolveDashboardModifierMembers(fileIdToPath);
        var allModifiedPaths = resolvedModifierMembersByDashboard.Values
            .SelectMany(members => members)
            .Where(member => member.ErrorMessage == null)
            .Select(member => member.EffectivePath);
        var adHocModifierMembers = ResolveAdHocModifierMembers();
        allModifiedPaths = allModifiedPaths.Concat(
            adHocModifierMembers
                .Where(member => member.ErrorMessage == null)
                .Select(member => member.EffectivePath));
        var modifiedPathExistence = await BuildPathExistenceMapAsync(allModifiedPaths);

        foreach (var group in _host.Groups)
        {
            if (group.Kind == LogGroupKind.Dashboard &&
                resolvedModifierMembersByDashboard.TryGetValue(group.Id, out var resolvedMembers))
            {
                group.ModifierLabel = _dashboardModifiers[group.Id].Label;
                group.ReplaceMemberFiles(BuildModifierMemberViewModels(
                    resolvedMembers,
                    openTabsByPath,
                    modifiedPathExistence,
                    selectedFilePath,
                    _host.ShowFullPathsInDashboard));
                continue;
            }

            group.ModifierLabel = string.Empty;
            group.RefreshMemberFiles(_host.Tabs, fileIdToPath, fileExistenceById, selectedFileId, _host.ShowFullPathsInDashboard);
        }

        SyncModifierLabels();
    }

    public async Task RefreshMemberFilesForFileIdsAsync(IReadOnlyDictionary<string, string> changedFilePathsById)
    {
        if (changedFilePathsById.Count == 0)
            return;

        if (HasActiveModifiers)
        {
            await RefreshAllMemberFilesAsync();
            return;
        }

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
                group.RefreshMemberFile(fileId, openTab, storedFilePath, fileExists, selectedFileId, _host.ShowFullPathsInDashboard);
            }
        }
    }

    public void UpdateSelectedMemberFileHighlights(string? selectedFileId)
    {
        foreach (var group in _host.Groups)
        {
            if (group.Kind == LogGroupKind.Dashboard && HasDashboardModifier(group.Id))
                group.SetSelectedMemberFilePath(_host.SelectedTab?.FilePath);
            else
                group.SetSelectedMemberFile(selectedFileId);
        }
    }

    private async Task<IReadOnlyList<string>> ResolveOpenTargetsAsync(LogGroupViewModel group)
    {
        if (_dashboardModifiers.TryGetValue(group.Id, out var modifier))
        {
            return modifier.Members
                .Where(member => member.ErrorMessage == null && !string.IsNullOrWhiteSpace(member.EffectivePath))
                .Select(member => member.EffectivePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var fileIds = ResolveFileIdsInDisplayOrder(group);
        var entriesById = await _fileRepo.GetByIdsAsync(fileIds);
        return fileIds
            .Where(entriesById.ContainsKey)
            .Select(fileId => entriesById[fileId].FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
    }

    private Dictionary<string, IReadOnlyList<ResolvedModifierMember>> ResolveDashboardModifierMembers(
        IReadOnlyDictionary<string, string> fileIdToPath)
    {
        var resolvedByDashboard = new Dictionary<string, IReadOnlyList<ResolvedModifierMember>>(StringComparer.Ordinal);
        foreach (var (dashboardId, modifier) in _dashboardModifiers)
        {
            var dashboard = _host.Groups.FirstOrDefault(group =>
                group.Kind == LogGroupKind.Dashboard &&
                string.Equals(group.Id, dashboardId, StringComparison.Ordinal));
            if (dashboard == null)
            {
                modifier.UpdateMembers(Array.Empty<ResolvedModifierMember>());
                continue;
            }

            var members = new List<ResolvedModifierMember>();
            foreach (var fileId in dashboard.Model.FileIds)
            {
                if (!fileIdToPath.TryGetValue(fileId, out var basePath) || string.IsNullOrWhiteSpace(basePath))
                {
                    members.Add(new ResolvedModifierMember(fileId, string.Empty, "Base file path is missing."));
                    continue;
                }

                if (!TryTransformPath(basePath, modifier.Patterns, modifier.TargetDate, out var effectivePath, out var errorMessage))
                {
                    members.Add(new ResolvedModifierMember(fileId, basePath, errorMessage));
                    continue;
                }

                members.Add(new ResolvedModifierMember(fileId, effectivePath, ErrorMessage: null));
            }

            modifier.UpdateMembers(members);
            resolvedByDashboard[dashboardId] = members;
        }

        return resolvedByDashboard;
    }

    private IReadOnlyList<ResolvedModifierMember> ResolveAdHocModifierMembers()
    {
        if (_adHocModifier == null)
            return Array.Empty<ResolvedModifierMember>();

        var members = new List<ResolvedModifierMember>();
        foreach (var basePath in _adHocModifier.BasePaths)
        {
            if (!TryTransformPath(basePath, _adHocModifier.Patterns, _adHocModifier.TargetDate, out var effectivePath, out var errorMessage))
            {
                members.Add(new ResolvedModifierMember(basePath, basePath, errorMessage));
                continue;
            }

            members.Add(new ResolvedModifierMember(basePath, effectivePath, ErrorMessage: null));
        }

        _adHocModifier.UpdateMembers(members);
        return members;
    }

    private static IReadOnlyList<GroupFileMemberViewModel> BuildModifierMemberViewModels(
        IReadOnlyList<ResolvedModifierMember> resolvedMembers,
        IReadOnlyDictionary<string, LogTabViewModel> openTabsByPath,
        IReadOnlyDictionary<string, bool> pathExistenceByPath,
        string? selectedFilePath,
        bool showFullPath)
    {
        var memberViewModels = new List<GroupFileMemberViewModel>(resolvedMembers.Count);
        foreach (var member in resolvedMembers)
        {
            openTabsByPath.TryGetValue(member.EffectivePath, out var openTab);
            var effectivePath = openTab?.FilePath ?? member.EffectivePath;
            var errorMessage = member.ErrorMessage;
            if (errorMessage == null &&
                (!pathExistenceByPath.TryGetValue(member.EffectivePath, out var exists) || !exists) &&
                openTab == null)
            {
                errorMessage = "File not found";
            }

            memberViewModels.Add(new GroupFileMemberViewModel(
                member.BaseKey,
                openTab?.FileName ?? Path.GetFileName(effectivePath),
                effectivePath,
                showFullPath,
                errorMessage,
                isSelected: string.Equals(effectivePath, selectedFilePath, StringComparison.OrdinalIgnoreCase)));
        }

        return memberViewModels;
    }

    private IReadOnlyList<string> ResolveCurrentAdHocBasePaths()
    {
        if (_adHocModifier != null)
            return _adHocModifier.BasePaths;

        var assignedFileIds = _host.Groups
            .Where(group => group.Kind == LogGroupKind.Dashboard)
            .SelectMany(group => group.Model.FileIds)
            .ToHashSet(StringComparer.Ordinal);
        var modifiedDashboardPaths = _dashboardModifiers.Values
            .SelectMany(modifier => modifier.EffectivePaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _host.Tabs
            .Where(tab => !assignedFileIds.Contains(tab.FileId) &&
                          !modifiedDashboardPaths.Contains(tab.FilePath))
            .Select(tab => tab.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void SyncModifierLabels()
    {
        foreach (var group in _host.Groups)
        {
            group.ModifierLabel = group.Kind == LogGroupKind.Dashboard &&
                                  _dashboardModifiers.TryGetValue(group.Id, out var modifier)
                ? modifier.Label
                : string.Empty;
        }
    }

    private static bool TryTransformPath(
        string basePath,
        IReadOnlyList<ReplacementPattern> patterns,
        DateTime targetDate,
        out string effectivePath,
        out string? errorMessage)
    {
        effectivePath = basePath;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            errorMessage = "Base file path is missing.";
            return false;
        }

        if (patterns.Count == 0)
        {
            errorMessage = "No date rolling patterns are configured.";
            return false;
        }

        string? firstError = null;
        string? firstTransformedPath = null;
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern.FindPattern))
            {
                firstError ??= "Find pattern is empty.";
                continue;
            }

            if (!ReplacementTokenParser.TryExpand(pattern.ReplacePattern, targetDate, out var expandedReplace, out var expandError))
            {
                firstError ??= expandError;
                continue;
            }

            if (basePath.IndexOf(pattern.FindPattern, StringComparison.OrdinalIgnoreCase) < 0)
            {
                firstError ??= "Pattern did not match path.";
                continue;
            }

            var candidatePath = basePath.Replace(pattern.FindPattern, expandedReplace, StringComparison.OrdinalIgnoreCase);
            firstTransformedPath ??= candidatePath;

            if (File.Exists(candidatePath))
            {
                effectivePath = candidatePath;
                errorMessage = null;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(firstTransformedPath))
        {
            effectivePath = firstTransformedPath;
            errorMessage = null;
            return true;
        }

        errorMessage = firstError ?? "Pattern did not match path.";
        return false;
    }

    private async Task<Dictionary<string, bool>> BuildPathExistenceMapAsync(IEnumerable<string> filePaths)
    {
        var distinctPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path => path, StringComparer.OrdinalIgnoreCase);
        if (distinctPaths.Count == 0)
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        return await _buildFileExistenceMapAsync(distinctPaths);
    }

    private static IReadOnlyList<ReplacementPattern> ClonePatterns(IReadOnlyList<ReplacementPattern> patterns)
        => patterns
            .Select(pattern => new ReplacementPattern
            {
                Id = pattern.Id,
                Name = pattern.Name,
                FindPattern = pattern.FindPattern,
                ReplacePattern = pattern.ReplacePattern
            })
            .ToList();

    private static IReadOnlyList<string> ResolveFileIdsInDisplayOrder(LogGroupViewModel group)
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

    private static Task<bool> FileExistsOffUiAsync(string filePath, CancellationToken ct)
        => Task.Run(() => File.Exists(filePath)).WaitAsync(ct);

    private static Task<Dictionary<string, bool>> BuildFileExistenceMapAsync(IReadOnlyDictionary<string, string> fileIdToPath)
        => Task.Run(() => fileIdToPath.ToDictionary(
            kvp => kvp.Key,
            kvp => File.Exists(kvp.Value),
            StringComparer.Ordinal));
}

internal class AppliedDashboardModifierState
{
    public AppliedDashboardModifierState(int daysBack, IReadOnlyList<ReplacementPattern> patterns)
    {
        DaysBack = daysBack;
        Patterns = patterns;
    }

    public int DaysBack { get; }

    public IReadOnlyList<ReplacementPattern> Patterns { get; }

    public string Label => $"T-{DaysBack}";

    public DateTime TargetDate => DateTime.Today.AddDays(-DaysBack);

    public IReadOnlyList<ResolvedModifierMember> Members { get; private set; } = Array.Empty<ResolvedModifierMember>();

    public IReadOnlySet<string> EffectivePaths { get; private set; } = EmptyPathSet.Instance;

    public void UpdateMembers(IReadOnlyList<ResolvedModifierMember> members)
    {
        Members = members;
        EffectivePaths = members
            .Where(member => member.ErrorMessage == null && !string.IsNullOrWhiteSpace(member.EffectivePath))
            .Select(member => member.EffectivePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class AppliedAdHocModifierState : AppliedDashboardModifierState
{
    public AppliedAdHocModifierState(int daysBack, IReadOnlyList<ReplacementPattern> patterns, IReadOnlyList<string> basePaths)
        : base(daysBack, patterns)
    {
        BasePaths = basePaths;
    }

    public IReadOnlyList<string> BasePaths { get; }
}

internal sealed record ResolvedModifierMember(string BaseKey, string EffectivePath, string? ErrorMessage);

internal static class EmptyPathSet
{
    public static IReadOnlySet<string> Instance { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
