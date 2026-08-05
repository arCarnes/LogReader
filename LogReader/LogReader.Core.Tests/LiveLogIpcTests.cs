namespace LogReader.Core.Tests;

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Pipes;
using System.Text.Json;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed class LiveLogIpcTests
{
    [Fact]
    public void PipeIdentity_IsStableCaseInsensitiveAndDoesNotExposeInputs()
    {
        var first = LiveLogPipeIdentityFactory.Create(@"C:\Private\WeezTail\", "S-1-5-21-123");
        var equivalent = LiveLogPipeIdentityFactory.Create(@"c:\private\weeztail", "S-1-5-21-123");
        var otherUser = LiveLogPipeIdentityFactory.Create(@"C:\Private\WeezTail", "S-1-5-21-456");

        Assert.Equal(first, equivalent);
        Assert.NotEqual(first, otherUser);
        Assert.StartsWith("weeztail-log-v1-", first.PipeName, StringComparison.Ordinal);
        Assert.DoesNotContain("Private", first.PipeName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("S-1-5", first.PipeName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, first.StorageIdentity.Length);
    }

    [Fact]
    public async Task Framing_RoundTripsPartialReadsAndRejectsOversizedOrPartialFrames()
    {
        var frame = new LiveLogIpcFrame
        {
            Type = LiveLogIpcProtocol.RequestFrame,
            ProtocolVersion = LiveLogIpcProtocol.CurrentVersion,
            RequestId = "request_1",
            Operation = LiveLogIpcProtocol.ServerStatusOperation
        };
        await using var encoded = new MemoryStream();
        await LiveLogIpcFraming.WriteFrameAsync(encoded, frame);
        var bytes = encoded.ToArray();
        await using var partialReader = new OneByteReadStream(bytes);

        var decoded = await LiveLogIpcFraming.ReadFrameAsync(partialReader);

        Assert.Equal(frame.Type, decoded!.Type);
        Assert.Equal(frame.RequestId, decoded.RequestId);

        var oversizedPrefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(oversizedPrefix, LiveLogIpcProtocol.MaximumFrameBytes + 1);
        await using var oversized = new MemoryStream(oversizedPrefix);
        var oversizedError = await Assert.ThrowsAsync<LiveLogIpcProtocolException>(
            () => LiveLogIpcFraming.ReadFrameAsync(oversized));
        Assert.Equal("invalid_frame_size", oversizedError.Code);

        await using var truncated = new MemoryStream(bytes[..^1]);
        var partialError = await Assert.ThrowsAsync<LiveLogIpcProtocolException>(
            () => LiveLogIpcFraming.ReadFrameAsync(truncated));
        Assert.Equal("partial_frame", partialError.Code);
    }

    [Fact]
    public async Task ClientComputerName_LocalPipeIsRecognized()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var pipeName = "weeztail-client-name-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        var wait = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await wait;

        var succeeded = LiveLogPipeClientValidator.TryGetClientComputerName(
            server.SafePipeHandle,
            out var clientName,
            out var errorCode);

        Assert.False(succeeded);
        Assert.Equal(229, errorCode);
        Assert.Empty(clientName);
        Assert.True(LiveLogPipeClientValidator.IsLocalClient(server));
    }

    [Fact]
    public void Server_ListenerCreationFailureIsFailSoftAndSanitized()
    {
        using var backend = new RecordingBackend();
        var diagnostics = new List<string>();
        using var server = new LiveLogIpcServer(
            LiveLogPipeIdentityFactory.Create(@"C:\storage", "S-1-5-21-123"),
            backend,
            diagnostics.Add,
            () => throw new UnauthorizedAccessException(@"C:\secret\pipe"));

        var started = server.TryStart();

        Assert.False(started);
        Assert.Equal(["live_log_listener_start_failed"], diagnostics);
        Assert.DoesNotContain("secret", string.Join(" ", diagnostics), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backend.TotalCalls);
    }

    [Fact]
    public async Task Server_NonLocalClientIsRejectedBeforeHandshakeOrBackendWork()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var backend = new RecordingBackend();
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = LiveLogPipeIdentityFactory.CreateCurrent(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var server = new LiveLogIpcServer(
            identity,
            backend,
            code =>
            {
                if (code == "live_log_remote_client_rejected")
                    rejected.TrySetResult();
            },
            pipeFactory: null,
            clientValidator: static _ => false);
        Assert.True(server.TryStart());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var client = await ConnectPipeAsync(identity, cancellation.Token);
        await rejected.Task.WaitAsync(cancellation.Token);

        Assert.Equal(0, backend.TotalCalls);
        await server.StopAsync();
    }

    [Fact]
    public async Task Server_DisconnectCancelsActiveRequest()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var backend = new RecordingBackend();
        var identity = LiveLogPipeIdentityFactory.CreateCurrent(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var server = new LiveLogIpcServer(identity, backend);
        Assert.True(server.TryStart());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var client = await ConnectAsync(identity, cancellation.Token);
        await LiveLogIpcFraming.WriteFrameAsync(
            client,
            Request(
                "disconnect_1",
                LiveLogIpcProtocol.SearchLogsOperation,
                new LogSearchQuery
                {
                    Targets = [new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file")],
                    Query = "needle"
                }),
            ct: cancellation.Token);
        await backend.SearchStarted.Task.WaitAsync(cancellation.Token);

        await client.DisposeAsync();

        await backend.SearchCancelled.Task.WaitAsync(cancellation.Token);
        await server.StopAsync();
    }

    [Fact]
    public async Task Server_CurrentUserHandshakeStatusVersionMismatchAndCancellationAreStructured()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var backend = new RecordingBackend();
        var identity = LiveLogPipeIdentityFactory.CreateCurrent(Path.GetTempPath());
        using var server = new LiveLogIpcServer(identity, backend);
        Assert.True(server.TryStart());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using (var client = await ConnectAsync(identity, cancellation.Token))
        {
            var statusId = "status_1";
            await LiveLogIpcFraming.WriteFrameAsync(
                client,
                Request(statusId, LiveLogIpcProtocol.ServerStatusOperation),
                ct: cancellation.Token);
            var response = await LiveLogIpcFraming.ReadFrameAsync(client, ct: cancellation.Token);

            Assert.True(response!.Success);
            var status = response.Payload!.Value.Deserialize<LogOperationEnvelope<LogQueryStatus>>(
                LiveLogIpcFraming.SerializerOptions);
            Assert.Equal(LogOperationBackendKind.LiveUi, status!.Backend);
            Assert.Equal(1, backend.StatusCallCount);

            var searchId = "search_1";
            await LiveLogIpcFraming.WriteFrameAsync(
                client,
                Request(
                    searchId,
                    LiveLogIpcProtocol.SearchLogsOperation,
                    new LogSearchQuery
                    {
                        Targets = [new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file")],
                        Query = "needle"
                    }),
                ct: cancellation.Token);
            await backend.SearchStarted.Task.WaitAsync(cancellation.Token);
            await LiveLogIpcFraming.WriteFrameAsync(
                client,
                new LiveLogIpcFrame
                {
                    Type = LiveLogIpcProtocol.CancelFrame,
                    ProtocolVersion = LiveLogIpcProtocol.CurrentVersion,
                    RequestId = searchId
                },
                ct: cancellation.Token);
            var cancelled = await LiveLogIpcFraming.ReadFrameAsync(client, ct: cancellation.Token);
            Assert.False(cancelled!.Success);
            Assert.Equal("request_cancelled", cancelled.Error!.Code);
            await backend.SearchCancelled.Task.WaitAsync(cancellation.Token);
        }

        await using (var incompatible = await ConnectPipeAsync(identity, cancellation.Token))
        {
            await LiveLogIpcFraming.WriteFrameAsync(
                incompatible,
                Handshake(identity, protocolVersion: 999),
                ct: cancellation.Token);
            var response = await LiveLogIpcFraming.ReadFrameAsync(incompatible, ct: cancellation.Token);
            Assert.False(response!.Success);
            Assert.Equal("incompatible_protocol", response.Error!.Code);
        }

        await server.StopAsync();
    }

    [Fact]
    public async Task Server_ThreeConnectionsAreAdmittedAndFourthTimesOutUntilSlotCloses()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var backend = new RecordingBackend();
        var identity = LiveLogPipeIdentityFactory.CreateCurrent(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var server = new LiveLogIpcServer(identity, backend);
        Assert.True(server.TryStart());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clients = new List<NamedPipeClientStream>();
        try
        {
            for (var index = 0; index < LiveLogIpcProtocol.MaximumClients; index++)
                clients.Add(await ConnectAsync(identity, cancellation.Token));

            await using var fourth = new NamedPipeClientStream(
                ".",
                identity.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var fourthTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => fourth.ConnectAsync(fourthTimeout.Token));

            clients[0].Dispose();
            await using var replacement = await ConnectAsync(identity, cancellation.Token);
        }
        finally
        {
            foreach (var client in clients)
                client.Dispose();
        }

        await server.StopAsync();
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(
        LiveLogPipeIdentity identity,
        CancellationToken ct)
    {
        var client = await ConnectPipeAsync(identity, ct);
        await LiveLogIpcFraming.WriteFrameAsync(client, Handshake(identity), ct: ct);
        var response = await LiveLogIpcFraming.ReadFrameAsync(client, ct: ct);
        Assert.True(response!.Success);
        Assert.Equal(LiveLogIpcProtocol.HandshakeResultFrame, response.Type);
        return client;
    }

    private static async Task<NamedPipeClientStream> ConnectPipeAsync(
        LiveLogPipeIdentity identity,
        CancellationToken ct)
    {
        var client = new NamedPipeClientStream(
            ".",
            identity.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(ct);
        return client;
    }

    private static LiveLogIpcFrame Handshake(LiveLogPipeIdentity identity, int protocolVersion = LiveLogIpcProtocol.CurrentVersion)
        => new()
        {
            Type = LiveLogIpcProtocol.HandshakeFrame,
            ProtocolVersion = protocolVersion,
            RequestId = "handshake_1",
            StorageIdentity = identity.StorageIdentity
        };

    private static LiveLogIpcFrame Request<T>(string requestId, string operation, T? payload = default)
        => new()
        {
            Type = LiveLogIpcProtocol.RequestFrame,
            ProtocolVersion = LiveLogIpcProtocol.CurrentVersion,
            RequestId = requestId,
            Operation = operation,
            Payload = payload == null
                ? null
                : JsonSerializer.SerializeToElement(payload, LiveLogIpcFraming.SerializerOptions)
        };

    private static LiveLogIpcFrame Request(string requestId, string operation)
        => Request<object?>(requestId, operation, payload: null);

    private sealed class RecordingBackend : ILogQueryBackend
    {
        public TaskCompletionSource SearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SearchCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StatusCallCount { get; private set; }

        public int TotalCalls { get; private set; }

        public Task<LogOperationEnvelope<ConfiguredLogTreeResult>> ListLogTreeAsync(ConfiguredLogTreeRequest request, CancellationToken ct = default)
        {
            TotalCalls++;
            return Task.FromResult(Envelope(new ConfiguredLogTreeResult("revision", null, null, 0, null, false)));
        }

        public async Task<LogOperationEnvelope<LogSearchResult>> SearchLogsAsync(LogSearchQuery request, CancellationToken ct = default)
        {
            TotalCalls++;
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

            return Envelope(new LogSearchResult());
        }

        public Task<LogOperationEnvelope<LogReadLinesResult>> ReadLogLinesAsync(LogReadLinesQuery request, CancellationToken ct = default)
        {
            TotalCalls++;
            return Task.FromResult(Envelope(new LogReadLinesResult()));
        }

        public Task<LogOperationEnvelope<LogReadTailResult>> ReadLogTailAsync(LogReadTailQuery request, CancellationToken ct = default)
        {
            TotalCalls++;
            return Task.FromResult(Envelope(new LogReadTailResult()));
        }

        public Task<LogOperationEnvelope<LogQueryStatus>> GetStatusAsync(CancellationToken ct = default)
        {
            TotalCalls++;
            StatusCallCount++;
            return Task.FromResult(Envelope(new LogQueryStatus { CacheOwnership = "ui_shared" }));
        }

        public void Dispose()
        {
        }

        private static LogOperationEnvelope<T> Envelope<T>(T result)
            => new(
                1,
                "request",
                LogOperationBackendKind.LiveUi,
                "revision",
                false,
                false,
                [],
                [],
                result);
    }

    private sealed class OneByteReadStream : MemoryStream
    {
        public OneByteReadStream(byte[] buffer)
            : base(buffer)
        {
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
