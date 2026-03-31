namespace LogReader.App.Services;

using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Models;

internal sealed class LogFilterSession
{
    private List<int>? _snapshotFilteredLineNumbers;
    private string? _activeFilterStatusText;
    private ActiveTailFilterState? _activeTailFilterState;

    public bool IsActive => _snapshotFilteredLineNumbers != null;

    public int FilteredLineCount => _snapshotFilteredLineNumbers?.Count ?? 0;

    public string? ActiveFilterStatusText => _activeFilterStatusText;

    public IReadOnlyList<int>? SnapshotFilteredLineNumbers => _snapshotFilteredLineNumbers;

    internal sealed class FilterSnapshot
    {
        public required IReadOnlyList<int> MatchingLineNumbers { get; init; }

        public string? StatusText { get; init; }

        public SearchRequest? FilterRequest { get; init; }

        public bool HasSeenParseableTimestamp { get; init; }
    }

    public void ApplyFilter(
        IReadOnlyList<int> matchingLineNumbers,
        string statusText,
        SearchRequest? filterRequest,
        bool hasParseableTimestamps,
        int totalLines)
    {
        _snapshotFilteredLineNumbers = matchingLineNumbers
            .Where(line => line > 0)
            .Distinct()
            .OrderBy(line => line)
            .ToList();
        _activeFilterStatusText = statusText;
        _activeTailFilterState = CreateTailFilterState(filterRequest, hasParseableTimestamps, totalLines);
    }

    internal FilterSnapshot? CaptureSnapshot()
    {
        if (_snapshotFilteredLineNumbers == null)
            return null;

        return new FilterSnapshot
        {
            MatchingLineNumbers = _snapshotFilteredLineNumbers.ToList(),
            StatusText = _activeFilterStatusText,
            FilterRequest = CloneSearchRequest(_activeTailFilterState?.SourceRequest),
            HasSeenParseableTimestamp = _activeTailFilterState?.HasSeenParseableTimestamp ?? false
        };
    }

    internal static FilterSnapshot CloneSnapshot(FilterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new FilterSnapshot
        {
            MatchingLineNumbers = snapshot.MatchingLineNumbers.ToList(),
            StatusText = snapshot.StatusText,
            FilterRequest = CloneSearchRequest(snapshot.FilterRequest),
            HasSeenParseableTimestamp = snapshot.HasSeenParseableTimestamp
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

        var canReuseStatusText = !string.IsNullOrWhiteSpace(snapshot.StatusText) &&
                                 _snapshotFilteredLineNumbers.Count == snapshot.MatchingLineNumbers.Count;
        _activeFilterStatusText = canReuseStatusText
            ? snapshot.StatusText
            : $"Filter active: {FilteredLineCount:N0} matching lines.";

        _activeTailFilterState = CreateTailFilterState(
            snapshot.FilterRequest,
            snapshot.HasSeenParseableTimestamp,
            totalLines);
        if (_activeTailFilterState != null)
            _activeTailFilterState.HasSeenParseableTimestamp = snapshot.HasSeenParseableTimestamp;
    }

    public void Clear()
    {
        _snapshotFilteredLineNumbers = null;
        _activeFilterStatusText = null;
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
        CancellationToken ct)
    {
        if (!IsActive || _activeTailFilterState == null || _snapshotFilteredLineNumbers == null)
            return FilterTailUpdateResult.NoChange(string.Empty, 0);

        if (updatedLineCount <= _activeTailFilterState.LastEvaluatedLine)
            return FilterTailUpdateResult.NoChange(_activeFilterStatusText ?? string.Empty, _snapshotFilteredLineNumbers.Count);

        var previousDisplayCount = _snapshotFilteredLineNumbers.Count;
        var firstUnprocessedLine = _activeTailFilterState.LastEvaluatedLine + 1;
        var readCount = Math.Max(0, updatedLineCount - _activeTailFilterState.LastEvaluatedLine);
        var appendedLines = await readLinesAsync(
            lineIndex,
            firstUnprocessedLine - 1,
            readCount,
            effectiveEncoding,
            ct);

        var addedMatchingLines = new List<FilterTailMatch>();
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
                addedMatchingLines.Add(new FilterTailMatch(lineNumber, lineText));
        }

        _activeTailFilterState.LastEvaluatedLine = updatedLineCount;

        _activeFilterStatusText = _activeTailFilterState.TimestampRange.HasBounds && !_activeTailFilterState.HasSeenParseableTimestamp
            ? "Filter active (tailing): no parseable timestamps found yet for the selected time range."
            : $"Filter active (tailing): {FilteredLineCount:N0} matching lines.";

        return new FilterTailUpdateResult(
            previousDisplayCount,
            _activeFilterStatusText,
            addedMatchingLines);
    }

    private static ActiveTailFilterState? CreateTailFilterState(
        SearchRequest? filterRequest,
        bool hasParseableTimestamps,
        int totalLines)
    {
        if (filterRequest == null || string.IsNullOrWhiteSpace(filterRequest.Query))
            return null;

        if (!TimestampParser.TryBuildRange(filterRequest.FromTimestamp, filterRequest.ToTimestamp, out var timestampRange, out _))
            return null;

        return new ActiveTailFilterState
        {
            Matcher = CreateLineMatcher(filterRequest),
            SourceRequest = CloneSearchRequest(filterRequest),
            TimestampRange = timestampRange,
            LastEvaluatedLine = totalLines,
            HasSeenParseableTimestamp = hasParseableTimestamps
        };
    }

    private static Func<string, bool> CreateLineMatcher(SearchRequest request)
    {
        if (request.IsRegex)
        {
            var regex = RegexPatternFactory.Create(request.Query, request.CaseSensitive, request.WholeWord);
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

                if (WholeWordMatcher.IsWholeWordMatch(line, idx, query.Length))
                    return true;

                startIndex = idx + Math.Max(1, query.Length);
            }

            return false;
        };
    }

    private static bool InsertSortedUnique(List<int> sortedLines, int lineNumber)
    {
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

        return new SearchRequest
        {
            Query = request.Query,
            IsRegex = request.IsRegex,
            CaseSensitive = request.CaseSensitive,
            WholeWord = request.WholeWord,
            FilePaths = request.FilePaths.ToList(),
            StartLineNumber = request.StartLineNumber,
            EndLineNumber = request.EndLineNumber,
            FromTimestamp = request.FromTimestamp,
            ToTimestamp = request.ToTimestamp
        };
    }

    internal sealed class FilterTailUpdateResult
    {
        public FilterTailUpdateResult(int previousDisplayCount, string statusText, IReadOnlyList<FilterTailMatch> addedMatchingLines)
        {
            PreviousDisplayCount = previousDisplayCount;
            StatusText = statusText;
            AddedMatchingLines = addedMatchingLines;
        }

        public int PreviousDisplayCount { get; }

        public string StatusText { get; }

        public IReadOnlyList<FilterTailMatch> AddedMatchingLines { get; }

        public bool HasChanges => AddedMatchingLines.Count > 0;

        public static FilterTailUpdateResult NoChange(string statusText, int previousDisplayCount)
            => new(previousDisplayCount, statusText, Array.Empty<FilterTailMatch>());
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
}
