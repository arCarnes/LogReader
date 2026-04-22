namespace LogReader.Core.Tests;

using System.Text;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class SearchServiceTests : IAsyncLifetime
{
    private readonly SearchService _searchService = new();
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
        return Task.CompletedTask;
    }

    private async Task<string> CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public async Task PlainTextSearch_FindsMatches()
    {
        var path = await CreateTestFile("test.log", "Hello World\nGoodbye World\nHello Again\n");
        var request = new SearchRequest { Query = "Hello", FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(2, result.Hits.Count);
        Assert.Equal(1, result.Hits[0].LineNumber);
        Assert.Equal(3, result.Hits[1].LineNumber);
    }

    [Fact]
    public async Task PlainTextSearch_CaseInsensitive()
    {
        var path = await CreateTestFile("test.log", "Hello World\nhello world\nHELLO WORLD\n");
        var request = new SearchRequest { Query = "hello", CaseSensitive = false, FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(3, result.Hits.Count);
    }

    [Fact]
    public async Task PlainTextSearch_CaseSensitive()
    {
        var path = await CreateTestFile("test.log", "Hello World\nhello world\nHELLO WORLD\n");
        var request = new SearchRequest { Query = "hello", CaseSensitive = true, FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Single(result.Hits);
        Assert.Equal(2, result.Hits[0].LineNumber);
    }

    [Fact]
    public async Task PlainTextSearch_LineRange_UsesInclusiveBounds()
    {
        var path = await CreateTestFile("range.log", "hit one\nhit two\nhit three\nhit four\nhit five\n");
        var request = new SearchRequest
        {
            Query = "hit",
            FilePaths = new List<string> { path },
            StartLineNumber = 2,
            EndLineNumber = 4
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(3, result.Hits.Count);
        Assert.Equal(2, result.Hits[0].LineNumber);
        Assert.Equal(4, result.Hits[2].LineNumber);
    }

    [Fact]
    public async Task SearchFileRangeAsync_ReadsOnlyRequestedRange_AndMatchesFullScan()
    {
        var path = await CreateTestFile("range-incremental.log", "skip one\nskip two\nhit three\nhit four\nskip five\n");
        var request = new SearchRequest
        {
            Query = "hit",
            FilePaths = new List<string> { path },
            StartLineNumber = 3,
            EndLineNumber = 4
        };
        var readRequests = new List<(int StartLine, int Count)>();

        var ranged = await _searchService.SearchFileRangeAsync(
            path,
            request,
            FileEncoding.Utf8,
            (startLine, count, _, _) =>
            {
                readRequests.Add((startLine, count));
                return Task.FromResult<IReadOnlyList<string>>(new[] { "hit three", "hit four" });
            });

        var full = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(new[] { (2, 2) }, readRequests);
        Assert.Equal(full.Hits.Select(hit => (hit.LineNumber, hit.MatchStart, hit.MatchLength)),
            ranged.Hits.Select(hit => (hit.LineNumber, hit.MatchStart, hit.MatchLength)));
    }

    [Fact]
    public async Task SearchFileRangeAsync_PreservesTimestampAndAllowedLineFiltering()
    {
        var path = await CreateTestFile(
            "range-filtered.log",
            "2026-03-09T19:49:10Z ERROR first\n2026-03-09T19:49:20Z ERROR second\n2026-03-09T19:49:30Z ERROR third\n");
        var request = new SearchRequest
        {
            Query = "ERROR",
            FilePaths = new List<string> { path },
            StartLineNumber = 1,
            EndLineNumber = 3,
            FromTimestamp = "2026-03-09T19:49:15Z",
            ToTimestamp = "2026-03-09T19:49:30Z",
            AllowedLineNumbersByFilePath = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = new List<int> { 1, 2 }
            }
        };

        var result = await _searchService.SearchFileRangeAsync(
            path,
            request,
            FileEncoding.Utf8,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[]
            {
                "2026-03-09T19:49:10Z ERROR first",
                "2026-03-09T19:49:20Z ERROR second",
                "2026-03-09T19:49:30Z ERROR third"
            }));

        Assert.True(result.HasParseableTimestamps);
        Assert.Single(result.Hits);
        Assert.Equal(2, result.Hits[0].LineNumber);
    }

    [Fact]
    public async Task PlainTextSearch_LineRange_EndBeforeStart_ReturnsNoHits()
    {
        var path = await CreateTestFile("range-empty.log", "hit one\nhit two\nhit three\n");
        var request = new SearchRequest
        {
            Query = "hit",
            FilePaths = new List<string> { path },
            StartLineNumber = 4,
            EndLineNumber = 2
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task PlainTextSearch_TimestampRange_Iso8601_FiltersLines()
    {
        var path = await CreateTestFile(
            "timestamp-iso.log",
            "2026-03-09T19:49:10Z ERROR first\n2026-03-09T19:49:20Z ERROR second\n2026-03-09T19:49:30Z ERROR third\n");
        var request = new SearchRequest
        {
            Query = "ERROR",
            FilePaths = new List<string> { path },
            FromTimestamp = "2026-03-09T19:49:15Z",
            ToTimestamp = "2026-03-09T19:49:25Z"
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.True(result.HasParseableTimestamps);
        Assert.Single(result.Hits);
        Assert.Equal(2, result.Hits[0].LineNumber);
    }

    [Fact]
    public async Task PlainTextSearch_TimestampRange_TimeOnly_FiltersLines()
    {
        var path = await CreateTestFile(
            "timestamp-time.log",
            "19:49:10.100 WARN first\n19:49:12.500 WARN second\n19:49:14.000 WARN third\n");
        var request = new SearchRequest
        {
            Query = "WARN",
            FilePaths = new List<string> { path },
            FromTimestamp = "19:49:11.000",
            ToTimestamp = "19:49:13.000"
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.True(result.HasParseableTimestamps);
        Assert.Single(result.Hits);
        Assert.Equal(2, result.Hits[0].LineNumber);
    }

    [Fact]
    public async Task PlainTextSearch_TimestampRange_NoParseableTimestamps_SetsFlagAndNoHits()
    {
        var path = await CreateTestFile(
            "timestamp-none.log",
            "ERROR first line\nERROR second line\n");
        var request = new SearchRequest
        {
            Query = "ERROR",
            FilePaths = new List<string> { path },
            FromTimestamp = "2026-03-09 19:49:00",
            ToTimestamp = "2026-03-09 19:50:00"
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.False(result.HasParseableTimestamps);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task PlainTextSearch_TimestampRange_InvalidInput_ReturnsError()
    {
        var path = await CreateTestFile("timestamp-invalid.log", "2026-03-09T19:49:10Z ERROR first\n");
        var request = new SearchRequest
        {
            Query = "ERROR",
            FilePaths = new List<string> { path },
            FromTimestamp = "not-a-timestamp"
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.NotNull(result.Error);
        Assert.Contains("Invalid 'From' timestamp", result.Error);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task RegexSearch_FindsMatches()
    {
        var path = await CreateTestFile("test.log", "2024-01-15 ERROR Something failed\n2024-01-15 INFO Started\n2024-01-15 ERROR Another error\n");
        var request = new SearchRequest { Query = @"ERROR\s+\w+", IsRegex = true, FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(2, result.Hits.Count);
    }

    [Fact]
    public async Task RegexSearch_CaseInsensitive()
    {
        var path = await CreateTestFile("test.log", "Error one\nERROR two\nerror three\n");
        var request = new SearchRequest { Query = "error", IsRegex = true, CaseSensitive = false, FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(3, result.Hits.Count);
    }

    [Fact]
    public async Task SearchFileAsync_ExplicitEncoding_DoesNotAllowBomOverride()
    {
        var path = Path.Combine(_testDir, "utf16-bom.log");
        await File.WriteAllTextAsync(path, "ERROR in utf16\n", Encoding.Unicode);
        var request = new SearchRequest { Query = "ERROR", FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Empty(result.Hits);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task SearchFiles_MultipleFiles_BoundedConcurrency()
    {
        var path1 = await CreateTestFile("test1.log", "Hello World\nFoo Bar\n");
        var path2 = await CreateTestFile("test2.log", "Hello Earth\nBaz Qux\n");
        var request = new SearchRequest { Query = "Hello", FilePaths = new List<string> { path1, path2 } };
        var encodings = new Dictionary<string, FileEncoding>
        {
            [path1] = FileEncoding.Utf8,
            [path2] = FileEncoding.Utf8
        };

        var results = await _searchService.SearchFilesAsync(request, encodings);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Single(r.Hits));
    }

    [Fact]
    public async Task Search_MatchPosition_IsCorrect()
    {
        var path = await CreateTestFile("test.log", "The quick brown fox\n");
        var request = new SearchRequest { Query = "brown", FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Single(result.Hits);
        Assert.Equal(10, result.Hits[0].MatchStart);
        Assert.Equal(5, result.Hits[0].MatchLength);
    }

    [Fact]
    public async Task Search_Cancellation_PreCanceledToken_ReturnsNoHits()
    {
        // A pre-canceled token must result in: no exception escapes, no error, no hits,
        // and the call completes quickly regardless of file size.
        var path = await CreateTestFile("cancel.log", "Line with searchable content\nAnother searchable line\n");
        var request = new SearchRequest { Query = "searchable", FilePaths = new List<string> { path } };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already canceled before call

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8, cts.Token);
        sw.Stop();

        Assert.Empty(result.Hits);
        Assert.Null(result.Error);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Pre-canceled search took {sw.ElapsedMilliseconds}ms; expected < 1s");
    }

    [Fact]
    public async Task Search_Cancellation_InFlight_TerminatesCleanly()
    {
        // 50 000 lines (~1 MB) is large enough to still be searching when the
        // 10 ms cancel fires on most machines, but small enough to write quickly.
        var lines = Enumerable.Range(0, 50_000).Select(i => $"Line {i} content here");
        var path = await CreateTestFile("cancel-inflight.log", string.Join("\n", lines));
        var request = new SearchRequest { Query = "content", FilePaths = new List<string> { path } };

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(10);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8, cts.Token);
        sw.Stop();

        // No exception escapes and no error is surfaced regardless of when cancellation fires
        Assert.Null(result.Error);
        // The result is always a valid object (partial hits, zero hits, or all hits are acceptable)
        Assert.NotNull(result);
        // Must complete within a bounded time — no hang on cancellation
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Canceled search took {sw.ElapsedMilliseconds}ms; expected < 2s");
    }

    [Fact]
    public async Task PlainTextSearch_MultipleMatchesOnSameLine()
    {
        var path = await CreateTestFile("test.log", "error error error\n");
        var request = new SearchRequest { Query = "error", FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(3, result.Hits.Count);
        Assert.Equal(0, result.Hits[0].MatchStart);
        Assert.Equal(6, result.Hits[1].MatchStart);
        Assert.Equal(12, result.Hits[2].MatchStart);
    }

    [Fact]
    public async Task Search_EmptyFile_ReturnsNoHits()
    {
        var path = await CreateTestFile("empty.log", "");
        var request = new SearchRequest { Query = "anything", FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Empty(result.Hits);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsNoHits()
    {
        var path = await CreateTestFile("test.log", "Hello World\n");
        var request = new SearchRequest { Query = "", FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.NotNull(result);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task RegexSearch_InvalidPattern_ReturnsError()
    {
        var path = await CreateTestFile("test.log", "Hello World\n");
        var request = new SearchRequest { Query = "[invalid", IsRegex = true, FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task RegexSearch_CatastrophicBacktracking_ReturnsErrorWithinTimeout()
    {
        // (a+)+$ on a string of a's with no trailing match triggers exponential backtracking.
        // SearchService uses a short regex timeout, so it should fail fast and surface
        // the timeout error rather than hanging.
        var line = new string('a', 30) + "!";
        var path = await CreateTestFile("backtrack.log", line + "\n");
        var request = new SearchRequest { Query = @"(a+)+$", IsRegex = true, FilePaths = new List<string> { path } };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);
        sw.Stop();

        Assert.NotNull(result.Error);
        Assert.Empty(result.Hits);
        Assert.True(sw.ElapsedMilliseconds < 2_000,
            $"Search took {sw.ElapsedMilliseconds}ms; expected to complete within 2s via regex timeout");
    }
}
