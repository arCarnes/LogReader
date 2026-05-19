namespace LogReader.Infrastructure.Services;

using System.Text;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class SearchService : ISearchService
{
    private const int BufferSize = 256 * 1024; // 256KB read buffer for search
    private const FileShare LogReadShare = FileShare.ReadWrite | FileShare.Delete;
    private readonly Func<string, SearchRequest, FileEncoding, CancellationToken, Task<SearchResult>>? _searchFileAsync;

    public SearchService()
    {
    }

    internal SearchService(Func<string, SearchRequest, FileEncoding, CancellationToken, Task<SearchResult>> searchFileAsync)
    {
        _searchFileAsync = searchFileAsync;
    }

    public async Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
    {
        var result = new SearchResult { FilePath = filePath };
        var isTimeOnlyFilterApply = IsTimeOnlyFilterApply(request);

        if (string.IsNullOrEmpty(request.Query) && !isTimeOnlyFilterApply)
            return result;

        if (!TimestampParser.TryBuildRange(request.FromTimestamp, request.ToTimestamp, out var timestampRange, out var rangeError))
        {
            result.Error = rangeError;
            return result;
        }

        try
        {
            var matcher = isTimeOnlyFilterApply ? null : CreateMatcher(request);
            var lineScope = GetLineScope(filePath, request);
            if (lineScope is { IsEmptyIncludeScope: true })
                return result;

            var enc = EncodingHelper.GetEncoding(encoding);

            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, LogReadShare, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var reader = new StreamReader(stream, enc, detectEncodingFromByteOrderMarks: false, bufferSize: BufferSize);

            long lineNumber = 0;
            string? line;

            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                ct.ThrowIfCancellationRequested();
                lineNumber++;

                if (request.StartLineNumber.HasValue && lineNumber < request.StartLineNumber.Value)
                    continue;

                if (request.EndLineNumber.HasValue && lineNumber > request.EndLineNumber.Value)
                    break;

                if (lineScope != null && !lineScope.Includes((int)lineNumber))
                    continue;

                if (timestampRange.HasBounds)
                {
                    if (!TimestampParser.TryParseFromLogLine(line, out var lineTimestamp))
                        continue;

                    result.HasParseableTimestamps = true;
                    if (!timestampRange.Contains(lineTimestamp))
                        continue;
                }

                if (isTimeOnlyFilterApply)
                    AddTimeOnlyFilterHit(result, request, lineNumber);
                else
                    AddMatchingHits(result, request, lineNumber, line, matcher!(line));

                if (result.HitLimitExceeded)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    public async Task<SearchResult> SearchFileRangeAsync(
        string filePath,
        SearchRequest request,
        FileEncoding encoding,
        Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readLinesAsync);

        var result = new SearchResult { FilePath = filePath };
        var isTimeOnlyFilterApply = IsTimeOnlyFilterApply(request);

        if (string.IsNullOrEmpty(request.Query) && !isTimeOnlyFilterApply)
            return result;

        if (!TimestampParser.TryBuildRange(request.FromTimestamp, request.ToTimestamp, out var timestampRange, out var rangeError))
        {
            result.Error = rangeError;
            return result;
        }

        if (!request.StartLineNumber.HasValue || !request.EndLineNumber.HasValue)
            return await SearchFileAsync(filePath, request, encoding, ct).ConfigureAwait(false);

        if (request.EndLineNumber.Value < request.StartLineNumber.Value)
            return result;

        try
        {
            var matcher = isTimeOnlyFilterApply ? null : CreateMatcher(request);
            var lineScope = GetLineScope(filePath, request);
            if (lineScope is { IsEmptyIncludeScope: true })
                return result;

            var startLineNumber = checked((int)Math.Max(1, request.StartLineNumber.Value));
            var endLineNumber = checked((int)Math.Max(0, request.EndLineNumber.Value));
            var lineCount = checked(endLineNumber - startLineNumber + 1);
            if (lineCount <= 0)
                return result;

            var lines = await readLinesAsync(startLineNumber - 1, lineCount, encoding, ct).ConfigureAwait(false);
            for (var offset = 0; offset < lines.Count; offset++)
            {
                ct.ThrowIfCancellationRequested();

                var lineNumber = startLineNumber + offset;
                if (lineScope != null && !lineScope.Includes(lineNumber))
                    continue;

                var line = lines[offset];
                if (timestampRange.HasBounds)
                {
                    if (!TimestampParser.TryParseFromLogLine(line, out var lineTimestamp))
                        continue;

                    result.HasParseableTimestamps = true;
                    if (!timestampRange.Contains(lineTimestamp))
                        continue;
                }

                if (isTimeOnlyFilterApply)
                    AddTimeOnlyFilterHit(result, request, lineNumber);
                else
                    AddMatchingHits(result, request, lineNumber, line, matcher!(line));

                if (result.HitLimitExceeded)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default)
    {
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            ToParallelismOperation(request.Usage),
            request.FilePaths);
        AdaptiveParallelismDiagnostics.WritePlan(plan);

        if (plan.TargetCount == 0)
            return Array.Empty<SearchResult>();

        var results = new SearchResult?[plan.TargetCount];
        var workOrder = AdaptiveParallelismScheduler.BuildInterleavedWorkOrder(plan);
        var nextIndex = -1;
        var workerCount = Math.Min(plan.GlobalLimit, plan.TargetCount);
        using var gates = AdaptiveParallelismGateSet.Create(plan);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => RunWorkerAsync())
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        return results
            .Select(result => result!)
            .ToArray();

        async Task RunWorkerAsync()
        {
            while (true)
            {
                var workOrderIndex = Interlocked.Increment(ref nextIndex);
                if (workOrderIndex >= workOrder.Count)
                    return;

                var targetIndex = workOrder[workOrderIndex];
                using (await gates.AcquireAsync(plan.Targets[targetIndex], ct).ConfigureAwait(false))
                {
                    results[targetIndex] = await SearchTargetAsync(targetIndex).ConfigureAwait(false);
                }
            }
        }

        async Task<SearchResult> SearchTargetAsync(int targetIndex)
        {
            var filePath = request.FilePaths[targetIndex];
            var encoding = fileEncodings.TryGetValue(filePath, out var enc) ? enc : FileEncoding.Utf8;
            var searchFileAsync = _searchFileAsync ?? SearchFileAsync;
            return await searchFileAsync(filePath, request, encoding, ct).ConfigureAwait(false);
        }
    }

    private static AdaptiveParallelismOperation ToParallelismOperation(SearchRequestUsage usage)
        => usage switch
        {
            SearchRequestUsage.FilterApply => AdaptiveParallelismOperation.FilterApply,
            _ => AdaptiveParallelismOperation.DiskSearch
        };

    private static bool IsTimeOnlyFilterApply(SearchRequest request)
        => request.Usage == SearchRequestUsage.FilterApply &&
           string.IsNullOrEmpty(request.Query) &&
           HasTimestampRange(request);

    private static bool HasTimestampRange(SearchRequest request)
        => !string.IsNullOrWhiteSpace(request.FromTimestamp) ||
           !string.IsNullOrWhiteSpace(request.ToTimestamp);

    private static Func<string, IEnumerable<(int start, int length)>> CreateMatcher(SearchRequest request)
    {
        if (request.IsRegex)
        {
            var regex = RegexPatternFactory.Create(request.Query, request.CaseSensitive);

            return line =>
            {
                var matches = regex.Matches(line);
                return matches.Select(m => (m.Index, m.Length));
            };
        }
        else
        {
            var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var query = request.Query;

            return line =>
            {
                var hits = new List<(int, int)>();
                int startIndex = 0;

                while (startIndex < line.Length)
                {
                    int idx = line.IndexOf(query, startIndex, comparison);
                    if (idx < 0) break;

                    hits.Add((idx, query.Length));

                    startIndex = idx + Math.Max(1, query.Length);
                }

                return hits;
            };
        }
    }

    private static LineScopeMatcher? GetLineScope(string filePath, SearchRequest request)
    {
        if (request.LineScopesByFilePath.TryGetValue(filePath, out var lineScope))
            return new LineScopeMatcher(lineScope.Mode, lineScope.LineNumbers);

        if (request.AllowedLineNumbersByFilePath.Count == 0)
            return null;

        if (!request.AllowedLineNumbersByFilePath.TryGetValue(filePath, out var allowedLines))
            return null;

        return new LineScopeMatcher(SearchLineScopeMode.IncludeOnly, allowedLines);
    }

    private static void AddMatchingHits(
        SearchResult result,
        SearchRequest request,
        long lineNumber,
        string line,
        IEnumerable<(int start, int length)> matches)
    {
        var matchList = matches.ToList();
        if (matchList.Count == 0)
            return;

        if (request.MaxHitsPerFile.HasValue && result.Hits.Count >= request.MaxHitsPerFile.Value)
        {
            result.HitLimitExceeded = true;
            return;
        }

        var (firstStart, firstLength) = matchList[0];
        if (request.Usage == SearchRequestUsage.FilterApply)
        {
            var originalMatches = matchList.Select(match => CreateOriginalMatchSpan(match.start, match.length)).ToList();
            result.Hits.Add(new SearchHit
            {
                LineNumber = lineNumber,
                LineText = string.Empty,
                MatchStart = firstStart,
                MatchLength = firstLength,
                OriginalMatchStart = firstStart,
                OriginalMatchLength = firstLength,
                Matches = originalMatches
            });
            return;
        }

        var retainedLine = RetainLineText(line, firstStart, firstLength, request.MaxRetainedLineTextLength);
        var retainedMatches = matchList
            .Select(match => CreateRetainedMatchSpan(match.start, match.length, retainedLine))
            .OfType<SearchMatchSpan>()
            .ToList();
        if (retainedMatches.Count == 0)
            retainedMatches.Add(CreateOriginalMatchSpan(firstStart, firstLength));

        var firstMatch = retainedMatches[0];
        result.Hits.Add(new SearchHit
        {
            LineNumber = lineNumber,
            LineText = retainedLine.Text,
            MatchStart = firstMatch.MatchStart,
            MatchLength = firstMatch.MatchLength,
            OriginalMatchStart = firstMatch.OriginalMatchStart,
            OriginalMatchLength = firstMatch.OriginalMatchLength,
            Matches = retainedMatches
        });
    }

    private static void AddTimeOnlyFilterHit(SearchResult result, SearchRequest request, long lineNumber)
    {
        if (request.MaxHitsPerFile.HasValue && result.Hits.Count >= request.MaxHitsPerFile.Value)
        {
            result.HitLimitExceeded = true;
            return;
        }

        result.Hits.Add(new SearchHit
        {
            LineNumber = lineNumber,
            LineText = string.Empty
        });
    }

    private static RetainedLineText RetainLineText(string line, int matchStart, int matchLength, int? maxRetainedLength)
    {
        if (!maxRetainedLength.HasValue || maxRetainedLength.Value <= 0 || line.Length <= maxRetainedLength.Value)
            return new RetainedLineText(line, 0, line.Length, 0);

        var maxLength = maxRetainedLength.Value;
        const string marker = "...";
        if (maxLength <= marker.Length * 2 + 1)
        {
            var start = Math.Min(Math.Max(0, matchStart), Math.Max(0, line.Length - maxLength));
            var end = Math.Min(line.Length, start + maxLength);
            return new RetainedLineText(line.Substring(start, end - start), start, end, 0);
        }

        var contentLength = Math.Max(1, maxLength - (marker.Length * 2));
        var contextBefore = Math.Max(0, (contentLength - matchLength) / 2);
        var windowStart = Math.Clamp(matchStart - contextBefore, 0, Math.Max(0, line.Length - contentLength));
        var windowEnd = Math.Min(line.Length, windowStart + contentLength);
        var hasPrefix = windowStart > 0;
        var hasSuffix = windowEnd < line.Length;

        var builder = new StringBuilder(maxLength);
        if (hasPrefix)
            builder.Append(marker);

        builder.Append(line, windowStart, windowEnd - windowStart);

        if (hasSuffix)
            builder.Append(marker);

        return new RetainedLineText(
            builder.ToString(),
            windowStart,
            windowEnd,
            hasPrefix ? marker.Length : 0);
    }

    private static SearchMatchSpan CreateOriginalMatchSpan(int start, int length)
        => new()
        {
            MatchStart = start,
            MatchLength = length,
            OriginalMatchStart = start,
            OriginalMatchLength = length
        };

    private static SearchMatchSpan? CreateRetainedMatchSpan(int start, int length, RetainedLineText retainedLine)
    {
        var end = start + length;
        var visibleStart = Math.Max(start, retainedLine.WindowStart);
        var visibleEnd = Math.Min(end, retainedLine.WindowEnd);
        if (visibleEnd <= visibleStart)
            return null;

        return new SearchMatchSpan
        {
            MatchStart = retainedLine.PrefixLength + visibleStart - retainedLine.WindowStart,
            MatchLength = visibleEnd - visibleStart,
            OriginalMatchStart = start,
            OriginalMatchLength = length
        };
    }

    private sealed record RetainedLineText(
        string Text,
        int WindowStart,
        int WindowEnd,
        int PrefixLength);

    private sealed class LineScopeMatcher
    {
        private readonly IReadOnlyList<int> _lineNumbers;
        private readonly HashSet<int>? _fallbackSet;
        private readonly SearchLineScopeMode _mode;

        public LineScopeMatcher(SearchLineScopeMode mode, IReadOnlyList<int> lineNumbers)
        {
            _mode = mode;
            _lineNumbers = lineNumbers;
            for (var i = 1; i < lineNumbers.Count; i++)
            {
                if (lineNumbers[i] >= lineNumbers[i - 1])
                    continue;

                _fallbackSet = lineNumbers
                    .Where(line => line > 0)
                    .ToHashSet();
                break;
            }
        }

        public bool IsEmptyIncludeScope => _mode == SearchLineScopeMode.IncludeOnly && _lineNumbers.Count == 0;

        public bool Includes(int lineNumber)
        {
            var contains = Contains(lineNumber);
            return _mode == SearchLineScopeMode.Exclude
                ? !contains
                : contains;
        }

        private bool Contains(int lineNumber)
        {
            if (lineNumber <= 0)
                return false;
            if (_fallbackSet != null)
                return _fallbackSet.Contains(lineNumber);

            if (_lineNumbers is List<int> list)
                return list.BinarySearch(lineNumber) >= 0;

            var low = 0;
            var high = _lineNumbers.Count - 1;
            while (low <= high)
            {
                var mid = low + ((high - low) / 2);
                var current = _lineNumbers[mid];
                if (current == lineNumber)
                    return true;

                if (current < lineNumber)
                    low = mid + 1;
                else
                    high = mid - 1;
            }

            return false;
        }
    }
}
