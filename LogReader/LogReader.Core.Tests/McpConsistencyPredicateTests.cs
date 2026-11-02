namespace LogReader.Core.Tests;

using LogReader.Core.Models;
using LogReader.Mcp;

public sealed partial class HeadlessLogQueryBackendTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consistency_PredicatesAgreeAndDiscloseTimestampCoverage(bool regex)
    {
        var path = await CreateFileAsync("predicates.log",
            "10:00:00 needle keep\n10:01:00 needle DROP\nmissing needle\n12:00:00 needle outside\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var targets = new[] { new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file") };
        var search = await backend.SearchLogsAsync(new LogSearchQuery
        {
            Targets = targets, Query = "needle", ExcludeQuery = "drop", UseRegex = regex,
            StartTimestamp = "09:00", EndTimestamp = "11:00", IncludeContextAfter = 1
        });
        var count = await backend.CountLogsAsync(new LogCountQuery
        {
            Targets = targets, Query = "needle", ExcludeQuery = "drop", UseRegex = regex,
            StartTimestamp = "09:00", EndTimestamp = "11:00"
        });
        Assert.Empty(search.Errors);
        Assert.Empty(count.Errors);
        Assert.Equal(1, search.Result!.MatchingLineCount);
        Assert.Equal(search.Result.MatchingLineCount, count.Result!.MatchingLineCount);
        Assert.Equal(new LogTimestampCoverage(4, 1, 1, true), search.Result.TimestampCoverage);
        Assert.Equal(search.Result.TimestampCoverage, count.Result.TimestampCoverage);
        Assert.Contains(SearchLines(Assert.Single(search.Result.Files)), line => line.Text.Contains("DROP"));
        var timeOnly = await backend.SearchLogsAsync(new LogSearchQuery
        {
            Targets = targets, StartTimestamp = "09:00", EndTimestamp = "11:00"
        });
        Assert.Equal(2, timeOnly.Result!.MatchingLineCount);
    }

    [Fact]
    public async Task Consistency_SearchRelativeWindowRemainsFrozenAcrossSlicesAndReplay()
    {
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var path = await CreateFileAsync("relative-search.log", "2026-11-01T11:30:00Z needle\n2026-11-01T11:45:00Z needle\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)), now: () => now,
            localTimeZone: TimeZoneInfo.Utc, limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 20, ContinuationIdleMilliseconds = 172_800_000 });
        LogSearchQuery Query(string? cursor) => new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", RelativeWindow = "last 1h", Cursor = cursor
        };
        var first = await backend.SearchLogsAsync(Query(null));
        var range = first.Result!.ResolvedTimeRange;
        var cursor = first.Result.NextCursor;
        Assert.NotNull(cursor);
        now = now.AddDays(1);
        LogSearchResult result = first.Result;
        for (var index = 0; cursor != null && index < 20; index++)
        {
            var page = await backend.SearchLogsAsync(Query(cursor));
            var replay = await backend.SearchLogsAsync(Query(cursor));
            Assert.Same(page, replay);
            result = page.Result!;
            Assert.Equal(range, result.ResolvedTimeRange);
            cursor = result.NextCursor;
        }
        Assert.True(result.IsQueryComplete);
        Assert.Equal(2, result.MatchingLineCount);
    }

    [Fact]
    public async Task Consistency_BackendRejectionsSetMcpErrorWithoutLosingStructuredDetails()
    {
        using var backend = CreateBackend(CreateSnapshot());
        var result = await new McpLogTools(backend).SearchLogsAsync([], "needle");
        Assert.True(result.IsError);
        Assert.Equal("targets_required", result.StructuredContent!.Value.GetProperty("errors")[0].GetProperty("code").GetString());
    }
}
