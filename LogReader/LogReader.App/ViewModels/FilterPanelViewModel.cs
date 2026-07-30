namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed class FilterWarningViewModel
{
    public FilterWarningViewModel(string filePath, string message)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(FileName))
            FileName = filePath;

        Message = message;
    }

    public string FilePath { get; }

    public string FileName { get; }

    public string Message { get; }
}

public partial class FilterPanelViewModel : ObservableObject, IDisposable
{
    private const string CurrentTabClearedStatusText = "Filter output cleared because the selected tab changed. Reapply filter to refresh.";
    private const string CurrentTabStaleStatusText = "Filter output is for a previous tab in this scope. Reapply filter to refresh.";
    private const string AllOpenTabsStaleStatusText = "Filter output is for a previous set of open tabs. Reapply filter to refresh.";
    private const string TargetModeStaleStatusText = "Filter output is for a different target. Reapply filter to refresh.";
    private const string CurrentTabNoParseableTimestampStatusText = "No parseable timestamps found in this file for the selected time range.";
    private const string InvalidatedFilterStatusText = "Filter cleared because the file contents or encoding changed. Reapply filter to refresh.";

    private readonly ISearchService _searchService;
    private readonly ILogWorkspaceContext _mainVm;
    private readonly SearchFilterSharedOptions _sharedOptions;
    private readonly WorkspaceScopedStateStore<ScopeOwnedFilterState> _scopeStateStore;
    private readonly Dictionary<string, LogFilterSession.FilterSnapshot> _appliedScopeSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _filterOperationGate = new(1, 1);
    private readonly List<WeakReference<LogTabViewModel>> _filterInvalidationTabs = new();
    private readonly object _filterPublicationSync = new();

    private WorkspaceScopeSnapshot _activeScopeSnapshot;
    private CancellationTokenSource? _applyFilterCts;
    private LogTabViewModel? _observedTab;
    private string _baseStatusText = string.Empty;
    private bool _preferBaseStatusText;
    private string _applyingStatusText = string.Empty;
    private FilterExecutionState? _visibleOutputExecutionState;
    private ScopeOwnedFilterState? _inFlightRollbackScopeState;
    private bool _visibleOutputIsStale;
    private HashSet<string> _pendingAllOpenTabsReplayPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingDashboardRehydrationDashboardId;
    private bool _pendingDashboardRehydrationLoadStarted;
    private long _filterSnapshotInvalidationVersion;
    private int _isDisposed;

    internal event EventHandler? FilterApplicabilityChanged;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isRegex;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private bool _excludeMatches;

    [ObservableProperty]
    private string _fromTimestamp = string.Empty;

    [ObservableProperty]
    private string _toTimestamp = string.Empty;

    partial void OnQueryChanged(string value) => ClearTransientStatusPreference();

    partial void OnIsRegexChanged(bool value) => ClearTransientStatusPreference();

    partial void OnCaseSensitiveChanged(bool value) => ClearTransientStatusPreference();

    partial void OnExcludeMatchesChanged(bool value) => ClearTransientStatusPreference();

    partial void OnFromTimestampChanged(string value) => ClearTransientStatusPreference();

    partial void OnToTimestampChanged(string value) => ClearTransientStatusPreference();

    public SearchFilterTargetMode TargetMode
    {
        get => _sharedOptions.TargetMode;
        set => _sharedOptions.TargetMode = value;
    }

    [ObservableProperty]
    private bool _isApplying;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public ObservableCollection<FilterWarningViewModel> Warnings { get; } = new();

    public bool IsCurrentTabTarget
    {
        get => TargetMode == SearchFilterTargetMode.CurrentTab;
        set
        {
            if (value)
                TargetMode = SearchFilterTargetMode.CurrentTab;
        }
    }

    public bool IsAllOpenTabsTarget
    {
        get => TargetMode == SearchFilterTargetMode.AllOpenTabs;
        set
        {
            if (value)
                TargetMode = SearchFilterTargetMode.AllOpenTabs;
        }
    }

    public bool HasWarnings => Warnings.Count > 0;

    public string ClearFilterLabel => TargetMode == SearchFilterTargetMode.AllOpenTabs
        ? "Clear Open Tabs Filter"
        : "Clear Tab Filter";

    public bool AreTargetAndSourceToggleEnabled => !_mainVm.IsDashboardLoading;

    public bool AreExecutionControlsEnabled => !_mainVm.IsDashboardLoading;

    internal FilterPanelViewModel(
        ISearchService searchService,
        ILogWorkspaceContext mainVm,
        SearchFilterSharedOptions? sharedOptions = null)
    {
        _searchService = searchService;
        _mainVm = mainVm;
        _sharedOptions = sharedOptions ?? new SearchFilterSharedOptions();
        Warnings.CollectionChanged += Warnings_CollectionChanged;
        _activeScopeSnapshot = _mainVm.GetActiveScopeSnapshot();
        _scopeStateStore = new WorkspaceScopedStateStore<ScopeOwnedFilterState>(
            _activeScopeSnapshot.ScopeKey,
            static () => new ScopeOwnedFilterState(),
            CloneScopeState);
        _sharedOptions.PropertyChanged += SharedOptions_PropertyChanged;
        RestoreScopeState(_scopeStateStore.ActivateScope(_activeScopeSnapshot.ScopeKey));
        OnSelectedTabChanged(_mainVm.SelectedTab);
    }

