namespace LogReader.App.Services;

using System.IO;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

internal sealed partial class DashboardActivationService
{
    private readonly TabMemberRefreshScheduler _membershipRefreshScheduler;
    private readonly Dictionary<string, DashboardFileProbeResult> _statusByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<DashboardFileProbeResult>> _pendingProbes = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _membershipRefreshShuttingDown;
    private Task _lastMembershipRefresh = Task.CompletedTask;
    private readonly object _membershipPublicationGate = new();

    internal Func<TabMemberRefreshRequest, Task>? QueueMemberRefresh { get; set; }
    internal IUiDispatcher? MembershipDispatcher { get; set; }

    internal Task DrainMembershipRefreshAsync() => _lastMembershipRefresh;

    internal void ShutdownMembershipRefresh()
    {
        _membershipRefreshShuttingDown = true;
        _membershipRefreshScheduler.Shutdown();
    }

    internal async Task ReconcileMembershipAsync(IEnumerable<string> dashboardIds, bool checkNewPaths = true)
    {
        if (_membershipRefreshShuttingDown)
            return;
        var ids = dashboardIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
            return;
        await PrepareMemberRowsAsync(ids, resolveModifiers: false, CancellationToken.None);
        if (!checkNewPaths)
            return;
        var request = new TabMemberRefreshRequest(false, new Dictionary<string, string>(), ids);
        _lastMembershipRefresh = QueueMemberRefresh?.Invoke(request) ?? _membershipRefreshScheduler.Queue(request);
    }

    private Task RunMembershipRefreshAsync(TabMemberRefreshRequest request, CancellationToken ct)
        => RefreshQueuedMembersAsync(request, ct);

    private async Task RefreshDashboardModifierRowsAsync(string dashboardId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { dashboardId };
        await PrepareMemberRowsAsync(ids, resolveModifiers: true, CancellationToken.None);
        var paths = _host.Groups.Where(group => group.Id == dashboardId)
            .SelectMany(group => group.MemberFiles).ToDictionary(member => member.FileId, member => member.FilePath, StringComparer.Ordinal);
        var request = new TabMemberRefreshRequest(false, paths, ids);
        _lastMembershipRefresh = QueueMemberRefresh?.Invoke(request) ?? _membershipRefreshScheduler.Queue(request);
    }

    private async Task ResolveAdHocModifierAsync()
    {
        var revision = _modifierService.GetAdHocRevision();
        var basePaths = _modifierService.GetAdHocBasePathsSnapshot().ToArray();
        var entries = await _fileRepo.GetByPathsAsync(basePaths);
        var captured = _modifierService.CaptureRefresh(Array.Empty<LogGroupViewModel>(),
            new Dictionary<string, string>(), includeAdHoc: true);
        var snapshot = await Task.Run(captured.Resolve);
        if (_membershipRefreshShuttingDown || !ReferenceEquals(revision, _modifierService.GetAdHocRevision()))
            return;
        captured.Apply(snapshot);
        _adHocDisplayNameIdsByPath.Clear();
        foreach (var member in snapshot.AdHocMembers)
            if (entries.TryGetValue(member.BaseKey, out var entry))
            {
                _adHocDisplayNameIdsByPath[member.EffectivePath] = entry.Id;
                if (!_displayNameEditGenerations.ContainsKey(entry.Id))
                    _displayNamesById[entry.Id] = entry.DisplayName;
            }
        ApplyDisplayNames();
    }

