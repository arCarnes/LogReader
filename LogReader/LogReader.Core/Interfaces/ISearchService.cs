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

        var results = new SearchResult[request.FilePaths.Count];
        var nextIndex = -1;
        var workers = Enumerable.Range(0, Math.Min(maximumConcurrency, request.FilePaths.Count))
            .Select(_ => RunWorkerAsync())
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        return results;

        async Task RunWorkerAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= request.FilePaths.Count)
                    return;

                var filePath = request.FilePaths[index];
                using (await acquireOperationAsync(filePath, ct).ConfigureAwait(false))
                {
                    var encoding = fileEncodings.TryGetValue(filePath, out var configured)
                        ? configured
                        : FileEncoding.Utf8;
                    results[index] = await SearchFileAsync(filePath, request, encoding, ct).ConfigureAwait(false);
                }
            }
        }
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
