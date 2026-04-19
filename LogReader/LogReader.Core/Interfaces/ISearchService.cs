namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Searches log files for text or regex patterns.
/// </summary>
public interface ISearchService
{
    /// <summary>Searches a single file and returns all matching hits.</summary>
    Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default);

    /// <summary>Searches a specific line range using a caller-supplied line reader.</summary>
    Task<SearchResult> SearchFileRangeAsync(
        string filePath,
        SearchRequest request,
        FileEncoding encoding,
        Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
        CancellationToken ct = default);

    /// <summary>Searches multiple files concurrently with bounded parallelism.</summary>
    Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4);
}
