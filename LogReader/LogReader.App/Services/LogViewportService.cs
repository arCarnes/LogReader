namespace LogReader.App.Services;

using System.IO;
using LogReader.App.Helpers;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

internal sealed class LogViewportService
{
    private const int InPlaceScrollShiftThreshold = 8;
    private readonly record struct FilteredViewportReadBatch(int StartLineNumber, int Count);

    private readonly LogTabViewModel _owner;
    private readonly LogFilterSession _filterSession;

    private int _viewportStartLine;
    private int _viewportLineCount = 50;
    private bool _suppressScrollChange;
    private long _viewportRequestVersion;

    public LogViewportService(LogTabViewModel owner, LogFilterSession filterSession)
    {
        _owner = owner;
        _filterSession = filterSession;
    }

    public int ViewportLineCount => _viewportLineCount;

    public int ViewportStartLine => _viewportStartLine;

    public bool IsSuppressingScrollChange => _suppressScrollChange;

    public void UpdateViewportLineCount(int count)
    {
        if (_owner.IsShutdownOrDisposed || count <= 0 || _viewportLineCount == count)
            return;

        _viewportLineCount = count;
        _owner.RaiseViewportPropertiesChanged();
        _ = LoadViewportAsync(_viewportStartLine, _viewportLineCount);
    }

    public Task<bool> RefreshViewportAsync()
        => _owner.IsShutdownOrDisposed
            ? Task.FromResult(false)
            : LoadViewportAsync(_viewportStartLine, _viewportLineCount);

    public async Task<bool> LoadViewportAsync(int startLine, int count, CancellationToken ct = default)
    {
        if (_owner.IsShutdownOrDisposed)
            return false;

        var maxStart = Math.Max(0, _owner.DisplayLineCount - Math.Max(1, count));
        var clampedStartLine = Math.Max(0, Math.Min(startLine, maxStart));
        var requestVersion = BeginViewportRequest();

        try
        {
            var lineIndexSnapshot = await _owner.GetLineIndexSnapshotAsync(ct);
            if (lineIndexSnapshot == null || _owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(requestVersion))
                return false;

            if (await TryShiftViewportInPlaceAsync(lineIndexSnapshot, clampedStartLine, count, requestVersion, ct))
                return true;

            var nextVisibleLines = new List<LogLineViewModel>(Math.Max(0, count));
            if (_filterSession.IsActive)
            {
                var filteredLines = _filterSession.SnapshotFilteredLineNumbers;
                if (filteredLines != null && filteredLines.Count > 0)
                {
                    var visibleLineNumbers = GetVisibleFilteredLineNumbers(filteredLines, clampedStartLine, count);
                    var lineTextByNumber = new Dictionary<int, string>(visibleLineNumbers.Count);
                    foreach (var readBatch in BuildFilteredViewportReadBatches(visibleLineNumbers))
                    {
                        var lines = await _owner.ReadLinesOffUiAsync(
                            lineIndexSnapshot,
                            readBatch.StartLineNumber - 1,
                            readBatch.Count,
                            _owner.EffectiveEncoding,
                            ct);

                        if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(requestVersion))
                            return false;

                        for (var offset = 0; offset < lines.Count; offset++)
                            lineTextByNumber[readBatch.StartLineNumber + offset] = lines[offset];
                    }

                    foreach (var actualLineNumber in visibleLineNumbers)
                    {
                        lineTextByNumber.TryGetValue(actualLineNumber, out var lineText);
                        lineText ??= string.Empty;
                        nextVisibleLines.Add(new LogLineViewModel
                        {
                            LineNumber = actualLineNumber,
                            Text = lineText,
                            HighlightColor = LineHighlighter.GetHighlightColor(_owner.CurrentSettings.HighlightRules, lineText)
                        });
                    }
                }
            }
            else
            {
                var lines = await _owner.ReadLinesOffUiAsync(
                    lineIndexSnapshot,
                    clampedStartLine,
                    count,
                    _owner.EffectiveEncoding,
                    ct);

                if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(requestVersion))
                    return false;

                for (var i = 0; i < lines.Count; i++)
                {
                    nextVisibleLines.Add(new LogLineViewModel
                    {
                        LineNumber = clampedStartLine + i + 1,
                        Text = lines[i],
                        HighlightColor = LineHighlighter.GetHighlightColor(_owner.CurrentSettings.HighlightRules, lines[i])
                    });
                }
            }

