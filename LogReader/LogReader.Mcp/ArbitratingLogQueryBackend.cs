namespace LogReader.Mcp;

using System.Collections.Immutable;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;

internal sealed class ArbitratingLogQueryBackend : ILogQueryBackend
{
    private const int MaximumTrackedTailCursors = 256;
    private static readonly TimeSpan LiveProbeCooldown = TimeSpan.FromSeconds(2);

    private readonly Func<CancellationToken, Task<ILogQueryBackend>> _liveFactory;
    private readonly Func<ILogQueryBackend> _headlessFactory;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Dictionary<string, LogOperationBackendKind> _tailCursorBackends = new(StringComparer.Ordinal);
    private readonly Queue<string> _tailCursorOrder = new();
    private ILogQueryBackend? _live;
    private ILogQueryBackend? _headless;
    private DateTime _nextLiveProbeUtc = DateTime.MinValue;
    private string _lastFallbackReason = "none";
    private int _disposed;

    public ArbitratingLogQueryBackend(
        Func<CancellationToken, Task<ILogQueryBackend>> liveFactory,
        Func<ILogQueryBackend> headlessFactory,
        Func<DateTime>? utcNow = null)
    {
        _liveFactory = liveFactory ?? throw new ArgumentNullException(nameof(liveFactory));
        _headlessFactory = headlessFactory ?? throw new ArgumentNullException(nameof(headlessFactory));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public Task<LogOperationEnvelope<ConfiguredLogTreeResult>> ListLogTreeAsync(
        ConfiguredLogTreeRequest request,
        CancellationToken ct = default)
        => ExecuteAsync((backend, token) => backend.ListLogTreeAsync(request, token), ct);

    public Task<LogOperationEnvelope<LogSearchResult>> SearchLogsAsync(
        LogSearchQuery request,
        CancellationToken ct = default)
        => ExecuteAsync((backend, token) => backend.SearchLogsAsync(request, token), ct);

    public Task<LogOperationEnvelope<LogReadLinesResult>> ReadLogLinesAsync(
        LogReadLinesQuery request,
        CancellationToken ct = default)
        => ExecuteAsync((backend, token) => backend.ReadLogLinesAsync(request, token), ct);

    public async Task<LogOperationEnvelope<LogReadTailResult>> ReadLogTailAsync(
        LogReadTailQuery request,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _lifetimeCancellation.Token);
        await _requestGate.WaitAsync(requestCancellation.Token).ConfigureAwait(false);
        try
        {
            var token = requestCancellation.Token;
            var selected = await SelectBackendAsync(token).ConfigureAwait(false);
            var resetCursor = request.Cursor != null &&
                              _tailCursorBackends.TryGetValue(request.Cursor, out var cursorBackend) &&
                              cursorBackend != selected.Kind;
            var effectiveRequest = resetCursor ? WithoutCursor(request) : request;
            LogOperationEnvelope<LogReadTailResult> response;
            try
            {
                response = await selected.Backend.ReadLogTailAsync(effectiveRequest, token).ConfigureAwait(false);
            }
            catch (LiveLogBackendUnavailableException ex) when (selected.Kind == LogOperationBackendKind.LiveUi)
            {
                LoseLiveBackend(ex.Reason);
                selected = new SelectedBackend(GetHeadlessBackend(), LogOperationBackendKind.Headless);
                resetCursor = request.Cursor != null;
                response = await selected.Backend.ReadLogTailAsync(
                    resetCursor ? WithoutCursor(request) : request,
                    token).ConfigureAwait(false);
            }

            if (resetCursor)
                response = MarkCursorReset(response);
            TrackTailCursor(response.Result?.NextCursor, selected.Kind);
            return response;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public Task<LogOperationEnvelope<LogQueryStatus>> GetStatusAsync(CancellationToken ct = default)
        => ExecuteAsync(
            async (backend, token) =>
            {
                var response = await backend.GetStatusAsync(token).ConfigureAwait(false);
                if (response.Result == null)
                    return response;

                var status = response.Result;
                return new LogOperationEnvelope<LogQueryStatus>(
                    response.SchemaVersion,
                    response.RequestId,
                    response.Backend,
                    response.CatalogRevision,
                    response.IsPartial,
                    response.IsTruncated,
                    response.TruncationReasons,
                    response.Errors,
                    new LogQueryStatus
                    {
                        IsReady = status.IsReady,
                        ConnectionState = status.ConnectionState,
                        CacheOwnership = status.CacheOwnership,
                        Limits = status.Limits,
                        ActiveIndexedSessions = status.ActiveIndexedSessions,
                        RetainedIndexedSessions = status.RetainedIndexedSessions,
                        MappedLineOffsets = status.MappedLineOffsets,
                        LiveUiAvailable = _live != null,
                        LastFallbackReason = _lastFallbackReason
                    });
            },
            ct);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCancellation.Cancel();
        if (!_requestGate.Wait(TimeSpan.FromSeconds(2)))
            return;
        try
        {
            _live?.Dispose();
            _headless?.Dispose();
            _live = null;
            _headless = null;
            _tailCursorBackends.Clear();
            _tailCursorOrder.Clear();
        }
        finally
        {
            _requestGate.Release();
            _requestGate.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    private async Task<LogOperationEnvelope<T>> ExecuteAsync<T>(
        Func<ILogQueryBackend, CancellationToken, Task<LogOperationEnvelope<T>>> operation,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _lifetimeCancellation.Token);
        await _requestGate.WaitAsync(requestCancellation.Token).ConfigureAwait(false);
        try
        {
            var token = requestCancellation.Token;
            var selected = await SelectBackendAsync(token).ConfigureAwait(false);
            try
            {
                return await operation(selected.Backend, token).ConfigureAwait(false);
            }
            catch (LiveLogBackendUnavailableException ex) when (selected.Kind == LogOperationBackendKind.LiveUi)
            {
                LoseLiveBackend(ex.Reason);
                return await operation(GetHeadlessBackend(), token).ConfigureAwait(false);
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<SelectedBackend> SelectBackendAsync(CancellationToken ct)
    {
        if (_live != null)
            return new SelectedBackend(_live, LogOperationBackendKind.LiveUi);

        if (_utcNow() >= _nextLiveProbeUtc)
        {
            try
            {
                _live = await _liveFactory(ct).ConfigureAwait(false);
                _headless?.Dispose();
                _headless = null;
                return new SelectedBackend(_live, LogOperationBackendKind.LiveUi);
            }
            catch (LiveLogBackendUnavailableException ex)
            {
                _lastFallbackReason = NormalizeFallbackReason(ex.Reason);
                _nextLiveProbeUtc = _utcNow() + LiveProbeCooldown;
            }
        }

        return new SelectedBackend(GetHeadlessBackend(), LogOperationBackendKind.Headless);
    }

    private ILogQueryBackend GetHeadlessBackend()
        => _headless ??= _headlessFactory();

    private void LoseLiveBackend(string reason)
    {
        _live?.Dispose();
        _live = null;
        _lastFallbackReason = NormalizeFallbackReason(reason);
        _nextLiveProbeUtc = _utcNow() + LiveProbeCooldown;
    }

    private void TrackTailCursor(string? cursor, LogOperationBackendKind backend)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return;
        if (_tailCursorBackends.TryAdd(cursor, backend))
            _tailCursorOrder.Enqueue(cursor);
        while (_tailCursorOrder.Count > MaximumTrackedTailCursors)
            _tailCursorBackends.Remove(_tailCursorOrder.Dequeue());
    }

    private static LogReadTailQuery WithoutCursor(LogReadTailQuery request)
        => new()
        {
            FileId = request.FileId,
            Cursor = null,
            MaxLines = request.MaxLines,
            DateOffsetDays = request.DateOffsetDays,
            TimeoutMilliseconds = request.TimeoutMilliseconds
        };

    private static LogOperationEnvelope<LogReadTailResult> MarkCursorReset(
        LogOperationEnvelope<LogReadTailResult> response)
    {
        var result = response.Result;
        var resetResult = result == null
            ? null
            : new LogReadTailResult
            {
                File = result.File,
                NextCursor = result.NextCursor,
                GenerationChanged = true,
                LastLineUpdated = result.LastLineUpdated,
                TotalLineCount = result.TotalLineCount
            };
        return new LogOperationEnvelope<LogReadTailResult>(
            response.SchemaVersion,
            response.RequestId,
            response.Backend,
            response.CatalogRevision,
            response.IsPartial,
            IsTruncated: true,
            response.TruncationReasons.Contains("backend_cursor_reset", StringComparer.Ordinal)
                ? response.TruncationReasons
                : response.TruncationReasons.Add("backend_cursor_reset"),
            response.Errors,
            resetResult);
    }

    private static string NormalizeFallbackReason(string reason)
        => reason switch
        {
            "connect_timeout" => "live_connect_timeout",
            "incompatible_protocol" or "incompatible_handshake" => "live_protocol_incompatible",
            "storage_identity_mismatch" => "live_storage_mismatch",
            "connection_lost" or "partial_frame" => "live_connection_lost",
            _ => "live_unavailable"
        };

    private sealed record SelectedBackend(ILogQueryBackend Backend, LogOperationBackendKind Kind);
}

internal sealed class OwnedHeadlessLogQueryBackend : ILogQueryBackend
{
    private readonly PersistedDashboardSnapshotReader _catalog;
    private readonly HeadlessLogQueryBackend _backend;

    public OwnedHeadlessLogQueryBackend()
    {
        _catalog = new PersistedDashboardSnapshotReader();
        var logReader = new ChunkedLogReaderService();
        var encodingDetection = new FileEncodingDetectionService();
        _backend = new HeadlessLogQueryBackend(
            _catalog,
            new SearchService(),
            encodingDetection,
            logReader,
            new IndexedLogSessionCache(logReader, encodingDetection));
    }

    public Task<LogOperationEnvelope<ConfiguredLogTreeResult>> ListLogTreeAsync(ConfiguredLogTreeRequest request, CancellationToken ct = default)
        => _backend.ListLogTreeAsync(request, ct);

    public Task<LogOperationEnvelope<LogSearchResult>> SearchLogsAsync(LogSearchQuery request, CancellationToken ct = default)
        => _backend.SearchLogsAsync(request, ct);

    public Task<LogOperationEnvelope<LogReadLinesResult>> ReadLogLinesAsync(LogReadLinesQuery request, CancellationToken ct = default)
        => _backend.ReadLogLinesAsync(request, ct);

    public Task<LogOperationEnvelope<LogReadTailResult>> ReadLogTailAsync(LogReadTailQuery request, CancellationToken ct = default)
        => _backend.ReadLogTailAsync(request, ct);

    public Task<LogOperationEnvelope<LogQueryStatus>> GetStatusAsync(CancellationToken ct = default)
        => _backend.GetStatusAsync(ct);

    public void Dispose()
    {
        _backend.Dispose();
        _catalog.Dispose();
    }
}
