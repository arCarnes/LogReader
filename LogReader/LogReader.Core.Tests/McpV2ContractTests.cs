namespace LogReader.Core.Tests;

using System.IO.Pipelines;
using System.Text.Json;
using Json.Schema;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public sealed partial class McpLogToolsTests
{
    [Fact]
    public void PublishedContracts_ExposeElevenClosedObjectToolsWithExplicitSelectors()
    {
        using var backend = new RecordingBackend();
        var tools = McpLogTools.CreateToolCollection(backend);
        Assert.Equal(new[] { "continue_log_count", "continue_log_search", "count_logs", "list_log_tree", "poll_log_monitor",
            "read_log_lines", "search_logs", "server_status", "start_log_monitor", "start_log_monitor_from_snapshot", "stop_log_monitor" },
            tools.Select(t => t.ProtocolTool.Name).Order(StringComparer.Ordinal));
        foreach (var tool in tools)
        {
            var schema = tool.ProtocolTool.InputSchema;
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.False(schema.TryGetProperty("oneOf", out _));
            Assert.False(schema.TryGetProperty("anyOf", out _));
            Assert.False(schema.TryGetProperty("allOf", out _));
            Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
            Assert.NotNull(tool.ProtocolTool.OutputSchema);
            Assert.DoesNotContain("file_grew_during_scan", tool.ProtocolTool.OutputSchema.Value.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("isCountExact", tool.ProtocolTool.OutputSchema.Value.GetRawText(), StringComparison.Ordinal);
        }
        foreach (var name in new[] { "count_logs", "search_logs", "start_log_monitor" })
        {
            var input = tools[name].ProtocolTool.InputSchema;
            Assert.Contains(input.GetProperty("required").EnumerateArray(), p => p.GetString() == "targets");
            var target = input.GetProperty("properties").GetProperty("targets").GetProperty("items");
            Assert.False(target.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(new[] { "kind", "id" }, target.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        }
        Assert.Equal(new[] { "cursor", "includeStatistics", "timeoutMilliseconds" },
            tools["continue_log_search"].ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("count_logs", "{\"query\":\"needle\"}", "/targets", "required_argument")]
    [InlineData("search_logs", "{\"targets\":null}", "/targets", "invalid_argument")]
    [InlineData("start_log_monitor", "{\"targets\":[]}", "/targets", "invalid_argument")]
    [InlineData("count_logs", "{\"targets\":[null]}", "/targets/0", "invalid_argument")]
    [InlineData("count_logs", "{\"targets\":[{\"id\":\"file\"}]}", "/targets/0/kind", "required_argument")]
    [InlineData("count_logs", "{\"targets\":[{\"kind\":\"logFile\"}]}", "/targets/0/id", "required_argument")]
    [InlineData("count_logs", "{\"targets\":[{\"kind\":2,\"id\":\"file\"}]}", "/targets/0/kind", "invalid_argument")]
    [InlineData("count_logs", "{\"targets\":[{\"kind\":\"file\",\"id\":\"file\"}]}", "/targets/0/kind", "invalid_argument")]
    [InlineData("count_logs", "{\"targets\":[{\"kind\":\"logFile\",\"id\":\"file\",\"path\":\"private\"}]}", "/targets/0/path", "unexpected_argument")]
    [InlineData("server_status", "{\"unexpected\":true}", "/unexpected", "unexpected_argument")]
    [InlineData("read_log_lines", "{}", "/fileId", "required_argument")]
    [InlineData("read_log_lines", "{\"fileId\":\"file\",\"count\":\"ten\"}", "/count", "invalid_argument")]
    [InlineData("read_log_lines", "{\"fileId\":\"file\",\"offset\":2147483648}", "/offset", "invalid_argument")]
    [InlineData("read_log_lines", "{\"fileId\":\"file\",\"anchor\":\"middle\"}", "/anchor", "invalid_argument")]
    [InlineData("continue_log_count", "{}", "/cursor", "required_argument")]
    [InlineData("continue_log_search", "{\"cursor\":\"opaque\",\"query\":\"changed\"}", "/query", "unexpected_argument")]
    [InlineData("poll_log_monitor", "{\"cursor\":null}", "/cursor", "invalid_argument")]
    [InlineData("stop_log_monitor", "{}", "/cursor", "required_argument")]
    [InlineData("start_log_monitor_from_snapshot", "{}", "/handoffCursor", "required_argument")]
    [InlineData("list_log_tree", "{\"maxDepth\":-1}", "/maxDepth", "invalid_argument")]
    public async Task PublishedContracts_RejectMalformedArgumentsBeforeBackend(string name, string json, string path, string code)
    {
        using var backend = new RecordingBackend();
        await WithV2ClientAsync(backend, async client =>
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToDictionary(p => p.Key, p => (object?)p.Value);
            var response = await client.CallToolAsync(name, args);
            var body = AssertV2(response, "error");
            Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("continuation").ValueKind);
            Assert.Contains(body.GetProperty("issues").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == code && issue.GetProperty("argumentPath").GetString() == path);
            Assert.DoesNotContain("private", body.GetRawText(), StringComparison.Ordinal);
            Assert.Null(backend.LastSearchRequest); Assert.Null(backend.LastCountRequest);
            Assert.Null(backend.LastMonitorRequest); Assert.Null(backend.LastReadRequest); Assert.Null(backend.LastTreeRequest);
            Assert.Equal(0, backend.StatusCallCount);
            Assert.True(McpV2Response.CompileSchema(McpLogTools.CreateToolCollection(backend)[name].ProtocolTool.OutputSchema!.Value).Evaluate(body).IsValid);
        });
    }

    [Theory]
    [InlineData("search_logs")]
    [InlineData("count_logs")]
    public async Task PublishedContracts_AcceptTimeOnlySelectionAndPublishSchemaValidData(string name)
    {
        using var backend = new RecordingBackend();
        await WithV2ClientAsync(backend, async client =>
        {
            var result = await client.CallToolAsync(name, new Dictionary<string, object?>
            {
                ["targets"] = new[] { new { kind = "logFile", id = "file" } }, ["relativeWindow"] = "last 10m"
            });
            var body = AssertV2(result, "success");
            Assert.True(body.GetProperty("data").TryGetProperty("scanComplete", out _));
            Assert.Empty(body.GetProperty("issues").EnumerateArray());
            Assert.True(McpV2Response.CompileSchema(McpLogTools.CreateToolCollection(backend)[name].ProtocolTool.OutputSchema!.Value).Evaluate(body).IsValid);
        });
    }

    internal static JsonElement AssertV2(CallToolResult response, string status)
    {
        Assert.Equal(status == "error", response.IsError == true);
        var body = response.StructuredContent!.Value;
        Assert.Equal(5, body.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(status, body.GetProperty("status").GetString());
        Assert.Equal(body.GetRawText(), Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
        return body;
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    public async Task PublishedContracts_BindSchemaValidIntegralNumbers(string number)
    {
        using var backend = new RecordingBackend();
        await WithV2ClientAsync(backend, async client =>
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"fileId\":\"file\",\"offset\":" + number + "}")!;
            var response = await client.CallToolAsync("read_log_lines", raw.ToDictionary(p => p.Key, p => (object?)p.Value));
            AssertV2(response, "success");
            Assert.Equal(1, backend.LastReadRequest!.Offset);
        });
    }

    internal static async Task WithV2ClientAsync(ILogQueryBackend backend, Func<McpClient, Task> action)
    {
        var incoming = new Pipe(); var outgoing = new Pipe();
        await using var transport = new StreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream(), "v2-tests", loggerFactory: null);
        await using var server = McpServer.Create(transport, new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "weeztail", Version = "test" },
            ServerInstructions = McpStdioHost.ServerInstructions, ToolCollection = McpLogTools.CreateToolCollection(backend)
        });
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = server.RunAsync(lifetime.Token);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream(), loggerFactory: null),
            clientOptions: null, loggerFactory: null, lifetime.Token);
        try { await action(client); }
        finally
        {
            lifetime.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }
}

