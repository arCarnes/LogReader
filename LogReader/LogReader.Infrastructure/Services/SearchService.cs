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
    private const int GenerationStabilityAttemptCount = 2;
    internal const int MatcherSessionCapacity = 128;
    private readonly Func<string, SearchRequest, FileEncoding, CancellationToken, Task<SearchResult>>? _searchFileAsync;
    private readonly Func<string, bool, Regex> _regexFactory;
    private readonly Func<FileStream, FileGenerationToken> _generationTokenProvider;
    private readonly object _matcherSessionsGate = new();
    private readonly Dictionary<CancellationToken, MatcherSessionEntry> _matcherSessions = new();
    private readonly LinkedList<CancellationToken> _matcherSessionOrder = new();

    internal int MatcherSessionCount
    {
        get
        {
            lock (_matcherSessionsGate)
                return _matcherSessions.Count;
        }
    }

    public SearchService()
    {
        _regexFactory = RegexPatternFactory.Create;
        _generationTokenProvider = FileGenerationTokenProvider.Capture;
    }

    internal SearchService(Func<string, SearchRequest, FileEncoding, CancellationToken, Task<SearchResult>> searchFileAsync)
    {
        _searchFileAsync = searchFileAsync;
        _regexFactory = RegexPatternFactory.Create;
        _generationTokenProvider = FileGenerationTokenProvider.Capture;
    }

    internal SearchService(
        Func<string, bool, Regex> regexFactory,
        Func<FileStream, FileGenerationToken>? generationTokenProvider = null)
    {
        _regexFactory = regexFactory;
        _generationTokenProvider = generationTokenProvider ?? FileGenerationTokenProvider.Capture;
    }

    public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
        => SearchFileAsync(filePath, request, encoding, preparedMatcher: null, ct);

    public Task<FilterResult> FilterFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
        => Task.Run(() => FilterFileCoreAsync(filePath, request, encoding, preparedMatcher: null, ct));

    private async Task<FilterResult> FilterFileCoreAsync(
        string filePath,
        SearchRequest request,
        FileEncoding encoding,
        PreparedFilterMatcher? preparedMatcher,
        CancellationToken ct)
    {
        var result = new FilterResult { FilePath = filePath };
        var isTimeOnlyFilterApply = IsTimeOnlyFilterApply(request);

        if (string.IsNullOrEmpty(request.Query) && !isTimeOnlyFilterApply)
            return result;

        if (!TimestampParser.TryBuildRange(request.FromTimestamp, request.ToTimestamp, out var timestampRange, out var rangeError))
        {
            result.Error = rangeError;
            return result;
        }

        PreparedFilterMatcher? matcher = null;
        LineScopeMatcher? lineScope = null;
        try
        {
            matcher = isTimeOnlyFilterApply ? null : preparedMatcher ?? PrepareFilterMatcher(request);
            if (matcher?.Error is { } matcherError)
            {
                result.Error = matcherError;
                return result;
            }

            lineScope = GetLineScope(filePath, request);
            if (lineScope is { IsEmptyIncludeScope: true })
                return result;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(result.Error) || ct.IsCancellationRequested)
            return result;

        for (var attempt = 0; attempt < GenerationStabilityAttemptCount; attempt++)
        {
            result = new FilterResult { FilePath = filePath };
            try
            {
                var isUnstable = await ScanFilterFileAsync(
                    result,
                    filePath,
                    request,
                    encoding,
                    timestampRange,
                    lineScope,
                    matcher,
                    isTimeOnlyFilterApply,
                    ct).ConfigureAwait(false);
                if (!isUnstable)
                    return result;
            }
            catch (OperationCanceledException)
            {
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        return new FilterResult
        {
            FilePath = filePath,
            Error = "The file changed repeatedly while it was being filtered."
        };
    }

    private async Task<bool> ScanFilterFileAsync(
        FilterResult result,
        string filePath,
        SearchRequest request,
        FileEncoding encoding,
        TimestampRange timestampRange,
        LineScopeMatcher? lineScope,
        PreparedFilterMatcher? matcher,
        bool isTimeOnlyFilterApply,
        CancellationToken ct)
    {
        var enc = EncodingHelper.GetEncoding(encoding);
        await using var stream = OpenSearchStream(filePath);
        var snapshotLength = GetSnapshotLength(stream.Length, encoding);
        var initialSnapshot = CaptureHandleSnapshot(stream) with { Length = snapshotLength };
        using var snapshotStream = new SnapshotReadStream(stream, snapshotLength);
        using var reader = new StreamReader(snapshotStream, enc, detectEncodingFromByteOrderMarks: false, bufferSize: BufferSize);

        var lineNumber = 0;
        var evaluatedThroughLine = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            lineNumber++;

            if (request.EndLineNumber.HasValue && lineNumber > request.EndLineNumber.Value)
                break;

            evaluatedThroughLine = lineNumber;
            if (request.StartLineNumber.HasValue && lineNumber < request.StartLineNumber.Value)
                continue;
            if (lineScope != null && !lineScope.Includes(lineNumber))
                continue;

            if (timestampRange.HasBounds)
            {
                if (!TimestampParser.TryParseFromLogLine(line, out var lineTimestamp))
                    continue;

                result.HasParseableTimestamps = true;
                if (!timestampRange.Contains(lineTimestamp))
                    continue;
            }

            if (!isTimeOnlyFilterApply && !matcher!.IsMatch(line))
                continue;

            if (request.MaxHitsPerFile.HasValue && result.MatchingLineNumbers.Count >= request.MaxHitsPerFile.Value)
            {
                result.HitLimitExceeded = true;
                break;
            }

            result.MatchingLineNumbers.Add(lineNumber);
        }

        result.EvaluatedThroughLine = evaluatedThroughLine;
        var finalSnapshot = CaptureHandleSnapshot(stream);
        if (IsUnstableScan(initialSnapshot, finalSnapshot))
            return true;

        result.GenerationEvidence = AccountForTimestampOnlyScanDrift(
            CorrelateWithCurrentPath(
                filePath,
                ResolveStableSnapshot(initialSnapshot, finalSnapshot)),
            initialSnapshot,
            finalSnapshot);
        return false;
    }

    private PreparedFilterMatcher PrepareFilterMatcher(SearchRequest request)
    {
        try
        {
            if (request.IsRegex)
            {
                var regex = _regexFactory(request.Query, request.CaseSensitive);
                return new PreparedFilterMatcher(regex.IsMatch, error: null);
            }

            var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var query = request.Query;
            return new PreparedFilterMatcher(
                line => line.IndexOf(query, comparison) >= 0,
                error: null);
        }
        catch (Exception ex)
        {
            return new PreparedFilterMatcher(isMatch: null, ex.Message);
        }
    }

    private async Task<SearchResult> SearchFileAsync(
        string filePath,
        SearchRequest request,
        FileEncoding encoding,
        PreparedMatcher? preparedMatcher,
        CancellationToken ct)
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

        PreparedMatcher? matcher = null;
        LineScopeMatcher? lineScope = null;
        try
        {
            matcher = isTimeOnlyFilterApply ? null : preparedMatcher ?? GetPreparedMatcher(request, ct);
            if (matcher?.Error is { } matcherError)
            {
                result.Error = matcherError;
                return result;
            }

            lineScope = GetLineScope(filePath, request);
            if (lineScope is { IsEmptyIncludeScope: true })
                return result;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(result.Error) || ct.IsCancellationRequested)
            return result;

        for (var attempt = 0; attempt < GenerationStabilityAttemptCount; attempt++)
        {
            result = new SearchResult { FilePath = filePath };
            try
            {
                var isUnstable = await ScanSearchFileAsync(
                    result,
                    filePath,
                    request,
                    encoding,
                    timestampRange,
                    lineScope,
                    matcher,
                    isTimeOnlyFilterApply,
                    ct).ConfigureAwait(false);
                if (!isUnstable)
                    return result;
            }
            catch (OperationCanceledException)
            {
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        return new SearchResult
        {
            FilePath = filePath,
            Error = "The file changed repeatedly while it was being searched."
        };
    }

    public Task<SearchResult> SearchFileRangeAsync(
        string filePath,
        SearchRequest request,
        FileEncoding encoding,
        Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
        CancellationToken ct = default)
        => SearchFileRangeAsync(
            filePath,
            request,
            encoding,
            readLinesAsync,
            preparedMatcher: null,
            ct);

    private async Task<SearchResult> SearchFileRangeAsync(
        string filePath,
        SearchRequest request,
        FileEncoding encoding,
        Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
        PreparedMatcher? preparedMatcher,
        CancellationToken ct)
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

        try
        {
            var matcher = isTimeOnlyFilterApply ? null : preparedMatcher ?? GetPreparedMatcher(request, ct);
            if (matcher?.Error is { } matcherError)
            {
                result.Error = matcherError;
                return result;
            }

            if (!request.StartLineNumber.HasValue || !request.EndLineNumber.HasValue)
                return await SearchFileAsync(filePath, request, encoding, matcher, ct).ConfigureAwait(false);

            if (request.EndLineNumber.Value < request.StartLineNumber.Value)
                return result;

            var lineScope = GetLineScope(filePath, request);

            var startLineNumber = checked((int)Math.Max(1, request.StartLineNumber.Value));
            var endLineNumber = checked((int)Math.Max(0, request.EndLineNumber.Value));
            var lineCount = checked(endLineNumber - startLineNumber + 1);
            if (lineCount <= 0)
                return result;

            if (lineScope is { IsEmptyIncludeScope: true })
            {
                result.EvaluatedThroughLine = endLineNumber;
                return result;
            }

            var lines = await readLinesAsync(startLineNumber - 1, lineCount, encoding, ct).ConfigureAwait(false);
            var evaluatedThroughLine = (long)startLineNumber - 1;
            var returnedLineCount = Math.Min(lines.Count, lineCount);
            for (var offset = 0; offset < returnedLineCount; offset++)
            {
                ct.ThrowIfCancellationRequested();

                var lineNumber = startLineNumber + offset;
                evaluatedThroughLine = lineNumber;
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
                    AddMatchingHits(result, request, lineNumber, line, matcher!.GetMatches(line));

                if (result.HitLimitExceeded)
                    break;
            }

            result.EvaluatedThroughLine = evaluatedThroughLine;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private async Task<bool> ScanSearchFileAsync(
        SearchResult result,
        string filePath,
        SearchRequest request,
        FileEncoding encoding,
        TimestampRange timestampRange,
        LineScopeMatcher? lineScope,
        PreparedMatcher? matcher,
        bool isTimeOnlyFilterApply,
        CancellationToken ct)
    {
        var enc = EncodingHelper.GetEncoding(encoding);
        await using var stream = OpenSearchStream(filePath);
        var snapshotLength = GetSnapshotLength(stream.Length, encoding);
        var initialSnapshot = CaptureHandleSnapshot(stream) with { Length = snapshotLength };
        using var snapshotStream = new SnapshotReadStream(stream, snapshotLength);
        using var reader = new StreamReader(snapshotStream, enc, detectEncodingFromByteOrderMarks: false, bufferSize: BufferSize);

        long lineNumber = 0;
        long evaluatedThroughLine = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            lineNumber++;

            if (request.EndLineNumber.HasValue && lineNumber > request.EndLineNumber.Value)
                break;

            evaluatedThroughLine = lineNumber;
            if (request.StartLineNumber.HasValue && lineNumber < request.StartLineNumber.Value)
                continue;
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
                AddMatchingHits(result, request, lineNumber, line, matcher!.GetMatches(line));

            if (result.HitLimitExceeded)
                break;
        }

        result.EvaluatedThroughLine = evaluatedThroughLine;

        var finalSnapshot = CaptureHandleSnapshot(stream);
        if (IsUnstableScan(initialSnapshot, finalSnapshot))
            return true;

        result.GenerationEvidence = AccountForTimestampOnlyScanDrift(
            CorrelateWithCurrentPath(
                filePath,
                ResolveStableSnapshot(initialSnapshot, finalSnapshot)),
            initialSnapshot,
            finalSnapshot);
        return false;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchFilesAsync(
        SearchRequest request,
        IDictionary<string, FileEncoding> fileEncodings,
        CancellationToken ct = default)
    {
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            ToParallelismOperation(request.Usage),
            request.FilePaths);
        AdaptiveParallelismDiagnostics.WritePlan(plan);

        if (plan.TargetCount == 0)
            return Array.Empty<SearchResult>();

        var results = new SearchResult?[plan.TargetCount];
        var preparedMatcher = _searchFileAsync == null && !IsTimeOnlyFilterApply(request)
            ? PrepareMatcher(request)
            : null;
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
            if (_searchFileAsync != null)
                return await _searchFileAsync(filePath, request, encoding, ct).ConfigureAwait(false);

            return await SearchFileAsync(filePath, request, encoding, preparedMatcher, ct).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyList<FilterResult>> FilterFilesAsync(
        SearchRequest request,
        IDictionary<string, FileEncoding> fileEncodings,
        CancellationToken ct = default)
        => Task.Run(() => FilterFilesCoreAsync(request, fileEncodings, ct));

    private async Task<IReadOnlyList<FilterResult>> FilterFilesCoreAsync(
        SearchRequest request,
        IDictionary<string, FileEncoding> fileEncodings,
        CancellationToken ct)
    {
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.FilterApply,
            request.FilePaths);
        AdaptiveParallelismDiagnostics.WritePlan(plan);

        if (plan.TargetCount == 0)
            return Array.Empty<FilterResult>();

        ct.ThrowIfCancellationRequested();

        var results = new FilterResult?[plan.TargetCount];
        var preparedMatcher = !IsTimeOnlyFilterApply(request)
            ? PrepareFilterMatcher(request)
            : null;
        var workOrder = AdaptiveParallelismScheduler.BuildInterleavedWorkOrder(plan);
        var nextIndex = -1;
        var workerCount = Math.Min(plan.GlobalLimit, plan.TargetCount);
        using var gates = AdaptiveParallelismGateSet.Create(plan);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => RunWorkerAsync())
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        return results.Select(result => result!).ToArray();

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
                    var filePath = request.FilePaths[targetIndex];
                    var encoding = fileEncodings.TryGetValue(filePath, out var enc) ? enc : FileEncoding.Utf8;
                    results[targetIndex] = await FilterFileCoreAsync(
                        filePath,
                        request,
                        encoding,
                        preparedMatcher,
                        ct).ConfigureAwait(false);
                }
            }
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

    private PreparedMatcher GetPreparedMatcher(SearchRequest request, CancellationToken ct)
    {
        if (!request.IsRegex || !ct.CanBeCanceled || ct.IsCancellationRequested)
            return PrepareMatcher(request);

        var signature = new MatcherSignature(request.Query, request.IsRegex, request.CaseSensitive);
        MatcherSessionEntry? replacedEntry = null;
        MatcherSessionEntry? evictedEntry = null;
        PreparedMatcher matcher;
        lock (_matcherSessionsGate)
        {
            if (_matcherSessions.TryGetValue(ct, out var existing) && existing.Signature == signature)
                return existing.Matcher;

            if (existing != null)
            {
                RemoveMatcherSessionLocked(ct, existing);
                replacedEntry = existing;
            }

            var entry = new MatcherSessionEntry(signature, PrepareMatcher(request));
            entry.OrderNode = _matcherSessionOrder.AddLast(ct);
            _matcherSessions[ct] = entry;
            entry.CancellationRegistration = ct.Register(() => RemoveMatcherSession(ct, entry));
            matcher = entry.Matcher;

            if (_matcherSessions.Count > MatcherSessionCapacity &&
                _matcherSessionOrder.First is { } oldestNode &&
                _matcherSessions.TryGetValue(oldestNode.Value, out var oldestEntry))
            {
                RemoveMatcherSessionLocked(oldestNode.Value, oldestEntry);
                evictedEntry = oldestEntry;
            }
        }

        replacedEntry?.CancellationRegistration.Dispose();
        evictedEntry?.CancellationRegistration.Dispose();
        return matcher;
    }

    private void RemoveMatcherSession(CancellationToken ct, MatcherSessionEntry entry)
    {
        lock (_matcherSessionsGate)
        {
            if (_matcherSessions.TryGetValue(ct, out var current) && ReferenceEquals(current, entry))
                RemoveMatcherSessionLocked(ct, entry);
        }
    }

    private void RemoveMatcherSessionLocked(CancellationToken ct, MatcherSessionEntry entry)
    {
        _matcherSessions.Remove(ct);
        if (entry.OrderNode?.List != null)
            _matcherSessionOrder.Remove(entry.OrderNode);
    }

    private PreparedMatcher PrepareMatcher(SearchRequest request)
    {
        try
        {
            return new PreparedMatcher(CreateMatcher(request), error: null);
        }
        catch (Exception ex)
        {
            return new PreparedMatcher(matches: null, ex.Message);
        }
    }

    private Func<string, IEnumerable<(int start, int length)>> CreateMatcher(SearchRequest request)
    {
        if (request.IsRegex)
        {
            var regex = _regexFactory(request.Query, request.CaseSensitive);

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
                var firstIndex = line.IndexOf(query, comparison);
                if (firstIndex < 0)
                    return Array.Empty<(int, int)>();

                var hits = new List<(int, int)> { (firstIndex, query.Length) };
                var startIndex = firstIndex + query.Length;

                while (startIndex < line.Length)
                {
                    var index = line.IndexOf(query, startIndex, comparison);
                    if (index < 0)
                        break;

                    hits.Add((index, query.Length));

                    startIndex = index + query.Length;
                }

                return hits;
            };
        }
    }

    private sealed class PreparedMatcher
    {
        private readonly Func<string, IEnumerable<(int start, int length)>>? _matches;

        public PreparedMatcher(
            Func<string, IEnumerable<(int start, int length)>>? matches,
            string? error)
        {
            _matches = matches;
            Error = error;
        }

        public string? Error { get; }

        public IEnumerable<(int start, int length)> GetMatches(string line)
        {
            if (Error != null)
                throw new ArgumentException(Error);

            return _matches!(line);
        }
    }

    private sealed class MatcherSessionEntry
    {
        public MatcherSessionEntry(MatcherSignature signature, PreparedMatcher matcher)
        {
            Signature = signature;
            Matcher = matcher;
        }

        public MatcherSignature Signature { get; }

        public PreparedMatcher Matcher { get; }

        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public LinkedListNode<CancellationToken>? OrderNode { get; set; }
    }

    private readonly record struct MatcherSignature(string Query, bool IsRegex, bool CaseSensitive);

    private static LineScopeMatcher? GetLineScope(string filePath, SearchRequest request)
    {
        var lineScope = GetLineScopeDefinition(filePath, request);
        return lineScope == null ? null : new LineScopeMatcher(lineScope.Mode, lineScope.LineNumbers);
    }

    private sealed class PreparedFilterMatcher
    {
        private readonly Func<string, bool>? _isMatch;

        public PreparedFilterMatcher(Func<string, bool>? isMatch, string? error)
        {
            _isMatch = isMatch;
            Error = error;
        }

        public string? Error { get; }

        public bool IsMatch(string line) => _isMatch!(line);
    }

    private static SearchLineScope? GetLineScopeDefinition(string filePath, SearchRequest request)
    {
        if (request.LineScopesByFilePath.TryGetValue(filePath, out var lineScope))
            return lineScope;

        if (!request.AllowedLineNumbersByFilePath.TryGetValue(filePath, out var allowedLines))
            return null;

        return new SearchLineScope
        {
            Mode = SearchLineScopeMode.IncludeOnly,
            LineNumbers = allowedLines
        };
    }

    private FileStream OpenSearchStream(string filePath)
        => new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            LogReadShare,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

    private static long GetSnapshotLength(long fileLength, FileEncoding encoding)
        => encoding is FileEncoding.Utf16 or FileEncoding.Utf16Be
            ? fileLength & ~1L
            : fileLength;

    private FileHandleSnapshot CaptureHandleSnapshot(FileStream stream)
        => new(
            GetGenerationTokenOrUnknown(stream),
            stream.Length,
            GetLastWriteTimeUtcOrDefault(stream));

    private FileGenerationToken GetGenerationTokenOrUnknown(FileStream stream)
    {
        try
        {
            return _generationTokenProvider(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return FileGenerationToken.Unknown;
        }
    }

    private static DateTime GetLastWriteTimeUtcOrDefault(FileStream stream)
    {
        try
        {
            return ChunkedLogReaderService.GetLastWriteTimeUtc(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return default;
        }
    }

    private static bool IsUnstableScan(FileHandleSnapshot initial, FileHandleSnapshot final)
    {
        if (initial.GenerationToken.IsKnown &&
            final.GenerationToken.IsKnown &&
            initial.GenerationToken != final.GenerationToken)
        {
            return true;
        }

        if (final.Length < initial.Length)
            return true;

        return false;
    }

    private static FileGenerationToken ResolveStableToken(FileHandleSnapshot initial, FileHandleSnapshot final)
    {
        if (initial.GenerationToken.IsKnown && final.GenerationToken.IsKnown)
        {
            return initial.GenerationToken == final.GenerationToken
                ? final.GenerationToken
                : FileGenerationToken.Unknown;
        }

        return initial.GenerationToken.IsKnown
            ? initial.GenerationToken
            : final.GenerationToken;
    }

    private static FileScanGenerationEvidence AccountForTimestampOnlyScanDrift(
        FileScanGenerationEvidence evidence,
        FileHandleSnapshot initial,
        FileHandleSnapshot final)
    {
        if (evidence.Correlation == FileGenerationCorrelation.Stale ||
            initial.Length != final.Length ||
            initial.LastWriteTimeUtc == default ||
            final.LastWriteTimeUtc == default ||
            initial.LastWriteTimeUtc == final.LastWriteTimeUtc)
        {
            return evidence;
        }

        return evidence with { Correlation = FileGenerationCorrelation.Unknown };
    }

    private static FileHandleSnapshot ResolveStableSnapshot(FileHandleSnapshot initial, FileHandleSnapshot final)
        => final with
        {
            GenerationToken = ResolveStableToken(initial, final),
            LastWriteTimeUtc = final.LastWriteTimeUtc != default
                ? final.LastWriteTimeUtc
                : initial.LastWriteTimeUtc
        };

    private FileScanGenerationEvidence CorrelateWithCurrentPath(
        string filePath,
        FileHandleSnapshot scannedSnapshot)
    {
        var scannedToken = scannedSnapshot.GenerationToken;
        if (!scannedToken.IsKnown)
            return FileScanGenerationEvidence.Unknown;

        try
        {
            using var currentStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                LogReadShare,
                bufferSize: 1,
                FileOptions.RandomAccess);
            var currentSnapshot = CaptureHandleSnapshot(currentStream);
            var currentToken = currentSnapshot.GenerationToken;
            if (currentSnapshot.Length < scannedSnapshot.Length)
                return new FileScanGenerationEvidence(scannedToken, FileGenerationCorrelation.Stale);

            if (!currentToken.IsKnown)
                return new FileScanGenerationEvidence(scannedToken, FileGenerationCorrelation.Unknown);

            if (currentToken != scannedToken)
                return new FileScanGenerationEvidence(scannedToken, FileGenerationCorrelation.Stale);

            var hasTimestampOnlyDrift = currentSnapshot.Length == scannedSnapshot.Length &&
                                        scannedSnapshot.LastWriteTimeUtc != default &&
                                        currentSnapshot.LastWriteTimeUtc != default &&
                                        currentSnapshot.LastWriteTimeUtc != scannedSnapshot.LastWriteTimeUtc;
            if (hasTimestampOnlyDrift)
                return new FileScanGenerationEvidence(scannedToken, FileGenerationCorrelation.Unknown);

            return new FileScanGenerationEvidence(
                scannedToken,
                FileGenerationCorrelation.Current);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileScanGenerationEvidence(scannedToken, FileGenerationCorrelation.Stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new FileScanGenerationEvidence(scannedToken, FileGenerationCorrelation.Unknown);
        }
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

    private readonly record struct FileHandleSnapshot(
        FileGenerationToken GenerationToken,
        long Length,
        DateTime LastWriteTimeUtc);

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
