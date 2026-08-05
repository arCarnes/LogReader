namespace LogReader.Infrastructure.Services;

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed class LiveLogIpcServer : IDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly LiveLogPipeIdentity _identity;
    private readonly ILogQueryBackend _backend;
    private readonly Action<string> _diagnostic;
    private readonly Func<NamedPipeServerStream> _pipeFactory;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _heavyOperationGate = new(1, 1);
    private readonly SemaphoreSlim _lightOperationGate = new(2, 2);
    private readonly ConcurrentDictionary<int, NamedPipeServerStream> _slotPipes = new();
    private readonly object _lifecycleGate = new();
    private Task[] _slotTasks = [];
    private bool _started;
    private bool _disposed;

    public LiveLogIpcServer(
        LiveLogPipeIdentity identity,
        ILogQueryBackend backend,
        Action<string>? diagnostic = null)
        : this(identity, backend, diagnostic, pipeFactory: null)
    {
    }

    internal LiveLogIpcServer(
        LiveLogPipeIdentity identity,
        ILogQueryBackend backend,
        Action<string>? diagnostic,
        Func<NamedPipeServerStream>? pipeFactory)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _diagnostic = diagnostic ?? (_ => { });
        _pipeFactory = pipeFactory ?? CreatePipe;
    }

    public bool TryStart()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return true;

            var initialPipes = new List<NamedPipeServerStream>(LiveLogIpcProtocol.MaximumClients);
            try
            {
                for (var slot = 0; slot < LiveLogIpcProtocol.MaximumClients; slot++)
                    initialPipes.Add(_pipeFactory());

                _slotTasks = initialPipes
                    .Select((pipe, slot) => RunSlotAsync(slot, pipe, _lifetimeCancellation.Token))
                    .ToArray();
                _started = true;
                return true;
            }
            catch (Exception ex) when (IsListenerException(ex))
            {
                foreach (var pipe in initialPipes)
                    pipe.Dispose();
                _diagnostic("live_log_listener_start_failed");
                return false;
            }
        }
    }

    public void BeginStop()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            _lifetimeCancellation.Cancel();
            foreach (var pipe in _slotPipes.Values)
                pipe.Dispose();
        }
    }

    public async Task StopAsync()
    {
        BeginStop();
        Task[] tasks;
        lock (_lifecycleGate)
            tasks = _slotTasks;
        if (tasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(tasks).WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
        {
            _diagnostic("live_log_listener_stop_incomplete");
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _lifetimeCancellation.Cancel();
        foreach (var pipe in _slotPipes.Values)
            pipe.Dispose();
        try
        {
            Task.WhenAll(_slotTasks).Wait(ShutdownTimeout);
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
            _diagnostic("live_log_listener_stop_incomplete");
        }

        if (_slotTasks.All(static task => task.IsCompleted))
        {
            _heavyOperationGate.Dispose();
            _lightOperationGate.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    private async Task RunSlotAsync(int slot, NamedPipeServerStream initialPipe, CancellationToken ct)
    {
        var pipe = initialPipe;
        while (!ct.IsCancellationRequested)
        {
            _slotPipes[slot] = pipe;
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsConnectionException(ex))
            {
                _diagnostic("live_log_connection_failed");
            }
            finally
            {
                _slotPipes.TryRemove(slot, out _);
                pipe.Dispose();
            }

            if (ct.IsCancellationRequested)
                return;

            try
            {
                pipe = _pipeFactory();
            }
            catch (Exception ex) when (IsListenerException(ex))
            {
                _diagnostic("live_log_listener_restart_failed");
                return;
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken serverToken)
    {
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        using var writeGate = new SemaphoreSlim(1, 1);
        using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation.Token);
        handshakeCancellation.CancelAfter(HandshakeTimeout);
        LiveLogIpcFrame? handshake;
        try
        {
            handshake = await LiveLogIpcFraming.ReadFrameAsync(
                pipe,
                ct: handshakeCancellation.Token).ConfigureAwait(false);
        }
        catch (LiveLogIpcProtocolException ex)
        {
            await TryWriteErrorAsync(pipe, writeGate, string.Empty, ex.Code, retryable: false, connectionCancellation.Token)
                .ConfigureAwait(false);
            return;
        }

        if (!ValidateHandshake(handshake, out var handshakeError))
        {
            await TryWriteErrorAsync(
                    pipe,
                    writeGate,
                    handshake?.RequestId ?? string.Empty,
                    handshakeError,
                    retryable: handshakeError == "incompatible_protocol",
                    connectionCancellation.Token)
                .ConfigureAwait(false);
            return;
        }

        await WriteAsync(
            pipe,
            writeGate,
            new LiveLogIpcFrame
            {
                Type = LiveLogIpcProtocol.HandshakeResultFrame,
                ProtocolVersion = LiveLogIpcProtocol.CurrentVersion,
                RequestId = handshake!.RequestId,
                Success = true,
                Capabilities = LiveLogIpcProtocol.Capabilities
            },
            connectionCancellation.Token).ConfigureAwait(false);

        Task? activeRequest = null;
        CancellationTokenSource? activeCancellation = null;
        string? activeRequestId = null;
        try
        {
            while (!connectionCancellation.IsCancellationRequested)
            {
                var frame = await LiveLogIpcFraming.ReadFrameAsync(
                    pipe,
                    ct: connectionCancellation.Token).ConfigureAwait(false);
                if (frame == null)
                    break;

                if (activeRequest?.IsCompleted == true)
                {
                    await ObserveAsync(activeRequest).ConfigureAwait(false);
                    activeRequest = null;
                    activeCancellation?.Dispose();
                    activeCancellation = null;
                    activeRequestId = null;
                }

                if (StringComparer.Ordinal.Equals(frame.Type, LiveLogIpcProtocol.CancelFrame))
                {
                    if (StringComparer.Ordinal.Equals(frame.RequestId, activeRequestId))
                        activeCancellation?.Cancel();
                    continue;
                }

                if (!StringComparer.Ordinal.Equals(frame.Type, LiveLogIpcProtocol.RequestFrame) ||
                    !IsValidRequestId(frame.RequestId) ||
                    frame.ProtocolVersion != LiveLogIpcProtocol.CurrentVersion)
                {
                    await TryWriteErrorAsync(
                        pipe,
                        writeGate,
                        frame.RequestId,
                        "invalid_request",
                        retryable: false,
                        connectionCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                if (activeRequest != null)
                {
                    await TryWriteErrorAsync(
                        pipe,
                        writeGate,
                        frame.RequestId,
                        "connection_busy",
                        retryable: true,
                        connectionCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation.Token);
                activeRequestId = frame.RequestId;
                activeRequest = ProcessAndWriteAsync(pipe, writeGate, frame, activeCancellation.Token);
            }
        }
        catch (LiveLogIpcProtocolException ex)
        {
            await TryWriteErrorAsync(
                pipe,
                writeGate,
                activeRequestId ?? string.Empty,
                ex.Code,
                retryable: false,
                connectionCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            connectionCancellation.Cancel();
            activeCancellation?.Cancel();
            if (activeRequest != null)
                await ObserveAsync(activeRequest).ConfigureAwait(false);
            activeCancellation?.Dispose();
        }
    }

    private async Task ProcessAndWriteAsync(
        NamedPipeServerStream pipe,
        SemaphoreSlim writeGate,
        LiveLogIpcFrame request,
        CancellationToken ct)
    {
        LiveLogIpcFrame response;
        try
        {
            using var admission = await AcquireOperationAsync(request.Operation, ct).ConfigureAwait(false);
            var payload = await DispatchAsync(request, ct).ConfigureAwait(false);
            response = new LiveLogIpcFrame
            {
                Type = LiveLogIpcProtocol.ResponseFrame,
                ProtocolVersion = LiveLogIpcProtocol.CurrentVersion,
                RequestId = request.RequestId,
                Success = true,
                Payload = payload
            };
        }
        catch (OperationCanceledException)
        {
            response = ErrorResponse(request.RequestId, "request_cancelled", retryable: true);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            response = ErrorResponse(request.RequestId, "invalid_request", retryable: false);
        }
        catch (Exception)
        {
            response = ErrorResponse(request.RequestId, "internal_error", retryable: true);
        }

        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        writeCancellation.CancelAfter(ResponseWriteTimeout);
        await WriteAsync(pipe, writeGate, response, writeCancellation.Token).ConfigureAwait(false);
    }

    private async Task<JsonElement> DispatchAsync(LiveLogIpcFrame request, CancellationToken ct)
    {
        var payload = request.Payload;
        object response = request.Operation switch
        {
            LiveLogIpcProtocol.ListLogTreeOperation => await _backend.ListLogTreeAsync(
                Deserialize<ConfiguredLogTreeRequest>(payload), ct).ConfigureAwait(false),
            LiveLogIpcProtocol.SearchLogsOperation => await _backend.SearchLogsAsync(
                Deserialize<LogSearchQuery>(payload), ct).ConfigureAwait(false),
            LiveLogIpcProtocol.ReadLogLinesOperation => await _backend.ReadLogLinesAsync(
                Deserialize<LogReadLinesQuery>(payload), ct).ConfigureAwait(false),
            LiveLogIpcProtocol.ReadLogTailOperation => await _backend.ReadLogTailAsync(
                Deserialize<LogReadTailQuery>(payload), ct).ConfigureAwait(false),
            LiveLogIpcProtocol.ServerStatusOperation => await _backend.GetStatusAsync(ct).ConfigureAwait(false),
            _ => throw new ArgumentException("Unknown live log operation.", nameof(request))
        };
        return JsonSerializer.SerializeToElement(response, response.GetType(), LiveLogIpcFraming.SerializerOptions);
    }

    private async Task<IDisposable> AcquireOperationAsync(string? operation, CancellationToken ct)
    {
        var gate = operation is LiveLogIpcProtocol.ListLogTreeOperation or LiveLogIpcProtocol.ServerStatusOperation
            ? _lightOperationGate
            : _heavyOperationGate;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new SemaphoreReleaser(gate);
    }

    private bool ValidateHandshake(LiveLogIpcFrame? handshake, out string error)
    {
        if (handshake == null ||
            !StringComparer.Ordinal.Equals(handshake.Type, LiveLogIpcProtocol.HandshakeFrame) ||
            !IsValidRequestId(handshake.RequestId))
        {
            error = "invalid_handshake";
            return false;
        }
        if (handshake.ProtocolVersion != LiveLogIpcProtocol.CurrentVersion)
        {
            error = "incompatible_protocol";
            return false;
        }
        if (!StringComparer.Ordinal.Equals(handshake.StorageIdentity, _identity.StorageIdentity))
        {
            error = "storage_identity_mismatch";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static T Deserialize<T>(JsonElement? payload)
    {
        if (payload == null)
            throw new JsonException("A request payload is required.");
        return payload.Value.Deserialize<T>(LiveLogIpcFraming.SerializerOptions)
               ?? throw new JsonException("The request payload is invalid.");
    }

    private static bool IsValidRequestId(string requestId)
        => requestId.Length is > 0 and <= 64 &&
           requestId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private NamedPipeServerStream CreatePipe()
        => new(
            _identity.PipeName,
            PipeDirection.InOut,
            LiveLogIpcProtocol.MaximumClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096,
            outBufferSize: 4096);

    private static LiveLogIpcFrame ErrorResponse(string requestId, string code, bool retryable)
        => new()
        {
            Type = LiveLogIpcProtocol.ResponseFrame,
            ProtocolVersion = LiveLogIpcProtocol.CurrentVersion,
            RequestId = IsValidRequestId(requestId) ? requestId : string.Empty,
            Success = false,
            Error = new LiveLogIpcError(code, "The live WeezTail log operation could not be completed.", retryable)
        };

    private static Task TryWriteErrorAsync(
        NamedPipeServerStream pipe,
        SemaphoreSlim writeGate,
        string requestId,
        string code,
        bool retryable,
        CancellationToken ct)
        => WriteAsync(pipe, writeGate, ErrorResponse(requestId, code, retryable), ct);

    private static async Task WriteAsync(
        NamedPipeServerStream pipe,
        SemaphoreSlim writeGate,
        LiveLogIpcFrame frame,
        CancellationToken ct)
    {
        await writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (pipe.IsConnected)
                await LiveLogIpcFraming.WriteFrameAsync(pipe, frame, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private static bool IsListenerException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static bool IsConnectionException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException or OperationCanceledException or LiveLogIpcProtocolException;

    private sealed class SemaphoreReleaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public SemaphoreReleaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