    internal async Task RefreshQueuedMembersAsync(TabMemberRefreshRequest request, CancellationToken ct)
    {
        await Task.Yield();
        if (_membershipRefreshShuttingDown)
            return;
        var ids = request.DashboardIds?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var needsResolution = false;
        await PublishMembershipAsync(() =>
        {
            foreach (var group in _host.Groups)
                if (group.Model.FileIds.Any(request.ChangedFilePaths.ContainsKey) ||
                    group.MemberFiles.Any(member => request.ChangedFilePaths.Values.Contains(member.FilePath, StringComparer.OrdinalIgnoreCase)))
                    ids.Add(group.Id);
            needsResolution = _host.Groups.Where(group => ids.Contains(group.Id))
                .Any(group => group.MemberFiles.Any(member => member.RequiresPathResolution));
        });
        await PrepareMemberRowsAsync(ids, resolveModifiers: needsResolution, ct);
        ct.ThrowIfCancellationRequested();
        if (_membershipRefreshShuttingDown)
            return;

        var force = request.ChangedFilePaths.Count > 0;
        var changedPaths = request.ChangedFilePaths.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        (LogGroupViewModel Group, GroupFileMemberViewModel Member)[] targets = Array.Empty<(LogGroupViewModel, GroupFileMemberViewModel)>();
        var revisions = new Dictionary<GroupFileMemberViewModel, long>();
        await PublishMembershipAsync(() =>
        {
            targets = _host.Groups.Where(group => ids.Contains(group.Id))
                .SelectMany(group => group.MemberFiles
                    .Where(member => !member.HasPathResolutionError && !string.IsNullOrWhiteSpace(member.FilePath) &&
                        (member.IsChecking || (force && (request.ChangedFilePaths.ContainsKey(member.FileId) || changedPaths.Contains(member.FilePath)))))
                    .Select(member => (Group: group, Member: member))).ToArray();
            foreach (var target in targets)
            {
                target.Member.IsChecking = true;
                target.Member.PresentationRevision++;
                revisions[target.Member] = target.Member.PresentationRevision;
            }
        });
        var paths = targets.Select(target => target.Member.FilePath).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path => path, StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
            return;
        try
        {
            var statuses = await ProbeSharedPathsAsync(paths, ct);
            await PublishMembershipAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (_membershipRefreshShuttingDown)
                    return;
                foreach (var (group, member) in targets)
                {
                    if (!_host.Groups.Contains(group) || !group.MemberFiles.Contains(member) || member.PresentationRevision != revisions[member])
                        continue;
                    member.ErrorMessage = _host.Tabs.Any(tab => string.Equals(tab.FilePath, member.FilePath, StringComparison.OrdinalIgnoreCase))
                        ? null : statuses[member.FilePath].ErrorMessage;
                    member.IsChecking = false;
                    member.PresentationRevision++;
                    group.NotifyMemberStatusChanged();
                }
                UpdateSelectedMemberFileHighlights();
            });
        }
        catch
        {
            await PublishMembershipAsync(() =>
            {
                if (_membershipRefreshShuttingDown)
                    return;
                foreach (var (group, member) in targets)
                    if (_host.Groups.Contains(group) && group.MemberFiles.Contains(member) && member.PresentationRevision == revisions[member])
                        member.IsChecking = false;
            });
            throw;
        }
    }

    private Task PublishMembershipAsync(Action action)
        => MembershipDispatcher?.InvokeAsync(action) ?? InvokeInlineAsync(action);

    private Task InvokeInlineAsync(Action action)
    {
        // Direct service hosts have no dispatcher to serialize command and worker publication.
        lock (_membershipPublicationGate)
            action();
        return Task.CompletedTask;
    }

    private async Task PrepareMemberRowsAsync(IReadOnlySet<string> ids, bool resolveModifiers, CancellationToken ct)
    {
        var groups = _host.Groups.Where(group => ids.Contains(group.Id) && group.Kind == LogGroupKind.Dashboard).ToArray();
        var snapshots = groups.ToDictionary(group => group, group => group.Model.FileIds.ToArray());
        var revisions = groups.ToDictionary(group => group, group => _modifierService.GetDashboardRevision(group.Id));
        var trackedIds = snapshots.Values.SelectMany(fileIds => fileIds).ToHashSet(StringComparer.Ordinal);
        trackedIds.UnionWith(_host.Tabs.Where(tab => tab.ScopeDashboardId != null && ids.Contains(tab.ScopeDashboardId)).Select(tab => tab.FileId));
        RegisterTargetedRefreshRequest(trackedIds, ResolveTrackedFileIdSnapshot());
        long nameGeneration;
        lock (_refreshGenerationGate)
            nameGeneration = _displayNameGeneration;
        var entries = await _fileRepo.GetByIdsAsync(trackedIds).WaitAsync(ct);
        var paths = entries.ToDictionary(pair => pair.Key, pair => pair.Value.FilePath, StringComparer.Ordinal);
        var captured = _modifierService.CaptureRefresh(groups, paths, includeAdHoc: false);
        var resolved = resolveModifiers
            ? await Task.Run(captured.Resolve, ct).WaitAsync(ct)
            : null;

        await PublishMembershipAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (_membershipRefreshShuttingDown)
                return;
            foreach (var entry in entries.Values)
                if (!_displayNameEditGenerations.TryGetValue(entry.Id, out var editedAt) || editedAt <= nameGeneration)
                    _displayNamesById[entry.Id] = entry.DisplayName;
            foreach (var group in groups)
            {
                if (!_host.Groups.Contains(group) || !group.Model.FileIds.SequenceEqual(snapshots[group]) ||
                    !ReferenceEquals(_modifierService.GetDashboardRevision(group.Id), revisions[group]))
                    continue;
                var members = new List<GroupFileMemberViewModel>();
                var oldMembers = group.MemberFiles.ToDictionary(member => member.FileId, StringComparer.Ordinal);
                foreach (var id in snapshots[group])
                {
                    if (!entries.TryGetValue(id, out var entry))
                        continue;
                    var path = entry.FilePath;
                    string? error = null;
                    if (resolved?.DashboardMembers.TryGetValue(group.Id, out var modified) == true)
                    {
                        var transformed = modified.FirstOrDefault(member => member.BaseKey == id);
                        if (transformed != null)
                        {
                            path = transformed.EffectivePath;
                            error = transformed.ErrorMessage;
                        }
                    }
                    else if (revisions[group] != null && oldMembers.TryGetValue(id, out var old))
                    {
                        // Keep a modifier's last resolved path until its worker result arrives.
                        members.Add(old);
                        continue;
                    }
                    var tab = _host.Tabs.FirstOrDefault(tab => string.Equals(tab.FilePath, path, StringComparison.OrdinalIgnoreCase));
                    DashboardFileProbeResult status;
                    bool known;
                    bool pending;
                    lock (_refreshGenerationGate)
                    {
                        known = _statusByPath.TryGetValue(NormalizeStatusPath(path), out status);
                        pending = _pendingProbes.ContainsKey(NormalizeStatusPath(path));
                        if (!_displayNameEditGenerations.TryGetValue(id, out var editedAt) || editedAt <= nameGeneration)
                            _displayNamesById[id] = entry.DisplayName;
                    }
                    members.Add(new GroupFileMemberViewModel(id, Path.GetFileName(path), path,
                        _host.ShowFullPathsInDashboard,
                        error ?? (tab != null ? null : known ? status.ErrorMessage :
                            oldMembers.TryGetValue(id, out var previous) && string.Equals(previous.FilePath, path, StringComparison.OrdinalIgnoreCase)
                                ? previous.ErrorMessage : null),
                        fileSizeText: tab == null ? null : GroupFileMemberViewModel.CreateFileSizeText(tab),
                        customDisplayName: _displayNamesById.GetValueOrDefault(id))
                    {
                        IsChecking = error == null && (!known || pending),
                        HasPathResolutionError = error != null,
                        RequiresPathResolution = revisions[group] != null && !resolveModifiers
                    });
                }
                group.ReconcileMemberFiles(members);
                if (resolved?.DashboardMembers.TryGetValue(group.Id, out var resolvedMembers) == true)
                    _modifierService.ApplyDashboardMembers(group.Id, revisions[group], resolvedMembers);
            }
            _modifierService.SyncModifierLabels(groups);
            ApplyDisplayNames();
            UpdateSelectedMemberFileHighlights();
        });
    }

    private Task RefreshChangedModifierMembersAsync(IReadOnlyDictionary<string, string> paths, CancellationToken ct)
        => RefreshQueuedMembersAsync(new TabMemberRefreshRequest(false, paths), ct);

    private async Task<Dictionary<string, DashboardFileProbeResult>> ProbeSharedPathsAsync(
        IReadOnlyDictionary<string, string> fileIdToPath, CancellationToken ct)
    {
        var newProbes = new Dictionary<string, TaskCompletionSource<DashboardFileProbeResult>>(StringComparer.OrdinalIgnoreCase);
        var tasks = new Dictionary<string, Task<DashboardFileProbeResult>>(StringComparer.Ordinal);
        var representatives = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_refreshGenerationGate)
        {
            foreach (var (id, path) in fileIdToPath)
            {
                var key = NormalizeStatusPath(path);
                if (!_pendingProbes.TryGetValue(key, out var task))
                {
                    var completion = new TaskCompletionSource<DashboardFileProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    newProbes[key] = completion;
                    representatives[id] = path;
                    task = completion.Task;
                    _pendingProbes[key] = task;
                }
                tasks[id] = task;
            }
        }
        if (representatives.Count > 0)
            _ = CompleteProbeBatchAsync(representatives, newProbes);
        await Task.WhenAll(tasks.Values).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        return tasks.ToDictionary(pair => pair.Key, pair => pair.Value.GetAwaiter().GetResult(), StringComparer.Ordinal);
    }

    private async Task CompleteProbeBatchAsync(
        IReadOnlyDictionary<string, string> representatives,
        IReadOnlyDictionary<string, TaskCompletionSource<DashboardFileProbeResult>> completions)
    {
        try
        {
            var result = await _buildFileProbeMapAsync(representatives).ConfigureAwait(false);
            lock (_refreshGenerationGate)
            {
                foreach (var (id, path) in representatives)
                {
                    var status = result.GetValueOrDefault(id, DashboardFileProbeResult.Unavailable);
                    var key = NormalizeStatusPath(path);
                    _statusByPath[key] = status;
                    completions[key].TrySetResult(status);
                    _pendingProbes.Remove(key);
                }
            }
        }
        catch (Exception ex)
        {
            lock (_refreshGenerationGate)
                foreach (var (path, completion) in completions)
                {
                    completion.TrySetException(ex);
                    _pendingProbes.Remove(path);
                    // A canceled waiter may have stopped observing its still-running probe.
                    _ = completion.Task.Exception;
                }
        }
    }

    private static string NormalizeStatusPath(string path)
    {
        try { return Path.GetFullPath(path).Replace('/', '\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Replace('/', '\\');
        }
    }
}
