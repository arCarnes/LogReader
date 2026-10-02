namespace LogReader.Core.Tests;

using System.Text.Json;
using System.Diagnostics;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;
using LogReader.Mcp;
using ModelContextProtocol.Protocol;

public sealed partial class HeadlessLogQueryBackendTests
{
    [Theory]
    [InlineData("catalog")]
    [InlineData("admission")]
    [InlineData("continuation")]
    public async Task ResumableCount_EarlyDeadlinePreservesContinuation(string blockedPhase)
    {
        var path = await CreateFileAsync("deadline-resume.log", "needle needle\nneedle\nneedle");
        var snapshot = CreateSnapshot(("file", path));
        var catalog = new PausingCatalogReader(snapshot);
        using var backend = CreateBackend(snapshot, catalogReader: catalog,
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 15, MaximumConcurrentDiskOperations = 1 });
        LogCountQuery Query(string? cursor, int? timeout = null) => new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle",
            Cursor = cursor, TimeoutMilliseconds = timeout
        };
        var first = await backend.CountLogsAsync(Query(null));
        Assert.Empty(first.Errors);
        Assert.Equal(1, first.Result!.MatchingLineCount);
        Assert.Equal(2, first.Result.MatchOccurrenceCount);
        var cursor = Assert.IsType<string>(first.Result.NextCursor);
        SemaphoreSlim? admission = null;
        QueryContinuationStore.Lease? heldContinuation = null;
        try
        {
            if (blockedPhase == "catalog")
                catalog.BlockReads = true;
            else if (blockedPhase == "admission")
            {
                admission = (SemaphoreSlim)typeof(HeadlessLogQueryBackend)
                    .GetField("_heavyRequestGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(backend)!;
                await admission.WaitAsync();
            }
            else
            {
                // Hold the real session lease without adding a production-only testing API.
                var store = (QueryContinuationStore)typeof(HeadlessLogQueryBackend)
                    .GetField("_continuations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(backend)!;
                var sessions = (Dictionary<string, QueryContinuationStore.Session>)typeof(QueryContinuationStore)
                    .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(store)!;
                var session = Assert.Single(sessions.Values);
                heldContinuation = await store.AcquireAsync(cursor, "count", session.Fingerprint, snapshot.Revision, default);
            }
            var timedOut = await backend.CountLogsAsync(Query(cursor, 50)).WaitAsync(TimeSpan.FromSeconds(5));
            var error = Assert.Single(timedOut.Errors);
            Assert.Equal("deadline_exceeded", error.Code);
            Assert.True(error.IsRetryable);
            Assert.Null(timedOut.Result);
        }
        finally
        {
            catalog.BlockReads = false;
            admission?.Release();
            heldContinuation?.Dispose();
        }

        var resumed = await backend.CountLogsAsync(Query(cursor));
        Assert.Empty(resumed.Errors);
        Assert.True(resumed.Result!.MatchingLineCount >= first.Result.MatchingLineCount);
        var replay = await backend.CountLogsAsync(Query(cursor));
        Assert.Equal(JsonSerializer.Serialize(resumed), JsonSerializer.Serialize(replay));
        for (var slice = 0; resumed.Result!.NextCursor != null && slice < 10; slice++)
        {
            resumed = await backend.CountLogsAsync(Query(resumed.Result.NextCursor));
            Assert.Empty(resumed.Errors);
        }
        Assert.Null(resumed.Result.NextCursor);
        Assert.True(resumed.Result.IsComplete);
        Assert.True(resumed.Result.IsTraversalComplete);
        Assert.Equal(3, resumed.Result.MatchingLineCount);
        Assert.Equal(4, resumed.Result.MatchOccurrenceCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumableCount_InitialEarlyDeadlineStillReturnsZeroCounts(bool blockAdmission)
    {
        var snapshot = CreateSnapshot();
        var catalog = new PausingCatalogReader(snapshot) { BlockReads = !blockAdmission };
        using var backend = CreateBackend(snapshot, catalogReader: catalog,
            limits: LogQueryEffectiveLimits.Default with { MaximumConcurrentDiskOperations = 1 });
        SemaphoreSlim? admission = null;
        try
        {
            if (blockAdmission)
            {
                admission = (SemaphoreSlim)typeof(HeadlessLogQueryBackend)
                    .GetField("_heavyRequestGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(backend)!;
                await admission.WaitAsync();
            }
            var response = await backend.CountLogsAsync(new LogCountQuery
            {
                Targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard")], Query = "needle",
                TimeoutMilliseconds = 50
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("deadline_exceeded", Assert.Single(response.Errors).Code);
            Assert.True(response.IsPartial);
            Assert.NotNull(response.Result);
            Assert.False(response.Result.IsComplete);
            Assert.False(response.Result.IsTraversalComplete);
            Assert.Equal(0, response.Result.MatchingLineCount);
            Assert.Contains("deadline_exceeded", response.Result.IncompleteReasons);
        }
        finally
        {
            admission?.Release();
        }
    }

    private sealed class PausingCatalogReader(ConfiguredLogCatalogSnapshot snapshot) : IConfiguredLogCatalogReader
    {
        public bool BlockReads { get; set; }
        public async Task<ConfiguredLogCatalogReadResult> ReadAsync(CancellationToken ct = default)
        {
            if (BlockReads)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ConfiguredLogCatalogReadResult.Success(snapshot);
        }
    }
    [Fact]
    public async Task ResumableQueries_CancellationDuringWorkRollsBackAndTimeBudgetYields()
    {
        var path = await CreateFileAsync("cancel-work.log", string.Join('\n', Enumerable.Repeat("needle needle", 200)));
        using var cancellation = new CancellationTokenSource();
        var cancelDuringWork = false;
        var samples = 0;
        long Clock()
        {
            if (cancelDuringWork && ++samples == 20)
                cancellation.Cancel();
            return Stopwatch.GetTimestamp();
        }
        using var backend = CreateBackend(CreateSnapshot(("file", path)), scanTimestamp: Clock,
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 100 });
        LogSearchQuery Query(string? cursor) => new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", Cursor = cursor, ResultMode = "countsOnly"
        };
        var first = await backend.SearchLogsAsync(Query(null));
        cancelDuringWork = true;
        var rejected = await backend.SearchLogsAsync(Query(first.Result!.NextCursor), cancellation.Token);
        Assert.Equal("request_cancelled", Assert.Single(rejected.Errors).Code);
        cancelDuringWork = false;
        var resumed = await backend.SearchLogsAsync(Query(first.Result.NextCursor));
        Assert.Empty(resumed.Errors);
        var replay = await backend.SearchLogsAsync(Query(first.Result.NextCursor));
        Assert.Equal(JsonSerializer.Serialize(resumed), JsonSerializer.Serialize(replay));

        var timestamp = 0L;
        using var timed = CreateBackend(CreateSnapshot(("file", path)),
            scanTimestamp: () => timestamp += Math.Max(1, Stopwatch.Frequency / 100_000),
            limits: LogQueryEffectiveLimits.Default with { SearchWorkMilliseconds = 1 });
        var response = await timed.SearchLogsAsync(Query(null));
        Assert.Empty(response.Errors);
        Assert.Equal("time_slice", response.Result!.StopReason);
        Assert.NotNull(response.Result.NextCursor);
    }

    [Fact]
    public async Task ResumableQueries_ConcurrentReplayAndCancelledAdvancePreserveCheckpoint()
    {
        var path = await CreateFileAsync("concurrent-resume.log", "needle\nneedle\nneedle");
        using var backend = CreateBackend(CreateSnapshot(("file", path)),
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 3 });
        LogSearchQuery Query(string? cursor, int? timeout = null) => new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", Cursor = cursor,
            TimeoutMilliseconds = timeout
        };
        var initial = await backend.SearchLogsAsync(Query(null));
        var cursor = initial.Result!.NextCursor;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var abandoned = await backend.SearchLogsAsync(Query(cursor), cancelled.Token);
        Assert.Equal("request_cancelled", Assert.Single(abandoned.Errors).Code);
        var concurrent = await Task.WhenAll(backend.SearchLogsAsync(Query(cursor, 1000)),
            backend.SearchLogsAsync(Query(cursor, 2000)));
        Assert.Empty(concurrent[0].Errors);
        Assert.Equal(JsonSerializer.Serialize(concurrent[0]), JsonSerializer.Serialize(concurrent[1]));
        Assert.NotNull(concurrent[0].Result!.NextCursor);
        var another = await backend.SearchLogsAsync(Query(concurrent[0].Result!.NextCursor));
        Assert.Empty(another.Errors);
        var stale = await backend.SearchLogsAsync(Query(cursor));
        Assert.Equal("stale_search_cursor", Assert.Single(stale.Errors).Code);
    }

    [Fact]
    public async Task ResumableQueries_ExpiryAndCapacityAreBoundedWithoutEviction()
    {
        var path = await CreateFileAsync("expire-resume.log", "needle\nneedle");
        var now = DateTimeOffset.UtcNow;
        using var backend = CreateBackend(CreateSnapshot(("file", path)), now: () => now,
            limits: LogQueryEffectiveLimits.Default with
            {
                SearchScanBytes = 3, MaximumContinuationSessions = 1, ContinuationIdleMilliseconds = 100
            });
        LogSearchQuery Query(string? cursor) => new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", Cursor = cursor
        };
        var first = await backend.SearchLogsAsync(Query(null));
        var rejected = await backend.SearchLogsAsync(Query(null));
        Assert.Equal("continuation_capacity_exceeded", Assert.Single(rejected.Errors).Code);
        var valid = await backend.SearchLogsAsync(Query(first.Result!.NextCursor));
        Assert.Empty(valid.Errors);
        now = now.AddMilliseconds(101);
        var expired = await backend.SearchLogsAsync(Query(valid.Result!.NextCursor));
        Assert.Equal("expired_search_cursor", Assert.Single(expired.Errors).Code);
        Assert.Empty((await backend.SearchLogsAsync(Query(null))).Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumableQueries_AbsentOrRegexMatchesFinishExactly(bool regex)
    {
        var path = await CreateFileAsync("regex-resume.log", "needle needle\nother\nneedle");
        using var backend = CreateBackend(CreateSnapshot(("file", path)),
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 5 });
        string? cursor = null;
        LogSearchResult? result = null;
        do
        {
            var response = await backend.SearchLogsAsync(new LogSearchQuery
            {
                Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = regex ? "n[a-z]+" : "absent",
                UseRegex = regex, ResultMode = "countsOnly", Cursor = cursor
            });
            Assert.Empty(response.Errors);
            result = response.Result!;
            cursor = result.NextCursor;
        } while (cursor != null);
        Assert.True(result.IsQueryComplete);
        Assert.Equal(regex ? 2 : 0, result.MatchingLineCount);
        Assert.Equal(regex ? 3 : 0, result.MatchOccurrenceCount);
    }

    [Fact]
    public async Task ResumableQueries_OversizedFileTerminatesAndNextFileCompletes()
    {
        var huge = await CreateFileAsync("oversized-resume.log", "needle line too large");
        var valid = await CreateFileAsync("valid-resume.log", "needle");
        using var backend = CreateBackend(CreateSnapshot(("huge", huge), ("valid", valid)),
            limits: LogQueryEffectiveLimits.Default with { MaximumSearchLineBytes = 8 });
        var response = await backend.CountLogsAsync(new LogCountQuery
        {
            Targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard")], Query = "needle"
        });
        Assert.Empty(response.Errors);
        Assert.True(response.Result!.IsTraversalComplete);
        Assert.False(response.Result.IsComplete);
        Assert.Equal(1, response.Result.MatchingLineCount);
        Assert.Equal(1, response.Result.FailedFileCount);
        Assert.Equal("log_line_too_large", response.Result.Files[0].Error!.Code);
        Assert.True(response.Result.Files[1].IsCountExact);
    }

    [Fact]
    public async Task ResumableQueries_TwoThousandCandidatesCompleteWithSmallCursorsAndNoDuplicateAliases()
    {
        var path = await CreateFileAsync("many-resume.log", "needle");
        var candidates = Enumerable.Range(0, 2000).Select(index => ($"file-{index}", path)).ToArray();
        using var backend = CreateBackend(CreateSnapshot(candidates));
        var response = await backend.CountLogsAsync(new LogCountQuery
        {
            Targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard")], Query = "needle"
        });
        Assert.Empty(response.Errors);
        Assert.True(response.Result!.IsTraversalComplete);
        Assert.True(response.Result.IsComplete);
        Assert.Equal(2000, response.Result.SelectedFileCount);
        Assert.Equal(1, response.Result.SearchedFileCount);
        Assert.Equal(1, response.Result.MatchingLineCount);
    }

    [Fact]
    public async Task ResumableCount_RelativeWindowRemainsFrozenAcrossMidnight()
    {
        var path = await CreateFileAsync("relative-resume.log", "2026-10-01T23:59:00Z needle");
        var now = new DateTimeOffset(2026, 10, 1, 23, 59, 30, TimeSpan.Zero);
        using var backend = CreateBackend(CreateSnapshot(("file", path)), now: () => now,
            localTimeZone: TimeZoneInfo.Utc, limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 4 });
        LogCountQuery Query(string? cursor) => new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle",
            RelativeWindow = "today", BucketSize = "hour", Cursor = cursor
        };
        var response = await backend.CountLogsAsync(Query(null));
        var range = response.Result!.ResolvedTimeRange;
        now = now.AddMinutes(1);
        while (response.Result!.NextCursor != null)
        {
            response = await backend.CountLogsAsync(Query(response.Result.NextCursor));
            Assert.Empty(response.Errors);
            Assert.Equal(range, response.Result!.ResolvedTimeRange);
        }
        Assert.True(response.Result.IsComplete);
        Assert.Equal(1, response.Result.MatchingLineCount);
    }

    [Theory]
    [InlineData("samples")]
    [InlineData("matchesOnly")]
    [InlineData("countsOnly")]
    public async Task ResumableSearch_ExhaustsTinySlicesWithoutDuplicateHitsOrCounts(string mode)
    {
        var path = await CreateFileAsync("resume.log", "before\nneedle needle\nother\nneedle\nafter\nneedle");
        var cache = CreateCache();
        using var backend = CreateBackend(CreateSnapshot(("file", path)), cache: cache,
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 7 });
        string? cursor = null;
        var hits = new List<long>();
        var lines = new HashSet<long>();
        long pageLines = 0;
        long pageOccurrences = 0;
        LogSearchResult? result = null;
        for (var page = 0; page < 100; page++)
        {
            var response = await backend.SearchLogsAsync(new LogSearchQuery
            {
                Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", ResultMode = mode,
                Cursor = cursor, MaxHitsPerFile = 1, IncludeContextBefore = 1, IncludeContextAfter = 1
            });
            Assert.Empty(response.Errors);
            result = Assert.IsType<LogSearchResult>(response.Result);
            hits.AddRange(result.Files.SelectMany(static file => file.Hits).Select(static hit => hit.LineNumber));
            foreach (var line in result.Files.SelectMany(static file => file.Excerpts).SelectMany(static excerpt => excerpt.Lines))
                lines.Add(line.LineNumber);
            pageLines += result.PageMatchingLineCount;
            pageOccurrences += result.PageMatchOccurrenceCount;
            Assert.DoesNotContain(_testDirectory, result.NextCursor ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.True((result.NextCursor?.Length ?? 0) < 1024);
            cursor = result.NextCursor;
            if (cursor == null)
                break;
        }
        Assert.NotNull(result);
        Assert.True(result.IsTraversalComplete);
        Assert.True(result.IsQueryComplete);
        Assert.Equal(3, result.MatchingLineCount);
        Assert.Equal(4, result.MatchOccurrenceCount);
        Assert.Equal(3, pageLines);
        Assert.Equal(4, pageOccurrences);
        Assert.Equal(1, result.SearchedFileCount);
        Assert.Equal(1, result.MatchedFileCount);
        Assert.Equal(0, result.RemainingFileCount);
        Assert.Equal("scope_exhausted", result.StopReason);
        Assert.Equal(mode == "countsOnly" ? [] : new long[] { 2, 4, 6 }, hits);
        if (mode == "samples")
            Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, lines.Order());
        Assert.Equal(0, cache.GetSnapshot().RetainedSessions);
    }

    [Fact]
    public async Task ResumableCount_BucketsAndRetryAreCumulativeReplacements()
    {
        var path = await CreateFileAsync("resume-count.log", "2026-10-01 10:00:00 needle needle\n2026-10-01 10:01:00 needle");
        using var backend = CreateBackend(CreateSnapshot(("file", path)),
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 11 });
        string? cursor = null;
        LogCountResult? result = null;
        for (var page = 0; page < 100; page++)
        {
            LogCountQuery Query(string? value) => new()
            {
                Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", Cursor = value,
                StartTimestamp = "2026-10-01 10:00:00", EndTimestamp = "2026-10-01 10:02:00", BucketSize = "minute"
            };
            var response = await backend.CountLogsAsync(Query(cursor));
            Assert.Empty(response.Errors);
            result = Assert.IsType<LogCountResult>(response.Result);
            if (cursor != null)
            {
                var replay = await backend.CountLogsAsync(Query(cursor));
                Assert.Equal(JsonSerializer.Serialize(response), JsonSerializer.Serialize(replay));
            }
            cursor = result.NextCursor;
            if (cursor == null)
                break;
        }
        Assert.True(result!.IsComplete);
        Assert.True(result.IsTraversalComplete);
        Assert.Equal(2, result.MatchingLineCount);
        Assert.Equal(3, result.MatchOccurrenceCount);
        Assert.Equal(2, result.Buckets.Sum(static bucket => bucket.MatchingLineCount));
        Assert.Equal(3, result.Buckets.Sum(static bucket => bucket.MatchOccurrenceCount));
        Assert.Equal(1, result.SearchedFileCount);
        Assert.Single(result.Files);
    }

    [Theory]
    [InlineData("samples", true)]
    [InlineData("matchesOnly", true)]
    [InlineData("countsOnly", true)]
    [InlineData("samples", false)]
    [InlineData("matchesOnly", false)]
    public async Task ResumableSearch_LineLimitReportsTruncationWithoutInvalidatingCounts(string mode, bool longLine)
    {
        var text = longLine ? new string('x', 5000) + "needle" + new string('x', 5000) : "needle";
        var path = await CreateFileAsync("line-limit-resume.log", text);
        var shortPath = await CreateFileAsync("short-line-resume.log", "needle");
        using var backend = CreateBackend(CreateSnapshot(("file", path), ("short", shortPath)));
        var response = await backend.SearchLogsAsync(new LogSearchQuery
        {
            Targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard")], Query = "needle", ResultMode = mode
        });
        var truncated = longLine && mode != "countsOnly";
        Assert.Empty(response.Errors);
        Assert.Equal(truncated, response.IsTruncated);
        Assert.Equal(truncated ? new[] { "line_character_limit" } : [], response.TruncationReasons);
        Assert.True(response.Result!.IsQueryComplete);
        Assert.True(response.Result.IsPageComplete);
        Assert.False(response.IsPartial);
        Assert.Equal(2, response.Result.MatchingLineCount);
        Assert.Equal(2, response.Result.MatchOccurrenceCount);
        var file = response.Result.Files[0];
        Assert.Equal(truncated, file.IsTruncated);
        Assert.False(response.Result.Files[1].IsTruncated);
        Assert.All(response.Result.Files, result =>
        {
            Assert.True(result.IsCountExact);
            Assert.Empty(result.IncompleteReasons);
        });
        if (mode == "countsOnly")
        {
            Assert.Empty(file.Hits);
            Assert.Empty(file.Excerpts);
        }
        else
        {
            var hit = Assert.Single(file.Hits);
            var line = Assert.Single(Assert.Single(file.Excerpts).Lines);
            Assert.Equal(truncated, line.IsTruncated);
            Assert.True(line.Text.Length <= LogQueryEffectiveLimits.Default.MaximumCharactersPerLine);
            Assert.Equal("needle", line.Text.Substring(hit.MatchStart, hit.MatchLength));
        }

        var wire = await new McpLogTools(backend).SearchLogsAsync(
            [new(ConfiguredLogTargetKind.Dashboard, "dashboard")], "needle", resultMode: mode);
        var envelope = wire.StructuredContent!.Value;
        Assert.Equal(envelope.GetRawText(), Assert.IsType<TextContentBlock>(Assert.Single(wire.Content)).Text);
        Assert.Equal(truncated, envelope.GetProperty("isTruncated").GetBoolean());
        var wireFile = envelope.GetProperty("result").GetProperty("files")[0];
        Assert.Equal(truncated, wireFile.TryGetProperty("isTruncated", out var fileTruncated));
        if (truncated)
        {
            Assert.True(fileTruncated.GetBoolean());
            Assert.Equal("line_character_limit", Assert.Single(envelope.GetProperty("truncationReasons").EnumerateArray()).GetString());
            Assert.True(wireFile.GetProperty("excerpts")[0].GetProperty("lines")[0].GetProperty("isTruncated").GetBoolean());
        }
        else
            Assert.False(envelope.TryGetProperty("truncationReasons", out _));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public async Task ResumableSearch_ContextLineLimitReportsTruncation(int before, int after)
    {
        var context = new string('x', 5000);
        var path = await CreateFileAsync("context-limit-resume.log", context + "\nneedle\n" + context);
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var response = await backend.SearchLogsAsync(new LogSearchQuery
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle",
            IncludeContextBefore = before, IncludeContextAfter = after
        });
        Assert.Empty(response.Errors);
        Assert.True(response.IsTruncated);
        Assert.Equal(new[] { "line_character_limit" }, response.TruncationReasons);
        Assert.True(response.Result!.IsQueryComplete);
        Assert.True(response.Result.IsPageComplete);
        Assert.Equal(1, response.Result.MatchingLineCount);
        var file = Assert.Single(response.Result.Files);
        Assert.True(file.IsTruncated);
        Assert.True(file.IsCountExact);
        Assert.Empty(file.IncompleteReasons);
        var lines = Assert.Single(file.Excerpts).Lines;
        Assert.Equal(1 + before + after, lines.Length);
        foreach (var line in lines)
        {
            Assert.Equal(line.LineNumber != 2, line.IsTruncated);
            Assert.True(line.Text.Length <= LogQueryEffectiveLimits.Default.MaximumCharactersPerLine);
        }
        var hit = Assert.Single(file.Hits);
        Assert.Equal("needle", lines.Single(line => line.LineNumber == hit.LineNumber).Text.Substring(hit.MatchStart, hit.MatchLength));

        var wire = await new McpLogTools(backend).SearchLogsAsync(
            [new(ConfiguredLogTargetKind.LogFile, "file")], "needle", includeContextBefore: before, includeContextAfter: after);
        var envelope = wire.StructuredContent!.Value;
        Assert.Equal(envelope.GetRawText(), Assert.IsType<TextContentBlock>(Assert.Single(wire.Content)).Text);
        Assert.True(envelope.GetProperty("isTruncated").GetBoolean());
        Assert.Equal("line_character_limit", Assert.Single(envelope.GetProperty("truncationReasons").EnumerateArray()).GetString());
        Assert.True(envelope.GetProperty("result").GetProperty("files")[0].GetProperty("isTruncated").GetBoolean());
    }

    [Fact]
    public async Task ResumableSearch_ResponseBudgetTruncationKeepsFileEvidenceLocal()
    {
        var longPath = await CreateFileAsync("response-long-resume.log", "needle" + new string('x', 2000));
        var shortPath = await CreateFileAsync("response-short-resume.log", "needle");
        using var backend = CreateBackend(CreateSnapshot(("long", longPath), ("short", shortPath)),
            limits: LogQueryEffectiveLimits.Default with { MaximumResponseCharacters = 1000 });
        LogSearchQuery Query(string? cursor) => new()
        {
            Targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard")], Query = "needle", Cursor = cursor
        };
        var first = await backend.SearchLogsAsync(Query(null));
        Assert.Empty(first.Errors);
        Assert.True(first.IsTruncated);
        Assert.Contains("response_text_limit", first.TruncationReasons);
        Assert.DoesNotContain("line_character_limit", first.TruncationReasons);
        Assert.True(first.Result!.Files[0].IsTruncated);
        Assert.False(first.Result.Files[1].IsTruncated);
        Assert.Equal("response_limit", first.Result.StopReason);
        var final = await backend.SearchLogsAsync(Query(first.Result.NextCursor));
        Assert.Empty(final.Errors);
        Assert.True(final.Result!.IsQueryComplete);
        Assert.Equal(2, final.Result.MatchingLineCount);
        Assert.False(final.IsTruncated);
        Assert.Empty(final.TruncationReasons);
    }
    [Fact]
    public async Task ResumableSearch_ResponseBudgetReturnsEveryHitOnLaterPages()
    {
        var path = await CreateFileAsync("response-resume.log", "needle one\nneedle two\nneedle three");
        using var backend = CreateBackend(CreateSnapshot(("file", path)),
            limits: LogQueryEffectiveLimits.Default with { MaximumResponseCharacters = 10 });
        string? cursor = null;
        var hits = new List<long>();
        do
        {
            var response = await backend.SearchLogsAsync(new LogSearchQuery
            {
                Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", Cursor = cursor
            });
            Assert.Empty(response.Errors);
            hits.AddRange(response.Result!.Files.SelectMany(static file => file.Hits).Select(static hit => hit.LineNumber));
            cursor = response.Result.NextCursor;
            if (cursor == null)
                Assert.True(response.Result.IsQueryComplete);
        } while (cursor != null && hits.Count < 10);
        Assert.Equal(new long[] { 1, 2, 3 }, hits);
        Assert.Null(cursor);
    }

    [Fact]
    public async Task ResumableSearch_AppendIsExcludedAndMakesFinalCountsUnverified()
    {
        var path = await CreateFileAsync("append-resume.log", "needle\nneedle");
        using var backend = CreateBackend(CreateSnapshot(("file", path)),
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 3 });
        LogSearchQuery Query(string? cursor) => new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", Cursor = cursor
        };
        var first = await backend.SearchLogsAsync(Query(null));
        Assert.NotNull(first.Result!.NextCursor);
        await File.AppendAllTextAsync(path, "\nneedle appended");
        var cursor = first.Result.NextCursor;
        LogSearchResult? result;
        do
        {
            var response = await backend.SearchLogsAsync(Query(cursor));
            Assert.Empty(response.Errors);
            result = response.Result!;
            cursor = result.NextCursor;
        } while (cursor != null);
        Assert.True(result.IsTraversalComplete);
        Assert.False(result.IsQueryComplete);
        Assert.Equal(2, result.MatchingLineCount);
        Assert.Contains("file_changed_during_search", result.IncompleteReasons);
    }

    [Fact]
    public async Task ResumableSearch_ChangedCatalogRejectsAdvanceAndReplay()
    {
        var path = await CreateFileAsync("catalog-resume.log", "needle\nneedle");
        var catalog = new MutableCatalogReader(CreateSnapshot(("file", path)));
        using var backend = CreateBackend(catalog.Snapshot, catalogReader: catalog,
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 3 });
        LogSearchQuery Query(string? cursor) => new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", Cursor = cursor
        };
        var first = await backend.SearchLogsAsync(Query(null));
        catalog.Snapshot = CreateSnapshot(("file", path + ".changed"));
        var stale = await backend.SearchLogsAsync(Query(first.Result!.NextCursor));
        Assert.Equal("stale_search_cursor", Assert.Single(stale.Errors).Code);
    }

    private sealed class MutableCatalogReader(ConfiguredLogCatalogSnapshot snapshot) : IConfiguredLogCatalogReader
    {
        public ConfiguredLogCatalogSnapshot Snapshot { get; set; } = snapshot;
        public Task<ConfiguredLogCatalogReadResult> ReadAsync(CancellationToken ct = default)
            => Task.FromResult(ConfiguredLogCatalogReadResult.Success(Snapshot));
    }
}