    [RelayCommand]
    private async Task ApplyFilter()
    {
        if (_mainVm.IsDashboardLoading)
            return;

        if (string.IsNullOrWhiteSpace(Query) &&
            string.IsNullOrWhiteSpace(FromTimestamp) &&
            string.IsNullOrWhiteSpace(ToTimestamp))
        {
            SetBaseStatusText("Enter filter text or time range.");
            return;
        }

        if (!TimestampParser.TryBuildRange(FromTimestamp, ToTimestamp, out _, out var rangeError))
        {
            SetBaseStatusText(rangeError ?? "Invalid timestamp range.");
            return;
        }

        var selectedTab = _mainVm.SelectedTab;
        if (TargetMode == SearchFilterTargetMode.CurrentTab && selectedTab == null)
        {
            SetBaseStatusText("Select a file tab to apply a filter.");
            return;
        }

        CancelActiveApplySession();
        selectedTab = _mainVm.SelectedTab;
        if (TargetMode == SearchFilterTargetMode.CurrentTab && selectedTab == null)
        {
            SetBaseStatusText("Select a file tab to apply a filter.");
            return;
        }

        var previousState = CaptureCurrentScopeState();
        var rollbackScopeState = CloneScopeState(previousState);
        _inFlightRollbackScopeState = rollbackScopeState;
        _activeScopeSnapshot = _mainVm.GetActiveScopeSnapshot();

        var sessionCts = new CancellationTokenSource();
        _applyFilterCts = sessionCts;
        var invalidationVersion = Volatile.Read(ref _filterSnapshotInvalidationVersion);
        var ct = sessionCts.Token;
        _applyingStatusText = string.Empty;
        IsApplying = true;
        RefreshVisibleStatusText();

        try
        {
            if (TargetMode == SearchFilterTargetMode.AllOpenTabs)
            {
                await ApplyAllOpenTabsFilterAsync(
                    previousState,
                    _mainVm.ActiveScopeDashboardId,
                    _activeScopeSnapshot,
                    sessionCts,
                    invalidationVersion,
                    ct);
            }
            else
            {
                await ApplyCurrentTabFilterAsync(
                    previousState,
                    _mainVm.ActiveScopeDashboardId,
                    selectedTab!,
                    sessionCts,
                    invalidationVersion,
                    ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Scope exits and user cancellations keep the last committed output intact.
        }
        catch (Exception ex)
        {
            if (IsCurrentSession(sessionCts))
                SetBaseStatusText($"Filter error: {ex.Message}");
        }
        finally
        {
            if (IsCurrentSession(sessionCts))
            {
                _applyFilterCts = null;
                _applyingStatusText = string.Empty;
                IsApplying = false;
                RefreshVisibleStatusText();
                sessionCts.Dispose();
            }

            if (ReferenceEquals(_inFlightRollbackScopeState, rollbackScopeState))
                _inFlightRollbackScopeState = null;
        }
    }

    [RelayCommand]
    private async Task ClearFilter()
    {
        if (_mainVm.IsDashboardLoading)
            return;

        Query = string.Empty;
        CancelActiveApplySession();
        IsApplying = false;
        RefreshVisibleStatusText();

        var clearScopeKey = _scopeStateStore.ActiveScopeKey;
        var clearScopeDashboardId = _mainVm.ActiveScopeDashboardId;
        var clearTargetMode = TargetMode;
        var clearExecutionState = CloneExecutionState(_visibleOutputExecutionState);
        var clearAppliedPaths = _appliedScopeSnapshots.Keys.ToList();
        var clearSelectedTab = _mainVm.SelectedTab;

        await _filterOperationGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _isDisposed) != 0 ||
                !_scopeStateStore.ActiveScopeKey.Equals(clearScopeKey) ||
                !string.Equals(_mainVm.ActiveScopeDashboardId, clearScopeDashboardId, StringComparison.Ordinal))
            {
                return;
            }

            if (clearTargetMode == SearchFilterTargetMode.AllOpenTabs)
            {
                if (clearExecutionState is not AllOpenTabsExecutionState)
                    return;

                await ClearAllOpenTabsApplicationAsync(clearAppliedPaths, clearScopeDashboardId);
                ClearCommittedOutputState();
                _inFlightRollbackScopeState = null;
                SetBaseStatusText("All open tabs filter cleared.");
                return;
            }

            if (clearSelectedTab == null)
            {
                SetBaseStatusText("Select a file tab to clear filter.");
                return;
            }

            var currentTabExecutionState = clearExecutionState as CurrentTabExecutionState;
            var selectedTabMatchesExecution = currentTabExecutionState != null &&
                                              MatchesCurrentTabExecution(clearSelectedTab, currentTabExecutionState);

            await clearSelectedTab.ClearFilterAsync();

            if (!selectedTabMatchesExecution)
            {
                RefreshVisibleStatusText();
                return;
            }

            _mainVm.UpdateRecentTabFilterSnapshot(currentTabExecutionState!.FilePath, clearScopeDashboardId, null);
            ClearCommittedOutputState();
            _inFlightRollbackScopeState = null;
            SetBaseStatusText("Current tab filter cleared.");
        }
        finally
        {
            _filterOperationGate.Release();
        }
    }

    internal void OnScopeChanging(WorkspaceScopeKey nextScopeKey)
    {
        if (nextScopeKey.Equals(_scopeStateStore.ActiveScopeKey))
            return;

        var activeState = _inFlightRollbackScopeState == null
            ? CaptureCurrentScopeState()
            : CloneScopeState(_inFlightRollbackScopeState);
        if (IsApplying)
        {
            CancelActiveApplySession();
            IsApplying = false;
        }

        _scopeStateStore.BeginScopeChange(nextScopeKey, activeState);
    }

    internal void OnScopeContextChanged()
    {
        var scopeSnapshot = _mainVm.GetActiveScopeSnapshot();
        _activeScopeSnapshot = scopeSnapshot;
        if (scopeSnapshot.ScopeKey.Equals(_scopeStateStore.ActiveScopeKey) &&
            _scopeStateStore.PendingScopeKey == null)
        {
            RefreshVisibleStatusText();
            return;
        }

        RestoreScopeState(_scopeStateStore.ActivateScope(scopeSnapshot.ScopeKey));
    }

    internal void ResetScopeState(WorkspaceScopeKey scopeKey)
    {
        _scopeStateStore.ResetScope(scopeKey);
        if (!scopeKey.Equals(_scopeStateStore.ActiveScopeKey))
            return;

        CancelActiveApplySession();
        IsApplying = false;
        _activeScopeSnapshot = _mainVm.GetActiveScopeSnapshot();
        RestoreScopeState(new ScopeOwnedFilterState());
    }

    public void OnSelectedTabChanged(LogTabViewModel? selectedTab)
    {
        if (selectedTab != null)
            ObserveFilterInvalidation(selectedTab);

        if (!ReferenceEquals(_observedTab, selectedTab))
        {
            _preferBaseStatusText = false;
            if (_observedTab != null)
                _observedTab.PropertyChanged -= SelectedTab_PropertyChanged;

            _observedTab = selectedTab;
            if (_observedTab != null)
                _observedTab.PropertyChanged += SelectedTab_PropertyChanged;
        }

        if (_scopeStateStore.PendingScopeKey != null)
            return;

        ApplyVisibleOutputInvalidationIfNeeded();
        RefreshVisibleStatusText();
    }

    internal async Task MaterializeStoredFilterStateAsync(LogTabViewModel tab, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ObserveFilterInvalidation(tab);

        await _filterOperationGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _isDisposed) != 0)
                return;

            var scopeState = GetMaterializationState(WorkspaceScopeKey.FromDashboardId(tab.ScopeDashboardId));
            if (scopeState == null || scopeState.ExecutionState is not AllOpenTabsExecutionState executionState)
                return;

            var matchesExecution = MatchesAllOpenTabsExecution(tab.ScopeDashboardId, executionState);
            var shouldSuppressStoredScopeOutput = scopeState.IsOutputStale && !matchesExecution;
            if (shouldSuppressStoredScopeOutput)
            {
                if (scopeState.AppliedScopeSnapshots.ContainsKey(tab.FilePath))
                {
                    _pendingAllOpenTabsReplayPaths.Add(tab.FilePath);
                    if (tab.IsFilterActive)
                    {
                        await tab.ClearFilterAsync();
                        _mainVm.UpdateRecentTabFilterSnapshot(tab.FilePath, tab.ScopeDashboardId, null);
                    }
                }

                return;
            }

            if (matchesExecution &&
                _pendingAllOpenTabsReplayPaths.Count > 0)
            {
                await ReplayDeferredAllOpenTabsSnapshotsAsync(scopeState, tab.ScopeDashboardId, ct);
            }

            if (!scopeState.AppliedScopeSnapshots.TryGetValue(tab.FilePath, out var snapshot))
                return;

            var priorSnapshot = tab.CaptureActiveFilterSnapshot();
            if (!await tab.RestoreFilterSnapshotAsync(snapshot, ct))
                RejectStoredFilterSnapshot(scopeState, tab.FilePath, tab.ScopeDashboardId, priorSnapshot);
        }
        finally
        {
            _filterOperationGate.Release();
        }
    }

    internal void CaptureStoredFilterStateBeforeTabClose(LogTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (_visibleOutputExecutionState is not AllOpenTabsExecutionState ||
            !string.Equals(tab.ScopeDashboardId, _mainVm.ActiveScopeDashboardId, StringComparison.Ordinal) ||
            !_appliedScopeSnapshots.ContainsKey(tab.FilePath) ||
            !tab.IsFilterActive)
        {
            return;
        }

        var snapshot = tab.CaptureActiveFilterSnapshot();
        if (snapshot != null)
            _appliedScopeSnapshots[tab.FilePath] = LogFilterSession.CloneSnapshot(snapshot);
    }

    internal LogFilterSession.FilterSnapshot? GetApplicableCurrentTabFilterSnapshot(SearchDataMode sourceMode)
    {
        if (_visibleOutputExecutionState is not CurrentTabExecutionState currentTabExecutionState)
            return null;

        var selectedTab = _mainVm.SelectedTab;
        if (selectedTab == null || !MatchesCurrentTabExecution(selectedTab, currentTabExecutionState) || !selectedTab.IsFilterActive)
            return null;

        var snapshot = selectedTab.CaptureActiveFilterSnapshot();
        return snapshot != null &&
               selectedTab.IsFilterSnapshotCompatible(snapshot) &&
               SnapshotMatchesSourceMode(snapshot, sourceMode)
            ? LogFilterSession.CloneSnapshot(snapshot!)
            : null;
    }

    internal IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableAllOpenTabsFilterSnapshots(SearchDataMode sourceMode)
    {
        if (_visibleOutputIsStale)
            return new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);

        if (_visibleOutputExecutionState is not AllOpenTabsExecutionState allOpenTabsExecutionState ||
            !MatchesAllOpenTabsExecution(allOpenTabsExecutionState))
        {
            return new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        var results = new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in _appliedScopeSnapshots.Keys)
        {
            var snapshot = GetApplicableAllOpenTabsFilterSnapshot(filePath, sourceMode);
            if (snapshot != null)
                results[filePath] = snapshot;
        }

        return results;
    }

    internal LogFilterSession.FilterSnapshot? GetApplicableAllOpenTabsFilterSnapshot(string filePath, SearchDataMode sourceMode)
    {
        if (_visibleOutputIsStale)
            return null;

        if (_visibleOutputExecutionState is not AllOpenTabsExecutionState allOpenTabsExecutionState ||
            !MatchesAllOpenTabsExecution(allOpenTabsExecutionState))
        {
            return null;
        }

        var openTabs = GetOpenTabsForScopeApplication(filePath, _mainVm.ActiveScopeDashboardId).ToList();
        foreach (var openTab in openTabs)
        {
            var liveSnapshot = openTab.CaptureActiveFilterSnapshot();
            if (liveSnapshot != null &&
                openTab.IsFilterSnapshotCompatible(liveSnapshot) &&
                SnapshotMatchesSourceMode(liveSnapshot, sourceMode))
            {
                return LogFilterSession.CloneSnapshot(liveSnapshot);
            }
        }

        if (openTabs.Count > 0 || !_appliedScopeSnapshots.TryGetValue(filePath, out var effectiveSnapshot))
            return null;

        return SnapshotMatchesSourceMode(effectiveSnapshot, sourceMode)
            ? LogFilterSession.CloneSnapshot(effectiveSnapshot!)
            : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        var stateToPersist = _inFlightRollbackScopeState == null
            ? CaptureCurrentScopeState()
            : CloneScopeState(_inFlightRollbackScopeState);
        _scopeStateStore.Persist(stateToPersist);
        CancelActiveApplySession();
        IsApplying = false;
        if (_observedTab != null)
            _observedTab.PropertyChanged -= SelectedTab_PropertyChanged;

        foreach (var tabReference in _filterInvalidationTabs)
        {
            if (tabReference.TryGetTarget(out var tab))
                tab.FilterSnapshotInvalidated -= Tab_FilterSnapshotInvalidated;
        }
        _filterInvalidationTabs.Clear();

        Warnings.CollectionChanged -= Warnings_CollectionChanged;
        _sharedOptions.PropertyChanged -= SharedOptions_PropertyChanged;
    }

    internal void RefreshLoadFreezeState()
    {
        OnPropertyChanged(nameof(AreTargetAndSourceToggleEnabled));
        OnPropertyChanged(nameof(AreExecutionControlsEnabled));
        RefreshVisibleStatusText();
    }

    private async Task ApplyCurrentTabFilterAsync(
        ScopeOwnedFilterState previousState,
        string? scopeDashboardId,
        LogTabViewModel selectedTab,
        CancellationTokenSource sessionCts,
        long invalidationVersion,
        CancellationToken ct)
    {
        var target = FilterEvaluationTarget.Capture(selectedTab);
        ObserveFilterInvalidation(selectedTab);
        var request = CreateFilterSearchRequest(new[] { selectedTab.FilePath });
        var result = await _searchService.FilterFileAsync(selectedTab.FilePath, request, target.Encoding, ct);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            SetBaseStatusText($"Filter error: {result.Error}");
            return;
        }

        var evaluatedThroughLine = Math.Max(0, result.EvaluatedThroughLine ?? target.TotalLines);
        var matchingLineNumbers = result.MatchingLineNumbers;
        var statusText = BuildPerFileStatusText(
            request,
            result,
            matchingLineNumbers.Count,
            evaluatedThroughLine,
            ExcludeMatches);
        var snapshot = CreateFilterSnapshot(
            matchingLineNumbers,
            statusText,
            request,
            result.HasParseableTimestamps,
            evaluatedThroughLine,
            GetLineSetMode(),
            result.GenerationEvidence,
            target);
        if (!selectedTab.IsFilterSnapshotCompatible(snapshot))
        {
            SetBaseStatusText("Filter error: file content changed while the filter was being evaluated.");
            return;
        }

        await _filterOperationGate.WaitAsync(ct);
        try
        {
            if (!CanMutateForSession(sessionCts, ct))
                return;

            var rollbackState = CaptureFilterRollbackState(
                previousState,
                scopeDashboardId,
                new[] { selectedTab.FilePath });
            CaptureRollbackTab(rollbackState, selectedTab);
            var applied = await selectedTab.TryCommitFilterSnapshotAsync(snapshot, ct);
            if (!applied)
            {
                SetBaseStatusText("Filter error: file content changed before the filter could be applied.");
                return;

            }

            var applicationPublished = false;
            try
            {
                if (!CanMutateForSession(sessionCts, ct))
                    return;

                var preservedTabs = new HashSet<LogTabViewModel>(ReferenceEqualityComparer.Instance) { selectedTab };
                await ClearAppliedFilterStateAsync(previousState, scopeDashboardId, preservedTabs, rollbackState);
                if (!CanMutateForSession(sessionCts, ct))
                    return;

                await selectedTab.RefreshCommittedFilterAsync(ct);
                statusText = selectedTab.ActiveFilterStatusText ?? statusText;

                lock (_filterPublicationSync)
                {
                    if (!TryCapturePublishableSnapshot(
                            selectedTab,
                            sessionCts,
                            invalidationVersion,
                            ct,
                            out var committedSnapshot))
                    {
                        return;
                    }

                    foreach (var filePath in rollbackState.RecentSnapshotsByPath.Keys)
                    {
                        _mainVm.UpdateRecentTabFilterSnapshot(
                            filePath,
                            scopeDashboardId,
                            string.Equals(filePath, selectedTab.FilePath, StringComparison.OrdinalIgnoreCase)
                                ? committedSnapshot
                                : null);
                    }

                    if (!CanPublishForSession(sessionCts, invalidationVersion, ct))
                        return;

                    ClearCommittedOutputState();
                    _baseStatusText = statusText;
                    _preferBaseStatusText = false;
                    _visibleOutputExecutionState = new CurrentTabExecutionState(selectedTab.TabInstanceId, selectedTab.FilePath);
                    applicationPublished = true;
                    _inFlightRollbackScopeState = null;
                    RaiseFilterApplicabilityChanged();
                    RefreshVisibleStatusText();
                }
            }
            finally
            {
                if (!applicationPublished)
                    await RestoreFilterRollbackStateAsync(rollbackState);
            }
        }
        finally
        {
            _filterOperationGate.Release();
        }
    }

    private async Task ApplyAllOpenTabsFilterAsync(
        ScopeOwnedFilterState previousState,
        string? scopeDashboardId,
        WorkspaceScopeSnapshot scopeSnapshot,
        CancellationTokenSource sessionCts,
        long invalidationVersion,
        CancellationToken ct)
    {
        var targets = WorkspaceScopeOrdering.GetDistinctOrderedOpenTabs(scopeSnapshot.OpenTabs)
            .Select(openTab => FilterEvaluationTarget.Capture(openTab.Tab))
            .ToList();
        foreach (var target in targets)
            ObserveFilterInvalidation(target.Tab);
        var targetPaths = targets
            .Select(target => target.FilePath)
            .ToList();

        if (targetPaths.Count == 0)
        {
            await _filterOperationGate.WaitAsync(ct);
            try
            {
                if (!CanMutateForSession(sessionCts, ct))
                    return;

                var rollbackState = CaptureFilterRollbackState(previousState, scopeDashboardId, targetPaths);
                var applicationPublished = false;
                try
                {
                    await ClearAppliedFilterStateAsync(previousState, scopeDashboardId, rollbackState: rollbackState);
                    lock (_filterPublicationSync)
                    {
                        if (!CanPublishForSession(sessionCts, invalidationVersion, ct))
                            return;

                        foreach (var filePath in rollbackState.RecentSnapshotsByPath.Keys)
                            _mainVm.UpdateRecentTabFilterSnapshot(filePath, scopeDashboardId, null);

                        if (!CanPublishForSession(sessionCts, invalidationVersion, ct))
                            return;

                        ClearCommittedOutputState();
                        _baseStatusText = "No open tabs to filter.";
                        _preferBaseStatusText = false;
                        _visibleOutputExecutionState = new AllOpenTabsExecutionState(targetPaths);
                        applicationPublished = true;
                        _inFlightRollbackScopeState = null;
                        RaiseFilterApplicabilityChanged();
                        RefreshVisibleStatusText();
                    }
                }
                finally
                {
                    if (!applicationPublished)
                        await RestoreFilterRollbackStateAsync(rollbackState);
                }
            }
            finally
            {
                _filterOperationGate.Release();
            }

            return;
        }

        var encodings = targets.ToDictionary(
            target => target.FilePath,
            target => target.Encoding,
            StringComparer.OrdinalIgnoreCase);

        var request = CreateFilterSearchRequest(targetPaths);
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.FilterApply,
            targetPaths);
        _applyingStatusText = AdaptiveParallelismDiagnostics.BuildOperationStatus(
            "Applying filter to",
            targetPaths.Count,
            "tab",
            plan);
        RefreshVisibleStatusText();

        var results = await _searchService.FilterFilesAsync(request, encodings, ct);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        var resultsByPath = results
            .Where(result => !string.IsNullOrWhiteSpace(result.FilePath))
            .GroupBy(result => result.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var warnings = new List<FilterWarningState>();
        var candidateSnapshots = new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in targetPaths)
        {
            if (!resultsByPath.TryGetValue(filePath, out var result))
                result = new FilterResult { FilePath = filePath };

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                warnings.Add(new FilterWarningState(filePath, $"Filter error: {result.Error}"));
                continue;
            }

            var matchingLineNumbers = result.MatchingLineNumbers;
            var target = targets
                .First(target => string.Equals(target.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            var evaluatedThroughLine = Math.Max(0, result.EvaluatedThroughLine ?? target.TotalLines);
            var statusText = BuildPerFileStatusText(
                request,
                result,
                matchingLineNumbers.Count,
                evaluatedThroughLine,
                ExcludeMatches);
            if (HasTimestampRange(request) && !result.HasParseableTimestamps)
                warnings.Add(new FilterWarningState(filePath, "No parseable timestamps found for the selected time range."));

            var snapshot = CreateFilterSnapshot(
                matchingLineNumbers,
                statusText,
                request,
                result.HasParseableTimestamps,
                evaluatedThroughLine,
                GetLineSetMode(),
                result.GenerationEvidence,
                target);
            if (!target.Tab.IsFilterSnapshotCompatible(snapshot))
            {
                warnings.Add(new FilterWarningState(filePath, "Filter error: file content changed while the filter was being evaluated."));
                continue;
            }

            candidateSnapshots[filePath] = snapshot;
        }

        await _filterOperationGate.WaitAsync(ct);
        try
        {
            if (!CanMutateForSession(sessionCts, ct))
                return;

            var rollbackState = CaptureFilterRollbackState(previousState, scopeDashboardId, targetPaths);
            var applicationPublished = false;
            try
            {
                var committedTabsByPath = new Dictionary<string, List<LogTabViewModel>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (filePath, snapshot) in candidateSnapshots)
                {
                    var applicationTabs = GetOpenTabsForScopeApplication(filePath, scopeDashboardId).ToList();
                    if (applicationTabs.Count == 0)
                    {
                        warnings.Add(new FilterWarningState(filePath, "Filter error: the target tab closed before the filter could be applied."));
                        continue;
                    }

                    var committedTabs = new List<LogTabViewModel>(applicationTabs.Count);
                    var pathCommitted = true;
                    try
                    {
                        foreach (var openTab in applicationTabs)
                        {
                            ObserveFilterInvalidation(openTab);
                            CaptureRollbackTab(rollbackState, openTab);
                            if (!openTab.IsFilterSnapshotCompatible(snapshot) ||
                                !await openTab.TryCommitFilterSnapshotAsync(snapshot, ct))
                            {
                                pathCommitted = false;
                                break;
                            }

                            committedTabs.Add(openTab);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(new FilterWarningState(filePath, $"Filter error: {ex.Message}"));
                        pathCommitted = false;
                    }

                    if (!pathCommitted)
                    {
                        await RestoreFilterRollbackStateAsync(
                            CaptureRollbackSubset(rollbackState.OpenTabSnapshots, committedTabs));

                        if (!warnings.Any(warning =>
                                string.Equals(warning.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                                warning.Message.Contains("Filter error:", StringComparison.Ordinal)))
                        {
                            warnings.Add(new FilterWarningState(filePath, "Filter error: file content changed before the filter could be applied."));
                        }

                        continue;
                    }

                    committedTabsByPath[filePath] = committedTabs;
                }

                var preservedTabs = new HashSet<LogTabViewModel>(
                    committedTabsByPath.Values.SelectMany(tabs => tabs),
                    ReferenceEqualityComparer.Instance);
                await ClearAppliedFilterStateAsync(previousState, scopeDashboardId, preservedTabs, rollbackState);
                if (!CanMutateForSession(sessionCts, ct))
                    return;

                await ClearAllOpenTabsApplicationAsync(targetPaths, scopeDashboardId, preservedTabs, rollbackState);
                if (!CanMutateForSession(sessionCts, ct))
                    return;

                var appliedSnapshots = new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);
                foreach (var (filePath, committedTabs) in committedTabsByPath)
                {
                    var committedSnapshot = committedTabs
                        .Select(tab => tab.CaptureActiveFilterSnapshot())
                        .FirstOrDefault(snapshot => snapshot != null);
                    if (committedSnapshot == null)
                    {
                        await RestoreFilterRollbackStateAsync(
                            CaptureRollbackSubset(rollbackState.OpenTabSnapshots, committedTabs));

                        warnings.Add(new FilterWarningState(filePath, "Filter error: the committed filter state could not be retained."));
                        continue;
                    }

                    appliedSnapshots[filePath] = LogFilterSession.CloneSnapshot(committedSnapshot);
                    foreach (var committedTab in committedTabs)
                    {
                        try
                        {
                            await committedTab.RefreshCommittedFilterAsync(ct);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            warnings.Add(new FilterWarningState(filePath, $"Filter viewport error: {ex.Message}"));
                        }
                    }
                }

                lock (_filterPublicationSync)
                {
                    if (!CanPublishForSession(sessionCts, invalidationVersion, ct) ||
                        !TryRefreshPublishableSnapshots(committedTabsByPath, appliedSnapshots))
                    {
                        return;
                    }

                    foreach (var filePath in rollbackState.RecentSnapshotsByPath.Keys
                                 .Concat(targetPaths)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        _mainVm.UpdateRecentTabFilterSnapshot(
                            filePath,
                            scopeDashboardId,
                            appliedSnapshots.TryGetValue(filePath, out var appliedSnapshot)
                                ? appliedSnapshot
                                : null);
                    }

                    if (!CanPublishForSession(sessionCts, invalidationVersion, ct))
                        return;

                    ClearCommittedOutputState();
                    foreach (var (filePath, snapshot) in appliedSnapshots)
                        _appliedScopeSnapshots[filePath] = LogFilterSession.CloneSnapshot(snapshot);

                    RestoreWarnings(warnings);
                    _baseStatusText = BuildScopeSummary(appliedSnapshots.Count, appliedSnapshots.Values.Sum(GetSnapshotDisplayCount), warnings.Count);
                    _preferBaseStatusText = false;
                    _visibleOutputExecutionState = new AllOpenTabsExecutionState(targetPaths);
                    applicationPublished = true;
                    _inFlightRollbackScopeState = null;
                    RaiseFilterApplicabilityChanged();
                    RefreshVisibleStatusText();
                }
            }
            finally
            {
                if (!applicationPublished)
                    await RestoreFilterRollbackStateAsync(rollbackState);
            }
        }
        finally
        {
            _filterOperationGate.Release();
        }
    }

    private async Task ClearAppliedFilterStateAsync(
        ScopeOwnedFilterState state,
        string? scopeDashboardId,
        IReadOnlySet<LogTabViewModel>? preservedTabs = null,
        FilterRollbackState? rollbackState = null)
    {
        switch (state.ExecutionState)
        {
            case CurrentTabExecutionState currentTabExecutionState:
                await ClearCurrentTabApplicationAsync(
                    currentTabExecutionState.FilePath,
                    scopeDashboardId,
                    preservedTabs,
                    rollbackState);
                break;

            case AllOpenTabsExecutionState:
                await ClearAllOpenTabsApplicationAsync(
                    state.AppliedScopeSnapshots.Keys,
                    scopeDashboardId,
                    preservedTabs,
                    rollbackState);
                break;
        }
    }

    private async Task ClearCurrentTabApplicationAsync(
        string filePath,
        string? scopeDashboardId,
        IReadOnlySet<LogTabViewModel>? preservedTabs = null,
        FilterRollbackState? rollbackState = null)
    {
        foreach (var tab in GetOpenTabsInScope(filePath, scopeDashboardId))
        {
            if (tab.IsFilterActive && preservedTabs?.Contains(tab) != true)
            {
                if (rollbackState != null)
                    CaptureRollbackTab(rollbackState, tab);
                await tab.ClearFilterAsync();
            }
        }

        if (rollbackState == null)
            _mainVm.UpdateRecentTabFilterSnapshot(filePath, scopeDashboardId, null);
    }

    private async Task ClearAllOpenTabsApplicationAsync(
        IEnumerable<string> filePaths,
        string? scopeDashboardId,
        IReadOnlySet<LogTabViewModel>? preservedTabs = null,
        FilterRollbackState? rollbackState = null)
    {
        var normalizedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalizedPaths.Count == 0)
            return;

        if (rollbackState == null)
        {
            foreach (var path in normalizedPaths)
                _mainVm.UpdateRecentTabFilterSnapshot(path, scopeDashboardId, null);
        }

        foreach (var tab in _mainVm.GetAllTabs().Where(tab =>
                     string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal) &&
                     normalizedPaths.Contains(tab.FilePath)))
        {
            if (tab.IsFilterActive && preservedTabs?.Contains(tab) != true)
            {
                if (rollbackState != null)
                    CaptureRollbackTab(rollbackState, tab);
                await tab.ClearFilterAsync();
            }
        }
    }

    private IEnumerable<LogTabViewModel> GetOpenTabsInScope(string filePath, string? scopeDashboardId)
    {
        return _mainVm.GetAllTabs().Where(tab =>
            string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal) &&
            string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<LogTabViewModel> GetOpenTabsForScopeApplication(string filePath, string? scopeDashboardId)
        => GetOpenTabsInScope(filePath, scopeDashboardId);

    private FilterRollbackState CaptureFilterRollbackState(
        ScopeOwnedFilterState previousState,
        string? scopeDashboardId,
        IEnumerable<string> targetPaths)
    {
        var affectedPaths = targetPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        switch (previousState.ExecutionState)
        {
            case CurrentTabExecutionState currentTab:
                affectedPaths.Add(currentTab.FilePath);
                break;
            case AllOpenTabsExecutionState:
                affectedPaths.UnionWith(previousState.AppliedScopeSnapshots.Keys);
                break;
        }

        var rollbackState = new Dictionary<LogTabViewModel, LogFilterSession.FilterSnapshot?>(
            ReferenceEqualityComparer.Instance);
        foreach (var tab in _mainVm.GetAllTabs().Where(tab =>
                     string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal) &&
                     affectedPaths.Contains(tab.FilePath)))
        {
            rollbackState[tab] = tab.CaptureActiveFilterSnapshot();
        }

        var recentSnapshots = new Dictionary<string, LogFilterSession.FilterSnapshot?>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in affectedPaths)
        {
            if (previousState.AppliedScopeSnapshots.TryGetValue(filePath, out var storedSnapshot))
            {
                recentSnapshots[filePath] = LogFilterSession.CloneSnapshot(storedSnapshot);
                continue;
            }

            recentSnapshots[filePath] = rollbackState
                .Where(entry => string.Equals(entry.Key.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Value)
                .FirstOrDefault(snapshot => snapshot != null);
        }

        return new FilterRollbackState(rollbackState, recentSnapshots, scopeDashboardId);
    }

    private async Task RestoreFilterRollbackStateAsync(FilterRollbackState rollbackState)
    {
        var rejectedPaths = await RestoreFilterRollbackStateAsync(rollbackState.OpenTabSnapshots);
        if (Volatile.Read(ref _isDisposed) != 0)
            return;

        foreach (var (filePath, snapshot) in rollbackState.RecentSnapshotsByPath)
        {
            _mainVm.UpdateRecentTabFilterSnapshot(
                filePath,
                rollbackState.ScopeDashboardId,
                rejectedPaths.Contains(filePath) ? null : snapshot);
        }
    }

    private static void CaptureRollbackTab(FilterRollbackState rollbackState, LogTabViewModel tab)
    {
        if (rollbackState.OpenTabSnapshots.ContainsKey(tab))
            return;

        var snapshot = tab.CaptureActiveFilterSnapshot();
        rollbackState.OpenTabSnapshots[tab] = snapshot;
        if (!rollbackState.RecentSnapshotsByPath.ContainsKey(tab.FilePath))
        {
            rollbackState.RecentSnapshotsByPath[tab.FilePath] = snapshot == null
                ? null
                : LogFilterSession.CloneSnapshot(snapshot);
        }
    }

    private async Task<HashSet<string>> RestoreFilterRollbackStateAsync(
        IReadOnlyDictionary<LogTabViewModel, LogFilterSession.FilterSnapshot?> rollbackState)
    {
        var rejectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tab, snapshot) in rollbackState)
        {
            if (tab.IsShutdownOrDisposed)
                continue;

            try
            {
                if (snapshot == null)
                {
                    if (tab.IsFilterActive)
                        await tab.ClearFilterAsync();
                    continue;
                }

                var restored = await tab.RestoreFilterSnapshotAsync(snapshot, CancellationToken.None);
                if (!restored)
                {
                    if (tab.IsFilterActive)
                        await tab.ClearFilterAsync();
                    rejectedPaths.Add(tab.FilePath);
                    continue;
                }
            }
            catch (Exception ex) when (ex is not StackOverflowException and not OutOfMemoryException)
            {
                // Rollback is best-effort per tab so one unavailable file cannot block the remaining repairs.
                rejectedPaths.Add(tab.FilePath);
            }
        }

        return rejectedPaths;
    }

    private static IReadOnlyDictionary<LogTabViewModel, LogFilterSession.FilterSnapshot?> CaptureRollbackSubset(
        IReadOnlyDictionary<LogTabViewModel, LogFilterSession.FilterSnapshot?> rollbackState,
        IEnumerable<LogTabViewModel> tabs)
    {
        var subset = new Dictionary<LogTabViewModel, LogFilterSession.FilterSnapshot?>(
            ReferenceEqualityComparer.Instance);
        foreach (var tab in tabs)
        {
            if (rollbackState.TryGetValue(tab, out var snapshot))
                subset[tab] = snapshot;
        }

        return subset;
    }

    private SearchRequest CreateFilterSearchRequest(IReadOnlyList<string> filePaths)
        => CreateSearchRequest(filePaths);

    private SearchRequest CreateSearchRequest(IReadOnlyList<string> filePaths)
    {
        return SearchRequest.Create(
            Query,
            IsRegex,
            CaseSensitive,
            filePaths,
            SearchRequestSourceMode.SnapshotAndTail,
            SearchRequestUsage.FilterApply,
            FromTimestamp,
            ToTimestamp);
    }

    private static string BuildPerFileStatusText(SearchRequest request, FilterResult result, int matchingLineCount, int totalLines, bool excludeMatches)
    {
        if (!excludeMatches && HasTimestampRange(request) && !result.HasParseableTimestamps)
            return CurrentTabNoParseableTimestampStatusText;

        var displayLineCount = excludeMatches
            ? Math.Max(0, totalLines - matchingLineCount)
            : matchingLineCount;
        return $"Filter active: {displayLineCount:N0} matching lines.";
    }

    private static string BuildScopeSummary(int appliedFileCount, int totalDisplayedLines, int warningCount)
    {
        var prefix = "Filter active";

        if (appliedFileCount == 0)
        {
            return warningCount > 0
                ? $"{prefix} completed with {warningCount:N0} warning(s). No open tabs were filtered."
                : "No open tabs were filtered.";
        }

        var summary = $"{prefix} across {appliedFileCount:N0} open tab(s): {totalDisplayedLines:N0} matching lines.";
        if (warningCount > 0)
            summary += $" {warningCount:N0} warning(s).";

        return summary;
    }

    private static bool HasTimestampRange(SearchRequest request)
        => request.FromTimestamp != null || request.ToTimestamp != null;

    private FilterLineSetMode GetLineSetMode()
        => ExcludeMatches ? FilterLineSetMode.ExcludeMatching : FilterLineSetMode.IncludeMatching;

    private static int GetSnapshotDisplayCount(LogFilterSession.FilterSnapshot snapshot)
        => snapshot.LineSetMode == FilterLineSetMode.ExcludeMatching
            ? Math.Max(0, (snapshot.TotalLinesAtSnapshot ?? 0) - snapshot.MatchingLineNumbers.Count)
            : snapshot.MatchingLineNumbers.Count;

    private static LogFilterSession.FilterSnapshot CreateFilterSnapshot(
        IReadOnlyList<int> matchingLineNumbers,
        string statusText,
        SearchRequest request,
        bool hasParseableTimestamps,
        int evaluatedThroughLine,
        FilterLineSetMode lineSetMode,
        FileScanGenerationEvidence generationEvidence,
        FilterEvaluationTarget target)
    {
        return new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = matchingLineNumbers.ToList(),
            StatusText = statusText,
            FilterRequest = CloneSearchRequest(request),
            HasSeenParseableTimestamp = hasParseableTimestamps,
            TotalLinesAtSnapshot = evaluatedThroughLine,
            LastEvaluatedLine = evaluatedThroughLine,
            LineSetMode = lineSetMode,
            GenerationEvidence = generationEvidence,
            CorrelatedTabInstanceId = target.TabInstanceId,
            CorrelatedSearchContentVersion = target.SearchContentVersion,
            EvaluatedEncoding = target.Encoding
        };
    }

    private ScopeOwnedFilterState? GetMaterializationState(WorkspaceScopeKey scopeKey)
        => _scopeStateStore.TryGetScopeState(scopeKey, CaptureCurrentScopeState);

    private ScopeOwnedFilterState CaptureCurrentScopeState()
    {
        return new ScopeOwnedFilterState
        {
            Query = Query,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            ExcludeMatches = ExcludeMatches,
            FromTimestamp = FromTimestamp,
            ToTimestamp = ToTimestamp,
            TargetMode = TargetMode,
            BaseStatusText = GetPersistableBaseStatusText(),
            ApplyingStatusText = _applyingStatusText,
            ExecutionState = CloneExecutionState(_visibleOutputExecutionState),
            Warnings = Warnings
                .Select(warning => new FilterWarningState(warning.FilePath, warning.Message))
                .ToList(),
            IsOutputStale = _visibleOutputIsStale,
            PendingAllOpenTabsReplayPaths = _pendingAllOpenTabsReplayPaths.ToList(),
            AppliedScopeSnapshots = CaptureLatestAppliedScopeSnapshots()
        };
    }

    private Dictionary<string, LogFilterSession.FilterSnapshot> CaptureLatestAppliedScopeSnapshots()
    {
        var snapshots = new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, storedSnapshot) in _appliedScopeSnapshots)
        {
            var openTabs = GetOpenTabsForScopeApplication(filePath, _mainVm.ActiveScopeDashboardId).ToList();
            var liveSnapshots = openTabs
                .Select(tab => (Tab: tab, Snapshot: tab.CaptureActiveFilterSnapshot()))
                .ToList();
            var liveSnapshot = liveSnapshots
                .FirstOrDefault(candidate =>
                    candidate.Snapshot != null &&
                    candidate.Tab.IsFilterSnapshotCompatible(candidate.Snapshot));
            if (liveSnapshot.Snapshot != null)
                snapshots[filePath] = LogFilterSession.CloneSnapshot(liveSnapshot.Snapshot);
            else if (liveSnapshots.All(candidate => candidate.Snapshot == null))
                snapshots[filePath] = LogFilterSession.CloneSnapshot(storedSnapshot);
        }

        return snapshots;
    }

    private string GetPersistableBaseStatusText()
    {
        if (!_preferBaseStatusText)
            return _baseStatusText;

        if (_visibleOutputExecutionState is CurrentTabExecutionState currentTabExecutionState &&
            _mainVm.SelectedTab != null &&
            MatchesCurrentTabExecution(_mainVm.SelectedTab, currentTabExecutionState) &&
            _mainVm.SelectedTab.IsFilterActive)
        {
            return _mainVm.SelectedTab.ActiveFilterStatusText ?? _baseStatusText;
        }

        if (_visibleOutputExecutionState is AllOpenTabsExecutionState && _appliedScopeSnapshots.Count > 0)
        {
            return BuildScopeSummary(
                _appliedScopeSnapshots.Count,
                _appliedScopeSnapshots.Values.Sum(GetSnapshotDisplayCount),
                Warnings.Count);
        }

        return _baseStatusText;
    }

    private void RestoreScopeState(ScopeOwnedFilterState state)
    {
        Query = state.Query;
        IsRegex = state.IsRegex;
        CaseSensitive = state.CaseSensitive;
        ExcludeMatches = state.ExcludeMatches;
        FromTimestamp = state.FromTimestamp;
        ToTimestamp = state.ToTimestamp;
        TargetMode = state.TargetMode;

        ClearCommittedOutputState();
        _baseStatusText = state.BaseStatusText;
        _preferBaseStatusText = false;
        _applyingStatusText = state.ApplyingStatusText;
        _visibleOutputExecutionState = CloneExecutionState(state.ExecutionState);
        _visibleOutputIsStale = state.IsOutputStale;
        _pendingAllOpenTabsReplayPaths = state.PendingAllOpenTabsReplayPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, snapshot) in state.AppliedScopeSnapshots)
            _appliedScopeSnapshots[filePath] = LogFilterSession.CloneSnapshot(snapshot);

        RestoreWarnings(state.Warnings);
        ArmPendingDashboardRehydrationIfNeeded();
        IsApplying = false;
        ApplyVisibleOutputInvalidationIfNeeded();
        RaiseFilterApplicabilityChanged();
        RefreshVisibleStatusText();
    }

    private void ClearCommittedOutputState()
    {
        _baseStatusText = string.Empty;
        _preferBaseStatusText = false;
        _applyingStatusText = string.Empty;
        _visibleOutputExecutionState = null;
        _visibleOutputIsStale = false;
        _pendingAllOpenTabsReplayPaths.Clear();
        ClearPendingDashboardRehydration();
        _appliedScopeSnapshots.Clear();
        RestoreWarnings(Array.Empty<FilterWarningState>());
        RaiseFilterApplicabilityChanged();
    }

    private void RestoreWarnings(IEnumerable<FilterWarningState> warnings)
    {
        Warnings.Clear();
        foreach (var warning in warnings)
            Warnings.Add(new FilterWarningViewModel(warning.FilePath, warning.Message));
    }

    private void SetBaseStatusText(string statusText)
    {
        _baseStatusText = statusText;
        _preferBaseStatusText = true;
        RefreshVisibleStatusText();
    }

    private void ClearTransientStatusPreference()
    {
        if (!_preferBaseStatusText)
            return;

        _preferBaseStatusText = false;
        RefreshVisibleStatusText();
    }

    private void RefreshVisibleStatusText()
    {
        UpdateVisibleOutputStaleState();
        StatusText = GetVisibleStatusText();
    }

    private string GetVisibleStatusText()
    {
        if (IsApplying)
            return !string.IsNullOrWhiteSpace(_applyingStatusText)
                ? _applyingStatusText
                : TargetMode == SearchFilterTargetMode.AllOpenTabs
                ? "Applying filter to all open tabs..."
                : "Applying filter to current tab...";

        if (_preferBaseStatusText)
            return _baseStatusText;

        if (_visibleOutputIsStale && _visibleOutputExecutionState is AllOpenTabsExecutionState)
            return AllOpenTabsStaleStatusText;

        if (_visibleOutputExecutionState is CurrentTabExecutionState currentTabExecutionState &&
            !MatchesCurrentTabExecution(currentTabExecutionState))
        {
            return CurrentTabStaleStatusText;
        }

        if (_visibleOutputExecutionState is AllOpenTabsExecutionState allOpenTabsExecutionState &&
            !MatchesAllOpenTabsExecution(allOpenTabsExecutionState) &&
            !ShouldDeferAllOpenTabsMismatchStalePromotion(allOpenTabsExecutionState))
        {
            return AllOpenTabsStaleStatusText;
        }

        if (HasVisibleOutputForDifferentTargetMode())
            return TargetModeStaleStatusText;

        if (_visibleOutputExecutionState is CurrentTabExecutionState visibleCurrentTabExecution &&
            _mainVm.SelectedTab != null &&
            MatchesCurrentTabExecution(_mainVm.SelectedTab, visibleCurrentTabExecution) &&
            _mainVm.SelectedTab.IsFilterActive)
        {
            return _mainVm.SelectedTab.StatusText;
        }

        return _baseStatusText;
    }

    private bool HasVisibleOutputForDifferentTargetMode()
    {
        return _visibleOutputExecutionState switch
        {
            CurrentTabExecutionState => TargetMode != SearchFilterTargetMode.CurrentTab,
            AllOpenTabsExecutionState => TargetMode != SearchFilterTargetMode.AllOpenTabs,
            _ => false
        };
    }

    private bool MatchesCurrentTabExecution(CurrentTabExecutionState executionState)
        => _mainVm.SelectedTab != null && MatchesCurrentTabExecution(_mainVm.SelectedTab, executionState);

    private static bool MatchesCurrentTabExecution(LogTabViewModel tab, CurrentTabExecutionState executionState)
    {
        return string.Equals(tab.TabInstanceId, executionState.TabInstanceId, StringComparison.Ordinal) &&
               string.Equals(tab.FilePath, executionState.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesAllOpenTabsExecution(AllOpenTabsExecutionState executionState)
        => MatchesAllOpenTabsExecution(_mainVm.ActiveScopeDashboardId, executionState);

    private bool MatchesAllOpenTabsExecution(string? scopeDashboardId, AllOpenTabsExecutionState executionState)
    {
        var currentOpenTabs = GetNormalizedOpenTabPathsForScope(scopeDashboardId);
        if (currentOpenTabs.Count != executionState.OrderedFilePaths.Count)
            return false;

        for (var i = 0; i < currentOpenTabs.Count; i++)
        {
            if (!string.Equals(currentOpenTabs[i], executionState.OrderedFilePaths[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private IReadOnlyList<string> GetNormalizedOpenTabPathsForScope(string? scopeDashboardId)
    {
        if (!string.Equals(scopeDashboardId, _mainVm.ActiveScopeDashboardId, StringComparison.Ordinal))
            return Array.Empty<string>();

        return _mainVm.GetAllOpenTabsExecutionFileOrderSnapshot(scopeDashboardId);
    }

    private static bool SnapshotMatchesSourceMode(LogFilterSession.FilterSnapshot? snapshot, SearchDataMode sourceMode)
    {
        var snapshotSourceMode = snapshot?.FilterRequest?.SourceMode;
        return snapshotSourceMode == SearchRequestSourceMode.SnapshotAndTail ||
               snapshotSourceMode == ToFilterApplicabilitySourceMode(sourceMode);
    }

    private static SearchRequestSourceMode ToFilterApplicabilitySourceMode(SearchDataMode sourceMode)
    {
        return sourceMode switch
        {
            SearchDataMode.Tail => SearchRequestSourceMode.Tail,
            _ => SearchRequestSourceMode.DiskSnapshot
        };
    }

    private void SelectedTab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LogTabViewModel)
            return;

        if (e.PropertyName is nameof(LogTabViewModel.StatusText) or nameof(LogTabViewModel.IsFilterActive))
            RefreshVisibleStatusText();
    }

    private void ObserveFilterInvalidation(LogTabViewModel tab)
    {
        for (var index = _filterInvalidationTabs.Count - 1; index >= 0; index--)
        {
            if (!_filterInvalidationTabs[index].TryGetTarget(out var observedTab))
            {
                _filterInvalidationTabs.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(observedTab, tab))
                return;
        }

        tab.FilterSnapshotInvalidated += Tab_FilterSnapshotInvalidated;
        _filterInvalidationTabs.Add(new WeakReference<LogTabViewModel>(tab));
    }

    private void Tab_FilterSnapshotInvalidated(object? sender, EventArgs e)
    {
        if (sender is not LogTabViewModel tab || Volatile.Read(ref _isDisposed) != 0)
            return;

        lock (_filterPublicationSync)
        {
            _mainVm.UpdateRecentTabFilterSnapshot(tab.FilePath, tab.ScopeDashboardId, null);
            if (!string.Equals(tab.ScopeDashboardId, _mainVm.ActiveScopeDashboardId, StringComparison.Ordinal))
                return;

            Interlocked.Increment(ref _filterSnapshotInvalidationVersion);

            if (IsApplying)
            {
                CancelActiveApplySession();
                IsApplying = false;
                _applyingStatusText = string.Empty;
            }

            if (_visibleOutputExecutionState is CurrentTabExecutionState currentTabExecutionState &&
                MatchesCurrentTabExecution(tab, currentTabExecutionState))
            {
                ClearCommittedOutputState();
                _inFlightRollbackScopeState = null;
                _baseStatusText = InvalidatedFilterStatusText;
                RefreshVisibleStatusText();
                return;
            }

            if (_visibleOutputExecutionState is not AllOpenTabsExecutionState allOpenTabsExecutionState ||
                !_appliedScopeSnapshots.Remove(tab.FilePath))
            {
                return;
            }

            _visibleOutputExecutionState = new AllOpenTabsExecutionState(
                allOpenTabsExecutionState.OrderedFilePaths
                    .Where(path => !string.Equals(path, tab.FilePath, StringComparison.OrdinalIgnoreCase))
                    .ToList());
            for (var index = Warnings.Count - 1; index >= 0; index--)
            {
                if (string.Equals(Warnings[index].FilePath, tab.FilePath, StringComparison.OrdinalIgnoreCase))
                    Warnings.RemoveAt(index);
            }

            Warnings.Add(new FilterWarningViewModel(tab.FilePath, InvalidatedFilterStatusText));
            _visibleOutputIsStale = true;
            _inFlightRollbackScopeState = null;
            RaiseFilterApplicabilityChanged();
            RefreshVisibleStatusText();
        }
    }

    private bool IsCurrentSession(CancellationTokenSource sessionCts)
        => ReferenceEquals(_applyFilterCts, sessionCts);

    private bool CanMutateForSession(CancellationTokenSource sessionCts, CancellationToken ct)
        => Volatile.Read(ref _isDisposed) == 0 &&
           IsCurrentSession(sessionCts) &&
           !ct.IsCancellationRequested;

    private bool CanPublishForSession(
        CancellationTokenSource sessionCts,
        long invalidationVersion,
        CancellationToken ct)
        => CanMutateForSession(sessionCts, ct) &&
           Volatile.Read(ref _filterSnapshotInvalidationVersion) == invalidationVersion;

    private bool TryCapturePublishableSnapshot(
        LogTabViewModel tab,
        CancellationTokenSource sessionCts,
        long invalidationVersion,
        CancellationToken ct,
        out LogFilterSession.FilterSnapshot? snapshot)
    {
        snapshot = null;
        if (!CanPublishForSession(sessionCts, invalidationVersion, ct) || tab.IsShutdownOrDisposed)
            return false;

        snapshot = tab.CaptureActiveFilterSnapshot();
        return snapshot != null && tab.IsFilterSnapshotCompatible(snapshot);
    }

    private static bool TryRefreshPublishableSnapshots(
        IReadOnlyDictionary<string, List<LogTabViewModel>> committedTabsByPath,
        IDictionary<string, LogFilterSession.FilterSnapshot> appliedSnapshots)
    {
        foreach (var (filePath, committedTabs) in committedTabsByPath)
        {
            LogFilterSession.FilterSnapshot? retainedSnapshot = null;
            foreach (var committedTab in committedTabs)
            {
                if (committedTab.IsShutdownOrDisposed)
                    return false;

                var activeSnapshot = committedTab.CaptureActiveFilterSnapshot();
                if (activeSnapshot == null || !committedTab.IsFilterSnapshotCompatible(activeSnapshot))
                    return false;

                retainedSnapshot ??= activeSnapshot;
            }

            if (retainedSnapshot == null)
                return false;

            appliedSnapshots[filePath] = LogFilterSession.CloneSnapshot(retainedSnapshot);
        }

        return true;
    }

    private void CancelActiveApplySession()
    {
        var current = _applyFilterCts;
        _applyFilterCts = null;
        if (current == null)
            return;

        current.Cancel();
        current.Dispose();
    }

    private void Warnings_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasWarnings));

    private void RaiseFilterApplicabilityChanged()
        => FilterApplicabilityChanged?.Invoke(this, EventArgs.Empty);

    private void SharedOptions_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchFilterSharedOptions.TargetMode))
        {
            _preferBaseStatusText = false;
            OnPropertyChanged(nameof(TargetMode));
            OnPropertyChanged(nameof(IsCurrentTabTarget));
            OnPropertyChanged(nameof(IsAllOpenTabsTarget));
            OnPropertyChanged(nameof(ClearFilterLabel));
            _pendingAllOpenTabsReplayPaths.Clear();
            ClearPendingDashboardRehydration();
            ApplyVisibleOutputInvalidationIfNeeded();
            RefreshVisibleStatusText();
            return;
        }

    }

    private async Task ReplayDeferredAllOpenTabsSnapshotsAsync(
        ScopeOwnedFilterState scopeState,
        string? scopeDashboardId,
        CancellationToken ct)
    {
        var replayedPaths = new List<string>();
        foreach (var filePath in _pendingAllOpenTabsReplayPaths.ToList())
        {
            if (!scopeState.AppliedScopeSnapshots.TryGetValue(filePath, out var snapshot))
            {
                replayedPaths.Add(filePath);
                continue;
            }

            var openTabs = GetOpenTabsForScopeApplication(filePath, scopeDashboardId).ToList();
            if (openTabs.Count == 0)
                continue;

            var priorSnapshots = new Dictionary<LogTabViewModel, LogFilterSession.FilterSnapshot?>(
                ReferenceEqualityComparer.Instance);
            foreach (var openTab in openTabs)
                priorSnapshots[openTab] = openTab.CaptureActiveFilterSnapshot();
            var restoredTabs = new List<LogTabViewModel>(openTabs.Count);
            var replayAccepted = false;
            var replayRejected = false;
            try
            {
                foreach (var openTab in openTabs)
                {
                    if (!await openTab.RestoreFilterSnapshotAsync(snapshot, ct))
                    {
                        replayRejected = true;
                        break;
                    }

                    restoredTabs.Add(openTab);
                }

                replayAccepted = !replayRejected;
            }
            finally
            {
                if (!replayAccepted)
                {
                    await RestoreFilterRollbackStateAsync(
                        CaptureRollbackSubset(priorSnapshots, restoredTabs));
                }
            }

            if (replayRejected)
            {
                var retainedSnapshot = priorSnapshots.Values.FirstOrDefault(prior => prior != null);
                RejectStoredFilterSnapshot(
                    scopeState,
                    filePath,
                    scopeDashboardId,
                    retainedSnapshot);
            }

            replayedPaths.Add(filePath);
        }

        foreach (var filePath in replayedPaths)
            _pendingAllOpenTabsReplayPaths.Remove(filePath);
    }

    private void RejectStoredFilterSnapshot(
        ScopeOwnedFilterState scopeState,
        string filePath,
        string? scopeDashboardId,
        LogFilterSession.FilterSnapshot? retainedSnapshot = null)
    {
        const string message = "Stored filter was not restored because the file content or encoding changed.";
        scopeState.AppliedScopeSnapshots.Remove(filePath);
        scopeState.Warnings.RemoveAll(warning =>
            string.Equals(warning.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        scopeState.Warnings.Add(new FilterWarningState(filePath, message));
        _mainVm.UpdateRecentTabFilterSnapshot(filePath, scopeDashboardId, retainedSnapshot);

        if (!string.Equals(scopeDashboardId, _mainVm.ActiveScopeDashboardId, StringComparison.Ordinal))
            return;

        _appliedScopeSnapshots.Remove(filePath);
        for (var i = Warnings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Warnings[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                Warnings.RemoveAt(i);
        }

        Warnings.Add(new FilterWarningViewModel(filePath, message));
        RefreshVisibleStatusText();
    }

    private void ApplyVisibleOutputInvalidationIfNeeded()
    {
        if (_visibleOutputExecutionState is CurrentTabExecutionState currentTabExecutionState &&
            !MatchesCurrentTabExecution(currentTabExecutionState))
        {
            ClearCommittedOutputState();
            _baseStatusText = CurrentTabClearedStatusText;
        }
    }

    private void UpdateVisibleOutputStaleState()
    {
        RefreshPendingDashboardRehydrationState();
        if (_visibleOutputExecutionState is not AllOpenTabsExecutionState allOpenTabsExecutionState)
            return;

        if (HasVisibleOutputForDifferentTargetMode())
        {
            _visibleOutputIsStale = true;
            return;
        }

        _visibleOutputIsStale = !MatchesAllOpenTabsExecution(allOpenTabsExecutionState) &&
                                !ShouldDeferAllOpenTabsMismatchStalePromotion(allOpenTabsExecutionState);
    }

    private bool ShouldDeferAllOpenTabsMismatchStalePromotion(AllOpenTabsExecutionState executionState)
    {
        RefreshPendingDashboardRehydrationState();
        if (_mainVm.IsDashboardLoading)
            return true;

        return !string.IsNullOrEmpty(_pendingDashboardRehydrationDashboardId) &&
               string.Equals(_pendingDashboardRehydrationDashboardId, _mainVm.ActiveScopeDashboardId, StringComparison.Ordinal) &&
               executionState.OrderedFilePaths.Count > 0;
    }

    private void ArmPendingDashboardRehydrationIfNeeded()
    {
        ClearPendingDashboardRehydration();
        if (_visibleOutputIsStale ||
            _visibleOutputExecutionState is not AllOpenTabsExecutionState executionState ||
            string.IsNullOrEmpty(_mainVm.ActiveScopeDashboardId))
        {
            return;
        }

        var currentOpenTabs = GetNormalizedOpenTabPathsForScope(_mainVm.ActiveScopeDashboardId);
        if (currentOpenTabs.Count != 0 ||
            executionState.OrderedFilePaths.Count == 0 ||
            _appliedScopeSnapshots.Count == 0)
        {
            return;
        }

        _pendingDashboardRehydrationDashboardId = _mainVm.ActiveScopeDashboardId;
    }

    private void RefreshPendingDashboardRehydrationState()
    {
        if (string.IsNullOrEmpty(_pendingDashboardRehydrationDashboardId))
            return;

        if (_visibleOutputIsStale ||
            _visibleOutputExecutionState is not AllOpenTabsExecutionState executionState ||
            !string.Equals(_pendingDashboardRehydrationDashboardId, _mainVm.ActiveScopeDashboardId, StringComparison.Ordinal))
        {
            ClearPendingDashboardRehydration();
            return;
        }

        if (HasVisibleOutputForDifferentTargetMode())
        {
            ClearPendingDashboardRehydration();
            return;
        }

        if (_mainVm.IsDashboardLoading)
        {
            _pendingDashboardRehydrationLoadStarted = true;
            return;
        }

        if (MatchesAllOpenTabsExecution(executionState))
        {
            ClearPendingDashboardRehydration();
            return;
        }

        var currentOpenTabs = GetNormalizedOpenTabPathsForScope(_pendingDashboardRehydrationDashboardId);
        if (currentOpenTabs.Count > 0 || _pendingDashboardRehydrationLoadStarted)
            ClearPendingDashboardRehydration();
    }

    private void ClearPendingDashboardRehydration()
    {
        _pendingDashboardRehydrationDashboardId = null;
        _pendingDashboardRehydrationLoadStarted = false;
    }

    private static SearchRequest CloneSearchRequest(SearchRequest request)
    {
        return request.Clone();
    }

    private static ScopeOwnedFilterState CloneScopeState(ScopeOwnedFilterState state)
    {
        return new ScopeOwnedFilterState
        {
            Query = state.Query,
            IsRegex = state.IsRegex,
            CaseSensitive = state.CaseSensitive,
            ExcludeMatches = state.ExcludeMatches,
            FromTimestamp = state.FromTimestamp,
            ToTimestamp = state.ToTimestamp,
            TargetMode = state.TargetMode,
            BaseStatusText = state.BaseStatusText,
            ApplyingStatusText = state.ApplyingStatusText,
            ExecutionState = CloneExecutionState(state.ExecutionState),
            Warnings = state.Warnings
                .Select(warning => new FilterWarningState(warning.FilePath, warning.Message))
                .ToList(),
            IsOutputStale = state.IsOutputStale,
            PendingAllOpenTabsReplayPaths = state.PendingAllOpenTabsReplayPaths.ToList(),
            AppliedScopeSnapshots = state.AppliedScopeSnapshots.ToDictionary(
                entry => entry.Key,
                entry => LogFilterSession.CloneSnapshot(entry.Value),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static FilterExecutionState? CloneExecutionState(FilterExecutionState? executionState)
    {
        return executionState switch
        {
            CurrentTabExecutionState currentTab => new CurrentTabExecutionState(currentTab.TabInstanceId, currentTab.FilePath),
            AllOpenTabsExecutionState allOpenTabs => new AllOpenTabsExecutionState(allOpenTabs.OrderedFilePaths.ToList()),
            _ => null
        };
    }

    private abstract record FilterExecutionState;

    private sealed record CurrentTabExecutionState(string TabInstanceId, string FilePath) : FilterExecutionState;

    private sealed record AllOpenTabsExecutionState(IReadOnlyList<string> OrderedFilePaths) : FilterExecutionState;

    private sealed record FilterWarningState(string FilePath, string Message);

    private sealed record FilterRollbackState(
        Dictionary<LogTabViewModel, LogFilterSession.FilterSnapshot?> OpenTabSnapshots,
        Dictionary<string, LogFilterSession.FilterSnapshot?> RecentSnapshotsByPath,
        string? ScopeDashboardId);

    private sealed record FilterEvaluationTarget(
        LogTabViewModel Tab,
        string FilePath,
        FileEncoding Encoding,
        int SearchContentVersion,
        string TabInstanceId,
        int TotalLines)
    {
        public static FilterEvaluationTarget Capture(LogTabViewModel tab)
            => new(
                tab,
                tab.FilePath,
                tab.EffectiveEncoding,
                tab.SearchContentVersion,
                tab.TabInstanceId,
                Math.Max(0, tab.TotalLines));
    }

    private sealed class ScopeOwnedFilterState
    {
        public string Query { get; init; } = string.Empty;

        public bool IsRegex { get; init; }

        public bool CaseSensitive { get; init; }

        public bool ExcludeMatches { get; init; }

        public string FromTimestamp { get; init; } = string.Empty;

        public string ToTimestamp { get; init; } = string.Empty;

        public SearchFilterTargetMode TargetMode { get; init; } = SearchFilterTargetMode.CurrentTab;

        public string BaseStatusText { get; init; } = string.Empty;

        public string ApplyingStatusText { get; init; } = string.Empty;

        public FilterExecutionState? ExecutionState { get; init; }

        public List<FilterWarningState> Warnings { get; init; } = new();

        public bool IsOutputStale { get; init; }

        public List<string> PendingAllOpenTabsReplayPaths { get; init; } = new();

        public Dictionary<string, LogFilterSession.FilterSnapshot> AppliedScopeSnapshots { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);
    }
}
