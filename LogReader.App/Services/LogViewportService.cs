namespace LogReader.App.Services;

using System.IO;
using LogReader.App.Helpers;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

internal sealed class LogViewportService
{
    private readonly LogTabViewModel _owner;
    private readonly LogFilterSession _filterSession;

    private int _viewportStartLine;
    private int _viewportLineCount = 50;
    private bool _suppressScrollChange;

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

    public Task RefreshViewportAsync()
        => _owner.IsShutdownOrDisposed
            ? Task.CompletedTask
            : LoadViewportAsync(_viewportStartLine, _viewportLineCount);

    public async Task LoadViewportAsync(int startLine, int count, CancellationToken ct = default)
    {
        if (_owner.IsShutdownOrDisposed)
            return;

        var maxStart = Math.Max(0, _owner.DisplayLineCount - Math.Max(1, count));
        _viewportStartLine = Math.Max(0, Math.Min(startLine, maxStart));

        try
        {
            var lineIndexSnapshot = await _owner.GetLineIndexSnapshotAsync(ct);
            if (lineIndexSnapshot == null || _owner.IsShutdownOrDisposed)
                return;

            var nextVisibleLines = new List<LogLineViewModel>(Math.Max(0, count));
            if (_filterSession.IsActive)
            {
                var filteredLines = _filterSession.SnapshotFilteredLineNumbers;
                if (filteredLines != null && filteredLines.Count > 0)
                {
                    var maxIndexExclusive = Math.Min(filteredLines.Count, _viewportStartLine + count);
                    for (var displayIndex = _viewportStartLine; displayIndex < maxIndexExclusive; displayIndex++)
                    {
                        var actualLineNumber = filteredLines[displayIndex];
                        var lineText = await _owner.ReadLineOffUiAsync(
                            lineIndexSnapshot,
                            actualLineNumber - 1,
                            _owner.EffectiveEncoding,
                            ct);

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
                    _viewportStartLine,
                    count,
                    _owner.EffectiveEncoding,
                    ct);

                for (var i = 0; i < lines.Count; i++)
                {
                    nextVisibleLines.Add(new LogLineViewModel
                    {
                        LineNumber = _viewportStartLine + i + 1,
                        Text = lines[i],
                        HighlightColor = LineHighlighter.GetHighlightColor(_owner.CurrentSettings.HighlightRules, lines[i])
                    });
                }
            }

            if (_owner.IsShutdownOrDisposed)
                return;

            _owner.ApplyVisibleLines(nextVisibleLines);
            SetScrollPosition(_viewportStartLine);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _owner.StatusText = $"Read error: {ex.Message}";
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

        var lineIndexSnapshot = await _owner.GetLineIndexSnapshotAsync(ct);
        if (lineIndexSnapshot == null)
            return false;

        var appendedCount = updatedLineCount - previousTotalLines;
        var appendedLines = await _owner.ReadLinesOffUiAsync(
            lineIndexSnapshot,
            previousTotalLines,
            appendedCount,
            _owner.EffectiveEncoding,
            ct);

        if (appendedLines.Count <= 0)
            return false;

        var maxLines = Math.Max(1, _viewportLineCount);
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

        _viewportStartLine = Math.Max(0, updatedLineCount - maxLines);
        SetScrollPosition(_viewportStartLine);
        return true;
    }

    public async Task ScrollToLineAsync(int startLine, CancellationTokenSource? existingNavCts)
    {
        if (_viewportStartLine == startLine && _owner.VisibleLines.Count > 0)
            return;

        existingNavCts?.Cancel();
        existingNavCts?.Dispose();
        var navCts = new CancellationTokenSource();
        _owner.ReplaceNavigationCts(navCts);
        try
        {
            await LoadViewportAsync(startLine, _viewportLineCount, navCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _owner.SetNavigateTargetLine(_owner.VisibleLines.FirstOrDefault()?.LineNumber ?? (_filterSession.IsActive ? -1 : _viewportStartLine + 1));
    }

    public Task JumpToTopAsync(CancellationTokenSource? existingNavCts)
        => ScrollToLineAsync(0, existingNavCts);

    public Task JumpToBottomAsync(CancellationTokenSource? existingNavCts)
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
            await LoadViewportAsync(startLine, _viewportLineCount, ct);
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

        var previousBottomStart = Math.Max(0, previousDisplayCount - _viewportLineCount);
        var newBottomStart = Math.Max(0, filteredLines.Count - _viewportLineCount);

        if (_viewportStartLine < previousBottomStart)
            return false;

        var maxLines = Math.Max(1, _viewportLineCount);
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

    private void SetScrollPosition(int value)
    {
        _suppressScrollChange = true;
        _owner.ScrollPosition = value;
        _suppressScrollChange = false;
    }
}
