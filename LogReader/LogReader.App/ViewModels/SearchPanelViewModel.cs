namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public enum SearchDataMode
{
    DiskSnapshot,
    Tail
}

public partial class SearchPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TailRetryDelay = TimeSpan.FromMilliseconds(300);
    private const int DisplaySearchMaxHitsPerFile = 10_000;
    private const int DisplaySearchMaxRetainedLineTextLength = 2_000;
    private const int TailSearchRangeChunkLineCount = 2_000;
    private const string ScopeExitCancelledStatusText = "Search stopped when leaving this scope. Rerun search to refresh these results.";
    private const string SelectedTabChangedStatusText = "Search results cleared because the selected tab changed. Rerun search to refresh.";
    private const string SearchOutputStaleStatusText = "Search output is for a previous context, target, or source. Rerun search to refresh.";
    private readonly ISearchService _searchService;
    private readonly ILogWorkspaceContext _mainVm;
    private readonly SearchFilterSharedOptions _sharedOptions;
    private readonly WorkspaceScopedStateStore<ScopeOwnedSearchState> _scopeStateStore;
    private readonly Dictionary<string, FileSearchResultViewModel> _resultsByFilePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _resultFileOrderByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TailSearchTracker> _tailTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _filesWithParseableTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private WorkspaceScopeSnapshot _activeScopeSnapshot;
    private CancellationTokenSource? _searchCts;
    private SearchSessionContext? _activeSessionContext;
    private string _baseStatusText = string.Empty;
    private SearchExecutionState? _visibleOutputExecutionState;
    private SearchExecutionState? _activeSessionExecutionState;
    private SearchFilterTargetMode? _visibleOutputTargetMode;
    private SearchDataMode? _visibleOutputSearchDataMode;
    private MonitorableResultSetState? _monitorableResultSet;
    private string _invalidationStatusText = string.Empty;
    private SearchOutputFreshness _visibleOutputFreshness;
    private SearchExecutionState? _activeMonitoringExecutionState;
    private int _activeMonitoringFileCount;
    private int _resultPresentationUpdateDepth;
    private bool _pendingVisibleRowsRefresh;
    private long _totalHits;
    private SearchStatusPresentation _baseStatusPresentation;
    private bool _hasCappedResults;

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

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _resultsHeaderText = string.Empty;

    public SearchFilterTargetMode TargetMode
    {
        get => _sharedOptions.TargetMode;
        set => _sharedOptions.TargetMode = value;
    }

    public SearchDataMode SearchDataMode
    {
        get => _sharedOptions.DataMode;
        set => _sharedOptions.DataMode = value;
    }

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

    public bool IsDiskSnapshotMode
    {
        get => SearchDataMode == SearchDataMode.DiskSnapshot;
        set
        {
            if (value)
                SearchDataMode = SearchDataMode.DiskSnapshot;
        }
    }

    public bool IsTailMode
    {
        get => SearchDataMode == SearchDataMode.Tail;
        set
        {
            if (value)
                SearchDataMode = SearchDataMode.Tail;
        }
    }

    public bool AreTargetAndSourceToggleEnabled => !_mainVm.IsDashboardLoading;

    public bool AreExecutionControlsEnabled => !_mainVm.IsDashboardLoading;

    public bool AreResultsInteractionEnabled => !_mainVm.IsDashboardLoading;

    public bool IsMonitoringNewMatches => _activeMonitoringExecutionState != null;

    public bool IsMonitorNewMatchesVisible => CanStartMonitoringNewMatches();

    public bool IsMonitorNewMatchesControlVisible => IsMonitoringNewMatches || IsMonitorNewMatchesVisible;

    public bool IsMonitorNewMatchesChecked => IsMonitoringNewMatches;

    public string MonitorNewMatchesToolTip => BuildMonitorNewMatchesToolTip();

    public string SearchActionButtonText => IsSearching ? "Cancel" : "Clear";

    public bool IsSearchActionButtonEnabled => IsSearching || AreExecutionControlsEnabled;

    public IRelayCommand SearchActionButtonCommand => IsSearching
        ? CancelSearchCommand
        : ClearResultsCommand;

    public ObservableCollection<FileSearchResultViewModel> Results { get; } = new();

    public SearchResultsFlatCollection VisibleRows { get; } = new();

    internal SearchPanelViewModel(
        ISearchService searchService,
        ILogWorkspaceContext mainVm,
        SearchFilterSharedOptions? sharedOptions = null)
    {
        _searchService = searchService;
        _mainVm = mainVm;
        _sharedOptions = sharedOptions ?? new SearchFilterSharedOptions();
        _activeScopeSnapshot = _mainVm.GetActiveScopeSnapshot();
        _scopeStateStore = new WorkspaceScopedStateStore<ScopeOwnedSearchState>(
            _activeScopeSnapshot.ScopeKey,
            static () => new ScopeOwnedSearchState(),
            CloneScopeState);
        _sharedOptions.PropertyChanged += SharedOptions_PropertyChanged;
        RestoreScopeState(_scopeStateStore.ActivateScope(_activeScopeSnapshot.ScopeKey));
    }

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(SearchActionButtonText));
        OnPropertyChanged(nameof(SearchActionButtonCommand));
        OnPropertyChanged(nameof(IsSearchActionButtonEnabled));
        RaiseMonitoringStateChanged();
    }

    [RelayCommand]
    private async Task ExecuteSearch()
    {
        if (_mainVm.IsDashboardLoading)
            return;

        CancelActiveSearchSession(updateUi: false);

        if (string.IsNullOrWhiteSpace(Query))
        {
            SetBaseStatusText("Enter a search query.", SearchStatusPresentation.InlineOnly);
            IsSearching = false;
            return;
        }

        _activeScopeSnapshot = _mainVm.GetActiveScopeSnapshot();
        var sessionCts = new CancellationTokenSource();
        _searchCts = sessionCts;
        var ct = sessionCts.Token;
        var selectedMode = SearchDataMode;
        var sessionContext = CreateSearchSessionContext(selectedMode, _activeScopeSnapshot);
        _activeSessionContext = sessionContext;
        _activeSessionExecutionState = CloneExecutionState(sessionContext.ExecutionState);
        _visibleOutputFreshness = SearchOutputFreshness.None;
        _invalidationStatusText = string.Empty;

        ClearVisibleResults();
        _monitorableResultSet = null;
        _resultFileOrderByPath.Clear();
        DetachTailTrackers();
        IsSearching = true;
        SetBaseStatusText("Searching...", SearchStatusPresentation.Both);

        try
        {
            var targets = BuildSearchTargets(sessionContext);
            if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
                return;

            _visibleOutputExecutionState = CloneExecutionState(_activeSessionExecutionState);
            _visibleOutputTargetMode = sessionContext.TargetMode;
            _visibleOutputSearchDataMode = sessionContext.SearchDataMode;
            CacheResultFileOrder(targets, sessionContext);
            if (targets.Count == 0)
            {
                _activeSessionExecutionState = null;
                _visibleOutputExecutionState = null;
                _visibleOutputTargetMode = null;
                _visibleOutputSearchDataMode = null;
                if (IsCurrentSession(sessionCts))
                {
                    SetBaseStatusText("No files to search", SearchStatusPresentation.HeaderOnly);
                    IsSearching = false;
                }
                return;
            }

            if (selectedMode == SearchDataMode.DiskSnapshot)
            {
                await RunDiskSnapshotSearchAsync(targets, sessionContext, sessionCts, ct);
                if (IsCurrentSession(sessionCts))
                {
                    UpdateMonitorableResultSet(sessionContext, targets);
                    SetBaseStatusText(BuildSnapshotStatus(), SearchStatusPresentation.HeaderOnly);
                    IsSearching = false;
                    _activeSessionExecutionState = null;
                }
                return;
            }

            InitializeTailTrackers(targets, sessionContext, sessionCts);

            if (selectedMode == SearchDataMode.Tail)
            {
                if (IsCurrentSession(sessionCts))
                    SetBaseStatusText(BuildTailStatus(sessionContext.SearchDataMode), SearchStatusPresentation.HeaderOnly);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSession(sessionCts))
                SetBaseStatusText("Search cancelled", SearchStatusPresentation.Both);
        }
        catch (Exception ex)
        {
            if (IsCurrentSession(sessionCts))
            {
                SetBaseStatusText($"Search error: {ex.Message}", SearchStatusPresentation.Both);
                IsSearching = false;
                _activeSessionExecutionState = null;
            }
        }
        finally
        {
            if (IsCurrentSession(sessionCts))
            {
                if (selectedMode == SearchDataMode.DiskSnapshot)
                    IsSearching = false;

                if (!IsSearching)
                {
                    _activeSessionContext = null;
                    _activeSessionExecutionState = null;
                }
            }
        }
    }

    private async Task RunDiskSnapshotSearchAsync(
        IReadOnlyList<SearchTarget> targets,
        SearchSessionContext sessionContext,
        CancellationTokenSource sessionCts,
        CancellationToken ct)
    {
        var filePaths = targets.Select(t => t.FilePath).ToList();
        var encodings = targets.ToDictionary(t => t.FilePath, t => t.Encoding, StringComparer.OrdinalIgnoreCase);
        var request = CreateSearchRequest(
            sessionContext,
            filePaths,
            SearchDataMode.DiskSnapshot,
            GetApplicableFilterSnapshots(sessionContext));
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.DiskSearch,
            filePaths);
        if (IsCurrentSession(sessionCts))
        {
            SetBaseStatusText(
                AdaptiveParallelismDiagnostics.BuildOperationStatus("Searching", filePaths.Count, "file", plan),
                SearchStatusPresentation.Both);
        }

        var results = await _searchService.SearchFilesAsync(request, encodings, ct);

        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        await ApplySearchResultsOnUiAsync(results, sessionCts, ct);
    }

    private void InitializeTailTrackers(
        IReadOnlyList<SearchTarget> targets,
        SearchSessionContext sessionContext,
        CancellationTokenSource sessionCts)
    {
        DetachTailTrackers();
        foreach (var target in targets)
        {
            if (target.Tab == null)
                continue;

            var baselineLine = Math.Max(0, target.Tab.TotalLines);
            var tracker = new TailSearchTracker
            {
                FilePath = target.FilePath,
                Encoding = target.Encoding,
                Tab = target.Tab,
                SnapshotLine = baselineLine,
                LastProcessedLine = baselineLine,
                SearchContentVersion = target.Tab.SearchContentVersion,
                SessionContext = sessionContext
            };
            PropertyChangedEventHandler propertyChangedHandler = (_, e) => OnTailTrackerPropertyChanged(tracker, e, sessionCts);
            tracker.PropertyChangedHandler = propertyChangedHandler;
            target.Tab.PropertyChanged += propertyChangedHandler;
            _tailTrackers[target.FilePath] = tracker;
        }
    }

    private void OnTailTrackerPropertyChanged(
        TailSearchTracker tracker,
        PropertyChangedEventArgs e,
        CancellationTokenSource sessionCts)
    {
        if (!IsCurrentSession(sessionCts) || sessionCts.IsCancellationRequested)
            return;

        if (e.PropertyName is not (nameof(LogTabViewModel.TotalLines) or nameof(LogTabViewModel.SearchContentVersion)))
            return;

        RequestTailTrackerRefresh(tracker, sessionCts);
    }

    private void RequestTailTrackerRefresh(TailSearchTracker tracker, CancellationTokenSource sessionCts)
    {
        Interlocked.Increment(ref tracker.PendingSignalVersion);
        if (Interlocked.CompareExchange(ref tracker.IsDrainActive, 1, 0) != 0)
            return;

        _ = DrainTailTrackerAsync(tracker, sessionCts, sessionCts.Token);
    }

    private async Task DrainTailTrackerAsync(TailSearchTracker tracker, CancellationTokenSource sessionCts, CancellationToken ct)
    {
        var processedVersion = 0;
        try
        {
            while (IsCurrentSession(sessionCts) && !ct.IsCancellationRequested)
            {
                processedVersion = Volatile.Read(ref tracker.PendingSignalVersion);
                var outcome = await ProcessTailTrackerAsync(tracker, sessionCts, ct);
                if (outcome == TailTrackerProcessOutcome.RetryPendingRange)
                {
                    if (Volatile.Read(ref tracker.PendingSignalVersion) == processedVersion)
                        await Task.Delay(TailRetryDelay, ct);

                    continue;
                }

                if (outcome == TailTrackerProcessOutcome.ContinuePendingRange)
                    continue;

                if (Volatile.Read(ref tracker.PendingSignalVersion) == processedVersion)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Exchange(ref tracker.IsDrainActive, 0);
            if (IsCurrentSession(sessionCts) && !ct.IsCancellationRequested)
            {
                var hasPendingSignal = Volatile.Read(ref tracker.PendingSignalVersion) != processedVersion;
                if (hasPendingSignal &&
                    Interlocked.CompareExchange(ref tracker.IsDrainActive, 1, 0) == 0)
                {
                    _ = DrainTailTrackerAsync(tracker, sessionCts, ct);
                }
                else
                {
                    SetBaseStatusText(BuildTailStatus(tracker.SessionContext.SearchDataMode), SearchStatusPresentation.HeaderOnly);
                }

            }
        }
    }

    private async Task<TailTrackerProcessOutcome> ProcessTailTrackerAsync(
        TailSearchTracker tracker,
        CancellationTokenSource sessionCts,
        CancellationToken ct)
    {
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return TailTrackerProcessOutcome.NoWork;

        if (tracker.SearchContentVersion != tracker.Tab.SearchContentVersion)
        {
            if (tracker.IsMonitoringTracker)
            {
                await StopMonitoringForTrackerOnUiAsync(
                    tracker,
                    sessionCts,
                    ct,
                    $"Monitoring stopped for {Path.GetFileName(tracker.FilePath)} because the file content changed.");
                return TailTrackerProcessOutcome.NoWork;
            }

            await ResetTailTrackerStateForContentResetOnUiAsync(tracker, sessionCts, ct);
        }

        var currentTotalLines = Math.Max(0, tracker.Tab.TotalLines);
        if (currentTotalLines < tracker.LastProcessedLine)
        {
            if (tracker.IsMonitoringTracker)
            {
                await StopMonitoringForTrackerOnUiAsync(
                    tracker,
                    sessionCts,
                    ct,
                    $"Monitoring stopped for {Path.GetFileName(tracker.FilePath)} because the file was truncated or rotated.");
                return TailTrackerProcessOutcome.NoWork;
            }

            await ResetTailTrackerStateForContentResetOnUiAsync(tracker, sessionCts, ct);
            currentTotalLines = Math.Max(0, tracker.Tab.TotalLines);
        }

        if (currentTotalLines <= tracker.LastProcessedLine)
            return TailTrackerProcessOutcome.NoWork;

        var expectedContentVersion = tracker.SearchContentVersion;
        var startLine = tracker.LastProcessedLine + 1;
        var searchEndLine = Math.Min(currentTotalLines, tracker.LastProcessedLine + TailSearchRangeChunkLineCount);
        var filterSnapshot = GetApplicableFilterSnapshot(tracker.FilePath, tracker.SessionContext);
        if (filterSnapshot?.LastEvaluatedLine < searchEndLine &&
            filterSnapshot.FilterRequest?.SourceMode != SearchRequestSourceMode.DiskSnapshot)
        {
            return TailTrackerProcessOutcome.RetryPendingRange;
        }

        var request = CreateSearchRequest(
            tracker.SessionContext,
            new List<string> { tracker.FilePath },
            tracker.SessionContext.SearchDataMode,
            GetApplicableFilterSnapshotMap(tracker.FilePath, tracker.SessionContext),
            startLineNumber: startLine,
            endLineNumber: searchEndLine);
        var result = await tracker.Tab.WithLineIndexLeaseAsync(
            (lineIndex, effectiveEncoding, innerCt) => _searchService.SearchFileRangeAsync(
                tracker.FilePath,
                request,
                effectiveEncoding,
                (rangeStartLine, rangeCount, rangeEncoding, rangeCt) =>
                    tracker.Tab.ReadLinesOffUiAsync(lineIndex, rangeStartLine, rangeCount, rangeEncoding, rangeCt),
                innerCt),
            ct).ConfigureAwait(false)
            ?? await _searchService.SearchFileAsync(tracker.FilePath, request, tracker.Encoding, ct).ConfigureAwait(false);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return TailTrackerProcessOutcome.NoWork;

        if (tracker.Tab.SearchContentVersion != expectedContentVersion ||
            tracker.SearchContentVersion != expectedContentVersion)
        {
            return TailTrackerProcessOutcome.NoWork;
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            await ApplySearchResultOnUiAsync(result, sessionCts, ct);
            return TailTrackerProcessOutcome.RetryPendingRange;
        }

        tracker.LastProcessedLine = searchEndLine;
        await ApplySearchResultOnUiAsync(result, sessionCts, ct);
        return searchEndLine < currentTotalLines
            ? TailTrackerProcessOutcome.ContinuePendingRange
            : TailTrackerProcessOutcome.Success;
    }

    private static IReadOnlyList<SearchTarget> BuildSearchTargets(SearchSessionContext sessionContext)
    {
        if (sessionContext.TargetMode == SearchFilterTargetMode.CurrentTab)
        {
            if (sessionContext.SelectedTab == null)
                return Array.Empty<SearchTarget>();

            return new[]
            {
                new SearchTarget
                {
                    FilePath = sessionContext.SelectedTab.FilePath,
                    Encoding = sessionContext.SelectedTab.EffectiveEncoding,
                    Tab = sessionContext.SelectedTab
                }
            };
        }

        var openTabs = sessionContext.SearchDataMode == SearchDataMode.DiskSnapshot
            ? WorkspaceScopeOrdering.GetDistinctOrderedEffectiveOpenTabs(sessionContext.ScopeSnapshot)
            : WorkspaceScopeOrdering.GetDistinctOrderedOpenTabs(sessionContext.ScopeSnapshot.OpenTabs);
        if (openTabs.Count == 0)
            return Array.Empty<SearchTarget>();

        var targets = new List<SearchTarget>(openTabs.Count);
        foreach (var openTab in openTabs)
        {
            targets.Add(new SearchTarget
            {
                FilePath = openTab.FilePath,
                Encoding = openTab.Tab.EffectiveEncoding,
                Tab = openTab.Tab
            });
        }

        return targets;
    }

    private IReadOnlyList<SearchTarget> BuildMonitoringTargets(MonitorableResultSetState resultSet)
    {
        if (resultSet.ExecutionState is CurrentTabExecutionState currentTabExecutionState)
        {
            var selectedTab = _mainVm.SelectedTab;
            if (selectedTab == null || !MatchesCurrentTabExecution(currentTabExecutionState))
                return Array.Empty<SearchTarget>();

            var monitorableFile = resultSet.Files.FirstOrDefault(file =>
                string.Equals(file.FilePath, selectedTab.FilePath, StringComparison.OrdinalIgnoreCase));

            return new[]
            {
                new SearchTarget
                {
                    FilePath = selectedTab.FilePath,
                    Encoding = selectedTab.EffectiveEncoding,
                    Tab = selectedTab,
                    SearchBoundaryLine = monitorableFile?.SearchBoundaryLine ?? Math.Max(0, selectedTab.TotalLines),
                    SearchContentVersion = monitorableFile?.SearchContentVersion ?? selectedTab.SearchContentVersion
                }
            };
        }

        if (resultSet.ExecutionState is not AllOpenTabsExecutionState allOpenTabsExecutionState ||
            !MatchesMonitorableAllOpenTabsExecution(allOpenTabsExecutionState))
        {
            return Array.Empty<SearchTarget>();
        }

        var openTabsByPath = WorkspaceScopeOrdering.GetDistinctOrderedOpenTabs(_mainVm.GetActiveScopeSnapshot().OpenTabs)
            .ToDictionary(openTab => openTab.FilePath, StringComparer.OrdinalIgnoreCase);
        var targets = new List<SearchTarget>(resultSet.Files.Count);
        foreach (var file in resultSet.Files)
        {
            if (!openTabsByPath.TryGetValue(file.FilePath, out var openTab))
                return Array.Empty<SearchTarget>();

            targets.Add(new SearchTarget
            {
                FilePath = openTab.FilePath,
                Encoding = openTab.Tab.EffectiveEncoding,
                Tab = openTab.Tab,
                SearchBoundaryLine = file.SearchBoundaryLine,
                SearchContentVersion = file.SearchContentVersion
            });
        }

        return targets;
    }

    private static bool HasStaleMonitoringTarget(IReadOnlyList<SearchTarget> targets)
    {
        foreach (var target in targets)
        {
            if (target.Tab == null)
                return true;

            if (target.Tab.SearchContentVersion != target.SearchContentVersion ||
                target.Tab.TotalLines < target.SearchBoundaryLine)
            {
                return true;
            }
        }

        return false;
    }

    private SearchSessionContext CreateMonitoringSessionContext(
        MonitorableResultSetState resultSet,
        SearchExecutionState executionState)
    {
        return new SearchSessionContext
        {
            TargetMode = resultSet.TargetMode,
            SearchDataMode = SearchDataMode.DiskSnapshot,
            Query = resultSet.Query,
            IsRegex = resultSet.IsRegex,
            CaseSensitive = resultSet.CaseSensitive,
            FromTimestamp = resultSet.FromTimestamp,
            ToTimestamp = resultSet.ToTimestamp,
            ScopeSnapshot = _mainVm.GetActiveScopeSnapshot(),
            SelectedTab = executionState is CurrentTabExecutionState ? _mainVm.SelectedTab : null,
            ExecutionState = CloneExecutionState(executionState),
            ResultFileOrderSnapshot = _mainVm.GetSearchResultFileOrderSnapshot().ToList()
        };
    }

    private void InitializeMonitoringTailTrackers(
        IReadOnlyList<SearchTarget> targets,
        SearchSessionContext sessionContext,
        CancellationTokenSource sessionCts)
    {
        DetachTailTrackers();
        foreach (var target in targets)
        {
            if (target.Tab == null)
                continue;

            var baselineLine = Math.Max(0, target.SearchBoundaryLine);
            var tracker = new TailSearchTracker
            {
                FilePath = target.FilePath,
                Encoding = target.Encoding,
                Tab = target.Tab,
                SessionContext = sessionContext,
                SnapshotLine = baselineLine,
                LastProcessedLine = baselineLine,
                SearchContentVersion = target.SearchContentVersion,
                IsMonitoringTracker = true
            };
            PropertyChangedEventHandler propertyChangedHandler = (_, e) => OnTailTrackerPropertyChanged(tracker, e, sessionCts);
            tracker.PropertyChangedHandler = propertyChangedHandler;
            target.Tab.PropertyChanged += propertyChangedHandler;
            _tailTrackers[target.FilePath] = tracker;

            if (target.Tab.TotalLines > baselineLine)
                RequestTailTrackerRefresh(tracker, sessionCts);
        }
    }

    private SearchRequest CreateSearchRequest(
        SearchSessionContext sessionContext,
        IReadOnlyList<string> filePaths,
        SearchDataMode sourceMode,
        IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot>? filterSnapshots = null,
        long? startLineNumber = null,
        long? endLineNumber = null)
    {
        return new SearchRequest
        {
            Query = sessionContext.Query,
            IsRegex = sessionContext.IsRegex,
            CaseSensitive = sessionContext.CaseSensitive,
            FilePaths = filePaths.ToList(),
            AllowedLineNumbersByFilePath = BuildAllowedLineNumbers(filePaths, filterSnapshots),
            StartLineNumber = startLineNumber,
            EndLineNumber = endLineNumber,
            FromTimestamp = sessionContext.FromTimestamp,
            ToTimestamp = sessionContext.ToTimestamp,
            SourceMode = ToRequestSourceMode(sourceMode),
            Usage = SearchRequestUsage.DiskSearch,
            MaxHitsPerFile = DisplaySearchMaxHitsPerFile,
            MaxRetainedLineTextLength = DisplaySearchMaxRetainedLineTextLength
        };
    }

    private IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableFilterSnapshots(SearchSessionContext sessionContext)
    {
        if (sessionContext.TargetMode == SearchFilterTargetMode.AllOpenTabs)
            return _mainVm.GetApplicableAllOpenTabsFilterSnapshots(sessionContext.SearchDataMode);

        return CreateSingleFileFilterSnapshotMap(
            sessionContext.SelectedTab?.FilePath,
            _mainVm.GetApplicableCurrentTabFilterSnapshot(sessionContext.SearchDataMode));
    }

    private LogFilterSession.FilterSnapshot? GetApplicableFilterSnapshot(string filePath, SearchSessionContext sessionContext)
    {
        if (sessionContext.TargetMode == SearchFilterTargetMode.AllOpenTabs)
            return _mainVm.GetApplicableAllOpenTabsFilterSnapshot(filePath, sessionContext.SearchDataMode);

        var snapshot = _mainVm.GetApplicableCurrentTabFilterSnapshot(sessionContext.SearchDataMode);
        return snapshot != null && sessionContext.SelectedTab != null &&
               string.Equals(sessionContext.SelectedTab.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
            ? snapshot
            : null;
    }

    private IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableFilterSnapshotMap(
        string filePath,
        SearchSessionContext sessionContext)
        => CreateSingleFileFilterSnapshotMap(filePath, GetApplicableFilterSnapshot(filePath, sessionContext));

    private static IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> CreateSingleFileFilterSnapshotMap(
        string? filePath,
        LogFilterSession.FilterSnapshot? snapshot)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(filePath))
            return new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [filePath] = snapshot
        };
    }

    private static Dictionary<string, IReadOnlyList<int>> BuildAllowedLineNumbers(
        IReadOnlyList<string> filePaths,
        IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot>? filterSnapshots)
    {
        var allowed = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
        if (filterSnapshots == null || filterSnapshots.Count == 0)
            return allowed;

        foreach (var filePath in filePaths)
        {
            if (!filterSnapshots.TryGetValue(filePath, out var snapshot))
                continue;

            allowed[filePath] = snapshot.MatchingLineNumbers;
        }

        return allowed;
    }

    private static SearchRequestSourceMode ToRequestSourceMode(SearchDataMode sourceMode)
    {
        return sourceMode switch
        {
            SearchDataMode.Tail => SearchRequestSourceMode.Tail,
            _ => SearchRequestSourceMode.DiskSnapshot
        };
    }

    private void ResetTailTrackerStateForContentReset(TailSearchTracker tracker)
    {
        AssertUiThread();
        tracker.SnapshotLine = 0;
        tracker.LastProcessedLine = 0;
        tracker.SearchContentVersion = tracker.Tab.SearchContentVersion;
        ClearResultForFile(tracker.FilePath);
    }

    private void ClearResultForFile(string filePath)
    {
        AssertUiThread();
        _filesWithParseableTimestamps.Remove(filePath);

        if (!_resultsByFilePath.Remove(filePath, out var fileResultVm))
            return;

        _totalHits -= fileResultVm.HitCount;
        Results.Remove(fileResultVm);
        RequestVisibleRowsRefresh();
    }

    private void MergeResults(IReadOnlyList<SearchResult> results)
    {
        AssertUiThread();
        BeginResultPresentationUpdate();
        try
        {
            foreach (var result in results)
                MergeResult(result);
        }
        finally
        {
            EndResultPresentationUpdate(false);
        }
    }

    private void MergeResult(SearchResult result)
    {
        AssertUiThread();
        RestoreVisibleExecutionStateFromActiveSessionIfNeeded();

        if (result.HasParseableTimestamps)
            _filesWithParseableTimestamps.Add(result.FilePath);
        if (result.HitLimitExceeded)
            _hasCappedResults = true;

        if (!_resultsByFilePath.TryGetValue(result.FilePath, out var fileResultVm))
        {
            if (result.Hits.Count == 0 && string.IsNullOrWhiteSpace(result.Error))
                return;

            fileResultVm = CreateFileResultViewModel(new SearchResult { FilePath = result.FilePath });
            _resultsByFilePath[result.FilePath] = fileResultVm;
            InsertResultInCanonicalOrder(fileResultVm);
        }
        else if (result.Hits.Count == 0 && string.IsNullOrWhiteSpace(result.Error))
        {
            fileResultVm.SetError(null);
            if (fileResultVm.HitCount == 0)
                ClearResultForFile(result.FilePath);
            return;
        }

        var hitsBefore = fileResultVm.HitCount;
        BeginResultPresentationUpdate();
        try
        {
            fileResultVm.AddHits(result.Hits);
        }
        finally
        {
            EndResultPresentationUpdate(fileResultVm.IsExpanded && fileResultVm.HitCount != hitsBefore);
        }

        _totalHits += fileResultVm.HitCount - hitsBefore;
        fileResultVm.SetError(result.Error);
    }

    private void CacheResultFileOrder(IReadOnlyList<SearchTarget> targets, SearchSessionContext sessionContext)
    {
        _resultFileOrderByPath.Clear();
        if (sessionContext.TargetMode != SearchFilterTargetMode.AllOpenTabs || targets.Count == 0)
            return;

        var targetPaths = targets
            .Select(target => target.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextIndex = 0;

        foreach (var filePath in sessionContext.ResultFileOrderSnapshot)
        {
            if (targetPaths.Contains(filePath) && !_resultFileOrderByPath.ContainsKey(filePath))
                _resultFileOrderByPath[filePath] = nextIndex++;
        }

        foreach (var target in targets)
        {
            if (!_resultFileOrderByPath.ContainsKey(target.FilePath))
                _resultFileOrderByPath[target.FilePath] = nextIndex++;
        }
    }

    private void InsertResultInCanonicalOrder(FileSearchResultViewModel fileResultVm)
    {
        AssertUiThread();
        if (!_resultFileOrderByPath.TryGetValue(fileResultVm.FilePath, out var targetOrder))
        {
            Results.Add(fileResultVm);
            RequestVisibleRowsRefresh();
            return;
        }

        for (var index = 0; index < Results.Count; index++)
        {
            if (_resultFileOrderByPath.TryGetValue(Results[index].FilePath, out var existingOrder) &&
                existingOrder > targetOrder)
            {
                Results.Insert(index, fileResultVm);
                RequestVisibleRowsRefresh();
                return;
            }
        }

        Results.Add(fileResultVm);
        RequestVisibleRowsRefresh();
    }

    private string BuildSnapshotStatus()
    {
        var filesWithHits = _resultsByFilePath.Values.Count(r => r.HitCount > 0);
        return AppendCapStatus($"{_totalHits:N0} in {filesWithHits} file(s)");
    }

    private string BuildTailStatus(SearchDataMode searchDataMode)
    {
        var filesWithHits = _resultsByFilePath.Values.Count(r => r.HitCount > 0);
        if (IsMonitoringNewMatches)
            return AppendCapStatus($"Monitoring new matches for {_activeMonitoringFileCount:N0} file(s): {_totalHits:N0} in {filesWithHits} file(s)");

        return searchDataMode switch
        {
            SearchDataMode.Tail => AppendCapStatus($"Monitoring tail: {_totalHits:N0} in {filesWithHits} file(s)"),
            _ => BuildSnapshotStatus()
        };
    }

    private string AppendCapStatus(string status)
        => _hasCappedResults
            ? $"{status}. Results capped; narrow the query to see more."
            : status;

    private string BuildMonitorNewMatchesToolTip()
    {
        return _monitorableResultSet?.TargetMode == SearchFilterTargetMode.AllOpenTabs
            ? "Monitor new matches in the files from this search."
            : "Monitor new matches in the file from this search.";
    }

    private void UpdateMonitorableResultSet(
        SearchSessionContext sessionContext,
        IReadOnlyList<SearchTarget> targets)
    {
        if (sessionContext.SearchDataMode != SearchDataMode.DiskSnapshot || targets.Count == 0)
        {
            _monitorableResultSet = null;
            return;
        }

        var resultFiles = new List<MonitorableResultFileState>(targets.Count);
        foreach (var target in targets)
        {
            resultFiles.Add(new MonitorableResultFileState(
                target.FilePath,
                target.Tab?.TabInstanceId,
                Math.Max(0, target.Tab?.TotalLines ?? 0),
                target.Tab?.SearchContentVersion ?? 0));
        }

        if (resultFiles.Count == 0)
        {
            _monitorableResultSet = null;
            return;
        }

        _monitorableResultSet = new MonitorableResultSetState
        {
            TargetMode = sessionContext.TargetMode,
            SearchDataMode = sessionContext.SearchDataMode,
            Query = sessionContext.Query,
            IsRegex = sessionContext.IsRegex,
            CaseSensitive = sessionContext.CaseSensitive,
            FromTimestamp = sessionContext.FromTimestamp,
            ToTimestamp = sessionContext.ToTimestamp,
            ExecutionState = sessionContext.TargetMode == SearchFilterTargetMode.AllOpenTabs
                ? new AllOpenTabsExecutionState(resultFiles.Select(file => file.FilePath).ToList())
                : CloneExecutionState(sessionContext.ExecutionState),
            Files = resultFiles
        };

        _visibleOutputExecutionState = CloneExecutionState(_monitorableResultSet.ExecutionState);
    }

    private bool IsCurrentSession(CancellationTokenSource sessionCts)
        => ReferenceEquals(_searchCts, sessionCts);

    private void DetachTailTrackers()
    {
        foreach (var tracker in _tailTrackers.Values)
        {
            if (tracker.PropertyChangedHandler != null)
                tracker.Tab.PropertyChanged -= tracker.PropertyChangedHandler;
        }

        _tailTrackers.Clear();
    }

    private void CancelActiveSearchSession(bool updateUi)
    {
        var current = _searchCts;
        _searchCts = null;
        if (current != null)
        {
            current.Cancel();
            current.Dispose();
        }

        DetachTailTrackers();
        _resultFileOrderByPath.Clear();
        _activeSessionContext = null;
        _activeSessionExecutionState = null;
        _activeMonitoringExecutionState = null;
        _activeMonitoringFileCount = 0;
        RaiseMonitoringStateChanged();

        if (updateUi && current != null)
        {
            IsSearching = false;
            SetBaseStatusText("Search cancelled", SearchStatusPresentation.Both);
        }
    }

    [RelayCommand]
    private void CancelSearch()
    {
        CancelActiveSearchSession(updateUi: true);
    }

    [RelayCommand]
    private void ClearResults()
    {
        if (_mainVm.IsDashboardLoading)
            return;

        CancelActiveSearchSession(updateUi: false);
        IsSearching = false;
        ClearVisibleResults();
        _resultFileOrderByPath.Clear();
        _baseStatusText = string.Empty;
        _baseStatusPresentation = SearchStatusPresentation.None;
        _visibleOutputExecutionState = null;
        _visibleOutputTargetMode = null;
        _visibleOutputSearchDataMode = null;
        _invalidationStatusText = string.Empty;
        _visibleOutputFreshness = SearchOutputFreshness.None;
        RefreshVisibleStatusText();
    }

    [RelayCommand]
    private void ToggleMonitoringNewMatches()
    {
        if (IsMonitoringNewMatches)
        {
            StopMonitoringNewMatches();
            return;
        }

        StartMonitoringNewMatches();
    }

    [RelayCommand]
    private void StartMonitoringNewMatches()
    {
        if (_mainVm.IsDashboardLoading || !CanStartMonitoringNewMatches())
            return;

        var resultSet = CloneMonitorableResultSet(_monitorableResultSet);
        if (resultSet == null)
            return;

        var executionState = CloneExecutionState(resultSet.ExecutionState);
        if (executionState == null)
            return;

        var monitoringTargets = BuildMonitoringTargets(resultSet);
        if (monitoringTargets.Count == 0)
            return;

        if (HasStaleMonitoringTarget(monitoringTargets))
        {
            SetBaseStatusText("Monitoring could not start because file content changed.", SearchStatusPresentation.HeaderOnly);
            return;
        }

        CancelActiveSearchSession(updateUi: false);

        var monitorCts = new CancellationTokenSource();
        _searchCts = monitorCts;
        var sessionContext = CreateMonitoringSessionContext(resultSet, executionState);
        _activeSessionContext = sessionContext;
        _activeMonitoringExecutionState = executionState;
        _activeMonitoringFileCount = monitoringTargets.Count;
        InitializeMonitoringTailTrackers(monitoringTargets, sessionContext, monitorCts);
        SetBaseStatusText(BuildTailStatus(SearchDataMode.Tail), SearchStatusPresentation.HeaderOnly);
        RaiseMonitoringStateChanged();
    }

    [RelayCommand]
    private void StopMonitoringNewMatches()
    {
        if (!IsMonitoringNewMatches)
            return;

        CancelActiveSearchSession(updateUi: false);
        SetBaseStatusText(BuildSnapshotStatus(), SearchStatusPresentation.HeaderOnly);
    }

    public void Dispose()
    {
        CancelActiveSearchSession(updateUi: false);
        _scopeStateStore.Persist(CaptureCurrentScopeState());
        _sharedOptions.PropertyChanged -= SharedOptions_PropertyChanged;
    }

    internal void RefreshLoadFreezeState()
    {
        OnPropertyChanged(nameof(AreTargetAndSourceToggleEnabled));
        OnPropertyChanged(nameof(AreExecutionControlsEnabled));
        OnPropertyChanged(nameof(AreResultsInteractionEnabled));
        OnPropertyChanged(nameof(IsSearchActionButtonEnabled));
    }

    internal void OnScopeChanging(WorkspaceScopeKey nextScopeKey)
    {
        if (nextScopeKey.Equals(_scopeStateStore.ActiveScopeKey))
            return;

        var activeState = CaptureCurrentScopeState();
        if (IsMonitoringNewMatches)
        {
            CancelActiveSearchSession(updateUi: false);
            activeState.VisibleOutputFreshness = activeState.Results.Count > 0 || activeState.ExecutionState != null
                ? SearchOutputFreshness.Stale
                : SearchOutputFreshness.None;
        }

        if (IsSearching)
        {
            var hadVisibleOutput = activeState.Results.Count > 0 ||
                                   !string.IsNullOrWhiteSpace(activeState.BaseStatusText) ||
                                   activeState.ExecutionState != null;
            CancelActiveSearchSession(updateUi: false);
            IsSearching = false;
            activeState.VisibleOutputFreshness = hadVisibleOutput
                ? SearchOutputFreshness.ScopeExitCancelled
                : SearchOutputFreshness.None;
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
            CancelActiveSearchIfOutputContextChanged();
            ApplyVisibleOutputInvalidationIfNeeded();
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

        CancelActiveSearchSession(updateUi: false);
        IsSearching = false;
        _activeScopeSnapshot = _mainVm.GetActiveScopeSnapshot();
        RestoreScopeState(new ScopeOwnedSearchState());
    }

    internal void OnSelectedTabChanged(LogTabViewModel? selectedTab)
    {
        if (_scopeStateStore.PendingScopeKey != null)
            return;

        CancelActiveSearchIfOutputContextChanged();
        ApplyVisibleOutputInvalidationIfNeeded();
        RefreshVisibleStatusText();
    }

    private ScopeOwnedSearchState CaptureCurrentScopeState()
    {
        return new ScopeOwnedSearchState
        {
            Query = Query,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            TargetMode = TargetMode,
            FromTimestamp = FromTimestamp,
            ToTimestamp = ToTimestamp,
            SearchDataMode = SearchDataMode,
            Results = CaptureResultStates(),
            BaseStatusText = _baseStatusText,
            BaseStatusPresentation = _baseStatusPresentation,
            ExecutionState = CloneExecutionState(_visibleOutputExecutionState),
            OutputTargetMode = _visibleOutputTargetMode,
            OutputSearchDataMode = _visibleOutputSearchDataMode,
            MonitorableResultSet = CloneMonitorableResultSet(_monitorableResultSet),
            VisibleOutputFreshness = _visibleOutputFreshness
        };
    }

    private void RestoreScopeState(ScopeOwnedSearchState state)
    {
        Query = state.Query;
        IsRegex = state.IsRegex;
        CaseSensitive = state.CaseSensitive;
        TargetMode = state.TargetMode;
        FromTimestamp = state.FromTimestamp;
        ToTimestamp = state.ToTimestamp;
        SearchDataMode = state.SearchDataMode;

        ClearVisibleResults();
        _activeSessionContext = null;
        _activeSessionExecutionState = null;
        _baseStatusText = state.BaseStatusText;
        _baseStatusPresentation = state.BaseStatusPresentation;
        _visibleOutputExecutionState = CloneExecutionState(state.ExecutionState);
        _visibleOutputTargetMode = state.OutputTargetMode;
        _visibleOutputSearchDataMode = state.OutputSearchDataMode;
        _monitorableResultSet = CloneMonitorableResultSet(state.MonitorableResultSet);
        _visibleOutputFreshness = state.VisibleOutputFreshness;
        _invalidationStatusText = string.Empty;
        IsSearching = false;
        RestoreResultStates(state.Results);
        ApplyVisibleOutputInvalidationIfNeeded();
        RefreshVisibleStatusText();
    }

    private List<FileSearchResultState> CaptureResultStates()
        => Results.Select(result => result.CaptureState()).ToList();

    private void RestoreResultStates(IReadOnlyList<FileSearchResultState> resultStates)
    {
        AssertUiThread();
        foreach (var resultState in resultStates)
        {
            var resultVm = new FileSearchResultViewModel(
                new SearchResult
                {
                    FilePath = resultState.FilePath,
                    Hits = resultState.Hits.ToList(),
                    Error = resultState.Error
                },
                _mainVm,
                OnResultPresentationChanged)
            {
                IsExpanded = resultState.IsExpanded
            };

            _resultsByFilePath[resultVm.FilePath] = resultVm;
            Results.Add(resultVm);
            _totalHits += resultVm.HitCount;
        }

        RefreshVisibleRows();
    }

    private void ClearVisibleResults()
    {
        AssertUiThread();
        Results.Clear();
        _resultsByFilePath.Clear();
        _filesWithParseableTimestamps.Clear();
        _totalHits = 0;
        _hasCappedResults = false;
        _monitorableResultSet = null;
        RefreshVisibleRows();
    }

    private void RestoreVisibleExecutionStateFromActiveSessionIfNeeded()
    {
        if (_visibleOutputExecutionState == null && _activeSessionExecutionState != null)
        {
            _visibleOutputExecutionState = CloneExecutionState(_activeSessionExecutionState);
            _visibleOutputTargetMode = _activeSessionContext?.TargetMode;
            _visibleOutputSearchDataMode = _activeSessionContext?.SearchDataMode;
        }
    }

    private SearchSessionContext CreateSearchSessionContext(SearchDataMode searchDataMode, WorkspaceScopeSnapshot scopeSnapshot)
    {
        var targetMode = TargetMode;
        var selectedTab = targetMode == SearchFilterTargetMode.CurrentTab
            ? _mainVm.SelectedTab
            : null;
        var executionState = CreateExecutionState(scopeSnapshot, targetMode, selectedTab);
        return new SearchSessionContext
        {
            TargetMode = targetMode,
            SearchDataMode = searchDataMode,
            Query = Query,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            FromTimestamp = NormalizeSearchTimestamp(FromTimestamp),
            ToTimestamp = NormalizeSearchTimestamp(ToTimestamp),
            ScopeSnapshot = scopeSnapshot,
            SelectedTab = selectedTab,
            ExecutionState = executionState,
            ResultFileOrderSnapshot = _mainVm.GetSearchResultFileOrderSnapshot().ToList()
        };
    }

    private static string? NormalizeSearchTimestamp(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private SearchExecutionState? CreateExecutionState(
        WorkspaceScopeSnapshot scopeSnapshot,
        SearchFilterTargetMode targetMode,
        LogTabViewModel? selectedTab)
    {
        return targetMode == SearchFilterTargetMode.AllOpenTabs
            ? new AllOpenTabsExecutionState(_mainVm.GetAllOpenTabsExecutionFileOrderSnapshot(_mainVm.ActiveScopeDashboardId))
            : selectedTab == null
                ? null
                : new CurrentTabExecutionState(selectedTab.TabInstanceId, selectedTab.FilePath);
    }

    private void SetBaseStatusText(string statusText, SearchStatusPresentation presentation = SearchStatusPresentation.HeaderOnly)
    {
        _baseStatusText = statusText;
        _baseStatusPresentation = string.IsNullOrWhiteSpace(statusText)
            ? SearchStatusPresentation.None
            : presentation;
        if (!string.IsNullOrWhiteSpace(statusText))
            RestoreVisibleExecutionStateFromActiveSessionIfNeeded();

        RefreshVisibleStatusText();
    }

    private void RefreshVisibleStatusText()
    {
        StatusText = GetVisibleInlineStatusText();
        ResultsHeaderText = GetVisibleResultsHeaderText();
        RaiseMonitoringStateChanged();
    }

    private string GetVisibleInlineStatusText()
    {
        if (_visibleOutputFreshness != SearchOutputFreshness.None)
            return string.Empty;

        return _baseStatusPresentation is SearchStatusPresentation.InlineOnly or SearchStatusPresentation.Both
            ? _baseStatusText
            : string.Empty;
    }

    private string GetVisibleResultsHeaderText()
    {
        if (_visibleOutputFreshness == SearchOutputFreshness.ScopeExitCancelled)
            return ScopeExitCancelledStatusText;

        if (_visibleOutputFreshness == SearchOutputFreshness.Stale)
            return SearchOutputStaleStatusText;

        if (!string.IsNullOrWhiteSpace(_invalidationStatusText))
            return _invalidationStatusText;

        return _baseStatusPresentation is SearchStatusPresentation.HeaderOnly or SearchStatusPresentation.Both
            ? _baseStatusText
            : string.Empty;
    }

    private bool MatchesCurrentTabExecution(CurrentTabExecutionState executionState)
    {
        var selectedTab = _mainVm.SelectedTab;
        return selectedTab != null &&
               string.Equals(selectedTab.TabInstanceId, executionState.TabInstanceId, StringComparison.Ordinal) &&
               string.Equals(selectedTab.FilePath, executionState.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesAllOpenTabsExecution(AllOpenTabsExecutionState executionState)
    {
        var currentOrderedPaths = _mainVm.GetAllOpenTabsExecutionFileOrderSnapshot(_mainVm.ActiveScopeDashboardId);
        if (currentOrderedPaths.Count != executionState.OrderedFilePaths.Count)
            return false;

        for (var i = 0; i < currentOrderedPaths.Count; i++)
        {
            if (!string.Equals(currentOrderedPaths[i], executionState.OrderedFilePaths[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private bool MatchesMonitorableAllOpenTabsExecution(AllOpenTabsExecutionState executionState)
    {
        var currentPaths = WorkspaceScopeOrdering.GetDistinctOrderedOpenTabs(_mainVm.GetActiveScopeSnapshot().OpenTabs)
            .Select(openTab => openTab.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return executionState.OrderedFilePaths.All(currentPaths.Contains);
    }

    private bool CanStartMonitoringNewMatches()
    {
        if (IsSearching ||
            IsMonitoringNewMatches ||
            _visibleOutputSearchDataMode != SearchDataMode.DiskSnapshot ||
            _monitorableResultSet == null)
        {
            return false;
        }

        return _monitorableResultSet.ExecutionState switch
        {
            CurrentTabExecutionState currentTabExecutionState => MatchesCurrentTabExecution(currentTabExecutionState),
            AllOpenTabsExecutionState allOpenTabsExecutionState => MatchesMonitorableAllOpenTabsExecution(allOpenTabsExecutionState),
            _ => false
        };
    }

    private void RaiseMonitoringStateChanged()
    {
        OnPropertyChanged(nameof(IsMonitoringNewMatches));
        OnPropertyChanged(nameof(IsMonitorNewMatchesChecked));
        OnPropertyChanged(nameof(IsMonitorNewMatchesVisible));
        OnPropertyChanged(nameof(IsMonitorNewMatchesControlVisible));
        OnPropertyChanged(nameof(MonitorNewMatchesToolTip));
    }

    private static ScopeOwnedSearchState CloneScopeState(ScopeOwnedSearchState state)
    {
        return new ScopeOwnedSearchState
        {
            Query = state.Query,
            IsRegex = state.IsRegex,
            CaseSensitive = state.CaseSensitive,
            TargetMode = state.TargetMode,
            FromTimestamp = state.FromTimestamp,
            ToTimestamp = state.ToTimestamp,
            SearchDataMode = state.SearchDataMode,
            Results = state.Results
                .Select(result => new FileSearchResultState(
                    result.FilePath,
                    result.Hits.Select(hit => new SearchHit
                    {
                        LineNumber = hit.LineNumber,
                        LineText = hit.LineText,
                        MatchStart = hit.MatchStart,
                        MatchLength = hit.MatchLength
                    }).ToList(),
                    result.Error,
                    result.IsExpanded))
                .ToList(),
            BaseStatusText = state.BaseStatusText,
            BaseStatusPresentation = state.BaseStatusPresentation,
            ExecutionState = CloneExecutionState(state.ExecutionState),
            OutputTargetMode = state.OutputTargetMode,
            OutputSearchDataMode = state.OutputSearchDataMode,
            MonitorableResultSet = CloneMonitorableResultSet(state.MonitorableResultSet),
            VisibleOutputFreshness = state.VisibleOutputFreshness
        };
    }

    private static MonitorableResultSetState? CloneMonitorableResultSet(MonitorableResultSetState? state)
    {
        if (state == null)
            return null;

        return new MonitorableResultSetState
        {
            TargetMode = state.TargetMode,
            SearchDataMode = state.SearchDataMode,
            Query = state.Query,
            IsRegex = state.IsRegex,
            CaseSensitive = state.CaseSensitive,
            FromTimestamp = state.FromTimestamp,
            ToTimestamp = state.ToTimestamp,
            ExecutionState = CloneExecutionState(state.ExecutionState),
            Files = state.Files
                .Select(file => new MonitorableResultFileState(
                    file.FilePath,
                    file.TabInstanceId,
                    file.SearchBoundaryLine,
                    file.SearchContentVersion))
                .ToList()
        };
    }

    private FileSearchResultViewModel CreateFileResultViewModel(SearchResult result)
        => new(result, _mainVm, OnResultPresentationChanged);

    private void OnResultPresentationChanged()
    {
        AssertUiThread();
        if (_resultPresentationUpdateDepth != 0)
            return;

        RequestVisibleRowsRefresh();
    }

    private void RefreshVisibleRows()
    {
        AssertUiThread();
        VisibleRows.Refresh(Results);
    }

    private static SearchExecutionState? CloneExecutionState(SearchExecutionState? executionState)
    {
        return executionState switch
        {
            CurrentTabExecutionState currentTab => new CurrentTabExecutionState(
                currentTab.TabInstanceId,
                currentTab.FilePath),
            AllOpenTabsExecutionState allOpenTabs => new AllOpenTabsExecutionState(
                allOpenTabs.OrderedFilePaths.ToList()),
            _ => null
        };
    }

    private void SharedOptions_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchFilterSharedOptions.TargetMode))
        {
            CancelActiveSearchIfOutputContextChanged();
            OnPropertyChanged(nameof(TargetMode));
            OnPropertyChanged(nameof(IsCurrentTabTarget));
            OnPropertyChanged(nameof(IsAllOpenTabsTarget));
            ApplyVisibleOutputInvalidationIfNeeded();
            RefreshVisibleStatusText();
            return;
        }

        if (e.PropertyName == nameof(SearchFilterSharedOptions.DataMode))
        {
            CancelActiveSearchIfOutputContextChanged();
            OnPropertyChanged(nameof(SearchDataMode));
            OnPropertyChanged(nameof(IsDiskSnapshotMode));
            OnPropertyChanged(nameof(IsTailMode));
            ApplyVisibleOutputInvalidationIfNeeded();
            RefreshVisibleStatusText();
        }
    }

    private void CancelActiveSearchIfOutputContextChanged()
    {
        if (!IsSearching || _activeSessionContext == null)
            return;

        if (MatchesCurrentOutputContext(_activeSessionContext))
            return;

        if (Results.Count > 0)
        {
            var invalidationReason = GetVisibleOutputInvalidationReason(
                _activeSessionContext.TargetMode,
                _activeSessionContext.SearchDataMode,
                _activeSessionContext.ExecutionState);
            ApplyInvalidationReason(invalidationReason);
        }
        else
        {
            _baseStatusText = string.Empty;
            _baseStatusPresentation = SearchStatusPresentation.None;
            _visibleOutputExecutionState = null;
            _visibleOutputTargetMode = null;
            _visibleOutputSearchDataMode = null;
            _visibleOutputFreshness = SearchOutputFreshness.None;
        }

        CancelActiveSearchSession(updateUi: false);
        IsSearching = false;
    }

    private bool MatchesCurrentOutputContext(SearchSessionContext sessionContext)
        => GetVisibleOutputInvalidationReason(
            sessionContext.TargetMode,
            sessionContext.SearchDataMode,
            sessionContext.ExecutionState) == SearchOutputInvalidationReason.None;

    private void ApplyVisibleOutputInvalidationIfNeeded()
    {
        if (IsSearching || _visibleOutputExecutionState == null)
            return;

        var invalidationReason = GetVisibleOutputInvalidationReason();
        if (invalidationReason == SearchOutputInvalidationReason.None)
        {
            if (_visibleOutputFreshness == SearchOutputFreshness.Stale)
                _visibleOutputFreshness = SearchOutputFreshness.None;
            _invalidationStatusText = string.Empty;
            return;
        }

        if (IsMonitoringNewMatches)
            CancelActiveSearchSession(updateUi: false);

        ApplyInvalidationReason(invalidationReason);
    }

    private SearchOutputInvalidationReason GetVisibleOutputInvalidationReason()
    {
        if (_visibleOutputSearchDataMode == SearchDataMode.DiskSnapshot &&
            _monitorableResultSet != null)
        {
            return GetMonitorableResultSetInvalidationReason(_monitorableResultSet);
        }

        return GetVisibleOutputInvalidationReason(
            _visibleOutputTargetMode,
            _visibleOutputSearchDataMode,
            _visibleOutputExecutionState);
    }

    private SearchOutputInvalidationReason GetMonitorableResultSetInvalidationReason(MonitorableResultSetState resultSet)
    {
        return resultSet.ExecutionState switch
        {
            CurrentTabExecutionState currentTabExecutionState when !MatchesCurrentTabExecution(currentTabExecutionState)
                => SearchOutputInvalidationReason.SelectedTabChanged,
            AllOpenTabsExecutionState allOpenTabsExecutionState when !MatchesMonitorableAllOpenTabsExecution(allOpenTabsExecutionState)
                => SearchOutputInvalidationReason.ScopeChanged,
            _ => SearchOutputInvalidationReason.None
        };
    }

    private SearchOutputInvalidationReason GetVisibleOutputInvalidationReason(
        SearchFilterTargetMode? targetMode,
        SearchDataMode? searchDataMode,
        SearchExecutionState? executionState)
    {
        if (executionState == null || targetMode == null || searchDataMode == null)
            return SearchOutputInvalidationReason.None;

        if (targetMode != TargetMode)
            return SearchOutputInvalidationReason.TargetChanged;

        if (searchDataMode != SearchDataMode)
            return SearchOutputInvalidationReason.SourceChanged;

        if (executionState is CurrentTabExecutionState currentTabExecutionState &&
            !MatchesCurrentTabExecution(currentTabExecutionState))
        {
            return SearchOutputInvalidationReason.SelectedTabChanged;
        }

        if (executionState is AllOpenTabsExecutionState allOpenTabsExecutionState &&
            !MatchesAllOpenTabsExecution(allOpenTabsExecutionState))
        {
            return SearchOutputInvalidationReason.ScopeChanged;
        }

        return SearchOutputInvalidationReason.None;
    }

    private void ApplyInvalidationReason(SearchOutputInvalidationReason invalidationReason)
    {
        switch (invalidationReason)
        {
            case SearchOutputInvalidationReason.None:
                _visibleOutputFreshness = SearchOutputFreshness.None;
                _invalidationStatusText = string.Empty;
                return;

            case SearchOutputInvalidationReason.SelectedTabChanged:
                ClearVisibleResults();
                _baseStatusText = string.Empty;
                _baseStatusPresentation = SearchStatusPresentation.None;
                _visibleOutputExecutionState = null;
                _visibleOutputTargetMode = null;
                _visibleOutputSearchDataMode = null;
                _visibleOutputFreshness = SearchOutputFreshness.None;
                _invalidationStatusText = SelectedTabChangedStatusText;
                return;

            default:
                _visibleOutputFreshness = SearchOutputFreshness.Stale;
                _invalidationStatusText = string.Empty;
                return;
        }
    }

    private void BeginResultPresentationUpdate()
    {
        AssertUiThread();
        _resultPresentationUpdateDepth++;
    }

    private void EndResultPresentationUpdate(bool refreshRows)
    {
        AssertUiThread();
        if (_resultPresentationUpdateDepth > 0)
            _resultPresentationUpdateDepth--;

        _pendingVisibleRowsRefresh |= refreshRows;
        if (_resultPresentationUpdateDepth != 0)
            return;

        if (_pendingVisibleRowsRefresh)
        {
            _pendingVisibleRowsRefresh = false;
            RefreshVisibleRows();
        }
    }

    private void RequestVisibleRowsRefresh()
    {
        AssertUiThread();
        if (_resultPresentationUpdateDepth != 0)
        {
            _pendingVisibleRowsRefresh = true;
            return;
        }

        RefreshVisibleRows();
    }

    private Task ApplySearchResultOnUiAsync(
        SearchResult result,
        CancellationTokenSource sessionCts,
        CancellationToken ct)
        => RunSessionUiMutationAsync(sessionCts, ct, () => MergeResult(result));

    private Task ApplySearchResultsOnUiAsync(
        IReadOnlyList<SearchResult> results,
        CancellationTokenSource sessionCts,
        CancellationToken ct)
        => RunSessionUiMutationAsync(sessionCts, ct, () => MergeResults(results));

    private Task ResetTailTrackerStateForContentResetOnUiAsync(
        TailSearchTracker tracker,
        CancellationTokenSource sessionCts,
        CancellationToken ct)
        => RunSessionUiMutationAsync(sessionCts, ct, () => ResetTailTrackerStateForContentReset(tracker));

    private Task StopMonitoringForTrackerOnUiAsync(
        TailSearchTracker tracker,
        CancellationTokenSource sessionCts,
        CancellationToken ct,
        string statusText)
        => RunSessionUiMutationAsync(sessionCts, ct, () => StopMonitoringForTracker(tracker, statusText));

    private async Task RunSessionUiMutationAsync(
        CancellationTokenSource sessionCts,
        CancellationToken ct,
        Action mutation)
    {
        ArgumentNullException.ThrowIfNull(sessionCts);
        ArgumentNullException.ThrowIfNull(mutation);

        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
                return;

            mutation();
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
                return;

            mutation();
        }, DispatcherPriority.Background);
    }

    private void StopMonitoringForTracker(TailSearchTracker tracker, string statusText)
    {
        AssertUiThread();
        if (!_tailTrackers.Remove(tracker.FilePath))
            return;

        if (tracker.PropertyChangedHandler != null)
            tracker.Tab.PropertyChanged -= tracker.PropertyChangedHandler;

        _activeMonitoringFileCount = _tailTrackers.Values.Count(t => t.IsMonitoringTracker);
        if (_activeMonitoringFileCount == 0)
            _activeMonitoringExecutionState = null;

        RaiseMonitoringStateChanged();
        SetBaseStatusText(statusText, SearchStatusPresentation.HeaderOnly);
    }

    [Conditional("DEBUG")]
    private void AssertUiThread()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            throw new InvalidOperationException("Search result state must only be mutated on the UI thread.");
    }

    private enum TailTrackerProcessOutcome
    {
        NoWork,
        Success,
        ContinuePendingRange,
        RetryPendingRange
    }

    private sealed class SearchTarget
    {
        public string FilePath { get; init; } = string.Empty;
        public FileEncoding Encoding { get; init; }
        public LogTabViewModel? Tab { get; init; }
        public long SearchBoundaryLine { get; init; }
        public int SearchContentVersion { get; init; }
    }

    private sealed class TailSearchTracker
    {
        public string FilePath { get; init; } = string.Empty;
        public FileEncoding Encoding { get; init; }
        public LogTabViewModel Tab { get; init; } = null!;
        public SearchSessionContext SessionContext { get; init; } = null!;
        public long SnapshotLine { get; set; }
        public long LastProcessedLine { get; set; }
        public int SearchContentVersion { get; set; }
        public bool IsMonitoringTracker { get; init; }
        public PropertyChangedEventHandler? PropertyChangedHandler { get; set; }
        public int PendingSignalVersion;
        public int IsDrainActive;
    }

    private sealed class SearchSessionContext
    {
        public SearchFilterTargetMode TargetMode { get; init; }
        public SearchDataMode SearchDataMode { get; init; }
        public string Query { get; init; } = string.Empty;
        public bool IsRegex { get; init; }
        public bool CaseSensitive { get; init; }
        public string? FromTimestamp { get; init; }
        public string? ToTimestamp { get; init; }
        public WorkspaceScopeSnapshot ScopeSnapshot { get; init; } = null!;
        public LogTabViewModel? SelectedTab { get; init; }
        public SearchExecutionState? ExecutionState { get; init; }
        public IReadOnlyList<string> ResultFileOrderSnapshot { get; init; } = Array.Empty<string>();
    }

    private enum SearchOutputFreshness
    {
        None,
        ScopeExitCancelled,
        Stale
    }

    private enum SearchOutputInvalidationReason
    {
        None,
        SelectedTabChanged,
        ScopeChanged,
        TargetChanged,
        SourceChanged
    }

    private abstract record SearchExecutionState;

    private sealed record CurrentTabExecutionState(string TabInstanceId, string FilePath) : SearchExecutionState;

    private sealed record AllOpenTabsExecutionState(IReadOnlyList<string> OrderedFilePaths) : SearchExecutionState;

    private sealed record MonitorableResultFileState(
        string FilePath,
        string? TabInstanceId,
        long SearchBoundaryLine,
        int SearchContentVersion);

    private sealed class MonitorableResultSetState
    {
        public SearchFilterTargetMode TargetMode { get; init; }
        public SearchDataMode SearchDataMode { get; init; }
        public string Query { get; init; } = string.Empty;
        public bool IsRegex { get; init; }
        public bool CaseSensitive { get; init; }
        public string? FromTimestamp { get; init; }
        public string? ToTimestamp { get; init; }
        public SearchExecutionState? ExecutionState { get; init; }
        public IReadOnlyList<MonitorableResultFileState> Files { get; init; } = Array.Empty<MonitorableResultFileState>();
    }

    private sealed class ScopeOwnedSearchState
    {
        public string Query { get; init; } = string.Empty;
        public bool IsRegex { get; init; }
        public bool CaseSensitive { get; init; }
        public SearchFilterTargetMode TargetMode { get; init; } = SearchFilterTargetMode.CurrentTab;
        public string FromTimestamp { get; init; } = string.Empty;
        public string ToTimestamp { get; init; } = string.Empty;
        public SearchDataMode SearchDataMode { get; init; } = SearchDataMode.DiskSnapshot;
        public List<FileSearchResultState> Results { get; init; } = new();
        public string BaseStatusText { get; init; } = string.Empty;
        public SearchStatusPresentation BaseStatusPresentation { get; init; }
        public SearchExecutionState? ExecutionState { get; init; }
        public SearchFilterTargetMode? OutputTargetMode { get; init; }
        public SearchDataMode? OutputSearchDataMode { get; init; }
        public MonitorableResultSetState? MonitorableResultSet { get; init; }
        public SearchOutputFreshness VisibleOutputFreshness { get; set; }
    }

    private enum SearchStatusPresentation
    {
        None,
        InlineOnly,
        HeaderOnly,
        Both
    }
}
