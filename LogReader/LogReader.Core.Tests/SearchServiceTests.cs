namespace LogReader.Core.Tests;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class SearchServiceTests : IAsyncLifetime
{
    private readonly SearchService _searchService = new();
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "WeezTailTests_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task SearchFileAsync_StableHandle_CapturesCurrentGenerationEvidence()
    {
        var path = await CreateTestFile("generation-stable.log", "match one\nmatch two\n");
        var token = FileGenerationToken.Create(1, 10);
        var service = new SearchService(RegexPatternFactory.Create, _ => token);
        var request = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };

        var result = await service.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(2, result.Hits.Count);
        Assert.Equal(2, result.MatchingLineCount);
        Assert.Equal(2, result.MatchOccurrenceCount);
        Assert.True(result.IsEvaluationComplete);
        Assert.Equal(2, result.EvaluatedThroughLine);
        Assert.Equal(token, result.GenerationEvidence.Token);
        Assert.Equal(FileGenerationCorrelation.Current, result.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task SearchFileAsync_PathSupersededAfterStableScan_RetainsStaleSnapshot()
    {
        var path = await CreateTestFile("generation-stale.log", "match\n");
        var scannedToken = FileGenerationToken.Create(1, 10);
        var currentToken = FileGenerationToken.Create(1, 11);
        var calls = 0;
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ => Interlocked.Increment(ref calls) <= 2 ? scannedToken : currentToken);
        var request = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };

        var result = await service.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Single(result.Hits);
        Assert.Equal(scannedToken, result.GenerationEvidence.Token);
        Assert.Equal(FileGenerationCorrelation.Stale, result.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task SearchFileAsync_SameIdentityTruncatedAfterScan_RetainsStaleSnapshot()
    {
        var path = await CreateTestFile("generation-truncated-after-scan.log", "match one\nmatch two\n");
        var token = FileGenerationToken.Create(1, 12);
        var calls = 0;
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 3)
                    File.WriteAllText(path, "match replacement\n");

                return token;
            });
        var request = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };

        var result = await service.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(2, result.Hits.Count);
        Assert.Equal(token, result.GenerationEvidence.Token);
        Assert.Equal(FileGenerationCorrelation.Stale, result.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task SearchFileAsync_SameIdentitySameSizeTimestampTouchAfterScan_IsUnknown()
    {
        var path = await CreateTestFile("generation-rewritten-after-scan.log", "match one\n");
        var token = FileGenerationToken.Create(1, 13);
        var calls = 0;
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 3)
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

                return token;
            });
        var request = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };

        var result = await service.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Single(result.Hits);
        Assert.Equal(token, result.GenerationEvidence.Token);
        Assert.Equal(FileGenerationCorrelation.Unknown, result.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task SearchFileAsync_SameIdentityTimestampTouchDuringScan_IsUnknownWithoutRetryError()
    {
        var path = await CreateTestFile("generation-timestamp-during-scan.log", "match one\n");
        var token = FileGenerationToken.Create(1, 15);
        var calls = 0;
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

                return token;
            });
        var request = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };

        var result = await service.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Single(result.Hits);
        Assert.Null(result.Error);
        Assert.Equal(3, calls);
        Assert.Equal(token, result.GenerationEvidence.Token);
        Assert.Equal(FileGenerationCorrelation.Unknown, result.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task SearchFileAsync_SameIdentityGrowthAfterScan_RemainsCurrent()
    {
        var path = await CreateTestFile("generation-appended-after-scan.log", "match one\n");
        var token = FileGenerationToken.Create(1, 14);
        var calls = 0;
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 3)
                    File.AppendAllText(path, "match appended\n");

                return token;
            });
        var request = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };

        var result = await service.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Single(result.Hits);
        Assert.Equal(token, result.GenerationEvidence.Token);
        Assert.Equal(FileGenerationCorrelation.Current, result.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task SearchFileAsync_FileGrowsAfterSnapshot_DefersAppendedMatches()
    {
        var path = await CreateTestFile("search-moving-eof.log", "match initial\n");
        var token = FileGenerationToken.Create(1, 140);
        var calls = 0;
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    File.AppendAllText(path, "match deferred\n");

                return token;
            });
        var request = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };

        var result = await service.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Single(result.Hits);
        Assert.Equal("match initial", result.Hits[0].LineText);
        Assert.Equal(1, result.MatchingLineCount);
        Assert.True(result.IsEvaluationComplete);
        Assert.True(result.FileChangedDuringOrAfterScan);
        Assert.Equal(1, result.EvaluatedThroughLine);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task FilterFileAsync_FileGrowsAfterSnapshot_DefersAppendedMatches()
    {
        var path = await CreateTestFile("filter-moving-eof.log", "match initial\n");
        var token = FileGenerationToken.Create(1, 141);
        var calls = 0;
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    File.AppendAllText(path, "match deferred\n");

                return token;
            });
        var request = new SearchRequest
        {
            Query = "match",
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply
        };

        var result = await service.FilterFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(new[] { 1 }, result.MatchingLineNumbers);
        Assert.Equal(1, result.EvaluatedThroughLine);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task SearchFileAsync_UnstableFirstAttempt_RetriesOnceWithoutCombiningRows()
    {
        var path = await CreateTestFile("generation-retry.log", "match\n");
        var firstToken = FileGenerationToken.Create(1, 10);
        var secondToken = FileGenerationToken.Create(1, 11);
        var stableToken = FileGenerationToken.Create(1, 12);
        var calls = 0;
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ => Interlocked.Increment(ref calls) switch
            {
                1 => firstToken,
                2 => secondToken,
                _ => stableToken
            });
        var request = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };

        var result = await service.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(5, calls);
        Assert.Single(result.Hits);
        Assert.Null(result.Error);
        Assert.Equal(stableToken, result.GenerationEvidence.Token);
        Assert.Equal(FileGenerationCorrelation.Current, result.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task SearchFileAsync_RepeatedUnstableHandle_ReturnsPerFileErrorWithoutRows()
    {
        var path = await CreateTestFile("generation-repeated-instability.log", "match\n");
        var calls = 0;
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ => FileGenerationToken.Create(1, (ulong)Interlocked.Increment(ref calls)));
        var request = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };

        var result = await service.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(4, calls);
        Assert.Empty(result.Hits);
        Assert.False(result.IsEvaluationComplete);
        Assert.Contains("changed repeatedly", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FilterFileAsync_UnstableFirstAttempt_RetriesOnceWithoutCombiningLines()
    {
        var path = await CreateTestFile("filter-generation-retry.log", "match\n");
        var firstToken = FileGenerationToken.Create(1, 20);
        var secondToken = FileGenerationToken.Create(1, 21);
        var stableToken = FileGenerationToken.Create(1, 22);
        var calls = 0;
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ => Interlocked.Increment(ref calls) switch
            {
                1 => firstToken,
                2 => secondToken,
                _ => stableToken
            });
        var request = new SearchRequest
        {
            Query = "match",
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply
        };

        var result = await service.FilterFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(5, calls);
        Assert.Equal(new[] { 1 }, result.MatchingLineNumbers);
        Assert.Equal(1, result.EvaluatedThroughLine);
        Assert.Null(result.Error);
        Assert.Equal(stableToken, result.GenerationEvidence.Token);
        Assert.Equal(FileGenerationCorrelation.Current, result.GenerationEvidence.Correlation);
    }

    [Fact]
    public async Task FilterFileAsync_RepeatedUnstableHandle_ReturnsPerFileErrorWithoutLines()
    {
        var path = await CreateTestFile("filter-generation-repeated-instability.log", "match\n");
        var calls = 0;
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ => FileGenerationToken.Create(1, (ulong)Interlocked.Increment(ref calls)));
        var request = new SearchRequest
        {
            Query = "match",
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply
        };

        var result = await service.FilterFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(4, calls);
        Assert.Empty(result.MatchingLineNumbers);
        Assert.Null(result.EvaluatedThroughLine);
        Assert.Contains("changed repeatedly", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAndFilter_GenerationMetadataUnavailable_RetainReadableResultsAsUnknown()
    {
        var path = await CreateTestFile("generation-unknown.log", "match\n");
        var service = new SearchService(
            RegexPatternFactory.Create,
            _ => throw new IOException("Identity unavailable."));
        var searchRequest = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };
        var filterRequest = new SearchRequest
        {
            Query = "match",
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply
        };

        var searchResult = await service.SearchFileAsync(path, searchRequest, FileEncoding.Utf8);
        var filterResult = await service.FilterFileAsync(path, filterRequest, FileEncoding.Utf8);

        Assert.Single(searchResult.Hits);
        Assert.Equal(FileGenerationCorrelation.Unknown, searchResult.GenerationEvidence.Correlation);
        Assert.Equal(new[] { 1 }, filterResult.MatchingLineNumbers);
        Assert.Equal(FileGenerationCorrelation.Unknown, filterResult.GenerationEvidence.Correlation);
        Assert.Equal(1, filterResult.EvaluatedThroughLine);
    }

    [Fact]
    public async Task SearchAndFilter_CurrentPathIdentityUnavailable_RetainKnownScannedTokenAsUnknown()
    {
        var path = await CreateTestFile("generation-current-path-unknown.log", "match\n");
        var scannedToken = FileGenerationToken.Create(1, 15);
        var searchCalls = 0;
        var searchService = new SearchService(
            RegexPatternFactory.Create,
            _ => Interlocked.Increment(ref searchCalls) <= 2
                ? scannedToken
                : FileGenerationToken.Unknown);
        var filterCalls = 0;
        var filterService = new SearchService(
            RegexPatternFactory.Create,
            _ => Interlocked.Increment(ref filterCalls) <= 2
                ? scannedToken
                : FileGenerationToken.Unknown);
        var searchRequest = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };
        var filterRequest = new SearchRequest
        {
            Query = "match",
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply
        };

        var searchResult = await searchService.SearchFileAsync(path, searchRequest, FileEncoding.Utf8);
        var filterResult = await filterService.FilterFileAsync(path, filterRequest, FileEncoding.Utf8);

        Assert.Equal(new FileScanGenerationEvidence(scannedToken, FileGenerationCorrelation.Unknown), searchResult.GenerationEvidence);
        Assert.Equal(new FileScanGenerationEvidence(scannedToken, FileGenerationCorrelation.Unknown), filterResult.GenerationEvidence);
    }

    [Fact]
    public async Task SearchAndFilter_EmptyFile_RecordZeroEvaluationBoundary()
    {
        var path = await CreateTestFile("empty-boundary.log", string.Empty);
        var searchRequest = new SearchRequest { Query = "match", FilePaths = new List<string> { path } };
        var filterRequest = new SearchRequest
        {
            Query = "match",
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply
        };

        var searchResult = await _searchService.SearchFileAsync(path, searchRequest, FileEncoding.Utf8);
        var filterResult = await _searchService.FilterFileAsync(path, filterRequest, FileEncoding.Utf8);

        Assert.Equal(0, searchResult.EvaluatedThroughLine);
        Assert.Equal(0, filterResult.EvaluatedThroughLine);
    }

    [Fact]
    public async Task SearchAndFilter_BoundedScan_RecordExactEvaluationBoundary()
    {
        var path = await CreateTestFile("bounded-evaluation.log", "match one\nmatch two\nmatch three\n");
        var searchRequest = new SearchRequest
        {
            Query = "match",
            FilePaths = new List<string> { path },
            EndLineNumber = 2
        };
        var filterRequest = searchRequest.Clone();
        filterRequest.Usage = SearchRequestUsage.FilterApply;

        var searchResult = await _searchService.SearchFileAsync(path, searchRequest, FileEncoding.Utf8);
        var filterResult = await _searchService.FilterFileAsync(path, filterRequest, FileEncoding.Utf8);

        Assert.Equal(new long[] { 1, 2 }, searchResult.Hits.Select(hit => hit.LineNumber));
        Assert.Equal(2, searchResult.EvaluatedThroughLine);
        Assert.Equal(new[] { 1, 2 }, filterResult.MatchingLineNumbers);
        Assert.Equal(2, filterResult.EvaluatedThroughLine);
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
    public async Task SearchFileAsync_CarriageReturnOnlyLineEndings_UsesSameLineNumbersAsIndex()
    {
        var path = await CreateTestFile("cr-only-search.log", "skip\rhit\rskip\rhit\r");
        var request = new SearchRequest { Query = "hit", FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(new long[] { 2, 4 }, result.Hits.Select(hit => hit.LineNumber).ToArray());
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
    public async Task SearchFileRangeAsync_ShortReadReportsActualEvaluatedBoundary()
    {
        var path = await CreateTestFile("range-short-read.log", "one\ntwo\nthree\nfour\nfive\nsix\n");
        var request = new SearchRequest
        {
            Query = "line",
            FilePaths = new List<string> { path },
            StartLineNumber = 3,
            EndLineNumber = 6
        };

        var result = await _searchService.SearchFileRangeAsync(
            path,
            request,
            FileEncoding.Utf8,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[]
            {
                "line three",
                "line four"
            }));

        Assert.Equal(4, result.EvaluatedThroughLine);
        Assert.Equal(new long[] { 3, 4 }, result.Hits.Select(hit => hit.LineNumber));
    }

    [Fact]
    public async Task SearchFileRangeAsync_EmptyIncludeScopeReportsRequestedEndWithoutReading()
    {
        var path = await CreateTestFile("range-empty-scope.log", "one\ntwo\nthree\nfour\n");
        var request = new SearchRequest
        {
            Query = "line",
            FilePaths = new List<string> { path },
            StartLineNumber = 2,
            EndLineNumber = 4,
            LineScopesByFilePath = new Dictionary<string, SearchLineScope>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = new()
                {
                    Mode = SearchLineScopeMode.IncludeOnly,
                    LineNumbers = Array.Empty<int>()
                }
            }
        };

        var result = await _searchService.SearchFileRangeAsync(
            path,
            request,
            FileEncoding.Utf8,
            (_, _, _, _) => throw new InvalidOperationException("An empty include scope should not read lines."));

        Assert.Equal(4, result.EvaluatedThroughLine);
        Assert.Empty(result.Hits);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task SearchFileRangeAsync_InvalidRegexWithEmptyIncludeScope_ReturnsErrorWithoutReading()
    {
        var path = await CreateTestFile("range-invalid-regex-empty-scope.log", "one\ntwo\nthree\nfour\n");
        var request = new SearchRequest
        {
            Query = "[invalid",
            IsRegex = true,
            FilePaths = new List<string> { path },
            StartLineNumber = 2,
            EndLineNumber = 4,
            LineScopesByFilePath = new Dictionary<string, SearchLineScope>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = new()
                {
                    Mode = SearchLineScopeMode.IncludeOnly,
                    LineNumbers = Array.Empty<int>()
                }
            }
        };

        var result = await _searchService.SearchFileRangeAsync(
            path,
            request,
            FileEncoding.Utf8,
            (_, _, _, _) => throw new InvalidOperationException("An invalid regex should fail before reading lines."));

        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(result.Hits);
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
            AllowedLineNumbersByFilePath = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase)
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
    public async Task SearchFileAsync_ExcludeLineScope_SkipsScopedLines()
    {
        var path = await CreateTestFile(
            "exclude-scope.log",
            "ERROR first\nERROR hidden\nERROR third\n");
        var request = new SearchRequest
        {
            Query = "ERROR",
            FilePaths = new List<string> { path },
            LineScopesByFilePath = new Dictionary<string, SearchLineScope>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = new()
                {
                    Mode = SearchLineScopeMode.Exclude,
                    LineNumbers = new[] { 2 }
                }
            }
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(new long[] { 1, 3 }, result.Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public async Task SearchFileRangeAsync_ExcludeLineScope_SkipsScopedLines()
    {
        var path = await CreateTestFile(
            "range-exclude-scope.log",
            "ERROR first\nERROR hidden\nERROR third\n");
        var request = new SearchRequest
        {
            Query = "ERROR",
            FilePaths = new List<string> { path },
            StartLineNumber = 1,
            EndLineNumber = 3,
            LineScopesByFilePath = new Dictionary<string, SearchLineScope>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = new()
                {
                    Mode = SearchLineScopeMode.Exclude,
                    LineNumbers = new[] { 2 }
                }
            }
        };

        var result = await _searchService.SearchFileRangeAsync(
            path,
            request,
            FileEncoding.Utf8,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[]
            {
                "ERROR first",
                "ERROR hidden",
                "ERROR third"
            }));

        Assert.Equal(new long[] { 1, 3 }, result.Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public async Task FilterApply_DoesNotRetainLineText_AndKeepsOneHitPerMatchingLine()
    {
        var path = await CreateTestFile("filter-memory.log", "error error error\nno match\nerror again\n");
        var request = new SearchRequest
        {
            Query = "error",
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(new long[] { 1, 3 }, result.Hits.Select(hit => hit.LineNumber).ToArray());
        Assert.All(result.Hits, hit => Assert.Equal(string.Empty, hit.LineText));
        Assert.Equal(3, result.Hits[0].Matches.Count);
        Assert.Equal(result.Hits[0].MatchStart, result.Hits[0].OriginalMatchStart);
        Assert.Equal(result.Hits[0].MatchLength, result.Hits[0].OriginalMatchLength);
    }

    [Theory]
    [InlineData(false, "error", new[] { 1, 3 })]
    [InlineData(true, "^error(?: error)+$", new[] { 1 })]
    public async Task FilterFileAsync_ReturnsCompactOrderedLineNumbers(bool isRegex, string query, int[] expectedLines)
    {
        var path = await CreateTestFile("compact-filter.log", "error error error\nno match\nerror again\n");
        var request = new SearchRequest
        {
            Query = query,
            IsRegex = isRegex,
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply
        };

        var result = await _searchService.FilterFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(expectedLines, result.MatchingLineNumbers);
    }

    [Fact]
    public async Task FilterFileAsync_TimeOnly_PreservesTimestampMetadata()
    {
        var path = await CreateTestFile(
            "compact-filter-time.log",
            "2026-03-09T19:49:10Z INFO first\n2026-03-09T19:49:20Z WARN second\ninvalid third\n");
        var request = new SearchRequest
        {
            Query = string.Empty,
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply,
            FromTimestamp = "2026-03-09T19:49:15Z",
            ToTimestamp = "2026-03-09T19:49:25Z"
        };

        var result = await _searchService.FilterFileAsync(path, request, FileEncoding.Utf8);

        Assert.True(result.HasParseableTimestamps);
        Assert.Equal(new[] { 2 }, result.MatchingLineNumbers);
    }

    [Fact]
    public async Task FilterFileAsync_PreCanceledToken_ReturnsNoMatches()
    {
        var path = await CreateTestFile("compact-filter-canceled.log", "error\nerror\n");
        var request = new SearchRequest
        {
            Query = "error",
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _searchService.FilterFileAsync(path, request, FileEncoding.Utf8, cts.Token);

        Assert.Empty(result.MatchingLineNumbers);
    }

    [Fact]
    public async Task FilterFileAsync_InvalidRegex_ReturnsError()
    {
        var path = await CreateTestFile("compact-filter-error.log", "error\n");
        var request = new SearchRequest
        {
            Query = "[",
            IsRegex = true,
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply
        };

        var result = await _searchService.FilterFileAsync(path, request, FileEncoding.Utf8);

        Assert.NotNull(result.Error);
        Assert.Empty(result.MatchingLineNumbers);
    }

    [Fact]
    public async Task FilterFileAsync_PreparesMatcherOffCallingThread()
    {
        var path = await CreateTestFile("compact-filter-thread.log", "error\n");
        var callingThreadId = Environment.CurrentManagedThreadId;
        var matcherThreadId = callingThreadId;
        var searchService = new SearchService((pattern, caseSensitive) =>
        {
            matcherThreadId = Environment.CurrentManagedThreadId;
            return RegexPatternFactory.Create(pattern, caseSensitive);
        });
        var request = new SearchRequest
        {
            Query = "error",
            IsRegex = true,
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply
        };

        await searchService.FilterFileAsync(path, request, FileEncoding.Utf8);

        Assert.NotEqual(callingThreadId, matcherThreadId);
    }

    [Fact]
    public async Task FilterFilesAsync_Regex_PreparesOneMatcherAndPreservesRequestOrder()
    {
        var regexCreationCount = 0;
        var searchService = new SearchService((pattern, caseSensitive) =>
        {
            Interlocked.Increment(ref regexCreationCount);
            return RegexPatternFactory.Create(pattern, caseSensitive);
        });
        var path1 = await CreateTestFile("compact-filter-regex-first.log", "ERROR first\nno match\n");
        var path2 = await CreateTestFile("compact-filter-regex-second.log", "no match\nerror second\n");
        var request = new SearchRequest
        {
            Query = "error",
            IsRegex = true,
            CaseSensitive = false,
            FilePaths = new List<string> { path2, path1 },
            Usage = SearchRequestUsage.FilterApply
        };
        var encodings = request.FilePaths.ToDictionary(path => path, _ => FileEncoding.Utf8);

        var results = await searchService.FilterFilesAsync(request, encodings);

        Assert.Equal(1, regexCreationCount);
        Assert.Equal(new[] { path2, path1 }, results.Select(result => result.FilePath).ToArray());
        Assert.Equal(new[] { 2 }, results[0].MatchingLineNumbers);
        Assert.Equal(new[] { 1 }, results[1].MatchingLineNumbers);
    }

    [Fact]
    public async Task FilterFilesAsync_InvalidRegex_PreparesOnceAndReturnsSameErrorForEachFile()
    {
        var regexCreationCount = 0;
        var searchService = new SearchService((pattern, caseSensitive) =>
        {
            Interlocked.Increment(ref regexCreationCount);
            return RegexPatternFactory.Create(pattern, caseSensitive);
        });
        var path1 = await CreateTestFile("compact-filter-invalid-first.log", "first\n");
        var path2 = await CreateTestFile("compact-filter-invalid-second.log", "second\n");
        var request = new SearchRequest
        {
            Query = "[invalid",
            IsRegex = true,
            FilePaths = new List<string> { path1, path2 },
            Usage = SearchRequestUsage.FilterApply
        };
        var encodings = request.FilePaths.ToDictionary(path => path, _ => FileEncoding.Utf8);

        var results = await searchService.FilterFilesAsync(request, encodings);

        Assert.Equal(1, regexCreationCount);
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.Empty(result.MatchingLineNumbers);
        });
        Assert.Equal(results[0].Error, results[1].Error);
    }

    [Fact]
    public async Task FilterFilesAsync_AppliesTimestampRangeAndPerFileLineScopes()
    {
        var path1 = await CreateTestFile(
            "compact-filter-scoped-first.log",
            "2026-03-09T19:49:10Z ERROR early\n" +
            "2026-03-09T19:49:20Z ERROR included\n" +
            "2026-03-09T19:49:30Z ERROR outside scope\n" +
            "invalid ERROR timestamp\n");
        var path2 = await CreateTestFile(
            "compact-filter-scoped-second.log",
            "2026-03-09T19:49:20Z ERROR excluded\n" +
            "2026-03-09T19:49:25Z INFO no match\n" +
            "2026-03-09T19:49:30Z ERROR included\n" +
            "2026-03-09T19:49:40Z ERROR late\n");
        var request = new SearchRequest
        {
            Query = "ERROR",
            FilePaths = new List<string> { path1, path2 },
            Usage = SearchRequestUsage.FilterApply,
            FromTimestamp = "2026-03-09T19:49:15Z",
            ToTimestamp = "2026-03-09T19:49:35Z",
            LineScopesByFilePath = new Dictionary<string, SearchLineScope>(StringComparer.OrdinalIgnoreCase)
            {
                [path1] = new()
                {
                    Mode = SearchLineScopeMode.IncludeOnly,
                    LineNumbers = new[] { 2 }
                },
                [path2] = new()
                {
                    Mode = SearchLineScopeMode.Exclude,
                    LineNumbers = new[] { 1 }
                }
            }
        };
        var encodings = request.FilePaths.ToDictionary(path => path, _ => FileEncoding.Utf8);

        var results = await _searchService.FilterFilesAsync(request, encodings);

        Assert.Equal(new[] { 2 }, results[0].MatchingLineNumbers);
        Assert.Equal(new[] { 3 }, results[1].MatchingLineNumbers);
        Assert.All(results, result => Assert.True(result.HasParseableTimestamps));
    }

    [Fact]
    public async Task FilterFilesAsync_PreCanceledTokenSkipsMatcherPreparation()
    {
        var regexCreationCount = 0;
        var searchService = new SearchService((pattern, caseSensitive) =>
        {
            Interlocked.Increment(ref regexCreationCount);
            return RegexPatternFactory.Create(pattern, caseSensitive);
        });
        var path1 = await CreateTestFile("compact-filter-canceled-first.log", "error\n");
        var path2 = await CreateTestFile("compact-filter-canceled-second.log", "error\n");
        var request = new SearchRequest
        {
            Query = "error",
            IsRegex = true,
            FilePaths = new List<string> { path1, path2 },
            Usage = SearchRequestUsage.FilterApply
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => searchService.FilterFilesAsync(
                request,
                request.FilePaths.ToDictionary(path => path, _ => FileEncoding.Utf8),
                cts.Token));
        Assert.Equal(0, regexCreationCount);
    }

    [Fact]
    public async Task FilterApply_TimeOnly_ReturnsInRangeTimestampedLinesWithoutMatchSpans()
    {
        var path = await CreateTestFile(
            "filter-time-only.log",
            "2026-03-09T19:49:10Z INFO first\n2026-03-09T19:49:20Z WARN second\n2026-03-09T19:49:30Z ERROR third\n");
        var request = new SearchRequest
        {
            Query = string.Empty,
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply,
            FromTimestamp = "2026-03-09T19:49:15Z",
            ToTimestamp = "2026-03-09T19:49:25Z"
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.True(result.HasParseableTimestamps);
        var hit = Assert.Single(result.Hits);
        Assert.Equal(2, hit.LineNumber);
        Assert.Equal(string.Empty, hit.LineText);
        Assert.Empty(hit.Matches);
        Assert.Equal(0, hit.MatchLength);
    }

    [Fact]
    public async Task FilterApply_EmptyQueryWithoutTimestamp_ReturnsNoHits()
    {
        var path = await CreateTestFile("filter-empty-query.log", "first\nsecond\n");
        var request = new SearchRequest
        {
            Query = string.Empty,
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply,
            FromTimestamp = " "
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task Search_EmptyQueryWithTimestamp_ReturnsNoHits()
    {
        var path = await CreateTestFile(
            "search-empty-query-time.log",
            "2026-03-09T19:49:10Z INFO first\n2026-03-09T19:49:20Z WARN second\n");
        var request = new SearchRequest
        {
            Query = string.Empty,
            FilePaths = new List<string> { path },
            FromTimestamp = "2026-03-09T19:49:00Z",
            ToTimestamp = "2026-03-09T19:50:00Z"
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Empty(result.Hits);
        Assert.False(result.HasParseableTimestamps);
    }

    [Fact]
    public async Task FilterApply_TimeOnly_NoParseableTimestamps_SetsFlagFalseAndNoHits()
    {
        var path = await CreateTestFile(
            "filter-time-only-no-timestamps.log",
            "INFO first\nWARN second\n");
        var request = new SearchRequest
        {
            Query = string.Empty,
            FilePaths = new List<string> { path },
            Usage = SearchRequestUsage.FilterApply,
            FromTimestamp = "2026-03-09 19:49:00",
            ToTimestamp = "2026-03-09 19:50:00"
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.False(result.HasParseableTimestamps);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task Search_MaxHitsPerFile_CapsRetainedHits()
    {
        var path = await CreateTestFile("hit-cap.log", string.Join("\n", Enumerable.Range(1, 10).Select(i => $"error {i}")));
        var request = new SearchRequest
        {
            Query = "error",
            FilePaths = new List<string> { path },
            MaxHitsPerFile = 3
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.True(result.HitLimitExceeded);
        Assert.Equal(new long[] { 1, 2, 3 }, result.Hits.Select(hit => hit.LineNumber).ToArray());
    }

    [Fact]
    public async Task Search_MaxHitsPerFile_CapsMatchedLines_NotOccurrences()
    {
        var path = await CreateTestFile("line-hit-cap.log", "error error error\nerror again\nerror third\n");
        var request = new SearchRequest
        {
            Query = "error",
            FilePaths = new List<string> { path },
            MaxHitsPerFile = 2
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.True(result.HitLimitExceeded);
        Assert.Equal(new long[] { 1, 2 }, result.Hits.Select(hit => hit.LineNumber).ToArray());
        Assert.Equal(3, result.Hits[0].Matches.Count);
    }

    [Fact]
    public async Task Search_CountOrientedLiteral_CapsSamplesAndCompletesLineAndOccurrenceCounts()
    {
        var path = await CreateTestFile(
            "complete-counts.log",
            "error error\nno match\nerror\nerror error error\n");
        var request = new SearchRequest
        {
            Query = "error",
            FilePaths = [path],
            MaxHitsPerFile = 1,
            ContinueEvaluatingAfterHitLimit = true
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Single(result.Hits);
        Assert.True(result.HitLimitExceeded);
        Assert.Equal(3, result.MatchingLineCount);
        Assert.Equal(6, result.MatchOccurrenceCount);
        Assert.Equal(4, result.EvaluatedThroughLine);
        Assert.True(result.IsEvaluationComplete);
        Assert.False(result.WasCancelled);
    }

    [Fact]
    public async Task Search_CountOrientedRegex_AppliesTimestampBoundsAndCountsEveryOccurrence()
    {
        var path = await CreateTestFile(
            "regex-timestamp-counts.log",
            "2026-03-09 19:48:00 WARN WARN\n" +
            "2026-03-09 19:49:00 WARN WARN\n" +
            "2026-03-09 19:50:00 WARN\n");
        var request = new SearchRequest
        {
            Query = "W.RN",
            IsRegex = true,
            FilePaths = [path],
            FromTimestamp = "2026-03-09 19:49:00",
            ToTimestamp = "2026-03-09 19:50:00",
            MaxHitsPerFile = 1,
            ContinueEvaluatingAfterHitLimit = true
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Single(result.Hits);
        Assert.Equal(2, result.MatchingLineCount);
        Assert.Equal(3, result.MatchOccurrenceCount);
        Assert.True(result.IsEvaluationComplete);
        Assert.True(result.HasParseableTimestamps);
    }

    [Fact]
    public async Task Search_CountOrientedAggregation_AccountsForDatedBucketsAndOccurrences()
    {
        var path = await CreateTestFile(
            "dated-bucket-counts.log",
            "2026-03-09T19:49:00Z WARN WARN\n" +
            "2026-03-09T19:49:59Z WARN\n" +
            "2026-03-09T19:50:00Z WARN WARN WARN\n");
        var firstStart = new DateTimeOffset(2026, 3, 9, 19, 49, 0, TimeSpan.Zero);
        var secondStart = firstStart.AddMinutes(1);
        var plan = new SearchTimestampAggregationPlan(
            SearchTimestampBucketKind.Dated,
            SearchTimestampBucketSize.Minute,
            [
                DatedBucket(0, firstStart, secondStart),
                DatedBucket(1, secondStart, secondStart.AddMinutes(1))
            ]);
        var request = new SearchRequest
        {
            Query = "WARN",
            FilePaths = [path],
            FromTimestamp = "2026-03-09T19:49:00Z",
            ToTimestamp = "2026-03-09T19:50:00Z",
            MaxHitsPerFile = 0,
            ContinueEvaluatingAfterHitLimit = true,
            TimestampAggregation = plan
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Empty(result.Hits);
        Assert.Equal(3, result.MatchingLineCount);
        Assert.Equal(6, result.MatchOccurrenceCount);
        Assert.Equal(2, result.TimestampBucketCounts[0].MatchingLineCount);
        Assert.Equal(3, result.TimestampBucketCounts[0].MatchOccurrenceCount);
        Assert.Equal(1, result.TimestampBucketCounts[1].MatchingLineCount);
        Assert.Equal(3, result.TimestampBucketCounts[1].MatchOccurrenceCount);
        Assert.Equal(0, result.UnbucketedMatchingLineCount);
        Assert.Equal(0, result.UnbucketedMatchOccurrenceCount);
        Assert.True(result.IsEvaluationComplete);
    }

    [Fact]
    public async Task SearchFileRange_CountOrientedAggregation_UsesTimeOfDayBuckets()
    {
        var path = await CreateTestFile("time-bucket-counts.log", "unused\n");
        var plan = new SearchTimestampAggregationPlan(
            SearchTimestampBucketKind.TimeOfDay,
            SearchTimestampBucketSize.Hour,
            [
                TimeOfDayBucket(0, new TimeSpan(9, 0, 0), new TimeSpan(10, 0, 0)),
                TimeOfDayBucket(1, new TimeSpan(10, 0, 0), new TimeSpan(11, 0, 0))
            ]);
        var request = new SearchRequest
        {
            Query = "ERROR",
            FilePaths = [path],
            StartLineNumber = 1,
            EndLineNumber = 3,
            FromTimestamp = "09:00",
            ToTimestamp = "10:59:59",
            MaxHitsPerFile = 0,
            ContinueEvaluatingAfterHitLimit = true,
            TimestampAggregation = plan
        };

        var result = await _searchService.SearchFileRangeAsync(
            path,
            request,
            FileEncoding.Utf8,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<string>>(
                [
                    "09:15:00 ERROR ERROR",
                    "2026-03-09 10:30:00 ERROR",
                    "11:00:00 ERROR"
                ]));

        Assert.Equal(2, result.MatchingLineCount);
        Assert.Equal(3, result.MatchOccurrenceCount);
        Assert.Equal(1, result.TimestampBucketCounts[0].MatchingLineCount);
        Assert.Equal(2, result.TimestampBucketCounts[0].MatchOccurrenceCount);
        Assert.Equal(1, result.TimestampBucketCounts[1].MatchingLineCount);
        Assert.Equal(1, result.TimestampBucketCounts[1].MatchOccurrenceCount);
        Assert.Equal(0, result.UnbucketedMatchingLineCount);
        Assert.True(result.IsEvaluationComplete);
    }

    [Fact]
    public void SearchTimestampAggregationPlan_RejectsOverlappingBuckets()
    {
        var start = new DateTimeOffset(2026, 3, 9, 19, 49, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new SearchTimestampAggregationPlan(
            SearchTimestampBucketKind.Dated,
            SearchTimestampBucketSize.Minute,
            [
                DatedBucket(0, start, start.AddMinutes(2)),
                DatedBucket(1, start.AddMinutes(1), start.AddMinutes(3))
            ]));
    }

    [Fact]
    public async Task Search_DefaultHitCap_StopsEvaluationForDesktopCompatibility()
    {
        var path = await CreateTestFile(
            "default-hit-cap.log",
            string.Join("\n", Enumerable.Range(1, 10).Select(i => $"error {i}")));
        var request = new SearchRequest
        {
            Query = "error",
            FilePaths = [path],
            MaxHitsPerFile = 2
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(2, result.Hits.Count);
        Assert.Equal(3, result.MatchingLineCount);
        Assert.Equal(3, result.MatchOccurrenceCount);
        Assert.Equal(3, result.EvaluatedThroughLine);
        Assert.True(result.HitLimitExceeded);
        Assert.False(result.IsEvaluationComplete);
    }

    [Fact]
    public async Task Search_MaxRetainedLineTextLength_TrimsLineTextAndAdjustsMatchPosition()
    {
        var prefix = new string('a', 100);
        var suffix = new string('z', 100);
        var path = await CreateTestFile("retained-text-cap.log", prefix + "needle" + suffix + "\n");
        var request = new SearchRequest
        {
            Query = "needle",
            FilePaths = new List<string> { path },
            MaxRetainedLineTextLength = 40
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        var hit = Assert.Single(result.Hits);
        Assert.True(hit.LineText.Length <= 40);
        Assert.Contains("needle", hit.LineText, StringComparison.Ordinal);
        Assert.Equal("needle", hit.LineText.Substring(hit.MatchStart, hit.MatchLength));
    }

    [Fact]
    public async Task Search_MultiMegabyteFileWithoutNewline_RetainsBoundedMatchingContext()
    {
        var line = new string('x', 2 * 1024 * 1024) + "needle";
        var path = await CreateTestFile("large-search-no-newline.log", line);
        var request = new SearchRequest
        {
            Query = "needle$",
            IsRegex = true,
            FilePaths = [path],
            MaxRetainedLineTextLength = 8192
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(1, hit.LineNumber);
        Assert.True(hit.LineText.Length <= 8192);
        Assert.EndsWith("needle", hit.LineText, StringComparison.Ordinal);
        Assert.Equal("needle", hit.LineText.Substring(hit.MatchStart, hit.MatchLength));
    }

    [Fact]
    public async Task Filter_MultiMegabyteFileWithoutNewline_ReturnsCompleteLineSet()
    {
        var path = await CreateTestFile(
            "large-filter-no-newline.log",
            new string('x', 2 * 1024 * 1024) + "needle");
        var request = new SearchRequest
        {
            Query = "needle",
            Usage = SearchRequestUsage.FilterApply,
            FilePaths = [path]
        };

        var result = await _searchService.FilterFileAsync(path, request, FileEncoding.Utf8);

        Assert.Equal(new[] { 1 }, result.MatchingLineNumbers);
    }

    [Fact]
    public async Task PlainTextSearch_MultipleMatchesOnSameLine_GroupsMatchesIntoSingleLineHit()
    {
        var prefix = new string('x', 100);
        var gap = new string('x', 300);
        var line = prefix + "needle" + gap + "needle" + prefix;
        var path = await CreateTestFile("repeated-matches.log", line + "\n");
        var request = new SearchRequest
        {
            Query = "needle",
            FilePaths = new List<string> { path }
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(line, hit.LineText);
        Assert.Equal(new[] { prefix.Length, prefix.Length + "needle".Length + gap.Length },
            hit.Matches.Select(match => match.MatchStart).ToArray());
        Assert.All(hit.Matches, match => Assert.Equal("needle".Length, match.MatchLength));
    }

    [Theory]
    [InlineData("^", 0, 0)]
    [InlineData("$", 10, 11)]
    [InlineData("\\b", 0, 0)]
    [InlineData("(?=error)", 0, 6)]
    public async Task RegexSearch_ZeroWidthMatches_ProduceOneNavigableHitPerMatchingLine(
        string pattern,
        int firstLineMatchStart,
        int secondLineMatchStart)
    {
        var path = await CreateTestFile("zero-width.log", "error here\nother error\n");
        var request = new SearchRequest
        {
            Query = pattern,
            IsRegex = true,
            FilePaths = [path]
        };

        var searchResult = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);
        var filterResult = await _searchService.FilterFileAsync(
            path,
            new SearchRequest
            {
                Query = pattern,
                IsRegex = true,
                Usage = SearchRequestUsage.FilterApply,
                FilePaths = [path]
            },
            FileEncoding.Utf8);

        Assert.Equal(new long[] { 1, 2 }, searchResult.Hits.Select(hit => hit.LineNumber));
        Assert.Equal(firstLineMatchStart, searchResult.Hits[0].MatchStart);
        Assert.Equal(secondLineMatchStart, searchResult.Hits[1].MatchStart);
        Assert.All(searchResult.Hits, hit => Assert.All(hit.Matches, match => Assert.Equal(0, match.MatchLength)));
        Assert.Equal(new[] { 1, 2 }, filterResult.MatchingLineNumbers);
    }

    [Fact]
    public async Task PlainTextSearch_DoesNotReturnOverlappingMatches()
    {
        var path = await CreateTestFile("non-overlapping-matches.log", "aaa\n");
        var request = new SearchRequest
        {
            Query = "aa",
            FilePaths = new List<string> { path }
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        var hit = Assert.Single(result.Hits);
        var match = Assert.Single(hit.Matches);
        Assert.Equal(0, match.MatchStart);
        Assert.Equal(2, match.MatchLength);
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
    public async Task SearchFileRangeAsync_InvalidRegexWithEndBeforeStart_ReturnsErrorWithoutReading()
    {
        var path = await CreateTestFile("invalid-regex-empty-range.log", "hit one\nhit two\nhit three\n");
        var request = new SearchRequest
        {
            Query = "[invalid",
            IsRegex = true,
            FilePaths = new List<string> { path },
            StartLineNumber = 4,
            EndLineNumber = 2
        };

        var result = await _searchService.SearchFileRangeAsync(
            path,
            request,
            FileEncoding.Utf8,
            (_, _, _, _) => throw new InvalidOperationException("An invalid regex should fail before reading lines."));

        Assert.False(string.IsNullOrWhiteSpace(result.Error));
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
        Assert.All(result.Hits, hit =>
        {
            var match = Assert.Single(hit.Matches);
            Assert.Equal(hit.MatchStart, match.MatchStart);
            Assert.Equal(hit.MatchLength, match.MatchLength);
        });
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
    public async Task RegexSearch_CaseInsensitive_IsCultureInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");

            var path = await CreateTestFile("turkish-regex.log", "INFO line\n");
            var request = new SearchRequest
            {
                Query = "info",
                IsRegex = true,
                CaseSensitive = false,
                FilePaths = new List<string> { path }
            };

            var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

            Assert.Single(result.Hits);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void RegexPatternFactory_CaseInsensitive_IsCultureInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");

            var regex = RegexPatternFactory.Create("info", caseSensitive: false);

            Assert.Matches(regex, "INFO");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
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
    public async Task SearchFiles_Regex_PreparesOneMatcherAndPreservesRequestOrder()
    {
        var regexCreationCount = 0;
        var searchService = new SearchService((pattern, caseSensitive) =>
        {
            Interlocked.Increment(ref regexCreationCount);
            return RegexPatternFactory.Create(pattern, caseSensitive);
        });
        var path1 = await CreateTestFile("regex-first.log", "ERROR first\n");
        var path2 = await CreateTestFile("regex-second.log", "error second\n");
        var request = new SearchRequest
        {
            Query = "error",
            IsRegex = true,
            CaseSensitive = false,
            FilePaths = new List<string> { path1, path2 }
        };
        var encodings = request.FilePaths.ToDictionary(path => path, _ => FileEncoding.Utf8);

        var results = await searchService.SearchFilesAsync(request, encodings);

        Assert.Equal(1, regexCreationCount);
        Assert.Equal(new[] { path1, path2 }, results.Select(result => result.FilePath).ToArray());
        Assert.All(results, result => Assert.Single(result.Hits));
    }

    [Fact]
    public async Task SearchFiles_InvalidRegex_ReturnsAnErrorForEachFile()
    {
        var path1 = await CreateTestFile("invalid-regex-first.log", "first\n");
        var path2 = await CreateTestFile("invalid-regex-second.log", "second\n");
        var request = new SearchRequest
        {
            Query = "[invalid",
            IsRegex = true,
            FilePaths = new List<string> { path1, path2 }
        };
        var encodings = request.FilePaths.ToDictionary(path => path, _ => FileEncoding.Utf8);

        var results = await _searchService.SearchFilesAsync(request, encodings);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.False(string.IsNullOrWhiteSpace(result.Error)));
        Assert.Equal(results[0].Error, results[1].Error);
    }

    [Fact]
    public async Task SearchFileRangeAsync_RegexReusesMatcherWithinCancellationSessionOnly()
    {
        var regexCreationCount = 0;
        var searchService = new SearchService((pattern, caseSensitive) =>
        {
            Interlocked.Increment(ref regexCreationCount);
            return RegexPatternFactory.Create(pattern, caseSensitive);
        });
        var request = new SearchRequest
        {
            Query = "error",
            IsRegex = true,
            StartLineNumber = 1,
            EndLineNumber = 1
        };
        using var firstSession = new CancellationTokenSource();

        await SearchRangeAsync(firstSession.Token);
        await SearchRangeAsync(firstSession.Token);

        Assert.Equal(1, regexCreationCount);

        firstSession.Cancel();
        Assert.Equal(0, searchService.MatcherSessionCount);
        using var secondSession = new CancellationTokenSource();
        await SearchRangeAsync(secondSession.Token);

        Assert.Equal(2, regexCreationCount);
        secondSession.Cancel();
        Assert.Equal(0, searchService.MatcherSessionCount);

        async Task SearchRangeAsync(CancellationToken ct)
        {
            var result = await searchService.SearchFileRangeAsync(
                "session.log",
                request,
                FileEncoding.Utf8,
                (_, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "ERROR" }),
                ct);
            Assert.Single(result.Hits);
        }
    }

    [Fact]
    public async Task SearchFileRangeAsync_RegexReplacesMatcherWhenSearchShapeChanges()
    {
        var regexCreationCount = 0;
        var searchService = new SearchService((pattern, caseSensitive) =>
        {
            Interlocked.Increment(ref regexCreationCount);
            return RegexPatternFactory.Create(pattern, caseSensitive);
        });
        using var session = new CancellationTokenSource();
        var request = new SearchRequest
        {
            Query = "error",
            IsRegex = true,
            StartLineNumber = 1,
            EndLineNumber = 1
        };

        await searchService.SearchFileRangeAsync(
            "session.log",
            request,
            FileEncoding.Utf8,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "ERROR" }),
            session.Token);
        request.CaseSensitive = true;
        await searchService.SearchFileRangeAsync(
            "session.log",
            request,
            FileEncoding.Utf8,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "ERROR" }),
            session.Token);

        Assert.Equal(2, regexCreationCount);
        session.Cancel();
    }

    [Fact]
    public async Task SearchFileRangeAsync_RegexMatcherSessionsRemainBoundedWithoutCancellation()
    {
        var regexCreationCount = 0;
        var searchService = new SearchService((pattern, caseSensitive) =>
        {
            Interlocked.Increment(ref regexCreationCount);
            return RegexPatternFactory.Create(pattern, caseSensitive);
        });
        var request = new SearchRequest
        {
            Query = "error",
            IsRegex = true,
            StartLineNumber = 1,
            EndLineNumber = 1
        };

        for (var index = 0; index < SearchService.MatcherSessionCapacity + 10; index++)
        {
            using var session = new CancellationTokenSource();
            var result = await searchService.SearchFileRangeAsync(
                "session.log",
                request,
                FileEncoding.Utf8,
                (_, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "ERROR" }),
                session.Token);

            Assert.Single(result.Hits);
        }

        Assert.Equal(SearchService.MatcherSessionCapacity + 10, regexCreationCount);
        Assert.Equal(SearchService.MatcherSessionCapacity, searchService.MatcherSessionCount);
    }

    [Fact]
    public async Task SearchFiles_AdaptiveScheduling_PreservesRequestOrderUnderOutOfOrderCompletion()
    {
        var paths = new[]
        {
            UncPath("server-a", "share", "slow.log"),
            UncPath("server-b", "share", "fast.log")
        };
        var service = new SearchService(async (filePath, _, _, ct) =>
        {
            await Task.Delay(filePath.Contains("slow", StringComparison.Ordinal) ? 75 : 10, ct);
            return new SearchResult { FilePath = filePath };
        });
        var request = new SearchRequest { Query = "needle", FilePaths = paths.ToList() };

        var results = await service.SearchFilesAsync(request, new Dictionary<string, FileEncoding>());

        Assert.Equal(paths, results.Select(result => result.FilePath).ToArray());
    }

    [Fact]
    public async Task SearchFiles_AdaptiveScheduling_InterleavesClusteredUncHosts()
    {
        var paths = Enumerable.Range(1, 6)
            .Select(index => UncPath("server-a", "share", $"a{index}.log"))
            .Concat(Enumerable.Range(1, 2)
                .Select(index => UncPath("server-b", "share", $"b{index}.log")))
            .ToArray();
        var startHosts = new ConcurrentQueue<string>();
        var service = new SearchService(async (filePath, _, _, ct) =>
        {
            startHosts.Enqueue(GetUncHost(filePath));
            await Task.Delay(75, ct);
            return new SearchResult { FilePath = filePath };
        });
        var request = new SearchRequest { Query = "needle", FilePaths = paths.ToList() };

        var results = await service.SearchFilesAsync(request, new Dictionary<string, FileEncoding>());

        var startedHosts = startHosts.ToArray();
        var firstServerBStart = Array.FindIndex(
            startedHosts,
            host => string.Equals(host, "server-b", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(paths, results.Select(result => result.FilePath).ToArray());
        Assert.InRange(firstServerBStart, 0, 5);
        Assert.True(
            startedHosts.Take(firstServerBStart).Count(host => string.Equals(host, "server-a", StringComparison.OrdinalIgnoreCase)) < 6);
    }

    [Fact]
    public async Task SearchFiles_AdaptiveScheduling_OneUncShareStaysBounded()
    {
        var paths = Enumerable.Range(1, 6)
            .Select(index => UncPath("server", "share", $"file{index}.log"))
            .ToArray();
        var activeCount = 0;
        var maxActiveCount = 0;
        var service = new SearchService(async (filePath, _, _, ct) =>
        {
            var active = Interlocked.Increment(ref activeCount);
            UpdateMaxObserved(ref maxActiveCount, active);
            try
            {
                await Task.Delay(75, ct);
                return new SearchResult { FilePath = filePath };
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
            }
        });
        var request = new SearchRequest { Query = "needle", FilePaths = paths.ToList() };

        await service.SearchFilesAsync(request, new Dictionary<string, FileEncoding>());

        Assert.Equal(2, maxActiveCount);
    }

    [Fact]
    public async Task SearchFiles_AdaptiveScheduling_InterleavesClusteredUncShares()
    {
        var paths = Enumerable.Range(1, 6)
            .Select(index => UncPath("server", "share-a", $"a{index}.log"))
            .Concat(Enumerable.Range(1, 2)
                .Select(index => UncPath("server", "share-b", $"b{index}.log")))
            .ToArray();
        var activeCount = 0;
        var maxActiveCount = 0;
        var activeCountByShare = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var maxActiveCountByShare = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var service = new SearchService(async (filePath, _, _, ct) =>
        {
            var share = GetUncShare(filePath);
            var active = Interlocked.Increment(ref activeCount);
            var activeForShare = activeCountByShare.AddOrUpdate(share, 1, (_, count) => count + 1);
            UpdateMaxObserved(ref maxActiveCount, active);
            maxActiveCountByShare.AddOrUpdate(
                share,
                activeForShare,
                (_, currentMax) => Math.Max(currentMax, activeForShare));
            try
            {
                await Task.Delay(75, ct);
                return new SearchResult { FilePath = filePath };
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
                activeCountByShare.AddOrUpdate(share, 0, (_, count) => count - 1);
            }
        });
        var request = new SearchRequest { Query = "needle", FilePaths = paths.ToList() };

        var results = await service.SearchFilesAsync(request, new Dictionary<string, FileEncoding>());

        Assert.Equal(paths, results.Select(result => result.FilePath).ToArray());
        Assert.Equal(3, maxActiveCount);
        Assert.Equal(2, maxActiveCountByShare["share-a"]);
        Assert.True(maxActiveCountByShare["share-b"] <= 2);
    }

    [Fact]
    public async Task SearchFiles_AdaptiveScheduling_MultipleUncHostsCanExceedOldFixedDefault()
    {
        var paths = Enumerable.Range(1, 5)
            .SelectMany(hostIndex => new[]
            {
                UncPath($"server{hostIndex}", "share", $"a{hostIndex}.log"),
                UncPath($"server{hostIndex}", "share", $"b{hostIndex}.log")
            })
            .ToArray();
        var activeCount = 0;
        var maxActiveCount = 0;
        var service = new SearchService(async (filePath, _, _, ct) =>
        {
            var active = Interlocked.Increment(ref activeCount);
            UpdateMaxObserved(ref maxActiveCount, active);
            try
            {
                await Task.Delay(75, ct);
                return new SearchResult { FilePath = filePath };
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
            }
        });
        var request = new SearchRequest { Query = "needle", FilePaths = paths.ToList() };

        await service.SearchFilesAsync(request, new Dictionary<string, FileEncoding>());

        Assert.True(maxActiveCount > 4);
    }

    [Fact]
    public async Task SearchFiles_AdaptiveScheduling_PreCanceledTokenCancelsPendingWork()
    {
        var startedCount = 0;
        var service = new SearchService((filePath, _, _, _) =>
        {
            Interlocked.Increment(ref startedCount);
            return Task.FromResult(new SearchResult { FilePath = filePath });
        });
        var request = new SearchRequest
        {
            Query = "needle",
            FilePaths = new List<string>
            {
                UncPath("server", "share", "one.log"),
                UncPath("server", "share", "two.log")
            }
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SearchFilesAsync(request, new Dictionary<string, FileEncoding>(), cts.Token));
        Assert.Equal(0, startedCount);
    }

    [Fact]
    public async Task SearchFiles_AdaptiveScheduling_ReturnsWhenCancellationIsHandledByClaimedSearches()
    {
        var paths = new[]
        {
            UncPath("server-a", "share", "one.log"),
            UncPath("server-b", "share", "two.log")
        };
        using var cts = new CancellationTokenSource();
        var startedCount = 0;
        var service = new SearchService((filePath, _, _, _) =>
        {
            if (Interlocked.Increment(ref startedCount) == paths.Length)
                cts.Cancel();

            return Task.FromResult(new SearchResult { FilePath = filePath });
        });
        var request = new SearchRequest { Query = "needle", FilePaths = paths.ToList() };

        var results = await service.SearchFilesAsync(request, new Dictionary<string, FileEncoding>(), cts.Token);

        Assert.Equal(paths, results.Select(result => result.FilePath).ToArray());
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
    public async Task Search_LongLine_PreservesFullLineTextAndMatchPosition()
    {
        var prefix = new string('a', 2_100);
        var line = prefix + "needle suffix";
        var path = await CreateTestFile("long-line.log", line + "\n");
        var request = new SearchRequest { Query = "needle", FilePaths = new List<string> { path } };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(line, hit.LineText);
        Assert.Equal(prefix.Length, hit.MatchStart);
        Assert.Equal("needle".Length, hit.MatchLength);
    }

    [Fact]
    public async Task SearchFileRangeAsync_LongLine_PreservesFullLineTextAndMatchPosition()
    {
        var prefix = new string('a', 2_100);
        var line = prefix + "needle suffix";
        var path = await CreateTestFile("long-range-line.log", line + "\n");
        var request = new SearchRequest
        {
            Query = "needle",
            FilePaths = new List<string> { path },
            StartLineNumber = 1,
            EndLineNumber = 1
        };

        var result = await _searchService.SearchFileRangeAsync(
            path,
            request,
            FileEncoding.Utf8,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { line }));

        var hit = Assert.Single(result.Hits);
        Assert.Equal(line, hit.LineText);
        Assert.Equal(prefix.Length, hit.MatchStart);
        Assert.Equal("needle".Length, hit.MatchLength);
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
        Assert.True(result.WasCancelled);
        Assert.False(result.IsEvaluationComplete);
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

        var hit = Assert.Single(result.Hits);
        Assert.Equal(0, hit.MatchStart);
        Assert.Equal(3, hit.Matches.Count);
        Assert.Equal(new[] { 0, 6, 12 }, hit.Matches.Select(match => match.MatchStart).ToArray());
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
    public async Task RegexSearch_InvalidPatternOnEmptyFile_ReturnsError()
    {
        var path = await CreateTestFile("invalid-regex-empty.log", string.Empty);
        var request = new SearchRequest
        {
            Query = "[invalid",
            IsRegex = true,
            FilePaths = new List<string> { path }
        };

        var result = await _searchService.SearchFileAsync(path, request, FileEncoding.Utf8);

        Assert.False(string.IsNullOrWhiteSpace(result.Error));
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

    private static string UncPath(string host, string share, string fileName)
        => $@"\\{host}\{share}\{fileName}";

    private static SearchTimestampBucketDefinition DatedBucket(
        int index,
        DateTimeOffset start,
        DateTimeOffset endExclusive)
        => new(
            index,
            start.ToString("O", CultureInfo.InvariantCulture),
            endExclusive.ToString("O", CultureInfo.InvariantCulture),
            start.UtcTicks,
            endExclusive.UtcTicks);

    private static SearchTimestampBucketDefinition TimeOfDayBucket(
        int index,
        TimeSpan start,
        TimeSpan endExclusive)
        => new(
            index,
            start.ToString("c", CultureInfo.InvariantCulture),
            endExclusive.ToString("c", CultureInfo.InvariantCulture),
            start.Ticks,
            endExclusive.Ticks);

    private static string GetUncHost(string filePath)
    {
        var trimmed = filePath.TrimStart('\\');
        var separator = trimmed.IndexOf('\\', StringComparison.Ordinal);
        return separator < 0 ? trimmed : trimmed[..separator];
    }

    private static string GetUncShare(string filePath)
    {
        var trimmed = filePath.TrimStart('\\');
        var hostSeparator = trimmed.IndexOf('\\', StringComparison.Ordinal);
        if (hostSeparator < 0)
            return string.Empty;

        var shareStart = hostSeparator + 1;
        var shareSeparator = trimmed.IndexOf('\\', shareStart);
        return shareSeparator < 0
            ? trimmed[shareStart..]
            : trimmed[shareStart..shareSeparator];
    }

    private static void UpdateMaxObserved(ref int maxObserved, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maxObserved);
            if (value <= current)
                return;

            if (Interlocked.CompareExchange(ref maxObserved, value, current) == current)
                return;
        }
    }
}
