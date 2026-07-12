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

    /// <summary>
    /// Attempts an adaptive indexed search for an include-only line scope. Returns <see langword="null"/>
    /// when sequential scanning is the safer strategy.
    /// </summary>
    Task<SearchResult?> TrySearchFileIndexedAsync(
        string filePath,
        SearchRequest request,
        FileEncoding encoding,
        int indexedLineCount,
        Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
        CancellationToken ct = default)
        => Task.FromResult<SearchResult?>(null);

    /// <summary>Searches multiple files concurrently using adaptive policy-driven parallelism.</summary>
    Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default);

    /// <summary>
    /// Searches multiple files while allowing callers with a stable line-index lease to attempt sparse reads.
    /// Implementations that do not support indexed search retain the normal sequential behavior.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchFilesAsync(
        SearchRequest request,
        IDictionary<string, FileEncoding> fileEncodings,
        Func<string, SearchRequest, FileEncoding, CancellationToken, Task<SearchResult?>> tryIndexedSearchAsync,
        CancellationToken ct = default)
        => SearchFilesAsync(request, fileEncodings, ct);

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
            HitLimitExceeded = result.HitLimitExceeded
        };
}
