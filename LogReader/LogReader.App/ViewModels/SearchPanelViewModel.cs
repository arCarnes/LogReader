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

public partial class SearchPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TailRetryDelay = TimeSpan.FromMilliseconds(300);
    private const string ScopeExitCancelledStatusText = "Search stopped when leaving this scope. Rerun search to refresh these results.";
    private const string SearchResultsClearedStatusText = "Results cleared because context, target, or source changed. Return to the original context to restore them or rerun search.";
    private const string ContextChangedCancelledStatusText = "Search stopped because context, target, or source changed. Rerun search to refresh these results.";
    private readonly ISearchService _searchService;
    private readonly ILogWorkspaceContext _mainVm;
    private readonly SearchFilterSharedOptions _sharedOptions;
    private readonly WorkspaceScopedStateStore<ScopeOwnedSearchState> _scopeStateStore;
    private readonly Dictionary<string, FileSearchResultViewModel> _resultsByFilePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _resultFileOrderByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TailSearchTracker> _tailTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _filesWithParseableTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SearchOutputCacheKey, CachedSearchOutputState> _cachedOutputs = new();
    private WorkspaceScopeSnapshot _activeScopeSnapshot;
    private CancellationTokenSource? _searchCts;
    private SearchSessionContext? _activeSessionContext;
    private string _baseStatusText = string.Empty;
    private SearchExecutionState? _visibleOutputExecutionState;
    private SearchExecutionState? _activeSessionExecutionState;
    private SearchOutputCacheKey? _visibleOutputCacheKey;
    private SearchOutputCacheKey? _activeSessionOutputCacheKey;
    private string _invalidationStatusText = string.Empty;
    private SearchOutputFreshness _visibleOutputFreshness;
    private bool _isVisibleOutputCacheDirty;
    private int _resultPresentationUpdateDepth;
    private bool _pendingVisibleRowsRefresh;
    private bool _pendingCacheSync;
    private long _totalHits;
    private bool _snapshotBackfillComplete;
    private SearchStatusPresentation _baseStatusPresentation;

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

    public bool IsCurrentScopeTarget
    {
        get => TargetMode == SearchFilterTargetMode.CurrentScope;
        set
        {
            if (value)
                TargetMode = SearchFilterTargetMode.CurrentScope;
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

    public bool IsSnapshotAndTailMode
    {
        get => SearchDataMode == SearchDataMode.SnapshotAndTail;
        set
        {
            if (value)
                SearchDataMode = SearchDataMode.SnapshotAndTail;
        }
    }

    public string SearchActionButtonText => IsSearching ? "Cancel" : "Clear";

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

        if (!value)
            FlushVisibleOutputCache();
    }

    [RelayCommand]
    private async Task ExecuteSearch()
    {
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
        _activeSessionOutputCacheKey = sessionContext.OutputCacheKey;
        _visibleOutputFreshness = SearchOutputFreshness.None;
        _invalidationStatusText = string.Empty;

        ClearVisibleResults();
        _resultFileOrderByPath.Clear();
        DetachTailTrackers();
        _snapshotBackfillComplete = false;
        RemoveCachedOutputForCurrentContext();
        IsSearching = true;
        SetBaseStatusText("Searching...", SearchStatusPresentation.Both);

        try
        {
            var targets = await BuildSearchTargetsAsync(sessionContext, ct);
            if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
                return;

            _visibleOutputExecutionState = CloneExecutionState(_activeSessionExecutionState);
            _visibleOutputCacheKey = _activeSessionOutputCacheKey;
            CacheResultFileOrder(targets, sessionContext);
            if (targets.Count == 0)
            {
                _activeSessionExecutionState = null;
                _activeSessionOutputCacheKey = null;
                _visibleOutputExecutionState = null;
                _visibleOutputCacheKey = null;
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
                    SetBaseStatusText(BuildSnapshotStatus(), SearchStatusPresentation.HeaderOnly);
                    IsSearching = false;
                    _activeSessionExecutionState = null;
                    _activeSessionOutputCacheKey = null;
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

            if (IsCurrentSession(sessionCts))
                SetBaseStatusText("Monitoring tail and backfilling disk snapshot...", SearchStatusPresentation.Both);

            await RunSnapshotBackfillAsync(targets, sessionContext, sessionCts, ct);
            _snapshotBackfillComplete = true;

            if (IsCurrentSession(sessionCts) && !ct.IsCancellationRequested)
                SetBaseStatusText(BuildTailStatus(sessionContext.SearchDataMode), SearchStatusPresentation.HeaderOnly);
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
                    _activeSessionOutputCacheKey = null;
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

    private async Task RunSnapshotBackfillAsync(
        IReadOnlyList<SearchTarget> targets,
        SearchSessionContext sessionContext,
        CancellationTokenSource sessionCts,
        CancellationToken ct)
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
                sessionContext,
                new List<string> { target.FilePath },
                sessionContext.SearchDataMode,
                GetApplicableFilterSnapshotMap(target.FilePath, sessionContext),
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
        SearchSessionContext sessionContext,
        CancellationToken ct)
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

        var membershipPaths = sessionContext.ScopeSnapshot.EffectiveMembership
            .Select(member => member.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (membershipPaths.Count == 0)
            return Array.Empty<SearchTarget>();

        var openTabsByPath = sessionContext.ScopeSnapshot.OpenTabs
            .GroupBy(tab => tab.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Tab, StringComparer.OrdinalIgnoreCase);

        if (sessionContext.SearchDataMode != SearchDataMode.DiskSnapshot)
        {
            var materializedTabs = await _mainVm.EnsureBackgroundTabsOpenAsync(
                membershipPaths.Where(path => !openTabsByPath.ContainsKey(path)).ToList(),
                sessionContext.ScopeDashboardId,
                ct);
            foreach (var (filePath, tab) in materializedTabs)
                openTabsByPath[filePath] = tab;
        }

        var targets = new List<SearchTarget>(membershipPaths.Count);
        foreach (var filePath in membershipPaths)
        {
            openTabsByPath.TryGetValue(filePath, out var tab);
            var encoding = tab?.EffectiveEncoding ??
                           await _mainVm.ResolveFilterFileEncodingAsync(filePath, sessionContext.ScopeDashboardId, ct);
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
            SourceMode = ToRequestSourceMode(sourceMode)
        };
    }

    private IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableFilterSnapshots(SearchSessionContext sessionContext)
    {
        if (sessionContext.TargetMode == SearchFilterTargetMode.CurrentScope)
            return _mainVm.GetApplicableCurrentScopeFilterSnapshots(sessionContext.SearchDataMode);

        var snapshot = _mainVm.GetApplicableCurrentTabFilterSnapshot(sessionContext.SearchDataMode);
        if (snapshot == null || sessionContext.SelectedTab == null)
            return new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [sessionContext.SelectedTab.FilePath] = snapshot
        };
    }

    private LogFilterSession.FilterSnapshot? GetApplicableFilterSnapshot(string filePath, SearchSessionContext sessionContext)
    {
        if (sessionContext.TargetMode == SearchFilterTargetMode.CurrentScope)
            return _mainVm.GetApplicableCurrentScopeFilterSnapshot(filePath, sessionContext.SearchDataMode);

        var snapshot = _mainVm.GetApplicableCurrentTabFilterSnapshot(sessionContext.SearchDataMode);
        return snapshot != null && sessionContext.SelectedTab != null &&
               string.Equals(sessionContext.SelectedTab.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
            ? snapshot
            : null;
    }

    private IReadOnlyDictionary<string, LogFilterSession.FilterSnapshot> GetApplicableFilterSnapshotMap(
        string filePath,
        SearchSessionContext sessionContext)
    {
        var snapshot = GetApplicableFilterSnapshot(filePath, sessionContext);
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
        RequestCacheSync();
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
        BeginResultPresentationUpdate();
        try
        {
            fileResultVm.AddHits(result.Hits);
        }
        finally
        {
            EndResultPresentationUpdate(
                refreshRows: fileResultVm.IsExpanded && fileResultVm.HitCount != hitsBefore,
                syncCache: true);
        }

        _totalHits += fileResultVm.HitCount - hitsBefore;
        fileResultVm.SetError(result.Error);
        RequestCacheSync();
    }

    private void CacheResultFileOrder(IReadOnlyList<SearchTarget> targets, SearchSessionContext sessionContext)
    {
        _resultFileOrderByPath.Clear();
        if (sessionContext.TargetMode != SearchFilterTargetMode.CurrentScope || targets.Count == 0)
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

    private string BuildSnapshotStatus()
    {
        var filesWithHits = _resultsByFilePath.Values.Count(r => r.HitCount > 0);
        return $"{_totalHits:N0} in {filesWithHits} file(s)";
    }

    private string BuildTailStatus(SearchDataMode searchDataMode)
    {
        var filesWithHits = _resultsByFilePath.Values.Count(r => r.HitCount > 0);
        return searchDataMode switch
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
        _activeSessionContext = null;
        _activeSessionExecutionState = null;
        _activeSessionOutputCacheKey = null;

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
        CancelActiveSearchSession(updateUi: false);
        IsSearching = false;
        ClearVisibleResults();
        _resultFileOrderByPath.Clear();
        _baseStatusText = string.Empty;
        _baseStatusPresentation = SearchStatusPresentation.None;
        _visibleOutputExecutionState = null;
        _visibleOutputCacheKey = null;
        _invalidationStatusText = string.Empty;
        _visibleOutputFreshness = SearchOutputFreshness.None;
        _snapshotBackfillComplete = false;
        RemoveCachedOutputForCurrentContext();
        RefreshVisibleStatusText();
    }

    public void Dispose()
    {
        CancelActiveSearchSession(updateUi: false);
        _scopeStateStore.Persist(CaptureCurrentScopeState());
        _sharedOptions.PropertyChanged -= SharedOptions_PropertyChanged;
    }

    internal void OnScopeChanging(WorkspaceScopeKey nextScopeKey)
    {
        if (nextScopeKey.Equals(_scopeStateStore.ActiveScopeKey))
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
            activeState.VisibleOutputFreshness = hadVisibleOutput
                ? SearchOutputFreshness.ScopeExitCancelled
                : SearchOutputFreshness.None;
            if (hadVisibleOutput &&
                activeState.ExecutionState != null &&
                BuildOutputCacheKey(activeState.TargetMode, activeState.SearchDataMode, activeState.ExecutionState) is { } cacheKey &&
                activeState.CachedOutputs.TryGetValue(cacheKey, out var cachedOutput))
            {
                activeState.CachedOutputs[cacheKey] = new CachedSearchOutputState
                {
                    Results = cachedOutput.Results,
                    BaseStatusText = cachedOutput.BaseStatusText,
                    BaseStatusPresentation = cachedOutput.BaseStatusPresentation,
                    ExecutionState = CloneExecutionState(cachedOutput.ExecutionState),
                    Freshness = SearchOutputFreshness.ScopeExitCancelled
                };
            }
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
            RefreshVisibleStatusText();
            return;
        }

        RestoreScopeState(_scopeStateStore.ActivateScope(scopeSnapshot.ScopeKey));
    }

    internal void OnSelectedTabChanged(LogTabViewModel? selectedTab)
    {
        if (_scopeStateStore.PendingScopeKey != null)
            return;

        CancelActiveSearchIfOutputContextChanged();
        RefreshVisibleStatusText();
    }

    private ScopeOwnedSearchState CaptureCurrentScopeState()
    {
        FlushVisibleOutputCache();
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
            VisibleOutputFreshness = _visibleOutputFreshness,
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
        _baseStatusText = string.Empty;
        _baseStatusPresentation = SearchStatusPresentation.None;
        _visibleOutputExecutionState = null;
        _visibleOutputCacheKey = null;
        _activeSessionContext = null;
        _activeSessionExecutionState = null;
        _visibleOutputFreshness = state.VisibleOutputFreshness;
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
            ScopeDashboardId = _mainVm.ActiveScopeDashboardId,
            SelectedTab = selectedTab,
            ExecutionState = executionState,
            OutputCacheKey = BuildOutputCacheKey(targetMode, searchDataMode, executionState),
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
        return targetMode == SearchFilterTargetMode.CurrentScope
            ? new CurrentScopeExecutionState(BuildOrderedScopeExecutionPaths(scopeSnapshot.EffectiveMembership.Select(member => member.FilePath)))
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

        RequestCacheSync();
        RefreshVisibleStatusText();
    }

    private void RefreshVisibleStatusText()
    {
        ReconcileVisibleOutput();
        StatusText = GetVisibleInlineStatusText();
        ResultsHeaderText = GetVisibleResultsHeaderText();
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

        if (_visibleOutputFreshness == SearchOutputFreshness.LiveSearchStoppedByContextChange)
            return ContextChangedCancelledStatusText;

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
            VisibleOutputFreshness = state.VisibleOutputFreshness,
            IsSearching = state.IsSearching,
            CachedOutputs = CloneCachedOutputs(state.CachedOutputs)
        };
    }

    private void ReconcileVisibleOutput()
    {
        if (_scopeStateStore.PendingScopeKey != null)
            return;

        var currentOutputCacheKey = CreateCurrentOutputCacheKey();
        if (_visibleOutputCacheKey != null &&
            !Equals(_visibleOutputCacheKey, currentOutputCacheKey))
        {
            FlushVisibleOutputCache();
            ClearVisibleResults();
            _visibleOutputExecutionState = null;
            _visibleOutputCacheKey = null;
            _invalidationStatusText = SearchResultsClearedStatusText;
            _visibleOutputFreshness = SearchOutputFreshness.None;
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
        _baseStatusPresentation = cachedOutput.BaseStatusPresentation;
        _visibleOutputExecutionState = CloneExecutionState(cachedOutput.ExecutionState);
        _visibleOutputCacheKey = cacheKey;
        _visibleOutputFreshness = cachedOutput.Freshness;
        _invalidationStatusText = string.Empty;
        _isVisibleOutputCacheDirty = false;
    }

    private void FlushVisibleOutputCache()
    {
        if (!_isVisibleOutputCacheDirty || _visibleOutputCacheKey == null)
            return;

        if (_visibleOutputExecutionState == null &&
            Results.Count == 0 &&
            string.IsNullOrWhiteSpace(_baseStatusText))
        {
            _cachedOutputs.Remove(_visibleOutputCacheKey);
            _isVisibleOutputCacheDirty = false;
            return;
        }

        _cachedOutputs[_visibleOutputCacheKey] = new CachedSearchOutputState
        {
            Results = CaptureResultStates(),
            BaseStatusText = _baseStatusText,
            BaseStatusPresentation = _baseStatusPresentation,
            ExecutionState = CloneExecutionState(_visibleOutputExecutionState),
            Freshness = _visibleOutputFreshness
        };
        _isVisibleOutputCacheDirty = false;
    }

    private FileSearchResultViewModel CreateFileResultViewModel(SearchResult result)
        => new(result, _mainVm, OnResultPresentationChanged);

    private void OnResultPresentationChanged()
    {
        if (_resultPresentationUpdateDepth != 0)
        {
            _pendingCacheSync = true;
            _isVisibleOutputCacheDirty = true;
            return;
        }

        RequestVisibleRowsRefresh();
        RequestCacheSync();
    }

    private void RefreshVisibleRows()
    {
        VisibleRows.Refresh(Results);
    }

    private void RemoveCachedOutputForCurrentContext()
    {
        _isVisibleOutputCacheDirty = false;
        var currentOutputCacheKey = CreateCurrentOutputCacheKey();
        if (currentOutputCacheKey != null)
            _cachedOutputs.Remove(currentOutputCacheKey);
    }

    private SearchOutputCacheKey? CreateCurrentOutputCacheKey()
        => BuildOutputCacheKey(TargetMode, SearchDataMode, CreateCurrentExecutionState());

    private SearchExecutionState? CreateCurrentExecutionState()
        => CreateExecutionState(_activeScopeSnapshot, TargetMode, _mainVm.SelectedTab);

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
            BaseStatusPresentation = state.BaseStatusPresentation,
            ExecutionState = CloneExecutionState(state.ExecutionState),
            Freshness = state.Freshness
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

    private void SharedOptions_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchFilterSharedOptions.TargetMode))
        {
            CancelActiveSearchIfOutputContextChanged();
            OnPropertyChanged(nameof(TargetMode));
            OnPropertyChanged(nameof(IsCurrentTabTarget));
            OnPropertyChanged(nameof(IsCurrentScopeTarget));
            RefreshVisibleStatusText();
            return;
        }

        if (e.PropertyName == nameof(SearchFilterSharedOptions.DataMode))
        {
            CancelActiveSearchIfOutputContextChanged();
            OnPropertyChanged(nameof(SearchDataMode));
            OnPropertyChanged(nameof(IsDiskSnapshotMode));
            OnPropertyChanged(nameof(IsTailMode));
            OnPropertyChanged(nameof(IsSnapshotAndTailMode));
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
            _visibleOutputFreshness = SearchOutputFreshness.LiveSearchStoppedByContextChange;
            RequestCacheSync();
        }
        else
        {
            _baseStatusText = string.Empty;
            _baseStatusPresentation = SearchStatusPresentation.None;
            _visibleOutputExecutionState = null;
            _visibleOutputFreshness = SearchOutputFreshness.None;
            RequestCacheSync();
        }

        CancelActiveSearchSession(updateUi: false);
        IsSearching = false;
    }

    private bool MatchesCurrentOutputContext(SearchSessionContext sessionContext)
        => Equals(sessionContext.OutputCacheKey, CreateCurrentOutputCacheKey());

    private void BeginResultPresentationUpdate()
    {
        _resultPresentationUpdateDepth++;
    }

    private void EndResultPresentationUpdate(bool refreshRows, bool syncCache)
    {
        if (_resultPresentationUpdateDepth > 0)
            _resultPresentationUpdateDepth--;

        _pendingVisibleRowsRefresh |= refreshRows;
        _pendingCacheSync |= syncCache;
        if (_resultPresentationUpdateDepth != 0)
            return;

        if (_pendingVisibleRowsRefresh)
        {
            _pendingVisibleRowsRefresh = false;
            RefreshVisibleRows();
        }

        if (_pendingCacheSync)
        {
            _pendingCacheSync = false;
            RequestCacheSync();
        }
    }

    private void RequestVisibleRowsRefresh()
    {
        if (_resultPresentationUpdateDepth != 0)
        {
            _pendingVisibleRowsRefresh = true;
            return;
        }

        RefreshVisibleRows();
    }

    private void RequestCacheSync()
    {
        _isVisibleOutputCacheDirty = true;
        if (_resultPresentationUpdateDepth != 0)
        {
            _pendingCacheSync = true;
            return;
        }

        if (!IsSearching)
            FlushVisibleOutputCache();
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
        public SearchSessionContext SessionContext { get; init; } = null!;
        public long SnapshotLine { get; set; }
        public long LastProcessedLine { get; set; }
        public int SearchContentVersion { get; set; }
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
        public string? ScopeDashboardId { get; init; }
        public LogTabViewModel? SelectedTab { get; init; }
        public SearchExecutionState? ExecutionState { get; init; }
        public SearchOutputCacheKey? OutputCacheKey { get; init; }
        public IReadOnlyList<string> ResultFileOrderSnapshot { get; init; } = Array.Empty<string>();
    }

    private enum SearchOutputFreshness
    {
        None,
        ScopeExitCancelled,
        LiveSearchStoppedByContextChange
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
        public SearchStatusPresentation BaseStatusPresentation { get; init; }
        public SearchExecutionState? ExecutionState { get; init; }
        public SearchOutputFreshness Freshness { get; init; }
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
        public SearchOutputFreshness VisibleOutputFreshness { get; set; }
        public bool IsSearching { get; set; }
        public Dictionary<SearchOutputCacheKey, CachedSearchOutputState> CachedOutputs { get; init; } = new();
    }

    private enum SearchStatusPresentation
    {
        None,
        InlineOnly,
        HeaderOnly,
        Both
    }
}
