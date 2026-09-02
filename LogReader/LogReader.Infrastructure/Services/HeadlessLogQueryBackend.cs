namespace LogReader.Infrastructure.Services;

using System.Collections.Immutable;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

/// <summary>
/// Executes bounded configured-log queries without persistence writes. Index ownership is supplied by composition.
/// </summary>
public sealed partial class HeadlessLogQueryBackend : ILogQueryBackend
{
    private readonly IConfiguredLogCatalogReader _catalogReader;
    private readonly ISearchService _searchService;
    private readonly IEncodingDetectionService _encodingDetection;
    private readonly IBoundedLogReaderService _logReader;
    private readonly IIndexedLogSessionProvider _indexedSessions;
    private readonly DashboardSelectionResolver _selectionResolver;
    private readonly ConfiguredLogTreeProjector _treeProjector;
    private readonly TailCursorCodec _cursorCodec;
    private readonly SearchCursorCodec _searchCursorCodec;
    private readonly LogQueryEffectiveLimits _limits;
    private readonly Func<DateOnly> _today;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly Func<string, bool> _pathExists;
    private readonly SemaphoreSlim _heavyRequestGate;
    private readonly SemaphoreSlim _diskOperationGate;
    private readonly SemaphoreSlim _uncOperationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lifetimeGate = new();
    private readonly AsyncLocal<QueryOperationMetrics?> _queryOperationMetrics = new();
    private int _activeRequests;
    private bool _disposed;
    private bool _resourcesDisposed;

    public HeadlessLogQueryBackend(
        IConfiguredLogCatalogReader catalogReader,
        ISearchService searchService,
        IEncodingDetectionService encodingDetection,
        IBoundedLogReaderService logReader,
        IIndexedLogSessionProvider indexedSessions,
        LogQueryEffectiveLimits? limits = null)
        : this(
            catalogReader,
            searchService,
            encodingDetection,
            logReader,
            indexedSessions,
            limits,
            () => DateOnly.FromDateTime(DateTime.Today),
            new TailCursorCodec(),
            searchCursorCodec: new SearchCursorCodec(),
            now: () => DateTimeOffset.Now,
            localTimeZone: TimeZoneInfo.Local)
    {
    }

    internal HeadlessLogQueryBackend(
        IConfiguredLogCatalogReader catalogReader,
        ISearchService searchService,
        IEncodingDetectionService encodingDetection,
        IBoundedLogReaderService logReader,
        IIndexedLogSessionProvider indexedSessions,
        LogQueryEffectiveLimits? limits,
        Func<DateOnly> today,
        TailCursorCodec cursorCodec,
        Func<string, bool>? pathExists = null,
        SearchCursorCodec? searchCursorCodec = null,
        Func<DateTimeOffset>? now = null,
        TimeZoneInfo? localTimeZone = null)
    {
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _encodingDetection = encodingDetection ?? throw new ArgumentNullException(nameof(encodingDetection));
        _logReader = logReader ?? throw new ArgumentNullException(nameof(logReader));
        _indexedSessions = indexedSessions ?? throw new ArgumentNullException(nameof(indexedSessions));
        _selectionResolver = new DashboardSelectionResolver();
        _treeProjector = new ConfiguredLogTreeProjector();
        _cursorCodec = cursorCodec ?? throw new ArgumentNullException(nameof(cursorCodec));
        _searchCursorCodec = searchCursorCodec ?? new SearchCursorCodec();
        _today = today ?? throw new ArgumentNullException(nameof(today));
        _now = now ?? (() => DateTimeOffset.Now);
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        _pathExists = pathExists ?? File.Exists;

        var cache = indexedSessions.GetProviderSnapshot();
        _limits = limits ?? LogQueryEffectiveLimits.Default with
        {
            MaximumIndexedSessions = cache.MaximumSessions,
            MaximumMappedLineOffsets = cache.MaximumMappedLineOffsets,
            IndexedSessionWarmRetentionMilliseconds = checked((int)Math.Min(
                int.MaxValue,
                cache.WarmRetentionDuration.TotalMilliseconds))
        };
        ValidateLimits(_limits);
        _heavyRequestGate = new SemaphoreSlim(_limits.MaximumConcurrentDiskOperations);
        _diskOperationGate = new SemaphoreSlim(_limits.MaximumConcurrentDiskOperations);
    }

    public async Task<LogOperationEnvelope<ConfiguredLogTreeResult>> ListLogTreeAsync(
        ConfiguredLogTreeRequest request,
        CancellationToken ct = default)
    {
        using var requestLease = BeginRequest();
        ArgumentNullException.ThrowIfNull(request);
        var requestId = CreateRequestId();
        using var scope = CreateDeadlineScope(timeoutMilliseconds: null, ct);
        try
        {
            var catalogRead = await _catalogReader.ReadAsync(scope.Token).ConfigureAwait(false);
            if (!catalogRead.IsSuccess)
                return Failure<ConfiguredLogTreeResult>(requestId, catalogRead.Error!);

            var result = _treeProjector.Project(catalogRead.Snapshot!, request);
            var truncationReasons = result.NextStartIndex.HasValue
                ? ImmutableArray.Create("tree_node_limit")
                : ImmutableArray<string>.Empty;
            if (result.DepthTruncated)
                truncationReasons = truncationReasons.Add("tree_depth_limit");
            if (result.ResponseBudgetTruncated)
                truncationReasons = truncationReasons.Add("tree_response_limit");

            return Envelope(
                requestId,
                result.CatalogRevision,
                isPartial: false,
                result.IsTruncated,
                truncationReasons,
                result.Errors,
                result);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<ConfiguredLogTreeResult>(requestId, ct);
        }
    }

