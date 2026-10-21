namespace LogReader.App.Services;

using System.ComponentModel;
using System.IO;
using LogReader.App.ViewModels;
using LogReader.Core;

internal sealed partial class DashboardOpenCoordinator
{
    private readonly Dictionary<string, InteractiveDashboardCycle> _interactiveLoads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _interactiveRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DashboardTransition> _dashboardTransitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _dashboardTransitionRevisions = new(StringComparer.Ordinal);
    private long _nextInteractiveRevision;
    private long _nextDashboardTransitionRevision;
    private int _publishedInteractiveLoadDepth;

    public void CancelDashboardLoad(string dashboardId)
    {
        CancelMemberOpens(dashboardId);
        if (_interactiveLoads.Remove(dashboardId, out var cycle))
        {
            cycle.Cancellation.Cancel();
            PublishCycle(cycle, terminal: true, canceled: true);
        }
        lock (_dashboardLoadGate)
            if (_dashboardLoadSession?.DashboardId == dashboardId)
                _dashboardLoadSession.CancellationTokenSource.Cancel();
    }

    public Task CancelDashboardLoadAndWaitAsync(string dashboardId)
        => RunDashboardTransitionAsync(dashboardId, () => Task.CompletedTask);

    public bool IsCurrentDashboardTransition(string dashboardId, long revision)
        => _dashboardTransitionRevisions.GetValueOrDefault(dashboardId) == revision;

    public async Task<long> RunDashboardTransitionAsync(string dashboardId, Func<Task> transitionAsync)
    {
        var revision = ++_nextDashboardTransitionRevision;
        _dashboardTransitionRevisions[dashboardId] = revision;
        if (!_dashboardTransitions.TryGetValue(dashboardId, out var transition))
            _dashboardTransitions.Add(dashboardId, transition = new DashboardTransition());
        transition.PendingCount++;
        try
        {
            await transition.Gate.WaitAsync();
            try
            {
                await CancelDashboardLoadAndWaitCoreAsync(dashboardId);
                await transitionAsync();
            }
            finally { transition.Gate.Release(); }
        }
        finally
        {
            if (--transition.PendingCount == 0)
            {
                _dashboardTransitions.Remove(dashboardId);
                transition.Completion.TrySetResult();
                transition.Gate.Dispose();
            }
        }
        return revision;
    }

    private async Task CancelDashboardLoadAndWaitCoreAsync(string dashboardId)
    {
        var tasks = GetMemberOpenTasks(dashboardId)
            .Append(_interactiveLoads.GetValueOrDefault(dashboardId)?.Task ?? Task.CompletedTask).ToArray();
        CancelDashboardLoad(dashboardId);
        await Task.WhenAll(tasks);
    }

    private void CancelInteractiveLoads()
    {
        foreach (var dashboardId in _interactiveLoads.Keys.Concat(_memberOpens.Keys).Distinct(StringComparer.Ordinal).ToArray())
            CancelDashboardLoad(dashboardId);
    }

    public async Task CancelAndDrainInteractiveLoadsAsync()
    {
        var tasks = _interactiveLoads.Values.Select(cycle => cycle.Task).Concat(GetMemberOpenTasks())
            .Concat(_dashboardTransitions.Values.Select(transition => transition.Completion.Task)).ToArray();
        CancelInteractiveLoads();
        await Task.WhenAll(tasks);
    }

