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
    private const string SourceModeStaleStatusText = "Filter output is for a different source mode. Reapply filter to refresh.";
    private const string CurrentTabNoParseableTimestampStatusText = "No parseable timestamps found in this file for the selected time range.";

    private readonly ISearchService _searchService;
    private readonly ILogWorkspaceContext _mainVm;
    private readonly SearchFilterSharedOptions _sharedOptions;
    private readonly WorkspaceScopedStateStore<ScopeOwnedFilterState> _scopeStateStore;
    private readonly Dictionary<string, LogFilterSession.FilterSnapshot> _appliedScopeSnapshots = new(StringComparer.OrdinalIgnoreCase);

    private WorkspaceScopeSnapshot _activeScopeSnapshot;
    private CancellationTokenSource? _applyFilterCts;
    private LogTabViewModel? _observedTab;
    private string _baseStatusText = string.Empty;
    private FilterExecutionState? _visibleOutputExecutionState;
    private bool _visibleOutputIsStale;
    private HashSet<string> _pendingAllOpenTabsReplayPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingDashboardRehydrationDashboardId;
    private bool _pendingDashboardRehydrationLoadStarted;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isRegex;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private string _fromTimestamp = string.Empty;

    [ObservableProperty]
    private string _toTimestamp = string.Empty;

    public SearchFilterTargetMode TargetMode
    {
        get => _sharedOptions.TargetMode;
        set => _sharedOptions.TargetMode = value;
    }

    public SearchDataMode SourceMode
    {
        get => _sharedOptions.DataMode;
        set => _sharedOptions.DataMode = value;
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

    public bool IsDiskSnapshotMode
    {
        get => SourceMode == SearchDataMode.DiskSnapshot;
        set
        {
            if (value)
                SourceMode = SearchDataMode.DiskSnapshot;
        }
    }

    public bool IsTailMode
    {
        get => SourceMode == SearchDataMode.Tail;
        set
        {
            if (value)
                SourceMode = SearchDataMode.Tail;
        }
    }

    public bool IsSnapshotAndTailMode
    {
        get => SourceMode == SearchDataMode.SnapshotAndTail;
        set
        {
            if (value)
                SourceMode = SearchDataMode.SnapshotAndTail;
        }
    }

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

        if (string.IsNullOrWhiteSpace(Query))
        {
            SetBaseStatusText("Enter filter text.");
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
        var previousState = CaptureCurrentScopeState();
        _activeScopeSnapshot = _mainVm.GetActiveScopeSnapshot();

        var sessionCts = new CancellationTokenSource();
        _applyFilterCts = sessionCts;
        var ct = sessionCts.Token;
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
                    ct);
            }
            else
            {
                await ApplyCurrentTabFilterAsync(
                    previousState,
                    _mainVm.ActiveScopeDashboardId,
                    selectedTab!,
                    sessionCts,
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
                IsApplying = false;
                RefreshVisibleStatusText();
                sessionCts.Dispose();
            }
        }
    }

    [RelayCommand]
    private async Task ClearFilter()
    {
        if (_mainVm.IsDashboardLoading)
            return;

        CancelActiveApplySession();
        IsApplying = false;
        RefreshVisibleStatusText();

        if (TargetMode == SearchFilterTargetMode.AllOpenTabs)
        {
            if (_visibleOutputExecutionState is not AllOpenTabsExecutionState)
                return;

            await ClearAllOpenTabsApplicationAsync(_appliedScopeSnapshots.Keys, _mainVm.ActiveScopeDashboardId);
            ClearCommittedOutputState();
            SetBaseStatusText("All open tabs filter cleared.");
            return;
        }

        var selectedTab = _mainVm.SelectedTab;
        if (selectedTab == null)
        {
            SetBaseStatusText("Select a file tab to clear filter.");
            return;
        }

        var currentTabExecutionState = _visibleOutputExecutionState as CurrentTabExecutionState;
        var selectedTabMatchesExecution = currentTabExecutionState != null &&
                                          MatchesCurrentTabExecution(selectedTab, currentTabExecutionState);

        await selectedTab.ClearFilterAsync();

        if (!selectedTabMatchesExecution)
        {
            RefreshVisibleStatusText();
            return;
        }

        _mainVm.UpdateRecentTabFilterSnapshot(currentTabExecutionState!.FilePath, _mainVm.ActiveScopeDashboardId, null);
        ClearCommittedOutputState();
        SetBaseStatusText("Current tab filter cleared.");
    }

    internal void OnScopeChanging(WorkspaceScopeKey nextScopeKey)
    {
        if (nextScopeKey.Equals(_scopeStateStore.ActiveScopeKey))
            return;

        var activeState = CaptureCurrentScopeState();
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
        if (!ReferenceEquals(_observedTab, selectedTab))
        {
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

        await tab.RestoreFilterSnapshotAsync(snapshot, ct);
    }

    internal LogFilterSession.FilterSnapshot? GetApplicableCurrentTabFilterSnapshot(SearchDataMode sourceMode)
    {
        if (_visibleOutputExecutionState is not CurrentTabExecutionState currentTabExecutionState)
            return null;

        var selectedTab = _mainVm.SelectedTab;
        if (selectedTab == null || !MatchesCurrentTabExecution(selectedTab, currentTabExecutionState) || !selectedTab.IsFilterActive)
            return null;

        var snapshot = selectedTab.CaptureActiveFilterSnapshot();
        return SnapshotMatchesSourceMode(snapshot, sourceMode)
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

        var liveSnapshot = GetOpenTabsForScopeApplication(filePath, _mainVm.ActiveScopeDashboardId)
            .Select(tab => tab.IsFilterActive ? tab.CaptureActiveFilterSnapshot() : null)
            .FirstOrDefault(candidate => candidate != null);
        var effectiveSnapshot = liveSnapshot;
        if (effectiveSnapshot == null && !_appliedScopeSnapshots.TryGetValue(filePath, out effectiveSnapshot))
            return null;

        return SnapshotMatchesSourceMode(effectiveSnapshot, sourceMode)
            ? LogFilterSession.CloneSnapshot(effectiveSnapshot!)
            : null;
    }

    public void Dispose()
    {
        _scopeStateStore.Persist(CaptureCurrentScopeState());
        CancelActiveApplySession();
        IsApplying = false;
        if (_observedTab != null)
            _observedTab.PropertyChanged -= SelectedTab_PropertyChanged;

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
        CancellationToken ct)
    {
        SearchRequest request;
        SearchResult result;
        List<int> matchingLineNumbers;
        string statusText;

        if (SourceMode == SearchDataMode.Tail)
        {
            request = CreateSearchRequest(new[] { selectedTab.FilePath }, SourceMode);
            result = new SearchResult { FilePath = selectedTab.FilePath };
            matchingLineNumbers = new List<int>();
            statusText = BuildPerFileStatusText(request, result, 0);
        }
        else
        {
            request = CreateSearchRequest(new[] { selectedTab.FilePath }, SourceMode);
            result = await _searchService.SearchFileAsync(selectedTab.FilePath, request, selectedTab.EffectiveEncoding, ct);
            if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
                return;

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                SetBaseStatusText($"Filter error: {result.Error}");
                return;
            }

            matchingLineNumbers = BuildMatchingLineNumbers(result);
            statusText = BuildPerFileStatusText(request, result, matchingLineNumbers.Count);
        }

        await ClearAppliedFilterStateAsync(previousState, scopeDashboardId);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        await selectedTab.ApplyFilterAsync(
            matchingLineNumbers,
            statusText,
            request,
            hasParseableTimestamps: result.HasParseableTimestamps);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        ClearCommittedOutputState();
        _baseStatusText = statusText;
        _visibleOutputExecutionState = new CurrentTabExecutionState(selectedTab.TabInstanceId, selectedTab.FilePath);
        RefreshVisibleStatusText();
    }

    private async Task ApplyAllOpenTabsFilterAsync(
        ScopeOwnedFilterState previousState,
        string? scopeDashboardId,
        WorkspaceScopeSnapshot scopeSnapshot,
        CancellationTokenSource sessionCts,
        CancellationToken ct)
    {
        var targetTabs = WorkspaceScopeOrdering.GetDistinctOrderedOpenTabs(scopeSnapshot.OpenTabs);
        var targetPaths = targetTabs
            .Select(openTab => openTab.FilePath)
            .ToList();

        if (targetPaths.Count == 0)
        {
            await ClearAppliedFilterStateAsync(previousState, scopeDashboardId);
            if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
                return;

            ClearCommittedOutputState();
            _baseStatusText = "No open tabs to filter.";
            _visibleOutputExecutionState = CreateAllOpenTabsExecutionState();
            RefreshVisibleStatusText();
            return;
        }

        var encodings = targetTabs.ToDictionary(
            openTab => openTab.FilePath,
            openTab => openTab.Tab.EffectiveEncoding,
            StringComparer.OrdinalIgnoreCase);

        var request = CreateSearchRequest(targetPaths, SourceMode);
        Dictionary<string, SearchResult> resultsByPath;
        if (SourceMode == SearchDataMode.Tail)
        {
            resultsByPath = targetPaths.ToDictionary(
                path => path,
                path => new SearchResult { FilePath = path },
                StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var results = await _searchService.SearchFilesAsync(request, encodings, ct);
            if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
                return;

            resultsByPath = results
                .Where(result => !string.IsNullOrWhiteSpace(result.FilePath))
                .GroupBy(result => result.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        }

        var warnings = new List<FilterWarningState>();
        var appliedSnapshots = new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in targetPaths)
        {
            if (!resultsByPath.TryGetValue(filePath, out var result))
                result = new SearchResult { FilePath = filePath };

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                warnings.Add(new FilterWarningState(filePath, $"Filter error: {result.Error}"));
                continue;
            }

            var matchingLineNumbers = BuildMatchingLineNumbers(result);
            var statusText = BuildPerFileStatusText(request, result, matchingLineNumbers.Count);
            if (HasTimestampRange(request) && !result.HasParseableTimestamps)
                warnings.Add(new FilterWarningState(filePath, "No parseable timestamps found for the selected time range."));

            appliedSnapshots[filePath] = CreateFilterSnapshot(
                matchingLineNumbers,
                statusText,
                request,
                result.HasParseableTimestamps);
        }

        await ClearAppliedFilterStateAsync(previousState, scopeDashboardId);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        await ClearAllOpenTabsApplicationAsync(targetPaths, scopeDashboardId);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        foreach (var (filePath, snapshot) in appliedSnapshots)
        {
            _mainVm.UpdateRecentTabFilterSnapshot(filePath, scopeDashboardId, snapshot);
            foreach (var openTab in GetOpenTabsForScopeApplication(filePath, scopeDashboardId))
                await openTab.RestoreFilterSnapshotAsync(snapshot, ct);
        }

        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        ClearCommittedOutputState();
        foreach (var (filePath, snapshot) in appliedSnapshots)
            _appliedScopeSnapshots[filePath] = LogFilterSession.CloneSnapshot(snapshot);

        RestoreWarnings(warnings);
        _baseStatusText = BuildScopeSummary(request, appliedSnapshots.Count, appliedSnapshots.Values.Sum(snapshot => snapshot.MatchingLineNumbers.Count), warnings.Count);
        _visibleOutputExecutionState = CreateAllOpenTabsExecutionState();
        RefreshVisibleStatusText();
    }

    private async Task ClearAppliedFilterStateAsync(ScopeOwnedFilterState state, string? scopeDashboardId)
    {
        switch (state.ExecutionState)
        {
            case CurrentTabExecutionState currentTabExecutionState:
                await ClearCurrentTabApplicationAsync(currentTabExecutionState.FilePath, scopeDashboardId);
                break;

            case AllOpenTabsExecutionState:
                await ClearAllOpenTabsApplicationAsync(state.AppliedScopeSnapshots.Keys, scopeDashboardId);
                break;
        }
    }

    private async Task ClearCurrentTabApplicationAsync(string filePath, string? scopeDashboardId)
    {
        foreach (var tab in GetOpenTabsInScope(filePath, scopeDashboardId))
        {
            if (tab.IsFilterActive)
                await tab.ClearFilterAsync();
        }

        _mainVm.UpdateRecentTabFilterSnapshot(filePath, scopeDashboardId, null);
    }

    private async Task ClearAllOpenTabsApplicationAsync(IEnumerable<string> filePaths, string? scopeDashboardId)
    {
        var normalizedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalizedPaths.Count == 0)
            return;

        foreach (var path in normalizedPaths)
            _mainVm.UpdateRecentTabFilterSnapshot(path, scopeDashboardId, null);

        foreach (var tab in _mainVm.GetAllTabs().Where(tab =>
                     string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal) &&
                     normalizedPaths.Contains(tab.FilePath)))
        {
            if (tab.IsFilterActive)
                await tab.ClearFilterAsync();
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

    private SearchRequest CreateSearchRequest(IReadOnlyList<string> filePaths, SearchDataMode sourceMode)
    {
        return new SearchRequest
        {
            Query = Query,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            FilePaths = filePaths.ToList(),
            FromTimestamp = string.IsNullOrWhiteSpace(FromTimestamp) ? null : FromTimestamp.Trim(),
            ToTimestamp = string.IsNullOrWhiteSpace(ToTimestamp) ? null : ToTimestamp.Trim(),
            SourceMode = ToRequestSourceMode(sourceMode)
        };
    }

    private static List<int> BuildMatchingLineNumbers(SearchResult result)
    {
        return result.Hits
            .Select(hit => (int)hit.LineNumber)
            .Distinct()
            .OrderBy(line => line)
            .ToList();
    }

    private static string BuildPerFileStatusText(SearchRequest request, SearchResult result, int matchingLineCount)
    {
        if (request.SourceMode == SearchRequestSourceMode.Tail)
        {
            return HasTimestampRange(request) && !result.HasParseableTimestamps
                ? "Filter active (tail only): no parseable timestamps found yet for the selected time range."
                : $"Filter active (tail only): {matchingLineCount:N0} matching lines.";
        }

        if (request.SourceMode == SearchRequestSourceMode.SnapshotAndTail)
        {
            return HasTimestampRange(request) && !result.HasParseableTimestamps
                ? "Filter active (snapshot + tail): no parseable timestamps found in this file for the selected time range."
                : $"Filter active (snapshot + tail): {matchingLineCount:N0} matching lines.";
        }

        if (HasTimestampRange(request) && !result.HasParseableTimestamps)
            return CurrentTabNoParseableTimestampStatusText;

        return $"Filter active: {matchingLineCount:N0} matching lines.";
    }

    private static string BuildScopeSummary(SearchRequest request, int appliedFileCount, int totalMatches, int warningCount)
    {
        var prefix = request.SourceMode switch
        {
            SearchRequestSourceMode.Tail => "Tail filter active",
            SearchRequestSourceMode.SnapshotAndTail => "Snapshot + tail filter active",
            _ => "Filter active"
        };

        if (appliedFileCount == 0)
        {
            return warningCount > 0
                ? $"{prefix} completed with {warningCount:N0} warning(s). No open tabs were filtered."
                : "No open tabs were filtered.";
        }

        var summary = $"{prefix} across {appliedFileCount:N0} open tab(s): {totalMatches:N0} matching lines.";
        if (warningCount > 0)
            summary += $" {warningCount:N0} warning(s).";

        return summary;
    }

    private static bool HasTimestampRange(SearchRequest request)
        => request.FromTimestamp != null || request.ToTimestamp != null;

    private static LogFilterSession.FilterSnapshot CreateFilterSnapshot(
        IReadOnlyList<int> matchingLineNumbers,
        string statusText,
        SearchRequest request,
        bool hasParseableTimestamps)
    {
        return new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = matchingLineNumbers.ToList(),
            StatusText = statusText,
            FilterRequest = CloneSearchRequest(request),
            HasSeenParseableTimestamp = hasParseableTimestamps
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
            FromTimestamp = FromTimestamp,
            ToTimestamp = ToTimestamp,
            TargetMode = TargetMode,
            SourceMode = SourceMode,
            BaseStatusText = _baseStatusText,
            ExecutionState = CloneExecutionState(_visibleOutputExecutionState),
            Warnings = Warnings
                .Select(warning => new FilterWarningState(warning.FilePath, warning.Message))
                .ToList(),
            IsOutputStale = _visibleOutputIsStale,
            PendingAllOpenTabsReplayPaths = _pendingAllOpenTabsReplayPaths.ToList(),
            AppliedScopeSnapshots = _appliedScopeSnapshots.ToDictionary(
                entry => entry.Key,
                entry => LogFilterSession.CloneSnapshot(entry.Value),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private void RestoreScopeState(ScopeOwnedFilterState state)
    {
        Query = state.Query;
        IsRegex = state.IsRegex;
        CaseSensitive = state.CaseSensitive;
        FromTimestamp = state.FromTimestamp;
        ToTimestamp = state.ToTimestamp;
        TargetMode = state.TargetMode;
        SourceMode = state.SourceMode;

        ClearCommittedOutputState();
        _baseStatusText = state.BaseStatusText;
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
        RefreshVisibleStatusText();
    }

    private void ClearCommittedOutputState()
    {
        _baseStatusText = string.Empty;
        _visibleOutputExecutionState = null;
        _visibleOutputIsStale = false;
        _pendingAllOpenTabsReplayPaths.Clear();
        ClearPendingDashboardRehydration();
        _appliedScopeSnapshots.Clear();
        RestoreWarnings(Array.Empty<FilterWarningState>());
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
            return TargetMode == SearchFilterTargetMode.AllOpenTabs
                ? "Applying filter to all open tabs..."
                : "Applying filter to current tab...";

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

        if (HasVisibleOutputForDifferentSourceMode())
            return SourceModeStaleStatusText;

        if (_visibleOutputExecutionState is CurrentTabExecutionState visibleCurrentTabExecution &&
            _mainVm.SelectedTab != null &&
            MatchesCurrentTabExecution(_mainVm.SelectedTab, visibleCurrentTabExecution) &&
            _mainVm.SelectedTab.IsFilterActive)
        {
            return _mainVm.SelectedTab.StatusText;
        }

        return _baseStatusText;
    }

    private bool HasVisibleOutputForDifferentSourceMode()
    {
        return _visibleOutputExecutionState switch
        {
            CurrentTabExecutionState currentTabExecutionState => HasCurrentTabOutputForDifferentSourceMode(currentTabExecutionState),
            AllOpenTabsExecutionState allOpenTabsExecutionState => HasAllOpenTabsOutputForDifferentSourceMode(allOpenTabsExecutionState),
            _ => false
        };
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

    private bool HasCurrentTabOutputForDifferentSourceMode(CurrentTabExecutionState executionState)
    {
        var selectedTab = _mainVm.SelectedTab;
        if (selectedTab == null ||
            !MatchesCurrentTabExecution(selectedTab, executionState) ||
            !selectedTab.IsFilterActive)
        {
            return false;
        }

        return !SnapshotMatchesSourceMode(selectedTab.CaptureActiveFilterSnapshot(), SourceMode);
    }

    private bool HasAllOpenTabsOutputForDifferentSourceMode(AllOpenTabsExecutionState executionState)
    {
        if (!MatchesAllOpenTabsExecution(executionState) || _appliedScopeSnapshots.Count == 0)
            return false;

        return _appliedScopeSnapshots.Values.Any(snapshot => !SnapshotMatchesSourceMode(snapshot, SourceMode));
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

    private AllOpenTabsExecutionState CreateAllOpenTabsExecutionState()
        => new(_mainVm.GetAllOpenTabsExecutionFileOrderSnapshot(_mainVm.ActiveScopeDashboardId));

    private IReadOnlyList<string> GetNormalizedOpenTabPathsForScope(string? scopeDashboardId)
    {
        if (!string.Equals(scopeDashboardId, _mainVm.ActiveScopeDashboardId, StringComparison.Ordinal))
            return Array.Empty<string>();

        return _mainVm.GetAllOpenTabsExecutionFileOrderSnapshot(scopeDashboardId);
    }

    private static bool SnapshotMatchesSourceMode(LogFilterSession.FilterSnapshot? snapshot, SearchDataMode sourceMode)
        => snapshot?.FilterRequest?.SourceMode == ToRequestSourceMode(sourceMode);

    private void SelectedTab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LogTabViewModel)
            return;

        if (e.PropertyName is nameof(LogTabViewModel.StatusText) or nameof(LogTabViewModel.IsFilterActive))
            RefreshVisibleStatusText();
    }

    private bool IsCurrentSession(CancellationTokenSource sessionCts)
        => ReferenceEquals(_applyFilterCts, sessionCts);

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

    private void SharedOptions_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchFilterSharedOptions.TargetMode))
        {
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

        if (e.PropertyName == nameof(SearchFilterSharedOptions.DataMode))
        {
            OnPropertyChanged(nameof(SourceMode));
            OnPropertyChanged(nameof(IsDiskSnapshotMode));
            OnPropertyChanged(nameof(IsTailMode));
            OnPropertyChanged(nameof(IsSnapshotAndTailMode));
            _pendingAllOpenTabsReplayPaths.Clear();
            ClearPendingDashboardRehydration();
            ApplyVisibleOutputInvalidationIfNeeded();
            RefreshVisibleStatusText();
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

            foreach (var openTab in openTabs)
                await openTab.RestoreFilterSnapshotAsync(snapshot, ct);

            replayedPaths.Add(filePath);
        }

        foreach (var filePath in replayedPaths)
            _pendingAllOpenTabsReplayPaths.Remove(filePath);
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

        if (HasVisibleOutputForDifferentTargetMode() ||
            HasVisibleOutputForDifferentSourceMode())
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

        if (HasVisibleOutputForDifferentTargetMode() ||
            HasVisibleOutputForDifferentSourceMode())
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
        return new SearchRequest
        {
            Query = request.Query,
            IsRegex = request.IsRegex,
            CaseSensitive = request.CaseSensitive,
            FilePaths = request.FilePaths.ToList(),
            AllowedLineNumbersByFilePath = request.AllowedLineNumbersByFilePath.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.ToList(),
                StringComparer.OrdinalIgnoreCase),
            StartLineNumber = request.StartLineNumber,
            EndLineNumber = request.EndLineNumber,
            FromTimestamp = request.FromTimestamp,
            ToTimestamp = request.ToTimestamp,
            SourceMode = request.SourceMode
        };
    }

    private static SearchRequestSourceMode ToRequestSourceMode(SearchDataMode sourceMode)
    {
        return sourceMode switch
        {
            SearchDataMode.Tail => SearchRequestSourceMode.Tail,
            SearchDataMode.SnapshotAndTail => SearchRequestSourceMode.SnapshotAndTail,
            _ => SearchRequestSourceMode.DiskSnapshot
        };
    }

    private static ScopeOwnedFilterState CloneScopeState(ScopeOwnedFilterState state)
    {
        return new ScopeOwnedFilterState
        {
            Query = state.Query,
            IsRegex = state.IsRegex,
            CaseSensitive = state.CaseSensitive,
            FromTimestamp = state.FromTimestamp,
            ToTimestamp = state.ToTimestamp,
            TargetMode = state.TargetMode,
            SourceMode = state.SourceMode,
            BaseStatusText = state.BaseStatusText,
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

    private sealed class ScopeOwnedFilterState
    {
        public string Query { get; init; } = string.Empty;

        public bool IsRegex { get; init; }

        public bool CaseSensitive { get; init; }

        public string FromTimestamp { get; init; } = string.Empty;

        public string ToTimestamp { get; init; } = string.Empty;

        public SearchFilterTargetMode TargetMode { get; init; } = SearchFilterTargetMode.CurrentTab;

        public SearchDataMode SourceMode { get; init; } = SearchDataMode.DiskSnapshot;

        public string BaseStatusText { get; init; } = string.Empty;

        public FilterExecutionState? ExecutionState { get; init; }

        public List<FilterWarningState> Warnings { get; init; } = new();

        public bool IsOutputStale { get; init; }

        public List<string> PendingAllOpenTabsReplayPaths { get; init; } = new();

        public Dictionary<string, LogFilterSession.FilterSnapshot> AppliedScopeSnapshots { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);
    }
}
