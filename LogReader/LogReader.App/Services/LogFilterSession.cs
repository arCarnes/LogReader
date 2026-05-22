namespace LogReader.App.Services;

using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Models;

internal sealed class LogFilterSession
{
    private const int TailFilterCatchUpChunkLineCount = 2_000;

    private List<int>? _snapshotFilteredLineNumbers;
    private IReadOnlyList<int>? _viewportFilteredLineNumbersSnapshot;
    private string? _activeFilterStatusText;
    private SearchRequest? _activeFilterRequest;
    private ActiveTailFilterState? _activeTailFilterState;
    private FilterLineSetMode _lineSetMode;
    private int _totalLinesAtSnapshot;

    public bool IsActive => _snapshotFilteredLineNumbers != null;

    public int FilteredLineCount => _snapshotFilteredLineNumbers?.Count ?? 0;

    public int DisplayLineCount => _snapshotFilteredLineNumbers == null
        ? 0
        : GetDisplayLineCount(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot);

    public FilterLineSetMode LineSetMode => _lineSetMode;

    public string? ActiveFilterStatusText => _activeFilterStatusText;

    public IReadOnlyList<int>? SnapshotFilteredLineNumbers => _snapshotFilteredLineNumbers;

    internal IReadOnlyList<int>? ViewportFilteredLineNumbersSnapshot
        => _viewportFilteredLineNumbersSnapshot ??= _snapshotFilteredLineNumbers?.ToArray();

    internal sealed class FilterSnapshot
    {
        public required IReadOnlyList<int> MatchingLineNumbers { get; init; }

        public FilterLineSetMode LineSetMode { get; init; }

        public int TotalLinesAtSnapshot { get; init; }

        public string? StatusText { get; init; }

        public SearchRequest? FilterRequest { get; init; }

        public bool HasSeenParseableTimestamp { get; init; }

        public int LastEvaluatedLine { get; init; }
    }

    public void ApplyFilter(
        IReadOnlyList<int> matchingLineNumbers,
        string statusText,
        SearchRequest? filterRequest,
        bool hasParseableTimestamps,
        int totalLines,
        FilterLineSetMode lineSetMode = FilterLineSetMode.IncludeMatching)
    {
        _snapshotFilteredLineNumbers = matchingLineNumbers
            .Where(line => line > 0)
            .Distinct()
            .OrderBy(line => line)
            .ToList();
        _lineSetMode = lineSetMode;
        _totalLinesAtSnapshot = Math.Max(0, totalLines);
        InvalidateViewportFilteredLineNumbersSnapshot();
        _activeFilterStatusText = statusText;
        _activeFilterRequest = CloneSearchRequest(filterRequest);
        _activeTailFilterState = CreateTailFilterState(filterRequest, hasParseableTimestamps, totalLines);
    }

    internal FilterSnapshot? CaptureSnapshot()
    {
        if (_snapshotFilteredLineNumbers == null)
            return null;

        return new FilterSnapshot
        {
            MatchingLineNumbers = _snapshotFilteredLineNumbers.ToList(),
            LineSetMode = _lineSetMode,
            TotalLinesAtSnapshot = _totalLinesAtSnapshot,
            StatusText = _activeFilterStatusText,
            FilterRequest = CloneSearchRequest(_activeFilterRequest),
            HasSeenParseableTimestamp = _activeTailFilterState?.HasSeenParseableTimestamp ?? false,
            LastEvaluatedLine = _activeTailFilterState?.LastEvaluatedLine ?? 0
        };
    }

    internal static FilterSnapshot CloneSnapshot(FilterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new FilterSnapshot
        {
            MatchingLineNumbers = snapshot.MatchingLineNumbers.ToList(),
            LineSetMode = snapshot.LineSetMode,
            TotalLinesAtSnapshot = snapshot.TotalLinesAtSnapshot,
            StatusText = snapshot.StatusText,
            FilterRequest = CloneSearchRequest(snapshot.FilterRequest),
            HasSeenParseableTimestamp = snapshot.HasSeenParseableTimestamp,
            LastEvaluatedLine = snapshot.LastEvaluatedLine
        };
    }