    private Task OpenInteractiveDashboardAsync(LogGroupViewModel group)
    {
        // A click during unload/reload must not start work outside that operation's drain.
        if (_dashboardTransitions.TryGetValue(group.Id, out var transition))
            return transition.Completion.Task;
        if (_interactiveLoads.TryGetValue(group.Id, out var existing))
            return existing.Task;
        if (group.LoadProgress.Phase is DashboardLoadPhase.Ready or DashboardLoadPhase.ReadyWithErrors or DashboardLoadPhase.Failed)
        {
            var paths = group.MemberFiles.Select(member => member.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var indexed = _host.Tabs.Where(tab => tab.ScopeDashboardId == group.Id && tab.IsIndexReady)
                .Select(tab => tab.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failed = _host.Tabs.Where(tab => tab.ScopeDashboardId == group.Id && tab.HasLoadError)
                .Select(tab => tab.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (paths.Length == group.LoadProgress.Total && paths.All(path => indexed.Contains(path) || failed.Contains(path)) &&
                paths.Count(indexed.Contains) == group.LoadProgress.Ready && paths.Count(failed.Contains) == group.LoadProgress.Failed)
            {
                if (paths.Length == 0)
                    _host.ExitDashboardScopeIfCurrentDashboardFinishedEmpty(group.Id);
                return Task.CompletedTask;
            }
        }
        var cycle = new InteractiveDashboardCycle(group, ++_nextInteractiveRevision);
        _interactiveLoads.Add(group.Id, cycle);
        _interactiveRevisions[group.Id] = cycle.Revision;
        _host.BeginDashboardLoadCycle(group.Id);
        PublishCycle(cycle, force: true);
        cycle.Task = RunInteractiveDashboardAsync(cycle);
        return cycle.Task;
    }

    private async Task RunInteractiveDashboardAsync(InteractiveDashboardCycle cycle)
    {
        var ct = cycle.Cancellation.Token;
        var pending = new List<Task>();
        var placeholdersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var targets = (await _resolveOpenTargetsAsync(cycle.Group)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            ct.ThrowIfCancellationRequested();
            cycle.TargetCount = targets.Length;
            cycle.TargetsResolved = true;
            PublishCycle(cycle, force: true);
            var preparedTargets = new TabWorkspaceService.PreparedTabOpen?[targets.Length];
            var memberOpenTargets = new Task?[targets.Length];
            for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                var path = targets[targetIndex];
                ct.ThrowIfCancellationRequested();
                if (TryGetMemberOpenTask(cycle.Group.Id, path, out var memberOpen))
                {
                    memberOpenTargets[targetIndex] = memberOpen;
                    continue;
                }
                var existing = _host.FindTabInScope(path, cycle.Group.Id);
                if (existing != null)
                {
                    if (existing.IsIndexReady) cycle.ReadyCount++;
                    else if (existing.HasLoadError) cycle.FailedCount++;
                    cycle.SettledCount++;
                    continue;
                }
                var prepared = await _host.CreateDashboardPlaceholderAsync(path, cycle.Group.Id, ct);
                if (prepared == null)
                {
                    cycle.FailedCount++;
                    cycle.SettledCount++;
                    continue;
                }
                if (_host.ActiveDashboardId == cycle.Group.Id)
                    _host.EnsureSelectedTabInCurrentScope();
                preparedTargets[targetIndex] = prepared;
            }
            placeholdersReady.TrySetResult();
            var workOrder = AdaptiveParallelismScheduler.BuildInterleavedWorkOrder(
                AdaptiveParallelismPolicy.CreatePlan(AdaptiveParallelismOperation.DashboardLoad, targets));
            foreach (var targetIndex in workOrder)
            {
                if (preparedTargets[targetIndex] is { } prepared)
                    pending.Add(FinishFileAsync(prepared));
                else if (memberOpenTargets[targetIndex] is { } memberOpen)
                    pending.Add(FinishMemberOpenAsync(targets[targetIndex], memberOpen));
            }
            await Task.WhenAll(pending);
            ct.ThrowIfCancellationRequested();
            PublishCycle(cycle, terminal: true);
            if (cycle.TargetCount == 0)
                _host.ExitDashboardScopeIfCurrentDashboardFinishedEmpty(cycle.Group.Id);
        }
        catch (OperationCanceledException)
        {
            PublishCycle(cycle, terminal: true, canceled: true);
        }
        catch
        {
            PublishCycle(cycle, terminal: true);
            throw;
        }
        finally
        {
            placeholdersReady.TrySetCanceled(ct.IsCancellationRequested ? ct : new CancellationToken(true));
            try
            {
                if (pending.Count > 0)
                {
                    try { await Task.WhenAll(pending); }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                if (_interactiveLoads.TryGetValue(cycle.Group.Id, out var current) && ReferenceEquals(current, cycle))
                    _interactiveLoads.Remove(cycle.Group.Id);
                UpdateAggregateLoadingState();
                cycle.Cancellation.Dispose();
            }
        }

        async Task FinishMemberOpenAsync(string path, Task memberOpen)
        {
            if (!cycle.WorkStarted)
            {
                cycle.WorkStarted = true;
                PublishCycle(cycle, force: true);
            }
            var succeeded = false;
            try
            {
                await memberOpen.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                succeeded = _host.FindTabInScope(path, cycle.Group.Id)?.IsIndexReady == true;
            }
            finally { SettleFile(succeeded); }
        }

        void SettleFile(bool succeeded)
        {
            if (ct.IsCancellationRequested)
                return;
            if (succeeded) cycle.ReadyCount++; else cycle.FailedCount++;
            cycle.SettledCount++;
            PublishCycle(cycle);
        }

        async Task FinishFileAsync(TabWorkspaceService.PreparedTabOpen prepared)
        {
            var tab = prepared.Tab;
            void ProgressChanged(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(LogTabViewModel.LoadingProgressText))
                    PublishCycle(cycle);
            }
            tab.PropertyChanged += ProgressChanged;
            var succeeded = false;
            try
            {
                var probe = await tab.ActiveSession.RunInteractiveReadAsync(() => _probeFileAsync(prepared.FilePath, ct), ct);
                if (!probe.IsFound)
                    throw new IOException($"Could not read '{tab.DisplayName}'.");
                if (!cycle.WorkStarted)
                {
                    cycle.WorkStarted = true;
                    PublishCycle(cycle, force: true);
                }
                await prepared.LoadAsync().WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (tab.HasLoadError)
                    throw new IOException(tab.DisplayStatusText);
                await placeholdersReady.Task.WaitAsync(ct);
                await _host.CompleteDashboardPlaceholderAsync(prepared, ct);
                succeeded = tab.IsIndexReady;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await tab.SetDashboardLoadFailureAsync(ex.Message);
            }
            finally
            {
                tab.PropertyChanged -= ProgressChanged;
                prepared.Dispose();
                SettleFile(succeeded);
            }
        }
    }

    private void PublishCycle(InteractiveDashboardCycle cycle, bool terminal = false, bool canceled = false, bool force = false)
    {
        if (cycle.Cancellation.IsCancellationRequested && !canceled || _interactiveRevisions.GetValueOrDefault(cycle.Group.Id) != cycle.Revision)
            return;
        var now = Environment.TickCount64;
        if (!terminal && !force && now - cycle.LastPublication < ProgressPublicationIntervalMs)
            return;
        cycle.LastPublication = now;
        var group = _host.Groups.FirstOrDefault(item => item.Id == cycle.Group.Id) ?? cycle.Group;
        var phase = !terminal ? !cycle.TargetsResolved ? DashboardLoadPhase.Resolving
            : cycle.WorkStarted ? DashboardLoadPhase.Working : DashboardLoadPhase.Queued
            : canceled ? DashboardLoadPhase.Canceled : cycle.ReadyCount == 0 ? DashboardLoadPhase.Failed
            : cycle.FailedCount > 0 ? DashboardLoadPhase.ReadyWithErrors : DashboardLoadPhase.Ready;
        group.SetLoadProgress(new DashboardLoadProgress(cycle.Revision, phase, cycle.TargetCount,
            cycle.ReadyCount, cycle.SettledCount, cycle.FailedCount,
            _host.SelectedTab?.ScopeDashboardId == group.Id ? _host.SelectedTab.ActiveSession.IndexProgress : null));
        _host.NotifyDashboardLoadStateChanged();
        if (terminal && !canceled && cycle.ReadyCount > 0 && _host.ActiveDashboardId != group.Id)
            group.DashboardReadyRevision = cycle.Revision;
        if (_host.ActiveDashboardId == group.Id)
        {
            var tabProgress = _host.SelectedTab?.LoadingProgressText;
            _host.DashboardLoadingStatusText = terminal ? group.DashboardLoadStatus :
                group.DashboardLoadStatus + (tabProgress == null ? string.Empty : $" — {tabProgress}");
        }
        UpdateAggregateLoadingState();
    }

    private void UpdateAggregateLoadingState()
    {
        var interactiveDepth = _interactiveLoads.Count;
        // Preserve workspace and legacy dashboard leases in the shared depth.
        _host.DashboardLoadDepth += interactiveDepth - _publishedInteractiveLoadDepth;
        _publishedInteractiveLoadDepth = interactiveDepth;
        _host.IsDashboardLoading = _host.DashboardLoadDepth > 0;
    }

    private sealed class DashboardTransition
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PendingCount { get; set; }
    }

    private sealed class InteractiveDashboardCycle
    {
        public InteractiveDashboardCycle(LogGroupViewModel group, long revision) => (Group, Revision) = (group, revision);
        public LogGroupViewModel Group { get; }
        public long Revision { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public int TargetCount { get; set; }
        public int ReadyCount { get; set; }
        public int SettledCount { get; set; }
        public int FailedCount { get; set; }
        public bool TargetsResolved { get; set; }
        public bool WorkStarted { get; set; }
        public long LastPublication { get; set; }
    }
}
