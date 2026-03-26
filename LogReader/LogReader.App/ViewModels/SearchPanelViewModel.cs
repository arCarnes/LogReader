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
    private readonly ISearchService _searchService;
    private readonly ILogWorkspaceContext _mainVm;
    private readonly Dictionary<string, FileSearchResultViewModel> _resultsByFilePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TailSearchTracker> _tailTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _filesWithParseableTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _searchCts;
    private SearchDataMode _activeSearchDataMode = SearchDataMode.DiskSnapshot;
    private long _totalHits;
    private bool _snapshotBackfillComplete;
    private bool _hasTimestampRangeFilter;
    private int _timestampRangeTargetFileCount;

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
            StatusText = "Enter a search query.";
            IsSearching = false;
            return;
        }

        if (!TimestampParser.TryBuildRange(FromTimestamp, ToTimestamp, out var timestampRange, out var rangeError))
        {
            StatusText = rangeError ?? "Invalid timestamp range.";
            IsSearching = false;
            return;
        }

        var sessionCts = new CancellationTokenSource();
        _searchCts = sessionCts;
        var ct = sessionCts.Token;
        var selectedMode = SearchDataMode;
        _activeSearchDataMode = selectedMode;

        Results.Clear();
        _resultsByFilePath.Clear();
        DetachTailTrackers();
        _filesWithParseableTimestamps.Clear();
        _totalHits = 0;
        _snapshotBackfillComplete = false;
        IsSearching = true;
        StatusText = "Searching...";

        try
        {
            var targets = BuildSearchTargets();
            _hasTimestampRangeFilter = timestampRange.HasBounds;
            _timestampRangeTargetFileCount = targets.Count;
            if (targets.Count == 0)
            {
                if (IsCurrentSession(sessionCts))
                {
                    StatusText = "No files to search";
                    IsSearching = false;
                }
                return;
            }

            if (selectedMode == SearchDataMode.DiskSnapshot)
            {
                await RunDiskSnapshotSearchAsync(targets, sessionCts, ct);
                if (IsCurrentSession(sessionCts))
                {
                    StatusText = BuildSnapshotStatus();
                    IsSearching = false;
                }
                return;
            }

            InitializeTailTrackers(targets, sessionCts);

            if (selectedMode == SearchDataMode.Tail)
            {
                if (IsCurrentSession(sessionCts))
                    StatusText = BuildTailStatus();
                return;
            }

            if (IsCurrentSession(sessionCts))
                StatusText = "Monitoring tail and backfilling disk snapshot...";

            await RunSnapshotBackfillAsync(targets, sessionCts, ct);
            _snapshotBackfillComplete = true;

            if (IsCurrentSession(sessionCts) && !ct.IsCancellationRequested)
                StatusText = BuildTailStatus();
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSession(sessionCts))
                StatusText = "Search cancelled";
        }
        catch (Exception ex)
        {
            if (IsCurrentSession(sessionCts))
            {
                StatusText = $"Search error: {ex.Message}";
                IsSearching = false;
            }
        }
        finally
        {
            if (selectedMode == SearchDataMode.DiskSnapshot && IsCurrentSession(sessionCts))
                IsSearching = false;
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
                    StatusText = BuildTailStatus();
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

    private IReadOnlyList<SearchTarget> BuildSearchTargets()
    {
        if (AllFiles)
        {
            return _mainVm.GetFilteredTabsSnapshot()
                .Select(tab => new SearchTarget
                {
                    FilePath = tab.FilePath,
                    Encoding = tab.EffectiveEncoding,
                    Tab = tab
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
            EndLineNumber = endLineNumber,
            FromTimestamp = string.IsNullOrWhiteSpace(FromTimestamp) ? null : FromTimestamp.Trim(),
            ToTimestamp = string.IsNullOrWhiteSpace(ToTimestamp) ? null : ToTimestamp.Trim()
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
        if (result.HasParseableTimestamps)
            _filesWithParseableTimestamps.Add(result.FilePath);

        if (!_resultsByFilePath.TryGetValue(result.FilePath, out var fileResultVm))
        {
            if (result.Hits.Count == 0 && string.IsNullOrWhiteSpace(result.Error))
                return;

            fileResultVm = new FileSearchResultViewModel(new SearchResult { FilePath = result.FilePath }, _mainVm, LineOrder);
            _resultsByFilePath[result.FilePath] = fileResultVm;
            Results.Add(fileResultVm);
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

    private void ApplyLineOrderToResults(SearchResultLineOrder lineOrder)
    {
        foreach (var result in _resultsByFilePath.Values)
            result.ApplyLineOrder(lineOrder);
    }

    private string BuildSnapshotStatus()
    {
        if (ShouldShowNoParseableTimestampStatus())
            return BuildNoParseableTimestampStatusForSnapshot();

        var filesWithHits = _resultsByFilePath.Values.Count(r => r.HitCount > 0);
        return $"{_totalHits:N0} in {filesWithHits} file(s)";
    }

    private string BuildTailStatus()
    {
        if (ShouldShowNoParseableTimestampStatus())
            return BuildNoParseableTimestampStatusForTail();

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

    private bool ShouldShowNoParseableTimestampStatus()
        => _hasTimestampRangeFilter &&
           _timestampRangeTargetFileCount > 0 &&
           _filesWithParseableTimestamps.Count == 0;

    private string BuildNoParseableTimestampStatusForSnapshot()
    {
        var fileLabel = _timestampRangeTargetFileCount == 1 ? "file" : "files";
        return $"No parseable timestamps found in {_timestampRangeTargetFileCount} {fileLabel} for the selected time range.";
    }

    private string BuildNoParseableTimestampStatusForTail()
    {
        return _activeSearchDataMode switch
        {
            SearchDataMode.Tail =>
                "Monitoring tail: no parseable timestamps found yet for the selected time range.",
            SearchDataMode.SnapshotAndTail when _snapshotBackfillComplete =>
                "Snapshot + Monitoring Tail:  no parseable timestamps found yet for the selected time range.",
            SearchDataMode.SnapshotAndTail =>
                "Monitoring tail + backfill: no parseable timestamps found yet for the selected time range.",
            _ => BuildNoParseableTimestampStatusForSnapshot()
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
        if (current == null)
            return;

        current.Cancel();
        current.Dispose();
        DetachTailTrackers();

        if (updateUi)
        {
            IsSearching = false;
            StatusText = "Search cancelled";
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
        Results.Clear();
        _resultsByFilePath.Clear();
        _filesWithParseableTimestamps.Clear();
        _totalHits = 0;
        if (IsSearching && (SearchDataMode == SearchDataMode.Tail || SearchDataMode == SearchDataMode.SnapshotAndTail))
            StatusText = BuildTailStatus();
        else
            StatusText = string.Empty;
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
}
