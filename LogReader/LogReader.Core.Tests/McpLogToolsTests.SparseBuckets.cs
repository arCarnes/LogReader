namespace LogReader.Core.Tests;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;
using LogReader.Mcp;
using ModelContextProtocol.Protocol;

public sealed partial class McpLogToolsTests
{
    [Theory]
    [InlineData("minute", "2026-10-04T00:00:00Z", "2026-10-04T00:09:59Z", false)]
    [InlineData("hour", "2026-10-04T00:00:00Z", "2026-10-04T09:59:59Z", false)]
    [InlineData("day", "2026-10-04T00:00:00Z", "2026-10-13T23:59:59Z", false)]
    [InlineData("minute", "2026-10-04T00:00:17Z", "2026-10-04T00:09:03Z", false)]
    [InlineData("hour", "2026-10-04T00:30:00Z", "2026-10-04T09:03:00Z", false)]
    [InlineData("day", "2026-10-04T12:30:00Z", "2026-10-13T09:03:00Z", false)]
    [InlineData("minute", "23:58:17", "23:59:59", false)]
    [InlineData("hour", "09:30", "12:59:59", false)]
    [InlineData("minute", "2026-11-01T00:59:30-04:00", "2026-11-01T02:00:30-05:00", true)]
    [InlineData("hour", "2026-11-01T00:30:00-04:00", "2026-11-01T02:30:00-05:00", true)]
    [InlineData("minute", "2026-03-08T01:58:30-05:00", "2026-03-08T03:01:30-04:00", true)]
    [InlineData("hour", "2026-03-08T01:30:00-05:00", "2026-03-08T03:30:00-04:00", true)]
    [InlineData("day", "2026-03-08T00:00:00-05:00", "2026-03-08T23:59:59-04:00", true)]
    [InlineData("day", "2026-11-01T00:00:00-04:00", "2026-11-01T23:59:59-05:00", true)]
    public async Task SparseBuckets_ReconstructActualBackendBoundaries(
        string size, string start, string end, bool eastern)
    {
        var count = CreateBucketFixture(size, start, end, eastern);
        using var backend = new RecordingBackend { CountResult = count };
        await WithClientAsync(backend, async client =>
        {
            var response = await client.CallToolAsync("count_logs", QueryArguments(false));
            var result = response.StructuredContent!.Value.GetProperty("result");
            Assert.Equal("sparse", result.GetProperty("bucketMode").GetString());
            Assert.False(result.TryGetProperty("buckets", out _));
            Assert.Equal(count.Buckets, ReconstructBuckets(result));
            Assert.Equal(3, result.GetProperty("contractVersion").GetInt32());
            Assert.Equal(JsonSerializer.SerializeToElement(count.ResolvedTimeRange, ModelContextProtocol.McpJsonUtilities.DefaultOptions).GetRawText(),
                result.GetProperty("resolvedTimeRange").GetRawText());
            Assert.True(result.GetProperty("isComplete").GetBoolean());
            AssertWireParity(response);
            // The backend object and version remain unchanged after projection.
            Assert.Equal(3, RecordingBackend.Envelope(count).SchemaVersion);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparseBuckets_ZeroTuplesPreserveCompletenessEvidence(bool complete)
    {
        var fixture = CreateBucketFixture("minute", "2026-10-04T00:00:00Z", "2026-10-04T16:39:59Z", false);
        var count = new LogCountResult
        {
            BucketSize = fixture.BucketSize, ResolvedTimeRange = fixture.ResolvedTimeRange,
            Buckets = fixture.Buckets.Select(bucket => bucket with { MatchingLineCount = 0, MatchOccurrenceCount = 0 }).ToImmutableArray(),
            IsComplete = complete, IsTraversalComplete = complete,
            IncompleteReasons = complete ? [] : ["timeout"], NextCursor = complete ? null : "opaque"
        };
        using var backend = new RecordingBackend { CountResult = count, QueryIsPartial = !complete };
        var response = await new McpLogTools(backend).CountLogsAsync([], "needle");
        var result = response.StructuredContent!.Value.GetProperty("result");
        Assert.Empty(result.GetProperty("bucketCounts").EnumerateArray());
        Assert.Equal(1_000, result.GetProperty("bucketGrid").GetProperty("count").GetInt32());
        Assert.Equal(complete, result.GetProperty("isComplete").GetBoolean());
        Assert.Equal(count.Buckets, ReconstructBuckets(result));
        if (!complete)
        {
            Assert.True(response.StructuredContent.Value.GetProperty("isPartial").GetBoolean());
            Assert.Equal("timeout", result.GetProperty("incompleteReasons")[0].GetString());
            Assert.Equal("opaque", result.GetProperty("nextCursor").GetString());
        }
    }

    [Fact]
    public async Task SparseBuckets_OneNonzeroOfOneThousandSavesAtLeastNinetyFivePercent()
    {
        var count = CreateBucketFixture("minute", "2026-10-04T00:00:00Z", "2026-10-04T16:39:59Z", false);
        using var backend = new RecordingBackend { CountResult = count };
        var tools = new McpLogTools(backend);
        var sparse = (await tools.CountLogsAsync([], "needle")).StructuredContent!.Value.GetProperty("result");
        var dense = (await tools.CountLogsAsync([], "needle", bucketMode: "dense")).StructuredContent!.Value.GetProperty("result");
        Assert.Equal(1_000, dense.GetProperty("buckets").GetArrayLength());
        Assert.Single(sparse.GetProperty("bucketCounts").EnumerateArray());
        Assert.Equal(count.Buckets, ReconstructBuckets(sparse));
        var denseBytes = Encoding.UTF8.GetByteCount(dense.GetProperty("buckets").GetRawText());
        var sparseBytes = Encoding.UTF8.GetByteCount(sparse.GetProperty("bucketGrid").GetRawText()) +
            Encoding.UTF8.GetByteCount(sparse.GetProperty("bucketCounts").GetRawText());
        _output.WriteLine($"Bucket payload: dense={denseBytes}, sparse={sparseBytes}, reduction={1d - (double)sparseBytes / denseBytes:P2} (UTF-8 bytes).");
        Assert.True(sparseBytes <= denseBytes * 0.05);
    }

    [Theory]
    [InlineData("sparse", "bucketCounts")]
    [InlineData("dense", "buckets")]
    public async Task CountBuckets_NoneReturnsModeSpecificEmptyArray(string mode, string property)
    {
        using var backend = new RecordingBackend();
        var response = await new McpLogTools(backend).CountLogsAsync([], "needle", bucketMode: mode);
        var result = response.StructuredContent!.Value.GetProperty("result");
        Assert.Equal(mode, result.GetProperty("bucketMode").GetString());
        Assert.Empty(result.GetProperty(property).EnumerateArray());
        Assert.False(result.TryGetProperty("bucketGrid", out _));
        AssertWireParity(response);
    }

    [Fact]
    public async Task CountBuckets_PresentationCanSwitchOnContinuationAndReplay()
    {
        using var backend = new RecordingBackend
        {
            CountResult = CreateBucketFixture("hour", "2026-10-04T00:00:00Z", "2026-10-04T01:59:59Z", false)
        };
        backend.CountHandler = (_, _) => Task.FromResult(RecordingBackend.Envelope(backend.CountResult));
        await WithClientAsync(backend, async client =>
        {
            var arguments = QueryArguments(true);
            arguments["cursor"] = "opaque-page";
            var sparse = await client.CallToolAsync("count_logs", arguments);
            var originalRequest = JsonSerializer.Serialize(backend.LastCountRequest);
            arguments["bucketMode"] = "dense";
            var dense = await client.CallToolAsync("count_logs", arguments);
            Assert.Equal(originalRequest, JsonSerializer.Serialize(backend.LastCountRequest));
            Assert.Equal("dense", dense.StructuredContent!.Value.GetProperty("result").GetProperty("bucketMode").GetString());
            arguments["bucketMode"] = "sparse";
            var replay = await client.CallToolAsync("count_logs", arguments);
            Assert.Equal(sparse.StructuredContent!.Value.GetRawText(), replay.StructuredContent!.Value.GetRawText());
            Assert.Equal(backend.CountResult.Buckets, ReconstructBuckets(sparse.StructuredContent.Value.GetProperty("result")));
            AssertWireParity(dense);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("SPARSE")]
    [InlineData("secret C:\\private\\file.log")]
    public async Task CountBuckets_InvalidModeIsSanitizedBeforeBackend(string mode)
    {
        using var backend = new RecordingBackend();
        await WithClientAsync(backend, async client =>
        {
            var arguments = QueryArguments(false);
            arguments["bucketMode"] = mode;
            var response = await client.CallToolAsync("count_logs", arguments);
            Assert.True(response.IsError);
            Assert.Null(response.StructuredContent);
            Assert.Equal("bucketMode must be one of: sparse, dense.", Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
            Assert.Null(backend.LastCountRequest);
        });
    }

    private static LogCountResult CreateBucketFixture(string size, string start, string end, bool eastern)
    {
        Assert.True(CountTimeWindowResolver.TryResolve(
            new LogCountQuery { BucketSize = size, StartTimestamp = start, EndTimestamp = end },
            DateTimeOffset.UtcNow, eastern ? TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time") : TimeZoneInfo.Utc,
            LogQueryEffectiveLimits.Default, out var resolution, out var error), error?.Message);
        var definitions = resolution!.AggregationPlan!.Buckets;
        return new LogCountResult
        {
            BucketSize = size, ResolvedTimeRange = resolution.ResolvedRange,
            Buckets = definitions.Select((bucket, index) => new LogCountBucket(
                resolution.ResolvedRange!.Kind, bucket.Start, bucket.EndExclusive,
                index == definitions.Count / 2 ? (long)int.MaxValue + 4 : 0,
                index == definitions.Count / 2 ? (long)int.MaxValue + 7 : 0)).ToImmutableArray(),
            IsComplete = true, IsTraversalComplete = true
        };
    }

    private static ImmutableArray<LogCountBucket> ReconstructBuckets(JsonElement result)
    {
        var grid = result.GetProperty("bucketGrid");
        var kind = grid.GetProperty("kind").GetString()!;
        var count = grid.GetProperty("count").GetInt32();
        var step = grid.GetProperty("stepSeconds").GetInt32();
        var anchors = grid.GetProperty("anchors").EnumerateArray().ToArray();
        Assert.Equal(0, anchors[0][0].GetInt32());
        Assert.Equal(anchors.Select(anchor => anchor[0].GetInt32()).Order(), anchors.Select(anchor => anchor[0].GetInt32()));
        var values = result.GetProperty("bucketCounts").EnumerateArray().ToDictionary(tuple => tuple[0].GetInt32());
        string Boundary(int index)
        {
            var anchor = anchors.Last(anchor => anchor[0].GetInt32() <= index);
            var seconds = (long)(index - anchor[0].GetInt32()) * step;
            return kind == "dated"
                ? DateTimeOffset.Parse(anchor[1].GetString()!, CultureInfo.InvariantCulture).AddSeconds(seconds).ToString("O", CultureInfo.InvariantCulture)
                : TimeSpan.Parse(anchor[1].GetString()!, CultureInfo.InvariantCulture).Add(TimeSpan.FromSeconds(seconds)).ToString("c", CultureInfo.InvariantCulture);
        }
        return Enumerable.Range(0, count).Select(index => new LogCountBucket(
            kind, Boundary(index), Boundary(index + 1),
            values.TryGetValue(index, out var tuple) ? tuple[1].GetInt64() : 0,
            values.TryGetValue(index, out tuple) ? tuple[2].GetInt64() : 0)).ToImmutableArray();
    }

    private static void AssertWireParity(CallToolResult response)
        => Assert.Equal(response.StructuredContent!.Value.GetRawText(), Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
}
