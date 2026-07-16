namespace LogReader.App.Services;

using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Models;

internal sealed class LogFilterSession
{
    private const int TailFilterCatchUpChunkLineCount = 2_000;
    internal const string TailRegexTimeoutStatusText =
        "Filter paused: regex timed out while evaluating appended lines. Reapply or edit the filter to resume.";

    private readonly object _stateSync = new();
    private List<int>? _snapshotFilteredLineNumbers;
    private IReadOnlyList<int>? _viewportFilteredLineNumbersSnapshot;
    private string? _activeFilterStatusText;
    private SearchRequest? _activeFilterRequest;
    private ActiveTailFilterState? _activeTailFilterState;
    private bool _isTailEvaluationPaused;
    private FilterLineSetMode _lineSetMode;
    private int _totalLinesAtSnapshot;
    private FileScanGenerationEvidence _generationEvidence = FileScanGenerationEvidence.Unknown;
    private string? _correlatedTabInstanceId;
    private int _correlatedSearchContentVersion;
    private FileEncoding _evaluatedEncoding = FileEncoding.Auto;

    public bool IsActive
    {
        get
        {
            lock (_stateSync)
                return _snapshotFilteredLineNumbers != null;
        }
    }

    public int FilteredLineCount
    {
        get
        {
            lock (_stateSync)
                return _snapshotFilteredLineNumbers?.Count ?? 0;
        }
    }

    public int DisplayLineCount
    {
        get
        {
            lock (_stateSync)
            {
                return _snapshotFilteredLineNumbers == null
                    ? 0
                    : GetDisplayLineCount(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot);
            }
        }
    }

    public FilterLineSetMode LineSetMode
    {
        get
        {
            lock (_stateSync)
                return _lineSetMode;
        }
    }

    public string? ActiveFilterStatusText
    {
        get
        {
            lock (_stateSync)
                return _activeFilterStatusText;
        }
    }

    public IReadOnlyList<int>? SnapshotFilteredLineNumbers
    {
        get
        {
            lock (_stateSync)
                return _snapshotFilteredLineNumbers?.ToArray();
        }
    }

    internal IReadOnlyList<int>? ViewportFilteredLineNumbersSnapshot
    {
        get
        {
            lock (_stateSync)
                return _viewportFilteredLineNumbersSnapshot ??= _snapshotFilteredLineNumbers?.ToArray();
        }
    }

    internal sealed class FilterSnapshot
    {
        public required IReadOnlyList<int> MatchingLineNumbers { get; init; }

        public FilterLineSetMode LineSetMode { get; init; }

        public int TotalLinesAtSnapshot { get; init; }

        public string? StatusText { get; init; }

        public SearchRequest? FilterRequest { get; init; }

        public bool HasSeenParseableTimestamp { get; init; }

        public int? LastEvaluatedLine { get; init; }

        public bool IsTailEvaluationPaused { get; init; }

        public FileScanGenerationEvidence GenerationEvidence { get; init; } = FileScanGenerationEvidence.Unknown;

        public string? CorrelatedTabInstanceId { get; init; }

        public int CorrelatedSearchContentVersion { get; init; }

        public FileEncoding EvaluatedEncoding { get; init; } = FileEncoding.Auto;
    }

    public void ApplyFilter(
        IReadOnlyList<int> matchingLineNumbers,
        string statusText,
        SearchRequest? filterRequest,
        bool hasParseableTimestamps,
        int totalLines,
        FilterLineSetMode lineSetMode = FilterLineSetMode.IncludeMatching,
        FileScanGenerationEvidence generationEvidence = default,
        int? evaluatedThroughLine = null,
        string? correlatedTabInstanceId = null,
        int correlatedSearchContentVersion = 0,
        FileEncoding evaluatedEncoding = FileEncoding.Auto)
    {
        lock (_stateSync)
        {
            _snapshotFilteredLineNumbers = NormalizeAppliedLineNumbers(matchingLineNumbers);
            _lineSetMode = lineSetMode;
            _totalLinesAtSnapshot = Math.Max(0, totalLines);
            InvalidateViewportFilteredLineNumbersSnapshot();
            _activeFilterStatusText = statusText;
            _activeFilterRequest = CloneSearchRequest(filterRequest);
            _activeTailFilterState = CreateTailFilterState(
                filterRequest,
                hasParseableTimestamps,
                evaluatedThroughLine ?? totalLines);
            _isTailEvaluationPaused = false;
            _generationEvidence = generationEvidence;
            _correlatedTabInstanceId = correlatedTabInstanceId;
            _correlatedSearchContentVersion = correlatedSearchContentVersion;
            _evaluatedEncoding = evaluatedEncoding;
        }
    }

