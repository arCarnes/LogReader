namespace LogReader.Core.Tests;

using System.Collections.Immutable;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;
using LogReader.Mcp;

public sealed class BackendArbitrationTests
{
    [Fact]
    public async Task LiveClient_HandshakesRoutesStatusAndPropagatesCancellation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var serverBackend = new TestBackend(LogOperationBackendKind.LiveUi);
        var identity = LiveLogPipeIdentityFactory.Create(
            @"C:\storage\" + Guid.NewGuid().ToString("N"),
            "S-1-5-21-test");
        using var server = new LiveLogIpcServer(identity, serverBackend);
        Assert.True(server.TryStart());
        using var client = await LiveLogIpcClientBackend.ConnectAsync(
            identity,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));

        var status = await client.GetStatusAsync();

        Assert.Equal(LogOperationBackendKind.LiveUi, status.Backend);
        Assert.Equal(1, serverBackend.StatusCalls);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var search = client.SearchLogsAsync(new LogSearchQuery
        {
            Targets = [new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file")],
            Query = "needle"
        }, cancellation.Token);
        await serverBackend.SearchStarted.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        await serverBackend.SearchCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await server.StopAsync();
    }

    [Fact]
    public async Task LiveClient_SequentialRequestsRemainOnSameConnection()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var serverBackend = new TestBackend(LogOperationBackendKind.LiveUi)
        {
            SearchResponse = Envelope(LogOperationBackendKind.LiveUi, new LogSearchResult())
        };
        var identity = LiveLogPipeIdentityFactory.Create(
            @"C:\storage\" + Guid.NewGuid().ToString("N"),
            "S-1-5-21-test");
        using var server = new LiveLogIpcServer(identity, serverBackend);
        Assert.True(server.TryStart());
        using var client = await LiveLogIpcClientBackend.ConnectAsync(
            identity,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var status = await client.GetStatusAsync();
            var tree = await client.ListLogTreeAsync(new ConfiguredLogTreeRequest());
            var search = await client.SearchLogsAsync(new LogSearchQuery());
            var lines = await client.ReadLogLinesAsync(new LogReadLinesQuery());
            var tail = await client.ReadLogTailAsync(new LogReadTailQuery());

            Assert.Equal(LogOperationBackendKind.LiveUi, status.Backend);
            Assert.Equal(LogOperationBackendKind.LiveUi, tree.Backend);
            Assert.Equal(LogOperationBackendKind.LiveUi, search.Backend);
            Assert.Equal(LogOperationBackendKind.LiveUi, lines.Backend);
            Assert.Equal(LogOperationBackendKind.LiveUi, tail.Backend);
        }

        Assert.Equal(20, serverBackend.StatusCalls);
        Assert.Equal(20, serverBackend.ListTreeCalls);
        Assert.Equal(20, serverBackend.SearchCalls);
        Assert.Equal(20, serverBackend.ReadLinesCalls);
        await server.StopAsync();
    }

    [Fact]
    public async Task LiveClient_MissingPipeFailsWithinBoundedConnectTimeout()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var identity = LiveLogPipeIdentityFactory.Create(
            @"C:\missing\" + Guid.NewGuid().ToString("N"),
            "S-1-5-21-test");
        var started = DateTime.UtcNow;

        var error = await Assert.ThrowsAsync<LiveLogBackendUnavailableException>(
            () => LiveLogIpcClientBackend.ConnectAsync(
                identity,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100)));

        Assert.Equal("connect_timeout", error.Reason);
        Assert.InRange((DateTime.UtcNow - started).TotalMilliseconds, 0, 1_000);
    }

    [Fact]
    public async Task LiveClient_OversizedServerResponseReturnsBoundedErrorWithoutFallbackSignal()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var nodes = Enumerable.Range(0, 100)
            .Select(index => new ConfiguredLogTreeNode(
                $"node-{index}",
                ConfiguredLogTargetKind.Dashboard,
                new string('名', 1_000),
                new string('界', 8_000),
                ParentId: null,
                Depth: 0,
                HasChildren: false));
        using var serverBackend = new TestBackend(LogOperationBackendKind.LiveUi)
        {
            ListTreeResponse = Envelope(
                LogOperationBackendKind.LiveUi,
                new ConfiguredLogTreeResult("revision", nodes, null, 100, null, false))
        };
        var identity = LiveLogPipeIdentityFactory.Create(
            @"C:\storage\" + Guid.NewGuid().ToString("N"),
            "S-1-5-21-test");
        using var server = new LiveLogIpcServer(identity, serverBackend);
        Assert.True(server.TryStart());
        using var client = await LiveLogIpcClientBackend.ConnectAsync(
            identity,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));

        var response = await client.ListLogTreeAsync(new ConfiguredLogTreeRequest());

        Assert.Equal(LogOperationBackendKind.LiveUi, response.Backend);
        Assert.True(response.IsTruncated);
        Assert.Equal("response_size_limit", Assert.Single(response.Errors).Code);
        Assert.Null(response.Result);
        await server.StopAsync();
    }

    [Fact]
    public async Task Arbitration_AbsentUiUsesHeadlessThenReprobesAndReleasesHeadlessCache()
    {
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var liveAvailable = false;
        var liveAttempts = 0;
        var headless = new TestBackend(LogOperationBackendKind.Headless);
        var live = new TestBackend(LogOperationBackendKind.LiveUi);
        using var arbitration = new ArbitratingLogQueryBackend(
            _ =>
            {
                liveAttempts++;
                return liveAvailable
                    ? Task.FromResult<ILogQueryBackend>(live)
                    : Task.FromException<ILogQueryBackend>(
                        new LiveLogBackendUnavailableException("connect_timeout"));
            },
            () => headless,
            () => now);

        var first = await arbitration.GetStatusAsync();
        var beforeCooldown = await arbitration.GetStatusAsync();
        liveAvailable = true;
        now = now.AddSeconds(3);
        var afterUiStarts = await arbitration.GetStatusAsync();

        Assert.Equal(LogOperationBackendKind.Headless, first.Backend);
        Assert.Equal("live_connect_timeout", first.Result!.LastFallbackReason);
        Assert.Equal(LogOperationBackendKind.Headless, beforeCooldown.Backend);
        Assert.Equal(LogOperationBackendKind.LiveUi, afterUiStarts.Backend);
        Assert.True(afterUiStarts.Result!.LiveUiAvailable);
        Assert.Equal(2, liveAttempts);
        Assert.Equal(1, headless.DisposeCalls);
    }

    [Fact]
    public async Task Arbitration_LiveLossRetriesOnceOnHeadlessWithoutReturningPartialLiveData()
    {
        var live = new TestBackend(LogOperationBackendKind.LiveUi)
        {
            ListTreeException = new LiveLogBackendUnavailableException("connection_lost")
        };
        var headless = new TestBackend(LogOperationBackendKind.Headless);
        using var arbitration = new ArbitratingLogQueryBackend(
            _ => Task.FromResult<ILogQueryBackend>(live),
            () => headless);

        var response = await arbitration.ListLogTreeAsync(new ConfiguredLogTreeRequest());

        Assert.Equal(LogOperationBackendKind.Headless, response.Backend);
        Assert.Equal(1, live.ListTreeCalls);
        Assert.Equal(1, headless.ListTreeCalls);
        Assert.Equal(1, live.DisposeCalls);
    }

    [Fact]
    public async Task Arbitration_RetryReauthorizesOriginalIdInsteadOfCarryingResolvedPath()
    {
        var live = new TestBackend(LogOperationBackendKind.LiveUi)
        {
            ReadLinesException = new LiveLogBackendUnavailableException("connection_lost")
        };
        var headless = new TestBackend(LogOperationBackendKind.Headless)
        {
            ReadLinesResponse = Envelope<LogReadLinesResult>(
                LogOperationBackendKind.Headless,
                result: null,
                errors: [new ConfiguredLogRequestError(
                    "unknown_target",
                    "The configured target no longer exists.")])
        };
        using var arbitration = new ArbitratingLogQueryBackend(
            _ => Task.FromResult<ILogQueryBackend>(live),
            () => headless);
        var request = new LogReadLinesQuery { FileId = "removed-file", StartLine = 1, Count = 1 };

        var response = await arbitration.ReadLogLinesAsync(request);

        Assert.Equal(LogOperationBackendKind.Headless, response.Backend);
        Assert.Equal("unknown_target", Assert.Single(response.Errors).Code);
        Assert.Same(request, headless.LastReadLinesRequest);
        Assert.Equal(1, live.ReadLinesCalls);
        Assert.Equal(1, headless.ReadLinesCalls);
    }

    [Fact]
    public async Task Arbitration_DoesNotFallbackWhenLiveUiReturnsBusyEnvelope()
    {
        var live = new TestBackend(LogOperationBackendKind.LiveUi)
        {
            SearchResponse = Envelope<LogSearchResult>(
                LogOperationBackendKind.LiveUi,
                result: null,
                errors: [new ConfiguredLogRequestError(
                    "interactive_work_pending",
                    "The agent search yielded to interactive WeezTail work.",
                    IsRetryable: true)])
        };
        var headless = new TestBackend(LogOperationBackendKind.Headless);
        using var arbitration = new ArbitratingLogQueryBackend(
            _ => Task.FromResult<ILogQueryBackend>(live),
            () => headless);

        var response = await arbitration.SearchLogsAsync(new LogSearchQuery
        {
            Targets = [new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file")],
            Query = "needle"
        });

        Assert.Equal(LogOperationBackendKind.LiveUi, response.Backend);
        Assert.Equal("interactive_work_pending", Assert.Single(response.Errors).Code);
        Assert.Equal(0, headless.SearchCalls);
    }

    [Fact]
    public async Task Arbitration_BackendSwitchResetsTailCursorExplicitly()
    {
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var liveAvailable = false;
        var headless = new TestBackend(LogOperationBackendKind.Headless)
        {
            TailResponse = TailEnvelope(LogOperationBackendKind.Headless, "headless_cursor")
        };
        var live = new TestBackend(LogOperationBackendKind.LiveUi)
        {
            TailResponse = TailEnvelope(LogOperationBackendKind.LiveUi, "live_cursor")
        };
        using var arbitration = new ArbitratingLogQueryBackend(
            _ => liveAvailable
                ? Task.FromResult<ILogQueryBackend>(live)
                : Task.FromException<ILogQueryBackend>(
                    new LiveLogBackendUnavailableException("connect_failed")),
            () => headless,
            () => now);

        var first = await arbitration.ReadLogTailAsync(new LogReadTailQuery { FileId = "file" });
        liveAvailable = true;
        now = now.AddSeconds(3);
        var switched = await arbitration.ReadLogTailAsync(new LogReadTailQuery
        {
            FileId = "file",
            Cursor = first.Result!.NextCursor
        });

        Assert.Equal(LogOperationBackendKind.LiveUi, switched.Backend);
        Assert.Null(live.LastTailRequest!.Cursor);
        Assert.True(switched.Result!.GenerationChanged);
        Assert.Contains("backend_cursor_reset", switched.TruncationReasons);
        Assert.Equal("live_cursor", switched.Result.NextCursor);
    }

    [Fact]
    public void McpAssembly_DoesNotReferenceWpfApplicationAssembly()
    {
        var references = typeof(McpStdioHost).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference => reference.Name == "WeezTail");
    }

    private static LogOperationEnvelope<LogReadTailResult> TailEnvelope(
        LogOperationBackendKind backend,
        string cursor)
        => Envelope(
            backend,
            new LogReadTailResult
            {
                NextCursor = cursor,
                TotalLineCount = 2
            });

    private static LogOperationEnvelope<T> Envelope<T>(
        LogOperationBackendKind backend,
        T? result,
        ImmutableArray<ConfiguredLogRequestError> errors = default)
        => new(
            1,
            Guid.NewGuid().ToString("N"),
            backend,
            "revision",
            IsPartial: !errors.IsDefaultOrEmpty,
            IsTruncated: false,
            TruncationReasons: [],
            Errors: errors.IsDefault ? [] : errors,
            Result: result);

    private sealed class TestBackend : ILogQueryBackend
    {
        private readonly LogOperationBackendKind _kind;

        public TestBackend(LogOperationBackendKind kind)
        {
            _kind = kind;
        }

        public TaskCompletionSource SearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SearchCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? ListTreeException { get; init; }

        public LogOperationEnvelope<ConfiguredLogTreeResult>? ListTreeResponse { get; init; }

        public Exception? ReadLinesException { get; init; }

        public LogOperationEnvelope<LogSearchResult>? SearchResponse { get; init; }

        public LogOperationEnvelope<LogReadTailResult>? TailResponse { get; init; }

        public LogOperationEnvelope<LogReadLinesResult>? ReadLinesResponse { get; init; }

        public LogReadTailQuery? LastTailRequest { get; private set; }

        public LogReadLinesQuery? LastReadLinesRequest { get; private set; }

        public int ListTreeCalls { get; private set; }

        public int SearchCalls { get; private set; }

        public int ReadLinesCalls { get; private set; }

        public int StatusCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<LogOperationEnvelope<ConfiguredLogTreeResult>> ListLogTreeAsync(
            ConfiguredLogTreeRequest request,
            CancellationToken ct = default)
        {
            ListTreeCalls++;
            return ListTreeException == null
                ? Task.FromResult(ListTreeResponse ?? Envelope(_kind, new ConfiguredLogTreeResult("revision", null, null, 0, null, false)))
                : Task.FromException<LogOperationEnvelope<ConfiguredLogTreeResult>>(ListTreeException);
        }

        public async Task<LogOperationEnvelope<LogSearchResult>> SearchLogsAsync(
            LogSearchQuery request,
            CancellationToken ct = default)
        {
            SearchCalls++;
            if (SearchResponse != null)
                return SearchResponse;

            SearchStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                SearchCancelled.TrySetResult();
                throw;
            }

            return Envelope(_kind, new LogSearchResult());
        }

        public Task<LogOperationEnvelope<LogReadLinesResult>> ReadLogLinesAsync(
            LogReadLinesQuery request,
            CancellationToken ct = default)
        {
            ReadLinesCalls++;
            LastReadLinesRequest = request;
            if (ReadLinesException != null)
                return Task.FromException<LogOperationEnvelope<LogReadLinesResult>>(ReadLinesException);
            return Task.FromResult(ReadLinesResponse ?? Envelope(_kind, new LogReadLinesResult()));
        }

        public Task<LogOperationEnvelope<LogReadTailResult>> ReadLogTailAsync(
            LogReadTailQuery request,
            CancellationToken ct = default)
        {
            LastTailRequest = request;
            return Task.FromResult(TailResponse ?? TailEnvelope(_kind, _kind + "_cursor"));
        }

        public Task<LogOperationEnvelope<LogQueryStatus>> GetStatusAsync(CancellationToken ct = default)
        {
            StatusCalls++;
            return Task.FromResult(Envelope(
                _kind,
                new LogQueryStatus
                {
                    IsReady = true,
                    CacheOwnership = _kind == LogOperationBackendKind.LiveUi ? "ui_shared" : "process_scoped"
                }));
        }

        public void Dispose() => DisposeCalls++;
    }
}
