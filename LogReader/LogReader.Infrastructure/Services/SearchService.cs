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

        if (string.IsNullOrEmpty(request.Query))
            return result;

        if (!TimestampParser.TryBuildRange(request.FromTimestamp, request.ToTimestamp, out var timestampRange, out var rangeError))
        {
            result.Error = rangeError;
            return result;
        }

        try
        {
            var matcher = CreateMatcher(request);
            var allowedLineNumbers = GetAllowedLineNumbers(filePath, request);
            if (allowedLineNumbers is { Count: 0 })
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

                if (allowedLineNumbers != null && !allowedLineNumbers.Contains((int)lineNumber))
                    continue;

                if (timestampRange.HasBounds)
                {
                    if (!TimestampParser.TryParseFromLogLine(line, out var lineTimestamp))
                        continue;

                    result.HasParseableTimestamps = true;
                    if (!timestampRange.Contains(lineTimestamp))
                        continue;
                }

                AddMatchingHits(result, request, lineNumber, line, matcher(line));
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

        if (string.IsNullOrEmpty(request.Query))
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
            var matcher = CreateMatcher(request);
            var allowedLineNumbers = GetAllowedLineNumbers(filePath, request);
            if (allowedLineNumbers is { Count: 0 })
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
                if (allowedLineNumbers != null && !allowedLineNumbers.Contains(lineNumber))
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

                AddMatchingHits(result, request, lineNumber, line, matcher(line));
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

    private static AllowedLineNumbers? GetAllowedLineNumbers(string filePath, SearchRequest request)
    {
        if (request.AllowedLineNumbersByFilePath.Count == 0)
            return null;

        if (!request.AllowedLineNumbersByFilePath.TryGetValue(filePath, out var allowedLines))
            return null;

        return new AllowedLineNumbers(allowedLines);
    }

    private static void AddMatchingHits(
        SearchResult result,
        SearchRequest request,
        long lineNumber,
        string line,
        IEnumerable<(int start, int length)> matches)
    {
        using var enumerator = matches.GetEnumerator();
        if (!enumerator.MoveNext())
            return;

        if (request.Usage == SearchRequestUsage.FilterApply)
        {
            var (start, length) = enumerator.Current;
            result.Hits.Add(new SearchHit
            {
                LineNumber = lineNumber,
                LineText = string.Empty,
                MatchStart = start,
                MatchLength = length
            });
            return;
        }

        do
        {
            if (request.MaxHitsPerFile.HasValue && result.Hits.Count >= request.MaxHitsPerFile.Value)
            {
                result.HitLimitExceeded = true;
                return;
            }

            var (start, length) = enumerator.Current;
            var retainedLine = RetainLineText(line, start, length, request.MaxRetainedLineTextLength, out var retainedMatchStart);
            result.Hits.Add(new SearchHit
            {
                LineNumber = lineNumber,
                LineText = retainedLine,
                MatchStart = retainedMatchStart,
                MatchLength = Math.Min(length, Math.Max(0, retainedLine.Length - retainedMatchStart)),
                OriginalMatchStart = start,
                OriginalMatchLength = length
            });
        }
        while (enumerator.MoveNext());
    }

    private static string RetainLineText(string line, int matchStart, int matchLength, int? maxRetainedLength, out int retainedMatchStart)
    {
        retainedMatchStart = matchStart;
        if (!maxRetainedLength.HasValue || maxRetainedLength.Value <= 0 || line.Length <= maxRetainedLength.Value)
            return line;

        var maxLength = maxRetainedLength.Value;
        const string marker = "...";
        if (maxLength <= marker.Length * 2 + 1)
        {
            var start = Math.Min(Math.Max(0, matchStart), Math.Max(0, line.Length - maxLength));
            retainedMatchStart = Math.Max(0, matchStart - start);
            return line.Substring(start, Math.Min(maxLength, line.Length - start));
        }

        var contentLength = Math.Max(1, maxLength - (marker.Length * 2));
        var contextBefore = Math.Max(0, (contentLength - matchLength) / 2);
        var windowStart = Math.Clamp(matchStart - contextBefore, 0, Math.Max(0, line.Length - contentLength));
        var windowEnd = Math.Min(line.Length, windowStart + contentLength);
        var hasPrefix = windowStart > 0;
        var hasSuffix = windowEnd < line.Length;
        retainedMatchStart = (hasPrefix ? marker.Length : 0) + Math.Max(0, matchStart - windowStart);

        var builder = new StringBuilder(maxLength);
        if (hasPrefix)
            builder.Append(marker);

        builder.Append(line, windowStart, windowEnd - windowStart);

        if (hasSuffix)
            builder.Append(marker);

        return builder.ToString();
    }

    private sealed class AllowedLineNumbers
    {
        private readonly IReadOnlyList<int> _lineNumbers;
        private readonly HashSet<int>? _fallbackSet;

        public AllowedLineNumbers(IReadOnlyList<int> lineNumbers)
        {
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

        public int Count => _lineNumbers.Count;

        public bool Contains(int lineNumber)
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
