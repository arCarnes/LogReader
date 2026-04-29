namespace LogReader.App.Services;

using System.IO;
using LogReader.App.Helpers;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Models;

internal sealed class LogViewportService
{
    private const int InPlaceScrollShiftThreshold = 8;
    private readonly record struct FilteredViewportReadBatch(int StartLineNumber, int Count);
    private readonly record struct VisibleLineSnapshot(int LineNumber, string Text, string? HighlightColor);
    private sealed record PreparedViewport(int StartLine, IReadOnlyList<LogLineViewModel> VisibleLines);
    private readonly record struct ViewportRequestSnapshot(
        int ClampedStartLine,
        int Count,
        int CurrentViewportStartLine,
        long RequestVersion,
        bool IsFilterActive,
        IReadOnlyList<int>? FilteredLineNumbers,
        IReadOnlyList<VisibleLineSnapshot> VisibleLines);
    private readonly record struct TailAppendRequestSnapshot(
        int PreviousTotalLines,
        int UpdatedLineCount,
        int ViewportLineCount,
        int CurrentViewportStartLine,
        long RequestVersion,
        IReadOnlyList<VisibleLineSnapshot> VisibleLines);

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

        var targetStartLine = _owner.AutoScrollEnabled
            ? Math.Max(0, _owner.DisplayLineCount - _viewportLineCount)
            : _viewportStartLine;
        _ = LoadViewportAsync(targetStartLine, _viewportLineCount);
    }

    public Task<bool> RefreshViewportAsync()
        => _owner.IsShutdownOrDisposed
            ? Task.FromResult(false)
            : LoadViewportAsync(_viewportStartLine, _viewportLineCount);

    public async Task<bool> LoadViewportAsync(int startLine, int count, CancellationToken ct = default)
    {
        if (_owner.IsShutdownOrDisposed)
            return false;

        var snapshot = await _owner.InvokeOnUiAsync(() => CaptureViewportRequest(startLine, count)).ConfigureAwait(false);
        if (snapshot == null)
            return false;

        try
        {
            var preparedViewport = await PrepareViewportAsync(snapshot.Value, ct).ConfigureAwait(false);
            if (preparedViewport == null)
                return false;

            return await _owner.InvokeOnUiAsync(() => ApplyPreparedViewport(snapshot.Value.RequestVersion, preparedViewport)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (IsCurrentViewportRequest(snapshot.Value.RequestVersion))
                await _owner.InvokeOnUiAsync(() => _owner.StatusText = $"Read error: {ex.Message}").ConfigureAwait(false);

            return false;
        }
    }

    public async Task<bool> TryAppendTailLinesToViewportAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct)
    {
        var snapshot = await _owner.InvokeOnUiAsync(() => CaptureTailAppendRequest(previousTotalLines, updatedLineCount)).ConfigureAwait(false);
        if (snapshot == null)
            return false;

        var preparedViewport = await PrepareTailAppendAsync(snapshot.Value, ct).ConfigureAwait(false);
        if (preparedViewport == null)
            return false;

        return await _owner.InvokeOnUiAsync(() => ApplyPreparedViewport(snapshot.Value.RequestVersion, preparedViewport)).ConfigureAwait(false);
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
            var viewportApplied = await LoadViewportAsync(startLine, _viewportLineCount, navCts.Token).ConfigureAwait(false);
            if (!viewportApplied)
                return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        await _owner.InvokeOnUiAsync(() =>
            _owner.SetNavigateTargetLine(_owner.VisibleLines.FirstOrDefault()?.LineNumber ?? (_filterSession.IsActive ? -1 : _viewportStartLine + 1))).ConfigureAwait(false);
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

        var navigationTarget = await _owner.InvokeOnUiAsync(() =>
        {
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

            return (StartLine: startLine, NavigateTargetLine: navigateTargetLine);
        }).ConfigureAwait(false);

        try
        {
            var viewportApplied = await LoadViewportAsync(navigationTarget.StartLine, _viewportLineCount, ct).ConfigureAwait(false);
            if (!viewportApplied || ct.IsCancellationRequested)
                return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _owner.InvokeOnUiAsync(() => _owner.SetNavigateTargetLine(navigationTarget.NavigateTargetLine)).ConfigureAwait(false);
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

    private ViewportRequestSnapshot? CaptureViewportRequest(int startLine, int count)
    {
        if (_owner.IsShutdownOrDisposed)
            return null;

        var maxStart = Math.Max(0, _owner.DisplayLineCount - Math.Max(1, count));
        var clampedStartLine = Math.Max(0, Math.Min(startLine, maxStart));
        var requestVersion = BeginViewportRequest();

        return new ViewportRequestSnapshot(
            clampedStartLine,
            count,
            _viewportStartLine,
            requestVersion,
            _filterSession.IsActive,
            _filterSession.ViewportFilteredLineNumbersSnapshot,
            SnapshotVisibleLines());
    }

    private TailAppendRequestSnapshot? CaptureTailAppendRequest(int previousTotalLines, int updatedLineCount)
    {
        if (_owner.IsShutdownOrDisposed ||
            _filterSession.IsActive ||
            !_owner.AutoScrollEnabled ||
            updatedLineCount <= previousTotalLines ||
            _viewportLineCount <= 0)
        {
            return null;
        }

        var requestVersion = BeginViewportRequest();
        return new TailAppendRequestSnapshot(
            previousTotalLines,
            updatedLineCount,
            _viewportLineCount,
            _viewportStartLine,
            requestVersion,
            SnapshotVisibleLines());
    }

    private async Task<PreparedViewport?> PrepareViewportAsync(ViewportRequestSnapshot snapshot, CancellationToken ct)
    {
        return await _owner.WithLineIndexLeaseAsync(
            async (lineIndexSnapshot, effectiveEncoding, innerCt) =>
            {
                if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(snapshot.RequestVersion))
                    return null;

                var shiftedViewport = await TryPrepareShiftViewportInPlaceAsync(
                    snapshot,
                    lineIndexSnapshot,
                    effectiveEncoding,
                    snapshot.RequestVersion,
                    innerCt).ConfigureAwait(false);
                if (shiftedViewport != null)
                    return shiftedViewport;

                var nextVisibleLines = new List<LogLineViewModel>(Math.Max(0, snapshot.Count));
                if (snapshot.IsFilterActive)
                {
                    var filteredLines = snapshot.FilteredLineNumbers;
                    if (filteredLines != null && filteredLines.Count > 0)
                    {
                        var visibleLineNumbers = GetVisibleFilteredLineNumbers(filteredLines, snapshot.ClampedStartLine, snapshot.Count);
                        var lineTextByNumber = new Dictionary<int, string>(visibleLineNumbers.Count);
                        foreach (var readBatch in BuildFilteredViewportReadBatches(visibleLineNumbers))
                        {
                            var lines = await _owner.ReadLinesOffUiAsync(
                                lineIndexSnapshot,
                                readBatch.StartLineNumber - 1,
                                readBatch.Count,
                                effectiveEncoding,
                                innerCt).ConfigureAwait(false);

                            if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(snapshot.RequestVersion))
                                return null;

                            for (var offset = 0; offset < lines.Count; offset++)
                                lineTextByNumber[readBatch.StartLineNumber + offset] = lines[offset];
                        }

                        foreach (var actualLineNumber in visibleLineNumbers)
                        {
                            lineTextByNumber.TryGetValue(actualLineNumber, out var lineText);
                            nextVisibleLines.Add(CreateVisibleLine(actualLineNumber, lineText ?? string.Empty));
                        }
                    }
                }
                else
                {
                    var lines = await _owner.ReadLinesOffUiAsync(
                        lineIndexSnapshot,
                        snapshot.ClampedStartLine,
                        snapshot.Count,
                        effectiveEncoding,
                        innerCt).ConfigureAwait(false);

                    if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(snapshot.RequestVersion))
                        return null;

                    for (var i = 0; i < lines.Count; i++)
                        nextVisibleLines.Add(CreateVisibleLine(snapshot.ClampedStartLine + i + 1, lines[i]));
                }

                if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(snapshot.RequestVersion))
                    return null;

                return new PreparedViewport(snapshot.ClampedStartLine, nextVisibleLines);
            },
            ct).ConfigureAwait(false);
    }

    private async Task<PreparedViewport?> PrepareTailAppendAsync(TailAppendRequestSnapshot snapshot, CancellationToken ct)
    {
        var expectedPreviousStart = Math.Max(0, snapshot.PreviousTotalLines - snapshot.ViewportLineCount);
        if (snapshot.CurrentViewportStartLine != expectedPreviousStart)
            return null;

        if (snapshot.PreviousTotalLines > 0 &&
            snapshot.VisibleLines.Count > 0 &&
            snapshot.VisibleLines[^1].LineNumber != snapshot.PreviousTotalLines)
        {
            return null;
        }

        return await _owner.WithLineIndexLeaseAsync(
            async (lineIndexSnapshot, effectiveEncoding, innerCt) =>
            {
                if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(snapshot.RequestVersion))
                    return null;

                var maxLines = Math.Max(1, snapshot.ViewportLineCount);
                var appendedCount = snapshot.UpdatedLineCount - snapshot.PreviousTotalLines;
                var appendedStartOffset = Math.Max(0, appendedCount - maxLines);
                var appendedToShowCount = appendedCount - appendedStartOffset;
                var appendedLines = await _owner.ReadLinesOffUiAsync(
                    lineIndexSnapshot,
                    snapshot.PreviousTotalLines + appendedStartOffset,
                    appendedToShowCount,
                    effectiveEncoding,
                    innerCt).ConfigureAwait(false);
                if (appendedLines.Count <= 0 || _owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(snapshot.RequestVersion))
                    return null;

                var nextVisibleLines = new List<LogLineViewModel>(maxLines);
                var retainedCount = Math.Max(0, Math.Min(snapshot.VisibleLines.Count, maxLines - appendedToShowCount));

                for (var i = snapshot.VisibleLines.Count - retainedCount; i < snapshot.VisibleLines.Count; i++)
                    nextVisibleLines.Add(ToViewModel(snapshot.VisibleLines[i]));

                for (var i = 0; i < appendedLines.Count; i++)
                    nextVisibleLines.Add(CreateVisibleLine(snapshot.PreviousTotalLines + appendedStartOffset + i + 1, appendedLines[i]));

                var nextViewportStart = Math.Max(0, snapshot.UpdatedLineCount - maxLines);
                return new PreparedViewport(nextViewportStart, nextVisibleLines);
            },
            ct).ConfigureAwait(false);
    }

    private async Task<PreparedViewport?> TryPrepareShiftViewportInPlaceAsync(
        ViewportRequestSnapshot snapshot,
        LineIndex lineIndexSnapshot,
        FileEncoding effectiveEncoding,
        long requestVersion,
        CancellationToken ct)
    {
        if (snapshot.IsFilterActive)
        {
            return await TryPrepareFilteredShiftViewportInPlaceAsync(
                snapshot,
                lineIndexSnapshot,
                effectiveEncoding,
                requestVersion,
                ct).ConfigureAwait(false);
        }

        if (countIsInvalid(snapshot))
            return null;

        var delta = snapshot.ClampedStartLine - snapshot.CurrentViewportStartLine;
        if (delta == 0 || Math.Abs(delta) > InPlaceScrollShiftThreshold || Math.Abs(delta) >= snapshot.Count)
            return null;

        var nextVisibleLines = new List<LogLineViewModel>(snapshot.Count);
        if (delta > 0)
        {
            var appendedLines = await _owner.ReadLinesOffUiAsync(
                lineIndexSnapshot,
                snapshot.CurrentViewportStartLine + snapshot.Count,
                delta,
                effectiveEncoding,
                ct).ConfigureAwait(false);

            if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(requestVersion) || appendedLines.Count != delta)
                return null;

            for (var i = delta; i < snapshot.VisibleLines.Count; i++)
                nextVisibleLines.Add(ToViewModel(snapshot.VisibleLines[i]));

            for (var i = 0; i < appendedLines.Count; i++)
                nextVisibleLines.Add(CreateVisibleLine(snapshot.ClampedStartLine + snapshot.Count - delta + i + 1, appendedLines[i]));
        }
        else
        {
            var prependCount = -delta;
            var prependedLines = await _owner.ReadLinesOffUiAsync(
                lineIndexSnapshot,
                snapshot.ClampedStartLine,
                prependCount,
                effectiveEncoding,
                ct).ConfigureAwait(false);

            if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(requestVersion) || prependedLines.Count != prependCount)
                return null;

            for (var i = 0; i < prependedLines.Count; i++)
                nextVisibleLines.Add(CreateVisibleLine(snapshot.ClampedStartLine + i + 1, prependedLines[i]));

            for (var i = 0; i < snapshot.Count - prependCount; i++)
                nextVisibleLines.Add(ToViewModel(snapshot.VisibleLines[i]));
        }

        return new PreparedViewport(snapshot.ClampedStartLine, nextVisibleLines);

        static bool countIsInvalid(ViewportRequestSnapshot localSnapshot)
            => localSnapshot.Count <= 0 || localSnapshot.VisibleLines.Count != localSnapshot.Count;
    }

    private async Task<PreparedViewport?> TryPrepareFilteredShiftViewportInPlaceAsync(
        ViewportRequestSnapshot snapshot,
        LineIndex lineIndexSnapshot,
        FileEncoding effectiveEncoding,
        long requestVersion,
        CancellationToken ct)
    {
        var filteredLines = snapshot.FilteredLineNumbers;
        if (snapshot.Count <= 0 || filteredLines == null || filteredLines.Count == 0)
            return null;

        var delta = snapshot.ClampedStartLine - snapshot.CurrentViewportStartLine;
        if (delta == 0 || Math.Abs(delta) > InPlaceScrollShiftThreshold)
            return null;

        var currentVisibleCount = GetVisibleCount(filteredLines.Count, snapshot.CurrentViewportStartLine, snapshot.Count);
        var targetVisibleCount = GetVisibleCount(filteredLines.Count, snapshot.ClampedStartLine, snapshot.Count);
        if (currentVisibleCount <= 0 ||
            targetVisibleCount <= 0 ||
            snapshot.VisibleLines.Count != currentVisibleCount)
        {
            return null;
        }

        var nextVisibleLines = new List<LogLineViewModel>(targetVisibleCount);
        var targetLineNumbers = GetVisibleFilteredLineNumbers(filteredLines, snapshot.ClampedStartLine, snapshot.Count);

        if (delta > 0)
        {
            var overlapCount = Math.Min(Math.Max(0, currentVisibleCount - delta), targetVisibleCount);
            var appendedLineNumbers = targetLineNumbers.Skip(overlapCount).ToList();
            var appendedLines = await ReadFilteredVisibleLinesAsync(
                appendedLineNumbers,
                lineIndexSnapshot,
                effectiveEncoding,
                requestVersion,
                ct).ConfigureAwait(false);
            if (appendedLines == null)
                return null;

            for (var i = delta; i < delta + overlapCount; i++)
                nextVisibleLines.Add(ToViewModel(snapshot.VisibleLines[i]));

            nextVisibleLines.AddRange(appendedLines);
        }
        else
        {
            var prependCount = Math.Min(-delta, targetVisibleCount);
            var prependedLineNumbers = targetLineNumbers.Take(prependCount).ToList();
            var prependedLines = await ReadFilteredVisibleLinesAsync(
                prependedLineNumbers,
                lineIndexSnapshot,
                effectiveEncoding,
                requestVersion,
                ct).ConfigureAwait(false);
            if (prependedLines == null)
                return null;

            nextVisibleLines.AddRange(prependedLines);

            var overlapCount = Math.Min(currentVisibleCount, targetVisibleCount - prependCount);
            for (var i = 0; i < overlapCount; i++)
                nextVisibleLines.Add(ToViewModel(snapshot.VisibleLines[i]));
        }

        return nextVisibleLines.Count == targetVisibleCount
            ? new PreparedViewport(snapshot.ClampedStartLine, nextVisibleLines)
            : null;
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

    private IReadOnlyList<VisibleLineSnapshot> SnapshotVisibleLines()
        => _owner.VisibleLines
            .Select(line => new VisibleLineSnapshot(line.LineNumber, line.Text, line.HighlightColor))
            .ToList();

    private bool ApplyPreparedViewport(long requestVersion, PreparedViewport preparedViewport)
    {
        if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(requestVersion))
            return false;

        _viewportStartLine = preparedViewport.StartLine;
        _owner.ApplyVisibleLines(preparedViewport.VisibleLines);
        SetScrollPosition(preparedViewport.StartLine);
        return true;
    }

    private LogLineViewModel CreateVisibleLine(int lineNumber, string lineText)
        => new()
        {
            LineNumber = lineNumber,
            Text = lineText,
            HighlightColor = LineHighlighter.GetHighlightColor(_owner.CurrentSettings.HighlightRules, lineText)
        };

    private static LogLineViewModel ToViewModel(VisibleLineSnapshot line)
        => new()
        {
            LineNumber = line.LineNumber,
            Text = line.Text,
            HighlightColor = line.HighlightColor
        };

    private void SetScrollPosition(int value)
    {
        _suppressScrollChange = true;
        _owner.ScrollPosition = value;
        _suppressScrollChange = false;
    }

    private long BeginViewportRequest()
        => Interlocked.Increment(ref _viewportRequestVersion);

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

    private async Task<List<LogLineViewModel>?> ReadFilteredVisibleLinesAsync(
        IReadOnlyList<int> lineNumbers,
        LineIndex lineIndexSnapshot,
        FileEncoding effectiveEncoding,
        long requestVersion,
        CancellationToken ct)
    {
        if (lineNumbers.Count == 0)
            return new List<LogLineViewModel>();

        var lineTextByNumber = new Dictionary<int, string>(lineNumbers.Count);
        foreach (var readBatch in BuildFilteredViewportReadBatches(lineNumbers))
        {
            var lines = await _owner.ReadLinesOffUiAsync(
                lineIndexSnapshot,
                readBatch.StartLineNumber - 1,
                readBatch.Count,
                effectiveEncoding,
                ct).ConfigureAwait(false);

            if (_owner.IsShutdownOrDisposed || !IsCurrentViewportRequest(requestVersion))
                return null;

            for (var offset = 0; offset < lines.Count; offset++)
                lineTextByNumber[readBatch.StartLineNumber + offset] = lines[offset];
        }

        var visibleLines = new List<LogLineViewModel>(lineNumbers.Count);
        foreach (var lineNumber in lineNumbers)
        {
            lineTextByNumber.TryGetValue(lineNumber, out var lineText);
            visibleLines.Add(CreateVisibleLine(lineNumber, lineText ?? string.Empty));
        }

        return visibleLines;
    }

    private static int GetVisibleCount(int filteredLineCount, int startLine, int count)
        => Math.Max(0, Math.Min(filteredLineCount, startLine + count) - startLine);

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