    internal void RestoreSnapshot(FilterSnapshot snapshot, int totalLines)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshotFilteredLineNumbers = snapshot.MatchingLineNumbers
            .Where(line => line > 0 && line <= totalLines)
            .Distinct()
            .OrderBy(line => line)
            .ToList();
        _lineSetMode = snapshot.LineSetMode;
        _totalLinesAtSnapshot = snapshot.TotalLinesAtSnapshot > 0
            ? Math.Min(snapshot.TotalLinesAtSnapshot, Math.Max(0, totalLines))
            : Math.Max(0, totalLines);
        InvalidateViewportFilteredLineNumbersSnapshot();

        var canReuseStatusText = snapshot.LineSetMode == FilterLineSetMode.IncludeMatching &&
                                 !string.IsNullOrWhiteSpace(snapshot.StatusText) &&
                                 _snapshotFilteredLineNumbers.Count == snapshot.MatchingLineNumbers.Count;
        _activeFilterStatusText = canReuseStatusText
            ? snapshot.StatusText
            : BuildStatusText(isTailing: false);
        _activeFilterRequest = CloneSearchRequest(snapshot.FilterRequest);

        _activeTailFilterState = CreateTailFilterState(
            snapshot.FilterRequest,
            snapshot.HasSeenParseableTimestamp,
            snapshot.LastEvaluatedLine > 0 ? snapshot.LastEvaluatedLine : totalLines);
        if (_activeTailFilterState != null)
            _activeTailFilterState.HasSeenParseableTimestamp = snapshot.HasSeenParseableTimestamp;
    }

    public void Clear()
    {
        _snapshotFilteredLineNumbers = null;
        _lineSetMode = FilterLineSetMode.IncludeMatching;
        _totalLinesAtSnapshot = 0;
        InvalidateViewportFilteredLineNumbersSnapshot();
        _activeFilterStatusText = null;
        _activeFilterRequest = null;
        _activeTailFilterState = null;
    }

    public void ResetForRotation()
    {
        Clear();
    }

    public async Task<FilterTailUpdateResult> ProcessAppendedLinesAsync(
        int updatedLineCount,
        LineIndex lineIndex,
        FileEncoding effectiveEncoding,
        Func<LineIndex, int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
        int retainedDisplayLineLimit,
        CancellationToken ct)
    {
        if (!IsActive || _activeTailFilterState == null || _snapshotFilteredLineNumbers == null)
            return FilterTailUpdateResult.NoChange(string.Empty, 0);

        if (updatedLineCount <= _activeTailFilterState.LastEvaluatedLine)
            return FilterTailUpdateResult.NoChange(_activeFilterStatusText ?? string.Empty, DisplayLineCount);

        var previousDisplayCount = DisplayLineCount;
        var firstUnprocessedLine = _activeTailFilterState.LastEvaluatedLine + 1;
        var retainedLimit = Math.Max(1, retainedDisplayLineLimit);
        var addedDisplayLines = new List<FilterTailMatch>();
        var addedDisplayLineCount = 0;
        var hasSnapshotChanged = false;
        var nextLine = firstUnprocessedLine;
        while (nextLine <= updatedLineCount)
        {
            ct.ThrowIfCancellationRequested();

            var chunkReadCount = Math.Min(TailFilterCatchUpChunkLineCount, updatedLineCount - nextLine + 1);
            var appendedLines = await readLinesAsync(
                lineIndex,
                nextLine - 1,
                chunkReadCount,
                effectiveEncoding,
                ct);

            for (var offset = 0; offset < appendedLines.Count; offset++)
            {
                ct.ThrowIfCancellationRequested();

                var lineText = appendedLines[offset];
                var lineNumber = nextLine + offset;

                var predicateMatches = true;
                if (_activeTailFilterState.TimestampRange.HasBounds)
                {
                    if (!TimestampParser.TryParseFromLogLine(lineText, out var timestamp))
                    {
                        predicateMatches = false;
                    }
                    else
                    {
                        _activeTailFilterState.HasSeenParseableTimestamp = true;
                        predicateMatches = _activeTailFilterState.TimestampRange.Contains(timestamp);
                    }
                }

                if (predicateMatches)
                    predicateMatches = _activeTailFilterState.Matcher(lineText);

                if (predicateMatches && InsertSortedUnique(_snapshotFilteredLineNumbers, lineNumber))
                {
                    hasSnapshotChanged = true;
                }

                if ((_lineSetMode == FilterLineSetMode.IncludeMatching && predicateMatches) ||
                    (_lineSetMode == FilterLineSetMode.ExcludeMatching && !predicateMatches))
                {
                    addedDisplayLineCount++;
                    addedDisplayLines.Add(new FilterTailMatch(lineNumber, lineText));
                    if (addedDisplayLines.Count > retainedLimit)
                        addedDisplayLines.RemoveAt(0);
                }
            }

            nextLine += appendedLines.Count;
            if (appendedLines.Count < chunkReadCount)
                break;
        }

        _activeTailFilterState.LastEvaluatedLine = updatedLineCount;
        _totalLinesAtSnapshot = Math.Max(_totalLinesAtSnapshot, updatedLineCount);

        if (hasSnapshotChanged || _lineSetMode == FilterLineSetMode.ExcludeMatching)
            InvalidateViewportFilteredLineNumbersSnapshot();

        _activeFilterStatusText = _lineSetMode == FilterLineSetMode.IncludeMatching &&
                                  _activeTailFilterState.TimestampRange.HasBounds &&
                                  !_activeTailFilterState.HasSeenParseableTimestamp
            ? "Filter active (tailing): no parseable timestamps found yet for the selected time range."
            : BuildStatusText(isTailing: true);

        return new FilterTailUpdateResult(
            previousDisplayCount,
            _activeFilterStatusText,
            addedDisplayLines,
            addedDisplayLineCount);
    }

    public int? GetDisplayLineNumberAt(int displayIndex)
    {
        if (_snapshotFilteredLineNumbers == null)
            return null;

        return GetDisplayLineNumberAt(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot, displayIndex);
    }

    public int? GetDisplayIndexForLineNumber(int lineNumber)
    {
        if (_snapshotFilteredLineNumbers == null)
            return null;

        return GetDisplayIndexForLineNumber(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot, lineNumber);
    }

    public int? GetFirstDisplayIndexAtOrAfterLineNumber(int lineNumber)
    {
        if (_snapshotFilteredLineNumbers == null)
            return null;

        return GetFirstDisplayIndexAtOrAfterLineNumber(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot, lineNumber);
    }

    public IReadOnlyList<int> GetDisplayLineNumbers(int startDisplayIndex, int count)
    {
        if (_snapshotFilteredLineNumbers == null || count <= 0)
            return Array.Empty<int>();

        return GetDisplayLineNumbers(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot, startDisplayIndex, count);
    }

    public bool IsLineVisible(int lineNumber)
        => _snapshotFilteredLineNumbers != null &&
           GetDisplayIndexForLineNumber(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot, lineNumber) != null;

    public FilterDisplaySnapshot? CaptureDisplaySnapshot()
    {
        if (_snapshotFilteredLineNumbers == null)
            return null;

        return new FilterDisplaySnapshot(_snapshotFilteredLineNumbers.ToArray(), _lineSetMode, _totalLinesAtSnapshot);
    }

    private static ActiveTailFilterState? CreateTailFilterState(
        SearchRequest? filterRequest,
        bool hasParseableTimestamps,
        int initialLastEvaluatedLine)
    {
        if (filterRequest == null ||
            filterRequest.SourceMode == SearchRequestSourceMode.DiskSnapshot)
            return null;

        if (!TimestampParser.TryBuildRange(filterRequest.FromTimestamp, filterRequest.ToTimestamp, out var timestampRange, out _))
            return null;

        var hasQuery = !string.IsNullOrWhiteSpace(filterRequest.Query);
        if (!hasQuery && !timestampRange.HasBounds)
            return null;

        return new ActiveTailFilterState
        {
            Matcher = hasQuery ? CreateLineMatcher(filterRequest) : _ => true,
            SourceRequest = CloneSearchRequest(filterRequest),
            TimestampRange = timestampRange,
            LastEvaluatedLine = Math.Max(0, initialLastEvaluatedLine),
            HasSeenParseableTimestamp = hasParseableTimestamps
        };
    }

    private static Func<string, bool> CreateLineMatcher(SearchRequest request)
    {
        if (request.IsRegex)
        {
            var regex = RegexPatternFactory.Create(request.Query, request.CaseSensitive);
            return line => regex.IsMatch(line);
        }

        var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var query = request.Query;
        return line =>
        {
            if (string.IsNullOrEmpty(query))
                return false;

            return line.Contains(query, comparison);
        };
    }

    private static bool InsertSortedUnique(List<int> sortedLines, int lineNumber)
    {
        if (sortedLines.Count == 0)
        {
            sortedLines.Add(lineNumber);
            return true;
        }

        var lastLineNumber = sortedLines[^1];
        if (lineNumber > lastLineNumber)
        {
            sortedLines.Add(lineNumber);
            return true;
        }

        if (lineNumber == lastLineNumber)
            return false;

        var index = sortedLines.BinarySearch(lineNumber);
        if (index >= 0)
            return false;

        sortedLines.Insert(~index, lineNumber);
        return true;
    }

    private static SearchRequest? CloneSearchRequest(SearchRequest? request)
    {
        if (request == null)
            return null;

        return request.Clone();
    }

    private void InvalidateViewportFilteredLineNumbersSnapshot()
        => _viewportFilteredLineNumbersSnapshot = null;

    private string BuildStatusText(bool isTailing)
    {
        var prefix = isTailing ? "Filter active (tailing)" : "Filter active";
        return $"{prefix}: {DisplayLineCount:N0} matching lines.";
    }

    private static int GetDisplayLineCount(IReadOnlyList<int> matchingLines, FilterLineSetMode mode, int totalLines)
        => mode == FilterLineSetMode.ExcludeMatching
            ? Math.Max(0, totalLines - CountLinesLessThanOrEqual(matchingLines, totalLines))
            : matchingLines.Count;

    private static int? GetDisplayLineNumberAt(
        IReadOnlyList<int> matchingLines,
        FilterLineSetMode mode,
        int totalLines,
        int displayIndex)
    {
        var displayCount = GetDisplayLineCount(matchingLines, mode, totalLines);
        if (displayIndex < 0 || displayIndex >= displayCount)
            return null;

        if (mode == FilterLineSetMode.IncludeMatching)
            return matchingLines[displayIndex];

        var targetVisibleCount = displayIndex + 1;
        var low = 1;
        var high = totalLines;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            var visibleThroughMid = mid - CountLinesLessThanOrEqual(matchingLines, mid);
            if (visibleThroughMid >= targetVisibleCount)
                high = mid;
            else
                low = mid + 1;
        }

        return low;
    }

    private static int? GetDisplayIndexForLineNumber(
        IReadOnlyList<int> matchingLines,
        FilterLineSetMode mode,
        int totalLines,
        int lineNumber)
    {
        if (lineNumber <= 0 || lineNumber > totalLines)
            return null;

        var matchIndex = BinarySearch(matchingLines, lineNumber);
        if (mode == FilterLineSetMode.IncludeMatching)
            return matchIndex >= 0 ? matchIndex : null;

        if (matchIndex >= 0)
            return null;

        return lineNumber - 1 - CountLinesLessThanOrEqual(matchingLines, lineNumber);
    }

    private static int? GetFirstDisplayIndexAtOrAfterLineNumber(
        IReadOnlyList<int> matchingLines,
        FilterLineSetMode mode,
        int totalLines,
        int lineNumber)
    {
        if (GetDisplayLineCount(matchingLines, mode, totalLines) == 0)
            return null;

        if (mode == FilterLineSetMode.IncludeMatching)
        {
            var matchIndex = BinarySearch(matchingLines, lineNumber);
            if (matchIndex >= 0)
                return matchIndex;

            var nextMatchIndex = ~matchIndex;
            return nextMatchIndex < matchingLines.Count
                ? nextMatchIndex
                : null;
        }

        var candidateLine = Math.Max(1, lineNumber);
        if (candidateLine > totalLines)
            return null;

        var matchingIndex = CountLinesLessThanOrEqual(matchingLines, candidateLine - 1);
        if (matchingIndex < matchingLines.Count && matchingLines[matchingIndex] == candidateLine)
        {
            do
            {
                candidateLine++;
                matchingIndex++;
            }
            while (candidateLine <= totalLines &&
                   matchingIndex < matchingLines.Count &&
                   matchingLines[matchingIndex] == candidateLine);
        }

        return candidateLine <= totalLines
            ? GetDisplayIndexForLineNumber(matchingLines, mode, totalLines, candidateLine)
            : null;
    }

    private static IReadOnlyList<int> GetDisplayLineNumbers(
        IReadOnlyList<int> matchingLines,
        FilterLineSetMode mode,
        int totalLines,
        int startDisplayIndex,
        int count)
    {
        var displayCount = GetDisplayLineCount(matchingLines, mode, totalLines);
        if (startDisplayIndex < 0 || startDisplayIndex >= displayCount || count <= 0)
            return Array.Empty<int>();

        var take = Math.Min(count, displayCount - startDisplayIndex);
        var lines = new List<int>(take);
        if (mode == FilterLineSetMode.IncludeMatching)
        {
            for (var i = 0; i < take; i++)
                lines.Add(matchingLines[startDisplayIndex + i]);
            return lines;
        }

        var currentLine = GetDisplayLineNumberAt(matchingLines, mode, totalLines, startDisplayIndex)!.Value;
        var matchingIndex = CountLinesLessThanOrEqual(matchingLines, currentLine - 1);
        while (lines.Count < take && currentLine <= totalLines)
        {
            if (matchingIndex < matchingLines.Count && matchingLines[matchingIndex] == currentLine)
            {
                do
                {
                    currentLine++;
                    matchingIndex++;
                }
                while (currentLine <= totalLines &&
                       matchingIndex < matchingLines.Count &&
                       matchingLines[matchingIndex] == currentLine);
                continue;
            }

            var visibleEndLine = matchingIndex < matchingLines.Count
                ? Math.Min(totalLines, matchingLines[matchingIndex] - 1)
                : totalLines;
            var batchCount = Math.Min(take - lines.Count, visibleEndLine - currentLine + 1);
            for (var i = 0; i < batchCount; i++)
                lines.Add(currentLine + i);

            currentLine += batchCount;
        }

        return lines;
    }

    private static int CountLinesLessThanOrEqual(IReadOnlyList<int> sortedLines, int lineNumber)
    {
        var low = 0;
        var high = sortedLines.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (sortedLines[mid] <= lineNumber)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    private static int BinarySearch(IReadOnlyList<int> sortedLines, int lineNumber)
    {
        if (sortedLines is List<int> list)
            return list.BinarySearch(lineNumber);

        var low = 0;
        var high = sortedLines.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var current = sortedLines[mid];
            if (current == lineNumber)
                return mid;
            if (current < lineNumber)
                low = mid + 1;
            else
                high = mid - 1;
        }

        return ~low;
    }

    internal sealed class FilterTailUpdateResult
    {
        public FilterTailUpdateResult(
            int previousDisplayCount,
            string statusText,
            IReadOnlyList<FilterTailMatch> addedMatchingLines,
            int addedDisplayLineCount)
        {
            PreviousDisplayCount = previousDisplayCount;
            StatusText = statusText;
            AddedMatchingLines = addedMatchingLines;
            AddedDisplayLineCount = addedDisplayLineCount;
        }

        public int PreviousDisplayCount { get; }

        public string StatusText { get; }

        public IReadOnlyList<FilterTailMatch> AddedMatchingLines { get; }

        public int AddedDisplayLineCount { get; }

        public bool HasCompleteAddedMatchingLines => AddedMatchingLines.Count == AddedDisplayLineCount;

        public bool HasChanges => AddedDisplayLineCount > 0;

        public static FilterTailUpdateResult NoChange(string statusText, int previousDisplayCount)
            => new(previousDisplayCount, statusText, Array.Empty<FilterTailMatch>(), 0);
    }

    internal sealed class FilterTailMatch
    {
        public FilterTailMatch(int lineNumber, string lineText)
        {
            LineNumber = lineNumber;
            LineText = lineText;
        }

        public int LineNumber { get; }

        public string LineText { get; }
    }

    private sealed class ActiveTailFilterState
    {
        public Func<string, bool> Matcher { get; init; } = _ => false;

        public SearchRequest? SourceRequest { get; init; }

        public TimestampRange TimestampRange { get; init; }

        public int LastEvaluatedLine { get; set; }

        public bool HasSeenParseableTimestamp { get; set; }
    }

    internal sealed class FilterDisplaySnapshot
    {
        private readonly IReadOnlyList<int> _matchingLineNumbers;
        private readonly FilterLineSetMode _lineSetMode;
        private readonly int _totalLines;

        public FilterDisplaySnapshot(IReadOnlyList<int> matchingLineNumbers, FilterLineSetMode lineSetMode, int totalLines)
        {
            _matchingLineNumbers = matchingLineNumbers;
            _lineSetMode = lineSetMode;
            _totalLines = totalLines;
        }

        public int DisplayLineCount => GetDisplayLineCount(_matchingLineNumbers, _lineSetMode, _totalLines);

        public IReadOnlyList<int> GetDisplayLineNumbers(int startDisplayIndex, int count)
            => LogFilterSession.GetDisplayLineNumbers(_matchingLineNumbers, _lineSetMode, _totalLines, startDisplayIndex, count);
    }
}

public enum FilterLineSetMode
{
    IncludeMatching,
    ExcludeMatching
}
