namespace LogReader.Mcp;

using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

internal sealed class LiveLogIpcClientBackend : ILogQueryBackend
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CancellationResponseTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private int _disposed;

    private LiveLogIpcClientBackend(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
    }

    public static async Task<LiveLogIpcClientBackend> ConnectAsync(CancellationToken ct = default)
    {
        LiveLogPipeIdentity identity;
        try
        {
            identity = LiveLogPipeIdentityFactory.CreateCurrentForConfiguredStorage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            throw new LiveLogBackendUnavailableException("storage_identity_unavailable", ex);
        }

        return await ConnectAsync(identity, ConnectTimeout, HandshakeTimeout, ct).ConfigureAwait(false);
    }

    internal static async Task<LiveLogIpcClientBackend> ConnectAsync(
        LiveLogPipeIdentity identity,
        TimeSpan connectTimeout,
        TimeSpan handshakeTimeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (connectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        if (handshakeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        var pipe = new NamedPipeClientStream(
            ".",
            identity.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using (var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCancellation.CancelAfter(connectTimeout);
                await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);
            }

            using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCancellation.CancelAfter(handshakeTimeout);
            var requestId = CreateRequestId();
            await LiveLogIpcFraming.WriteFrameAsync(
                pipe,
                new LiveLogIpcFrame
                {
                    Type = LiveLogIpcProtocol.HandshakeFrame,
                    ProtocolVersion = LiveLogIpcProtocol.CurrentVersion,
                    RequestId = requestId,
                    StorageIdentity = identity.StorageIdentity
                },
                ct: handshakeCancellation.Token).ConfigureAwait(false);
            var response = await LiveLogIpcFraming.ReadFrameAsync(
                pipe,
                ct: handshakeCancellation.Token).ConfigureAwait(false);
            if (response is not
                {
                    Type: LiveLogIpcProtocol.HandshakeResultFrame,
                    ProtocolVersion: LiveLogIpcProtocol.CurrentVersion,
                    Success: true
                } ||
                !StringComparer.Ordinal.Equals(response.RequestId, requestId) ||
                !LiveLogIpcProtocol.Capabilities.All(response.Capabilities.Contains))
            {
                throw new LiveLogBackendUnavailableException(
                    response?.Error?.Code ?? "incompatible_handshake");
            }

            return new LiveLogIpcClientBackend(pipe);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            pipe.Dispose();
            throw new LiveLogBackendUnavailableException("connect_timeout", ex);
        }
        catch (LiveLogBackendUnavailableException)
        {
            pipe.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or LiveLogIpcProtocolException)
        {
            pipe.Dispose();
            throw new LiveLogBackendUnavailableException("connect_failed", ex);
        }
    }

    public Task<LogOperationEnvelope<ConfiguredLogTreeResult>> ListLogTreeAsync(
        ConfiguredLogTreeRequest request,
        CancellationToken ct = default)
        => InvokeAsync<ConfiguredLogTreeRequest, ConfiguredLogTreeResult>(
            LiveLogIpcProtocol.ListLogTreeOperation,
            request,
            ct);

    public Task<LogOperationEnvelope<LogSearchResult>> SearchLogsAsync(
        LogSearchQuery request,
        CancellationToken ct = default)
        => InvokeAsync<LogSearchQuery, LogSearchResult>(LiveLogIpcProtocol.SearchLogsOperation, request, ct);

    public Task<LogOperationEnvelope<LogReadLinesResult>> ReadLogLinesAsync(
        LogReadLinesQuery request,
        CancellationToken ct = default)
        => InvokeAsync<LogReadLinesQuery, LogReadLinesResult>(LiveLogIpcProtocol.ReadLogLinesOperation, request, ct);

    public Task<LogOperationEnvelope<LogReadTailResult>> ReadLogTailAsync(
        LogReadTailQuery request,
        CancellationToken ct = default)
        => InvokeAsync<LogReadTailQuery, LogReadTailResult>(LiveLogIpcProtocol.ReadLogTailOperation, request, ct);

    public Task<LogOperationEnvelope<LogQueryStatus>> GetStatusAsync(CancellationToken ct = default)
        => InvokeAsync<object?, LogQueryStatus>(LiveLogIpcProtocol.ServerStatusOperation, payload: null, ct);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCancellation.Cancel();
        _pipe.Dispose();
        _requestGate.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task<LogOperationEnvelope<TResponse>> InvokeAsync<TRequest, TResponse>(
        string operation,
        TRequest? payload,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                _lifetimeCancellation.Token);
            var requestId = CreateRequestId();
            var frame = new LiveLogIpcFrame
            {
                Type = LiveLogIpcProtocol.RequestFrame,
                ProtocolVersion = LiveLogIpcProtocol.CurrentVersion,
                RequestId = requestId,
                Operation = operation,
                Payload = payload == null
                    ? null
                    : JsonSerializer.SerializeToElement(payload, SerializerOptions)
            };

            await LiveLogIpcFraming.WriteFrameAsync(
                _pipe,
                frame,
                ct: requestCancellation.Token).ConfigureAwait(false);
            var responseTask = LiveLogIpcFraming.ReadFrameAsync(
                _pipe,
                ct: _lifetimeCancellation.Token);
            LiveLogIpcFrame? response;
            if (!ct.CanBeCanceled)
            {
                response = await responseTask.ConfigureAwait(false);
            }
            else
            {
                var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (await Task.WhenAny(responseTask, cancellationTask).ConfigureAwait(false) != responseTask)
                {
                    await TryCancelAsync(requestId).ConfigureAwait(false);
                    try
                    {
                        await responseTask.WaitAsync(CancellationResponseTimeout).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
                    {
                        Invalidate();
                    }

                    ct.ThrowIfCancellationRequested();
                }

                response = await responseTask.ConfigureAwait(false);
            }

            if (response == null ||
                !StringComparer.Ordinal.Equals(response.Type, LiveLogIpcProtocol.ResponseFrame) ||
                response.ProtocolVersion != LiveLogIpcProtocol.CurrentVersion ||
                !StringComparer.Ordinal.Equals(response.RequestId, requestId))
            {
                throw new LiveLogBackendUnavailableException("invalid_response");
            }
            if (response.Success != true)
            {
                if (response.Error?.Code == "request_cancelled" && ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                throw new LiveLogBackendUnavailableException(response.Error?.Code ?? "live_request_failed");
            }
            if (response.Payload == null)
                throw new LiveLogBackendUnavailableException("missing_response_payload");

            var envelope = response.Payload.Value.Deserialize<LogOperationEnvelope<TResponse>>(SerializerOptions)
                           ?? throw new LiveLogBackendUnavailableException("invalid_response_payload");
            if (envelope.Backend != LogOperationBackendKind.LiveUi)
                throw new LiveLogBackendUnavailableException("unexpected_backend");
            return envelope;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LiveLogBackendUnavailableException)
        {
            Invalidate();
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or JsonException or LiveLogIpcProtocolException)
        {
            Invalidate();
            throw new LiveLogBackendUnavailableException("connection_lost", ex);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task TryCancelAsync(string requestId)
    {
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
            await LiveLogIpcFraming.WriteFrameAsync(
                _pipe,
                new LiveLogIpcFrame
                {
                    Type = LiveLogIpcProtocol.CancelFrame,
                    ProtocolVersion = LiveLogIpcProtocol.CurrentVersion,
                    RequestId = requestId
                },
                ct: cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private void Invalidate()
    {
        try
        {
            _lifetimeCancellation.Cancel();
            _pipe.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string CreateRequestId() => Guid.NewGuid().ToString("N");
}

internal sealed class LiveLogBackendUnavailableException : IOException
{
    public LiveLogBackendUnavailableException(string reason, Exception? innerException = null)
        : base("The live WeezTail log backend is unavailable.", innerException)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? "unavailable" : reason;
    }

    public string Reason { get; }
}
