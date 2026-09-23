namespace LogReader.Core.Tests;

using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public sealed class McpLogToolsTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public McpLogToolsTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("search_logs", false, null)]
    [InlineData("search_logs", true, null)]
    [InlineData("search_logs", false, false)]
    [InlineData("search_logs", true, false)]
    [InlineData("search_logs", false, true)]
    [InlineData("search_logs", true, true)]
    [InlineData("count_logs", false, null)]
    [InlineData("count_logs", true, null)]
    [InlineData("count_logs", false, false)]
    [InlineData("count_logs", true, false)]
    [InlineData("count_logs", false, true)]
    [InlineData("count_logs", true, true)]
    [InlineData("read_log_lines", false, null)]
    [InlineData("read_log_lines", true, null)]
    [InlineData("read_log_tail", false, null)]
    [InlineData("read_log_tail", true, null)]
    public async Task StreamProtocol_CompactsMetadataWithoutLosingInvestigationEvidence(string toolName, bool incomplete, bool? includeStatistics)
    {
        var error = incomplete ? new ConfiguredLogRequestError("log_access_denied", "Access denied.") : null;
        ImmutableArray<string> reasons = incomplete ? ["file_error"] : [];
        ImmutableArray<ConfiguredLogProvenance> provenance =
        [new("folder", ConfiguredLogTargetKind.Folder, "Services", "dashboard", "Services/API")];
        var hit = new LogSearchHit(2, 0, 5);
        ImmutableArray<LogSearchExcerpt> excerpts =
        [new([
            new LogSearchExcerptLine(1, "Starting request", false),
            new LogSearchExcerptLine(2, "ERROR request failed", incomplete)
        ])];
        using var backend = new RecordingBackend
        {
            QueryIsPartial = incomplete,
            SearchResult = new LogSearchResult
            {
                Statistics = new LogSearchStatistics(12345, 67, 50, 49, 1, 2, 1),
                Files = Enumerable.Range(0, 50).Select(index => new LogSearchFileResult(
                    $"file-{index}", "Application", provenance, "utf-8", "generation", [hit], excerpts, error, incomplete)
                {
                    MatchingLineCount = 1,
                    MatchOccurrenceCount = 1,
                    IsCountExact = !incomplete,
                    IncompleteReasons = reasons,
                    ProvenanceTotalCount = 1
                }).ToImmutableArray(),
                NextCursor = "opaque-continuation",
                IsPageComplete = !incomplete,
                IsQueryComplete = false,
                IncompleteReasons = ["unvisited_pages"],
                PageIncompleteReasons = reasons
            },
            CountResult = new LogCountResult
            {
                Statistics = new LogSearchStatistics(67890, 123, 100, 98, 2, 2, 1),
                Files = [new LogCountFileResult("file-0", "Application", provenance, "utf-8", "generation", error)
                {
                    IsCountExact = !incomplete,
                    IncompleteReasons = reasons
                }],
                IsComplete = !incomplete,
                IncompleteReasons = reasons
            },
            ReadFile = new LogReadFileResult("file-0", "Application", provenance, "utf-8", "generation",
                [new LogLineResult(1, "Starting request", false)], error)
        };
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "compact-test", loggerFactory: null);
        await using var server = McpServer.Create(serverTransport, new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "weeztail", Version = "test" },
            ToolCollection = McpLogTools.CreateToolCollection(backend)
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(cancellation.Token);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), loggerFactory: null),
            clientOptions: null, loggerFactory: null, cancellation.Token);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellation.Token);
            var protocolTool = tools.Single(tool => tool.Name == toolName).ProtocolTool;
            AssertCompactSchema(protocolTool.OutputSchema!.Value);
            if (toolName is "search_logs" or "count_logs")
            {
                var parameter = protocolTool.InputSchema.GetProperty("properties").GetProperty("includeStatistics");
                Assert.Equal("boolean", parameter.GetProperty("type").GetString());
                Assert.False(parameter.GetProperty("default").GetBoolean());
                Assert.DoesNotContain(protocolTool.InputSchema.GetProperty("required").EnumerateArray(),
                    value => value.GetString() == "includeStatistics");
                Assert.Contains("statistics", protocolTool.OutputSchema!.Value.ToString(), StringComparison.Ordinal);
            }
            Dictionary<string, object?> arguments = toolName is "search_logs" or "count_logs"
                ? new() { ["targets"] = new[] { new { kind = "folder", id = "folder" } }, ["query"] = "ERROR" }
                : new() { ["fileId"] = "file-0" };
            if (includeStatistics.HasValue)
                arguments["includeStatistics"] = includeStatistics.Value;
            if (toolName == "read_log_tail")
                arguments["query"] = "request";
            var response = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellation.Token);
            Assert.NotEqual(true, response.IsError);
            var envelope = response.StructuredContent!.Value;
            Assert.Equal(3, envelope.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(incomplete && toolName is "search_logs" or "count_logs", envelope.GetProperty("isPartial").GetBoolean());
            Assert.False(envelope.GetProperty("isTruncated").GetBoolean());
            var result = envelope.GetProperty("result");
            Assert.Equal(includeStatistics == true, result.TryGetProperty("statistics", out var statistics));
            if (includeStatistics == true)
            {
                var expected = toolName == "search_logs" ? backend.SearchResult.Statistics : backend.CountResult.Statistics;
                Assert.Equal(JsonSerializer.Serialize(expected, McpJsonUtilities.DefaultOptions), statistics.GetRawText());
            }
            Assert.False(result.TryGetProperty("effectiveLimits", out _));
            var file = toolName is "search_logs" or "count_logs" ? result.GetProperty("files")[0] : result.GetProperty("file");
            Assert.Equal("file-0", file.GetProperty("fileId").GetString());
            Assert.Equal("Services/API", file.GetProperty("provenance")[0].GetProperty("dashboardTreePath").GetString());
            Assert.Equal(incomplete, file.TryGetProperty("error", out var fileError));
            if (incomplete)
                Assert.Equal("log_access_denied", fileError.GetProperty("code").GetString());
            if (toolName is "search_logs" or "count_logs")
            {
                Assert.Equal(!incomplete, file.GetProperty("isCountExact").GetBoolean());
                Assert.Equal(incomplete, file.TryGetProperty("incompleteReasons", out var fileReasons));
                if (incomplete)
                    Assert.Equal("file_error", fileReasons[0].GetString());
            }
            if (toolName == "search_logs")
            {
                Assert.Equal(4, result.GetProperty("contractVersion").GetInt32());
                var returnedHit = file.GetProperty("hits")[0];
                Assert.Equal(2, returnedHit.GetProperty("lineNumber").GetInt64());
                Assert.False(returnedHit.TryGetProperty("text", out _));
                Assert.False(returnedHit.TryGetProperty("contextBefore", out _));
                var returnedLines = file.GetProperty("excerpts")[0].GetProperty("lines");
                Assert.Equal("Starting request", returnedLines[0].GetProperty("text").GetString());
                Assert.Equal("ERROR request failed", returnedLines[1].GetProperty("text").GetString());
                Assert.False(returnedLines[0].TryGetProperty("isTruncated", out _));
                Assert.Equal(incomplete, returnedLines[1].TryGetProperty("isTruncated", out var lineTruncated));
                if (incomplete)
                    Assert.True(lineTruncated.GetBoolean());
                Assert.Equal(incomplete, file.GetProperty("isTruncated").GetBoolean());
                Assert.False(file.TryGetProperty("provenanceTotalCount", out _));
                Assert.False(file.TryGetProperty("evaluatedThroughLine", out _));
                Assert.Equal("opaque-continuation", result.GetProperty("nextCursor").GetString());
                Assert.False(result.GetProperty("isQueryComplete").GetBoolean());
                Assert.False(result.TryGetProperty("areQueryCountsExact", out _));
                Assert.False(result.TryGetProperty("arePageCountsExact", out _));
                Assert.False(result.TryGetProperty("totalHitCount", out _));
                Assert.False(result.TryGetProperty("completionState", out _));
                Assert.Equal("unvisited_pages", result.GetProperty("incompleteReasons")[0].GetString());
                Assert.Equal(incomplete, result.TryGetProperty("pageIncompleteReasons", out _));

                var oldOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
                oldOptions.Converters.Insert(0, new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
                var previous = JsonSerializer.Serialize(RecordingBackend.Envelope(backend.SearchResult), oldOptions);
                var currentBytes = Encoding.UTF8.GetByteCount(envelope.GetRawText());
                var previousBytes = Encoding.UTF8.GetByteCount(previous);
                Assert.True(currentBytes < previousBytes - 1_000);
                _output.WriteLine($"50-file structured search response: {previousBytes} -> {currentBytes} UTF-8 bytes (incomplete={incomplete}, includeStatistics={includeStatistics}).");
            }
            else if (toolName == "count_logs")
            {
                Assert.Equal(2, result.GetProperty("contractVersion").GetInt32());
                Assert.Equal(!incomplete, result.GetProperty("isComplete").GetBoolean());
                Assert.False(result.TryGetProperty("areCountsExact", out _));
                Assert.False(result.TryGetProperty("completionState", out _));
                Assert.False(file.TryGetProperty("provenanceTotalCount", out _));
                Assert.Equal(incomplete, result.TryGetProperty("incompleteReasons", out _));
                Assert.Equal(0, result.GetProperty("matchingLineCount").GetInt64());
            }
            else
            {
                Assert.Equal("Starting request", file.GetProperty("lines")[0].GetProperty("text").GetString());
                if (toolName == "read_log_tail")
                {
                    Assert.Equal(1, result.GetProperty("examinedLineCount").GetInt32());
                    Assert.Equal(0, result.GetProperty("skippedLineCount").GetInt32());
                    Assert.Equal(0, result.GetProperty("remainingLineCount").GetInt32());
                    Assert.False(result.TryGetProperty("removedLineNumber", out _));
                }
            }

            // The SDK's text fallback must carry the same compact shape as structuredContent.
            var text = Assert.IsType<TextContentBlock>(Assert.Single(response.Content));
            Assert.Equal(envelope.GetRawText(), JsonDocument.Parse(text.Text).RootElement.GetRawText());
            if (toolName is "search_logs" or "count_logs")
            {
                arguments["includeStatistics"] = includeStatistics != true;
                var opposite = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellation.Token);
                var originalNode = JsonNode.Parse(envelope.GetRawText())!;
                var oppositeNode = JsonNode.Parse(opposite.StructuredContent!.Value.GetRawText())!;
                originalNode["result"]!.AsObject().Remove("statistics");
                oppositeNode["result"]!.AsObject().Remove("statistics");
                Assert.True(JsonNode.DeepEquals(originalNode, oppositeNode));
            }
            var status = await client.CallToolAsync("server_status", cancellationToken: cancellation.Token);
            Assert.Equal(50, status.StructuredContent!.Value.GetProperty("result").GetProperty("queryBackend")
                .GetProperty("limits").GetProperty("maximumFiles").GetInt32());
        }
        finally
        {
            cancellation.Cancel();
            try { await serverTask; }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("search_logs", false)]
    [InlineData("search_logs", true)]
    [InlineData("count_logs", false)]
    [InlineData("count_logs", true)]
    public async Task StreamProtocol_StatisticsDoNotFabricateResultsOnFailure(string toolName, bool includeStatistics)
    {
        using var backend = new RecordingBackend
        {
            SearchHandler = (_, _) => Task.FromResult(Failure<LogSearchResult>()),
            CountHandler = (_, _) => Task.FromResult(Failure<LogCountResult>())
        };
        await WithClientAsync(backend, async client =>
        {
            var response = await client.CallToolAsync(toolName, QueryArguments(includeStatistics));
            Assert.NotEqual(true, response.IsError);
            var envelope = response.StructuredContent!.Value;
            Assert.False(envelope.TryGetProperty("result", out _));
            var originalFailure = toolName == "search_logs"
                ? JsonSerializer.Serialize(Failure<LogSearchResult>(), McpJsonUtilities.DefaultOptions)
                : JsonSerializer.Serialize(Failure<LogCountResult>(), McpJsonUtilities.DefaultOptions);
            Assert.Equal(originalFailure, envelope.GetRawText());
            Assert.Equal("invalid_request", envelope.GetProperty("errors")[0].GetProperty("code").GetString());
            Assert.DoesNotContain("statistics", envelope.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(envelope.GetRawText(), Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
        });

        static LogOperationEnvelope<T> Failure<T>()
            => RecordingBackend.Envelope<T>(default!) with
            {
                Errors = [new ConfiguredLogRequestError("invalid_request", "Invalid query.")]
            };
    }

    [Theory]
    [InlineData("search_logs")]
    [InlineData("count_logs")]
    public async Task StreamProtocol_StatisticsSelectionIsIsolatedAcrossConcurrentCalls(string toolName)
    {
        using var backend = new RecordingBackend();
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WaitForBothAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref entered) == 2)
                release.TrySetResult();
            await release.Task.WaitAsync(ct);
        }
        backend.SearchHandler = async (_, ct) =>
        {
            await WaitForBothAsync(ct);
            return RecordingBackend.Envelope(backend.SearchResult);
        };
        backend.CountHandler = async (_, ct) =>
        {
            await WaitForBothAsync(ct);
            return RecordingBackend.Envelope(backend.CountResult);
        };
        await WithClientAsync(backend, async client =>
        {
            var responses = await Task.WhenAll(
                client.CallToolAsync(toolName, QueryArguments(false)).AsTask(),
                client.CallToolAsync(toolName, QueryArguments(true)).AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(responses[0].StructuredContent!.Value.GetProperty("result").TryGetProperty("statistics", out _));
            Assert.True(responses[1].StructuredContent!.Value.GetProperty("result").TryGetProperty("statistics", out _));
        });
    }

    [Theory]
    [InlineData("search_logs")]
    [InlineData("count_logs")]
    public async Task StreamProtocol_StatisticsOptInPreservesCancellation(string toolName)
    {
        using var backend = new RecordingBackend();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WaitForCancellationAsync(CancellationToken ct)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        }
        backend.SearchHandler = async (_, ct) =>
        {
            await WaitForCancellationAsync(ct);
            return RecordingBackend.Envelope(backend.SearchResult);
        };
        backend.CountHandler = async (_, ct) =>
        {
            await WaitForCancellationAsync(ct);
            return RecordingBackend.Envelope(backend.CountResult);
        };
        await WithClientAsync(backend, async client =>
        {
            using var cancellation = new CancellationTokenSource();
            var call = client.CallToolAsync(toolName, QueryArguments(true), cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
    }

    private static Dictionary<string, object?> QueryArguments(bool includeStatistics)
        => new()
        {
            ["targets"] = new[] { new { kind = "folder", id = "folder" } },
            ["query"] = "ERROR",
            ["includeStatistics"] = includeStatistics
        };

    private static async Task WithClientAsync(ILogQueryBackend backend, Func<McpClient, Task> action)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "statistics-test", loggerFactory: null);
        await using var server = McpServer.Create(serverTransport, new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "weeztail", Version = "test" },
            ToolCollection = McpLogTools.CreateToolCollection(backend)
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.RunAsync(cancellation.Token);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), loggerFactory: null),
            clientOptions: null, loggerFactory: null, cancellation.Token);
        try { await action(client); }
        finally
        {
            cancellation.Cancel();
            try { await serverTask; }
            catch (OperationCanceledException) { }
        }
    }

    private static void AssertCompactSchema(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("properties", out var properties))
            {
                Assert.False(properties.TryGetProperty("effectiveLimits", out _));
                if (schema.TryGetProperty("required", out var required))
                {
                    Assert.DoesNotContain(required.EnumerateArray(), item => item.GetString() is
                        "incompleteReasons" or "pageIncompleteReasons" or "error" or "statistics" or
                        "hits" or "excerpts" or "evaluatedThroughLine" or
                        "provenanceTotalCount" or "examinedLineCount" or "skippedLineCount" or
                        "remainingLineCount" or "removedLineNumber");
                    if (properties.TryGetProperty("hits", out _) && properties.TryGetProperty("isCountExact", out _))
                        Assert.DoesNotContain(required.EnumerateArray(), item => item.GetString() == "encoding");
                }
            }
            foreach (var property in schema.EnumerateObject())
                AssertCompactSchema(property.Value);
        }
        else if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schema.EnumerateArray())
                AssertCompactSchema(item);
        }
    }

    private static void AssertSearchExcerptLineSchema(JsonElement schema)
    {
        var found = false;
        Visit(schema);
        Assert.True(found, "The search output schema did not expose excerpt lines.");

        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("properties", out var properties) &&
                    properties.TryGetProperty("lineNumber", out _) &&
                    properties.TryGetProperty("text", out _) &&
                    properties.TryGetProperty("isTruncated", out _))
                {
                    found = true;
                    Assert.True(node.TryGetProperty("required", out var required));
                    Assert.DoesNotContain(required.EnumerateArray(), item => item.GetString() == "isTruncated");
                }
                foreach (var property in node.EnumerateObject())
                    Visit(property.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                    Visit(item);
            }
        }
    }

    [Fact]
    public void CreateToolCollection_AdvertisesOnlySixReadOnlyStructuredTools()
    {
        using var backend = new RecordingBackend();

        var tools = McpLogTools.CreateToolCollection(backend).ToArray();

        Assert.Equal(
            ["count_logs", "list_log_tree", "read_log_lines", "read_log_tail", "search_logs", "server_status"],
            tools.Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, tool =>
        {
            Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
            Assert.False(tool.ProtocolTool.Annotations.DestructiveHint);
            Assert.True(tool.ProtocolTool.Annotations.IdempotentHint);
            Assert.Equal("object", tool.ProtocolTool.InputSchema.GetProperty("type").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Object, tool.ProtocolTool.OutputSchema!.Value.ValueKind);
            Assert.DoesNotContain("mcpServer", tool.ProtocolTool.InputSchema.ToString(), StringComparison.OrdinalIgnoreCase);
        });
        Assert.False(tools.Single(tool => tool.ProtocolTool.Name == "list_log_tree").ProtocolTool.Annotations!.OpenWorldHint);
        Assert.True(tools.Single(tool => tool.ProtocolTool.Name == "search_logs").ProtocolTool.Annotations!.OpenWorldHint);
        Assert.True(tools.Single(tool => tool.ProtocolTool.Name == "count_logs").ProtocolTool.Annotations!.OpenWorldHint);
        Assert.True(tools.Single(tool => tool.ProtocolTool.Name == "read_log_lines").ProtocolTool.Annotations!.OpenWorldHint);
        Assert.True(tools.Single(tool => tool.ProtocolTool.Name == "read_log_tail").ProtocolTool.Annotations!.OpenWorldHint);
        Assert.False(tools.Single(tool => tool.ProtocolTool.Name == "server_status").ProtocolTool.Annotations!.OpenWorldHint);
    }

    [Fact]
    public void CountToolSchema_ExposesTypedTargetsWindowsAndAggregateOutput()
    {
        using var backend = new RecordingBackend();
        var countTool = McpLogTools.CreateToolCollection(backend)["count_logs"].ProtocolTool;
        var schemaText = countTool.InputSchema.ToString();
        var outputSchemaText = countTool.OutputSchema!.Value.ToString();

        Assert.Equal(["query", "targets"], countTool.InputSchema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString())
            .Order(StringComparer.Ordinal));
        Assert.Contains("relativeWindow", schemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bucketSize", schemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("matchingLineCount", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("matchOccurrenceCount", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resolvedTimeRange", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("buckets", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isComplete", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("areCountsExact", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completionState", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cursor", schemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cancellationToken", schemaText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchToolSchema_UsesTypedStringTargetKindsAndOmitsCancellationToken()
    {
        using var backend = new RecordingBackend();
        var searchTool = McpLogTools.CreateToolCollection(backend)["search_logs"].ProtocolTool;
        var schema = searchTool.InputSchema;
        var schemaText = schema.ToString();

        Assert.Equal(["query", "targets"], schema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString())
            .Order(StringComparer.Ordinal));
        Assert.Contains("targets", schemaText, StringComparison.Ordinal);
        Assert.Contains("folder", schemaText, StringComparison.Ordinal);
        Assert.Contains("dashboard", schemaText, StringComparison.Ordinal);
        Assert.Contains("logFile", schemaText, StringComparison.Ordinal);
        Assert.Contains("samples", schemaText, StringComparison.Ordinal);
        Assert.Contains("matchesOnly", schemaText, StringComparison.Ordinal);
        Assert.Contains("countsOnly", schemaText, StringComparison.Ordinal);
        Assert.Contains("cursor", schemaText, StringComparison.OrdinalIgnoreCase);
        var outputSchemaText = searchTool.OutputSchema!.Value.ToString();
        Assert.Contains("nextCursor", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pageMatchingLineCount", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isQueryComplete", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pageOmittedZeroHitFileCount", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provenanceTotalCount", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isProvenanceTruncated", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("excerpts", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contextBefore", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contextAfter", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        AssertSearchExcerptLineSchema(searchTool.OutputSchema.Value);
        Assert.DoesNotContain("totalHitCount", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arePageCountsExact", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("areQueryCountsExact", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completionState", outputSchemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cancellationToken", schemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Folder\"", schemaText, StringComparison.Ordinal);
        Assert.Equal(
            schemaText,
            McpLogTools.CreateToolCollection(backend)["search_logs"].ProtocolTool.InputSchema.ToString());
    }

    [Fact]
    public async Task SearchLogsAsync_MapsEveryPublicArgumentToPureBackendContract()
    {
        using var backend = new RecordingBackend();
        var tools = new McpLogTools(backend);
        var targets = new[]
        {
            new ConfiguredLogTarget(ConfiguredLogTargetKind.Folder, "folder-id")
        };

        await tools.SearchLogsAsync(
            targets,
            "error.*42",
            useRegex: true,
            caseSensitive: true,
            resultMode: "matchesOnly",
            cursor: "opaque-search-cursor",
            dateOffsetDays: 2,
            startTimestamp: "2026-08-04 10:00:00",
            endTimestamp: "2026-08-04 11:00:00",
            maxFiles: 3,
            maxHitsPerFile: 4,
            maxTotalHits: 5,
            includeContextBefore: 6,
            includeContextAfter: 7,
            timeoutMilliseconds: 8_000);

        var request = Assert.IsType<LogSearchQuery>(backend.LastSearchRequest);
        Assert.Equal(targets, request.Targets);
        Assert.Equal("error.*42", request.Query);
        Assert.True(request.UseRegex);
        Assert.True(request.CaseSensitive);
        Assert.Equal("matchesOnly", request.ResultMode);
        Assert.Equal("opaque-search-cursor", request.Cursor);
        Assert.Equal(2, request.DateOffsetDays);
        Assert.Equal("2026-08-04 10:00:00", request.StartTimestamp);
        Assert.Equal("2026-08-04 11:00:00", request.EndTimestamp);
        Assert.Equal(3, request.MaxFiles);
        Assert.Equal(4, request.MaxHitsPerFile);
        Assert.Equal(5, request.MaxTotalHits);
        Assert.Equal(6, request.IncludeContextBefore);
        Assert.Equal(7, request.IncludeContextAfter);
        Assert.Equal(8_000, request.TimeoutMilliseconds);
    }

    [Fact]
    public async Task CountLogsAsync_MapsEveryPublicArgumentToPureBackendContract()
    {
        using var backend = new RecordingBackend();
        var tools = new McpLogTools(backend);
        var targets = new[] { new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard-id") };

        await tools.CountLogsAsync(
            targets,
            "known event",
            useRegex: true,
            caseSensitive: true,
            dateOffsetDays: 2,
            startTimestamp: "2026-08-04 10:00:00",
            endTimestamp: "2026-08-04 11:00:00",
            relativeWindow: null,
            bucketSize: "minute",
            timeoutMilliseconds: 8_000);

        var request = Assert.IsType<LogCountQuery>(backend.LastCountRequest);
        Assert.Equal(targets, request.Targets);
        Assert.Equal("known event", request.Query);
        Assert.True(request.UseRegex);
        Assert.True(request.CaseSensitive);
        Assert.Equal(2, request.DateOffsetDays);
        Assert.Equal("2026-08-04 10:00:00", request.StartTimestamp);
        Assert.Equal("2026-08-04 11:00:00", request.EndTimestamp);
        Assert.Null(request.RelativeWindow);
        Assert.Equal("minute", request.BucketSize);
        Assert.Equal(8_000, request.TimeoutMilliseconds);
    }

    [Fact]
    public async Task ListReadTailAndStatus_MapToBackendWithoutPathsOrAmbientState()
    {
        using var backend = new RecordingBackend();
        var tools = new McpLogTools(backend);

        await tools.ListLogTreeAsync("root", maxDepth: 2, maxNodes: 3, startIndex: 4);
        await tools.ReadLogLinesAsync("file", startLine: 5, count: 6, dateOffsetDays: 7, timeoutMilliseconds: 8_000);
        await tools.ReadLogTailAsync("file", cursor: "opaque", maxLines: 9, dateOffsetDays: 10,
            timeoutMilliseconds: 11_000, query: "error", useRegex: true, caseSensitive: true);
        var status = await tools.GetServerStatusAsync(server: null);

        Assert.Equal(new ConfiguredLogTreeRequest("root", 2, 3, 4), backend.LastTreeRequest);
        Assert.Equal("file", backend.LastReadRequest!.FileId);
        Assert.Equal(5, backend.LastReadRequest.StartLine);
        Assert.Equal(6, backend.LastReadRequest.Count);
        Assert.Equal(7, backend.LastReadRequest.DateOffsetDays);
        Assert.Equal(8_000, backend.LastReadRequest.TimeoutMilliseconds);
        Assert.Equal("file", backend.LastTailRequest!.FileId);
        Assert.Equal("opaque", backend.LastTailRequest.Cursor);
        Assert.Equal(9, backend.LastTailRequest.MaxLines);
        Assert.Equal(10, backend.LastTailRequest.DateOffsetDays);
        Assert.Equal(11_000, backend.LastTailRequest.TimeoutMilliseconds);
        Assert.Equal("error", backend.LastTailRequest.Query);
        Assert.True(backend.LastTailRequest.UseRegex);
        Assert.True(backend.LastTailRequest.CaseSensitive);
        Assert.Equal(1, backend.StatusCallCount);
        Assert.Equal("stdio", status.Result!.Transport);
        Assert.Equal("tools_only", status.Result.PrimitivePolicy);
        Assert.Equal("not_negotiated", status.Result.ProtocolVersion);
    }

    [Fact]
    public async Task StreamProtocol_InitializesListsAndCallsStructuredTool()
    {
        using var backend = new RecordingBackend();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "weeztail-test",
            loggerFactory: null);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "weeztail", Version = "test" },
            ToolCollection = McpLogTools.CreateToolCollection(backend)
        };
        await using var server = McpServer.Create(serverTransport, options);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(cancellation.Token);
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            loggerFactory: null);
        await using var client = await McpClient.CreateAsync(
            clientTransport,
            clientOptions: null,
            loggerFactory: null,
            cancellation.Token);

        var tools = await client.ListToolsAsync(cancellationToken: cancellation.Token);
        var status = await client.CallToolAsync(
            "server_status",
            arguments: null,
            cancellationToken: cancellation.Token);

        Assert.Equal(6, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "server_status");
        Assert.NotEqual(true, status.IsError);
        Assert.NotNull(status.StructuredContent);
        Assert.Equal(3, status.StructuredContent.Value.GetProperty("schemaVersion").GetInt32());
        Assert.False(status.StructuredContent.Value.TryGetProperty("backend", out _));
        Assert.Equal("stdio", status.StructuredContent.Value.GetProperty("result").GetProperty("transport").GetString());
        Assert.Equal(
            client.NegotiatedProtocolVersion,
            status.StructuredContent.Value.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal(1, backend.StatusCallCount);

        cancellation.Cancel();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task StreamProtocol_ClientCancellationPropagatesToToolBackend()
    {
        using var backend = new RecordingBackend();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.StatusHandler = async cancellationToken =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }

            return RecordingBackend.Envelope(new LogQueryStatus());
        };
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "weeztail-cancellation-test",
            loggerFactory: null);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "weeztail", Version = "test" },
            ToolCollection = McpLogTools.CreateToolCollection(backend)
        };
        await using var server = McpServer.Create(serverTransport, options);
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(serverCancellation.Token);
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            loggerFactory: null);
        await using var client = await McpClient.CreateAsync(
            clientTransport,
            clientOptions: null,
            loggerFactory: null,
            serverCancellation.Token);
        using var callCancellation = new CancellationTokenSource();

        var call = client.CallToolAsync(
            "server_status",
            arguments: null,
            cancellationToken: callCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        serverCancellation.Cancel();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task StreamProtocol_UnexpectedBackendFailureDoesNotExposeDetails()
    {
        using var backend = new RecordingBackend
        {
            StatusHandler = _ => throw new InvalidOperationException("secret C:\\private\\logs\\application.log")
        };
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "weeztail-error-test",
            loggerFactory: null);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "weeztail", Version = "test" },
            ToolCollection = McpLogTools.CreateToolCollection(backend)
        };
        await using var server = McpServer.Create(serverTransport, options);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(cancellation.Token);
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            loggerFactory: null);
        await using var client = await McpClient.CreateAsync(
            clientTransport,
            clientOptions: null,
            loggerFactory: null,
            cancellation.Token);

        var result = await client.CallToolAsync(
            "server_status",
            arguments: null,
            cancellationToken: cancellation.Token);

        Assert.True(result.IsError);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("application.log", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("McpLogToolsTests", serialized, StringComparison.Ordinal);
        cancellation.Cancel();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReadLogTail_CompactsIdleAndFilteredNoMatchProtocolResponses(bool filtered, bool appended)
    {
        var file = new LogReadFileResult("file", "Application", [], "utf-8", "generation",
            [new LogLineResult(1, "hit", false)], Error: null);
        var emptyFile = file with { Lines = [] };
        using var backend = new RecordingBackend();
        backend.TailHandler = (request, _) => Task.FromResult(RecordingBackend.Envelope(
            request.Cursor == null
                ? new LogReadTailResult { File = file, NextCursor = "opaque-initial", TotalLineCount = 1 }
                : new LogReadTailResult
                {
                    File = emptyFile,
                    NextCursor = appended ? "opaque-advanced" : "opaque-initial",
                    IsIdle = !appended,
                    CompactFile = true,
                    TotalLineCount = appended ? 2 : 1,
                    ExaminedLineCount = filtered ? appended ? 1 : 0 : null,
                    SkippedLineCount = filtered ? appended ? 1 : 0 : null,
                    RemainingLineCount = filtered ? 0 : null
                }));
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "tail-compact-test", loggerFactory: null);
        await using var server = McpServer.Create(serverTransport, new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "weeztail", Version = "test" },
            ToolCollection = McpLogTools.CreateToolCollection(backend)
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(cancellation.Token);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), loggerFactory: null),
            clientOptions: null, loggerFactory: null, cancellation.Token);
        try
        {
            var tool = (await client.ListToolsAsync(cancellationToken: cancellation.Token))
                .Single(item => item.Name == "read_log_tail").ProtocolTool;
            var resultSchema = tool.OutputSchema!.Value.GetProperty("properties").GetProperty("result");
            var resultRequired = resultSchema.TryGetProperty("required", out var required)
                ? required.EnumerateArray().Select(item => item.GetString()).ToArray()
                : [];
            Assert.Contains("isIdle", resultSchema.GetProperty("properties").EnumerateObject().Select(item => item.Name));
            Assert.DoesNotContain("file", resultRequired);
            Assert.DoesNotContain("nextCursor", resultRequired);

            var arguments = new Dictionary<string, object?> { ["fileId"] = "file" };
            if (filtered)
                arguments["query"] = "hit";
            var initial = await client.CallToolAsync("read_log_tail", arguments, cancellationToken: cancellation.Token);
            var initialResult = initial.StructuredContent!.Value.GetProperty("result");
            Assert.False(initialResult.GetProperty("isIdle").GetBoolean());
            Assert.Equal("file", initialResult.GetProperty("file").GetProperty("fileId").GetString());
            Assert.Equal("opaque-initial", initialResult.GetProperty("nextCursor").GetString());

            arguments["cursor"] = "opaque-initial";
            var poll = await client.CallToolAsync("read_log_tail", arguments, cancellationToken: cancellation.Token);
            var envelope = poll.StructuredContent!.Value;
            var result = envelope.GetProperty("result");
            Assert.Equal(!appended, result.GetProperty("isIdle").GetBoolean());
            Assert.False(result.TryGetProperty("file", out _));
            Assert.Equal(appended, result.TryGetProperty("nextCursor", out var nextCursor));
            if (appended)
            {
                Assert.Equal("opaque-advanced", nextCursor.GetString());
                Assert.Equal(1, result.GetProperty("examinedLineCount").GetInt32());
                Assert.Equal(1, result.GetProperty("skippedLineCount").GetInt32());
                Assert.Equal(0, result.GetProperty("remainingLineCount").GetInt32());
            }
            else
            {
                Assert.False(result.TryGetProperty("totalLineCount", out _));
                Assert.False(result.TryGetProperty("examinedLineCount", out _));
                Assert.False(result.TryGetProperty("skippedLineCount", out _));
                Assert.False(result.TryGetProperty("remainingLineCount", out _));
            }
            var text = Assert.IsType<TextContentBlock>(Assert.Single(poll.Content));
            Assert.Equal(envelope.GetRawText(), JsonDocument.Parse(text.Text).RootElement.GetRawText());

            arguments["cursor"] = appended ? nextCursor.GetString() : "opaque-initial";
            await client.CallToolAsync("read_log_tail", arguments, cancellationToken: cancellation.Token);
            Assert.Equal(arguments["cursor"], backend.LastTailRequest!.Cursor);
        }
        finally
        {
            cancellation.Cancel();
            try { await serverTask; }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class RecordingBackend : ILogQueryBackend
    {
        public LogSearchResult SearchResult { get; init; } = new();

        public LogCountResult CountResult { get; init; } = new();

        public LogReadFileResult? ReadFile { get; init; }

        public bool QueryIsPartial { get; init; }

        public Func<LogSearchQuery, CancellationToken, Task<LogOperationEnvelope<LogSearchResult>>>? SearchHandler { get; set; }

        public Func<LogCountQuery, CancellationToken, Task<LogOperationEnvelope<LogCountResult>>>? CountHandler { get; set; }

        public Func<LogReadTailQuery, CancellationToken, Task<LogOperationEnvelope<LogReadTailResult>>>? TailHandler { get; set; }

        public ConfiguredLogTreeRequest? LastTreeRequest { get; private set; }

        public LogSearchQuery? LastSearchRequest { get; private set; }

        public LogCountQuery? LastCountRequest { get; private set; }

        public LogReadLinesQuery? LastReadRequest { get; private set; }

        public LogReadTailQuery? LastTailRequest { get; private set; }

        public int StatusCallCount { get; private set; }

        public Func<CancellationToken, Task<LogOperationEnvelope<LogQueryStatus>>>? StatusHandler { get; set; }

        public Task<LogOperationEnvelope<ConfiguredLogTreeResult>> ListLogTreeAsync(
            ConfiguredLogTreeRequest request,
            CancellationToken ct = default)
        {
            LastTreeRequest = request;
            return Task.FromResult(Envelope(new ConfiguredLogTreeResult(
                "revision",
                nodes: null,
                errors: null,
                totalNodeCount: 0,
                nextStartIndex: null,
                depthTruncated: false)));
        }

        public Task<LogOperationEnvelope<LogSearchResult>> SearchLogsAsync(
            LogSearchQuery request,
            CancellationToken ct = default)
        {
            LastSearchRequest = request;
            return SearchHandler?.Invoke(request, ct) ?? Task.FromResult(Envelope(SearchResult) with { IsPartial = QueryIsPartial });
        }

        public Task<LogOperationEnvelope<LogCountResult>> CountLogsAsync(
            LogCountQuery request,
            CancellationToken ct = default)
        {
            LastCountRequest = request;
            return CountHandler?.Invoke(request, ct) ?? Task.FromResult(Envelope(CountResult) with { IsPartial = QueryIsPartial });
        }

        public Task<LogOperationEnvelope<LogReadLinesResult>> ReadLogLinesAsync(
            LogReadLinesQuery request,
            CancellationToken ct = default)
        {
            LastReadRequest = request;
            return Task.FromResult(Envelope(new LogReadLinesResult { File = ReadFile }));
        }

        public Task<LogOperationEnvelope<LogReadTailResult>> ReadLogTailAsync(
            LogReadTailQuery request,
            CancellationToken ct = default)
        {
            LastTailRequest = request;
            if (TailHandler is not null)
                return TailHandler(request, ct);
            return Task.FromResult(Envelope(new LogReadTailResult
            {
                File = ReadFile,
                ExaminedLineCount = request.Query == null ? null : 1,
                SkippedLineCount = request.Query == null ? null : 0,
                RemainingLineCount = request.Query == null ? null : 0
            }));
        }

        public Task<LogOperationEnvelope<LogQueryStatus>> GetStatusAsync(CancellationToken ct = default)
        {
            StatusCallCount++;
            if (StatusHandler is not null)
                return StatusHandler(ct);
            return Task.FromResult(Envelope(new LogQueryStatus()));
        }

        public void Dispose()
        {
        }

        internal static LogOperationEnvelope<T> Envelope<T>(T result)
            => new(
                LogOperationEnvelope<T>.CurrentSchemaVersion,
                "request",
                "revision",
                IsPartial: false,
                IsTruncated: false,
                TruncationReasons: ImmutableArray<string>.Empty,
                Errors: ImmutableArray<ConfiguredLogRequestError>.Empty,
                result);
    }
}