    public async Task<LogOperationEnvelope<LogSearchResult>> SearchLogsAsync(
        LogSearchQuery request,
        CancellationToken ct = default)
    {
        using var requestLease = BeginRequest();
        ArgumentNullException.ThrowIfNull(request);
        var requestId = CreateRequestId();
        var validation = ValidateSearchRequest(request, out var effectiveFileLimit, out var effectiveHitsPerFile, out var effectiveTotalHits);
        if (!validation.IsEmpty)
            return Rejected<LogSearchResult>(requestId, validation);

        _queryOperationMetrics.Value = new QueryOperationMetrics();
        using var scope = CreateDeadlineScope(request.TimeoutMilliseconds, ct);
        try
        {
            await _heavyRequestGate.WaitAsync(scope.Token).ConfigureAwait(false);
            try
            {
                var catalogRead = await _catalogReader.ReadAsync(scope.Token).ConfigureAwait(false);
                if (!catalogRead.IsSuccess)
                    return Failure<LogSearchResult>(requestId, catalogRead.Error!);

                var requestFingerprint = CreateSearchRequestFingerprint(
                    request,
                    effectiveFileLimit,
                    effectiveHitsPerFile,
                    effectiveTotalHits);
                var targetFingerprint = CreateTargetFingerprint(request.Targets);
                var referenceDate = default(DateOnly);
                SearchCursorPayload? cursorPayload = null;
                ConfiguredLogSelectionContinuation? continuation = null;
                if (request.Cursor != null)
                {
                    if (!_searchCursorCodec.TryDecode(request.Cursor, out cursorPayload))
                    {
                        return Rejected<LogSearchResult>(
                            requestId,
                            [Error("invalid_search_cursor", "The search cursor is malformed or invalid.")],
                            catalogRead.Snapshot!.Revision);
                    }

                    if (!StringComparer.Ordinal.Equals(cursorPayload!.CatalogRevision, catalogRead.Snapshot!.Revision))
                    {
                        return Rejected<LogSearchResult>(
                            requestId,
                            [Error("stale_search_cursor", "The configured log catalog changed after the cursor was issued.")],
                            catalogRead.Snapshot.Revision);
                    }

                    if (!StringComparer.Ordinal.Equals(cursorPayload.RequestFingerprint, requestFingerprint) ||
                        !StringComparer.Ordinal.Equals(cursorPayload.TargetFingerprint, targetFingerprint) ||
                        cursorPayload.DateOffsetDays != request.DateOffsetDays)
                    {
                        return Rejected<LogSearchResult>(
                            requestId,
                            [Error("mismatched_search_cursor", "The search cursor does not match this request.")],
                            catalogRead.Snapshot.Revision);
                    }

                    continuation = new ConfiguredLogSelectionContinuation(
                        cursorPayload.NextStableFileIndex,
                        cursorPayload.SeenPhysicalPathIdentities.ToImmutableArray());
                    referenceDate = DateOnly.FromDayNumber(cursorPayload.ReferenceDateDayNumber);
                }
                else
                {
                    referenceDate = _today();
                }

                var selection = await ResolveAsync(
                    catalogRead.Snapshot!,
                    request.Targets,
                    request.DateOffsetDays,
                    referenceDate,
                    effectiveFileLimit,
                    continuation,
                    scope.Token).ConfigureAwait(false);
                if (!selection.IsSuccess)
                {
                    var selectionLimitReason = selection.Errors.Any(error =>
                        StringComparer.Ordinal.Equals(error.Code, "search_candidate_limit_exceeded"))
                        ? "search_candidate_limit"
                        : "resolved_file_limit";
                    return Envelope<LogSearchResult>(
                        requestId,
                        selection.CatalogRevision,
                        isPartial: false,
                        isTruncated: selection.Summary.RejectedByLimit,
                        selection.Summary.RejectedByLimit ? [selectionLimitReason] : [],
                        selection.Errors,
                        result: null);
                }

                var searchOutcome = await SearchSelectedFilesAsync(
                    selection.Files,
                    request,
                    effectiveHitsPerFile,
                    scope.Token).ConfigureAwait(false);
                scope.Token.ThrowIfCancellationRequested();

                return await BuildSearchEnvelopeAsync(
                    requestId,
                    selection,
                    searchOutcome.Results,
                    searchOutcome.ElapsedMilliseconds,
                    cursorPayload,
                    requestFingerprint,
                    targetFingerprint,
                    referenceDate,
                    request,
                    effectiveFileLimit,
                    effectiveHitsPerFile,
                    effectiveTotalHits,
                    scope.Token).ConfigureAwait(false);
            }
            finally
            {
                _heavyRequestGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return Cancelled<LogSearchResult>(requestId, ct);
        }
    }

    public async Task<LogOperationEnvelope<LogReadLinesResult>> ReadLogLinesAsync(
        LogReadLinesQuery request,
        CancellationToken ct = default)
    {
        using var requestLease = BeginRequest();
        ArgumentNullException.ThrowIfNull(request);
        var requestId = CreateRequestId();
        var count = request.Count ?? _limits.DefaultReadLineCount;
        var validation = ValidateReadRequest(
            request.FileId,
            request.StartLine,
            count,
            request.DateOffsetDays,
            request.TimeoutMilliseconds);
        if (!validation.IsEmpty)
            return Rejected<LogReadLinesResult>(requestId, validation);

        using var scope = CreateDeadlineScope(request.TimeoutMilliseconds, ct);
        try
        {
            await _heavyRequestGate.WaitAsync(scope.Token).ConfigureAwait(false);
            try
            {
                var catalogRead = await _catalogReader.ReadAsync(scope.Token).ConfigureAwait(false);
                if (!catalogRead.IsSuccess)
                    return Failure<LogReadLinesResult>(requestId, catalogRead.Error!);

                var selection = await ResolveSingleFileAsync(
                    catalogRead.Snapshot!,
                    request.FileId,
                    request.DateOffsetDays,
                    scope.Token).ConfigureAwait(false);
                if (!selection.IsSuccess)
                    return SelectionFailure<LogReadLinesResult>(requestId, selection);
                if (selection.Files.IsEmpty)
                    return SelectionFileFailure<LogReadLinesResult>(requestId, selection);

                var file = selection.Files[0];
                var provenanceBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters / 4);
                var retainedProvenance = RetainProvenance(file.Provenance, provenanceBudget);
                var responseBudget = new ResponseCharacterBudget(
                    _limits.MaximumResponseCharacters - provenanceBudget.Consumed);
                LogReadLinesResult result;
                try
                {
                    result = await ExecuteDiskOperationAsync(
                        file.PhysicalPath,
                        async token =>
                        {
                            using var lease = _indexedSessions.AcquireSession(file.PhysicalPath);
                            var snapshot = await lease.CaptureCurrentIndexAsync(
                                [new IndexedLogReadRange(request.StartLine - 1, count)],
                                token).ConfigureAwait(false);
                            var lines = await ReadSnapshotAsync(
                                lease,
                                file.PhysicalPath,
                                snapshot,
                                responseBudget.Remaining,
                                token).ConfigureAwait(false);
                            var mapped = MapLines(lines, responseBudget);
                            return new LogReadLinesResult
                            {
                                File = new LogReadFileResult(
                                    file.FileId,
                                    file.DisplayName,
                                    retainedProvenance.Items,
                                    EncodingName(snapshot.Encoding),
                                    _cursorCodec.GetGenerationIdentity(snapshot),
                                    mapped,
                                    Error: null)
                                {
                                    ProvenanceTotalCount = retainedProvenance.TotalCount,
                                    IsProvenanceTruncated = retainedProvenance.IsTruncated
                                },
                                RequestedStartLine = request.StartLine,
                                RequestedCount = count,
                                ActualStartLine = mapped.IsEmpty ? null : mapped[0].LineNumber,
                                ActualEndLine = mapped.IsEmpty ? null : mapped[^1].LineNumber,
                                TotalLineCount = snapshot.TotalLineCount
                            };
                        },
                        scope.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsPerFileException(ex))
                {
                    result = new LogReadLinesResult
                    {
                        File = FailedReadFile(file, retainedProvenance, ex),
                        RequestedStartLine = request.StartLine,
                        RequestedCount = count
                    };
                }

                var truncated = retainedProvenance.IsTruncated ||
                                responseBudget.IsExhausted ||
                                result.File?.Lines.Any(static line => line.IsTruncated) == true;
                return Envelope(
                    requestId,
                    selection.CatalogRevision,
                    isPartial: result.File?.Error != null,
                    truncated,
                    GetLineTruncationReasons(result.File?.Lines ?? [], responseBudget, retainedProvenance.IsTruncated),
                    errors: [],
                    result);
            }
            finally
            {
                _heavyRequestGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return Cancelled<LogReadLinesResult>(requestId, ct);
        }
    }

    public async Task<LogOperationEnvelope<LogReadTailResult>> ReadLogTailAsync(
        LogReadTailQuery request,
        CancellationToken ct = default)
    {
        using var requestLease = BeginRequest();
        ArgumentNullException.ThrowIfNull(request);
        var requestId = CreateRequestId();
        var maxLines = request.MaxLines ?? _limits.DefaultReadLineCount;
        var validation = ValidateTailRequest(request, maxLines, out var cursor);
        if (!validation.IsEmpty)
            return Rejected<LogReadTailResult>(requestId, validation);

        using var scope = CreateDeadlineScope(request.TimeoutMilliseconds, ct);
        try
        {
            await _heavyRequestGate.WaitAsync(scope.Token).ConfigureAwait(false);
            try
            {
                var catalogRead = await _catalogReader.ReadAsync(scope.Token).ConfigureAwait(false);
                if (!catalogRead.IsSuccess)
                    return Failure<LogReadTailResult>(requestId, catalogRead.Error!);

                var selection = await ResolveSingleFileAsync(
                    catalogRead.Snapshot!,
                    request.FileId,
                    request.DateOffsetDays,
                    scope.Token).ConfigureAwait(false);
                if (!selection.IsSuccess)
                    return SelectionFailure<LogReadTailResult>(requestId, selection);
                if (selection.Files.IsEmpty)
                    return SelectionFileFailure<LogReadTailResult>(requestId, selection);

                var file = selection.Files[0];
                var pathIdentity = _cursorCodec.GetPathIdentity(file.PhysicalPath);
                if (cursor != null &&
                    (!StringComparer.Ordinal.Equals(cursor.FileId, file.FileId) ||
                     !StringComparer.Ordinal.Equals(cursor.PathIdentity, pathIdentity)))
                {
                    return Rejected<LogReadTailResult>(
                        requestId,
                        [Error("invalid_tail_cursor", "The tail cursor does not belong to the selected configured log file.")],
                        selection.CatalogRevision);
                }

                var provenanceBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters / 4);
                var retainedProvenance = RetainProvenance(file.Provenance, provenanceBudget);
                var responseBudget = new ResponseCharacterBudget(
                    _limits.MaximumResponseCharacters - provenanceBudget.Consumed);
                LogReadTailResult result;
                try
                {
                    result = await ExecuteDiskOperationAsync(
                        file.PhysicalPath,
                        async token =>
                        {
                            using var lease = _indexedSessions.AcquireSession(file.PhysicalPath);
                            for (var attempt = 0; attempt < 2; attempt++)
                            {
                                IReadOnlyList<IndexedLogReadRange> probeRanges = cursor is { LastLineNumber: > 0 }
                                    ? [new IndexedLogReadRange(cursor.LastLineNumber - 1, 1)]
                                    : [];
                                var metadata = await lease.CaptureCurrentIndexAsync(probeRanges, token).ConfigureAwait(false);
                                if (cursor != null && cursor.Encoding != metadata.Encoding)
                                    throw new InvalidTailCursorException();

                                var generation = _cursorCodec.GetGenerationIdentity(metadata);
                                var generationChanged = cursor != null &&
                                    (!StringComparer.Ordinal.Equals(cursor.GenerationIdentity, generation) ||
                                     cursor.FileSize > metadata.FileSize ||
                                     !CursorOffsetStillMatches(cursor, metadata));
                                var previousTailLineExtended = cursor != null &&
                                    !generationChanged &&
                                    cursor.LastLineNumber > 0 &&
                                    cursor.FileSize < metadata.FileSize &&
                                    metadata.TryGetLineBounds(cursor.LastLineNumber - 1, out var previousBounds) &&
                                    previousBounds!.EndOffset > cursor.FileSize &&
                                    await AppendStartsWithLineContentAsync(
                                        file.PhysicalPath,
                                        cursor.FileSize,
                                        metadata.FileSize,
                                        metadata.Encoding,
                                        token).ConfigureAwait(false);
                                var lastLineUpdated = false;
                                int startIndex;
                                if (cursor == null || generationChanged)
                                {
                                    startIndex = Math.Max(0, metadata.TotalLineCount - maxLines);
                                }
                                else if (previousTailLineExtended)
                                {
                                    startIndex = cursor.LastLineNumber - 1;
                                    lastLineUpdated = true;
                                }
                                else
                                {
                                    startIndex = Math.Min(cursor.LastLineNumber, metadata.TotalLineCount);
                                }

                                var snapshot = await lease.CaptureCurrentIndexAsync(
                                    [new IndexedLogReadRange(startIndex, maxLines)],
                                    token).ConfigureAwait(false);
                                if (!metadata.HasSameSourceAs(snapshot))
                                {
                                    if (attempt == 0)
                                        continue;
                                    throw new IOException("The log index changed while preparing the tail read.");
                                }

                                var lines = await ReadSnapshotAsync(
                                    lease,
                                    file.PhysicalPath,
                                    snapshot,
                                    responseBudget.Remaining,
                                    token).ConfigureAwait(false);
                                var mapped = MapLines(lines, responseBudget);
                                var lastLineNumber = mapped.IsEmpty
                                    ? Math.Min(cursor?.LastLineNumber ?? 0, snapshot.TotalLineCount)
                                    : mapped[^1].LineNumber;
                                var lastOffset = TryGetLineStartOffset(snapshot, lastLineNumber, out var offset)
                                    ? offset
                                    : TryGetLineStartOffset(metadata, lastLineNumber, out offset)
                                        ? offset
                                        : 0;
                                var nextCursor = _cursorCodec.Encode(new TailCursorPayload(
                                    Version: 1,
                                    file.FileId,
                                    pathIdentity,
                                    snapshot.Encoding,
                                    generation,
                                    lastLineNumber,
                                    lastOffset,
                                    snapshot.FileSize));

                                return new LogReadTailResult
                                {
                                    File = new LogReadFileResult(
                                        file.FileId,
                                        file.DisplayName,
                                        retainedProvenance.Items,
                                        EncodingName(snapshot.Encoding),
                                        generation,
                                        mapped,
                                        Error: null)
                                    {
                                        ProvenanceTotalCount = retainedProvenance.TotalCount,
                                        IsProvenanceTruncated = retainedProvenance.IsTruncated
                                    },
                                    NextCursor = nextCursor,
                                    GenerationChanged = generationChanged,
                                    LastLineUpdated = lastLineUpdated,
                                    TotalLineCount = snapshot.TotalLineCount
                                };
                            }

                            throw new IOException("The log index changed while preparing the tail read.");
                        },
                        scope.Token).ConfigureAwait(false);
                }
                catch (InvalidTailCursorException)
                {
                    return Rejected<LogReadTailResult>(
                        requestId,
                        [Error("invalid_tail_cursor", "The tail cursor encoding no longer matches the configured log file.")],
                        selection.CatalogRevision);
                }
                catch (Exception ex) when (IsPerFileException(ex))
                {
                    result = new LogReadTailResult
                    {
                        File = FailedReadFile(file, retainedProvenance, ex)
                    };
                }

                var truncated = retainedProvenance.IsTruncated ||
                                responseBudget.IsExhausted ||
                                result.File?.Lines.Any(static line => line.IsTruncated) == true;
                return Envelope(
                    requestId,
                    selection.CatalogRevision,
                    isPartial: result.File?.Error != null,
                    truncated,
                    GetLineTruncationReasons(result.File?.Lines ?? [], responseBudget, retainedProvenance.IsTruncated),
                    errors: [],
                    result);
            }
            finally
            {
                _heavyRequestGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return Cancelled<LogReadTailResult>(requestId, ct);
        }
    }

    public async Task<LogOperationEnvelope<LogQueryStatus>> GetStatusAsync(CancellationToken ct = default)
    {
        using var requestLease = BeginRequest();
        var requestId = CreateRequestId();
        using var scope = CreateDeadlineScope(timeoutMilliseconds: null, ct);
        try
        {
            var catalogRead = await _catalogReader.ReadAsync(scope.Token).ConfigureAwait(false);
            var cache = _indexedSessions.GetProviderSnapshot();
            var status = new LogQueryStatus
            {
                IsReady = catalogRead.IsSuccess,
                ConnectionState = catalogRead.IsSuccess ? "ready" : "catalog_unavailable",
                Limits = _limits,
                ActiveIndexedSessions = cache.ActiveSessions,
                RetainedIndexedSessions = cache.RetainedSessions,
                MappedLineOffsets = cache.MappedLineOffsets
            };
            return Envelope(
                requestId,
                catalogRead.Snapshot?.Revision ?? string.Empty,
                isPartial: false,
                isTruncated: false,
                truncationReasons: [],
                errors: catalogRead.Error == null ? [] : [ToRequestError(catalogRead.Error)],
                status);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<LogQueryStatus>(requestId, ct);
        }
    }

    public void Dispose()
    {
        var disposeResources = false;
        lock (_lifetimeGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _lifetimeCancellation.Cancel();
            disposeResources = _activeRequests == 0;
            if (disposeResources)
                _resourcesDisposed = true;
        }

        if (disposeResources)
            DisposeResources();
    }

    private async Task<SearchBatchOutcome> SearchSelectedFilesAsync(
        ImmutableArray<ResolvedConfiguredLogFile> files,
        LogSearchQuery query,
        int maximumHitsPerFile,
        CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var paths = files.Select(static file => file.PhysicalPath).ToList();
        var searchRequest = SearchRequest.Create(
            query.Query,
            query.UseRegex,
            query.CaseSensitive,
            paths,
            SearchRequestSourceMode.DiskSnapshot,
            SearchRequestUsage.DiskSearch,
            query.StartTimestamp,
            query.EndTimestamp,
            maxHitsPerFile: string.Equals(query.ResultMode, "countsOnly", StringComparison.Ordinal)
                ? 0
                : maximumHitsPerFile,
            maxRetainedLineTextLength: _limits.MaximumCharactersPerLine,
            continueEvaluatingAfterHitLimit: !string.Equals(query.ResultMode, "samples", StringComparison.Ordinal));
        var results = await _searchService.SearchFilesBoundedWithEncodingAsync(
            searchRequest,
            _limits.MaximumConcurrentDiskOperations,
            path => _encodingDetection
                .ResolveEncodingDecision(path, FileEncoding.Auto)
                .ResolvedEncoding,
            AcquireDiskOperationAsync,
            ct).ConfigureAwait(false);
        stopwatch.Stop();
        return new SearchBatchOutcome(results.ToArray(), stopwatch.ElapsedMilliseconds);
    }

    private async Task<LogOperationEnvelope<LogSearchResult>> BuildSearchEnvelopeAsync(
        string requestId,
        ConfiguredLogSelectionResult selection,
        SearchResult[] rawResults,
        long elapsedMilliseconds,
        SearchCursorPayload? cursorPayload,
        string requestFingerprint,
        string targetFingerprint,
        DateOnly referenceDate,
        LogSearchQuery request,
        int effectiveFileLimit,
        int effectiveHitsPerFile,
        int effectiveTotalHits,
        CancellationToken ct)
    {
        var files = new List<LogSearchFileResult>(selection.Files.Length + selection.FileErrors.Length);
        var provenanceBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters / 4);
        var selectedFileProvenance = selection.Files
            .Select(file => RetainProvenance(file.Provenance, provenanceBudget))
            .ToArray();
        var selectionErrorProvenance = selection.FileErrors
            .Select(fileError => RetainProvenance(fileError.Provenance, provenanceBudget))
            .ToArray();
        var budget = new ResponseCharacterBudget(
            _limits.MaximumResponseCharacters - provenanceBudget.Consumed);
        var remainingHits = effectiveTotalHits;
        var truncationReasons = new HashSet<string>(StringComparer.Ordinal);
        var incompleteReasons = new HashSet<string>(StringComparer.Ordinal);
        var hasFileError = false;
        var failedFileCount = 0;
        var matchedFileCount = 0;
        var includeHits = !string.Equals(request.ResultMode, "countsOnly", StringComparison.Ordinal);
        var includeContext = string.Equals(request.ResultMode, "samples", StringComparison.Ordinal);
        if (selectedFileProvenance.Any(static provenance => provenance.IsTruncated) ||
            selectionErrorProvenance.Any(static provenance => provenance.IsTruncated))
        {
            truncationReasons.Add("provenance_metadata_limit");
        }

        for (var index = 0; index < selection.Files.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var file = selection.Files[index];
            var raw = rawResults[index];
            var retainedProvenance = selectedFileProvenance[index];
            var encoding = raw.ResolvedEncoding;
            if (!string.IsNullOrWhiteSpace(raw.Error))
            {
                hasFileError = true;
                failedFileCount++;
                incompleteReasons.Add("file_read_failed");
                files.Add(new LogSearchFileResult(
                    file.FileId,
                    file.DisplayName,
                    retainedProvenance.Items,
                    EncodingName(encoding),
                    Generation: null,
                    Hits: [],
                    Error("log_read_failed", "The configured log file could not be searched.", retryable: true, file.FileId),
                    IsTruncated: retainedProvenance.IsTruncated)
                {
                    ProvenanceTotalCount = retainedProvenance.TotalCount,
                    IsProvenanceTruncated = retainedProvenance.IsTruncated,
                    MatchingLineCount = raw.MatchingLineCount,
                    MatchOccurrenceCount = raw.MatchOccurrenceCount,
                    IsCountExact = false,
                    EvaluatedThroughLine = raw.EvaluatedThroughLine,
                    IncompleteReasons = ImmutableArray.Create("file_read_failed")
                });
                continue;
            }

            if (raw.MatchingLineCount > 0)
                matchedFileCount++;

            var fileIncompleteReasons = new HashSet<string>(StringComparer.Ordinal);
            if (raw.WasCancelled)
                fileIncompleteReasons.Add("evaluation_cancelled");
            if (!raw.IsEvaluationComplete)
                fileIncompleteReasons.Add("evaluation_incomplete");
            if (raw.FileChangedDuringOrAfterScan)
                fileIncompleteReasons.Add("file_changed_during_search");
            if (raw.GenerationEvidence.Correlation == FileGenerationCorrelation.Stale)
                fileIncompleteReasons.Add("file_generation_changed");
            else if (raw.GenerationEvidence.Correlation != FileGenerationCorrelation.Current)
                fileIncompleteReasons.Add("file_generation_unverified");

            var allowedHits = includeHits ? Math.Min(raw.Hits.Count, remainingHits) : 0;
            var selectedHits = includeHits ? raw.Hits.Take(allowedHits).ToArray() : [];
            if (includeHits)
                remainingHits -= allowedHits;
            var fileTruncated = retainedProvenance.IsTruncated ||
                                includeHits && (raw.HitLimitExceeded || allowedHits < raw.Hits.Count);
            if (includeHits && raw.HitLimitExceeded)
            {
                truncationReasons.Add("hits_per_file_limit");
                fileIncompleteReasons.Add("hit_samples_truncated");
            }
            if (includeHits && (allowedHits < raw.Hits.Count ||
                remainingHits == 0 && rawResults.Skip(index + 1).Any(static result => result.Hits.Count > 0))
            )
            {
                truncationReasons.Add("total_hit_limit");
                fileIncompleteReasons.Add("hit_samples_truncated");
            }

            ConfiguredLogRequestError? contextError = null;
            ImmutableArray<LogSearchHit> mappedHits;
            try
            {
                mappedHits = await MapSearchHitsAsync(
                    file,
                    encoding,
                    raw,
                    selectedHits,
                    includeContext ? request.IncludeContextBefore : 0,
                    includeContext ? request.IncludeContextAfter : 0,
                    budget,
                    ct).ConfigureAwait(false);
            }
            catch (SearchContextSnapshotMismatchException)
            {
                contextError = Error(
                    "context_generation_changed",
                    "Matches were found, but the log changed before context could be read.",
                    retryable: true,
                    file.FileId);
                hasFileError = true;
                failedFileCount++;
                fileTruncated = true;
                fileIncompleteReasons.Add("context_unavailable");
                mappedHits = MapSearchHitsWithoutContext(selectedHits, budget);
            }
            catch (Exception ex) when (IsPerFileException(ex))
            {
                contextError = Error(
                    "context_read_failed",
                    "Matches were found, but indexed context could not be read.",
                    retryable: true,
                    file.FileId);
                hasFileError = true;
                failedFileCount++;
                fileTruncated = true;
                fileIncompleteReasons.Add("context_unavailable");
                mappedHits = MapSearchHitsWithoutContext(selectedHits, budget);
            }

            if (mappedHits.Length < selectedHits.Length)
            {
                fileTruncated = true;
                truncationReasons.Add("response_text_limit");
                fileIncompleteReasons.Add("response_truncated");
            }
            if (mappedHits.Any(static hit => hit.IsTextTruncated))
            {
                truncationReasons.Add("line_character_limit");
                fileIncompleteReasons.Add("response_truncated");
            }
            if (budget.IsExhausted)
            {
                truncationReasons.Add("response_text_limit");
                fileIncompleteReasons.Add("response_truncated");
            }

            incompleteReasons.UnionWith(fileIncompleteReasons);

            files.Add(new LogSearchFileResult(
                file.FileId,
                file.DisplayName,
                retainedProvenance.Items,
                EncodingName(encoding),
                _cursorCodec.GetGenerationIdentity(raw.GenerationEvidence),
                mappedHits,
                contextError,
                fileTruncated)
            {
                ProvenanceTotalCount = retainedProvenance.TotalCount,
                IsProvenanceTruncated = retainedProvenance.IsTruncated,
                MatchingLineCount = raw.MatchingLineCount,
                MatchOccurrenceCount = raw.MatchOccurrenceCount,
                IsCountExact = fileIncompleteReasons.Count == 0,
                EvaluatedThroughLine = raw.EvaluatedThroughLine,
                IncompleteReasons = fileIncompleteReasons.Order(StringComparer.Ordinal).ToImmutableArray()
            });
        }

        for (var index = 0; index < selection.FileErrors.Length; index++)
        {
            var fileError = selection.FileErrors[index];
            var retainedProvenance = selectionErrorProvenance[index];
            hasFileError = true;
            incompleteReasons.Add("file_selection_failed");
            files.Add(new LogSearchFileResult(
                fileError.FileId,
                fileError.DisplayName,
                retainedProvenance.Items,
                Encoding: string.Empty,
                Generation: null,
                Hits: [],
                Error(
                    fileError.Code,
                    fileError.Message,
                    retryable: false,
                    fileError.FileId),
                IsTruncated: retainedProvenance.IsTruncated)
            {
                ProvenanceTotalCount = retainedProvenance.TotalCount,
                IsProvenanceTruncated = retainedProvenance.IsTruncated,
                IncompleteReasons = ImmutableArray.Create("file_selection_failed")
            });
        }

        var totalHits = files.Sum(static file => file.Hits.Length);
        var pageMatchingLineCount = rawResults.Sum(static result => result.MatchingLineCount);
        var pageOccurrenceCount = rawResults.Sum(static result => result.MatchOccurrenceCount);
        var pageCountsAreExact = incompleteReasons.Count == 0;
        var priorPagesComplete = cursorPayload?.PriorPagesComplete ?? true;
        var cumulativeMatchingLineCount = (cursorPayload?.CumulativeMatchingLineCount ?? 0) + pageMatchingLineCount;
        var cumulativeOccurrenceCount = (cursorPayload?.CumulativeMatchOccurrenceCount ?? 0) + pageOccurrenceCount;
        var cumulativeScannedFileCount = (cursorPayload?.CumulativeScannedFileCount ?? 0) + rawResults.Length;
        var cumulativeSkippedFileCount = (cursorPayload?.CumulativeSkippedFileCount ?? 0) + selection.FileErrors.Length;
        var cumulativeFailedFileCount = (cursorPayload?.CumulativeFailedFileCount ?? 0) + failedFileCount;
        var cumulativeMatchedFileCount = (cursorPayload?.CumulativeMatchedFileCount ?? 0) + matchedFileCount;
        var cumulativeIncompleteReasons = new HashSet<string>(
            cursorPayload?.IncompleteReasons ?? [],
            StringComparer.Ordinal);
        cumulativeIncompleteReasons.UnionWith(incompleteReasons);
        var queryCountsAreExact = priorPagesComplete && pageCountsAreExact && !selection.HasMore;
        var queryIncompleteReasons = new HashSet<string>(cumulativeIncompleteReasons, StringComparer.Ordinal);
        if (selection.HasMore)
            queryIncompleteReasons.Add("unvisited_pages");

        string? nextCursor = null;
        if (selection.Continuation != null)
        {
            nextCursor = _searchCursorCodec.Encode(new SearchCursorPayload(
                2,
                selection.CatalogRevision,
                requestFingerprint,
                targetFingerprint,
                request.DateOffsetDays,
                referenceDate.DayNumber,
                selection.Continuation.NextStableFileIndex,
                selection.Continuation.SeenPhysicalPathIdentities.ToArray(),
                cumulativeMatchingLineCount,
                cumulativeOccurrenceCount,
                cumulativeScannedFileCount,
                cumulativeSkippedFileCount,
                cumulativeFailedFileCount,
                cumulativeMatchedFileCount,
                priorPagesComplete && pageCountsAreExact,
                cumulativeIncompleteReasons.Order(StringComparer.Ordinal).ToArray()));
        }
        var result = new LogSearchResult
        {
            ResultMode = request.ResultMode,
            Files = files.ToImmutableArray(),
            SelectedFileCount = selection.Summary.ExpandedStableFileCount,
            SearchedFileCount = cumulativeScannedFileCount,
            TotalHitCount = totalHits,
            ReturnedHitCount = totalHits,
            NextCursor = nextCursor,
            PageMatchingLineCount = pageMatchingLineCount,
            PageMatchOccurrenceCount = pageOccurrenceCount,
            MatchingLineCount = cumulativeMatchingLineCount,
            MatchOccurrenceCount = cumulativeOccurrenceCount,
            SkippedFileCount = cumulativeSkippedFileCount,
            FailedFileCount = cumulativeFailedFileCount,
            RemainingFileCount = selection.Summary.RemainingCandidateCount,
            MatchedFileCount = cumulativeMatchedFileCount,
            ArePageCountsExact = pageCountsAreExact,
            AreQueryCountsExact = queryCountsAreExact,
            IsPageComplete = pageCountsAreExact,
            IsQueryComplete = queryCountsAreExact,
            CompletionState = queryCountsAreExact ? "complete" : "incomplete",
            IncompleteReasons = queryIncompleteReasons.Order(StringComparer.Ordinal).ToImmutableArray(),
            PageIncompleteReasons = incompleteReasons.Order(StringComparer.Ordinal).ToImmutableArray(),
            Statistics = new LogSearchStatistics(
                rawResults.Sum(static raw => raw.ScannedFileSize ?? 0),
                elapsedMilliseconds,
                rawResults.Length,
                rawResults.Length,
                selection.FileErrors.Length,
                _queryOperationMetrics.Value?.PeakDiskOperations ?? 0,
                _queryOperationMetrics.Value?.PeakUncOperations ?? 0),
            EffectiveLimits = _limits with
            {
                MaximumFiles = effectiveFileLimit,
                MaximumHitsPerFile = effectiveHitsPerFile,
                MaximumTotalHits = effectiveTotalHits
            }
        };
        return Envelope(
            requestId,
            selection.CatalogRevision,
            isPartial: hasFileError ||
                       (cursorPayload?.CumulativeFailedFileCount ?? 0) > 0 ||
                       (cursorPayload?.CumulativeSkippedFileCount ?? 0) > 0,
            isTruncated: truncationReasons.Count > 0,
            truncationReasons.Order(StringComparer.Ordinal).ToImmutableArray(),
            errors: [],
            result);
    }

    private async Task<ImmutableArray<LogSearchHit>> MapSearchHitsAsync(
        ResolvedConfiguredLogFile file,
        FileEncoding encoding,
        SearchResult rawResult,
        IReadOnlyList<SearchHit> hits,
        int contextBefore,
        int contextAfter,
        ResponseCharacterBudget budget,
        CancellationToken ct)
    {
        if (hits.Count == 0 || budget.IsExhausted)
            return [];

        if (contextBefore == 0 && contextAfter == 0)
            return MapSearchHitsWithoutContext(hits, budget);

        return await ExecuteDiskOperationAsync(
            file.PhysicalPath,
            async token =>
            {
                using var lease = _indexedSessions.AcquireSession(file.PhysicalPath, encoding);
                var ranges = hits
                    .Where(static hit => hit.LineNumber is >= 1 and <= int.MaxValue)
                    .Select(hit =>
                    {
                        var zeroBasedHit = checked((int)hit.LineNumber - 1);
                        var rangeStart = Math.Max(0, zeroBasedHit - contextBefore);
                        return new IndexedLogReadRange(
                            rangeStart,
                            zeroBasedHit - rangeStart + contextAfter + 1);
                    })
                    .ToArray();
                var snapshot = await lease.CaptureCurrentIndexAsync(ranges, token).ConfigureAwait(false);
                if (!MatchesSearchSnapshot(rawResult, snapshot))
                    throw new SearchContextSnapshotMismatchException();

                var contextLines = snapshot.Lines.IsEmpty || budget.IsExhausted
                    ? Array.Empty<BoundedIndexedLine>()
                    : await ReadSnapshotAsync(
                        lease,
                        file.PhysicalPath,
                        snapshot,
                        budget.Remaining,
                        token).ConfigureAwait(false);
                var mapped = ImmutableArray.CreateBuilder<LogSearchHit>(hits.Count);
                foreach (var hit in hits)
                {
                    if (budget.IsExhausted || hit.LineNumber is < 1 or > int.MaxValue)
                        break;

                    var retained = TakeSearchHitText(hit, budget);
                    var zeroBasedHit = checked((int)hit.LineNumber - 1);
                    var before = MapContextLines(
                        contextLines,
                        Math.Max(0, zeroBasedHit - contextBefore),
                        zeroBasedHit,
                        budget);
                    var after = MapContextLines(
                        contextLines,
                        zeroBasedHit + 1,
                        (int)Math.Min(int.MaxValue, (long)zeroBasedHit + contextAfter + 1),
                        budget);
                    mapped.Add(new LogSearchHit(
                        hit.LineNumber,
                        retained.Text,
                        hit.LineTextTruncated || retained.IsTruncated,
                        retained.MatchStart,
                        retained.MatchLength,
                        before,
                        after));
                }

                return mapped.ToImmutable();
            },
            ct).ConfigureAwait(false);
    }

    private ImmutableArray<LogSearchHit> MapSearchHitsWithoutContext(
        IReadOnlyList<SearchHit> hits,
        ResponseCharacterBudget budget)
    {
        var mapped = ImmutableArray.CreateBuilder<LogSearchHit>(hits.Count);
        foreach (var hit in hits)
        {
            if (budget.IsExhausted)
                break;

            var retained = TakeSearchHitText(hit, budget);
            mapped.Add(new LogSearchHit(
                hit.LineNumber,
                retained.Text,
                hit.LineTextTruncated || retained.IsTruncated,
                retained.MatchStart,
                retained.MatchLength,
                ContextBefore: [],
                ContextAfter: []));
        }

        return mapped.ToImmutable();
    }

    private static ImmutableArray<LogLineResult> MapContextLines(
        IReadOnlyList<BoundedIndexedLine> lines,
        int startLine,
        int endLineExclusive,
        ResponseCharacterBudget budget)
    {
        var mapped = ImmutableArray.CreateBuilder<LogLineResult>();
        foreach (var line in lines)
        {
            if (line.LineNumber < startLine || line.LineNumber >= endLineExclusive)
                continue;
            if (budget.IsExhausted)
                break;

            var text = TakeLogText(line.Text, budget, out var responseTruncated);
            mapped.Add(new LogLineResult(
                line.LineNumber + 1,
                text,
                line.IsTruncated || responseTruncated));
        }

        return mapped.ToImmutable();
    }

    private static bool MatchesSearchSnapshot(
        SearchResult rawResult,
        IndexedLogReadSnapshot snapshot)
    {
        if (rawResult.ScannedFileSize != snapshot.FileSize ||
            rawResult.GenerationEvidence.Correlation == FileGenerationCorrelation.Stale)
        {
            return false;
        }

        var scannedToken = rawResult.GenerationEvidence.Token;
        if (scannedToken.IsKnown)
        {
            if (rawResult.GenerationEvidence.Correlation != FileGenerationCorrelation.Current ||
                !snapshot.GenerationToken.IsKnown ||
                scannedToken != snapshot.GenerationToken)
            {
                return false;
            }
        }
        else if (rawResult.ScannedLastWriteTimeUtc == default ||
                 snapshot.LastWriteTimeUtc == default)
        {
            return false;
        }

        return rawResult.ScannedLastWriteTimeUtc == default ||
               snapshot.LastWriteTimeUtc == default ||
               rawResult.ScannedLastWriteTimeUtc == snapshot.LastWriteTimeUtc;
    }

    private Task<ConfiguredLogSelectionResult> ResolveAsync(
        ConfiguredLogCatalogSnapshot snapshot,
        IEnumerable<ConfiguredLogTarget> targets,
        int dateOffsetDays,
        DateOnly referenceDate,
        int maximumFiles,
        ConfiguredLogSelectionContinuation? continuation,
        CancellationToken ct)
        => Task.Run(
                () => _selectionResolver.Resolve(
                    snapshot,
                    new ConfiguredLogSelectionRequest(
                        targets,
                        referenceDate,
                        dateOffsetDays,
                        _limits.MaximumTargets,
                        maximumFiles,
                        continuation,
                        _limits.MaximumSearchCandidates),
                    new ExistingPathCandidateSelector(this, ct),
                    ct),
                CancellationToken.None)
            .WaitAsync(ct);

    private Task<ConfiguredLogSelectionResult> ResolveSingleFileAsync(
        ConfiguredLogCatalogSnapshot snapshot,
        string fileId,
        int dateOffsetDays,
        CancellationToken ct)
        => ResolveAsync(
            snapshot,
            [new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, fileId)],
            dateOffsetDays,
            _today(),
            maximumFiles: 1,
            continuation: null,
            ct: ct);

