namespace LogReader.Infrastructure.Services;

using System.Text;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class SearchService : ISearchService
{
    private const int BufferSize = 256 * 1024; // 256KB read buffer for search

    public async Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
    {
        var result = new SearchResult { FilePath = filePath };

        if (string.IsNullOrEmpty(request.Query))
            return result;

        try
        {
            var matcher = CreateMatcher(request);
            var enc = EncodingHelper.GetEncoding(encoding);

            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var reader = new StreamReader(stream, enc, detectEncodingFromByteOrderMarks: true, bufferSize: BufferSize);

            long lineNumber = 0;
            string? line;

            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                ct.ThrowIfCancellationRequested();
                lineNumber++;

                var matches = matcher(line);
                foreach (var (start, length) in matches)
                {
                    result.Hits.Add(new SearchHit
                    {
                        LineNumber = lineNumber,
                        LineText = line.Length > 2000 ? line[..2000] + "..." : line,
                        MatchStart = start,
                        MatchLength = length
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
    {
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = request.FilePaths.Select(async filePath =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var encoding = fileEncodings.TryGetValue(filePath, out var enc) ? enc : FileEncoding.Utf8;
                return await SearchFileAsync(filePath, request, encoding, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        return results;
    }

    private static Func<string, IEnumerable<(int start, int length)>> CreateMatcher(SearchRequest request)
    {
        if (request.IsRegex)
        {
            var options = RegexOptions.Compiled;
            if (!request.CaseSensitive)
                options |= RegexOptions.IgnoreCase;

            var pattern = request.WholeWord ? $@"\b{request.Query}\b" : request.Query;
            var regex = new Regex(pattern, options, TimeSpan.FromSeconds(5));

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

                    if (request.WholeWord)
                    {
                        bool wordStart = idx == 0 || !char.IsLetterOrDigit(line[idx - 1]);
                        bool wordEnd = idx + query.Length >= line.Length || !char.IsLetterOrDigit(line[idx + query.Length]);
                        if (wordStart && wordEnd)
                        {
                            hits.Add((idx, query.Length));
                        }
                    }
                    else
                    {
                        hits.Add((idx, query.Length));
                    }

                    startIndex = idx + Math.Max(1, query.Length);
                }

                return hits;
            };
        }
    }
}
