namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    private const string SearchResultsClearedStatusText = "Results cleared because context, target, or source changed. Return to the original context to restore them or rerun search.";
    private readonly ISearchService _searchService;
    private readonly ILogWorkspaceContext _mainVm;
    private readonly Dictionary<WorkspaceScopeKey, ScopeOwnedSearchState> _scopeStates = new();
    private readonly Dictionary<string, FileSearchResultViewModel> _resultsByFilePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _resultFileOrderByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TailSearchTracker> _tailTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _filesWithParseableTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SearchOutputCacheKey, CachedSearchOutputState> _cachedOutputs = new();
    private WorkspaceScopeKey _activeScopeKey;
    private WorkspaceScopeSnapshot _activeScopeSnapshot;
    private WorkspaceScopeKey? _pendingScopeKey;
    private CancellationTokenSource? _searchCts;
    private SearchDataMode _activeSearchDataMode = SearchDataMode.DiskSnapshot;
    private string _baseStatusText = string.Empty;
    private SearchExecutionState? _visibleOutputExecutionState;
    private SearchExecutionState? _activeSessionExecutionState;
    private SearchOutputCacheKey? _visibleOutputCacheKey;
    private SearchOutputCacheKey? _activeSessionOutputCacheKey;
    private string _invalidationStatusText = string.Empty;
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
    private SearchFilterTargetMode _targetMode = SearchFilterTargetMode.CurrentTab;

    [ObservableProperty]
    private string _fromTimestamp = string.Empty;

    [ObservableProperty]
    private string _toTimestamp = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private SearchDataMode _searchDataMode = SearchDataMode.DiskSnapshot;

    [ObservableProperty]
    private SearchResultLineOrder _lineOrder = SearchResultLineOrder.Ascending;

    public bool IsCurrentTabTarget
    {
        get => TargetMode == SearchFilterTargetMode.CurrentTab;
        set
        {
            if (value)
                TargetMode = SearchFilterTargetMode.CurrentTab;
        }
    }

    public bool IsCurrentScopeTarget
    {
        get => TargetMode == SearchFilterTargetMode.CurrentScope;
        set
        {
            if (value)
                TargetMode = SearchFilterTargetMode.CurrentScope;
        }
    }

    partial void OnTargetModeChanged(SearchFilterTargetMode value)
    {
        OnPropertyChanged(nameof(IsCurrentTabTarget));
        OnPropertyChanged(nameof(IsCurrentScopeTarget));
        RefreshVisibleStatusText();
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

    public SearchResultsFlatCollection VisibleRows { get; } = new();

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
        RefreshVisibleStatusText();
    }

    partial void OnLineOrderChanged(SearchResultLineOrder value)
    {
        OnPropertyChanged(nameof(IsAscendingLineOrder));
        OnPropertyChanged(nameof(IsDescendingLineOrder));
        ApplyLineOrderToResults(value);
        SyncCurrentVisibleOutputToCache();
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
        _invalidationStatusText = string.Empty;

        ClearVisibleResults();
        _resultFileOrderByPath.Clear();
        DetachTailTrackers();
        _snapshotBackfillComplete = false;
        RemoveCachedOutputForCurrentContext();
        IsSearching = true;
        SetBaseStatusText("Searching...");

        try
        {
            var targets = await BuildSearchTargetsAsync(_activeScopeSnapshot, selectedMode, ct);
            _activeSessionExecutionState = CreateExecutionState(targets);
            _activeSessionOutputCacheKey = BuildOutputCacheKey(TargetMode, selectedMode, _activeSessionExecutionState);
            _visibleOutputExecutionState = CloneExecutionState(_activeSessionExecutionState);
            _visibleOutputCacheKey = _activeSessionOutputCacheKey;
            CacheResultFileOrder(targets);
            if (targets.Count == 0)
            {
                _activeSessionExecutionState = null;
                _activeSessionOutputCacheKey = null;
                _visibleOutputExecutionState = null;
                _visibleOutputCacheKey = null;
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
                    _activeSessionOutputCacheKey = null;
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
                {
                    _activeSessionExecutionState = null;
                    _activeSessionOutputCacheKey = null;
                }
            }
        }
    }

    private async Task RunDiskSnapshotSearchAsync(IReadOnlyList<SearchTarget> targets, CancellationTokenSource sessionCts, CancellationToken ct)
    {
        var filePaths = targets.Select(t => t.FilePath).ToList();
        var encodings = targets.ToDictionary(t => t.FilePath, t => t.Encoding, StringComparer.OrdinalIgnoreCase);
        var request = CreateSearchRequest(filePaths, SearchDataMode.DiskSnapshot, GetApplicableFilterSnapshots());
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
            var request = CreateSearchRequest(
                new List<string> { target.FilePath },
                SearchDataMode.SnapshotAndTail,
                GetApplicableFilterSnapshotMap(target.FilePath),
                startLineNumber: 1,
                endLineNumber: snapshotEndLine);
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
        var filterSnapshot = GetApplicableFilterSnapshot(tracker.FilePath);
        if (filterSnapshot?.LastEvaluatedLine < searchEndLine &&
            filterSnapshot.FilterRequest?.SourceMode != SearchRequestSourceMode.DiskSnapshot)
        {
            return TailTrackerProcessOutcome.RetryPendingRange;
        }

        var request = CreateSearchRequest(
            new List<string> { tracker.FilePath },
            _activeSearchDataMode,
            GetApplicableFilterSnapshotMap(tracker.FilePath),
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

    private async Task<IReadOnlyList<SearchTarget>> BuildSearchTargetsAsync(
        WorkspaceScopeSnapshot scopeSnapshot,
        SearchDataMode selectedMode,
        CancellationToken ct)
    {
        if (TargetMode == SearchFilterTargetMode.CurrentTab)
        {
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

        var membershipPaths = scopeSnapshot.EffectiveMembership
            .Select(member => member.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (membershipPaths.Count == 0)
            return Array.Empty<SearchTarget>();

        var openTabsByPath = scopeSnapshot.OpenTabs
            .GroupBy(tab => tab.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Tab, StringComparer.OrdinalIgnoreCase);

        if (selectedMode != SearchDataMode.DiskSnapshot)
        {
            var materializedTabs = await _mainVm.EnsureBackgroundTabsOpenAsync(
                membershipPaths.Where(path => !openTabsByPath.ContainsKey(path)).ToList(),
                _mainVm.ActiveScopeDashboardId,
                ct);
            foreach (var (filePath, tab) in materializedTabs)
                openTabsByPath[filePath] = tab;
        }

        var targets = new List<SearchTarget>(membershipPaths.Count);
        foreach (var filePath in membershipPaths)
        {
            openTabsByPath.TryGetValue(filePath, out var tab);
            var encoding = tab?.EffectiveEncoding ??
                           await _mainVm.ResolveFilterFileEncodingAsync(filePath, _mainVm.ActiveScopeDashboardId, ct);
            targets.Add(new SearchTarget
            {
                FilePath = filePath,
                Encoding = encoding,
                Tab = tab
            });
        }

        return targets;
    }

    private SearchRequest CreateSearchRequest(
        IReadOnlyList<string> filePaths,
        SearchDataMode sourceMode,
        IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot>? filterSnapshots = null,
        long? startLineNumber = null,
        long? endLineNumber = null)
    {
        return new SearchRequest
        {
            Query = Query,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            FilePaths = filePaths.ToList(),
            AllowedLineNumbersByFilePath = BuildAllowedLineNumbers(filePaths, filterSnapshots),
            StartLineNumber = startLineNumber,
            EndLineNumber = endLineNumber,
            FromTimestamp = string.IsNullOrWhiteSpace(FromTimestamp) ? null : FromTimestamp.Trim(),
            ToTimestamp = string.IsNullOrWhiteSpace(ToTimestamp) ? null : ToTimestamp.Trim(),
            SourceMode = ToRequestSourceMode(sourceMode)
        };
    }

    private IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableFilterSnapshots()
    {
        if (TargetMode == SearchFilterTargetMode.CurrentScope)
            return _mainVm.GetApplicableCurrentScopeFilterSnapshots(SearchDataMode);

        var snapshot = _mainVm.GetApplicableCurrentTabFilterSnapshot(SearchDataMode);
        if (snapshot == null || _mainVm.SelectedTab == null)
            return new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [_mainVm.SelectedTab.FilePath] = snapshot
        };
    }

    private LogFilterSession.FilterSnapshot? GetApplicableFilterSnapshot(string filePath)
    {
        if (TargetMode == SearchFilterTargetMode.CurrentScope)
            return _mainVm.GetApplicableCurrentScopeFilterSnapshot(filePath, SearchDataMode);

        var snapshot = _mainVm.GetApplicableCurrentTabFilterSnapshot(SearchDataMode);
        return snapshot != null && _mainVm.SelectedTab != null &&
               string.Equals(_mainVm.SelectedTab.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
            ? snapshot
            : null;
    }

    private IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableFilterSnapshotMap(string filePath)
    {
        var snapshot = GetApplicableFilterSnapshot(filePath);
        if (snapshot == null)
            return new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [filePath] = snapshot
        };
    }

    private static Dictionary<string, List<int>> BuildAllowedLineNumbers(
        IReadOnlyList<string> filePaths,
        IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot>? filterSnapshots)
    {
        var allowed = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        if (filterSnapshots == null || filterSnapshots.Count == 0)
            return allowed;

        foreach (var filePath in filePaths)
        {
            if (!filterSnapshots.TryGetValue(filePath, out var snapshot))
                continue;

            allowed[filePath] = snapshot.MatchingLineNumbers
                .Where(line => line > 0)
                .Distinct()
                .OrderBy(line => line)
                .ToList();
        }

        return allowed;
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
        RefreshVisibleRows();
        SyncCurrentVisibleOutputToCache();
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
        fileResultVm.AddHits(result.Hits, LineOrder);
        _totalHits += fileResultVm.HitCount - hitsBefore;
        fileResultVm.SetError(result.Error);
        SyncCurrentVisibleOutputToCache();
    }

    private void CacheResultFileOrder(IReadOnlyList<SearchTarget> targets)
    {
        _resultFileOrderByPath.Clear();
        if (TargetMode != SearchFilterTargetMode.CurrentScope || targets.Count == 0)
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
            RefreshVisibleRows();
            return;
        }

        for (var index = 0; index < Results.Count; index++)
        {
            if (_resultFileOrderByPath.TryGetValue(Results[index].FilePath, out var existingOrder) &&
                existingOrder > targetOrder)
            {
                Results.Insert(index, fileResultVm);
                RefreshVisibleRows();
                return;
            }
        }

        Results.Add(fileResultVm);
        RefreshVisibleRows();
    }

    private void ApplyLineOrderToResults(SearchResultLineOrder lineOrder)
    {
        foreach (var result in _resultsByFilePath.Values)
            result.ApplyLineOrder(lineOrder);

        RefreshVisibleRows();
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
        _activeSessionOutputCacheKey = null;

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
        _visibleOutputCacheKey = null;
        _invalidationStatusText = string.Empty;
        _showScopeExitCancelledStatus = false;
        _snapshotBackfillComplete = false;
        RemoveCachedOutputForCurrentContext();
        RefreshVisibleStatusText();
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
            if (hadVisibleOutput &&
                activeState.ExecutionState != null &&
                BuildOutputCacheKey(activeState.TargetMode, activeState.SearchDataMode, activeState.ExecutionState) is { } cacheKey &&
                activeState.CachedOutputs.TryGetValue(cacheKey, out var cachedOutput))
            {
                activeState.CachedOutputs[cacheKey] = new CachedSearchOutputState
                {
                    Results = cachedOutput.Results,
                    BaseStatusText = cachedOutput.BaseStatusText,
                    ExecutionState = CloneExecutionState(cachedOutput.ExecutionState),
                    ScopeExitCancelled = true
                };
            }
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
        SyncCurrentVisibleOutputToCache();
        return new ScopeOwnedSearchState
        {
            Query = Query,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            TargetMode = TargetMode,
            FromTimestamp = FromTimestamp,
            ToTimestamp = ToTimestamp,
            SearchDataMode = SearchDataMode,
            LineOrder = LineOrder,
            Results = CaptureResultStates(),
            BaseStatusText = _baseStatusText,
            ExecutionState = CloneExecutionState(_visibleOutputExecutionState),
            ScopeExitCancelled = _showScopeExitCancelledStatus,
            IsSearching = IsSearching,
            CachedOutputs = CloneCachedOutputs(_cachedOutputs)
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

        _cachedOutputs.Clear();
        foreach (var (key, output) in state.CachedOutputs)
            _cachedOutputs[key] = CloneCachedOutputState(output);

        ClearVisibleResults();
        LineOrder = state.LineOrder;
        _activeSearchDataMode = state.SearchDataMode;
        _baseStatusText = string.Empty;
        _visibleOutputExecutionState = null;
        _visibleOutputCacheKey = null;
        _activeSessionExecutionState = null;
        _showScopeExitCancelledStatus = state.ScopeExitCancelled;
        _invalidationStatusText = state.CachedOutputs.Count > 0
            ? SearchResultsClearedStatusText
            : string.Empty;
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
                LineOrder,
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
        Results.Clear();
        _resultsByFilePath.Clear();
        _filesWithParseableTimestamps.Clear();
        _totalHits = 0;
        RefreshVisibleRows();
    }

    private void RestoreVisibleExecutionStateFromActiveSessionIfNeeded()
    {
        if (_visibleOutputExecutionState == null && _activeSessionExecutionState != null)
        {
            _visibleOutputExecutionState = CloneExecutionState(_activeSessionExecutionState);
            _visibleOutputCacheKey = _activeSessionOutputCacheKey;
        }
    }

    private SearchExecutionState? CreateExecutionState(IReadOnlyList<SearchTarget> targets)
    {
        if (targets.Count == 0)
            return null;

        var firstTab = targets[0].Tab;
        return TargetMode == SearchFilterTargetMode.CurrentScope
            ? new CurrentScopeExecutionState(BuildOrderedScopeExecutionPaths(targets.Select(target => target.FilePath)))
            : firstTab == null
                ? null
                : new CurrentTabExecutionState(firstTab.TabInstanceId, targets[0].FilePath);
    }

    private void SetBaseStatusText(string statusText)
    {
        _baseStatusText = statusText;
        if (!string.IsNullOrWhiteSpace(statusText))
            RestoreVisibleExecutionStateFromActiveSessionIfNeeded();

        SyncCurrentVisibleOutputToCache();
        RefreshVisibleStatusText();
    }

    private void RefreshVisibleStatusText()
    {
        ReconcileVisibleOutput();
        StatusText = GetVisibleStatusText();
    }

    private string GetVisibleStatusText()
    {
        if (_showScopeExitCancelledStatus)
            return ScopeExitCancelledStatusText;

        if (!string.IsNullOrWhiteSpace(_invalidationStatusText))
            return _invalidationStatusText;

        return _baseStatusText;
    }

    private bool MatchesCurrentTabExecution(CurrentTabExecutionState executionState)
    {
        var selectedTab = _mainVm.SelectedTab;
        return selectedTab != null &&
               string.Equals(selectedTab.TabInstanceId, executionState.TabInstanceId, StringComparison.Ordinal) &&
               string.Equals(selectedTab.FilePath, executionState.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesCurrentScopeExecution(CurrentScopeExecutionState executionState)
    {
        var currentOrderedPaths = BuildOrderedScopeExecutionPaths(
            _activeScopeSnapshot.EffectiveMembership.Select(member => member.FilePath));
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

    private IReadOnlyList<string> BuildOrderedScopeExecutionPaths(IEnumerable<string> targetPaths)
    {
        var orderedPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedTargetPaths = targetPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .ToList();

        if (normalizedTargetPaths.Count == 0)
            return normalizedTargetPaths;

        foreach (var filePath in _mainVm.GetSearchResultFileOrderSnapshot()
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath))
        {
            if (seenPaths.Add(filePath) && normalizedTargetPaths.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                orderedPaths.Add(filePath);
        }

        foreach (var filePath in normalizedTargetPaths)
        {
            if (seenPaths.Add(filePath))
                orderedPaths.Add(filePath);
        }

        return orderedPaths;
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
            IsSearching = state.IsSearching,
            CachedOutputs = CloneCachedOutputs(state.CachedOutputs)
        };
    }

    private void ReconcileVisibleOutput()
    {
        if (_pendingScopeKey != null)
            return;

        var currentOutputCacheKey = CreateCurrentOutputCacheKey();
        if (_visibleOutputCacheKey != null &&
            !Equals(_visibleOutputCacheKey, currentOutputCacheKey))
        {
            ClearVisibleResults();
            _visibleOutputExecutionState = null;
            _visibleOutputCacheKey = null;
            _invalidationStatusText = SearchResultsClearedStatusText;
            _showScopeExitCancelledStatus = false;
        }

        if (_visibleOutputCacheKey == null &&
            currentOutputCacheKey != null &&
            _cachedOutputs.TryGetValue(currentOutputCacheKey, out var cachedOutput))
        {
            RestoreCachedOutput(currentOutputCacheKey, cachedOutput);
        }
    }

    private void RestoreCachedOutput(SearchOutputCacheKey cacheKey, CachedSearchOutputState cachedOutput)
    {
        ClearVisibleResults();
        RestoreResultStates(cachedOutput.Results);
        _baseStatusText = cachedOutput.BaseStatusText;
        _visibleOutputExecutionState = CloneExecutionState(cachedOutput.ExecutionState);
        _visibleOutputCacheKey = cacheKey;
        _showScopeExitCancelledStatus = cachedOutput.ScopeExitCancelled;
        _invalidationStatusText = string.Empty;
    }

    private void SyncCurrentVisibleOutputToCache()
    {
        if (_visibleOutputCacheKey == null)
            return;

        if (_visibleOutputExecutionState == null &&
            Results.Count == 0 &&
            string.IsNullOrWhiteSpace(_baseStatusText))
        {
            _cachedOutputs.Remove(_visibleOutputCacheKey);
            return;
        }

        _cachedOutputs[_visibleOutputCacheKey] = new CachedSearchOutputState
        {
            Results = CaptureResultStates(),
            BaseStatusText = _baseStatusText,
            ExecutionState = CloneExecutionState(_visibleOutputExecutionState),
            ScopeExitCancelled = _showScopeExitCancelledStatus
        };
    }

    private FileSearchResultViewModel CreateFileResultViewModel(SearchResult result)
        => new(result, _mainVm, LineOrder, OnResultPresentationChanged);

    private void OnResultPresentationChanged()
    {
        RefreshVisibleRows();
        SyncCurrentVisibleOutputToCache();
    }

    private void RefreshVisibleRows()
    {
        VisibleRows.Refresh(Results);
    }

    private void RemoveCachedOutputForCurrentContext()
    {
        var currentOutputCacheKey = CreateCurrentOutputCacheKey();
        if (currentOutputCacheKey != null)
            _cachedOutputs.Remove(currentOutputCacheKey);
    }

    private SearchOutputCacheKey? CreateCurrentOutputCacheKey()
        => BuildOutputCacheKey(TargetMode, SearchDataMode, CreateCurrentExecutionState());

    private SearchExecutionState? CreateCurrentExecutionState()
    {
        return TargetMode == SearchFilterTargetMode.CurrentScope
            ? new CurrentScopeExecutionState(BuildOrderedScopeExecutionPaths(
                _activeScopeSnapshot.EffectiveMembership.Select(member => member.FilePath)))
            : _mainVm.SelectedTab == null
                ? null
                : new CurrentTabExecutionState(_mainVm.SelectedTab.TabInstanceId, _mainVm.SelectedTab.FilePath);
    }

    private static SearchOutputCacheKey? BuildOutputCacheKey(
        SearchFilterTargetMode targetMode,
        SearchDataMode searchDataMode,
        SearchExecutionState? executionState)
    {
        if (executionState == null)
            return null;

        return executionState switch
        {
            CurrentTabExecutionState currentTab => new SearchOutputCacheKey(
                targetMode,
                searchDataMode,
                $"tab|{currentTab.TabInstanceId}|{Path.GetFullPath(currentTab.FilePath)}"),
            CurrentScopeExecutionState currentScope => new SearchOutputCacheKey(
                targetMode,
                searchDataMode,
                $"scope|{string.Join('\u001F', currentScope.OrderedFilePaths.Select(Path.GetFullPath))}"),
            _ => null
        };
    }

    private static Dictionary<SearchOutputCacheKey, CachedSearchOutputState> CloneCachedOutputs(
        IReadOnlyDictionary<SearchOutputCacheKey, CachedSearchOutputState> cachedOutputs)
    {
        return cachedOutputs.ToDictionary(
            entry => entry.Key,
            entry => CloneCachedOutputState(entry.Value));
    }

    private static CachedSearchOutputState CloneCachedOutputState(CachedSearchOutputState state)
    {
        return new CachedSearchOutputState
        {
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
            ScopeExitCancelled = state.ScopeExitCancelled
        };
    }

    private static SearchExecutionState? CloneExecutionState(SearchExecutionState? executionState)
    {
        return executionState switch
        {
            CurrentTabExecutionState currentTab => new CurrentTabExecutionState(
                currentTab.TabInstanceId,
                currentTab.FilePath),
            CurrentScopeExecutionState currentScope => new CurrentScopeExecutionState(
                currentScope.OrderedFilePaths.ToList()),
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
        public LogTabViewModel? Tab { get; init; }
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

    private sealed record CurrentTabExecutionState(string TabInstanceId, string FilePath) : SearchExecutionState;

    private sealed record CurrentScopeExecutionState(IReadOnlyList<string> OrderedFilePaths) : SearchExecutionState;

    private sealed record SearchOutputCacheKey(
        SearchFilterTargetMode TargetMode,
        SearchDataMode SearchDataMode,
        string ContextSignature);

    private sealed class CachedSearchOutputState
    {
        public List<FileSearchResultState> Results { get; init; } = new();
        public string BaseStatusText { get; init; } = string.Empty;
        public SearchExecutionState? ExecutionState { get; init; }
        public bool ScopeExitCancelled { get; init; }
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
        public SearchResultLineOrder LineOrder { get; init; } = SearchResultLineOrder.Ascending;
        public List<FileSearchResultState> Results { get; init; } = new();
        public string BaseStatusText { get; init; } = string.Empty;
        public SearchExecutionState? ExecutionState { get; init; }
        public bool ScopeExitCancelled { get; set; }
        public bool IsSearching { get; set; }
        public Dictionary<SearchOutputCacheKey, CachedSearchOutputState> CachedOutputs { get; init; } = new();
    }
}