    private async Task<T> ExecuteDiskOperationAsync<T>(
        string filePath,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        var isUnc = IsUncPath(filePath);
        var uncAcquired = false;
        var diskAcquired = false;
        var tracked = false;
        try
        {
            if (isUnc)
            {
                await _uncOperationGate.WaitAsync(ct).ConfigureAwait(false);
                uncAcquired = true;
            }

            await _diskOperationGate.WaitAsync(ct).ConfigureAwait(false);
            diskAcquired = true;
            RecordOperationStarted(isUnc);
            tracked = true;

            return await operation(ct).ConfigureAwait(false);
        }
        finally
        {
            if (tracked)
                RecordOperationCompleted(isUnc);
            if (diskAcquired)
                _diskOperationGate.Release();
            if (uncAcquired)
                _uncOperationGate.Release();
        }
    }

    private async ValueTask<IDisposable> AcquireDiskOperationAsync(
        string filePath,
        CancellationToken ct)
    {
        var isUnc = IsUncPath(filePath);
        var uncAcquired = false;
        var diskAcquired = false;
        try
        {
            if (isUnc)
            {
                await _uncOperationGate.WaitAsync(ct).ConfigureAwait(false);
                uncAcquired = true;
            }

            await _diskOperationGate.WaitAsync(ct).ConfigureAwait(false);
            diskAcquired = true;
            RecordOperationStarted(isUnc);

            return new DiskOperationLease(() => ReleaseOperation(isUnc));
        }
        catch
        {
            if (diskAcquired)
                _diskOperationGate.Release();
            if (uncAcquired)
                _uncOperationGate.Release();
            throw;
        }
    }