public sealed partial class HeadlessLogQueryBackendTests
{
    [Theory]
    [InlineData("search_logs", "{\"startTimestamp\":\"bad\"}", "invalid_timestamp_range", "/startTimestamp")]
    [InlineData("count_logs", "{\"endTimestamp\":\"bad\"}", "invalid_timestamp_range", "/endTimestamp")]
    [InlineData("count_logs", "{\"startTimestamp\":\"2026-11-19T12:00:00Z\",\"endTimestamp\":\"13:00\"}", "invalid_timestamp_range", "/endTimestamp")]
    [InlineData("start_log_monitor", "{\"startTimestamp\":\"13:00\",\"endTimestamp\":\"12:00\"}", "invalid_timestamp_range", "/endTimestamp")]
    [InlineData("count_logs", "{\"startTimestamp\":\"12:00\",\"relativeWindow\":\"today\"}", "conflicting_time_window", "/relativeWindow")]
    [InlineData("count_logs", "{\"relativeWindow\":\"last 366d\"}", "relative_window_limit_exceeded", "/relativeWindow")]
    [InlineData("count_logs", "{\"startTimestamp\":\"00:00\",\"endTimestamp\":\"23:00\",\"bucketSize\":\"minute\"}", "count_bucket_limit_exceeded", "/bucketSize")]
    public async Task PublishedContracts_PointToCorrectableTimeArguments(string tool, string input, string code, string path)
    {
        using var backend = CreateBackend(CreateSnapshot());
        await McpLogToolsTests.WithV2ClientAsync(backend, async client =>
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input)!
                .ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
            arguments["targets"] = new[] { new { kind = "logFile", id = "file" } };
            arguments["query"] = "needle";
            var body = McpLogToolsTests.AssertV2(await client.CallToolAsync(tool, arguments), "error");
            var issue = Assert.Single(body.GetProperty("issues").EnumerateArray());
            Assert.Equal(code, issue.GetProperty("code").GetString());
            Assert.Equal("argument", issue.GetProperty("scope").GetString());
            Assert.Equal("correct_arguments", issue.GetProperty("recovery").GetString());
            Assert.Equal(path, issue.GetProperty("argumentPath").GetString());
            Assert.True(McpV2Response.CompileSchema(McpLogTools.CreateToolCollection(backend)[tool].ProtocolTool.OutputSchema!.Value).Evaluate(body).IsValid);
        });
    }

    [Fact]
    public async Task V2_ConcurrentCursorOnlyCallsReplayAndRetainOriginalSelection()
    {
        var path = await CreateFileAsync("v2-replay.log", "needle\nneedle\nneedle");
        var targets = new List<ConfiguredLogTarget> { new(ConfiguredLogTargetKind.LogFile, "file") };
        using var backend = CreateBackend(CreateSnapshot(("file", path)), limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 3 });
        var first = await backend.SearchLogsAsync(new() { Targets = targets, Query = "needle" });
        var cursor = first.Result!.NextCursor!;
        Assert.NotNull(cursor);
        targets.Clear(); // The retained request must own its selection, not caller-owned collections.
        var pair = await Task.WhenAll(backend.ContinueLogSearchAsync(cursor), backend.ContinueLogSearchAsync(cursor));
        Assert.Same(pair[0], pair[1]);
        Assert.Empty(pair[0].Errors);
        var wrongOperation = await backend.ContinueLogCountAsync(cursor);
        Assert.Equal("mismatched_count_cursor", Assert.Single(wrongOperation.Errors).Code);
        var current = pair[0];
        for (var i = 0; current.Result!.NextCursor is { } next && i < 100; i++)
            current = await backend.ContinueLogSearchAsync(next);
        Assert.True(current.Result!.IsQueryComplete);
        Assert.Equal(3, current.Result.MatchingLineCount);
    }

    [Fact]
    public async Task V2_CursorOnlyContinuationReauthorizesAndExpiresWithoutRestarting()
    {
        var path = await CreateFileAsync("v2-authorization.log", "needle\nneedle");
        var catalog = new MutableCatalogReader(CreateSnapshot(("file", path)));
        var now = DateTimeOffset.UtcNow;
        using var backend = CreateBackend(catalog.Snapshot, catalogReader: catalog, now: () => now,
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 3, ContinuationIdleMilliseconds = 1000 });
        var first = await backend.CountLogsAsync(new() { Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle" });
        var cursor = first.Result!.NextCursor!;
        catalog.Snapshot = CreateSnapshot(("file", path + ".different"));
        var stale = await backend.ContinueLogCountAsync(cursor);
        Assert.Equal("stale_count_cursor", Assert.Single(stale.Errors).Code);
        now = now.AddSeconds(2);
        var expired = await backend.ContinueLogCountAsync(cursor);
        Assert.Equal("expired_count_cursor", Assert.Single(expired.Errors).Code);
        Assert.Null(expired.Result);
    }

    [Fact]
    public async Task V2_EmptyLineReadAndGenuineZeroMatchesAreSuccessful()
    {
        var path = await CreateFileAsync("v2-empty.log", "");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        await McpLogToolsTests.WithV2ClientAsync(backend, async client =>
        {
            var read = await client.CallToolAsync("read_log_lines", new Dictionary<string, object?> { ["fileId"] = "file", ["anchor"] = "end" });
            var data = McpLogToolsTests.AssertV2(read, "success").GetProperty("data");
            Assert.Empty(data.GetProperty("file").GetProperty("lines").EnumerateArray());
            var count = await client.CallToolAsync("count_logs", new Dictionary<string, object?>
            { ["targets"] = new[] { new { kind = "logFile", id = "file" } }, ["query"] = "needle" });
            var body = McpLogToolsTests.AssertV2(count, "success");
            Assert.Equal(0, body.GetProperty("data").GetProperty("matchingLineCount").GetInt64());
            Assert.True(body.GetProperty("data").GetProperty("scanComplete").GetBoolean());
            Assert.Empty(body.GetProperty("issues").EnumerateArray());
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task V2_ObservedGrowthQualifiesTheFrozenExtent(bool count, bool rewrite)
    {
        var path = await CreateFileAsync("v2-growth.log", "needle\nneedle\nneedle\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)), limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 3 });
        await McpLogToolsTests.WithV2ClientAsync(backend, async client =>
        {
            var tools = McpLogTools.CreateToolCollection(backend);
            var start = count ? "count_logs" : "search_logs";
            var initial = await client.CallToolAsync(start, new Dictionary<string, object?>
            {
                ["targets"] = new[] { new { kind = "logFile", id = "file" } }, ["query"] = "needle", ["enableHandoff"] = true
            });
            var body = McpLogToolsTests.AssertV2(initial, "success");
            Assert.False(body.GetProperty("data").TryGetProperty("handoffCursor", out _));
            if (rewrite)
                await File.WriteAllTextAsync(path, "needle\nneedle\nnormal\nneedle appended\n");
            else
                await File.AppendAllTextAsync(path, "needle appended\n");

            JsonElement? submitted = null;
            for (var page = 0; body.GetProperty("continuation").ValueKind != JsonValueKind.Null && page < 100; page++)
            {
                submitted = body.GetProperty("continuation");
                var tool = submitted.Value.GetProperty("tool").GetString()!;
                var response = await client.CallToolAsync(tool, new Dictionary<string, object?>
                    { ["cursor"] = submitted.Value.GetProperty("cursor").GetString() });
                body = McpLogToolsTests.AssertV2(response, "partial");
                var issue = Assert.Single(body.GetProperty("issues").EnumerateArray());
                Assert.Equal("file_grew_during_scan", issue.GetProperty("code").GetString());
                Assert.Equal("file", issue.GetProperty("scope").GetString());
                Assert.Equal("file", issue.GetProperty("fileId").GetString());
                Assert.Equal("report_failure", issue.GetProperty("recovery").GetString());
                Assert.True(McpV2Response.CompileSchema(tools[tool].ProtocolTool.OutputSchema!.Value).Evaluate(body).IsValid);
                if (page == 0)
                {
                    var handoff = await client.CallToolAsync("start_log_monitor_from_snapshot", new Dictionary<string, object?>
                        { ["handoffCursor"] = body.GetProperty("continuation").GetProperty("cursor").GetString() });
                    var rejected = McpLogToolsTests.AssertV2(handoff, "error");
                    Assert.Equal("handoff_boundary_unavailable",
                        Assert.Single(rejected.GetProperty("issues").EnumerateArray()).GetProperty("code").GetString());
                }
            }
            Assert.Equal(JsonValueKind.Null, body.GetProperty("continuation").ValueKind);
            var data = body.GetProperty("data");
            Assert.True(data.GetProperty("isTraversalComplete").GetBoolean());
            Assert.False(data.GetProperty("scanComplete").GetBoolean());
            Assert.Equal(rewrite ? 2 : 3, data.GetProperty("matchingLineCount").GetInt64());
            Assert.False(data.TryGetProperty("handoffCursor", out _));
            Assert.NotNull(submitted);
            var replay = await client.CallToolAsync(submitted.Value.GetProperty("tool").GetString()!,
                new Dictionary<string, object?> { ["cursor"] = submitted.Value.GetProperty("cursor").GetString() });
            Assert.Equal(body.GetRawText(), replay.StructuredContent!.Value.GetRawText());
        });
    }

    [Theory]
    [InlineData("start", 0, 2, "one,two")]
    [InlineData("start", 2, 2, "three,four")]
    [InlineData("end", 0, 2, "three,four")]
    [InlineData("end", 1, 2, "two,three")]
    [InlineData("end", 99, 2, "")]
    [InlineData("start", 99, 2, "")]
    public async Task V2_LineReadsSelectPhysicalRangesWithoutTimestamps(string anchor, int offset, int count, string expected)
    {
        var path = await CreateFileAsync("v2-lines.log", "one\ntwo\nthree\nfour");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        await McpLogToolsTests.WithV2ClientAsync(backend, async client =>
        {
            var response = await client.CallToolAsync("read_log_lines", new Dictionary<string, object?>
            { ["fileId"] = "file", ["anchor"] = anchor, ["offset"] = offset, ["count"] = count });
            var body = McpLogToolsTests.AssertV2(response, "success");
            var lines = body.GetProperty("data").GetProperty("file").GetProperty("lines").EnumerateArray().ToArray();
            Assert.Equal(expected, string.Join(",", lines.Select(l => l.GetProperty("text").GetString())));
            Assert.Equal(lines.Select(l => l.GetProperty("lineNumber").GetInt64()).Order(), lines.Select(l => l.GetProperty("lineNumber").GetInt64()));
            Assert.True(McpV2Response.CompileSchema(McpLogTools.CreateToolCollection(backend)["read_log_lines"].ProtocolTool.OutputSchema!.Value).Evaluate(body).IsValid);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V2_SearchOutcomesIncludeEarlierFileFailures(bool includeReadable)
    {
        var first = Path.Combine(_testDirectory, "missing-first.log");
        var second = includeReadable
            ? await CreateFileAsync("readable-second.log", "no match\n")
            : Path.Combine(_testDirectory, "missing-second.log");
        using var backend = CreateBackend(CreateSnapshot(("first", first), ("second", second)));
        await McpLogToolsTests.WithV2ClientAsync(backend, async client =>
        {
            var result = await client.CallToolAsync("search_logs", new Dictionary<string, object?>
            {
                ["targets"] = new[] { new { kind = "logFile", id = "first" }, new { kind = "logFile", id = "second" } },
                ["query"] = "needle", ["maxFiles"] = 1
            });
            var page = McpLogToolsTests.AssertV2(result, "partial");
            Assert.Equal("first", Assert.Single(page.GetProperty("issues").EnumerateArray()).GetProperty("fileId").GetString());
            var next = page.GetProperty("continuation");
            var arguments = new Dictionary<string, object?> { ["cursor"] = next.GetProperty("cursor").GetString() };
            var tool = next.GetProperty("tool").GetString()!;
            var final = McpLogToolsTests.AssertV2(await client.CallToolAsync(tool, arguments), includeReadable ? "partial" : "error");
            Assert.Equal(JsonValueKind.Null, final.GetProperty("continuation").ValueKind);
            Assert.Equal(includeReadable ? new[] { "first" } : new[] { "first", "second" },
                final.GetProperty("issues").EnumerateArray().Select(issue => issue.GetProperty("fileId").GetString()));
            if (includeReadable)
            {
                var data = final.GetProperty("data");
                Assert.Empty(data.GetProperty("files").EnumerateArray());
                Assert.Equal(0, data.GetProperty("matchingLineCount").GetInt64());
                Assert.False(data.GetProperty("scanComplete").GetBoolean());
            }
            else
                Assert.Equal(JsonValueKind.Null, final.GetProperty("data").ValueKind);

            Assert.True(McpV2Response.CompileSchema(McpLogTools.CreateToolCollection(backend)[tool].ProtocolTool.OutputSchema!.Value).Evaluate(final).IsValid);
            Assert.Equal(final.GetRawText(), (await client.CallToolAsync(tool, arguments)).StructuredContent!.Value.GetRawText());
        });
    }

    [Fact]
    public async Task V2_CountClassifiesAllFailuresEvenWhenIssuesAreCapped()
    {
        var files = Enumerable.Range(1, 72).Select(i => ($"file-{i:D2}", Path.Combine(_testDirectory, $"missing-{i:D2}.log"))).ToArray();
        using var backend = CreateBackend(CreateSnapshot(files));
        await McpLogToolsTests.WithV2ClientAsync(backend, async client =>
        {
            var result = await client.CallToolAsync("count_logs", new Dictionary<string, object?>
            {
                ["targets"] = new[] { new { kind = "dashboard", id = "dashboard" } },
                ["query"] = "needle"
            });
            var body = McpLogToolsTests.AssertV2(result, "error");
            Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
            var issues = body.GetProperty("issues").EnumerateArray().ToArray();
            Assert.Equal(files.Take(64).Select(file => file.Item1),
                issues[..^1].Select(issue => issue.GetProperty("fileId").GetString()));
            Assert.Equal("file_issues_truncated", issues[^1].GetProperty("code").GetString());
            Assert.StartsWith("8 ", issues[^1].GetProperty("message").GetString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task V2_MonitorReportsAllStoppedFilesAcrossPolls()
    {
        var first = await CreateFileAsync("monitor-first.log", "one\n");
        var second = await CreateFileAsync("monitor-second.log", "two\n");
        using var backend = CreateBackend(CreateSnapshot(("first", first), ("second", second)));
        await McpLogToolsTests.WithV2ClientAsync(backend, async client =>
        {
            var initial = McpLogToolsTests.AssertV2(await client.CallToolAsync("start_log_monitor", new Dictionary<string, object?>
            {
                ["targets"] = new[] { new { kind = "logFile", id = "first" }, new { kind = "logFile", id = "second" } }
            }), "success");
            await File.WriteAllTextAsync(first, "");
            await File.WriteAllTextAsync(second, "");

            var partial = McpLogToolsTests.AssertV2(await PollAsync(initial), "partial");
            Assert.Equal("first", Assert.Single(partial.GetProperty("issues").EnumerateArray()).GetProperty("fileId").GetString());
            var final = McpLogToolsTests.AssertV2(await PollAsync(partial), "error");
            Assert.Equal(JsonValueKind.Null, final.GetProperty("continuation").ValueKind);
            Assert.Equal(JsonValueKind.Null, final.GetProperty("data").ValueKind);
            Assert.Equal(new[] { "first", "second" },
                final.GetProperty("issues").EnumerateArray().Select(issue => issue.GetProperty("fileId").GetString()));
            Assert.Equal(final.GetRawText(), (await PollAsync(partial)).StructuredContent!.Value.GetRawText());

            async Task<CallToolResult> PollAsync(JsonElement previous) => await client.CallToolAsync("poll_log_monitor",
                new Dictionary<string, object?>
                {
                    ["cursor"] = previous.GetProperty("continuation").GetProperty("cursor").GetString(), ["maxFiles"] = 1
                });
        });
    }

    [Theory]
    [InlineData(false, "error")]
    [InlineData(true, "partial")]
    public async Task V2_FileFailuresAreAttributedAtEnvelopeLevel(bool includeReadable, string expected)
    {
        var path = await CreateFileAsync("v2-readable.log", "needle\n");
        var entries = includeReadable ? new[] { ("missing", path + ".missing"), ("file", path) } : new[] { ("missing", path + ".missing") };
        using var backend = CreateBackend(CreateSnapshot(entries));
        await McpLogToolsTests.WithV2ClientAsync(backend, async client =>
        {
            var response = await client.CallToolAsync("count_logs", new Dictionary<string, object?>
            { ["targets"] = entries.Select(e => new { kind = "logFile", id = e.Item1 }).ToArray(), ["query"] = "needle" });
            var body = McpLogToolsTests.AssertV2(response, expected);
            var issue = Assert.Single(body.GetProperty("issues").EnumerateArray());
            Assert.Equal("log_not_found", issue.GetProperty("code").GetString());
            Assert.Equal("missing", issue.GetProperty("fileId").GetString());
            Assert.DoesNotContain(path, body.GetRawText(), StringComparison.Ordinal);
            if (includeReadable)
                Assert.All(body.GetProperty("data").GetProperty("files").EnumerateArray(), f => Assert.False(f.TryGetProperty("error", out _)));
        });
    }
}
