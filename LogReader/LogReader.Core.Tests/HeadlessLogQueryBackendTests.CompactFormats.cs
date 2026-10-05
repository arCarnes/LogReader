namespace LogReader.Core.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using LogReader.Core.Models;
using LogReader.Mcp;
using ModelContextProtocol;

public sealed partial class HeadlessLogQueryBackendTests
{
    [Theory]
    [InlineData("search_logs")]
    [InlineData("count_logs")]
    public async Task CompactFormats_RealContinuationsAndReplaysCanSwitchPresentation(string toolName)
    {
        var first = await CreateFileAsync("compact-first.log", "2026-10-01T10:00:00Z needle needle\n2026-10-01T10:01:00Z needle");
        var second = await CreateFileAsync("compact-second.log", "2026-10-01T10:02:00Z needle");
        using var backend = CreateBackend(CreateSnapshot(("first", first), ("second", second)),
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 11 }, localTimeZone: TimeZoneInfo.Utc);
        var tools = new McpLogTools(backend);
        ConfiguredLogTarget[] targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard")];
        string? cursor = null;
        JsonElement final = default;
        var pages = 0;
        for (; pages < 100; pages++)
        {
            var input = cursor;
            async Task<JsonElement> CallAsync(bool compact)
            {
                var response = toolName == "search_logs"
                    ? await tools.SearchLogsAsync(targets, "needle", resultMode: "countsOnly", cursor: input, maxFiles: 1,
                        provenanceMode: compact ? "shared" : "inline")
                    : await tools.CountLogsAsync(targets, "needle", cursor: input,
                        startTimestamp: "2026-10-01T10:00:00Z", endTimestamp: "2026-10-01T10:02:59Z", bucketSize: "minute",
                        bucketMode: compact ? "sparse" : "dense", provenanceMode: compact ? "shared" : "inline");
                Assert.NotEqual(true, response.IsError);
                var envelope = response.StructuredContent!.Value;
                if (envelope.TryGetProperty("errors", out var errors))
                    Assert.Empty(errors.EnumerateArray());
                return envelope.GetProperty("result");
            }
            final = await CallAsync(pages % 2 == 0);
            if (input != null)
            {
                var alternate = await CallAsync(pages % 2 != 0);
                Assert.True(JsonNode.DeepEquals(Canonical(final), Canonical(alternate)));
            }
            cursor = final.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            if (cursor == null)
                break;
        }
        Assert.InRange(pages, 1, 99);
        Assert.True(final.GetProperty(toolName == "count_logs" ? "isComplete" : "isQueryComplete").GetBoolean());
        Assert.Equal(3, final.GetProperty("matchingLineCount").GetInt64());
        Assert.Equal(4, final.GetProperty("matchOccurrenceCount").GetInt64());

        static JsonNode Canonical(JsonElement result)
        {
            var node = result.TryGetProperty("provenanceTable", out _)
                ? McpLogToolsTests.ReconstructProvenance(result) : JsonNode.Parse(result.GetRawText())!;
            if (result.TryGetProperty("bucketCounts", out _))
            {
                node["buckets"] = JsonSerializer.SerializeToNode(McpLogToolsTests.ReconstructBuckets(result), McpJsonUtilities.DefaultOptions);
                node.AsObject().Remove("bucketGrid");
                node.AsObject().Remove("bucketCounts");
            }
            node.AsObject().Remove("bucketMode");
            return node;
        }
    }

    [Fact]
    public async Task CompactFormats_RealFilteredTailCursorAcceptsProvenanceChanges()
    {
        var path = await CreateFileAsync("compact-tail.log", "needle\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var tools = new McpLogTools(backend);
        var first = (await tools.ReadLogTailAsync("file", query: "needle")).StructuredContent!.Value.GetProperty("result");
        Assert.True(first.TryGetProperty("provenanceTable", out _));
        await File.AppendAllTextAsync(path, "needle appended\n");
        var next = (await tools.ReadLogTailAsync("file", cursor: first.GetProperty("nextCursor").GetString(),
            query: "needle", provenanceMode: "inline")).StructuredContent!.Value;
        Assert.False(next.TryGetProperty("errors", out _));
        var result = next.GetProperty("result");
        Assert.Equal("needle appended", result.GetProperty("file").GetProperty("lines")[0].GetProperty("text").GetString());
        Assert.True(result.GetProperty("file").TryGetProperty("provenance", out _));
        var idle = (await tools.ReadLogTailAsync("file", cursor: result.GetProperty("nextCursor").GetString(),
            query: "needle", provenanceMode: "shared")).StructuredContent!.Value.GetProperty("result");
        Assert.True(idle.GetProperty("isIdle").GetBoolean());
        Assert.False(idle.TryGetProperty("provenanceTable", out _));
    }
}