    private bool ProbePathExists(string filePath, CancellationToken ct)
        => ProbePathExistsAsync(filePath, ct).GetAwaiter().GetResult();

    private async Task<bool> ProbePathExistsAsync(string filePath, CancellationToken ct)
    {
        var isUnc = IsUncPath(filePath);
        var diskAcquired = false;
        var uncAcquired = false;
        var tracked = false;
        try
        {
            if (isUnc)
            {
                await _uncOperationGate.WaitAsync(ct).ConfigureAwait(false);
                uncAcquired = true;
            }

            await _diskOperationGate.WaitAsync(ct).ConfigureAwait(false);
            diskAcquired = true;
            RecordOperationStarted(isUnc);
            tracked = true;

            ct.ThrowIfCancellationRequested();
            var detachedLease = BeginDetachedWork();
            Task<bool> probeTask;
            try
            {
                probeTask = Task.Run(() => _pathExists(filePath));
            }
            catch
            {
                detachedLease.Dispose();
                throw;
            }

            var completion = CompleteDetachedPathProbeAsync(
                probeTask,
                detachedLease,
                isUnc);
            ObserveDetachedFault(completion);
            diskAcquired = false;
            uncAcquired = false;
            tracked = false;
            return await completion.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (tracked)
                RecordOperationCompleted(isUnc);
            if (diskAcquired)
                _diskOperationGate.Release();
            if (uncAcquired)
                _uncOperationGate.Release();
        }
    }

