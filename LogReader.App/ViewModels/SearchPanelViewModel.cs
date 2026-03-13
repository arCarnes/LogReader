namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

public partial class SearchPanelViewModel : ObservableObject
{
    private readonly ISearchService _searchService;
    private readonly MainViewModel _mainVm;
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

    public SearchPanelViewModel(ISearchService searchService, MainViewModel mainVm)
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

    [RelayCommand]
    private async Task ExecuteSearch()
    {
        if (string.IsNullOrWhiteSpace(Query))
            return;

        if (!TimestampParser.TryBuildRange(FromTimestamp, ToTimestamp, out var timestampRange, out var rangeError))
        {
            StatusText = rangeError ?? "Invalid timestamp range.";
            IsSearching = false;
            return;
        }

        CancelActiveSearchSession(updateUi: false);
        var sessionCts = new CancellationTokenSource();
        _searchCts = sessionCts;
        var ct = sessionCts.Token;
        var selectedMode = SearchDataMode;
        _activeSearchDataMode = selectedMode;

        Results.Clear();
        _resultsByFilePath.Clear();
        _tailTrackers.Clear();
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
                await RunDiskSnapshotSearchAsync(targets, ct);
                if (IsCurrentSession(sessionCts))
                {
                    StatusText = BuildSnapshotStatus();
                    IsSearching = false;
                }
                return;
            }

            InitializeTailTrackers(targets);
            _ = MonitorTailAsync(sessionCts, ct);

            if (selectedMode == SearchDataMode.Tail)
            {
                if (IsCurrentSession(sessionCts))
                    StatusText = BuildTailStatus();
                return;
            }

            if (IsCurrentSession(sessionCts))
                StatusText = "Monitoring tail and backfilling disk snapshot...";

            await RunSnapshotBackfillAsync(targets, ct);
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

    private async Task RunDiskSnapshotSearchAsync(IReadOnlyList<SearchTarget> targets, CancellationToken ct)
    {
        var filePaths = targets.Select(t => t.FilePath).ToList();
        var encodings = targets.ToDictionary(t => t.FilePath, t => t.Encoding, StringComparer.OrdinalIgnoreCase);
        var request = CreateSearchRequest(filePaths);
        var results = await _searchService.SearchFilesAsync(request, encodings, ct);
        foreach (var result in results)
            MergeResult(result);
    }

    private void InitializeTailTrackers(IReadOnlyList<SearchTarget> targets)
    {
        _tailTrackers.Clear();
        foreach (var target in targets)
        {
            var baselineLine = Math.Max(0, target.Tab.TotalLines);
            _tailTrackers[target.FilePath] = new TailSearchTracker
            {
                FilePath = target.FilePath,
                Encoding = target.Encoding,
                Tab = target.Tab,
                SnapshotLine = baselineLine,
                LastProcessedLine = baselineLine
            };
        }
    }

    private async Task RunSnapshotBackfillAsync(IReadOnlyList<SearchTarget> targets, CancellationToken ct)
    {
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (!_tailTrackers.TryGetValue(target.FilePath, out var tracker))
                continue;

            if (tracker.SnapshotLine <= 0)
                continue;

            var request = CreateSearchRequest(new List<string> { target.FilePath }, startLineNumber: 1, endLineNumber: tracker.SnapshotLine);
            var result = await _searchService.SearchFileAsync(target.FilePath, request, target.Encoding, ct);
            MergeResult(result);
        }
    }

    private async Task MonitorTailAsync(CancellationTokenSource sessionCts, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var tracker in _tailTrackers.Values.ToList())
                    await ProcessTailTrackerAsync(tracker, ct);

                if (IsCurrentSession(sessionCts))
                    StatusText = BuildTailStatus();

                await Task.Delay(300, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (IsCurrentSession(sessionCts))
            {
                IsSearching = false;
                if (ct.IsCancellationRequested && StatusText.StartsWith("Monitoring", StringComparison.OrdinalIgnoreCase))
                    StatusText = "Search cancelled";
            }
        }
    }

    private async Task ProcessTailTrackerAsync(TailSearchTracker tracker, CancellationToken ct)
    {
        var currentTotalLines = Math.Max(0, tracker.Tab.TotalLines);
        if (currentTotalLines < tracker.LastProcessedLine)
            tracker.LastProcessedLine = 0;

        if (currentTotalLines <= tracker.LastProcessedLine)
            return;

        var startLine = tracker.LastProcessedLine + 1;
        var request = CreateSearchRequest(
            new List<string> { tracker.FilePath },
            startLineNumber: startLine,
            endLineNumber: currentTotalLines);
        var result = await _searchService.SearchFileAsync(tracker.FilePath, request, tracker.Encoding, ct);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            MergeResult(result);
            return;
        }

        tracker.LastProcessedLine = currentTotalLines;
        MergeResult(result);
    }

    private IReadOnlyList<SearchTarget> BuildSearchTargets()
    {
        if (AllFiles)
        {
            return _mainVm.GetAllTabs()
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

    private void MergeResult(SearchResult result)
    {
        if (result.HasParseableTimestamps)
            _filesWithParseableTimestamps.Add(result.FilePath);

        if (result.Hits.Count == 0 && string.IsNullOrWhiteSpace(result.Error))
            return;

        if (!_resultsByFilePath.TryGetValue(result.FilePath, out var fileResultVm))
        {
            fileResultVm = new FileSearchResultViewModel(new SearchResult { FilePath = result.FilePath }, _mainVm, LineOrder);
            _resultsByFilePath[result.FilePath] = fileResultVm;
            Results.Add(fileResultVm);
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
        return $"{_totalHits:N0} matches in {filesWithHits} files";
    }

    private string BuildTailStatus()
    {
        if (ShouldShowNoParseableTimestampStatus())
            return BuildNoParseableTimestampStatusForTail();

        var filesWithHits = _resultsByFilePath.Values.Count(r => r.HitCount > 0);
        return _activeSearchDataMode switch
        {
            SearchDataMode.Tail => $"Monitoring tail: {_totalHits:N0} matches in {filesWithHits} files",
            SearchDataMode.SnapshotAndTail when _snapshotBackfillComplete =>
                $"Monitoring tail (snapshot complete): {_totalHits:N0} matches in {filesWithHits} files",
            SearchDataMode.SnapshotAndTail =>
                $"Monitoring tail + backfill: {_totalHits:N0} matches in {filesWithHits} files",
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
                "Monitoring tail (snapshot complete): no parseable timestamps found yet for the selected time range.",
            SearchDataMode.SnapshotAndTail =>
                "Monitoring tail + backfill: no parseable timestamps found yet for the selected time range.",
            _ => BuildNoParseableTimestampStatusForSnapshot()
        };
    }

    private bool IsCurrentSession(CancellationTokenSource sessionCts)
        => ReferenceEquals(_searchCts, sessionCts);

    private void CancelActiveSearchSession(bool updateUi)
    {
        var current = _searchCts;
        _searchCts = null;
        if (current == null)
            return;

        current.Cancel();
        current.Dispose();
        _tailTrackers.Clear();

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
        StatusText = await _mainVm.NavigateToTimestampAsync(NavigateTimestamp);
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
    }
}
