namespace LogReader.Infrastructure.Services;

using System.Collections.Immutable;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

/// <summary>
/// Executes bounded configured-log queries without persistence writes. Index ownership is supplied by composition.
/// </summary>
public sealed class HeadlessLogQueryBackend : ILogQueryBackend
{
    private readonly IConfiguredLogCatalogReader _catalogReader;
    private readonly ISearchService _searchService;
    private readonly IEncodingDetectionService _encodingDetection;
    private readonly IBoundedLogReaderService _logReader;
    private readonly IIndexedLogSessionProvider _indexedSessions;
    private readonly DashboardSelectionResolver _selectionResolver;
    private readonly ConfiguredLogTreeProjector _treeProjector;
    private readonly TailCursorCodec _cursorCodec;
    private readonly LogQueryEffectiveLimits _limits;
    private readonly Func<DateOnly> _today;
    private readonly SemaphoreSlim _heavyRequestGate;
    private readonly SemaphoreSlim _diskOperationGate;
    private readonly SemaphoreSlim _uncOperationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lifetimeGate = new();
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
            new TailCursorCodec())
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
        TailCursorCodec cursorCodec)
    {
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _encodingDetection = encodingDetection ?? throw new ArgumentNullException(nameof(encodingDetection));
        _logReader = logReader ?? throw new ArgumentNullException(nameof(logReader));
        _indexedSessions = indexedSessions ?? throw new ArgumentNullException(nameof(indexedSessions));
        _selectionResolver = new DashboardSelectionResolver();
        _treeProjector = new ConfiguredLogTreeProjector();
        _cursorCodec = cursorCodec ?? throw new ArgumentNullException(nameof(cursorCodec));
        _today = today ?? throw new ArgumentNullException(nameof(today));

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

        using var scope = CreateDeadlineScope(request.TimeoutMilliseconds, ct);
        try
        {
            await _heavyRequestGate.WaitAsync(scope.Token).ConfigureAwait(false);
            try
            {
                var catalogRead = await _catalogReader.ReadAsync(scope.Token).ConfigureAwait(false);
                if (!catalogRead.IsSuccess)
                    return Failure<LogSearchResult>(requestId, catalogRead.Error!);

                var selection = Resolve(
                    catalogRead.Snapshot!,
                    request.Targets,
                    request.DateOffsetDays,
                    effectiveFileLimit);
                if (!selection.IsSuccess)
                {
                    return Envelope<LogSearchResult>(
                        requestId,
                        selection.CatalogRevision,
                        isPartial: false,
                        isTruncated: selection.Summary.RejectedByLimit,
                        selection.Summary.RejectedByLimit ? ["resolved_file_limit"] : [],
                        selection.Errors,
                        result: null);
                }

                var searchResults = await SearchSelectedFilesAsync(
                    selection.Files,
                    request,
                    effectiveHitsPerFile,
                    scope.Token).ConfigureAwait(false);
                scope.Token.ThrowIfCancellationRequested();

                return await BuildSearchEnvelopeAsync(
                    requestId,
                    selection,
                    searchResults,
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

                var selection = ResolveSingleFile(catalogRead.Snapshot!, request.FileId, request.DateOffsetDays);
                if (!selection.IsSuccess)
                    return SelectionFailure<LogReadLinesResult>(requestId, selection);
                if (selection.Files.IsEmpty)
                    return SelectionFileFailure<LogReadLinesResult>(requestId, selection);

                var file = selection.Files[0];
                var responseBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters);
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
                                    file.Provenance,
                                    EncodingName(snapshot.Encoding),
                                    _cursorCodec.GetGenerationIdentity(snapshot),
                                    mapped,
                                    Error: null),
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
                        File = FailedReadFile(file, ex),
                        RequestedStartLine = request.StartLine,
                        RequestedCount = count
                    };
                }

                var truncated = responseBudget.IsExhausted ||
                                result.File?.Lines.Any(static line => line.IsTruncated) == true;
                return Envelope(
                    requestId,
                    selection.CatalogRevision,
                    isPartial: result.File?.Error != null,
                    truncated,
                    GetLineTruncationReasons(result.File?.Lines ?? [], responseBudget),
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

                var selection = ResolveSingleFile(catalogRead.Snapshot!, request.FileId, request.DateOffsetDays);
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

                var responseBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters);
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
                                    previousBounds!.EndOffset > cursor.FileSize;
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
                                        file.Provenance,
                                        EncodingName(snapshot.Encoding),
                                        generation,
                                        mapped,
                                        Error: null),
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
                        File = FailedReadFile(file, ex)
                    };
                }

                var truncated = responseBudget.IsExhausted ||
                                result.File?.Lines.Any(static line => line.IsTruncated) == true;
                return Envelope(
                    requestId,
                    selection.CatalogRevision,
                    isPartial: result.File?.Error != null,
                    truncated,
                    GetLineTruncationReasons(result.File?.Lines ?? [], responseBudget),
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

    private async Task<SearchResult[]> SearchSelectedFilesAsync(
        ImmutableArray<ResolvedConfiguredLogFile> files,
        LogSearchQuery query,
        int maximumHitsPerFile,
        CancellationToken ct)
    {
        var paths = files.Select(static file => file.PhysicalPath).ToList();
        var encodings = paths.ToDictionary(
            static path => path,
            path => _encodingDetection
                .ResolveEncodingDecision(path, FileEncoding.Auto)
                .ResolvedEncoding,
            StringComparer.OrdinalIgnoreCase);
        var searchRequest = SearchRequest.Create(
            query.Query,
            query.UseRegex,
            query.CaseSensitive,
            paths,
            SearchRequestSourceMode.DiskSnapshot,
            SearchRequestUsage.DiskSearch,
            query.StartTimestamp,
            query.EndTimestamp,
            maxHitsPerFile: maximumHitsPerFile,
            maxRetainedLineTextLength: _limits.MaximumCharactersPerLine);
        var results = await _searchService.SearchFilesBoundedAsync(
            searchRequest,
            encodings,
            _limits.MaximumConcurrentDiskOperations,
            AcquireDiskOperationAsync,
            ct).ConfigureAwait(false);
        return results.ToArray();
    }

    private async Task<LogOperationEnvelope<LogSearchResult>> BuildSearchEnvelopeAsync(
        string requestId,
        ConfiguredLogSelectionResult selection,
        SearchResult[] rawResults,
        LogSearchQuery request,
        int effectiveFileLimit,
        int effectiveHitsPerFile,
        int effectiveTotalHits,
        CancellationToken ct)
    {
        var files = new List<LogSearchFileResult>(selection.Files.Length + selection.FileErrors.Length);
        var budget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters);
        var remainingHits = effectiveTotalHits;
        var truncationReasons = new HashSet<string>(StringComparer.Ordinal);
        var hasFileError = false;

        for (var index = 0; index < selection.Files.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var file = selection.Files[index];
            var raw = rawResults[index];
            var encoding = _encodingDetection
                .ResolveEncodingDecision(file.PhysicalPath, FileEncoding.Auto)
                .ResolvedEncoding;
            if (!string.IsNullOrWhiteSpace(raw.Error))
            {
                hasFileError = true;
                files.Add(new LogSearchFileResult(
                    file.FileId,
                    file.DisplayName,
                    file.Provenance,
                    EncodingName(encoding),
                    Generation: null,
                    Hits: [],
                    Error("log_read_failed", "The configured log file could not be searched.", retryable: true, file.FileId),
                    IsTruncated: false));
                continue;
            }

            var allowedHits = Math.Min(raw.Hits.Count, remainingHits);
            var selectedHits = raw.Hits.Take(allowedHits).ToArray();
            remainingHits -= allowedHits;
            var fileTruncated = raw.HitLimitExceeded || allowedHits < raw.Hits.Count;
            if (raw.HitLimitExceeded)
                truncationReasons.Add("hits_per_file_limit");
            if (allowedHits < raw.Hits.Count ||
                remainingHits == 0 && rawResults.Skip(index + 1).Any(static result => result.Hits.Count > 0))
                truncationReasons.Add("total_hit_limit");

            ConfiguredLogRequestError? contextError = null;
            ImmutableArray<LogSearchHit> mappedHits;
            try
            {
                mappedHits = await MapSearchHitsAsync(
                    file,
                    encoding,
                    selectedHits,
                    request.IncludeContextBefore,
                    request.IncludeContextAfter,
                    budget,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsPerFileException(ex))
            {
                contextError = Error(
                    "context_read_failed",
                    "Matches were found, but indexed context could not be read.",
                    retryable: true,
                    file.FileId);
                hasFileError = true;
                fileTruncated = true;
                mappedHits = MapSearchHitsWithoutContext(selectedHits, budget);
            }

            if (mappedHits.Length < selectedHits.Length)
            {
                fileTruncated = true;
                truncationReasons.Add("response_text_limit");
            }
            if (mappedHits.Any(static hit => hit.IsTextTruncated))
                truncationReasons.Add("line_character_limit");
            if (budget.IsExhausted)
                truncationReasons.Add("response_text_limit");

            files.Add(new LogSearchFileResult(
                file.FileId,
                file.DisplayName,
                file.Provenance,
                EncodingName(encoding),
                _cursorCodec.GetGenerationIdentity(raw.GenerationEvidence),
                mappedHits,
                contextError,
                fileTruncated));
        }

        foreach (var fileError in selection.FileErrors)
        {
            hasFileError = true;
            files.Add(new LogSearchFileResult(
                fileError.FileId,
                fileError.DisplayName,
                fileError.Provenance,
                Encoding: string.Empty,
                Generation: null,
                Hits: [],
                Error(
                    fileError.Code,
                    fileError.Message,
                    retryable: false,
                    fileError.FileId),
                IsTruncated: false));
        }

        var totalHits = files.Sum(static file => file.Hits.Length);
        var result = new LogSearchResult
        {
            Files = files.ToImmutableArray(),
            SelectedFileCount = selection.Summary.ResolvedPhysicalFileCount,
            SearchedFileCount = rawResults.Length,
            TotalHitCount = totalHits,
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
            isPartial: hasFileError,
            isTruncated: truncationReasons.Count > 0,
            truncationReasons.Order(StringComparer.Ordinal).ToImmutableArray(),
            errors: [],
            result);
    }

    private async Task<ImmutableArray<LogSearchHit>> MapSearchHitsAsync(
        ResolvedConfiguredLogFile file,
        FileEncoding encoding,
        IReadOnlyList<SearchHit> hits,
        int contextBefore,
        int contextAfter,
        ResponseCharacterBudget budget,
        CancellationToken ct)
    {
        if (contextBefore == 0 && contextAfter == 0)
            return MapSearchHitsWithoutContext(hits, budget);

        return await ExecuteDiskOperationAsync(
            file.PhysicalPath,
            async token =>
            {
                using var lease = _indexedSessions.AcquireSession(file.PhysicalPath, encoding);
                var mapped = ImmutableArray.CreateBuilder<LogSearchHit>(hits.Count);
                foreach (var hit in hits)
                {
                    if (budget.IsExhausted || hit.LineNumber is < 1 or > int.MaxValue)
                        break;

                    var text = TakeLogText(hit.LineText, budget, out var responseTruncated);
                    var zeroBasedHit = checked((int)hit.LineNumber - 1);
                    var rangeStart = Math.Max(0, zeroBasedHit - contextBefore);
                    var readCount = zeroBasedHit - rangeStart + contextAfter + 1;
                    var snapshot = await lease.CaptureCurrentIndexAsync(
                        [new IndexedLogReadRange(rangeStart, readCount)],
                        token).ConfigureAwait(false);
                    var contextLines = snapshot.Lines.IsEmpty || budget.IsExhausted
                        ? Array.Empty<BoundedIndexedLine>()
                        : await ReadSnapshotAsync(
                            lease,
                            file.PhysicalPath,
                            snapshot,
                            budget.Remaining,
                            token).ConfigureAwait(false);
                    var before = MapContextLines(contextLines, zeroBasedHit, before: true, budget);
                    var after = MapContextLines(contextLines, zeroBasedHit, before: false, budget);
                    mapped.Add(new LogSearchHit(
                        hit.LineNumber,
                        text,
                        hit.LineTextTruncated || responseTruncated,
                        hit.MatchStart,
                        hit.MatchLength,
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

            var text = TakeLogText(hit.LineText, budget, out var responseTruncated);
            mapped.Add(new LogSearchHit(
                hit.LineNumber,
                text,
                hit.LineTextTruncated || responseTruncated,
                hit.MatchStart,
                hit.MatchLength,
                ContextBefore: [],
                ContextAfter: []));
        }

        return mapped.ToImmutable();
    }

    private static ImmutableArray<LogLineResult> MapContextLines(
        IReadOnlyList<BoundedIndexedLine> lines,
        int zeroBasedHit,
        bool before,
        ResponseCharacterBudget budget)
    {
        var mapped = ImmutableArray.CreateBuilder<LogLineResult>();
        foreach (var line in lines)
        {
            if (before ? line.LineNumber >= zeroBasedHit : line.LineNumber <= zeroBasedHit)
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

    private ConfiguredLogSelectionResult Resolve(
        ConfiguredLogCatalogSnapshot snapshot,
        IEnumerable<ConfiguredLogTarget> targets,
        int dateOffsetDays,
        int maximumFiles)
        => _selectionResolver.Resolve(
            snapshot,
            new ConfiguredLogSelectionRequest(
                targets,
                _today(),
                dateOffsetDays,
                _limits.MaximumTargets,
                maximumFiles),
            ExistingPathCandidateSelector.Instance);

    private ConfiguredLogSelectionResult ResolveSingleFile(
        ConfiguredLogCatalogSnapshot snapshot,
        string fileId,
        int dateOffsetDays)
        => Resolve(
            snapshot,
            [new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, fileId)],
            dateOffsetDays,
            maximumFiles: 1);

    private async Task<T> ExecuteDiskOperationAsync<T>(
        string filePath,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        await _diskOperationGate.WaitAsync(ct).ConfigureAwait(false);
        var uncAcquired = false;
        try
        {
            if (IsUncPath(filePath))
            {
                await _uncOperationGate.WaitAsync(ct).ConfigureAwait(false);
                uncAcquired = true;
            }

            return await operation(ct).ConfigureAwait(false);
        }
        finally
        {
            if (uncAcquired)
                _uncOperationGate.Release();
            _diskOperationGate.Release();
        }
    }

    private async ValueTask<IDisposable> AcquireDiskOperationAsync(
        string filePath,
        CancellationToken ct)
    {
        await _diskOperationGate.WaitAsync(ct).ConfigureAwait(false);
        var uncAcquired = false;
        try
        {
            if (IsUncPath(filePath))
            {
                await _uncOperationGate.WaitAsync(ct).ConfigureAwait(false);
                uncAcquired = true;
            }

            return new DiskOperationLease(
                _diskOperationGate,
                uncAcquired ? _uncOperationGate : null);
        }
        catch
        {
            if (uncAcquired)
                _uncOperationGate.Release();
            _diskOperationGate.Release();
            throw;
        }
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
            limits.DefaultTimeoutMilliseconds < 1)
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

    private static ImmutableArray<string> GetLineTruncationReasons(
        ImmutableArray<LogLineResult> lines,
        ResponseCharacterBudget budget)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
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
        Exception exception)
        => new(
            file.FileId,
            file.DisplayName,
            file.Provenance,
            Encoding: string.Empty,
            Generation: null,
            Lines: [],
            ToFileError(exception, file.FileId));

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

        public bool IsExhausted => Remaining == 0;

        public int Consume(int requestedCharacters)
        {
            var admitted = Math.Min(Remaining, Math.Max(0, requestedCharacters));
            Remaining -= admitted;
            return admitted;
        }
    }

    private sealed class ExistingPathCandidateSelector : IConfiguredLogPathCandidateSelector
    {
        internal static ExistingPathCandidateSelector Instance { get; } = new();

        public string SelectPath(string fileId, ImmutableArray<string> orderedCandidates)
            => orderedCandidates.FirstOrDefault(File.Exists) ?? orderedCandidates[0];
    }

    private sealed class InvalidTailCursorException : Exception;

    private sealed class DiskOperationLease : IDisposable
    {
        private SemaphoreSlim? _diskGate;
        private SemaphoreSlim? _uncGate;

        public DiskOperationLease(SemaphoreSlim diskGate, SemaphoreSlim? uncGate)
        {
            _diskGate = diskGate;
            _uncGate = uncGate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _uncGate, null)?.Release();
            Interlocked.Exchange(ref _diskGate, null)?.Release();
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
