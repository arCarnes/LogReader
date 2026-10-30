namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Searches log files for text or regex patterns.
/// </summary>
public interface ISearchService
{
    /// <summary>Evaluates a single file for filtering and returns compact ordered line numbers.</summary>
    async Task<FilterResult> FilterFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
    {
        var searchResult = await SearchFileAsync(filePath, request, encoding, ct).ConfigureAwait(false);
        return ToFilterResult(searchResult);
    }

    /// <summary>Evaluates multiple files for filtering using adaptive policy-driven parallelism.</summary>
    async Task<IReadOnlyList<FilterResult>> FilterFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default)
    {
        var searchResults = await SearchFilesAsync(request, fileEncodings, ct).ConfigureAwait(false);
        return searchResults.Select(ToFilterResult).ToArray();
    }

    /// <summary>Searches a single file and returns all matching hits.</summary>
    Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default);

    /// <summary>Searches a specific line range using a caller-supplied line reader.</summary>
    Task<SearchResult> SearchFileRangeAsync(
        string filePath,
        SearchRequest request,
        FileEncoding encoding,
        Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
        CancellationToken ct = default);

    /// <summary>Searches multiple files concurrently using adaptive policy-driven parallelism.</summary>
    Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default);

    /// <summary>
    /// Searches a stable file list with a caller-owned concurrency/admission policy.
    /// Implementations may prepare shared matcher state once for the batch.
    /// </summary>
    async Task<IReadOnlyList<SearchResult>> SearchFilesBoundedAsync(
        SearchRequest request,
        IDictionary<string, FileEncoding> fileEncodings,
        int maximumConcurrency,
        Func<string, CancellationToken, ValueTask<IDisposable>> acquireOperationAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fileEncodings);
        ArgumentNullException.ThrowIfNull(acquireOperationAsync);
        if (maximumConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));

        return await BoundedSearchBatchRunner.RunAsync(
            request.FilePaths, workOrder: null, maximumConcurrency, acquireOperationAsync,
            (filePath, token) => SearchFileAsync(filePath, request,
                fileEncodings.TryGetValue(filePath, out var encoding) ? encoding : FileEncoding.Utf8, token),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves each file's encoding after acquiring its caller-owned operation lease and carries the
    /// resolved value on the internal search result for downstream mapping and context reads.
    /// </summary>
    async Task<IReadOnlyList<SearchResult>> SearchFilesBoundedWithEncodingAsync(
        SearchRequest request,
        int maximumConcurrency,
        Func<string, FileEncoding> resolveEncoding,
        Func<string, CancellationToken, ValueTask<IDisposable>> acquireOperationAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolveEncoding);
        ArgumentNullException.ThrowIfNull(acquireOperationAsync);
        if (maximumConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));

        var plan = AdaptiveParallelismPolicy.CreatePlan(AdaptiveParallelismOperation.DiskSearch, request.FilePaths);
        var workOrder = AdaptiveParallelismScheduler.BuildInterleavedWorkOrder(plan);
        return await BoundedSearchBatchRunner.RunAsync(
            request.FilePaths, workOrder, maximumConcurrency, acquireOperationAsync,
            async (filePath, token) =>
            {
                var encoding = resolveEncoding(filePath);
                var result = await SearchFileAsync(filePath, request, encoding, token).ConfigureAwait(false);
                result.ResolvedEncoding = encoding;
                return result;
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches a stable file list while preserving completed ordered slots when cancellation stops the batch.
    /// This is intended for bounded count reduction; ordinary search cancellation semantics remain unchanged.
    /// </summary>
    async Task<BoundedSearchBatchResult> SearchFilesBoundedWithEncodingPartialAsync(
        SearchRequest request,
        int maximumConcurrency,
        Func<string, FileEncoding> resolveEncoding,
        Func<string, CancellationToken, ValueTask<IDisposable>> acquireOperationAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolveEncoding);
        ArgumentNullException.ThrowIfNull(acquireOperationAsync);
        if (maximumConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));

        return await BoundedSearchBatchRunner.RunPartialAsync(
            request.FilePaths, workOrder: null, maximumConcurrency, acquireOperationAsync,
            async (filePath, token) =>
            {
                var encoding = resolveEncoding(filePath);
                var result = await SearchFileAsync(filePath, request, encoding, token).ConfigureAwait(false);
                result.ResolvedEncoding = encoding;
                return result;
            }, ct).ConfigureAwait(false);
    }

    private static FilterResult ToFilterResult(SearchResult result)
        => new()
        {
            FilePath = result.FilePath,
            MatchingLineNumbers = result.Hits
                .Select(hit => checked((int)hit.LineNumber))
                .Where(line => line > 0)
                .Distinct()
                .OrderBy(line => line)
                .ToList(),
            Error = result.Error,
            HasParseableTimestamps = result.HasParseableTimestamps,
            HitLimitExceeded = result.HitLimitExceeded,
            GenerationEvidence = result.GenerationEvidence,
            EvaluatedThroughLine = result.EvaluatedThroughLine.HasValue
                ? checked((int)Math.Min(int.MaxValue, result.EvaluatedThroughLine.Value))
                : null
        };
}
