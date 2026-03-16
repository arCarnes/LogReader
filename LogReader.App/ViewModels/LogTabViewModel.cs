namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Helpers;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class LogTabViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan DisposeLineIndexTimeout = TimeSpan.FromSeconds(2);

    public sealed partial class EncodingOptionItem : ObservableObject
    {
        public FileEncoding Value { get; init; }

        [ObservableProperty]
        private string _label = string.Empty;
    }

    private readonly ILogReaderService _logReader;
    private readonly IFileTailService _tailService;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly SemaphoreSlim _lineIndexLock = new(1, 1);
    private AppSettings _settings;
    private LineIndex? _lineIndex;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _navCts;
    private Task? _lineIndexDisposeTask;
    private int _isDisposed;
    private int _shutdownStarted;
    private int _tailPollingIntervalMs = 250;
    private List<int>? _snapshotFilteredLineNumbers;
    private string? _activeFilterStatusText;
    private ActiveTailFilterState? _activeTailFilterState;
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(250);

    internal bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) != 0;

    private bool IsShutdownOrDisposed => IsShuttingDown || Volatile.Read(ref _isDisposed) != 0;

    public string FileId { get; }
    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);

    [ObservableProperty]
    private FileEncoding _encoding = FileEncoding.Auto;

    private FileEncoding _effectiveEncoding = FileEncoding.Utf8;
    public FileEncoding EffectiveEncoding
    {
        get => _effectiveEncoding;
        private set => SetProperty(ref _effectiveEncoding, value);
    }

    private string _encodingStatusText = "Auto -> UTF-8 (fallback)";
    public string EncodingStatusText
    {
        get => _encodingStatusText;
        private set => SetProperty(ref _encodingStatusText, value);
    }

    [ObservableProperty]
    private bool _autoScrollEnabled = true;

    [ObservableProperty]
    private int _totalLines;

    [ObservableProperty]
    private int _navigateToLineNumber = -1;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasLoadError;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isSuspended;

    [ObservableProperty]
    private DateTime _lastVisibleAtUtc = DateTime.UtcNow;

    [ObservableProperty]
    private DateTime _lastHiddenAtUtc = DateTime.MinValue;

    // The currently visible lines (virtualized window)
    public ObservableCollection<LogLineViewModel> VisibleLines { get; } = new();

    private int _viewportStartLine;
    private int _viewportLineCount = 50; // initial estimate; corrected by SizeChanged
    private bool _suppressScrollChange;

    private EncodingOptionItem AutoEncodingOption { get; }

    public IReadOnlyList<EncodingOptionItem> EncodingOptions { get; }

    public string SelectedEncodingDisplayLabel => Encoding == FileEncoding.Auto
        ? $"Auto ({EncodingHelper.GetEncodingDisplayName(EffectiveEncoding)})"
        : EncodingHelper.GetEncodingDisplayName(Encoding);

    public int ViewportLineCount => _viewportLineCount;
    public bool IsFilterActive => _snapshotFilteredLineNumbers != null;
    public int FilteredLineCount => _snapshotFilteredLineNumbers?.Count ?? 0;
    public int DisplayLineCount => IsFilterActive ? FilteredLineCount : TotalLines;
    public int MaxScrollPosition => Math.Max(0, DisplayLineCount - _viewportLineCount);

    [ObservableProperty]
    private int _scrollPosition;

    public LogTabViewModel(
        string fileId,
        string filePath,
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        AppSettings settings)
    {
        FileId = fileId;
        FilePath = filePath;
        _logReader = logReader;
        _tailService = tailService;
        _encodingDetectionService = encodingDetectionService;
        _settings = settings;
        AutoEncodingOption = new EncodingOptionItem { Value = FileEncoding.Auto, Label = "Auto (UTF-8)" };
        EncodingOptions = new[]
        {
            AutoEncodingOption,
            new EncodingOptionItem { Value = FileEncoding.Utf8, Label = "UTF-8" },
            new EncodingOptionItem { Value = FileEncoding.Utf16, Label = "UTF-16" },
            new EncodingOptionItem { Value = FileEncoding.Utf16Be, Label = "UTF-16 BE" },
            new EncodingOptionItem { Value = FileEncoding.Ansi, Label = "ANSI" }
        };

        _tailService.LinesAppended += OnLinesAppended;
        _tailService.FileRotated += OnFileRotated;
        _tailService.TailError += OnTailError;
        ResolveEffectiveEncoding();
    }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    internal void UpdateViewportLineCount(int count)
    {
        if (IsShutdownOrDisposed || count <= 0 || _viewportLineCount == count) return;
        _viewportLineCount = count;
        _suppressScrollChange = true;
        OnPropertyChanged(nameof(ViewportLineCount));
        OnPropertyChanged(nameof(MaxScrollPosition));
        _suppressScrollChange = false;
        _ = LoadViewportAsync(_viewportStartLine, _viewportLineCount);
    }

    public Task RefreshViewportAsync()
        => IsShutdownOrDisposed
            ? Task.CompletedTask
            : LoadViewportAsync(_viewportStartLine, _viewportLineCount);

    public async Task LoadAsync()
    {
        if (IsShutdownOrDisposed)
            return;

        // Cancel and dispose any in-flight load so we don't race.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IsLoading = true;
        HasLoadError = false;
        StatusText = "Building index...";

        try
        {
            ResolveEffectiveEncoding();

            LineIndex? oldIndex;
            await _lineIndexLock.WaitAsync();
            try
            {
                oldIndex = _lineIndex;
                _lineIndex = null;
            }
            finally
            {
                _lineIndexLock.Release();
            }

            oldIndex?.Dispose();

            var newIndex = await BuildIndexOffUiAsync(EffectiveEncoding, cts.Token);
            await _lineIndexLock.WaitAsync();
            try
            {
                _lineIndex = newIndex;
                TotalLines = _lineIndex.LineCount;
            }
            finally
            {
                _lineIndexLock.Release();
            }

            StatusText = IsFilterActive
                ? _activeFilterStatusText ?? $"Filter active: {FilteredLineCount:N0} matching lines."
                : $"{TotalLines:N0} lines";

            if (IsShutdownOrDisposed)
            {
                IsSuspended = true;
                return;
            }

            // Load initial viewport
            var initialStart = IsFilterActive
                ? 0
                : Math.Max(0, TotalLines - _viewportLineCount);
            await LoadViewportAsync(initialStart, _viewportLineCount);

            if (IsShutdownOrDisposed)
            {
                IsSuspended = true;
                return;
            }

            // Start tailing
            _tailService.StartTailing(FilePath, EffectiveEncoding, _tailPollingIntervalMs);
            IsSuspended = false;
            HasLoadError = false;
        }
        catch (OperationCanceledException)
        {
            return; // Superseded by a newer load; leave IsLoading/StatusText for the new one
        }
        catch (Exception ex)
        {
            HasLoadError = true;
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsLoading = false;
        }
    }

    public async Task LoadViewportAsync(int startLine, int count, CancellationToken ct = default)
    {
        if (IsShutdownOrDisposed)
            return;

        var maxStart = Math.Max(0, DisplayLineCount - Math.Max(1, count));
        _viewportStartLine = Math.Max(0, Math.Min(startLine, maxStart));

        try
        {
            LineIndex? lineIndexSnapshot;
            await _lineIndexLock.WaitAsync(ct);
            try
            {
                lineIndexSnapshot = _lineIndex;
            }
            finally
            {
                _lineIndexLock.Release();
            }

            if (lineIndexSnapshot == null || IsShutdownOrDisposed) return;

            var nextVisibleLines = new List<LogLineViewModel>(Math.Max(0, count));
            if (IsFilterActive)
            {
                var filteredLines = _snapshotFilteredLineNumbers;
                if (filteredLines != null && filteredLines.Count > 0)
                {
                    var maxIndexExclusive = Math.Min(filteredLines.Count, _viewportStartLine + count);
                    for (int displayIndex = _viewportStartLine; displayIndex < maxIndexExclusive; displayIndex++)
                    {
                        var actualLineNumber = filteredLines[displayIndex];
                        var lineText = await ReadLineOffUiAsync(
                            lineIndexSnapshot,
                            actualLineNumber - 1,
                            EffectiveEncoding,
                            ct);

                        nextVisibleLines.Add(new LogLineViewModel
                        {
                            LineNumber = actualLineNumber,
                            Text = lineText,
                            HighlightColor = LineHighlighter.GetHighlightColor(_settings.HighlightRules, lineText)
                        });
                    }
                }
            }
            else
            {
                var lines = await ReadLinesOffUiAsync(
                    lineIndexSnapshot,
                    _viewportStartLine,
                    count,
                    EffectiveEncoding,
                    ct);

                for (int i = 0; i < lines.Count; i++)
                {
                    nextVisibleLines.Add(new LogLineViewModel
                    {
                        LineNumber = _viewportStartLine + i + 1,
                        Text = lines[i],
                        HighlightColor = LineHighlighter.GetHighlightColor(_settings.HighlightRules, lines[i])
                    });
                }
            }

            if (IsShutdownOrDisposed)
                return;

            ApplyVisibleLines(nextVisibleLines);
            _suppressScrollChange = true;
            ScrollPosition = _viewportStartLine;
            _suppressScrollChange = false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            StatusText = $"Read error: {ex.Message}";
        }
    }

    private async Task<bool> TryAppendTailLinesToViewportAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct)
    {
        if (IsFilterActive || !AutoScrollEnabled)
            return false;

        if (updatedLineCount <= previousTotalLines || _viewportLineCount <= 0)
            return false;

        var expectedPreviousStart = Math.Max(0, previousTotalLines - _viewportLineCount);
        if (_viewportStartLine != expectedPreviousStart)
            return false;

        if (previousTotalLines > 0 && VisibleLines.Count > 0 && VisibleLines[^1].LineNumber != previousTotalLines)
            return false;

        LineIndex? lineIndexSnapshot;
        await _lineIndexLock.WaitAsync(ct);
        try
        {
            lineIndexSnapshot = _lineIndex;
        }
        finally
        {
            _lineIndexLock.Release();
        }

        if (lineIndexSnapshot == null)
            return false;

        var appendedCount = updatedLineCount - previousTotalLines;
        var appendedLines = await ReadLinesOffUiAsync(
            lineIndexSnapshot,
            previousTotalLines,
            appendedCount,
            EffectiveEncoding,
            ct);

        if (appendedLines.Count <= 0)
            return false;

        var maxLines = Math.Max(1, _viewportLineCount);
        var appendedStartOffset = Math.Max(0, appendedLines.Count - maxLines);
        var appendedToShowCount = appendedLines.Count - appendedStartOffset;
        var retainedCount = Math.Max(0, Math.Min(VisibleLines.Count, maxLines - appendedToShowCount));

        while (VisibleLines.Count > retainedCount)
            VisibleLines.RemoveAt(0);

        for (var i = appendedStartOffset; i < appendedLines.Count; i++)
        {
            var lineText = appendedLines[i];
            VisibleLines.Add(new LogLineViewModel
            {
                LineNumber = previousTotalLines + i + 1,
                Text = lineText,
                HighlightColor = LineHighlighter.GetHighlightColor(_settings.HighlightRules, lineText)
            });
        }

        _viewportStartLine = Math.Max(0, updatedLineCount - maxLines);
        _suppressScrollChange = true;
        ScrollPosition = _viewportStartLine;
        _suppressScrollChange = false;
        return true;
    }

    partial void OnTotalLinesChanged(int value)
    {
        OnPropertyChanged(nameof(DisplayLineCount));
        OnPropertyChanged(nameof(MaxScrollPosition));
    }

    partial void OnScrollPositionChanged(int value)
    {
        if (_suppressScrollChange || IsShutdownOrDisposed) return;
        _ = ScrollToLineAsync(value);
    }

    private async Task ScrollToLineAsync(int startLine)
    {
        if (_viewportStartLine == startLine && VisibleLines.Count > 0)
            return;

        _navCts?.Cancel();
        _navCts?.Dispose();
        _navCts = new CancellationTokenSource();
        try { await LoadViewportAsync(startLine, _viewportLineCount, _navCts.Token); }
        catch (OperationCanceledException) { return; }
        SetNavigateTargetLine(VisibleLines.FirstOrDefault()?.LineNumber ?? (IsFilterActive ? -1 : _viewportStartLine + 1));
    }

    [RelayCommand]
    private async Task JumpToTop()
    {
        await ScrollToLineAsync(0);
        SetNavigateTargetLine(VisibleLines.FirstOrDefault()?.LineNumber ?? (IsFilterActive ? -1 : 1));
    }

    [RelayCommand]
    private async Task JumpToBottom()
    {
        await ScrollToLineAsync(Math.Max(0, DisplayLineCount - _viewportLineCount));
        SetNavigateTargetLine(VisibleLines.LastOrDefault()?.LineNumber ?? (IsFilterActive ? -1 : TotalLines));
    }

    public async Task NavigateToLineAsync(int lineNumber)
    {
        // Cancel any in-progress navigation so rapid clicks don't race
        _navCts?.Cancel();
        _navCts?.Dispose();
        _navCts = new CancellationTokenSource();
        var ct = _navCts.Token;

        var navigateTargetLine = lineNumber;
        int startLine;
        if (IsFilterActive)
        {
            var filteredLines = _snapshotFilteredLineNumbers;
            if (filteredLines == null || filteredLines.Count == 0)
            {
                startLine = 0;
                navigateTargetLine = -1;
            }
            else
            {
                var filterIndex = filteredLines.BinarySearch(lineNumber);
                if (filterIndex < 0)
                {
                    filterIndex = ~filterIndex;
                    if (filterIndex >= filteredLines.Count)
                        filterIndex = filteredLines.Count - 1;
                }

                navigateTargetLine = filteredLines[filterIndex];
                startLine = Math.Max(0, filterIndex - _viewportLineCount / 2);
            }
        }
        else
        {
            startLine = Math.Max(0, lineNumber - _viewportLineCount / 2);
        }

        try
        {
            await LoadViewportAsync(startLine, _viewportLineCount, ct);
        }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested) return;

        SetNavigateTargetLine(navigateTargetLine);
    }

    partial void OnEncodingChanged(FileEncoding value)
    {
        if (IsShutdownOrDisposed)
            return;

        ResolveEffectiveEncoding();
        OnPropertyChanged(nameof(SelectedEncodingDisplayLabel));

        // If the tab hasn't started loading yet, the upcoming explicit LoadAsync will use the correct encoding.
        // If a load is already in progress, LoadAsync will cancel the old one and restart.
        if (Volatile.Read(ref _lineIndex) == null && !IsLoading) return;
        _tailService.StopTailing(FilePath);
        _ = LoadAsync();
    }

    private void ResolveEffectiveEncoding()
    {
        var decision = _encodingDetectionService.ResolveEncodingDecision(FilePath, Encoding);
        EffectiveEncoding = decision.ResolvedEncoding;
        EncodingStatusText = decision.StatusText;
        AutoEncodingOption.Label = $"Auto ({EncodingHelper.GetEncodingDisplayName(EffectiveEncoding)})";
        OnPropertyChanged(nameof(SelectedEncodingDisplayLabel));
    }

    public void OnBecameVisible()
    {
        if (IsShutdownOrDisposed)
            return;

        IsVisible = true;
        LastVisibleAtUtc = DateTime.UtcNow;
        ResumeTailing();
    }

    public void OnBecameHidden()
    {
        if (IsShutdownOrDisposed)
            return;

        if (!IsVisible) return;
        IsVisible = false;
        LastHiddenAtUtc = DateTime.UtcNow;
        SuspendTailing();
    }

    public void SuspendTailing()
    {
        if (IsSuspended) return;
        _tailService.StopTailing(FilePath);
        IsSuspended = true;
    }

    public void ResumeTailing()
    {
        if (IsShutdownOrDisposed)
            return;

        _ = ResumeTailingWithCatchUpAsync(_tailPollingIntervalMs);
    }

    public void ApplyVisibleTailingMode(int pollingIntervalMs)
    {
        if (IsShutdownOrDisposed)
            return;

        _ = ResumeTailingWithCatchUpAsync(pollingIntervalMs);
    }

    public async Task ApplyFilterAsync(
        IReadOnlyList<int> matchingLineNumbers,
        string statusText,
        SearchRequest? filterRequest = null,
        bool hasParseableTimestamps = false)
    {
        var filtered = matchingLineNumbers
            .Where(line => line > 0)
            .Distinct()
            .OrderBy(line => line)
            .ToList();

        _snapshotFilteredLineNumbers = filtered;
        _activeFilterStatusText = statusText;
        _activeTailFilterState = CreateTailFilterState(filterRequest, hasParseableTimestamps);
        RaiseFilterPropertiesChanged();

        await LoadViewportAsync(0, _viewportLineCount);
        SetNavigateTargetLine(VisibleLines.FirstOrDefault()?.LineNumber ?? -1);
        StatusText = statusText;
    }

    public async Task ClearFilterAsync()
    {
        if (!IsFilterActive)
            return;

        _snapshotFilteredLineNumbers = null;
        _activeFilterStatusText = null;
        _activeTailFilterState = null;
        RaiseFilterPropertiesChanged();

        await LoadViewportAsync(Math.Max(0, TotalLines - _viewportLineCount), _viewportLineCount);
        SetNavigateTargetLine(VisibleLines.FirstOrDefault()?.LineNumber ?? (TotalLines > 0 ? 1 : -1));
        StatusText = $"{TotalLines:N0} lines";
    }

    private ActiveTailFilterState? CreateTailFilterState(SearchRequest? filterRequest, bool hasParseableTimestamps)
    {
        if (filterRequest == null || string.IsNullOrWhiteSpace(filterRequest.Query))
            return null;

        if (!TimestampParser.TryBuildRange(filterRequest.FromTimestamp, filterRequest.ToTimestamp, out var timestampRange, out _))
            return null;

        return new ActiveTailFilterState
        {
            Matcher = CreateLineMatcher(filterRequest),
            TimestampRange = timestampRange,
            LastEvaluatedLine = TotalLines,
            HasSeenParseableTimestamp = hasParseableTimestamps
        };
    }

    private static Func<string, bool> CreateLineMatcher(SearchRequest request)
    {
        if (request.IsRegex)
        {
            var options = RegexOptions.Compiled;
            if (!request.CaseSensitive)
                options |= RegexOptions.IgnoreCase;

            var pattern = request.WholeWord ? $@"\b{request.Query}\b" : request.Query;
            var regex = new Regex(pattern, options, RegexMatchTimeout);
            return line => regex.IsMatch(line);
        }

        var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var query = request.Query;
        return line =>
        {
            if (string.IsNullOrEmpty(query))
                return false;

            var startIndex = 0;
            while (startIndex < line.Length)
            {
                var idx = line.IndexOf(query, startIndex, comparison);
                if (idx < 0)
                    return false;

                if (!request.WholeWord)
                    return true;

                var wordStart = idx == 0 || !char.IsLetterOrDigit(line[idx - 1]);
                var wordEnd = idx + query.Length >= line.Length || !char.IsLetterOrDigit(line[idx + query.Length]);
                if (wordStart && wordEnd)
                    return true;

                startIndex = idx + Math.Max(1, query.Length);
            }

            return false;
        };
    }

    private async Task ApplyTailFilterForAppendedLinesAsync(int updatedLineCount, CancellationToken ct)
    {
        if (!IsFilterActive || _activeTailFilterState == null || _snapshotFilteredLineNumbers == null)
            return;

        if (updatedLineCount <= _activeTailFilterState.LastEvaluatedLine)
            return;

        var previousDisplayCount = DisplayLineCount;
        var wasAtBottom = _viewportStartLine >= Math.Max(0, previousDisplayCount - _viewportLineCount);

        LineIndex? lineIndexSnapshot;
        await _lineIndexLock.WaitAsync(ct);
        try
        {
            lineIndexSnapshot = _lineIndex;
        }
        finally
        {
            _lineIndexLock.Release();
        }

        if (lineIndexSnapshot == null)
            return;

        var firstUnprocessedLine = _activeTailFilterState.LastEvaluatedLine + 1;
        var readCount = Math.Max(0, updatedLineCount - _activeTailFilterState.LastEvaluatedLine);
        var appendedLines = await ReadLinesOffUiAsync(
            lineIndexSnapshot,
            firstUnprocessedLine - 1,
            readCount,
            EffectiveEncoding,
            ct);

        var addedMatches = 0;
        var addedMatchingLines = new List<(int LineNumber, string LineText)>();
        for (var offset = 0; offset < appendedLines.Count; offset++)
        {
            var lineText = appendedLines[offset];
            var lineNumber = firstUnprocessedLine + offset;

            if (_activeTailFilterState.TimestampRange.HasBounds)
            {
                if (!TimestampParser.TryParseFromLogLine(lineText, out var timestamp))
                    continue;

                _activeTailFilterState.HasSeenParseableTimestamp = true;
                if (!_activeTailFilterState.TimestampRange.Contains(timestamp))
                    continue;
            }

            if (!_activeTailFilterState.Matcher(lineText))
                continue;

            if (InsertSortedUnique(_snapshotFilteredLineNumbers, lineNumber))
            {
                addedMatches++;
                addedMatchingLines.Add((lineNumber, lineText));
            }
        }

        _activeTailFilterState.LastEvaluatedLine = updatedLineCount;

        if (_activeTailFilterState.TimestampRange.HasBounds && !_activeTailFilterState.HasSeenParseableTimestamp)
        {
            _activeFilterStatusText = "Filter active (tailing): no parseable timestamps found yet for the selected time range.";
        }
        else
        {
            _activeFilterStatusText = $"Filter active (tailing): {FilteredLineCount:N0} matching lines.";
        }

        StatusText = _activeFilterStatusText;
        if (addedMatches <= 0)
            return;

        RaiseFilterPropertiesChanged();
        if (!wasAtBottom)
            return;

        var updatedInPlace = TryAppendFilteredTailLinesToViewportInPlace(previousDisplayCount, addedMatchingLines);
        if (!updatedInPlace)
            await LoadViewportAsync(Math.Max(0, DisplayLineCount - _viewportLineCount), _viewportLineCount, ct);

        SetNavigateTargetLine(VisibleLines.LastOrDefault()?.LineNumber ?? -1);
    }

    private bool TryAppendFilteredTailLinesToViewportInPlace(
        int previousDisplayCount,
        IReadOnlyList<(int LineNumber, string LineText)> addedMatchingLines)
    {
        if (!IsFilterActive || _snapshotFilteredLineNumbers == null || addedMatchingLines.Count == 0 || _viewportLineCount <= 0)
            return false;

        // Only do in-place updates when new matches are appended at the end of the filtered list.
        if (_snapshotFilteredLineNumbers.Count < previousDisplayCount + addedMatchingLines.Count)
            return false;

        for (var i = 0; i < addedMatchingLines.Count; i++)
        {
            var expectedLineNumber = _snapshotFilteredLineNumbers[previousDisplayCount + i];
            if (expectedLineNumber != addedMatchingLines[i].LineNumber)
                return false;
        }

        var previousBottomStart = Math.Max(0, previousDisplayCount - _viewportLineCount);
        var newBottomStart = Math.Max(0, _snapshotFilteredLineNumbers.Count - _viewportLineCount);

        if (_viewportStartLine < previousBottomStart)
            return false;

        var maxLines = Math.Max(1, _viewportLineCount);
        var appendedStartOffset = Math.Max(0, addedMatchingLines.Count - maxLines);
        var appendedToShowCount = addedMatchingLines.Count - appendedStartOffset;
        var retainedCount = Math.Max(0, Math.Min(VisibleLines.Count, maxLines - appendedToShowCount));

        while (VisibleLines.Count > retainedCount)
            VisibleLines.RemoveAt(0);

        for (var i = appendedStartOffset; i < addedMatchingLines.Count; i++)
        {
            var added = addedMatchingLines[i];
            VisibleLines.Add(new LogLineViewModel
            {
                LineNumber = added.LineNumber,
                Text = added.LineText,
                HighlightColor = LineHighlighter.GetHighlightColor(_settings.HighlightRules, added.LineText)
            });
        }

        _viewportStartLine = newBottomStart;
        _suppressScrollChange = true;
        ScrollPosition = _viewportStartLine;
        _suppressScrollChange = false;
        return true;
    }

    private static bool InsertSortedUnique(List<int> sortedLines, int lineNumber)
    {
        var index = sortedLines.BinarySearch(lineNumber);
        if (index >= 0)
            return false;

        sortedLines.Insert(~index, lineNumber);
        return true;
    }

    public async Task ResumeTailingWithCatchUpAsync(int pollingIntervalMs)
    {
        if (IsShutdownOrDisposed)
        {
            SuspendTailing();
            return;
        }

        if (Volatile.Read(ref _lineIndex) == null || IsLoading) return;
        pollingIntervalMs = Math.Max(100, pollingIntervalMs);
        var wasSuspended = IsSuspended;
        var navigateTargetBeforeResume = NavigateToLineNumber;
        if (!wasSuspended && _tailPollingIntervalMs == pollingIntervalMs) return;

        string? catchUpErrorMessage = null;
        var startedDuringResume = false;
        try
        {
            if (wasSuspended)
            {
                // Resume tailing immediately so visibility transitions are reflected synchronously.
                _tailService.StartTailing(FilePath, EffectiveEncoding, pollingIntervalMs);
                _tailPollingIntervalMs = pollingIntervalMs;
                IsSuspended = false;
                startedDuringResume = true;

                int? updatedLineCount = null;
                await _lineIndexLock.WaitAsync();
                try
                {
                    if (_lineIndex != null)
                    {
                        _lineIndex = await UpdateIndexOffUiAsync(_lineIndex, EffectiveEncoding, CancellationToken.None);
                        updatedLineCount = _lineIndex.LineCount;
                    }
                }
                finally
                {
                    _lineIndexLock.Release();
                }

                if (updatedLineCount != null)
                {
                    if (IsShutdownOrDisposed)
                    {
                        SuspendTailing();
                        return;
                    }

                    var previousTotalLines = TotalLines;
                    TotalLines = updatedLineCount.Value;
                    if (IsFilterActive)
                        await ApplyTailFilterForAppendedLinesAsync(updatedLineCount.Value, CancellationToken.None);

                    StatusText = IsFilterActive
                        ? _activeFilterStatusText ?? $"Filter active: {FilteredLineCount:N0} matching lines."
                        : $"{TotalLines:N0} lines";

                    if (AutoScrollEnabled && !IsFilterActive)
                    {
                        var updatedInPlace = await TryAppendTailLinesToViewportAsync(previousTotalLines, TotalLines, CancellationToken.None);
                        if (!updatedInPlace)
                            await LoadViewportAsync(Math.Max(0, TotalLines - _viewportLineCount), _viewportLineCount);

                        SetNavigateTargetLineIfUnchanged(navigateTargetBeforeResume, TotalLines);
                    }
                }
            }
            else
            {
                _tailService.StopTailing(FilePath);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            catchUpErrorMessage = ex.Message;
        }

        if (IsShutdownOrDisposed)
        {
            SuspendTailing();
            return;
        }

        try
        {
            if (!startedDuringResume)
            {
                _tailService.StartTailing(FilePath, EffectiveEncoding, pollingIntervalMs);
                _tailPollingIntervalMs = pollingIntervalMs;
                IsSuspended = false;
            }

            if (!string.IsNullOrWhiteSpace(catchUpErrorMessage))
                StatusText = $"Tail resumed (catch-up skipped): {catchUpErrorMessage}";
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            IsSuspended = true;
            StatusText = $"Tail error: {ex.Message}";
        }
    }

    private async void OnLinesAppended(object? sender, TailEventArgs e)
    {
        if (IsShutdownOrDisposed || !string.Equals(e.FilePath, FilePath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            int? updatedLineCount = null;
            await _lineIndexLock.WaitAsync();
            try
            {
                if (_lineIndex != null)
                {
                    _lineIndex = await _logReader.UpdateIndexAsync(FilePath, _lineIndex, EffectiveEncoding);
                    updatedLineCount = _lineIndex.LineCount;
                }
            }
            finally
            {
                _lineIndexLock.Release();
            }

            if (updatedLineCount == null || IsShutdownOrDisposed) return;

            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null) return;

            // Keep all VM-bound updates on the UI thread.
            _ = app.Dispatcher.BeginInvoke(async () =>
            {
                if (IsShutdownOrDisposed)
                    return;

                try
                {
                    var previousTotalLines = TotalLines;
                    TotalLines = updatedLineCount.Value;
                    if (IsFilterActive)
                    {
                        await ApplyTailFilterForAppendedLinesAsync(updatedLineCount.Value, CancellationToken.None);
                        StatusText = _activeFilterStatusText ?? $"Filter active: {FilteredLineCount:N0} matching lines.";
                        return;
                    }

                    StatusText = $"{TotalLines:N0} lines";
                    if (!AutoScrollEnabled) return;

                    var updatedInPlace = await TryAppendTailLinesToViewportAsync(previousTotalLines, TotalLines, CancellationToken.None);
                    if (!updatedInPlace)
                        await LoadViewportAsync(Math.Max(0, TotalLines - _viewportLineCount), _viewportLineCount);

                    SetNavigateTargetLine(TotalLines);
                }
                catch (OperationCanceledException) { }
            });
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            StatusText = $"Tail error: {ex.Message}";
        }
    }

    private void OnFileRotated(object? sender, FileRotatedEventArgs e)
    {
        if (IsShutdownOrDisposed || !string.Equals(e.FilePath, FilePath, StringComparison.OrdinalIgnoreCase)) return;

        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null) return;

        _ = app.Dispatcher.BeginInvoke(async () =>
        {
            if (IsShutdownOrDisposed)
                return;

            StatusText = "File rotated, reloading...";
            if (IsFilterActive)
            {
                _snapshotFilteredLineNumbers = null;
                _activeFilterStatusText = null;
                _activeTailFilterState = null;
                RaiseFilterPropertiesChanged();
            }
            await _lineIndexLock.WaitAsync();
            try
            {
                _lineIndex?.Dispose();
                _lineIndex = null;
            }
            finally
            {
                _lineIndexLock.Release();
            }

            await LoadAsync();
        });
    }

    private void OnTailError(object? sender, TailErrorEventArgs e)
    {
        if (IsShutdownOrDisposed || !string.Equals(e.FilePath, FilePath, StringComparison.OrdinalIgnoreCase)) return;

        void ApplyTailErrorState()
        {
            if (IsShutdownOrDisposed)
                return;

            IsSuspended = true;
            StatusText = $"Tailing stopped: {e.ErrorMessage}";
        }

        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null || app.Dispatcher.CheckAccess())
        {
            ApplyTailErrorState();
            return;
        }

        _ = app.Dispatcher.BeginInvoke(new Action(ApplyTailErrorState));
    }

    private void SetNavigateTargetLine(int lineNumber)
    {
        NavigateToLineNumber = -1;
        if (lineNumber > 0)
            NavigateToLineNumber = lineNumber;
    }

    private void SetNavigateTargetLineIfUnchanged(int expectedCurrentLine, int lineNumber)
    {
        if (NavigateToLineNumber != expectedCurrentLine)
            return;

        SetNavigateTargetLine(lineNumber);
    }

    private void ApplyVisibleLines(IReadOnlyList<LogLineViewModel> nextVisibleLines)
    {
        var sharedCount = Math.Min(VisibleLines.Count, nextVisibleLines.Count);
        for (var i = 0; i < sharedCount; i++)
            VisibleLines[i] = nextVisibleLines[i];

        for (var i = VisibleLines.Count - 1; i >= nextVisibleLines.Count; i--)
            VisibleLines.RemoveAt(i);

        for (var i = sharedCount; i < nextVisibleLines.Count; i++)
            VisibleLines.Add(nextVisibleLines[i]);
    }

    private Task<LineIndex> BuildIndexOffUiAsync(FileEncoding encoding, CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.BuildIndexAsync(FilePath, encoding, ct).ConfigureAwait(false), ct);

    private Task<IReadOnlyList<string>> ReadLinesOffUiAsync(
        LineIndex lineIndex,
        int startLine,
        int count,
        FileEncoding encoding,
        CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.ReadLinesAsync(FilePath, lineIndex, startLine, count, encoding, ct).ConfigureAwait(false), ct);

    private Task<string> ReadLineOffUiAsync(
        LineIndex lineIndex,
        int lineNumber,
        FileEncoding encoding,
        CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.ReadLineAsync(FilePath, lineIndex, lineNumber, encoding, ct).ConfigureAwait(false), ct);

    private Task<LineIndex> UpdateIndexOffUiAsync(
        LineIndex lineIndex,
        FileEncoding encoding,
        CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.UpdateIndexAsync(FilePath, lineIndex, encoding, ct).ConfigureAwait(false), ct);

    private sealed class ActiveTailFilterState
    {
        public Func<string, bool> Matcher { get; init; } = _ => false;
        public TimestampRange TimestampRange { get; init; }
        public int LastEvaluatedLine { get; set; }
        public bool HasSeenParseableTimestamp { get; set; }
    }

    private void RaiseFilterPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(FilteredLineCount));
        OnPropertyChanged(nameof(DisplayLineCount));
        OnPropertyChanged(nameof(MaxScrollPosition));
    }

    internal void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        _loadCts?.Cancel();
        _navCts?.Cancel();
        _tailService.StopTailing(FilePath);
        IsSuspended = true;
        IsLoading = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        BeginShutdown();
        _tailService.LinesAppended -= OnLinesAppended;
        _tailService.FileRotated -= OnFileRotated;
        _tailService.TailError -= OnTailError;
        _loadCts?.Dispose();
        _navCts?.Dispose();

        var lineIndexDisposeTask = EnsureLineIndexDisposedTask();
        try
        {
            lineIndexDisposeTask.Wait(DisposeLineIndexTimeout);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static inner =>
            inner is OperationCanceledException or ObjectDisposedException))
        {
        }
    }

    private Task EnsureLineIndexDisposedTask()
        => _lineIndexDisposeTask ??= DisposeLineIndexAsync();

    private void DisposeLineIndexUnsafe()
    {
        _lineIndex?.Dispose();
        _lineIndex = null;
    }

    private async Task DisposeLineIndexAsync()
    {
        var lockTaken = false;
        try
        {
            await _lineIndexLock.WaitAsync().ConfigureAwait(false);
            lockTaken = true;
            DisposeLineIndexUnsafe();
        }
        catch (ObjectDisposedException) { }
        finally
        {
            if (lockTaken)
                _lineIndexLock.Release();
        }
    }
}
