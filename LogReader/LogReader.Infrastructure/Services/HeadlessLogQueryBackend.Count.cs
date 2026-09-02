namespace LogReader.Infrastructure.Services;

using System.Collections.Immutable;
using LogReader.Core;
using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackend
{
    public async Task<LogOperationEnvelope<LogCountResult>> CountLogsAsync(
        LogCountQuery request,
        CancellationToken ct = default)
    {
        using var requestLease = BeginRequest();
        ArgumentNullException.ThrowIfNull(request);
        var requestId = CreateRequestId();
        var validation = ValidateCountRequest(request);
        if (!validation.IsEmpty)
            return Rejected<LogCountResult>(requestId, validation);

        var capturedNow = _now();
        if (!CountTimeWindowResolver.TryResolve(
                request,
                capturedNow,
                _localTimeZone,
                _limits,
                out var timeResolution,
                out var timeError))
        {
            return Rejected<LogCountResult>(requestId, [timeError!]);
        }

        _queryOperationMetrics.Value = new QueryOperationMetrics();
        using var scope = CreateDeadlineScope(request.TimeoutMilliseconds, ct);
        var resolvedTime = timeResolution!;
        var accumulator = new CountAccumulator(
            resolvedTime,
            evidence => _cursorCodec.GetGenerationIdentity(evidence));
        var catalogRevision = string.Empty;
        var deadlineExceeded = false;
        var gateAcquired = false;
        try
        {
            await _heavyRequestGate.WaitAsync(scope.Token).ConfigureAwait(false);
            gateAcquired = true;
            var catalogRead = await _catalogReader.ReadAsync(scope.Token).ConfigureAwait(false);
            if (!catalogRead.IsSuccess)
                return Failure<LogCountResult>(requestId, catalogRead.Error!);

            catalogRevision = catalogRead.Snapshot!.Revision;
            var localNow = TimeZoneInfo.ConvertTime(capturedNow, _localTimeZone);
            var referenceDate = DateOnly.FromDateTime(localNow.DateTime);
            ConfiguredLogSelectionContinuation? continuation = null;
            while (true)
            {
                var selection = await ResolveAsync(
                    catalogRead.Snapshot,
                    request.Targets,
                    request.DateOffsetDays,
                    referenceDate,
                    _limits.MaximumFiles,
                    continuation,
                    scope.Token).ConfigureAwait(false);

                accumulator.ObserveSelection(selection);
                if (!selection.IsSuccess)
                {
                    var selectionLimitReason = selection.Errors.Any(error =>
                        StringComparer.Ordinal.Equals(error.Code, "search_candidate_limit_exceeded"))
                        ? "search_candidate_limit"
                        : "resolved_file_limit";
                    return Envelope<LogCountResult>(
                        requestId,
                        selection.CatalogRevision,
                        isPartial: false,
                        isTruncated: selection.Summary.RejectedByLimit,
                        selection.Summary.RejectedByLimit ? [selectionLimitReason] : [],
                        selection.Errors,
                        result: null);
                }

                accumulator.AddSelectionErrors(selection);
                var batch = await CountSelectedFilesAsync(
                    selection.Files,
                    request,
                    resolvedTime,
                    scope.Token).ConfigureAwait(false);
                accumulator.AddBatch(selection, batch);
                if (batch.WasCancelled)
                {
                    if (ct.IsCancellationRequested || _lifetimeCancellation.IsCancellationRequested)
                        scope.Token.ThrowIfCancellationRequested();
                    deadlineExceeded = IsInternalDeadlineCancellation(scope, ct);
                    if (!deadlineExceeded)
                        accumulator.MarkIncomplete("evaluation_cancelled");
                    accumulator.SetRemaining(selection.Summary.RemainingCandidateCount + batch.UnprocessedFileCount);
                    break;
                }

                if (selection.Continuation == null)
                {
                    accumulator.SetRemaining(0);
                    break;
                }

                continuation = selection.Continuation;
            }

            if (deadlineExceeded)
                accumulator.MarkIncomplete("deadline_exceeded");

            return BuildCountEnvelope(
                requestId,
                catalogRevision,
                accumulator,
                deadlineExceeded);
        }
        catch (OperationCanceledException) when (IsInternalDeadlineCancellation(scope, ct))
        {
            accumulator.MarkIncomplete("deadline_exceeded");
            return BuildCountEnvelope(
                requestId,
                catalogRevision,
                accumulator,
                deadlineExceeded: true);
        }
        catch (OperationCanceledException) when (
            ct.IsCancellationRequested || _lifetimeCancellation.IsCancellationRequested)
        {
            return Cancelled<LogCountResult>(requestId, ct);
        }
        finally
        {
            if (gateAcquired)
                _heavyRequestGate.Release();
        }
    }

    private async Task<CountBatchOutcome> CountSelectedFilesAsync(
        ImmutableArray<ResolvedConfiguredLogFile> files,
        LogCountQuery query,
        CountTimeResolution timeResolution,
        CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var searchRequest = SearchRequest.Create(
            query.Query,
            query.UseRegex,
            query.CaseSensitive,
            files.Select(static file => file.PhysicalPath),
            SearchRequestSourceMode.DiskSnapshot,
            SearchRequestUsage.DiskSearch,
            timeResolution.StartTimestamp,
            timeResolution.EndTimestamp,
            maxHitsPerFile: 0,
            maxRetainedLineTextLength: _limits.MaximumCharactersPerLine,
            continueEvaluatingAfterHitLimit: true,
            timestampAggregation: timeResolution.AggregationPlan);
        var batch = await _searchService.SearchFilesBoundedWithEncodingPartialAsync(
            searchRequest,
            _limits.MaximumConcurrentDiskOperations,
            path => _encodingDetection.ResolveEncodingDecision(path, FileEncoding.Auto).ResolvedEncoding,
            AcquireDiskOperationAsync,
            ct).ConfigureAwait(false);
        stopwatch.Stop();
        return new CountBatchOutcome(batch.Results, batch.WasCancelled, stopwatch.ElapsedMilliseconds);
    }

    private LogOperationEnvelope<LogCountResult> BuildCountEnvelope(
        string requestId,
        string catalogRevision,
        CountAccumulator accumulator,
        bool deadlineExceeded)
    {
        var contentBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters * 3 / 4);
        if (accumulator.TimeResolution.ResolvedRange is { } resolvedRange)
        {
            contentBudget.Consume(
                resolvedRange.Kind.Length +
                resolvedRange.Start.Length +
                resolvedRange.End.Length +
                resolvedRange.TimeZoneId.Length +
                (resolvedRange.RelativeWindow?.Length ?? 0));
        }

        var buckets = BuildCountBuckets(accumulator, contentBudget);
        var provenanceBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters / 4);
        var files = ImmutableArray.CreateBuilder<LogCountFileResult>();
        var metadataTruncated = false;
        foreach (var entry in accumulator.FileEntries.OrderBy(static entry => entry.StableFileIndex))
        {
            var baseCharacters = CountFileEntryCharacters(entry);
            if (baseCharacters > contentBudget.Remaining)
            {
                metadataTruncated = true;
                break;
            }

            contentBudget.Consume(baseCharacters);
            var retainedProvenance = RetainProvenance(entry.Provenance, provenanceBudget);
            if (retainedProvenance.IsTruncated)
                metadataTruncated = true;
            files.Add(new LogCountFileResult(
                entry.FileId,
                entry.DisplayName,
                retainedProvenance.Items,
                entry.Encoding,
                entry.Generation,
                entry.Error)
            {
                ProvenanceTotalCount = retainedProvenance.TotalCount,
                IsProvenanceTruncated = retainedProvenance.IsTruncated,
                MatchingLineCount = entry.MatchingLineCount,
                MatchOccurrenceCount = entry.MatchOccurrenceCount,
                IsCountExact = entry.IncompleteReasons.IsEmpty,
                IncompleteReasons = entry.IncompleteReasons
            });
        }

        if (files.Count < accumulator.FileEntries.Count)
            metadataTruncated = true;
        var exact = accumulator.IncompleteReasons.Count == 0 && accumulator.RemainingFileCount == 0;
        var result = new LogCountResult
        {
            MatchingLineCount = accumulator.MatchingLineCount,
            MatchOccurrenceCount = accumulator.MatchOccurrenceCount,
            UnbucketedMatchingLineCount = accumulator.UnbucketedMatchingLineCount,
            UnbucketedMatchOccurrenceCount = accumulator.UnbucketedMatchOccurrenceCount,
            SelectedFileCount = accumulator.SelectedFileCount,
            SearchedFileCount = accumulator.SearchedFileCount,
            MatchedFileCount = accumulator.MatchedFileCount,
            SkippedFileCount = accumulator.SkippedFileCount,
            FailedFileCount = accumulator.FailedFileCount,
            RemainingFileCount = accumulator.RemainingFileCount,
            AreCountsExact = exact,
            IsComplete = exact,
            CompletionState = exact ? "complete" : "incomplete",
            IncompleteReasons = accumulator.IncompleteReasons.Order(StringComparer.Ordinal).ToImmutableArray(),
            ResolvedTimeRange = accumulator.TimeResolution.ResolvedRange,
            BucketSize = accumulator.TimeResolution.BucketSize,
            Buckets = buckets,
            Files = files.ToImmutable(),
            FileRecordTotalCount = accumulator.FileEntries.Count,
            ReturnedFileRecordCount = files.Count,
            IsFileRecordTruncated = metadataTruncated,
            Statistics = new LogSearchStatistics(
                accumulator.BytesEvaluated,
                accumulator.ElapsedMilliseconds,
                accumulator.FilesStarted,
                accumulator.FilesCompleted,
                accumulator.SkippedFileCount,
                _queryOperationMetrics.Value?.PeakDiskOperations ?? 0,
                _queryOperationMetrics.Value?.PeakUncOperations ?? 0),
            EffectiveLimits = _limits
        };
        var truncationReasons = metadataTruncated
            ? ImmutableArray.Create("count_metadata_limit")
            : ImmutableArray<string>.Empty;
        var errors = deadlineExceeded
            ? ImmutableArray.Create(Error(
                "deadline_exceeded",
                "The request deadline was exceeded after partial count results were collected.",
                retryable: true))
            : ImmutableArray<ConfiguredLogRequestError>.Empty;
        return Envelope(
            requestId,
            catalogRevision,
            isPartial: !exact,
            isTruncated: metadataTruncated,
            truncationReasons,
            errors,
            result);
    }

    private static ImmutableArray<LogCountBucket> BuildCountBuckets(
        CountAccumulator accumulator,
        ResponseCharacterBudget contentBudget)
    {
        if (accumulator.TimeResolution.AggregationPlan == null)
            return [];

        var buckets = ImmutableArray.CreateBuilder<LogCountBucket>();
        foreach (var definition in accumulator.TimeResolution.AggregationPlan.Buckets)
        {
            var kind = accumulator.TimeResolution.AggregationPlan.Kind == SearchTimestampBucketKind.Dated
                ? "dated"
                : "timeOfDay";
            contentBudget.Consume(definition.Start.Length + definition.EndExclusive.Length + kind.Length);
            accumulator.BucketCounts.TryGetValue(definition.Index, out var count);
            buckets.Add(new LogCountBucket(
                kind,
                definition.Start,
                definition.EndExclusive,
                count?.MatchingLineCount ?? 0,
                count?.MatchOccurrenceCount ?? 0));
        }

        return buckets.ToImmutable();
    }

    private ImmutableArray<ConfiguredLogRequestError> ValidateCountRequest(LogCountQuery request)
    {
        var errors = ImmutableArray.CreateBuilder<ConfiguredLogRequestError>();
        if (request.Targets == null || request.Targets.Count == 0)
            errors.Add(Error("targets_required", "At least one configured target is required."));
        else if (request.Targets.Count > _limits.MaximumTargets)
            errors.Add(Error("target_limit_exceeded", $"No more than {_limits.MaximumTargets} targets may be requested."));
        if (string.IsNullOrEmpty(request.Query))
            errors.Add(Error("query_required", "A non-empty count query is required."));
        else if (request.Query.Length > _limits.MaximumQueryCharacters)
            errors.Add(Error("query_too_long", $"The query cannot exceed {_limits.MaximumQueryCharacters} characters."));
        if (request.StartTimestamp is { Length: > ConfiguredLogLimits.DefaultMaxTimestampCharacters } ||
            request.EndTimestamp is { Length: > ConfiguredLogLimits.DefaultMaxTimestampCharacters } ||
            request.RelativeWindow is { Length: > ConfiguredLogLimits.DefaultMaxTimestampCharacters })
        {
            errors.Add(Error(
                "timestamp_too_long",
                $"Timestamp and relative-window values cannot exceed {ConfiguredLogLimits.DefaultMaxTimestampCharacters} characters."));
        }
        if (request.DateOffsetDays < 0)
            errors.Add(Error("invalid_date_offset", "dateOffsetDays cannot be negative."));
        if (request.UseRegex && !RegexPatternFactory.TryCreate(request.Query, request.CaseSensitive, out _))
            errors.Add(Error("invalid_regex", "The regular expression is invalid."));
        ValidateTimeout(request.TimeoutMilliseconds, errors);
        return errors.ToImmutable();
    }

    private bool IsInternalDeadlineCancellation(
        CancellationTokenSource deadlineScope,
        CancellationToken callerToken)
        => deadlineScope.IsCancellationRequested &&
           !callerToken.IsCancellationRequested &&
           !_lifetimeCancellation.IsCancellationRequested;

    private static int CountFileEntryCharacters(CountFileEntry entry)
        => checked(
            entry.FileId.Length +
            entry.DisplayName.Length +
            entry.Encoding.Length +
            (entry.Generation?.Length ?? 0) +
            (entry.Error?.Code.Length ?? 0) +
            (entry.Error?.Message.Length ?? 0) +
            entry.IncompleteReasons.Sum(static reason => reason.Length));

    private sealed record CountBatchOutcome(
        IReadOnlyList<SearchResult?> Results,
        bool WasCancelled,
        long ElapsedMilliseconds)
    {
        public int UnprocessedFileCount => Results.Count(static result => result == null);
    }

    private sealed record CountFileEntry(
        int StableFileIndex,
        string FileId,
        string DisplayName,
        ImmutableArray<ConfiguredLogProvenance> Provenance,
        string Encoding,
        string? Generation,
        long MatchingLineCount,
        long MatchOccurrenceCount,
        ConfiguredLogRequestError? Error,
        ImmutableArray<string> IncompleteReasons);

    private sealed class CountAccumulator
    {
        private readonly Func<FileScanGenerationEvidence, string?> _generationIdentity;

        public CountAccumulator(
            CountTimeResolution timeResolution,
            Func<FileScanGenerationEvidence, string?> generationIdentity)
        {
            TimeResolution = timeResolution;
            _generationIdentity = generationIdentity;
        }

        public CountTimeResolution TimeResolution { get; }
        public List<CountFileEntry> FileEntries { get; } = [];
        public Dictionary<int, SearchTimestampBucketCount> BucketCounts { get; } = [];
        public HashSet<string> IncompleteReasons { get; } = new(StringComparer.Ordinal);
        public long MatchingLineCount { get; private set; }
        public long MatchOccurrenceCount { get; private set; }
        public long UnbucketedMatchingLineCount { get; private set; }
        public long UnbucketedMatchOccurrenceCount { get; private set; }
        public int SelectedFileCount { get; private set; }
        public int SearchedFileCount { get; private set; }
        public int MatchedFileCount { get; private set; }
        public int SkippedFileCount { get; private set; }
        public int FailedFileCount { get; private set; }
        public int RemainingFileCount { get; private set; }
        public long BytesEvaluated { get; private set; }
        public long ElapsedMilliseconds { get; private set; }
        public int FilesStarted { get; private set; }
        public int FilesCompleted { get; private set; }

        public void ObserveSelection(ConfiguredLogSelectionResult selection)
        {
            SelectedFileCount = Math.Max(SelectedFileCount, selection.Summary.ExpandedStableFileCount);
            RemainingFileCount = selection.Summary.RemainingCandidateCount;
        }

        public void AddSelectionErrors(ConfiguredLogSelectionResult selection)
        {
            foreach (var error in selection.FileErrors)
            {
                SkippedFileCount++;
                IncompleteReasons.Add("file_selection_failed");
                FileEntries.Add(new CountFileEntry(
                    StableFileIndex(selection, error.FileId),
                    error.FileId,
                    error.DisplayName,
                    error.Provenance,
                    string.Empty,
                    null,
                    0,
                    0,
                    new ConfiguredLogRequestError(error.Code, error.Message, error.FileId),
                    ImmutableArray.Create("file_selection_failed")));
            }
        }

        public void AddBatch(ConfiguredLogSelectionResult selection, CountBatchOutcome batch)
        {
            var files = selection.Files;
            ElapsedMilliseconds += batch.ElapsedMilliseconds;
            for (var index = 0; index < batch.Results.Count; index++)
            {
                var raw = batch.Results[index];
                if (raw == null)
                    continue;

                var file = files[index];
                SearchedFileCount++;
                FilesStarted++;
                if (!raw.WasCancelled)
                    FilesCompleted++;
                BytesEvaluated += raw.ScannedFileSize ?? 0;
                MatchingLineCount += raw.MatchingLineCount;
                MatchOccurrenceCount += raw.MatchOccurrenceCount;
                UnbucketedMatchingLineCount += raw.UnbucketedMatchingLineCount;
                UnbucketedMatchOccurrenceCount += raw.UnbucketedMatchOccurrenceCount;
                if (raw.MatchingLineCount > 0)
                    MatchedFileCount++;

                foreach (var (bucketIndex, rawCount) in raw.TimestampBucketCounts)
                {
                    if (!BucketCounts.TryGetValue(bucketIndex, out var count))
                    {
                        count = new SearchTimestampBucketCount();
                        BucketCounts.Add(bucketIndex, count);
                    }
                    count.MatchingLineCount += rawCount.MatchingLineCount;
                    count.MatchOccurrenceCount += rawCount.MatchOccurrenceCount;
                }

                var reasons = FileIncompleteReasons(raw);
                IncompleteReasons.UnionWith(reasons);
                ConfiguredLogRequestError? fileError = null;
                if (!string.IsNullOrWhiteSpace(raw.Error))
                {
                    FailedFileCount++;
                    fileError = Error(
                        "log_read_failed",
                        "The configured log file could not be counted.",
                        retryable: true,
                        file.FileId);
                }

                if (raw.MatchingLineCount > 0 || !reasons.IsEmpty)
                {
                    FileEntries.Add(new CountFileEntry(
                        StableFileIndex(selection, file.FileId),
                        file.FileId,
                        file.DisplayName,
                        file.Provenance,
                        EncodingName(raw.ResolvedEncoding),
                        _generationIdentity(raw.GenerationEvidence),
                        raw.MatchingLineCount,
                        raw.MatchOccurrenceCount,
                        fileError,
                        reasons));
                }
            }
        }

        public void SetRemaining(int count)
            => RemainingFileCount = Math.Max(0, count);

        public void MarkIncomplete(string reason)
            => IncompleteReasons.Add(reason);

        private static int StableFileIndex(ConfiguredLogSelectionResult selection, string fileId)
            => selection.StableFileIndexesById.TryGetValue(fileId, out var stableFileIndex)
                ? stableFileIndex
                : int.MaxValue;

        private static ImmutableArray<string> FileIncompleteReasons(SearchResult raw)
        {
            var reasons = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(raw.Error))
                reasons.Add("file_read_failed");
            if (raw.WasCancelled)
                reasons.Add("evaluation_cancelled");
            if (!raw.IsEvaluationComplete)
                reasons.Add("evaluation_incomplete");
            if (raw.FileChangedDuringOrAfterScan)
                reasons.Add("file_changed_during_search");
            if (raw.GenerationEvidence.Correlation == FileGenerationCorrelation.Stale)
                reasons.Add("file_generation_changed");
            else if (raw.GenerationEvidence.Correlation != FileGenerationCorrelation.Current)
                reasons.Add("file_generation_unverified");
            if (raw.UnbucketedMatchingLineCount > 0)
                reasons.Add("timestamp_bucket_unassigned");
            return reasons.Order(StringComparer.Ordinal).ToImmutableArray();
        }
    }
}
