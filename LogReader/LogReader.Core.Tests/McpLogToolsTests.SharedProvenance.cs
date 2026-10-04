namespace LogReader.Core.Tests;

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogReader.Core.Models;
using LogReader.Mcp;
using ModelContextProtocol.Protocol;

public sealed partial class McpLogToolsTests
{
    [Theory]
    [InlineData("search_logs", false)]
    [InlineData("search_logs", true)]
    [InlineData("count_logs", false)]
    [InlineData("count_logs", true)]
    [InlineData("read_log_lines", false)]
    [InlineData("read_log_lines", true)]
    [InlineData("read_log_tail", false)]
    [InlineData("read_log_tail", true)]
    public async Task SharedProvenance_ReconstructsInlineEvidenceAcrossTools(string toolName, bool incomplete)
    {
        var first = Route("folder");
        var second = first with { DashboardTreePath = "Services/Other" };
        using var backend = ProvenanceBackend([first, second, first with { }], [second, Route("other")], incomplete);
        await WithClientAsync(backend, async client =>
        {
            var arguments = FileToolArguments(toolName);
            var shared = await client.CallToolAsync(toolName, arguments);
            var sharedResult = shared.StructuredContent!.Value.GetProperty("result");
            Assert.Equal(toolName is "search_logs" or "count_logs" ? 3 : 2, sharedResult.GetProperty("provenanceTable").GetArrayLength());
            var file = VisibleFiles(sharedResult).First();
            Assert.Equal(new[] { 0, 1, 0 }, file.GetProperty("provenanceRefs").EnumerateArray().Select(item => item.GetInt32()));
            Assert.False(file.TryGetProperty("provenance", out _));
            if (incomplete)
            {
                Assert.True(file.GetProperty("isProvenanceTruncated").GetBoolean());
                Assert.Equal(9, file.GetProperty("provenanceTotalCount").GetInt32());
                Assert.Equal("log_access_denied", file.GetProperty("error").GetProperty("code").GetString());
            }
            arguments["provenanceMode"] = "inline";
            var inline = await client.CallToolAsync(toolName, arguments);
            var inlineResult = inline.StructuredContent!.Value.GetProperty("result");
            Assert.False(inlineResult.TryGetProperty("provenanceTable", out _));
            Assert.All(VisibleFiles(inlineResult), visible => Assert.False(visible.TryGetProperty("provenanceRefs", out _)));
            Assert.True(JsonNode.DeepEquals(ReconstructProvenance(sharedResult), JsonNode.Parse(inlineResult.GetRawText())));
            AssertWireParity(shared);
            AssertWireParity(inline);
            arguments["provenanceMode"] = "shared";
            var explicitShared = await client.CallToolAsync(toolName, arguments);
            Assert.Equal(shared.StructuredContent.Value.GetRawText(), explicitShared.StructuredContent!.Value.GetRawText());
        });
    }

    [Fact]
    public async Task SharedProvenance_DeduplicatesEveryFieldWithOrdinalEquality()
    {
        var route = Route("folder");
        ImmutableArray<ConfiguredLogProvenance> variants =
        [
            route, route with { },
            route with { RequestedTargetId = "Folder" },
            route with { RequestedTargetKind = ConfiguredLogTargetKind.Dashboard },
            route with { TargetTreePath = "services" },
            route with { DashboardId = "dashboard-other" },
            route with { DashboardTreePath = "services/API" }
        ];
        using var backend = ProvenanceBackend(variants, [route]);
        var response = await new McpLogTools(backend).SearchLogsAsync([], "needle");
        var result = response.StructuredContent!.Value.GetProperty("result");
        Assert.Equal(6, result.GetProperty("provenanceTable").GetArrayLength());
        Assert.Equal(new[] { 0, 0, 1, 2, 3, 4, 5 }, result.GetProperty("files")[0].GetProperty("provenanceRefs")
            .EnumerateArray().Select(item => item.GetInt32()));
        Assert.Equal(0, result.GetProperty("files")[1].GetProperty("provenanceRefs")[0].GetInt32());
    }

    [Theory]
    [InlineData("search_logs")]
    [InlineData("count_logs")]
    [InlineData("read_log_lines")]
    [InlineData("read_log_tail")]
    public async Task SharedProvenance_EmptyRetainedArraysStayEmpty(string toolName)
    {
        using var backend = ProvenanceBackend([], []);
        await WithClientAsync(backend, async client =>
        {
            var response = await client.CallToolAsync(toolName, FileToolArguments(toolName));
            var result = response.StructuredContent!.Value.GetProperty("result");
            Assert.Empty(result.GetProperty("provenanceTable").EnumerateArray());
            Assert.All(VisibleFiles(result), file => Assert.Empty(file.GetProperty("provenanceRefs").EnumerateArray()));
        });
    }

