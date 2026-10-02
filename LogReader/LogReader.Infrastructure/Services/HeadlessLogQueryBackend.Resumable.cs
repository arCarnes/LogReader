namespace LogReader.Infrastructure.Services;

using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackend
{
    private QueryContinuationStore? _continuations;

    private QueryContinuationStore Continuations
    {
        get
        {
            lock (_lifetimeGate)
                return _continuations ??= new QueryContinuationStore(_limits, _now);
        }
    }

    private async Task<LogOperationEnvelope<T>> RunResumableAsync<T>(LogSearchQuery search,
        LogCountQuery? count, int fileLimit, int hitsPerFile, int totalHits, CancellationToken callerToken)
    {
        var requestId = CreateRequestId();
        var operation = count == null ? "search" : "count";
        var cursor = count?.Cursor ?? search.Cursor;
        var fingerprint = count == null
            ? CreateSearchRequestFingerprint(search, fileLimit, hitsPerFile, totalHits)
            : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                count.Targets, count.Query, count.UseRegex, count.CaseSensitive, count.DateOffsetDays,
                count.StartTimestamp, count.EndTimestamp, count.RelativeWindow, count.BucketSize
            })));
        _queryOperationMetrics.Value = new QueryOperationMetrics();
        using var deadline = CreateDeadlineScope(count?.TimeoutMilliseconds ?? search.TimeoutMilliseconds, callerToken);
        var gate = false;
        try
        {
            await _heavyRequestGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            gate = true;
            var catalog = await _catalogReader.ReadAsync(deadline.Token).ConfigureAwait(false);
            if (!catalog.IsSuccess)
                return Failure<T>(requestId, catalog.Error!);
            using var transaction = await Continuations.AcquireAsync(cursor, operation, fingerprint,
                catalog.Snapshot!.Revision, deadline.Token).ConfigureAwait(false);
            if (transaction.IsReplay)
                return (LogOperationEnvelope<T>)transaction.ReplayResult!;

            var state = transaction.State is QueryTraversal previous ? previous.Clone() : new QueryTraversal
            {
                ReferenceDate = _today()
            };
            if (count != null && state.CountTime == null)
            {
                var now = _now();
                if (!CountTimeWindowResolver.TryResolve(count, now, _localTimeZone, _limits,
                    out var time, out var error))
                    return Rejected<T>(requestId, [error!]);
                state.CountTime = time;
                state.ReferenceDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _localTimeZone).DateTime);
            }
            var timeout = count?.TimeoutMilliseconds ?? search.TimeoutMilliseconds ?? _limits.DefaultTimeoutMilliseconds;
            var budget = new ScanWorkBudget(Math.Max(1, Math.Min(_limits.SearchWorkMilliseconds, timeout * 4 / 5)),
                _limits.SearchScanBytes, _scanTimestamp);
            var request = SearchRequest.Create(search.Query, search.UseRegex, search.CaseSensitive, [],
                SearchRequestSourceMode.DiskSnapshot, SearchRequestUsage.DiskSearch,
                state.CountTime?.StartTimestamp ?? search.StartTimestamp,
                state.CountTime?.EndTimestamp ?? search.EndTimestamp,
                maxHitsPerFile: count != null || search.ResultMode == "countsOnly" ? 0 : null,
                maxRetainedLineTextLength: _limits.MaximumCharactersPerLine,
                timestampAggregation: state.CountTime?.AggregationPlan);
            var scanner = ((SearchService)_searchService).CreateResumableScanner(_encodingDetection);
            var regex = ((SearchService)_searchService).PrepareResumableRegex(request);
            var page = new TraversalPage(_limits.MaximumResponseCharacters);
            var readers = new ConcurrentDictionary<TraversalFile, ActiveScanReader>();
            var stopwatch = Stopwatch.StartNew();
            var stopReason = "scope_exhausted";
            var hardDeadline = false;
            try
            {
                SetHitAllowance(state.Files.Where(static file => !file.Done));
                while (!budget.IsStopped)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    while (state.NextFileIndex < state.Files.Count && state.Files[state.NextFileIndex].Done)
                        state.NextFileIndex++;
                    var current = state.NextFileIndex < state.Files.Count ? state.Files[state.NextFileIndex] : null;
                    if (current == null)
                    {
                        if (state.ResolverFinished)
                            break;
                        if (count == null && page.ResolvedPages > 0)
                        {
                            stopReason = "scan_budget";
                            break;
                        }
                        var selection = await ResolveAsync(catalog.Snapshot, search.Targets, search.DateOffsetDays,
                            state.ReferenceDate, fileLimit, state.Resolver, deadline.Token).ConfigureAwait(false);
                        if (!selection.IsSuccess)
                            return Envelope<T>(requestId, selection.CatalogRevision, false, selection.Summary.RejectedByLimit,
                                selection.Summary.RejectedByLimit ? [selection.Errors.Any(static error => error.Code == "search_candidate_limit_exceeded")
                                    ? "search_candidate_limit" : "resolved_file_limit"] : [], selection.Errors, default);
                        state.SelectedFiles = selection.Summary.ExpandedStableFileCount;
                        state.RemainingCandidates = selection.Summary.RemainingCandidateCount;
                        state.Resolver = selection.Continuation;
                        state.ResolverFinished = !selection.HasMore;
                        foreach (var file in selection.Files)
                            state.Files.Add(new TraversalFile(file, selection.StableFileIndexesById[file.FileId]));
                        foreach (var error in selection.FileErrors)
                        {
                            var file = new TraversalFile(new ResolvedConfiguredLogFile(error.FileId, error.DisplayName,
                                "", [], [], error.Provenance), selection.StableFileIndexesById[error.FileId])
                            {
                                Done = true, SelectionError = true,
                                Error = Error(error.Code, error.Message, targetId: error.FileId)
                            };
                            file.Checkpoint.Reasons.Add("file_selection_failed");
                            state.Files.Add(file);
                            page.Observe(file);
                        }
                        state.Files.Sort(static (left, right) => left.StableIndex.CompareTo(right.StableIndex));
                        page.ResolvedPages++;
                        SetHitAllowance(state.Files.Where(static file => !file.Done));
                        EnsureTraversalCapacity(state);
                        continue;
                    }

                    var followingIndex = state.NextFileIndex + 1;
                    while (followingIndex < state.Files.Count && state.Files[followingIndex].Done)
                        followingIndex++;
                    var following = followingIndex < state.Files.Count ? state.Files[followingIndex] : null;
                    var fill = new List<Task>();
                    if (current.Pending.Count == 0 || NeedsContext(current, search, count))
                        fill.Add(FillAsync(current, current.Pending.Count == 0 ? 256 : search.IncludeContextAfter + 1));
                    if (following != null && following.Pending.Count == 0 && !following.Checkpoint.Initialized)
                        fill.Add(FillAsync(following, 256));
                    if (fill.Count > 0)
                        await Task.WhenAll(fill).ConfigureAwait(false);
                    foreach (var failed in new[] { current, following }.OfType<TraversalFile>()
                        .Where(static file => file.Error != null && !file.Reported))
                    {
                        page.Observe(failed);
                        await CloseAsync(failed).ConfigureAwait(false);
                    }
                    if (current.Done)
                    {
                        page.Observe(current);
                        await CloseAsync(current).ConfigureAwait(false);
                        continue;
                    }
                    if (current.Pending.Count == 0 || NeedsContext(current, search, count))
                    {
                        if (budget.IsStopped)
                            break;
                        if (current.Checkpoint.Done)
                        {
                            FinishFile(current);
                            page.Observe(current);
                            await CloseAsync(current).ConfigureAwait(false);
                        }
                        continue;
                    }

                    var line = current.Pending[0];
                    var segment = page.Observe(current);
                    var textMode = count == null && search.ResultMode != "countsOnly";
                    if (line.Hit != null && textMode)
                    {
                        if (segment.Result.Hits.Count >= hitsPerFile || page.Hits >= totalHits)
                        {
                            stopReason = "hit_limit";
                            break;
                        }
                        if (page.HitBudget.IsExhausted || page.Hits > 0 &&
                            line.Hit.LineText.Length > page.HitBudget.Remaining)
                        {
                            stopReason = "response_limit";
                            break;
                        }
                        var retained = TakeSearchHitText(line.Hit, page.HitBudget);
                        segment.Result.Hits.Add(new SearchHit
                        {
                            LineNumber = line.Number, LineText = retained.Text,
                            MatchStart = retained.MatchStart, MatchLength = retained.MatchLength,
                            LineTextTruncated = retained.IsTruncated || line.Hit.LineTextTruncated
                        });
                        segment.Lines[line.Number] = new LogSearchExcerptLine(line.Number, retained.Text,
                            retained.IsTruncated || line.Hit.LineTextTruncated);
                        if (retained.IsTruncated)
                            page.TruncationReasons.Add("response_text_limit");
                        if (line.Hit.LineTextTruncated)
                            page.TruncationReasons.Add("line_character_limit");
                        page.Hits++;
                        if (search.ResultMode == "samples")
                        {
                            foreach (var context in current.History.TakeLast(search.IncludeContextBefore)
                                .Concat(current.Pending.Skip(1).Take(search.IncludeContextAfter)))
                                segment.Context.TryAdd(context.Number, context);
                        }
                    }

                    SearchService.AccountResumableLine(current.Total, request, line.Occurrences, line.Timestamp);
                    SearchService.AccountResumableLine(segment.Result, request, line.Occurrences, line.Timestamp);
                    segment.Bytes += line.Bytes;
                    current.EvaluatedThroughLine = line.Number;
                    current.Pending.RemoveAt(0);
                    if (search.ResultMode == "samples" && count == null && search.IncludeContextBefore > 0)
                    {
                        current.History.Add(line);
                        if (current.History.Count > search.IncludeContextBefore)
                            current.History.RemoveAt(0);
                    }
                    if (current.Pending.Count == 0 && current.Checkpoint.Done)
                    {
                        FinishFile(current);
                        await CloseAsync(current).ConfigureAwait(false);
                    }
                }
                if (budget.IsStopped && stopReason == "scope_exhausted")
                    stopReason = budget.StopReason;

                void SetHitAllowance(IEnumerable<TraversalFile> files)
                {
                    var provenance = new ResponseCharacterBudget(_limits.MaximumResponseCharacters / 4);
                    foreach (var file in files)
                        RetainProvenance(file.File.Provenance, provenance);
                    var spent = page.HitBudget.Consumed;
                    page.HitBudget = new ResponseCharacterBudget(Math.Max(0,
                        _limits.MaximumResponseCharacters - provenance.Consumed));
                    page.HitBudget.Consume(spent);
                }

                async Task FillAsync(TraversalFile file, int desired)
                {
                    if (file.Done || file.Checkpoint.Done || budget.IsStopped)
                        return;
                    try
                    {
                        using var disk = await AcquireDiskOperationAsync(file.File.PhysicalPath, deadline.Token).ConfigureAwait(false);
                        if (!readers.TryGetValue(file, out var active))
                        {
                            var reader = await scanner.OpenAsync(file.File.PhysicalPath, file.Checkpoint, deadline.Token).ConfigureAwait(false);
                            active = new ActiveScanReader(reader);
                            readers.TryAdd(file, active);
                            file.Started = true;
                        }
                        while (file.Pending.Count < desired && !file.Checkpoint.Done && !budget.IsStopped)
                        {
                            var line = await scanner.ReadNextAsync(active.Reader, file.Checkpoint, request, regex,
                                budget, _limits.MaximumSearchLineBytes, deadline.Token).ConfigureAwait(false);
                            if (line == null)
                                break;
                            // Only bounded text and occurrence summaries survive read-ahead.
                            var text = count != null || search.ResultMode == "countsOnly" ? string.Empty :
                                line.Hit?.LineText ?? line.Text[..Math.Min(line.Text.Length, _limits.MaximumCharactersPerLine)];
                            file.Pending.Add(new PendingScanLine(line.Number, LogContentSanitizer.Normalize(text),
                                line.Hit, line.Occurrences, line.Timestamp, line.Bytes,
                                line.Text.Length > _limits.MaximumCharactersPerLine));
                            if (line.Hit != null && count == null && search.ResultMode != "countsOnly")
                            {
                                desired = Math.Min(desired, file.Pending.Count +
                                    (search.ResultMode == "samples" ? search.IncludeContextAfter : 0));
                            }
                        }
                        scanner.ValidateAfterRead(active.Reader.Stream, file.Checkpoint);
                        // The name must still identify the open generation, including replacement while reading.
                        using var verification = new FileStream(file.File.PhysicalPath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        var identity = FileGenerationTokenProvider.Capture(verification);
                        if (file.Checkpoint.Generation.IsKnown && identity != file.Checkpoint.Generation)
                            throw new ScanFileException("log_generation_changed");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
                    {
                        file.Started = true;
                        file.Error = ex is ScanFileException scan
                            ? Error(scan.Code, "The configured file could not be continued safely.", targetId: file.File.FileId)
                            : ex is RegexMatchTimeoutException
                                ? Error("regex_match_timeout", "The regular expression match timed out.", targetId: file.File.FileId)
                                : Error("log_read_failed", "The configured log file could not be searched.", targetId: file.File.FileId);
                        file.Checkpoint.Reasons.Add(ex is ScanFileException changed && changed.Code.Contains("generation")
                            ? "file_generation_unverified" : "file_read_failed");
                        file.Done = true;
                        file.Pending.Clear();
                        file.History.Clear();
                        file.Checkpoint.ReleaseBuffers();
                    }
                }

                async Task CloseAsync(TraversalFile file)
                {
                    if (readers.TryRemove(file, out var active))
                        await active.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested &&
                !_lifetimeCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                hardDeadline = true;
                stopReason = "time_slice";
            }
            finally
            {
                foreach (var active in readers.Values)
                    await active.DisposeAsync().ConfigureAwait(false);
            }
            callerToken.ThrowIfCancellationRequested();
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            stopwatch.Stop();
            foreach (var file in state.Files.Where(static file => file.Started && !file.Done && !file.Checkpoint.Generation.IsKnown))
            {
                file.Error = Error("log_generation_unverified", "The file identity cannot support continuation.", targetId: file.File.FileId);
                file.Checkpoint.Reasons.Add("file_generation_unverified");
                FinishFile(file);
                page.Observe(file);
            }
            var traversalComplete = state.ResolverFinished && state.Files.All(static file => file.Done);
            if (traversalComplete)
                stopReason = "scope_exhausted";
            var nextCursor = traversalComplete ? null : transaction.NextCursor;
            object result = count == null
                ? BuildResumableSearch(requestId, catalog.Snapshot.Revision, state, page, search, nextCursor,
                    stopReason, stopwatch.ElapsedMilliseconds, hardDeadline)
                : BuildResumableCount(requestId, catalog.Snapshot.Revision, state, page, nextCursor,
                    stopReason, stopwatch.ElapsedMilliseconds, hardDeadline);
            foreach (var file in page.Files.Keys.Where(static file => file.Done))
                file.Reported = true;
            EnsureTraversalCapacity(state);
            var bytes = state.RetainedBytes + JsonSerializer.SerializeToUtf8Bytes(result).LongLength * 3 + 4096;
            transaction.Commit(state, result, bytes, traversalComplete);
            return (LogOperationEnvelope<T>)result;
        }
        catch (ContinuationException ex)
        {
            var code = ex.Code.Replace("_query_cursor", count == null ? "_search_cursor" : "_count_cursor", StringComparison.Ordinal);
            return Rejected<T>(requestId, [Error(code, "The query continuation is unavailable or does not match this request.", retryable: true)]);
        }
        catch (OperationCanceledException) when (count != null && cursor == null && !callerToken.IsCancellationRequested &&
            !_lifetimeCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            CountTimeWindowResolver.TryResolve(count, _now(), _localTimeZone, _limits, out var time, out _);
            var empty = new QueryTraversal { CountTime = time };
            return (LogOperationEnvelope<T>)(object)BuildResumableCount(requestId, "", empty,
                new TraversalPage(_limits.MaximumResponseCharacters), null, "time_slice", 0, true);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<T>(requestId, callerToken);
        }
        finally
        {
            if (gate)
                _heavyRequestGate.Release();
        }
    }

    private void EnsureTraversalCapacity(QueryTraversal state)
    {
        if (state.RetainedBytes > _limits.MaximumContinuationSessionBytes)
            throw new ContinuationException("continuation_capacity_exceeded");
    }

    private static bool NeedsContext(TraversalFile file, LogSearchQuery search, LogCountQuery? count)
        => count == null && search.ResultMode == "samples" && file.Pending.Count > 0 &&
           file.Pending[0].Hit != null && !file.Checkpoint.Done &&
           file.Pending.Count <= search.IncludeContextAfter;

    private static void FinishFile(TraversalFile file)
    {
        file.Done = true;
        file.Checkpoint.ReleaseBuffers();
        file.Pending.Clear();
        file.History.Clear();
    }

    private LogOperationEnvelope<LogSearchResult> BuildResumableSearch(string requestId, string revision,
        QueryTraversal state, TraversalPage page, LogSearchQuery query, string? nextCursor, string stopReason,
        long elapsed, bool deadline)
    {
        var provenanceBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters / 4);
        var contextBudget = new ResponseCharacterBudget(page.HitBudget.Remaining);
        var files = ImmutableArray.CreateBuilder<LogSearchFileResult>();
        var omitted = 0;
        foreach (var segment in page.Files.Values.OrderBy(static segment => segment.File.StableIndex))
        {
            var file = segment.File;
            var reasons = FileReasons(file);
            var provenance = RetainProvenance(file.File.Provenance, provenanceBudget);
            if (segment.Result.MatchingLineCount == 0 && file.Error == null && reasons.IsEmpty && file.Done && !provenance.IsTruncated)
            {
                omitted++;
                continue;
            }
            var contextOmitted = false;
            var orderedContext = BuildOrderedContextLineNumbers(segment.Result.Hits, query.IncludeContextBefore,
                query.IncludeContextAfter, checked((int)Math.Min(int.MaxValue, file.Checkpoint.LineNumber - 1)));
            foreach (var context in orderedContext.Select(number => segment.Context.GetValueOrDefault(number + 1L)).OfType<PendingScanLine>())
            {
                if (segment.Lines.ContainsKey(context.Number))
                    continue;
                if (contextBudget.IsExhausted)
                {
                    contextOmitted = true;
                    page.TruncationReasons.Add("response_text_limit");
                    break;
                }
                var text = TakeLogText(context.Text, contextBudget, out var truncated);
                segment.Lines[context.Number] = new LogSearchExcerptLine(context.Number, text, truncated || context.Truncated);
                if (truncated)
                    page.TruncationReasons.Add("response_text_limit");
                if (context.Truncated)
                    page.TruncationReasons.Add("line_character_limit");
            }
            if (provenance.IsTruncated)
                page.TruncationReasons.Add("provenance_metadata_limit");
            files.Add(new LogSearchFileResult(file.File.FileId, file.File.DisplayName, provenance.Items,
                query.ResultMode == "countsOnly" ? null : EncodingName(file.Checkpoint.Encoding),
                Generation(file), segment.Result.Hits.Select(static hit => new LogSearchHit(hit.LineNumber,
                    hit.MatchStart, hit.MatchLength)).ToImmutableArray(), MergeExcerpts(segment.Lines.Values),
                file.Error, provenance.IsTruncated || contextOmitted || segment.Lines.Values.Any(static line => line.IsTruncated))
            {
                MatchingLineCount = segment.Result.MatchingLineCount,
                MatchOccurrenceCount = segment.Result.MatchOccurrenceCount,
                IsCountExact = file.Done && reasons.IsEmpty,
                EvaluatedThroughLine = file.EvaluatedThroughLine,
                IncompleteReasons = reasons,
                ProvenanceTotalCount = provenance.TotalCount,
                IsProvenanceTruncated = provenance.IsTruncated
            });
        }
        var reasonsAll = TraversalReasons(state, nextCursor);
        var result = new LogSearchResult
        {
            ResultMode = query.ResultMode, Files = files.ToImmutable(), PageOmittedZeroHitFileCount = omitted,
            SelectedFileCount = state.SelectedFiles, SearchedFileCount = state.Files.Count(static file => file.Started),
            ReturnedHitCount = page.Hits, NextCursor = nextCursor,
            PageMatchingLineCount = page.Files.Values.Sum(static segment => segment.Result.MatchingLineCount),
            PageMatchOccurrenceCount = page.Files.Values.Sum(static segment => segment.Result.MatchOccurrenceCount),
            MatchingLineCount = state.Files.Sum(static file => file.Total.MatchingLineCount),
            MatchOccurrenceCount = state.Files.Sum(static file => file.Total.MatchOccurrenceCount),
            SkippedFileCount = state.Files.Count(static file => file.SelectionError),
            FailedFileCount = state.Files.Count(static file => file.Error != null && !file.SelectionError),
            MatchedFileCount = state.Files.Count(static file => file.Total.MatchingLineCount > 0),
            RemainingFileCount = RemainingFiles(state),
            IsPageComplete = page.Files.Values.All(static segment => segment.File.Error == null && segment.File.Checkpoint.Reasons.Count == 0),
            IsQueryComplete = nextCursor == null && reasonsAll.IsEmpty,
            IsTraversalComplete = nextCursor == null, StopReason = stopReason,
            IncompleteReasons = reasonsAll,
            PageIncompleteReasons = page.Files.Values.SelectMany(static segment => FileReasons(segment.File))
                .Distinct().Order(StringComparer.Ordinal).ToImmutableArray(),
            Statistics = PageStatistics(page, elapsed), EffectiveLimits = _limits
        };
        return Envelope(requestId, revision, !result.IsQueryComplete, page.TruncationReasons.Count > 0,
            page.TruncationReasons.Order(StringComparer.Ordinal).ToImmutableArray(), DeadlineErrors(deadline), result);
    }

    private LogOperationEnvelope<LogCountResult> BuildResumableCount(string requestId, string revision,
        QueryTraversal state, TraversalPage page, string? nextCursor, string stopReason, long elapsed, bool deadline)
    {
        var time = state.CountTime!;
        var accumulator = new CountAccumulator(time, evidence => _cursorCodec.GetGenerationIdentity(evidence));
        var entries = state.Files.Where(static file => file.Total.MatchingLineCount > 0 || file.Error != null ||
            file.Checkpoint.Reasons.Count > 0 || file.Started && !file.Done).OrderBy(static file => file.StableIndex).ToArray();
        var provenanceBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters / 4);
        var contentBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters * 3 / 4);
        if (time.ResolvedRange is { } range)
            contentBudget.Consume(range.Kind.Length + range.Start.Length + range.End.Length +
                range.TimeZoneId.Length + (range.RelativeWindow?.Length ?? 0));
        foreach (var file in state.Files)
            foreach (var (index, value) in file.Total.TimestampBucketCounts)
            {
                if (!accumulator.BucketCounts.TryGetValue(index, out var bucket))
                    accumulator.BucketCounts[index] = bucket = new SearchTimestampBucketCount();
                bucket.MatchingLineCount += value.MatchingLineCount;
                bucket.MatchOccurrenceCount += value.MatchOccurrenceCount;
            }
        var buckets = BuildCountBuckets(accumulator, contentBudget);
        var files = ImmutableArray.CreateBuilder<LogCountFileResult>();
        var truncated = false;
        foreach (var file in entries)
        {
            var reasons = FileReasons(file);
            var required = file.File.FileId.Length + file.File.DisplayName.Length + reasons.Sum(static reason => reason.Length) +
                EncodingName(file.Checkpoint.Encoding).Length + (file.Error?.Code.Length ?? 0) +
                (file.Error?.Message.Length ?? 0) + (Generation(file)?.Length ?? 0);
            if (required > contentBudget.Remaining)
            {
                truncated = true;
                break;
            }
            contentBudget.Consume(required);
            var provenance = RetainProvenance(file.File.Provenance, provenanceBudget);
            truncated |= provenance.IsTruncated;
            files.Add(new LogCountFileResult(file.File.FileId, file.File.DisplayName, provenance.Items,
                EncodingName(file.Checkpoint.Encoding), Generation(file), file.Error)
            {
                MatchingLineCount = file.Total.MatchingLineCount, MatchOccurrenceCount = file.Total.MatchOccurrenceCount,
                IsCountExact = file.Done && reasons.IsEmpty, IncompleteReasons = reasons,
                ProvenanceTotalCount = provenance.TotalCount, IsProvenanceTruncated = provenance.IsTruncated
            });
        }
        var reasonsAll = TraversalReasons(state, nextCursor);
        if (deadline && nextCursor == null)
            reasonsAll = reasonsAll.Add("deadline_exceeded");
        var traversalComplete = state.ResolverFinished && state.Files.All(static file => file.Done);
        var result = new LogCountResult
        {
            MatchingLineCount = state.Files.Sum(static file => file.Total.MatchingLineCount),
            MatchOccurrenceCount = state.Files.Sum(static file => file.Total.MatchOccurrenceCount),
            UnbucketedMatchingLineCount = state.Files.Sum(static file => file.Total.UnbucketedMatchingLineCount),
            UnbucketedMatchOccurrenceCount = state.Files.Sum(static file => file.Total.UnbucketedMatchOccurrenceCount),
            SelectedFileCount = state.SelectedFiles, SearchedFileCount = state.Files.Count(static file => file.Started),
            MatchedFileCount = state.Files.Count(static file => file.Total.MatchingLineCount > 0),
            SkippedFileCount = state.Files.Count(static file => file.SelectionError),
            FailedFileCount = state.Files.Count(static file => file.Error != null && !file.SelectionError),
            RemainingFileCount = RemainingFiles(state), IsComplete = traversalComplete && reasonsAll.IsEmpty,
            IsTraversalComplete = traversalComplete, StopReason = stopReason, NextCursor = nextCursor,
            IncompleteReasons = reasonsAll, ResolvedTimeRange = time.ResolvedRange, BucketSize = time.BucketSize,
            Buckets = buckets, Files = files.ToImmutable(),
            FileRecordTotalCount = entries.Length, ReturnedFileRecordCount = files.Count,
            IsFileRecordTruncated = truncated, Statistics = PageStatistics(page, elapsed), EffectiveLimits = _limits
        };
        return Envelope(requestId, revision, !result.IsComplete, truncated,
            truncated ? ["count_metadata_limit"] : [], DeadlineErrors(deadline), result);
    }

    private static int RemainingFiles(QueryTraversal state)
        => state.RemainingCandidates + state.Files.Count(static file => !file.Done);

    private static ImmutableArray<string> FileReasons(TraversalFile file)
    {
        var reasons = file.Checkpoint.Reasons.ToHashSet(StringComparer.Ordinal);
        if (!file.Done)
            reasons.Add("evaluation_incomplete");
        if (file.Total.UnbucketedMatchingLineCount > 0)
            reasons.Add("timestamp_bucket_unassigned");
        return reasons.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static ImmutableArray<string> TraversalReasons(QueryTraversal state, string? nextCursor)
    {
        var reasons = state.Files.SelectMany(static file => file.Checkpoint.Reasons).ToHashSet(StringComparer.Ordinal);
        if (state.Files.Any(static file => file.Total.UnbucketedMatchingLineCount > 0))
            reasons.Add("timestamp_bucket_unassigned");
        if (nextCursor != null)
            reasons.Add("unvisited_pages");
        return reasons.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private string? Generation(TraversalFile file) => _cursorCodec.GetGenerationIdentity(new FileScanGenerationEvidence(
        file.Checkpoint.Generation, file.Checkpoint.Reasons.Count == 0 ? FileGenerationCorrelation.Current : FileGenerationCorrelation.Unknown));

    private LogSearchStatistics PageStatistics(TraversalPage page, long elapsed) => new(
        page.Files.Values.Sum(static segment => segment.Bytes), elapsed,
        page.Files.Count, page.Files.Values.Count(static segment => segment.File.Done),
        page.Files.Values.Count(static segment => segment.File.SelectionError),
        _queryOperationMetrics.Value?.PeakDiskOperations ?? 0, _queryOperationMetrics.Value?.PeakUncOperations ?? 0);

    private static ImmutableArray<ConfiguredLogRequestError> DeadlineErrors(bool deadline) => deadline
        ? [Error("deadline_exceeded", "The deadline interrupted this slice; committed progress was retained.", retryable: true)] : [];

    private static ImmutableArray<LogSearchExcerpt> MergeExcerpts(IEnumerable<LogSearchExcerptLine> lines)
    {
        var excerpts = ImmutableArray.CreateBuilder<LogSearchExcerpt>();
        var current = ImmutableArray.CreateBuilder<LogSearchExcerptLine>();
        foreach (var line in lines.OrderBy(static line => line.LineNumber))
        {
            if (current.Count > 0 && current[^1].LineNumber + 1 != line.LineNumber)
            {
                excerpts.Add(new LogSearchExcerpt(current.ToImmutable()));
                current.Clear();
            }
            current.Add(line);
        }
        if (current.Count > 0)
            excerpts.Add(new LogSearchExcerpt(current.ToImmutable()));
        return excerpts.ToImmutable();
    }

    private sealed class QueryTraversal
    {
        public DateOnly ReferenceDate;
        public CountTimeResolution? CountTime;
        public ConfiguredLogSelectionContinuation? Resolver;
        public bool ResolverFinished;
        public int SelectedFiles;
        public int RemainingCandidates;
        public int NextFileIndex;
        public List<TraversalFile> Files = [];
        public QueryTraversal Clone() => new()
        {
            ReferenceDate = ReferenceDate, CountTime = CountTime, Resolver = Resolver,
            ResolverFinished = ResolverFinished, SelectedFiles = SelectedFiles, RemainingCandidates = RemainingCandidates,
            NextFileIndex = NextFileIndex,
            Files = Files.Select(static file => file.Clone()).ToList()
        };
        public long RetainedBytes => 1024 + Files.Sum(static file => file.RetainedBytes) +
            (Resolver?.SeenPhysicalPathIdentities.Length ?? 0) * 128L;
    }

    private sealed class TraversalFile(ResolvedConfiguredLogFile file, int stableIndex)
    {
        public ResolvedConfiguredLogFile File = file;
        public int StableIndex = stableIndex;
        public ResumableFileCheckpoint Checkpoint = new();
        public SearchResult Total = new();
        public bool Started;
        public bool Done;
        public bool SelectionError;
        public bool Reported;
        public ConfiguredLogRequestError? Error;
        public long? EvaluatedThroughLine;
        public List<PendingScanLine> Pending = [];
        public List<PendingScanLine> History = [];
        public TraversalFile Clone() => new(File, StableIndex)
        {
            Checkpoint = Checkpoint.Clone(), Started = Started, Done = Done, SelectionError = SelectionError, Reported = Reported,
            Error = Error, EvaluatedThroughLine = EvaluatedThroughLine, Pending = Pending.ToList(), History = History.ToList(),
            Total = new SearchResult
            {
                MatchingLineCount = Total.MatchingLineCount, MatchOccurrenceCount = Total.MatchOccurrenceCount,
                UnbucketedMatchingLineCount = Total.UnbucketedMatchingLineCount,
                UnbucketedMatchOccurrenceCount = Total.UnbucketedMatchOccurrenceCount,
                TimestampBucketCounts = Total.TimestampBucketCounts.ToDictionary(static pair => pair.Key,
                    static pair => new SearchTimestampBucketCount
                    {
                        MatchingLineCount = pair.Value.MatchingLineCount, MatchOccurrenceCount = pair.Value.MatchOccurrenceCount
                    })
            }
        };
        public long RetainedBytes => 1024 + Checkpoint.RetainedBytes + (File.PhysicalPath.Length + File.DisplayName.Length + File.FileId.Length) * 2L +
            File.EquivalentFileIds.Sum(static id => 32L + id.Length * 2L) +
            File.OrderedPathCandidates.Sum(static path => 32L + path.Length * 2L) +
            File.Provenance.Sum(static provenance => 128L + (provenance.RequestedTargetId.Length + provenance.TargetTreePath.Length +
                provenance.DashboardId.Length + provenance.DashboardTreePath.Length) * 2L) +
            Pending.Concat(History).Sum(static line => 256L + line.Text.Length * 2L + (line.Hit?.LineText.Length ?? 0) * 2L) +
            Total.TimestampBucketCounts.Count * 128L;
    }

    private sealed record PendingScanLine(long Number, string Text, SearchHit? Hit, int Occurrences,
        ParsedTimestamp? Timestamp, int Bytes, bool Truncated);

    private sealed class TraversalSegment(TraversalFile file)
    {
        public TraversalFile File { get; } = file;
        public SearchResult Result { get; } = new();
        public long Bytes;
        public Dictionary<long, LogSearchExcerptLine> Lines { get; } = [];
        public Dictionary<long, PendingScanLine> Context { get; } = [];
    }

    private sealed class TraversalPage(int maximumCharacters)
    {
        public int Hits;
        public int ResolvedPages;
        public ResponseCharacterBudget HitBudget { get; set; } = new(maximumCharacters);
        public Dictionary<TraversalFile, TraversalSegment> Files { get; } = [];
        public HashSet<string> TruncationReasons { get; } = new(StringComparer.Ordinal);
        public TraversalSegment Observe(TraversalFile file)
        {
            if (!Files.TryGetValue(file, out var segment))
                Files.Add(file, segment = new TraversalSegment(file));
            return segment;
        }
    }

    private sealed class ActiveScanReader(ResumableScanReader reader) : IAsyncDisposable
    {
        public ResumableScanReader Reader { get; } = reader;
        public ValueTask DisposeAsync() => Reader.DisposeAsync();
    }
}
