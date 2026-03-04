namespace LogReader.Tests;

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
    public async Task PlainTextSearch_WholeWord()
    {
        var path = await CreateTestFile("test.log", "error occurred\nerrors found\nan error here\n");
        var request = new SearchRequest { Query = "error", WholeWord = true, CaseSensitive = false, FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(2, result.Hits.Count); // "error occurred" and "an error here" but not "errors found"
        Assert.Equal(1, result.Hits[0].LineNumber);
        Assert.Equal(3, result.Hits[1].LineNumber);
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
    public async Task Search_Cancellation_StopsEarly()
    {
        // Create a large file so search takes long enough to be cancelled
        var lines = Enumerable.Range(0, 1_000_000).Select(i => $"Line {i} with some searchable content here padding padding padding");
        var path = await CreateTestFile("large.log", string.Join("\n", lines));
        var request = new SearchRequest { Query = "searchable", FilePaths = new List<string> { path } };

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(10); // Cancel very quickly

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8, cts.Token);

        // Either cancelled (no hits) or completed (on very fast machines) - both acceptable
        Assert.True(result.Hits.Count == 0 || result.Hits.Count < 1_000_000);
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
        // SearchService constructs Regex with TimeSpan.FromSeconds(5), so it should time out
        // and surface the error rather than hanging indefinitely.
        var line = new string('a', 30) + "!";
        var path = await CreateTestFile("backtrack.log", line + "\n");
        var request = new SearchRequest { Query = @"(a+)+$", IsRegex = true, FilePaths = new List<string> { path } };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);
        sw.Stop();

        Assert.NotNull(result.Error);
        Assert.Empty(result.Hits);
        Assert.True(sw.ElapsedMilliseconds < 6_000,
            $"Search took {sw.ElapsedMilliseconds}ms; expected to complete within 6s via regex timeout");
    }
}