    [Theory]
    [InlineData("search_logs")]
    [InlineData("count_logs")]
    [InlineData("read_log_lines")]
    [InlineData("read_log_tail")]
    public async Task SharedProvenance_NoVisibleFilesOmitsTable(string toolName)
    {
        using var backend = new RecordingBackend();
        backend.TailHandler = (_, _) => Task.FromResult(RecordingBackend.Envelope(new LogReadTailResult
        {
            File = new LogReadFileResult("file", "Application", [Route("folder")], "utf-8", "generation", [], null),
            CompactFile = true
        }));
        await WithClientAsync(backend, async client =>
        {
            var response = await client.CallToolAsync(toolName, FileToolArguments(toolName));
            Assert.False(response.StructuredContent!.Value.GetProperty("result").TryGetProperty("provenanceTable", out _));
            AssertWireParity(response);
        });
    }

    [Theory]
    [InlineData("search_logs")]
    [InlineData("count_logs")]
    [InlineData("read_log_lines")]
    [InlineData("read_log_tail")]
    public async Task SharedProvenance_InvalidModeIsSanitizedBeforeBackend(string toolName)
    {
        using var backend = new RecordingBackend();
        await WithClientAsync(backend, async client =>
        {
            var arguments = FileToolArguments(toolName);
            arguments["provenanceMode"] = "secret C:\\private\\file.log";
            var response = await client.CallToolAsync(toolName, arguments);
            Assert.True(response.IsError);
            Assert.Null(response.StructuredContent);
            Assert.Equal("provenanceMode must be one of: shared, inline.", Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
            Assert.Null(backend.LastSearchRequest);
            Assert.Null(backend.LastCountRequest);
            Assert.Null(backend.LastReadRequest);
            Assert.Null(backend.LastTailRequest);
        });
    }

    [Theory]
    [InlineData("search_logs")]
    [InlineData("count_logs")]
    [InlineData("read_log_tail")]
    public async Task SharedProvenance_ContinuationAndReplayAreSelfContained(string toolName)
    {
        using var backend = ProvenanceBackend([Route("later"), Route("first")], [Route("first")]);
        await WithClientAsync(backend, async client =>
        {
            var arguments = FileToolArguments(toolName);
            arguments["cursor"] = "opaque-next-page";
            arguments["query"] = "needle";
            var shared = await client.CallToolAsync(toolName, arguments);
            var request = BackendRequest();
            arguments["provenanceMode"] = "inline";
            var inline = await client.CallToolAsync(toolName, arguments);
            Assert.Equal(request, BackendRequest());
            Assert.True(JsonNode.DeepEquals(ReconstructProvenance(shared.StructuredContent!.Value.GetProperty("result")),
                JsonNode.Parse(inline.StructuredContent!.Value.GetProperty("result").GetRawText())));
            arguments["provenanceMode"] = "shared";
            var replay = await client.CallToolAsync(toolName, arguments);
            Assert.Equal(shared.StructuredContent.Value.GetRawText(), replay.StructuredContent!.Value.GetRawText());
            Assert.Equal("later", replay.StructuredContent.Value.GetProperty("result").GetProperty("provenanceTable")[0]
                .GetProperty("requestedTargetId").GetString());
        });
        string BackendRequest() => toolName switch
        {
            "search_logs" => JsonSerializer.Serialize(backend.LastSearchRequest),
            "count_logs" => JsonSerializer.Serialize(backend.LastCountRequest),
            _ => JsonSerializer.Serialize(backend.LastTailRequest)
        };
    }

    [Fact]
    public async Task SharedProvenance_RepeatedRoutesReduceMultiFileBytes()
    {
        using var backend = ProvenanceBackend([Route("folder"), Route("dashboard")], [Route("folder"), Route("dashboard")]);
        backend.SearchHandler = (_, _) => Task.FromResult(RecordingBackend.Envelope(new LogSearchResult
        {
            Files = Enumerable.Range(0, 50).Select(index => backend.SearchResult.Files[index % 2] with { FileId = $"file-{index}" }).ToImmutableArray()
        }));
        var tools = new McpLogTools(backend);
        var shared = (await tools.SearchLogsAsync([], "needle")).StructuredContent!.Value;
        var inline = (await tools.SearchLogsAsync([], "needle", provenanceMode: "inline")).StructuredContent!.Value;
        var sharedBytes = Encoding.UTF8.GetByteCount(shared.GetRawText());
        var inlineBytes = Encoding.UTF8.GetByteCount(inline.GetRawText());
        _output.WriteLine($"Repeated provenance response: inline={inlineBytes}, shared={sharedBytes}, saved={inlineBytes - sharedBytes} UTF-8 bytes.");
        Assert.True(sharedBytes < inlineBytes);
        Assert.True(JsonNode.DeepEquals(ReconstructProvenance(shared.GetProperty("result")), JsonNode.Parse(inline.GetProperty("result").GetRawText())));
    }

    [Theory]
    [InlineData("read_log_lines")]
    [InlineData("read_log_tail")]
    public async Task SharedProvenance_ReadFailuresDoNotFabricateFileOrTable(string toolName)
    {
        using var backend = new RecordingBackend();
        backend.ReadHandler = (_, _) => Task.FromResult(RecordingBackend.Envelope<LogReadLinesResult>(null!) with
        {
            Errors = [new ConfiguredLogRequestError("invalid_file_id", "Unknown configured file.")]
        });
        backend.TailHandler = (_, _) => Task.FromResult(RecordingBackend.Envelope<LogReadTailResult>(null!) with
        {
            Errors = [new ConfiguredLogRequestError("invalid_file_id", "Unknown configured file.")]
        });
        await WithClientAsync(backend, async client =>
        {
            var response = await client.CallToolAsync(toolName, FileToolArguments(toolName));
            Assert.False(response.StructuredContent!.Value.TryGetProperty("result", out _));
            Assert.Equal("invalid_file_id", response.StructuredContent.Value.GetProperty("errors")[0].GetProperty("code").GetString());
            AssertWireParity(response);
        });
    }

    [Theory]
    [InlineData("read_log_lines")]
    [InlineData("read_log_tail")]
    public async Task SharedProvenance_ReadCancellationPropagates(string toolName)
    {
        using var backend = new RecordingBackend();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WaitAsync(CancellationToken ct)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        }
        backend.ReadHandler = async (_, ct) => { await WaitAsync(ct); return RecordingBackend.Envelope(new LogReadLinesResult()); };
        backend.TailHandler = async (_, ct) => { await WaitAsync(ct); return RecordingBackend.Envelope(new LogReadTailResult()); };
        await WithClientAsync(backend, async client =>
        {
            using var cancellation = new CancellationTokenSource();
            var call = client.CallToolAsync(toolName, FileToolArguments(toolName), cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
    }

    private static ConfiguredLogProvenance Route(string id)
        => new(id, ConfiguredLogTargetKind.Folder, "Services", "dashboard", "Services/API");

    private static RecordingBackend ProvenanceBackend(
        ImmutableArray<ConfiguredLogProvenance> first, ImmutableArray<ConfiguredLogProvenance> second, bool incomplete = false)
    {
        var error = incomplete ? new ConfiguredLogRequestError("log_access_denied", "Access denied.") : null;
        LogSearchFileResult SearchFile(string id, ImmutableArray<ConfiguredLogProvenance> provenance)
            => new(id, "Application", provenance, "utf-8", "generation", [], [], error, incomplete)
            {
                MatchingLineCount = 1, IsCountExact = !incomplete,
                IsProvenanceTruncated = incomplete, ProvenanceTotalCount = incomplete ? 9 : provenance.Length,
                IncompleteReasons = incomplete ? ["file_error"] : []
            };
        LogCountFileResult CountFile(string id, ImmutableArray<ConfiguredLogProvenance> provenance)
            => new(id, "Application", provenance, "utf-8", "generation", error)
            {
                MatchingLineCount = 1, IsCountExact = !incomplete,
                IsProvenanceTruncated = incomplete, ProvenanceTotalCount = incomplete ? 9 : provenance.Length,
                IncompleteReasons = incomplete ? ["file_error"] : []
            };
        return new RecordingBackend
        {
            SearchResult = new LogSearchResult { Files = [SearchFile("first", first), SearchFile("second", second)] },
            CountResult = new LogCountResult { Files = [CountFile("first", first), CountFile("second", second)] },
            ReadFile = new LogReadFileResult("first", "Application", first, "utf-8", "generation", [new LogLineResult(1, "needle", false)], error)
            {
                IsProvenanceTruncated = incomplete, ProvenanceTotalCount = incomplete ? 9 : first.Length
            }
        };
    }

    private static Dictionary<string, object?> FileToolArguments(string toolName)
        => toolName is "search_logs" or "count_logs" ? QueryArguments(false) : new() { ["fileId"] = "file" };

    private static IEnumerable<JsonElement> VisibleFiles(JsonElement result)
        => result.TryGetProperty("files", out var files) ? files.EnumerateArray()
            : result.TryGetProperty("file", out var file) ? [file] : Enumerable.Empty<JsonElement>();

    internal static JsonNode ReconstructProvenance(JsonElement result)
    {
        var reconstructed = JsonNode.Parse(result.GetRawText())!.AsObject();
        var table = reconstructed["provenanceTable"]!.AsArray();
        var files = reconstructed["files"] is JsonArray many ? many.OfType<JsonObject>()
            : reconstructed["file"] is JsonObject single ? [single] : Enumerable.Empty<JsonObject>();
        foreach (var file in files)
        {
            var provenance = new JsonArray();
            foreach (var index in file["provenanceRefs"]!.AsArray())
                provenance.Add(table[index!.GetValue<int>()]!.DeepClone());
            file.Remove("provenanceRefs");
            file["provenance"] = provenance;
        }
        reconstructed.Remove("provenanceTable");
        return reconstructed;
    }
}