    internal FilterSnapshot? CaptureSnapshot()
    {
        lock (_stateSync)
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
                LastEvaluatedLine = _activeTailFilterState?.LastEvaluatedLine,
                IsTailEvaluationPaused = _isTailEvaluationPaused,
                GenerationEvidence = _generationEvidence,
                CorrelatedTabInstanceId = _correlatedTabInstanceId,
                CorrelatedSearchContentVersion = _correlatedSearchContentVersion,
                EvaluatedEncoding = _evaluatedEncoding
            };
        }
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
            LastEvaluatedLine = snapshot.LastEvaluatedLine,
            IsTailEvaluationPaused = snapshot.IsTailEvaluationPaused,
            GenerationEvidence = snapshot.GenerationEvidence,
            CorrelatedTabInstanceId = snapshot.CorrelatedTabInstanceId,
            CorrelatedSearchContentVersion = snapshot.CorrelatedSearchContentVersion,
            EvaluatedEncoding = snapshot.EvaluatedEncoding
        };
    }

    internal void RestoreSnapshot(FilterSnapshot snapshot, int totalLines)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_stateSync)
        {
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
            _isTailEvaluationPaused = snapshot.IsTailEvaluationPaused;
            _generationEvidence = snapshot.GenerationEvidence;
            _correlatedTabInstanceId = snapshot.CorrelatedTabInstanceId;
            _correlatedSearchContentVersion = snapshot.CorrelatedSearchContentVersion;
            _evaluatedEncoding = snapshot.EvaluatedEncoding;
            _activeFilterStatusText = _isTailEvaluationPaused
                ? TailRegexTimeoutStatusText
                : canReuseStatusText
                    ? snapshot.StatusText
                    : BuildStatusText(isTailing: false);
            _activeFilterRequest = CloneSearchRequest(snapshot.FilterRequest);

            _activeTailFilterState = CreateTailFilterState(
                snapshot.FilterRequest,
                snapshot.HasSeenParseableTimestamp,
                snapshot.LastEvaluatedLine ?? totalLines);
            if (_activeTailFilterState != null)
                _activeTailFilterState.HasSeenParseableTimestamp = snapshot.HasSeenParseableTimestamp;
        }
    }

    public void Clear()
    {
        lock (_stateSync)
        {
            _snapshotFilteredLineNumbers = null;
            _lineSetMode = FilterLineSetMode.IncludeMatching;
            _totalLinesAtSnapshot = 0;
            InvalidateViewportFilteredLineNumbersSnapshot();
            _activeFilterStatusText = null;
            _activeFilterRequest = null;
            _activeTailFilterState = null;
            _isTailEvaluationPaused = false;
            _generationEvidence = FileScanGenerationEvidence.Unknown;
            _correlatedTabInstanceId = null;
            _correlatedSearchContentVersion = 0;
            _evaluatedEncoding = FileEncoding.Auto;
        }
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
        ActiveTailFilterState tailState;
        FilterLineSetMode lineSetMode;
        int previousDisplayCount;
        int firstUnprocessedLine;
        bool hasSeenParseableTimestamp;
        lock (_stateSync)
        {
            if (_snapshotFilteredLineNumbers == null)
                return FilterTailUpdateResult.NoChange(string.Empty, 0, 0, isEvaluationPaused: false);

            previousDisplayCount = GetDisplayLineCount(
                _snapshotFilteredLineNumbers,
                _lineSetMode,
                _totalLinesAtSnapshot);
            if (_isTailEvaluationPaused || _activeTailFilterState == null)
                return FilterTailUpdateResult.NoChange(
                    _activeFilterStatusText ?? string.Empty,
                    previousDisplayCount,
                    _activeTailFilterState?.LastEvaluatedLine ?? 0,
                    _isTailEvaluationPaused);

            tailState = _activeTailFilterState;
            if (updatedLineCount <= tailState.LastEvaluatedLine)
                return FilterTailUpdateResult.NoChange(
                    _activeFilterStatusText ?? string.Empty,
                    previousDisplayCount,
                    tailState.LastEvaluatedLine,
                    isEvaluationPaused: false);

            lineSetMode = _lineSetMode;
            firstUnprocessedLine = tailState.LastEvaluatedLine + 1;
            hasSeenParseableTimestamp = tailState.HasSeenParseableTimestamp;
        }

        var retainedLimit = Math.Max(1, retainedDisplayLineLimit);
        var addedDisplayLines = new List<FilterTailMatch>();
        var matchingLineNumbersToInsert = new List<int>();
        var addedDisplayLineCount = 0;
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
                if (tailState.TimestampRange.HasBounds)
                {
                    if (!TimestampParser.TryParseFromLogLine(lineText, out var timestamp))
                    {
                        predicateMatches = false;
                    }
                    else
                    {
                        hasSeenParseableTimestamp = true;
                        predicateMatches = tailState.TimestampRange.Contains(timestamp);
                    }
                }

                if (predicateMatches)
                {
                    try
                    {
                        predicateMatches = tailState.Matcher(lineText);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        lock (_stateSync)
                        {
                            if (ReferenceEquals(_activeTailFilterState, tailState))
                            {
                                tailState.HasSeenParseableTimestamp = hasSeenParseableTimestamp;
                                _isTailEvaluationPaused = true;
                                _activeFilterStatusText = TailRegexTimeoutStatusText;
                            }

                            return FilterTailUpdateResult.NoChange(
                                _activeFilterStatusText ?? string.Empty,
                                _snapshotFilteredLineNumbers == null
                                    ? 0
                                    : GetDisplayLineCount(
                                        _snapshotFilteredLineNumbers,
                                        _lineSetMode,
                                        _totalLinesAtSnapshot),
                                tailState.LastEvaluatedLine,
                                isEvaluationPaused: true);
                        }
                    }
                }

                if (predicateMatches)
                    matchingLineNumbersToInsert.Add(lineNumber);

                if ((lineSetMode == FilterLineSetMode.IncludeMatching && predicateMatches) ||
                    (lineSetMode == FilterLineSetMode.ExcludeMatching && !predicateMatches))
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

        var evaluatedThroughLine = Math.Max(firstUnprocessedLine - 1, nextLine - 1);
        lock (_stateSync)
        {
            if (_snapshotFilteredLineNumbers == null || !ReferenceEquals(_activeTailFilterState, tailState))
            {
                return FilterTailUpdateResult.NoChange(
                    _activeFilterStatusText ?? string.Empty,
                    _snapshotFilteredLineNumbers == null
                        ? 0
                        : GetDisplayLineCount(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot),
                    _activeTailFilterState?.LastEvaluatedLine ?? 0,
                    _isTailEvaluationPaused);
            }

            var hasSnapshotChanged = false;
            foreach (var matchingLineNumber in matchingLineNumbersToInsert)
                hasSnapshotChanged |= InsertSortedUnique(_snapshotFilteredLineNumbers, matchingLineNumber);

            tailState.LastEvaluatedLine = evaluatedThroughLine;
            tailState.HasSeenParseableTimestamp = hasSeenParseableTimestamp;
            _totalLinesAtSnapshot = Math.Max(_totalLinesAtSnapshot, evaluatedThroughLine);

            if (hasSnapshotChanged || lineSetMode == FilterLineSetMode.ExcludeMatching)
                InvalidateViewportFilteredLineNumbersSnapshot();

            _activeFilterStatusText = lineSetMode == FilterLineSetMode.IncludeMatching &&
                                      tailState.TimestampRange.HasBounds &&
                                      !tailState.HasSeenParseableTimestamp
                ? "Filter active (tailing): no parseable timestamps found yet for the selected time range."
                : BuildStatusText(isTailing: true);

            return new FilterTailUpdateResult(
                previousDisplayCount,
                _activeFilterStatusText,
                addedDisplayLines,
                addedDisplayLineCount,
                evaluatedThroughLine,
                isEvaluationPaused: false);
        }
    }

    public int? GetDisplayLineNumberAt(int displayIndex)
    {
        lock (_stateSync)
        {
            if (_snapshotFilteredLineNumbers == null)
                return null;

            return GetDisplayLineNumberAt(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot, displayIndex);
        }
    }

    public int? GetDisplayIndexForLineNumber(int lineNumber)
    {
        lock (_stateSync)
        {
            if (_snapshotFilteredLineNumbers == null)
                return null;

            return GetDisplayIndexForLineNumber(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot, lineNumber);
        }
    }

    public int? GetFirstDisplayIndexAtOrAfterLineNumber(int lineNumber)
    {
        lock (_stateSync)
        {
            if (_snapshotFilteredLineNumbers == null)
                return null;

            return GetFirstDisplayIndexAtOrAfterLineNumber(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot, lineNumber);
        }
    }

    public IReadOnlyList<int> GetDisplayLineNumbers(int startDisplayIndex, int count)
    {
        lock (_stateSync)
        {
            if (_snapshotFilteredLineNumbers == null || count <= 0)
                return Array.Empty<int>();

            return GetDisplayLineNumbers(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot, startDisplayIndex, count);
        }
    }

    public bool IsLineVisible(int lineNumber)
    {
        lock (_stateSync)
        {
            return _snapshotFilteredLineNumbers != null &&
                   GetDisplayIndexForLineNumber(_snapshotFilteredLineNumbers, _lineSetMode, _totalLinesAtSnapshot, lineNumber) != null;
        }
    }

    public FilterDisplaySnapshot? CaptureDisplaySnapshot()
    {
        lock (_stateSync)
        {
            if (_snapshotFilteredLineNumbers == null)
                return null;

            return new FilterDisplaySnapshot(_snapshotFilteredLineNumbers.ToArray(), _lineSetMode, _totalLinesAtSnapshot);
        }
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

    private static List<int> NormalizeAppliedLineNumbers(IReadOnlyList<int> matchingLineNumbers)
    {
        var isSortedUniquePositive = true;
        for (var index = 0; index < matchingLineNumbers.Count; index++)
        {
            var lineNumber = matchingLineNumbers[index];
            if (lineNumber <= 0 || (index > 0 && lineNumber <= matchingLineNumbers[index - 1]))
            {
                isSortedUniquePositive = false;
                break;
            }
        }

        if (isSortedUniquePositive)
            return matchingLineNumbers.ToList();

        return matchingLineNumbers
            .Where(line => line > 0)
            .Distinct()
            .OrderBy(line => line)
            .ToList();
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
            int addedDisplayLineCount,
            int evaluatedThroughLine,
            bool isEvaluationPaused)
        {
            PreviousDisplayCount = previousDisplayCount;
            StatusText = statusText;
            AddedMatchingLines = addedMatchingLines;
            AddedDisplayLineCount = addedDisplayLineCount;
            EvaluatedThroughLine = evaluatedThroughLine;
            IsEvaluationPaused = isEvaluationPaused;
        }

        public int PreviousDisplayCount { get; }

        public string StatusText { get; }

        public IReadOnlyList<FilterTailMatch> AddedMatchingLines { get; }

        public int AddedDisplayLineCount { get; }

        public int EvaluatedThroughLine { get; }

        public bool IsEvaluationPaused { get; }

        public bool HasCompleteAddedMatchingLines => AddedMatchingLines.Count == AddedDisplayLineCount;

        public bool HasChanges => AddedDisplayLineCount > 0;

        public static FilterTailUpdateResult NoChange(
            string statusText,
            int previousDisplayCount,
            int evaluatedThroughLine,
            bool isEvaluationPaused)
            => new(
                previousDisplayCount,
                statusText,
                Array.Empty<FilterTailMatch>(),
                0,
                evaluatedThroughLine,
                isEvaluationPaused);
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
