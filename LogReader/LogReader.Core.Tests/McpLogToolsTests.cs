namespace LogReader.Core.Tests;

using System.Collections.Immutable;
using System.IO.Pipelines;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public sealed class McpLogToolsTests
{
    [Fact]
    public void CreateToolCollection_AdvertisesOnlyFiveReadOnlyStructuredTools()
    {
        using var backend = new RecordingBackend();

        var tools = McpLogTools.CreateToolCollection(backend).ToArray();

        Assert.Equal(
            ["list_log_tree", "read_log_lines", "read_log_tail", "search_logs", "server_status"],
            tools.Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, tool =>
        {
            Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
            Assert.False(tool.ProtocolTool.Annotations.DestructiveHint);
            Assert.True(tool.ProtocolTool.Annotations.IdempotentHint);
            Assert.False(tool.ProtocolTool.Annotations.OpenWorldHint);
            Assert.Equal("object", tool.ProtocolTool.InputSchema.GetProperty("type").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Object, tool.ProtocolTool.OutputSchema!.Value.ValueKind);
        });
    }

    [Fact]
    public void SearchToolSchema_UsesTypedStringTargetKindsAndOmitsCancellationToken()
    {
        using var backend = new RecordingBackend();
        var searchTool = McpLogTools.CreateToolCollection(backend)["search_logs"].ProtocolTool;
        var schema = searchTool.InputSchema.ToString();

        Assert.Contains("targets", schema, StringComparison.Ordinal);
        Assert.Contains("folder", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dashboard", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logFile", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cancellationToken", schema, StringComparison.OrdinalIgnoreCase);
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
    public async Task ListReadTailAndStatus_MapToBackendWithoutPathsOrAmbientState()
    {
        using var backend = new RecordingBackend();
        var tools = new McpLogTools(backend);

        await tools.ListLogTreeAsync("root", maxDepth: 2, maxNodes: 3, startIndex: 4);
        await tools.ReadLogLinesAsync("file", startLine: 5, count: 6, dateOffsetDays: 7, timeoutMilliseconds: 8_000);
        await tools.ReadLogTailAsync("file", cursor: "opaque", maxLines: 9, dateOffsetDays: 10, timeoutMilliseconds: 11_000);
        await tools.GetServerStatusAsync();

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
        Assert.Equal(1, backend.StatusCallCount);
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

        Assert.Equal(5, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "server_status");
        Assert.NotEqual(true, status.IsError);
        Assert.NotNull(status.StructuredContent);
        Assert.Equal(1, status.StructuredContent.Value.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("headless", status.StructuredContent.Value.GetProperty("backend").GetString());
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

    private sealed class RecordingBackend : ILogQueryBackend
    {
        public ConfiguredLogTreeRequest? LastTreeRequest { get; private set; }

        public LogSearchQuery? LastSearchRequest { get; private set; }

        public LogReadLinesQuery? LastReadRequest { get; private set; }

        public LogReadTailQuery? LastTailRequest { get; private set; }

        public int StatusCallCount { get; private set; }

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
            return Task.FromResult(Envelope(new LogSearchResult()));
        }

        public Task<LogOperationEnvelope<LogReadLinesResult>> ReadLogLinesAsync(
            LogReadLinesQuery request,
            CancellationToken ct = default)
        {
            LastReadRequest = request;
            return Task.FromResult(Envelope(new LogReadLinesResult()));
        }

        public Task<LogOperationEnvelope<LogReadTailResult>> ReadLogTailAsync(
            LogReadTailQuery request,
            CancellationToken ct = default)
        {
            LastTailRequest = request;
            return Task.FromResult(Envelope(new LogReadTailResult()));
        }

        public Task<LogOperationEnvelope<LogQueryStatus>> GetStatusAsync(CancellationToken ct = default)
        {
            StatusCallCount++;
            return Task.FromResult(Envelope(new LogQueryStatus()));
        }

        public void Dispose()
        {
        }

        private static LogOperationEnvelope<T> Envelope<T>(T result)
            => new(
                LogOperationEnvelope<T>.CurrentSchemaVersion,
                "request",
                LogOperationBackendKind.Headless,
                "revision",
                IsPartial: false,
                IsTruncated: false,
                TruncationReasons: ImmutableArray<string>.Empty,
                Errors: ImmutableArray<ConfiguredLogRequestError>.Empty,
                result);
    }
}