    private async Task<bool> CompleteDetachedPathProbeAsync(
        Task<bool> probeTask,
        RequestLease detachedLease,
        bool isUnc)
    {
        try
        {
            return await probeTask.ConfigureAwait(false);
        }
        finally
        {
            ReleaseOperation(isUnc);
            detachedLease.Dispose();
        }
    }

    private void RecordOperationStarted(bool isUnc)
    {
        _queryOperationMetrics.Value?.RecordStarted(isUnc);
    }

    private void RecordOperationCompleted(bool isUnc)
    {
        _queryOperationMetrics.Value?.RecordCompleted(isUnc);
    }

    private void ReleaseOperation(bool isUnc)
    {
        RecordOperationCompleted(isUnc);
        _diskOperationGate.Release();
        if (isUnc)
            _uncOperationGate.Release();
    }

    private static void UpdatePeak(ref int peak, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref peak);
            if (value <= current || Interlocked.CompareExchange(ref peak, value, current) == current)
                return;
        }
    }

    private static void ObserveDetachedFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private ImmutableArray<ConfiguredLogRequestError> ValidateSearchRequest(
        LogSearchQuery request,
        out int effectiveFileLimit,
        out int effectiveHitsPerFile,
        out int effectiveTotalHits)
    {
        var errors = ImmutableArray.CreateBuilder<ConfiguredLogRequestError>();
        effectiveFileLimit = ValidateLowerLimit(request.MaxFiles, _limits.MaximumFiles, "maxFiles", "invalid_file_limit", errors);
        effectiveHitsPerFile = ValidateLowerLimit(request.MaxHitsPerFile, _limits.MaximumHitsPerFile, "maxHitsPerFile", "invalid_hit_limit", errors);
        effectiveTotalHits = ValidateLowerLimit(request.MaxTotalHits, _limits.MaximumTotalHits, "maxTotalHits", "invalid_total_hit_limit", errors);
        if (request.Targets == null || request.Targets.Count == 0)
            errors.Add(Error("targets_required", "At least one configured target is required."));
        else if (request.Targets.Count > _limits.MaximumTargets)
            errors.Add(Error("target_limit_exceeded", $"No more than {_limits.MaximumTargets} targets may be requested."));
        if (string.IsNullOrEmpty(request.Query))
            errors.Add(Error("query_required", "A non-empty search query is required."));
        else if (request.Query.Length > _limits.MaximumQueryCharacters)
            errors.Add(Error("query_too_long", $"The query cannot exceed {_limits.MaximumQueryCharacters} characters."));
        if (request.ResultMode is not ("samples" or "matchesOnly" or "countsOnly"))
        {
            errors.Add(Error(
                "invalid_result_mode",
                "resultMode must be one of: samples, matchesOnly, or countsOnly."));
        }
        if (request.StartTimestamp is { Length: > ConfiguredLogLimits.DefaultMaxTimestampCharacters } ||
            request.EndTimestamp is { Length: > ConfiguredLogLimits.DefaultMaxTimestampCharacters })
        {
            errors.Add(Error(
                "timestamp_too_long",
                $"Timestamp values cannot exceed {ConfiguredLogLimits.DefaultMaxTimestampCharacters} characters."));
        }
        if (request.IncludeContextBefore is < 0 || request.IncludeContextBefore > _limits.MaximumContextLines ||
            request.IncludeContextAfter is < 0 || request.IncludeContextAfter > _limits.MaximumContextLines)
        {
            errors.Add(Error("invalid_context_limit", $"Context counts must be between 0 and {_limits.MaximumContextLines}."));
        }
        if (request.DateOffsetDays < 0)
            errors.Add(Error("invalid_date_offset", "dateOffsetDays cannot be negative."));
        if (!TimestampParser.TryBuildRange(request.StartTimestamp, request.EndTimestamp, out _, out _))
            errors.Add(Error("invalid_timestamp_range", "The requested timestamp range is invalid."));
        if (request.UseRegex && !RegexPatternFactory.TryCreate(request.Query, request.CaseSensitive, out _))
            errors.Add(Error("invalid_regex", "The regular expression is invalid."));
        ValidateTimeout(request.TimeoutMilliseconds, errors);
        return errors.ToImmutable();
    }

    private string CreateSearchRequestFingerprint(
        LogSearchQuery request,
        int effectiveFileLimit,
        int effectiveHitsPerFile,
        int effectiveTotalHits)
    {
        var canonical = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Targets = request.Targets.Select(target => new { Kind = (int)target.Kind, target.Id }).ToArray(),
            request.Query,
            request.UseRegex,
            request.CaseSensitive,
            request.ResultMode,
            request.DateOffsetDays,
            StartTimestamp = NormalizeFingerprintText(request.StartTimestamp),
            EndTimestamp = NormalizeFingerprintText(request.EndTimestamp),
            MaxFiles = effectiveFileLimit,
            MaxHitsPerFile = effectiveHitsPerFile,
            MaxTotalHits = effectiveTotalHits,
            request.IncludeContextBefore,
            request.IncludeContextAfter,
            TimeoutMilliseconds = request.TimeoutMilliseconds ?? _limits.DefaultTimeoutMilliseconds
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(canonical));
    }

    private static string CreateTargetFingerprint(IReadOnlyList<ConfiguredLogTarget> targets)
    {
        var canonical = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            targets.Select(target => new { Kind = (int)target.Kind, target.Id }).ToArray());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(canonical));
    }

    private static string? NormalizeFingerprintText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private ImmutableArray<ConfiguredLogRequestError> ValidateReadRequest(
        string fileId,
        int startLine,
        int count,
        int dateOffsetDays,
        int? timeoutMilliseconds)
    {
        var errors = ImmutableArray.CreateBuilder<ConfiguredLogRequestError>();
        if (string.IsNullOrWhiteSpace(fileId))
            errors.Add(Error("file_id_required", "A configured log-file ID is required."));
        else if (fileId.Length > ConfiguredLogLimits.DefaultMaxIdCharacters)
            errors.Add(Error("invalid_file_id", $"Configured file IDs cannot exceed {ConfiguredLogLimits.DefaultMaxIdCharacters} characters."));
        if (startLine < 1)
            errors.Add(Error("invalid_start_line", "startLine must be at least 1."));
        if (count is < 1 || count > _limits.MaximumReadLineCount)
            errors.Add(Error("invalid_line_count", $"count must be between 1 and {_limits.MaximumReadLineCount}."));
        if (dateOffsetDays < 0)
            errors.Add(Error("invalid_date_offset", "dateOffsetDays cannot be negative."));
        ValidateTimeout(timeoutMilliseconds, errors);
        return errors.ToImmutable();
    }

    private ImmutableArray<ConfiguredLogRequestError> ValidateTailRequest(
        LogReadTailQuery request,
        int maxLines,
        out TailCursorPayload? cursor)
    {
        var errors = ValidateReadRequest(
            request.FileId,
            startLine: 1,
            maxLines,
            request.DateOffsetDays,
            request.TimeoutMilliseconds).ToBuilder();
        cursor = null;
        if (request.Cursor != null && !_cursorCodec.TryDecode(request.Cursor, out cursor))
            errors.Add(Error("invalid_tail_cursor", "The tail cursor is invalid or belongs to another server process."));
        return errors.ToImmutable();
    }

    private void ValidateTimeout(
        int? timeoutMilliseconds,
        ImmutableArray<ConfiguredLogRequestError>.Builder errors)
    {
        if (timeoutMilliseconds is < 1 || timeoutMilliseconds > _limits.DefaultTimeoutMilliseconds)
        {
            errors.Add(Error(
                "invalid_timeout",
                $"timeoutMilliseconds must be between 1 and {_limits.DefaultTimeoutMilliseconds}."));
        }
    }

    private static int ValidateLowerLimit(
        int? requested,
        int maximum,
        string name,
        string code,
        ImmutableArray<ConfiguredLogRequestError>.Builder errors)
    {
        if (requested is null)
            return maximum;
        if (requested is < 1 || requested > maximum)
        {
            errors.Add(Error(code, $"{name} must be between 1 and {maximum}."));
            return maximum;
        }

        return requested.Value;
    }

    private static void ValidateLimits(LogQueryEffectiveLimits limits)
    {
        if (limits.MaximumTargets < 1 ||
            limits.MaximumFiles < 1 ||
            limits.MaximumQueryCharacters < 1 ||
            limits.MaximumHitsPerFile < 1 ||
            limits.MaximumTotalHits < 1 ||
            limits.MaximumCharactersPerLine < 1 ||
            limits.MaximumContextLines < 0 ||
            limits.DefaultReadLineCount < 1 ||
            limits.MaximumReadLineCount < limits.DefaultReadLineCount ||
            limits.MaximumResponseCharacters < 1 ||
            limits.MaximumConcurrentDiskOperations < 1 ||
            limits.DefaultTimeoutMilliseconds < 1 ||
            limits.MaximumSearchCandidates is < 1 or > ConfiguredLogLimits.DefaultMaxSearchCandidates ||
            limits.MaximumCountBuckets is < 1 or > ConfiguredLogLimits.DefaultMaxCountBuckets ||
            limits.MaximumRelativeWindowDays is < 1 or > ConfiguredLogLimits.DefaultMaxRelativeWindowDays)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
    }

    private static ImmutableArray<LogLineResult> MapLines(
        IReadOnlyList<BoundedIndexedLine> lines,
        ResponseCharacterBudget budget)
    {
        var mapped = ImmutableArray.CreateBuilder<LogLineResult>(lines.Count);
        foreach (var line in lines)
        {
            if (budget.IsExhausted)
                break;
            var text = TakeLogText(line.Text, budget, out var responseTruncated);
            mapped.Add(new LogLineResult(
                line.LineNumber + 1,
                text,
                line.IsTruncated || responseTruncated));
        }

        return mapped.ToImmutable();
    }

    private async Task<IReadOnlyList<BoundedIndexedLine>> ReadSnapshotAsync(
        IIndexedLogSessionLease lease,
        string filePath,
        IndexedLogReadSnapshot snapshot,
        int maximumTotalCharacters,
        CancellationToken ct)
    {
        var lines = await _logReader.ReadBoundedLinesAsync(
            filePath,
            snapshot,
            _limits.MaximumCharactersPerLine,
            maximumTotalCharacters,
            ct).ConfigureAwait(false);
        if (!await lease.RevalidateCurrentIndexAsync(snapshot, ct).ConfigureAwait(false))
            throw new IOException("The log index generation changed during the bounded read.");
        return lines;
    }

    private static string TakeLogText(
        string value,
        ResponseCharacterBudget budget,
        out bool isTruncated)
    {
        var normalized = LogContentSanitizer.Normalize(value);
        var allowed = budget.Consume(normalized.Length);
        isTruncated = allowed < normalized.Length;
        return allowed == normalized.Length ? normalized : normalized[..allowed];
    }

    private static RetainedSearchHitText TakeSearchHitText(
        SearchHit hit,
        ResponseCharacterBudget budget)
    {
        var normalized = LogContentSanitizer.Normalize(hit.LineText);
        var admittedLength = Math.Min(normalized.Length, budget.Remaining);
        if (admittedLength == normalized.Length)
        {
            budget.Consume(admittedLength);
            return new RetainedSearchHitText(
                normalized,
                hit.MatchStart,
                hit.MatchLength,
                IsTruncated: false);
        }

        var matchStart = Math.Clamp(hit.MatchStart, 0, normalized.Length);
        var matchEnd = (int)Math.Clamp(
            (long)matchStart + Math.Max(0, hit.MatchLength),
            matchStart,
            normalized.Length);
        var maximumWindowStart = normalized.Length - admittedLength;
        int windowStart;
        if (matchEnd - matchStart >= admittedLength)
        {
            windowStart = Math.Min(matchStart, maximumWindowStart);
        }
        else
        {
            var minimumStartForWholeMatch = Math.Max(0, matchEnd - admittedLength);
            var maximumStartForWholeMatch = Math.Min(matchStart, maximumWindowStart);
            var centeredStart = matchStart - ((admittedLength - (matchEnd - matchStart)) / 2);
            windowStart = Math.Clamp(
                centeredStart,
                minimumStartForWholeMatch,
                maximumStartForWholeMatch);
        }

        var windowEnd = windowStart + admittedLength;
        var visibleMatchStart = Math.Max(matchStart, windowStart);
        var visibleMatchEnd = Math.Min(matchEnd, windowEnd);
        var text = normalized.Substring(windowStart, admittedLength);
        budget.Consume(text.Length);
        return new RetainedSearchHitText(
            text,
            visibleMatchStart - windowStart,
            Math.Max(0, visibleMatchEnd - visibleMatchStart),
            IsTruncated: true);
    }

    private static ImmutableArray<string> GetLineTruncationReasons(
        ImmutableArray<LogLineResult> lines,
        ResponseCharacterBudget budget,
        bool provenanceTruncated)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        if (provenanceTruncated)
            reasons.Add("provenance_metadata_limit");
        if (lines.Any(static line => line.IsTruncated))
            reasons.Add("line_character_limit");
        if (budget.IsExhausted)
            reasons.Add("response_text_limit");
        return reasons.ToImmutable();
    }

    private static bool CursorOffsetStillMatches(
        TailCursorPayload cursor,
        IndexedLogReadSnapshot snapshot)
    {
        if (cursor.LastLineNumber == 0)
            return true;
        if (cursor.LastLineNumber > snapshot.TotalLineCount)
            return false;
        return snapshot.TryGetLineBounds(cursor.LastLineNumber - 1, out var bounds) &&
               bounds!.StartOffset == cursor.LastOffset;
    }

    private static async Task<bool> AppendStartsWithLineContentAsync(
        string filePath,
        long appendOffset,
        long fileSize,
        FileEncoding encoding,
        CancellationToken ct)
    {
        var codeUnitSize = encoding is FileEncoding.Utf16 or FileEncoding.Utf16Be ? 2 : 1;
        if (appendOffset < 0 || fileSize - appendOffset < codeUnitSize)
            return false;

        var buffer = new byte[codeUnitSize];
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: codeUnitSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        stream.Position = appendOffset;
        var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        if (read != codeUnitSize)
            return false;

        var firstCharacter = encoding switch
        {
            FileEncoding.Utf16 => (char)(buffer[0] | (buffer[1] << 8)),
            FileEncoding.Utf16Be => (char)((buffer[0] << 8) | buffer[1]),
            _ => (char)buffer[0]
        };
        return firstCharacter is not '\r' and not '\n';
    }

    private static bool TryGetLineStartOffset(
        IndexedLogReadSnapshot snapshot,
        int oneBasedLineNumber,
        out long offset)
    {
        if (oneBasedLineNumber > 0 &&
            snapshot.TryGetLineBounds(oneBasedLineNumber - 1, out var bounds))
        {
            offset = bounds!.StartOffset;
            return true;
        }

        offset = 0;
        return false;
    }

    private static LogReadFileResult FailedReadFile(
        ResolvedConfiguredLogFile file,
        RetainedProvenance retainedProvenance,
        Exception exception)
        => new(
            file.FileId,
            file.DisplayName,
            retainedProvenance.Items,
            Encoding: string.Empty,
            Generation: null,
            Lines: [],
            ToFileError(exception, file.FileId))
        {
            ProvenanceTotalCount = retainedProvenance.TotalCount,
            IsProvenanceTruncated = retainedProvenance.IsTruncated
        };

    private static RetainedProvenance RetainProvenance(
        ImmutableArray<ConfiguredLogProvenance> provenance,
        ResponseCharacterBudget budget)
    {
        var retained = ImmutableArray.CreateBuilder<ConfiguredLogProvenance>();
        foreach (var item in provenance)
        {
            var characterCount = checked(
                item.RequestedTargetId.Length +
                item.TargetTreePath.Length +
                item.DashboardId.Length +
                item.DashboardTreePath.Length);
            if (characterCount > budget.Remaining)
                break;

            budget.Consume(characterCount);
            retained.Add(item);
        }

        return new RetainedProvenance(
            retained.ToImmutable(),
            provenance.Length,
            retained.Count < provenance.Length);
    }

    private static ConfiguredLogRequestError ToFileError(Exception exception, string fileId)
        => exception switch
        {
            FileNotFoundException or DirectoryNotFoundException =>
                Error("log_not_found", "The configured log file is not currently available.", retryable: true, fileId),
            UnauthorizedAccessException =>
                Error("log_access_denied", "Access to the configured log file was denied.", retryable: false, fileId),
            LineIndexCapacityExceededException or IndexedLogSessionCapacityExceededException =>
                Error("index_capacity_exceeded", "The bounded line-index capacity is exhausted.", retryable: true, fileId),
            AutomaticReloadBlockedException =>
                Error("log_generation_unstable", "The configured log file changed repeatedly during the indexed read.", retryable: true, fileId),
            IOException =>
                Error("log_read_failed", "The configured log file could not be read consistently.", retryable: true, fileId),
            _ => Error("log_read_failed", "The configured log file could not be read.", retryable: true, fileId)
        };

    private static bool IsPerFileException(Exception exception)
        => exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    private static bool IsUncPath(string filePath)
        => filePath.StartsWith("\\\\", StringComparison.Ordinal);

    private static string EncodingName(FileEncoding encoding)
        => encoding switch
        {
            FileEncoding.Utf8 => "utf-8",
            FileEncoding.Utf8Bom => "utf-8-bom",
            FileEncoding.Ansi => "windows-1252",
            FileEncoding.Utf16 => "utf-16-le",
            FileEncoding.Utf16Be => "utf-16-be",
            _ => "utf-8"
        };

    private CancellationTokenSource CreateDeadlineScope(int? timeoutMilliseconds, CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCancellation.Token);
        source.CancelAfter(timeoutMilliseconds ?? _limits.DefaultTimeoutMilliseconds);
        return source;
    }

    private static string CreateRequestId()
        => Guid.NewGuid().ToString("N");

    private static ConfiguredLogRequestError Error(
        string code,
        string message,
        bool retryable = false,
        string? targetId = null,
        ConfiguredLogTargetKind? targetKind = null)
        => new(code, message, targetId, targetKind, retryable);

    private static ConfiguredLogRequestError ToRequestError(ConfiguredLogCatalogReadError error)
        => Error(error.Code, error.Message, error.IsRetryable);

    private LogOperationEnvelope<T> Failure<T>(
        string requestId,
        ConfiguredLogCatalogReadError error)
        => Rejected<T>(requestId, [ToRequestError(error)]);

    private LogOperationEnvelope<T> SelectionFailure<T>(
        string requestId,
        ConfiguredLogSelectionResult selection)
        => Envelope<T>(
            requestId,
            selection.CatalogRevision,
            isPartial: false,
            selection.Summary.RejectedByLimit,
            selection.Summary.RejectedByLimit ? ["resolved_file_limit"] : [],
            selection.Errors,
            result: default);

    private LogOperationEnvelope<T> SelectionFileFailure<T>(
        string requestId,
        ConfiguredLogSelectionResult selection)
        => Envelope<T>(
            requestId,
            selection.CatalogRevision,
            isPartial: false,
            isTruncated: false,
            truncationReasons: [],
            errors: selection.FileErrors.Select(fileError => Error(
                fileError.Code,
                fileError.Message,
                targetId: fileError.FileId,
                targetKind: ConfiguredLogTargetKind.LogFile)).ToImmutableArray(),
            result: default);

    private LogOperationEnvelope<T> Cancelled<T>(string requestId, CancellationToken callerToken)
        => Rejected<T>(
            requestId,
            [callerToken.IsCancellationRequested
                ? Error("request_cancelled", "The request was cancelled.", retryable: true)
                : _lifetimeCancellation.IsCancellationRequested
                    ? Error("backend_stopping", "The log-query backend is stopping.", retryable: true)
                    : Error("deadline_exceeded", "The request deadline was exceeded.", retryable: true)]);

    private LogOperationEnvelope<T> Rejected<T>(
        string requestId,
        ImmutableArray<ConfiguredLogRequestError> errors,
        string catalogRevision = "")
        => Envelope<T>(
            requestId,
            catalogRevision,
            isPartial: false,
            isTruncated: false,
            truncationReasons: [],
            errors,
            result: default);

    private LogOperationEnvelope<T> Envelope<T>(
        string requestId,
        string catalogRevision,
        bool isPartial,
        bool isTruncated,
        ImmutableArray<string> truncationReasons,
        ImmutableArray<ConfiguredLogRequestError> errors,
        T? result)
        => new(
            LogOperationEnvelope<T>.CurrentSchemaVersion,
            requestId,
            catalogRevision,
            isPartial,
            isTruncated,
            truncationReasons,
            errors,
            result);

    private sealed class ResponseCharacterBudget
    {
        public ResponseCharacterBudget(int maximumCharacters)
        {
            Remaining = maximumCharacters;
        }

        public int Remaining { get; private set; }

        public int Consumed { get; private set; }

        public bool IsExhausted => Remaining == 0;

        public int Consume(int requestedCharacters)
        {
            var admitted = Math.Min(Remaining, Math.Max(0, requestedCharacters));
            Remaining -= admitted;
            Consumed += admitted;
            return admitted;
        }
    }

    private sealed class ExistingPathCandidateSelector : IConfiguredLogPathCandidateSelector
    {
        private readonly HeadlessLogQueryBackend _owner;
        private readonly CancellationToken _cancellationToken;

        internal ExistingPathCandidateSelector(
            HeadlessLogQueryBackend owner,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            _cancellationToken = cancellationToken;
        }

        public string SelectPath(string fileId, ImmutableArray<string> orderedCandidates)
        {
            foreach (var candidate in orderedCandidates)
            {
                if (_owner.ProbePathExists(candidate, _cancellationToken))
                    return candidate;
            }

            return orderedCandidates[0];
        }
    }

    private sealed class SearchContextSnapshotMismatchException : IOException;

    private sealed class InvalidTailCursorException : Exception;

    private readonly record struct RetainedSearchHitText(
        string Text,
        int MatchStart,
        int MatchLength,
        bool IsTruncated);

    private sealed record SearchBatchOutcome(SearchResult[] Results, long ElapsedMilliseconds);

    private sealed record RetainedProvenance(
        ImmutableArray<ConfiguredLogProvenance> Items,
        int TotalCount,
        bool IsTruncated);

    private sealed class QueryOperationMetrics
    {
        private int _activeDiskOperations;
        private int _activeUncOperations;
        private int _peakDiskOperations;
        private int _peakUncOperations;

        public int PeakDiskOperations => Volatile.Read(ref _peakDiskOperations);

        public int PeakUncOperations => Volatile.Read(ref _peakUncOperations);

        public void RecordStarted(bool isUnc)
        {
            var activeDisk = Interlocked.Increment(ref _activeDiskOperations);
            UpdatePeak(ref _peakDiskOperations, activeDisk);
            if (isUnc)
            {
                var activeUnc = Interlocked.Increment(ref _activeUncOperations);
                UpdatePeak(ref _peakUncOperations, activeUnc);
            }
        }

        public void RecordCompleted(bool isUnc)
        {
            if (isUnc)
                Interlocked.Decrement(ref _activeUncOperations);
            Interlocked.Decrement(ref _activeDiskOperations);
        }
    }

    private sealed class DiskOperationLease : IDisposable
    {
        private Action? _release;

        public DiskOperationLease(Action release)
        {
            _release = release;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }

    private RequestLease BeginRequest()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeRequests++;
            return new RequestLease(this);
        }
    }

    private RequestLease BeginDetachedWork()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_resourcesDisposed, this);
            _activeRequests++;
            return new RequestLease(this);
        }
    }

    private void EndRequest()
    {
        var disposeResources = false;
        lock (_lifetimeGate)
        {
            _activeRequests--;
            if (_disposed && _activeRequests == 0 && !_resourcesDisposed)
            {
                _resourcesDisposed = true;
                disposeResources = true;
            }
        }

        if (disposeResources)
            DisposeResources();
    }

    private void DisposeResources()
    {
        _indexedSessions.Dispose();
        _heavyRequestGate.Dispose();
        _diskOperationGate.Dispose();
        _uncOperationGate.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private sealed class RequestLease : IDisposable
    {
        private HeadlessLogQueryBackend? _owner;

        public RequestLease(HeadlessLogQueryBackend owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndRequest();
        }
    }
}
