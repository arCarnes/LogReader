namespace LogReader.Core.Tests;

using System.Text.Json;
using LogReader.Core.Models;
using LogReader.Mcp;
using ModelContextProtocol.Protocol;

public sealed partial class HeadlessLogQueryBackendTests
{
    [Theory]
    [InlineData("samples")]
    [InlineData("matchesOnly")]
    [InlineData("countsOnly")]
    [InlineData("count")]
    [InlineData("count-hour")]
    public async Task FileGrowth_SnapshotWireResultsRetainEvidenceAndExplicitHandoffUsesFrozenBoundary(string operation)
    {
        var path = await CreateFileAsync("growth-wire.log",
            "2026-11-04T10:00:00Z needle\n2026-11-04T10:01:00Z needle\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)), localTimeZone: TimeZoneInfo.Utc,
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 8 });
        var tools = new McpLogTools(backend);
        var count = operation is "count" or "count-hour";
        var targets = new[] { new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file") };
        var first = Result(await Run(null));
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        await File.AppendAllTextAsync(path, "2026-11-04T10:02:00Z needle appended\n");
        JsonElement result = default;
        string? terminalInput = null;
        var returnedHits = 0;
        var reportedPageGrowth = false;
        for (var page = 0; cursor != null && page < 100; page++)
        {
            terminalInput = cursor;
            result = Result(await Run(cursor));
            if (!count)
            {
                returnedHits += result.GetProperty("returnedHitCount").GetInt32();
                if (result.GetProperty("files").GetArrayLength() > 0)
                {
                    Assert.Contains("file_grew_during_scan", Reasons(result, "pageIncompleteReasons"));
                    reportedPageGrowth = true;
                }
                else
                    Assert.False(result.TryGetProperty("pageIncompleteReasons", out _));
            }
            cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        }
        Assert.Null(cursor);
        Assert.True(result.GetProperty("isTraversalComplete").GetBoolean());
        Assert.False(result.GetProperty(count ? "isComplete" : "isQueryComplete").GetBoolean());
        Assert.Equal("scope_exhausted", result.GetProperty("stopReason").GetString());
        Assert.Equal(2, result.GetProperty("matchingLineCount").GetInt64());
        Assert.Equal(2, result.GetProperty("matchOccurrenceCount").GetInt64());
        Assert.Equal("file_grew_during_scan", Assert.Single(Reasons(result, "incompleteReasons")));
        Assert.Equal(count ? 4 : 7, result.GetProperty("contractVersion").GetInt32());
        var file = Assert.Single(result.GetProperty("files").EnumerateArray());
        Assert.False(file.GetProperty("isCountExact").GetBoolean());
        Assert.False(file.TryGetProperty("error", out _));
        Assert.Equal("file_grew_during_scan", Assert.Single(Reasons(file, "incompleteReasons")));
        if (!count)
        {
            Assert.True(reportedPageGrowth);
            Assert.Equal(operation == "countsOnly" ? 0 : 2, returnedHits);
        }
        if (operation == "count-hour")
            Assert.Equal(2, result.GetProperty("buckets").EnumerateArray()
                .Sum(bucket => bucket.GetProperty("matchingLineCount").GetInt64()));
        Assert.Equal(result.GetRawText(), Result(await Run(terminalInput)).GetRawText());

        var handoff = await backend.MonitorLogsAsync(new()
        {
            HandoffCursor = result.GetProperty("handoffCursor").GetString(), TimeoutMilliseconds = 5000
        });
        var monitorCursor = handoff.Result?.NextCursor;
        try
        {
            Assert.Empty(handoff.Errors);
            var current = handoff;
            var events = new List<LogMonitorEvent>();
            for (var page = 0; page < 100; page++)
            {
                Assert.Empty(current.Errors);
                var observed = Assert.Single(current.Result!.Files);
                Assert.Null(observed.Error);
                events.AddRange(observed.Events);
                if (current.Result.IsCaughtUp) break;
                current = await backend.MonitorLogsAsync(new() { Cursor = monitorCursor, TimeoutMilliseconds = 5000 });
                monitorCursor = current.Result?.NextCursor ?? monitorCursor;
            }
            Assert.True(current.Result!.IsCaughtUp);
            Assert.Equal("2026-11-04T10:02:00Z needle appended", Assert.Single(events).Text);
            var poll = new LogMonitorQuery { Cursor = monitorCursor, TimeoutMilliseconds = 5000 };
            var idle = await backend.MonitorLogsAsync(poll);
            monitorCursor = idle.Result?.NextCursor ?? monitorCursor;
            Assert.Empty(idle.Errors);
            Assert.True(idle.Result!.IsIdle);
            Assert.Empty(idle.Result.Files.SelectMany(item => item.Events));
            Assert.Same(idle, await backend.MonitorLogsAsync(poll));
            Assert.Equal(result.GetRawText(), Result(await Run(terminalInput)).GetRawText());
        }
        finally
        {
            // A failed assertion must not leave retained observation state behind.
            if (monitorCursor != null)
                await backend.MonitorLogsAsync(new() { Cursor = monitorCursor, Release = true });
        }

        Task<CallToolResult> Run(string? next) => count
            ? tools.CountLogsAsync(targets, "needle", startTimestamp: "2026-11-04T10:00:00Z",
                endTimestamp: "2026-11-04T11:00:00Z", bucketSize: operation == "count-hour" ? "hour" : "none",
                cursor: next, bucketMode: "dense", enableHandoff: true, timeoutMilliseconds: 5000)
            : tools.SearchLogsAsync(targets, "needle", resultMode: operation, cursor: next,
                enableHandoff: true, timeoutMilliseconds: 5000);

        static JsonElement Result(CallToolResult response)
        {
            Assert.False(response.IsError == true);
            var envelope = response.StructuredContent!.Value;
            Assert.Equal(4, envelope.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(envelope.GetRawText(), Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
            Assert.DoesNotContain("file_changed_during_search", envelope.GetRawText(), StringComparison.Ordinal);
            if (envelope.TryGetProperty("errors", out var errors))
                Assert.Empty(errors.EnumerateArray());
            return envelope.GetProperty("result");
        }

        static IEnumerable<string?> Reasons(JsonElement value, string name)
            => value.GetProperty(name).EnumerateArray().Select(reason => reason.GetString());
    }
}
