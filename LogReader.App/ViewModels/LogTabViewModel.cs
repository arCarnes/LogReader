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
    private readonly ILogReaderService _logReader;
    private readonly IFileTailService _tailService;
    private readonly SemaphoreSlim _lineIndexLock = new(1, 1);
    private AppSettings _settings;
    private LineIndex? _lineIndex;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _navCts;
    private int _isDisposed;
    private int _tailPollingIntervalMs = 250;
    private List<int>? _snapshotFilteredLineNumbers;
    private string? _activeFilterStatusText;
    private ActiveTailFilterState? _activeTailFilterState;
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(250);

    public string FileId { get; }
    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);

    [ObservableProperty]
    private FileEncoding _encoding = FileEncoding.Utf8;

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

    public static IReadOnlyList<FileEncoding> EncodingOptions { get; } = new[]
    {
        FileEncoding.Utf8,
        FileEncoding.Utf8Bom,
        FileEncoding.Ansi,
        FileEncoding.Utf16,
        FileEncoding.Utf16Be
    };

    public int ViewportLineCount => _viewportLineCount;
    public bool IsFilterActive => _snapshotFilteredLineNumbers != null;
    public int FilteredLineCount => _snapshotFilteredLineNumbers?.Count ?? 0;
    public int DisplayLineCount => IsFilterActive ? FilteredLineCount : TotalLines;
    public int MaxScrollPosition => Math.Max(0, DisplayLineCount - _viewportLineCount);

    [ObservableProperty]
    private int _scrollPosition;

    public LogTabViewModel(string fileId, string filePath, ILogReaderService logReader, IFileTailService tailService, AppSettings settings)
    {
        FileId = fileId;
        FilePath = filePath;
        _logReader = logReader;
        _tailService = tailService;
        _settings = settings;

        _tailService.LinesAppended += OnLinesAppended;
        _tailService.FileRotated += OnFileRotated;
    }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    internal void UpdateViewportLineCount(int count)
    {
        if (count <= 0 || _viewportLineCount == count) return;
        _viewportLineCount = count;
        _suppressScrollChange = true;
        OnPropertyChanged(nameof(ViewportLineCount));
        OnPropertyChanged(nameof(MaxScrollPosition));
        _suppressScrollChange = false;
        _ = LoadViewportAsync(_viewportStartLine, _viewportLineCount);
    }

    public Task RefreshViewportAsync() => LoadViewportAsync(_viewportStartLine, _viewportLineCount);

    public async Task LoadAsync()
    {
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

            var newIndex = await _logReader.BuildIndexAsync(FilePath, Encoding, cts.Token);
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

            // Load initial viewport
            var initialStart = IsFilterActive
                ? 0
                : Math.Max(0, TotalLines - _viewportLineCount);
            await LoadViewportAsync(initialStart, _viewportLineCount);

            // Start tailing
            _tailService.StartTailing(FilePath, Encoding, _tailPollingIntervalMs);
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

            if (lineIndexSnapshot == null) return;
            VisibleLines.Clear();
            if (IsFilterActive)
            {
                var filteredLines = _snapshotFilteredLineNumbers;
                if (filteredLines != null && filteredLines.Count > 0)
                {
                    var maxIndexExclusive = Math.Min(filteredLines.Count, _viewportStartLine + count);
                    for (int displayIndex = _viewportStartLine; displayIndex < maxIndexExclusive; displayIndex++)
                    {
                        var actualLineNumber = filteredLines[displayIndex];
                        var lineText = await _logReader.ReadLineAsync(
                            FilePath,
                            lineIndexSnapshot,
                            actualLineNumber - 1,
                            Encoding,
                            ct);

                        VisibleLines.Add(new LogLineViewModel
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
                var lines = await _logReader.ReadLinesAsync(
                    FilePath,
                    lineIndexSnapshot,
                    _viewportStartLine,
                    count,
                    Encoding,
                    ct);

                for (int i = 0; i < lines.Count; i++)
                {
                    VisibleLines.Add(new LogLineViewModel
                    {
                        LineNumber = _viewportStartLine + i + 1,
                        Text = lines[i],
                        HighlightColor = LineHighlighter.GetHighlightColor(_settings.HighlightRules, lines[i])
                    });
                }
            }

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

    partial void OnTotalLinesChanged(int value)
    {
        OnPropertyChanged(nameof(DisplayLineCount));
        OnPropertyChanged(nameof(MaxScrollPosition));
    }

    partial void OnScrollPositionChanged(int value)
    {
        if (_suppressScrollChange) return;
        AutoScrollEnabled = false;
        _ = ScrollToLineAsync(value);
    }

    private async Task ScrollToLineAsync(int startLine)
    {
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
        AutoScrollEnabled = false;
        await ScrollToLineAsync(0);
        SetNavigateTargetLine(VisibleLines.FirstOrDefault()?.LineNumber ?? (IsFilterActive ? -1 : 1));
    }

    [RelayCommand]
    private async Task JumpToBottom()
    {
        await ScrollToLineAsync(Math.Max(0, DisplayLineCount - _viewportLineCount));
        SetNavigateTargetLine(VisibleLines.LastOrDefault()?.LineNumber ?? (IsFilterActive ? -1 : TotalLines));
        AutoScrollEnabled = !IsFilterActive;
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
        // If the tab hasn't started loading yet, the upcoming explicit LoadAsync will use the correct encoding.
        // If a load is already in progress, LoadAsync will cancel the old one and restart.
        if (Volatile.Read(ref _lineIndex) == null && !IsLoading) return;
        _tailService.StopTailing(FilePath);
        _ = LoadAsync();
    }

    public void OnBecameVisible(bool globalAutoTailEnabled)
    {
        IsVisible = true;
        LastVisibleAtUtc = DateTime.UtcNow;
        ResumeTailingIfAllowed(globalAutoTailEnabled);
    }

    public void OnBecameHidden()
    {
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

    public void ResumeTailingIfAllowed(bool globalAutoTailEnabled)
    {
        _ = ResumeTailingWithCatchUpIfAllowedAsync(globalAutoTailEnabled, _tailPollingIntervalMs);
    }

    public void ApplyVisibleTailingMode(bool globalAutoTailEnabled, int pollingIntervalMs)
    {
        _ = ResumeTailingWithCatchUpIfAllowedAsync(globalAutoTailEnabled, pollingIntervalMs);
    }

    public async Task ApplySnapshotFilterAsync(
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
        AutoScrollEnabled = false;
        RaiseFilterPropertiesChanged();

        await LoadViewportAsync(0, _viewportLineCount);
        SetNavigateTargetLine(VisibleLines.FirstOrDefault()?.LineNumber ?? -1);
        StatusText = statusText;
    }

    public async Task ClearSnapshotFilterAsync()
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
        var appendedLines = await _logReader.ReadLinesAsync(
            FilePath,
            lineIndexSnapshot,
            firstUnprocessedLine - 1,
            readCount,
            Encoding,
            ct);

        var addedMatches = 0;
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
                addedMatches++;
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

        await LoadViewportAsync(Math.Max(0, DisplayLineCount - _viewportLineCount), _viewportLineCount, ct);
        SetNavigateTargetLine(VisibleLines.LastOrDefault()?.LineNumber ?? -1);
    }

    private static bool InsertSortedUnique(List<int> sortedLines, int lineNumber)
    {
        var index = sortedLines.BinarySearch(lineNumber);
        if (index >= 0)
            return false;

        sortedLines.Insert(~index, lineNumber);
        return true;
    }

    public async Task ResumeTailingWithCatchUpIfAllowedAsync(bool globalAutoTailEnabled, int pollingIntervalMs)
    {
        if (!globalAutoTailEnabled)
        {
            SuspendTailing();
            return;
        }

        if (Volatile.Read(ref _lineIndex) == null || IsLoading) return;
        pollingIntervalMs = Math.Max(100, pollingIntervalMs);
        if (!IsSuspended && _tailPollingIntervalMs == pollingIntervalMs) return;

        try
        {
            if (IsSuspended)
            {
                int? updatedLineCount = null;
                await _lineIndexLock.WaitAsync();
                try
                {
                    if (_lineIndex != null)
                    {
                        _lineIndex = await _logReader.UpdateIndexAsync(FilePath, _lineIndex, Encoding);
                        updatedLineCount = _lineIndex.LineCount;
                    }
                }
                finally
                {
                    _lineIndexLock.Release();
                }

                if (updatedLineCount != null)
                {
                    TotalLines = updatedLineCount.Value;
                    if (IsFilterActive)
                        await ApplyTailFilterForAppendedLinesAsync(updatedLineCount.Value, CancellationToken.None);

                    StatusText = IsFilterActive
                        ? _activeFilterStatusText ?? $"Filter active: {FilteredLineCount:N0} matching lines."
                        : $"{TotalLines:N0} lines";

                    if (AutoScrollEnabled && !IsFilterActive)
                    {
                        await LoadViewportAsync(Math.Max(0, TotalLines - _viewportLineCount), _viewportLineCount);
                        SetNavigateTargetLine(TotalLines);
                    }
                }
            }
            else
            {
                _tailService.StopTailing(FilePath);
            }

            _tailService.StartTailing(FilePath, Encoding, pollingIntervalMs);
            _tailPollingIntervalMs = pollingIntervalMs;
            IsSuspended = false;
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            StatusText = $"Tail error: {ex.Message}";
        }
    }

    private async void OnLinesAppended(object? sender, TailEventArgs e)
    {
        if (!string.Equals(e.FilePath, FilePath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            int? updatedLineCount = null;
            await _lineIndexLock.WaitAsync();
            try
            {
                if (_lineIndex != null)
                {
                    _lineIndex = await _logReader.UpdateIndexAsync(FilePath, _lineIndex, Encoding);
                    updatedLineCount = _lineIndex.LineCount;
                }
            }
            finally
            {
                _lineIndexLock.Release();
            }

            if (updatedLineCount == null) return;

            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null) return;

            // Keep all VM-bound updates on the UI thread.
            _ = app.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    TotalLines = updatedLineCount.Value;
                    if (IsFilterActive)
                    {
                        await ApplyTailFilterForAppendedLinesAsync(updatedLineCount.Value, CancellationToken.None);
                        StatusText = _activeFilterStatusText ?? $"Filter active: {FilteredLineCount:N0} matching lines.";
                        return;
                    }

                    StatusText = $"{TotalLines:N0} lines";
                    if (!AutoScrollEnabled) return;

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
        if (!string.Equals(e.FilePath, FilePath, StringComparison.OrdinalIgnoreCase)) return;

        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null) return;

        _ = app.Dispatcher.BeginInvoke(async () =>
        {
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

    private void SetNavigateTargetLine(int lineNumber)
    {
        NavigateToLineNumber = -1;
        if (lineNumber > 0)
            NavigateToLineNumber = lineNumber;
    }

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        _tailService.LinesAppended -= OnLinesAppended;
        _tailService.FileRotated -= OnFileRotated;
        _tailService.StopTailing(FilePath);
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _navCts?.Cancel();
        _navCts?.Dispose();

        if (_lineIndexLock.Wait(0))
        {
            try
            {
                DisposeLineIndexUnsafe();
            }
            finally
            {
                _lineIndexLock.Release();
            }
        }
        else
        {
            _ = DisposeLineIndexAsync();
        }
    }

    private void DisposeLineIndexUnsafe()
    {
        _lineIndex?.Dispose();
        _lineIndex = null;
    }

    private async Task DisposeLineIndexAsync()
    {
        try
        {
            await _lineIndexLock.WaitAsync().ConfigureAwait(false);
            DisposeLineIndexUnsafe();
        }
        finally
        {
            _lineIndexLock.Release();
        }
    }
}