            if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(requestVersion))
                return false;

            _viewportStartLine = clampedStartLine;
            _owner.ApplyVisibleLines(nextVisibleLines);
            SetScrollPosition(clampedStartLine);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (IsCurrentViewportRequest(requestVersion))
                _owner.StatusText = $"Read error: {ex.Message}";

            return false;
        }
    }

    public async Task<bool> TryAppendTailLinesToViewportAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct)
    {
        if (_filterSession.IsActive || !_owner.AutoScrollEnabled)
            return false;

        if (updatedLineCount <= previousTotalLines || _viewportLineCount <= 0)
            return false;

        var expectedPreviousStart = Math.Max(0, previousTotalLines - _viewportLineCount);
        if (_viewportStartLine != expectedPreviousStart)
            return false;

        if (previousTotalLines > 0 && _owner.VisibleLines.Count > 0 && _owner.VisibleLines[^1].LineNumber != previousTotalLines)
            return false;

        var maxLines = Math.Max(1, _viewportLineCount);
        var nextViewportStart = Math.Max(0, updatedLineCount - maxLines);
        var requestVersion = BeginViewportRequest();
        var lineIndexSnapshot = await _owner.GetLineIndexSnapshotAsync(ct);
        if (lineIndexSnapshot == null || _owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(requestVersion))
            return false;

        var appendedCount = updatedLineCount - previousTotalLines;
        var appendedLines = await _owner.ReadLinesOffUiAsync(
            lineIndexSnapshot,
            previousTotalLines,
            appendedCount,
            _owner.EffectiveEncoding,
            ct);

        if (appendedLines.Count <= 0 || _owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(requestVersion))
            return false;

        var appendedStartOffset = Math.Max(0, appendedLines.Count - maxLines);
        var appendedToShowCount = appendedLines.Count - appendedStartOffset;
        var retainedCount = Math.Max(0, Math.Min(_owner.VisibleLines.Count, maxLines - appendedToShowCount));

        while (_owner.VisibleLines.Count > retainedCount)
            _owner.VisibleLines.RemoveAt(0);

        for (var i = appendedStartOffset; i < appendedLines.Count; i++)
        {
            var lineText = appendedLines[i];
            _owner.VisibleLines.Add(new LogLineViewModel
            {
                LineNumber = previousTotalLines + i + 1,
                Text = lineText,
                HighlightColor = LineHighlighter.GetHighlightColor(_owner.CurrentSettings.HighlightRules, lineText)
            });
        }

        _viewportStartLine = nextViewportStart;
        SetScrollPosition(nextViewportStart);
        return true;
    }

    public async Task<bool> ScrollToLineAsync(int startLine, CancellationTokenSource? existingNavCts)
    {
        if (_viewportStartLine == startLine && _owner.VisibleLines.Count > 0)
            return true;

        existingNavCts?.Cancel();
        existingNavCts?.Dispose();
        var navCts = new CancellationTokenSource();
        _owner.ReplaceNavigationCts(navCts);
        try
        {
            var viewportApplied = await LoadViewportAsync(startLine, _viewportLineCount, navCts.Token);
            if (!viewportApplied)
                return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        _owner.SetNavigateTargetLine(_owner.VisibleLines.FirstOrDefault()?.LineNumber ?? (_filterSession.IsActive ? -1 : _viewportStartLine + 1));
        return true;
    }

    public Task<bool> JumpToTopAsync(CancellationTokenSource? existingNavCts)
        => ScrollToLineAsync(0, existingNavCts);

    public Task<bool> JumpToBottomAsync(CancellationTokenSource? existingNavCts)
        => ScrollToLineAsync(Math.Max(0, _owner.DisplayLineCount - _viewportLineCount), existingNavCts);

    public async Task NavigateToLineAsync(int lineNumber, CancellationTokenSource? existingNavCts)
    {
        existingNavCts?.Cancel();
        existingNavCts?.Dispose();
        var navCts = new CancellationTokenSource();
        _owner.ReplaceNavigationCts(navCts);
        var ct = navCts.Token;

        var navigateTargetLine = lineNumber;
        int startLine;
        if (_filterSession.IsActive)
        {
            var filteredLines = _filterSession.SnapshotFilteredLineNumbers;
            if (filteredLines == null || filteredLines.Count == 0)
            {
                startLine = 0;
                navigateTargetLine = -1;
            }
            else
            {
                var filterIndex = filteredLines is List<int> filteredList
                    ? filteredList.BinarySearch(lineNumber)
                    : filteredLines.ToList().BinarySearch(lineNumber);
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
            var viewportApplied = await LoadViewportAsync(startLine, _viewportLineCount, ct);
            if (!viewportApplied)
                return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        _owner.SetNavigateTargetLine(navigateTargetLine);
    }

    public bool TryAppendFilteredTailLinesToViewportInPlace(
        int previousDisplayCount,
        IReadOnlyList<LogFilterSession.FilterTailMatch> addedMatchingLines)
    {
        var filteredLines = _filterSession.SnapshotFilteredLineNumbers;
        if (!_filterSession.IsActive || filteredLines == null || addedMatchingLines.Count == 0 || _viewportLineCount <= 0)
            return false;

        if (filteredLines.Count < previousDisplayCount + addedMatchingLines.Count)
            return false;

        for (var i = 0; i < addedMatchingLines.Count; i++)
        {
            var expectedLineNumber = filteredLines[previousDisplayCount + i];
            if (expectedLineNumber != addedMatchingLines[i].LineNumber)
                return false;
        }

        var maxLines = Math.Max(1, _viewportLineCount);
        var previousBottomStart = Math.Max(0, previousDisplayCount - maxLines);
        var newBottomStart = Math.Max(0, filteredLines.Count - _viewportLineCount);

        if (!MatchesVisibleLines(filteredLines, previousBottomStart, previousDisplayCount))
            return false;

        BeginViewportRequest();
        var appendedStartOffset = Math.Max(0, addedMatchingLines.Count - maxLines);
        var appendedToShowCount = addedMatchingLines.Count - appendedStartOffset;
        var retainedCount = Math.Max(0, Math.Min(_owner.VisibleLines.Count, maxLines - appendedToShowCount));

        while (_owner.VisibleLines.Count > retainedCount)
            _owner.VisibleLines.RemoveAt(0);

        for (var i = appendedStartOffset; i < addedMatchingLines.Count; i++)
        {
            var added = addedMatchingLines[i];
            _owner.VisibleLines.Add(new LogLineViewModel
            {
                LineNumber = added.LineNumber,
                Text = added.LineText,
                HighlightColor = LineHighlighter.GetHighlightColor(_owner.CurrentSettings.HighlightRules, added.LineText)
            });
        }

        _viewportStartLine = newBottomStart;
        SetScrollPosition(_viewportStartLine);
        return true;
    }

    private bool MatchesVisibleLines(IReadOnlyList<int> filteredLines, int viewportStart, int filteredLineCount)
    {
        if (_viewportStartLine != viewportStart)
            return false;

        var expectedVisibleCount = Math.Max(0, Math.Min(_viewportLineCount, filteredLineCount - viewportStart));
        if (_owner.VisibleLines.Count != expectedVisibleCount)
            return false;

        for (var i = 0; i < expectedVisibleCount; i++)
        {
            if (_owner.VisibleLines[i].LineNumber != filteredLines[viewportStart + i])
                return false;
        }

        return true;
    }

    private void SetScrollPosition(int value)
    {
        _suppressScrollChange = true;
        _owner.ScrollPosition = value;
        _suppressScrollChange = false;
    }

    private async Task<bool> TryShiftViewportInPlaceAsync(
        LineIndex lineIndexSnapshot,
        int nextStartLine,
        int count,
        long requestVersion,
        CancellationToken ct)
    {
        if (_filterSession.IsActive ||
            _owner.IsShutdownOrDisposed ||
            count <= 0 ||
            _owner.VisibleLines.Count != count)
        {
            return false;
        }

        var delta = nextStartLine - _viewportStartLine;
        if (delta == 0 || Math.Abs(delta) > InPlaceScrollShiftThreshold || Math.Abs(delta) >= count)
            return false;

        if (delta > 0)
        {
            var appendedLines = await _owner.ReadLinesOffUiAsync(
                lineIndexSnapshot,
                _viewportStartLine + count,
                delta,
                _owner.EffectiveEncoding,
                ct);

            if (_owner.IsShutdownOrDisposed ||
                !IsCurrentViewportRequest(requestVersion) ||
                appendedLines.Count != delta)
            {
                return false;
            }

            for (var i = 0; i < delta; i++)
                _owner.VisibleLines.RemoveAt(0);

            for (var i = 0; i < appendedLines.Count; i++)
            {
                var lineText = appendedLines[i];
                _owner.VisibleLines.Add(new LogLineViewModel
                {
                    LineNumber = nextStartLine + count - delta + i + 1,
                    Text = lineText,
                    HighlightColor = LineHighlighter.GetHighlightColor(_owner.CurrentSettings.HighlightRules, lineText)
                });
            }
        }
        else
        {
            var prependCount = -delta;
            var prependedLines = await _owner.ReadLinesOffUiAsync(
                lineIndexSnapshot,
                nextStartLine,
                prependCount,
                _owner.EffectiveEncoding,
                ct);

            if (_owner.IsShutdownOrDisposed ||
                !IsCurrentViewportRequest(requestVersion) ||
                prependedLines.Count != prependCount)
            {
                return false;
            }

            for (var i = prependedLines.Count - 1; i >= 0; i--)
            {
                var lineText = prependedLines[i];
                _owner.VisibleLines.Insert(0, new LogLineViewModel
                {
                    LineNumber = nextStartLine + i + 1,
                    Text = lineText,
                    HighlightColor = LineHighlighter.GetHighlightColor(_owner.CurrentSettings.HighlightRules, lineText)
                });
            }

            while (_owner.VisibleLines.Count > count)
                _owner.VisibleLines.RemoveAt(_owner.VisibleLines.Count - 1);
        }

        _viewportStartLine = nextStartLine;
        SetScrollPosition(nextStartLine);
        return true;
    }

    private long BeginViewportRequest()
    {
        return Interlocked.Increment(ref _viewportRequestVersion);
    }

    private static List<int> GetVisibleFilteredLineNumbers(IReadOnlyList<int> filteredLines, int startLine, int count)
    {
        var maxIndexExclusive = Math.Min(filteredLines.Count, startLine + count);
        if (maxIndexExclusive <= startLine)
            return new List<int>();

        var visibleLineNumbers = new List<int>(maxIndexExclusive - startLine);
        for (var displayIndex = startLine; displayIndex < maxIndexExclusive; displayIndex++)
            visibleLineNumbers.Add(filteredLines[displayIndex]);

        return visibleLineNumbers;
    }

    private static List<FilteredViewportReadBatch> BuildFilteredViewportReadBatches(IReadOnlyList<int> lineNumbers)
    {
        var batches = new List<FilteredViewportReadBatch>();
        if (lineNumbers.Count == 0)
            return batches;

        var batchStart = lineNumbers[0];
        var batchCount = 1;
        for (var i = 1; i < lineNumbers.Count; i++)
        {
            if (lineNumbers[i] == lineNumbers[i - 1] + 1)
            {
                batchCount++;
                continue;
            }

            batches.Add(new FilteredViewportReadBatch(batchStart, batchCount));
            batchStart = lineNumbers[i];
            batchCount = 1;
        }

        batches.Add(new FilteredViewportReadBatch(batchStart, batchCount));
        return batches;
    }

    private bool IsCurrentViewportRequest(long requestVersion)
        => Volatile.Read(ref _viewportRequestVersion) == requestVersion;
}
