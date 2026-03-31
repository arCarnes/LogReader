namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public enum SearchDataMode
{
    DiskSnapshot,
    Tail,
    SnapshotAndTail
}

public enum SearchResultLineOrder
{
    Ascending,
    Descending
}

public partial class SearchPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TailRetryDelay = TimeSpan.FromMilliseconds(300);
    private const string ScopeExitCancelledStatusText = "Search stopped when leaving this scope. Rerun search to refresh these results.";
    private const string CurrentFileStaleStatusText = "Results are for a previous file in this scope. Rerun search to refresh.";
    private const string CurrentScopeStaleStatusText = "Results are for a previous set of open tabs in this scope. Rerun search to refresh.";
    private readonly ISearchService _searchService;
    private readonly ILogWorkspaceContext _mainVm;
    private readonly Dictionary<WorkspaceScopeKey, ScopeOwnedSearchState> _scopeStates = new();
    private readonly Dictionary<string, FileSearchResultViewModel> _resultsByFilePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _resultFileOrderByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TailSearchTracker> _tailTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _filesWithParseableTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private WorkspaceScopeKey _activeScopeKey;
    private WorkspaceScopeSnapshot _activeScopeSnapshot;
    private WorkspaceScopeKey? _pendingScopeKey;
    private CancellationTokenSource? _searchCts;
    private SearchDataMode _activeSearchDataMode = SearchDataMode.DiskSnapshot;
    private string _baseStatusText = string.Empty;
    private SearchExecutionState? _visibleOutputExecutionState;
    private SearchExecutionState? _activeSessionExecutionState;
    private bool _showScopeExitCancelledStatus;
    private long _totalHits;
    private bool _snapshotBackfillComplete;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isRegex;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private bool _wholeWord;

    [ObservableProperty]
    private bool _allFiles;

    [ObservableProperty]
    private string _fromTimestamp = string.Empty;

    [ObservableProperty]
    private string _toTimestamp = string.Empty;

    [ObservableProperty]
    private string _navigateTimestamp = string.Empty;

    [ObservableProperty]
    private string _navigateLineNumber = string.Empty;

    [ObservableProperty]
    private string _goToErrorText = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private SearchDataMode _searchDataMode = SearchDataMode.DiskSnapshot;

    [ObservableProperty]
    private SearchResultLineOrder _lineOrder = SearchResultLineOrder.Ascending;

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

    public bool IsSnapshotAndTailMode
    {
        get => SearchDataMode == SearchDataMode.SnapshotAndTail;
        set
        {
            if (value)
                SearchDataMode = SearchDataMode.SnapshotAndTail;
        }
    }

    public bool IsAscendingLineOrder
    {
        get => LineOrder == SearchResultLineOrder.Ascending;
        set
        {
            if (value)
                LineOrder = SearchResultLineOrder.Ascending;
        }
    }

    public bool IsDescendingLineOrder
    {
        get => LineOrder == SearchResultLineOrder.Descending;
        set
        {
            if (value)
                LineOrder = SearchResultLineOrder.Descending;
        }
    }

    public ObservableCollection<FileSearchResultViewModel> Results { get; } = new();

    internal SearchPanelViewModel(ISearchService searchService, ILogWorkspaceContext mainVm)
    {
        _searchService = searchService;
        _mainVm = mainVm;
        _activeScopeSnapshot = _mainVm.GetActiveScopeSnapshot();
        _activeScopeKey = _activeScopeSnapshot.ScopeKey;
        RestoreScopeState(GetOrCreateScopeState(_activeScopeKey));
    }

    partial void OnSearchDataModeChanged(SearchDataMode value)
    {
        OnPropertyChanged(nameof(IsDiskSnapshotMode));
        OnPropertyChanged(nameof(IsTailMode));
        OnPropertyChanged(nameof(IsSnapshotAndTailMode));
    }

    partial void OnLineOrderChanged(SearchResultLineOrder value)
    {
        OnPropertyChanged(nameof(IsAscendingLineOrder));
        OnPropertyChanged(nameof(IsDescendingLineOrder));
        ApplyLineOrderToResults(value);
    }

    partial void OnNavigateTimestampChanged(string value)
    {
        GoToErrorText = string.Empty;
    }

    partial void OnNavigateLineNumberChanged(string value)
    {
        GoToErrorText = string.Empty;
    }

    [RelayCommand]
    private async Task ExecuteSearch()
    {
        CancelActiveSearchSession(updateUi: false);

        if (string.IsNullOrWhiteSpace(Query))
        {
            SetBaseStatusText("Enter a search query.");
            IsSearching = false;
            return;
        }

        _activeScopeSnapshot = _mainVm.GetActiveScopeSnapshot();
        var sessionCts = new CancellationTokenSource();
        _searchCts = sessionCts;
        var ct = sessionCts.Token;
        var selectedMode = SearchDataMode;
        _activeSearchDataMode = selectedMode;
        _showScopeExitCancelledStatus = false;

        ClearVisibleResults();
        _resultFileOrderByPath.Clear();
        DetachTailTrackers();
        _snapshotBackfillComplete = false;
        IsSearching = true;
        SetBaseStatusText("Searching...");

        try
        {
            var targets = BuildSearchTargets(_activeScopeSnapshot);
            _activeSessionExecutionState = CreateExecutionState(targets);
            _visibleOutputExecutionState = CloneExecutionState(_activeSessionExecutionState);
            CacheResultFileOrder(targets);
            if (targets.Count == 0)
            {
                _activeSessionExecutionState = null;
                _visibleOutputExecutionState = null;
                if (IsCurrentSession(sessionCts))
                {
                    SetBaseStatusText("No files to search");
                    IsSearching = false;
                }
                return;
            }

            if (selectedMode == SearchDataMode.DiskSnapshot)
            {
                await RunDiskSnapshotSearchAsync(targets, sessionCts, ct);
                if (IsCurrentSession(sessionCts))
                {
                    SetBaseStatusText(BuildSnapshotStatus());
                    IsSearching = false;
                    _activeSessionExecutionState = null;
                }
                return;
            }

            InitializeTailTrackers(targets, sessionCts);

            if (selectedMode == SearchDataMode.Tail)
            {
                if (IsCurrentSession(sessionCts))
                    SetBaseStatusText(BuildTailStatus());
                return;
            }

            if (IsCurrentSession(sessionCts))
                SetBaseStatusText("Monitoring tail and backfilling disk snapshot...");

            await RunSnapshotBackfillAsync(targets, sessionCts, ct);
            _snapshotBackfillComplete = true;

            if (IsCurrentSession(sessionCts) && !ct.IsCancellationRequested)
                SetBaseStatusText(BuildTailStatus());
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSession(sessionCts))
                SetBaseStatusText("Search cancelled");
        }
        catch (Exception ex)
        {
            if (IsCurrentSession(sessionCts))
            {
                SetBaseStatusText($"Search error: {ex.Message}");
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
                    _activeSessionExecutionState = null;
            }
        }
    }

    private async Task RunDiskSnapshotSearchAsync(IReadOnlyList<SearchTarget> targets, CancellationTokenSource sessionCts, CancellationToken ct)
    {
        var filePaths = targets.Select(t => t.FilePath).ToList();
        var encodings = targets.ToDictionary(t => t.FilePath, t => t.Encoding, StringComparer.OrdinalIgnoreCase);
        var request = CreateSearchRequest(filePaths);
        var results = await _searchService.SearchFilesAsync(request, encodings, ct);

        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        foreach (var result in results)
        {
            if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
                return;

            MergeResult(result);
        }
    }

    private void InitializeTailTrackers(IReadOnlyList<SearchTarget> targets, CancellationTokenSource sessionCts)
    {
        DetachTailTrackers();
        foreach (var target in targets)
        {
            var baselineLine = Math.Max(0, target.Tab.TotalLines);
            var tracker = new TailSearchTracker
            {
                FilePath = target.FilePath,
                Encoding = target.Encoding,
                Tab = target.Tab,
                SnapshotLine = baselineLine,
                LastProcessedLine = baselineLine,
                SearchContentVersion = target.Tab.SearchContentVersion
            };
            PropertyChangedEventHandler propertyChangedHandler = (_, e) => OnTailTrackerPropertyChanged(tracker, e, sessionCts);
            tracker.PropertyChangedHandler = propertyChangedHandler;
            target.Tab.PropertyChanged += propertyChangedHandler;
            _tailTrackers[target.FilePath] = tracker;
        }
    }

    private async Task RunSnapshotBackfillAsync(IReadOnlyList<SearchTarget> targets, CancellationTokenSource sessionCts, CancellationToken ct)
    {
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentSession(sessionCts))
                return;

            if (!_tailTrackers.TryGetValue(target.FilePath, out var tracker))
                continue;

            if (tracker.SnapshotLine <= 0)
                continue;

            var expectedContentVersion = tracker.SearchContentVersion;
            var snapshotEndLine = tracker.SnapshotLine;
            var request = CreateSearchRequest(new List<string> { target.FilePath }, startLineNumber: 1, endLineNumber: snapshotEndLine);
            var result = await _searchService.SearchFileAsync(target.FilePath, request, target.Encoding, ct);
            if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
                return;

            if (tracker.Tab.SearchContentVersion != expectedContentVersion ||
                tracker.SearchContentVersion != expectedContentVersion)
            {
                continue;
            }

            MergeResult(result);
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
                    SetBaseStatusText(BuildTailStatus());
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
            ResetTailTrackerStateForContentReset(tracker);

        var currentTotalLines = Math.Max(0, tracker.Tab.TotalLines);
        if (currentTotalLines < tracker.LastProcessedLine)
        {
            ResetTailTrackerStateForContentReset(tracker);
            currentTotalLines = Math.Max(0, tracker.Tab.TotalLines);
        }

        if (currentTotalLines <= tracker.LastProcessedLine)
            return TailTrackerProcessOutcome.NoWork;

        var expectedContentVersion = tracker.SearchContentVersion;
        var searchEndLine = currentTotalLines;
        var startLine = tracker.LastProcessedLine + 1;
        var request = CreateSearchRequest(
            new List<string> { tracker.FilePath },
            startLineNumber: startLine,
            endLineNumber: searchEndLine);
        var result = await _searchService.SearchFileAsync(tracker.FilePath, request, tracker.Encoding, ct);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return TailTrackerProcessOutcome.NoWork;

        if (tracker.Tab.SearchContentVersion != expectedContentVersion ||
            tracker.SearchContentVersion != expectedContentVersion)
        {
            return TailTrackerProcessOutcome.NoWork;
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            MergeResult(result);
            return TailTrackerProcessOutcome.RetryPendingRange;
        }

        tracker.LastProcessedLine = searchEndLine;
        MergeResult(result);
        return TailTrackerProcessOutcome.Success;
    }

    private IReadOnlyList<SearchTarget> BuildSearchTargets(WorkspaceScopeSnapshot scopeSnapshot)
    {
        if (AllFiles)
        {
            return scopeSnapshot.OpenTabs
                .Select(tabSnapshot => new SearchTarget
                {
                    FilePath = tabSnapshot.FilePath,
                    Encoding = tabSnapshot.Tab.EffectiveEncoding,
                    Tab = tabSnapshot.Tab
                })
                .ToList();
        }

        if (_mainVm.SelectedTab == null)
            return Array.Empty<SearchTarget>();

        return new[]
        {
            new SearchTarget
            {
                FilePath = _mainVm.SelectedTab.FilePath,
                Encoding = _mainVm.SelectedTab.EffectiveEncoding,
                Tab = _mainVm.SelectedTab
            }
        };
    }

    private SearchRequest CreateSearchRequest(IReadOnlyList<string> filePaths, long? startLineNumber = null, long? endLineNumber = null)
    {
        return new SearchRequest
        {
            Query = Query,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            WholeWord = WholeWord,
            FilePaths = filePaths.ToList(),
            StartLineNumber = startLineNumber,
            EndLineNumber = endLineNumber
        };
    }

    private void ResetTailTrackerStateForContentReset(TailSearchTracker tracker)
    {
        tracker.SnapshotLine = 0;
        tracker.LastProcessedLine = 0;
        tracker.SearchContentVersion = tracker.Tab.SearchContentVersion;
        ClearResultForFile(tracker.FilePath);
    }

    private void ClearResultForFile(string filePath)
    {
        _filesWithParseableTimestamps.Remove(filePath);

        if (!_resultsByFilePath.Remove(filePath, out var fileResultVm))
            return;

        _totalHits -= fileResultVm.HitCount;
        Results.Remove(fileResultVm);
    }

    private void MergeResult(SearchResult result)
    {
        RestoreVisibleExecutionStateFromActiveSessionIfNeeded();

        if (result.HasParseableTimestamps)
            _filesWithParseableTimestamps.Add(result.FilePath);

        if (!_resultsByFilePath.TryGetValue(result.FilePath, out var fileResultVm))
        {
            if (result.Hits.Count == 0 && string.IsNullOrWhiteSpace(result.Error))
                return;

            fileResultVm = new FileSearchResultViewModel(new SearchResult { FilePath = result.FilePath }, _mainVm, LineOrder);
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
        fileResultVm.AddHits(result.Hits, LineOrder);
        _totalHits += fileResultVm.HitCount - hitsBefore;
        fileResultVm.SetError(result.Error);
    }

    private void CacheResultFileOrder(IReadOnlyList<SearchTarget> targets)
    {
        _resultFileOrderByPath.Clear();
        if (!AllFiles || targets.Count == 0)
            return;

        var targetPaths = targets
            .Select(target => target.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextIndex = 0;

        foreach (var filePath in _mainVm.GetSearchResultFileOrderSnapshot())
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
        if (!_resultFileOrderByPath.TryGetValue(fileResultVm.FilePath, out var targetOrder))
        {
            Results.Add(fileResultVm);
            return;
        }

        for (var index = 0; index < Results.Count; index++)
        {
            if (_resultFileOrderByPath.TryGetValue(Results[index].FilePath, out var existingOrder) &&
                existingOrder > targetOrder)
            {
                Results.Insert(index, fileResultVm);
                return;
            }
        }

        Results.Add(fileResultVm);
    }

    private void ApplyLineOrderToResults(SearchResultLineOrder lineOrder)
    {
        foreach (var result in _resultsByFilePath.Values)
            result.ApplyLineOrder(lineOrder);
    }

    private string BuildSnapshotStatus()
    {
        var filesWithHits = _resultsByFilePath.Values.Count(r => r.HitCount > 0);
        return $"{_totalHits:N0} in {filesWithHits} file(s)";
    }

    private string BuildTailStatus()
    {
        var filesWithHits = _resultsByFilePath.Values.Count(r => r.HitCount > 0);
        return _activeSearchDataMode switch
        {
            SearchDataMode.Tail => $"Monitoring tail: {_totalHits:N0} in {filesWithHits} file(s)",
            SearchDataMode.SnapshotAndTail when _snapshotBackfillComplete =>
                $"Snapshot + Monitoring Tail: {_totalHits:N0} in {filesWithHits} file(s)",
            SearchDataMode.SnapshotAndTail =>
                $"Monitoring tail + backfill: {_totalHits:N0} in {filesWithHits} file(s)",
            _ => BuildSnapshotStatus()
        };
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
        _activeSessionExecutionState = null;

        if (updateUi && current != null)
        {
            IsSearching = false;
            SetBaseStatusText("Search cancelled");
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
        CancelActiveSearchSession(updateUi: false);
        IsSearching = false;
        ClearVisibleResults();
        _resultFileOrderByPath.Clear();
        _baseStatusText = string.Empty;
        _visibleOutputExecutionState = null;
        _showScopeExitCancelledStatus = false;
        _snapshotBackfillComplete = false;
        RefreshVisibleStatusText();
    }

    [RelayCommand]
    private async Task GoToTimestamp()
    {
        var result = await _mainVm.NavigateToTimestampAsync(NavigateTimestamp);
        GoToErrorText = result.ErrorText;
    }

    [RelayCommand]
    private async Task GoToLine()
    {
        var result = await _mainVm.NavigateToLineAsync(NavigateLineNumber);
        GoToErrorText = result.ErrorText;
    }

    public void Dispose()
    {
        CancelActiveSearchSession(updateUi: false);
        PersistActiveScopeState();
    }

    internal void OnScopeChanging(WorkspaceScopeKey nextScopeKey)
    {
        if (nextScopeKey.Equals(_activeScopeKey))
            return;

        var activeState = CaptureCurrentScopeState();
        if (IsSearching)
        {
            var hadVisibleOutput = activeState.Results.Count > 0 ||
                                   !string.IsNullOrWhiteSpace(activeState.BaseStatusText) ||
                                   activeState.ExecutionState != null;
            CancelActiveSearchSession(updateUi: false);
            IsSearching = false;
            activeState.IsSearching = false;
            activeState.ScopeExitCancelled = hadVisibleOutput;
        }

        _scopeStates[_activeScopeKey] = activeState;
        _pendingScopeKey = nextScopeKey;
    }

    internal void OnScopeContextChanged()
    {
        var scopeSnapshot = _mainVm.GetActiveScopeSnapshot();
        _activeScopeSnapshot = scopeSnapshot;
        if (scopeSnapshot.ScopeKey.Equals(_activeScopeKey) && _pendingScopeKey == null)
        {
            RefreshVisibleStatusText();
            return;
        }

        _activeScopeKey = scopeSnapshot.ScopeKey;
        _pendingScopeKey = null;
        RestoreScopeState(GetOrCreateScopeState(_activeScopeKey));
    }

    internal void OnSelectedTabChanged(LogTabViewModel? selectedTab)
    {
        if (_pendingScopeKey != null)
            return;

        RefreshVisibleStatusText();
    }

    private ScopeOwnedSearchState GetOrCreateScopeState(WorkspaceScopeKey scopeKey)
    {
        if (_scopeStates.TryGetValue(scopeKey, out var existingState))
            return CloneScopeState(existingState);

        return new ScopeOwnedSearchState();
    }

    private void PersistActiveScopeState()
    {
        _scopeStates[_activeScopeKey] = CaptureCurrentScopeState();
    }

    private ScopeOwnedSearchState CaptureCurrentScopeState()
    {
        return new ScopeOwnedSearchState
        {
            Query = Query,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            WholeWord = WholeWord,
            AllFiles = AllFiles,
            FromTimestamp = FromTimestamp,
            ToTimestamp = ToTimestamp,
            SearchDataMode = SearchDataMode,
            LineOrder = LineOrder,
            Results = CaptureResultStates(),
            BaseStatusText = _baseStatusText,
            ExecutionState = CloneExecutionState(_visibleOutputExecutionState),
            ScopeExitCancelled = _showScopeExitCancelledStatus,
            IsSearching = IsSearching
        };
    }

    private void RestoreScopeState(ScopeOwnedSearchState state)
    {
        Query = state.Query;
        IsRegex = state.IsRegex;
        CaseSensitive = state.CaseSensitive;
        WholeWord = state.WholeWord;
        AllFiles = state.AllFiles;
        FromTimestamp = state.FromTimestamp;
        ToTimestamp = state.ToTimestamp;
        SearchDataMode = state.SearchDataMode;

        ClearVisibleResults();
        LineOrder = state.LineOrder;
        RestoreResultStates(state.Results);

        _activeSearchDataMode = state.SearchDataMode;
        _baseStatusText = state.BaseStatusText;
        _visibleOutputExecutionState = CloneExecutionState(state.ExecutionState);
        _activeSessionExecutionState = null;
        _showScopeExitCancelledStatus = state.ScopeExitCancelled;
        _snapshotBackfillComplete = false;
        IsSearching = false;
        RefreshVisibleStatusText();
    }

    private List<FileSearchResultState> CaptureResultStates()
        => Results.Select(result => result.CaptureState()).ToList();

    private void RestoreResultStates(IReadOnlyList<FileSearchResultState> resultStates)
    {
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
                LineOrder)
            {
                IsExpanded = resultState.IsExpanded
            };

            _resultsByFilePath[resultVm.FilePath] = resultVm;
            Results.Add(resultVm);
            _totalHits += resultVm.HitCount;
        }
    }

    private void ClearVisibleResults()
    {
        Results.Clear();
        _resultsByFilePath.Clear();
        _filesWithParseableTimestamps.Clear();
        _totalHits = 0;
    }

    private void RestoreVisibleExecutionStateFromActiveSessionIfNeeded()
    {
        if (_visibleOutputExecutionState == null && _activeSessionExecutionState != null)
            _visibleOutputExecutionState = CloneExecutionState(_activeSessionExecutionState);
    }

    private SearchExecutionState? CreateExecutionState(IReadOnlyList<SearchTarget> targets)
    {
        if (targets.Count == 0)
            return null;

        return AllFiles
            ? new CurrentScopeExecutionState(targets
                .Select(target => new SearchTargetExecutionEntry(target.Tab.TabInstanceId, target.FilePath))
                .ToList())
            : new CurrentFileExecutionState(targets[0].Tab.TabInstanceId, targets[0].FilePath);
    }

    private void SetBaseStatusText(string statusText)
    {
        _baseStatusText = statusText;
        if (!string.IsNullOrWhiteSpace(statusText))
            RestoreVisibleExecutionStateFromActiveSessionIfNeeded();

        RefreshVisibleStatusText();
    }

    private void RefreshVisibleStatusText()
    {
        StatusText = GetVisibleStatusText();
    }

    private string GetVisibleStatusText()
    {
        if (_showScopeExitCancelledStatus)
            return ScopeExitCancelledStatusText;

        if (_visibleOutputExecutionState is CurrentFileExecutionState currentFileExecutionState &&
            !MatchesCurrentFileExecution(currentFileExecutionState))
        {
            return CurrentFileStaleStatusText;
        }

        if (_visibleOutputExecutionState is CurrentScopeExecutionState currentScopeExecutionState &&
            !MatchesCurrentScopeExecution(currentScopeExecutionState))
        {
            return CurrentScopeStaleStatusText;
        }

        return _baseStatusText;
    }

    private bool MatchesCurrentFileExecution(CurrentFileExecutionState executionState)
    {
        var selectedTab = _mainVm.SelectedTab;
        return selectedTab != null &&
               string.Equals(selectedTab.TabInstanceId, executionState.TabInstanceId, StringComparison.Ordinal) &&
               string.Equals(selectedTab.FilePath, executionState.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesCurrentScopeExecution(CurrentScopeExecutionState executionState)
    {
        var currentOpenTabs = _activeScopeSnapshot.OpenTabs;
        if (currentOpenTabs.Count != executionState.OpenTabs.Count)
            return false;

        for (var i = 0; i < currentOpenTabs.Count; i++)
        {
            if (!string.Equals(currentOpenTabs[i].TabInstanceId, executionState.OpenTabs[i].TabInstanceId, StringComparison.Ordinal) ||
                !string.Equals(currentOpenTabs[i].FilePath, executionState.OpenTabs[i].FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static ScopeOwnedSearchState CloneScopeState(ScopeOwnedSearchState state)
    {
        return new ScopeOwnedSearchState
        {
            Query = state.Query,
            IsRegex = state.IsRegex,
            CaseSensitive = state.CaseSensitive,
            WholeWord = state.WholeWord,
            AllFiles = state.AllFiles,
            FromTimestamp = state.FromTimestamp,
            ToTimestamp = state.ToTimestamp,
            SearchDataMode = state.SearchDataMode,
            LineOrder = state.LineOrder,
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
            ExecutionState = CloneExecutionState(state.ExecutionState),
            ScopeExitCancelled = state.ScopeExitCancelled,
            IsSearching = state.IsSearching
        };
    }

    private static SearchExecutionState? CloneExecutionState(SearchExecutionState? executionState)
    {
        return executionState switch
        {
            CurrentFileExecutionState currentFile => new CurrentFileExecutionState(
                currentFile.TabInstanceId,
                currentFile.FilePath),
            CurrentScopeExecutionState currentScope => new CurrentScopeExecutionState(
                currentScope.OpenTabs
                    .Select(tab => new SearchTargetExecutionEntry(tab.TabInstanceId, tab.FilePath))
                    .ToList()),
            _ => null
        };
    }

    private enum TailTrackerProcessOutcome
    {
        NoWork,
        Success,
        RetryPendingRange
    }

    private sealed class SearchTarget
    {
        public string FilePath { get; init; } = string.Empty;
        public FileEncoding Encoding { get; init; }
        public LogTabViewModel Tab { get; init; } = null!;
    }

    private sealed class TailSearchTracker
    {
        public string FilePath { get; init; } = string.Empty;
        public FileEncoding Encoding { get; init; }
        public LogTabViewModel Tab { get; init; } = null!;
        public long SnapshotLine { get; set; }
        public long LastProcessedLine { get; set; }
        public int SearchContentVersion { get; set; }
        public PropertyChangedEventHandler? PropertyChangedHandler { get; set; }
        public int PendingSignalVersion;
        public int IsDrainActive;
    }

    private abstract record SearchExecutionState;

    private sealed record CurrentFileExecutionState(string TabInstanceId, string FilePath) : SearchExecutionState;

    private sealed record CurrentScopeExecutionState(IReadOnlyList<SearchTargetExecutionEntry> OpenTabs) : SearchExecutionState;

    private sealed record SearchTargetExecutionEntry(string TabInstanceId, string FilePath);

    private sealed class ScopeOwnedSearchState
    {
        public string Query { get; init; } = string.Empty;
        public bool IsRegex { get; init; }
        public bool CaseSensitive { get; init; }
        public bool WholeWord { get; init; }
        public bool AllFiles { get; init; }
        public string FromTimestamp { get; init; } = string.Empty;
        public string ToTimestamp { get; init; } = string.Empty;
        public SearchDataMode SearchDataMode { get; init; } = SearchDataMode.DiskSnapshot;
        public SearchResultLineOrder LineOrder { get; init; } = SearchResultLineOrder.Ascending;
        public List<FileSearchResultState> Results { get; init; } = new();
        public string BaseStatusText { get; init; } = string.Empty;
        public SearchExecutionState? ExecutionState { get; init; }
        public bool ScopeExitCancelled { get; set; }
        public bool IsSearching { get; set; }
    }
}
