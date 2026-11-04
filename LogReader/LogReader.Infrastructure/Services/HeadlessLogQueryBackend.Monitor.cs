namespace LogReader.Infrastructure.Services;

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackend
{
    internal const int MaximumMonitorFiles = LogMonitorLimits.MaximumFiles;
    private const int MonitorBoundaryBytes = LogMonitorLimits.BoundaryProbeBytes;
    private const long MonitorPollBytes = LogMonitorLimits.PollScanBytes;
    private const long MonitorFileBytes = LogMonitorLimits.FileScanBytes;

    public async Task<LogOperationEnvelope<LogMonitorResult>> MonitorLogsAsync(
        LogMonitorQuery query, CancellationToken ct = default)
    {
        using var requestLease = BeginRequest();
        ArgumentNullException.ThrowIfNull(query);
        var requestId = CreateRequestId();
        var validation = ValidateMonitor(query);
        if (!validation.IsEmpty)
            return Rejected<LogMonitorResult>(requestId, validation);
        using var deadline = CreateDeadlineScope(query.TimeoutMilliseconds, ct);
        var gate = false;
        try
        {
            await _heavyRequestGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            gate = true;
            var catalog = await _catalogReader.ReadAsync(deadline.Token).ConfigureAwait(false);
            if (!catalog.IsSuccess)
                return Failure<LogMonitorResult>(requestId, catalog.Error!);
            using var transaction = await Continuations.AcquireAsync(query.Cursor, "monitor", "monitor-v1",
                catalog.Snapshot!.Revision, deadline.Token).ConfigureAwait(false);
            if (transaction.IsReplay)
                return (LogOperationEnvelope<LogMonitorResult>)transaction.ReplayResult!;

            var initial = transaction.State == null;
            var state = transaction.State is MonitorScope previous ? previous.Clone() : new MonitorScope();
            var files = ImmutableArray.CreateBuilder<LogMonitorFileResult>();
            var textBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters * 3 / 4);
            var provenanceBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters / 4);
            var bytes = 0L;
            var examined = 0;
            var missing = 0L;
            var outside = 0L;
            var checkedFiles = 0;
            var scopeChecked = false;
            var idle = false;
            if (initial && query.HandoffCursor != null)
                state = await CreateHandoffScopeAsync(query.HandoffCursor, catalog.Snapshot.Revision, deadline.Token).ConfigureAwait(false);
            if (initial && query.ObserveForMilliseconds.HasValue)
                state.ObservationEndsAt = _now().AddMilliseconds(query.ObserveForMilliseconds.Value);
            var periodEnded = state.ObservationEndsAt.HasValue && _now() >= state.ObservationEndsAt;
            if (initial && query.HandoffCursor == null)
            {
                var capturedNow = _now();
                if (!CountTimeWindowResolver.TryResolve(new LogCountQuery
                    { StartTimestamp = query.StartTimestamp, EndTimestamp = query.EndTimestamp, RelativeWindow = query.RelativeWindow },
                    capturedNow, _localTimeZone, _limits, out var time, out var error))
                    return Rejected<LogMonitorResult>(requestId, [error!]);
                state.Time = time!;
                state.ReferenceDate = query.RelativeWindow == null ? _today() :
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(capturedNow, _localTimeZone).DateTime);
                state.DateOffsetDays = query.DateOffsetDays;
                if (state.DateOffsetDays > state.ReferenceDate.DayNumber)
                    return Rejected<LogMonitorResult>(requestId,
                        [Error("invalid_date_offset", "dateOffsetDays must select a representable date on or before the captured server date.")]);
                state.Request = SearchRequest.Create(query.Query ?? "", query.UseRegex, query.CaseSensitive, [],
                    SearchRequestSourceMode.Tail, SearchRequestUsage.DiskSearch, time!.StartTimestamp, time.EndTimestamp,
                    maxRetainedLineTextLength: _limits.MaximumCharactersPerLine);
                state.Request.ExcludeQuery = query.ExcludeQuery;
                ConfiguredLogSelectionContinuation? selectionCursor = null;
                var byPath = new Dictionary<string, MonitorFile>(StringComparer.OrdinalIgnoreCase);
                do
                {
                    var selection = await ResolveAsync(catalog.Snapshot, query.Targets, query.DateOffsetDays,
                        state.ReferenceDate, _limits.MaximumFiles, selectionCursor, deadline.Token).ConfigureAwait(false);
                    if (!selection.IsSuccess)
                        return SelectionFailure<LogMonitorResult>(requestId, selection);
                    foreach (var file in selection.Files)
                    {
                        if (byPath.TryGetValue(file.PhysicalPath, out var existing))
                        {
                            existing.File = existing.File with
                            {
                                EquivalentFileIds = existing.File.EquivalentFileIds.Union(file.EquivalentFileIds, StringComparer.Ordinal).ToImmutableArray(),
                                Provenance = existing.File.Provenance.Union(file.Provenance).ToImmutableArray(),
                                OrderedPathCandidates = existing.File.OrderedPathCandidates.Union(file.OrderedPathCandidates, StringComparer.OrdinalIgnoreCase).ToImmutableArray()
                            };
                        }
                        else
                        {
                            var tracker = new MonitorFile(file) { StableIndex = selection.StableFileIndexesById[file.FileId] };
                            byPath.Add(file.PhysicalPath, tracker);
                            state.Files.Add(tracker);
                        }
                    }
                    foreach (var file in selection.FileErrors)
                        state.Files.Add(new MonitorFile(new ResolvedConfiguredLogFile(file.FileId, file.DisplayName,
                            "", [], [], file.Provenance))
                        {
                            StableIndex = selection.StableFileIndexesById[file.FileId], Stopped = true,
                            Error = Error(file.Code, file.Message, targetId: file.FileId)
                        });
                    if (state.Files.Count > MaximumMonitorFiles)
                        return Rejected<LogMonitorResult>(requestId,
                            [Error("monitor_scope_limit", $"Select at most {MaximumMonitorFiles} physical files for one monitor (unresolved entries also consume slots).")]);
                    if (state.RetainedBytes > _limits.MaximumContinuationSessionBytes)
                        throw new ContinuationException("continuation_capacity_exceeded", "query_too_large");
                    // Revisit later aliases so their authorization routes merge into the retained
                    // physical identity. The resolver's candidate ceiling still bounds all work.
                    selectionCursor = selection.Continuation is { } next
                        ? next with { SeenPhysicalPathIdentities = [] } : null;
                } while (selectionCursor != null);
                state.Files.Sort((left, right) => left.StableIndex.CompareTo(right.StableIndex));
                foreach (var tracker in state.Files)
                {
                    if (tracker.Stopped)
                    {
                        files.Add(MonitorFileResult(tracker, [], provenanceBudget));
                        continue;
                    }
                    var file = tracker.File;
                    try
                    {
                        await ExecuteDiskOperationAsync(file.PhysicalPath, async token =>
                        {
                            await CaptureMonitorBaselineAsync(tracker, token).ConfigureAwait(false);
                            return true;
                        }, deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsPerFileException(ex))
                    {
                        tracker.Stopped = true;
                        tracker.Error = MonitorError(ex, file.FileId) with
                        {
                            Message = "Observation could not start for this file because a safe initial boundary was unavailable. Other admitted files may continue. A new observation requires explicit intent.",
                            IsRetryable = false
                        };
                    }
                    files.Add(MonitorFileResult(tracker, [], provenanceBudget));
                }
                // No cursor is committed until every admitted file has a baseline or an explicit stopped error.
                state.SweepRemaining = state.Files.Count;
            }
            else if (!query.Release && !periodEnded)
            {
                if (state.SweepRemaining == 0)
                {
                    state.SweepRemaining = state.Files.Count;
                    state.SweepActive = false;
                }
                var budget = new ScanWorkBudget(Math.Max(1, (_limits.SearchWorkMilliseconds)),
                    Math.Min(_limits.SearchScanBytes, MonitorPollBytes), _scanTimestamp);
                var regex = _scanner.PrepareRegex(state.Request);
                var maximumLines = query.MaxLines ?? _limits.DefaultReadLineCount;
                var pollFiles = Math.Min(query.MaxFiles, state.SweepRemaining);
                for (var index = 0; index < pollFiles && !budget.IsStopped && examined < maximumLines &&
                    (!state.ObservationEndsAt.HasValue || _now() < state.ObservationEndsAt) &&
                    !textBudget.IsExhausted; index++)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var slot = state.NextFile;
                    var tracker = state.Files[slot].Clone();
                    var events = ImmutableArray.CreateBuilder<LogMonitorEvent>();
                    if (!tracker.Stopped)
                    {
                        using var fileDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        fileDeadline.CancelAfter(Math.Max(1, (query.TimeoutMilliseconds ?? _limits.DefaultTimeoutMilliseconds) /
                            Math.Max(1, pollFiles + 1)));
                        var before = tracker.Checkpoint.Clone();
                        try
                        {
                            await ExecuteDiskOperationAsync(tracker.File.PhysicalPath, async token =>
                            {
                                await using var reader = await _scanner.OpenAsync(tracker.File.PhysicalPath,
                                    tracker.Checkpoint, token).ConfigureAwait(false);
                                var checkpoint = tracker.Checkpoint;
                                var encoding = _encodingDetection.ResolveEncodingDecision(tracker.File.PhysicalPath, FileEncoding.Auto);
                                var incompleteUtf8 = checkpoint.Encoding == FileEncoding.Utf8 &&
                                    encoding.ResolvedEncoding == FileEncoding.Ansi && encoding.HasIncompleteTrailingUtf8Sequence;
                                if (encoding.ResolvedEncoding != checkpoint.Encoding && !incompleteUtf8)
                                    throw new ScanFileException("log_encoding_changed");
                                var nextExtent = checkpoint.Encoding is FileEncoding.Utf16 or FileEncoding.Utf16Be
                                    ? checkpoint.ObservedLength & ~1L : checkpoint.ObservedLength;
                                var grew = nextExtent > checkpoint.Extent;
                                // Finish captured work before admitting growth, unless an incomplete UTF-8 character needs more bytes.
                                if (grew && (checkpoint.Done || checkpoint.AwaitingUtf8Continuation))
                                {
                                    if (checkpoint.Done)
                                    {
                                        if (checkpoint.LastLineUnterminated)
                                        {
                                            checkpoint.ReadOffset = checkpoint.LastLineStartOffset;
                                            checkpoint.LineNumber = Math.Max(1, checkpoint.LineNumber - 1);
                                        }
                                        checkpoint.SkipLeadingLf = checkpoint.LastLineEndedCr;
                                    }
                                    checkpoint.Done = false;
                                    checkpoint.AwaitingUtf8Continuation = false;
                                    checkpoint.Extent = nextExtent;
                                }
                                var fileBudget = new ScanWorkBudget(_limits.SearchWorkMilliseconds,
                                    Math.Min(MonitorFileBytes, budget.RemainingBytes), _scanTimestamp);
                                try
                                {
                                    var lines = 0;
                                    while (!checkpoint.Done && !budget.IsStopped && !fileBudget.IsStopped &&
                                        (!state.ObservationEndsAt.HasValue || _now() < state.ObservationEndsAt) &&
                                        examined + lines < maximumLines && lines < _limits.DefaultReadLineCount &&
                                        !textBudget.IsExhausted)
                                    {
                                        var line = await reader.ReadNextAsync(state.Request, regex, fileBudget,
                                            _limits.MaximumSearchLineBytes, token).ConfigureAwait(false);
                                        if (line == null)
                                            break;
                                        lines++;
                                        var offset = checkpoint.LastLineStartOffset;
                                        var update = tracker.PublishedMatched && tracker.PublishedOffset == offset;
                                        if (line.Occurrences > 0)
                                        {
                                            var retained = TakeLogText(line.Hit?.LineText ?? line.Text, textBudget, out var truncated);
                                            events.Add(new LogMonitorEvent(update ? "updated" : "appended", offset,
                                                retained, truncated || line.Hit?.LineTextTruncated == true));
                                        }
                                        else if (update)
                                            events.Add(new LogMonitorEvent("removed", offset, null, false));
                                        tracker.PublishedOffset = checkpoint.LastLineUnterminated ? offset : null;
                                        tracker.PublishedMatched = checkpoint.LastLineUnterminated && line.Occurrences > 0;
                                    }
                                }
                                finally { budget.AddBytes(checked((int)fileBudget.Bytes)); }
                                reader.ValidateAfterRead();
                                checkpoint.Reasons.Clear(); // Growth is expected observation, not snapshot drift.
                                return true;
                            }, fileDeadline.Token).ConfigureAwait(false);
                            tracker.Error = null;
                        }
                        catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                        {
                            tracker = state.Files[slot].Clone();
                            tracker.Error = Error("monitor_file_timeout", "This file exceeded its poll slice; other files may advance. Retry with the returned scope cursor.",
                                retryable: true, targetId: tracker.File.FileId);
                            events.Clear();
                        }
                        catch (Exception ex) when (IsPerFileException(ex) || ex is RegexMatchTimeoutException)
                        {
                            tracker = state.Files[slot].Clone();
                            tracker.Error = MonitorError(ex, tracker.File.FileId);
                            tracker.Stopped = ex is ScanFileException or RegexMatchTimeoutException;
                            events.Clear();
                        }
                        examined += checked((int)(tracker.Checkpoint.EvaluatedLines - before.EvaluatedLines));
                        missing += tracker.Checkpoint.MissingTimestamps - before.MissingTimestamps;
                        outside += tracker.Checkpoint.OutsideTimeRange - before.OutsideTimeRange;
                        state.SweepActive |= tracker.Checkpoint.ReadOffset != before.ReadOffset || events.Count > 0 || tracker.Error != null;
                    }
                    state.Files[slot] = tracker;
                    files.Add(MonitorFileResult(tracker, events.ToImmutable(), provenanceBudget));
                    state.NextFile = (slot + 1) % state.Files.Count;
                    state.SweepRemaining--;
                    checkedFiles++;
                }
                bytes = budget.Bytes;
                scopeChecked = state.SweepRemaining == 0;
                idle = scopeChecked && !state.SweepActive;
            }
            deadline.Token.ThrowIfCancellationRequested();
            periodEnded |= state.ObservationEndsAt.HasValue && _now() >= state.ObservationEndsAt;
            var stopped = state.Files.Count(file => file.Stopped);
            var failed = state.Files.Any(file => file.Error != null);
            var backlog = state.Files.Count(MonitorHasBacklog);
            var terminal = query.Release || periodEnded || stopped == state.Files.Count;
            var result = new LogMonitorResult
            {
                State = query.Release ? "released" : periodEnded ? "period_ended" : terminal ? "stopped" : initial ? "initialized" : "observing",
                ObservationEndsAt = state.ObservationEndsAt?.ToString("O", CultureInfo.InvariantCulture),
                SelectedDate = state.ReferenceDate.AddDays(-state.DateOffsetDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                SelectedFileCount = state.Files.Count, CheckedFileCount = checkedFiles,
                UncheckedFileCount = state.SweepRemaining, BacklogFileCount = backlog, StoppedFileCount = stopped,
                IsScopeChecked = scopeChecked, IsCaughtUp = !terminal && scopeChecked && backlog == 0 && !failed,
                IsIdle = !terminal && idle && backlog == 0 && !failed,
                NextCursor = terminal ? null : transaction.NextCursor, Files = files.ToImmutable(),
                BytesEvaluated = bytes, ExaminedLineCount = examined,
                TimestampCoverage = new(examined, missing, outside, state.Request.FromTimestamp != null || state.Request.ToTimestamp != null), ResolvedTimeRange = state.Time?.ResolvedRange
            };
            var truncated = files.Any(file => file.IsProvenanceTruncated || file.Events.Any(item => item.IsTruncated));
            var response = Envelope(requestId, catalog.Snapshot.Revision, failed || stopped > 0, truncated,
                truncated ? ["monitor_output_limit"] : [], [], result);
            if (terminal)
                state.Files.Clear();
            transaction.Commit(state, response, state.RetainedBytes + JsonSerializer.SerializeToUtf8Bytes(response).LongLength * 3 + 4096, terminal);
            return response;
        }
        catch (ContinuationException ex)
        {
            return Rejected<LogMonitorResult>(requestId, [ContinuationError(ex, "monitor")]);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<LogMonitorResult>(requestId, ct);
        }
        finally
        {
            if (gate)
                _heavyRequestGate.Release();
        }
    }

    private async Task<MonitorScope> CreateHandoffScopeAsync(string cursor, string revision, CancellationToken ct)
    {
        using var source = await Continuations.AcquireSnapshotAsync(cursor, revision, ct).ConfigureAwait(false);
        if (source.State is not QueryTraversal snapshot || !snapshot.ResolverFinished || snapshot.Predicate == null ||
            snapshot.Files.Count > MaximumMonitorFiles || snapshot.Files.Any(file => !file.Done || file.Pending.Count != 0 || file.Error != null ||
                !file.Checkpoint.Initialized || !file.Checkpoint.Done || !file.Checkpoint.Generation.IsKnown ||
                file.Checkpoint.Reasons.Any(reason => reason != ResumableLogScanner.FileGrewDuringScan)))
            throw new ContinuationException("handoff_boundary_unavailable");
        var predicate = snapshot.Predicate.Clone();
        predicate.MaxHitsPerFile = null;
        predicate.TimestampAggregation = null;
        var state = new MonitorScope
        {
            ReferenceDate = snapshot.ReferenceDate, DateOffsetDays = snapshot.DateOffsetDays,
            Request = predicate, Time = snapshot.CountTime, SweepRemaining = snapshot.Files.Count
        };
        foreach (var file in snapshot.Files)
        {
            var checkpoint = file.Checkpoint.Clone();
            checkpoint.IsLiveObservation = true;
            checkpoint.ReleaseBuffers();
            checkpoint.Reasons.Clear();
            state.Files.Add(new MonitorFile(file.File)
            {
                Checkpoint = checkpoint,
                PublishedOffset = checkpoint.LastLineUnterminated ? checkpoint.LastLineStartOffset : null,
                PublishedMatched = checkpoint.LastLineUnterminated && checkpoint.LastLineMatched
            });
        }
        return state;
    }

    private async Task CaptureMonitorBaselineAsync(MonitorFile file, CancellationToken ct)
    {
        var checkpoint = file.Checkpoint;
        await using (var reader = await _scanner.OpenAsync(file.File.PhysicalPath, checkpoint, ct).ConfigureAwait(false))
            reader.ValidateAfterRead();
        if (!checkpoint.Generation.IsKnown)
            throw new ScanFileException("log_generation_unverified");
        await using var stream = new FileStream(file.File.PhysicalPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (FileGenerationTokenProvider.Capture(stream) != checkpoint.Generation || stream.Length < checkpoint.Extent)
            throw new ScanFileException("log_generation_changed");
        var contentStart = checkpoint.ReadOffset; // Actual BOM evidence from the scanner, including BOM-less UTF-16.
        var length = checkpoint.Extent;
        var count = (int)Math.Min(length, MonitorBoundaryBytes);
        var probe = new byte[count];
        stream.Position = length - count;
        await stream.ReadExactlyAsync(probe, ct).ConfigureAwait(false);
        var unit = checkpoint.Encoding is FileEncoding.Utf16 or FileEncoding.Utf16Be ? 2 : 1;
        var start = length;
        var terminated = length == 0;
        for (var offset = count - unit; offset >= 0; offset -= unit)
        {
            var value = unit == 1 ? probe[offset] : checkpoint.Encoding == FileEncoding.Utf16
                ? probe[offset] | probe[offset + 1] << 8 : probe[offset] << 8 | probe[offset + 1];
            if (value is '\r' or '\n')
            {
                start = length - count + offset + unit;
                terminated = offset == count - unit;
                break;
            }
            start = length - count + offset;
        }
        if (start == length - count && start > contentStart && !terminated)
            throw new ScanFileException("monitor_boundary_unavailable");
        checkpoint.ReadOffset = length;
        checkpoint.LastLineStartOffset = Math.Max(start, contentStart);
        checkpoint.LastLineUnterminated = !terminated;
        checkpoint.LastLineEndedCr = count >= unit && (unit == 1 ? probe[^1] == 13 :
            checkpoint.Encoding == FileEncoding.Utf16 ? probe[^2] == 13 && probe[^1] == 0 : probe[^2] == 0 && probe[^1] == 13);
        checkpoint.Done = true;
        checkpoint.ReleaseBuffers();
    }

    private ImmutableArray<ConfiguredLogRequestError> ValidateMonitor(LogMonitorQuery query)
    {
        var errors = ImmutableArray.CreateBuilder<ConfiguredLogRequestError>();
        if (query.Release && query.Cursor == null)
            errors.Add(Error("monitor_cursor_required", "release requires the current monitor cursor."));
        if (query.MaxFiles is < 1 or > MaximumMonitorFiles)
            errors.Add(Error("invalid_file_limit", $"maxFiles must be between 1 and {MaximumMonitorFiles}."));
        ValidateLowerLimit(query.MaxLines, _limits.MaximumReadLineCount, "maxLines", "invalid_line_count", errors);
        ValidateTimeout(query.TimeoutMilliseconds, errors);
        if (query.ObserveForMilliseconds is < 1 or > 86_400_000)
            errors.Add(Error("invalid_observation_period", "observeForMilliseconds must be between 1 and 86400000."));
        if (query.Cursor != null && (query.HandoffCursor != null || query.ObserveForMilliseconds != null))
            errors.Add(Error("monitor_options_bound", "A poll cursor cannot be combined with handoff or a new period."));
        if (query.Cursor != null || query.HandoffCursor != null)
        {
            if (query.Targets is { Count: > 0 } || query.Query != null || query.ExcludeQuery != null || query.UseRegex ||
                query.CaseSensitive || query.DateOffsetDays != 0 || query.StartTimestamp != null || query.EndTimestamp != null || query.RelativeWindow != null)
                errors.Add(Error("monitor_options_bound", "A monitor cursor already binds selection and predicates. Poll using cursor and budget controls only."));
        }
        else
        {
            if (query.Targets == null || query.Targets.Count == 0 || query.Targets.Count > _limits.MaximumTargets)
                errors.Add(Error("invalid_targets", "Supply configured targets within the server target limit."));
            if (query.DateOffsetDays < 0)
                errors.Add(Error("invalid_date_offset", "dateOffsetDays cannot be negative."));
            if (query.Query == null && query.ExcludeQuery == null && (query.UseRegex || query.CaseSensitive))
                errors.Add(Error("invalid_monitor_filter", "Regex/case options require an inclusion or exclusion query."));
            ValidateExclusion(query.Query, query.UseRegex, query.CaseSensitive, errors, "query");
            ValidateExclusion(query.ExcludeQuery, query.UseRegex, query.CaseSensitive, errors);
            if (query.StartTimestamp is { Length: > ConfiguredLogLimits.DefaultMaxTimestampCharacters } ||
                query.EndTimestamp is { Length: > ConfiguredLogLimits.DefaultMaxTimestampCharacters } ||
                query.RelativeWindow is { Length: > ConfiguredLogLimits.DefaultMaxTimestampCharacters })
                errors.Add(Error("timestamp_too_long", "Time criteria exceed the supported character limit."));
        }
        return errors.ToImmutable();
    }

    private static ConfiguredLogRequestError MonitorError(Exception ex, string fileId) => Error(
        ex is ScanFileException scan ? scan.Code : ex is RegexMatchTimeoutException ? "regex_match_timeout" : "monitor_file_unavailable",
        ex is RegexMatchTimeoutException
            ? "The observation filter exceeded its regex match timeout. Report incomplete observation; changing the filter requires an explicitly requested new observation."
            : ex is ScanFileException
            ? "Observation stopped for this file because its content, encoding, or safe boundary cannot be continued. Report the gap; starting another observation requires explicit intent."
            : "This file is unavailable. Its boundary was retained; healthy files can advance on the next bounded poll.",
        retryable: ex is not (ScanFileException or RegexMatchTimeoutException), targetId: fileId);

    private static bool MonitorHasBacklog(MonitorFile file)
        => !file.Stopped && (!file.Checkpoint.Done || file.Checkpoint.ObservedLength > file.Checkpoint.Extent);

    private LogMonitorFileResult MonitorFileResult(MonitorFile file, ImmutableArray<LogMonitorEvent> events,
        ResponseCharacterBudget budget)
    {
        var provenance = RetainProvenance(file.File.Provenance, budget);
        return new(file.File.FileId, file.File.DisplayName, provenance.Items,
            file.Checkpoint.Generation.IsKnown
                ? _cursorCodec.GetGenerationIdentity(new FileScanGenerationEvidence(file.Checkpoint.Generation, FileGenerationCorrelation.Current)) : null,
            file.Stopped ? "stopped" : file.Error != null ? "unavailable" : MonitorHasBacklog(file) ? "backlog" : "caught_up",
            Math.Max(0, Math.Max(file.Checkpoint.Extent, file.Checkpoint.ObservedLength) - file.Checkpoint.ReadOffset), events, file.Error)
        { ProvenanceTotalCount = provenance.TotalCount, IsProvenanceTruncated = provenance.IsTruncated };
    }

    private sealed class MonitorFile(ResolvedConfiguredLogFile file)
    {
        public int StableIndex;
        public ResolvedConfiguredLogFile File = file;
        public ResumableFileCheckpoint Checkpoint = new() { IsLiveObservation = true };
        public bool Stopped;
        public ConfiguredLogRequestError? Error;
        public long? PublishedOffset;
        public bool PublishedMatched;
        public MonitorFile Clone() => new(File)
        { StableIndex = StableIndex, Checkpoint = Checkpoint.Clone(), Stopped = Stopped, Error = Error, PublishedOffset = PublishedOffset, PublishedMatched = PublishedMatched };
        public long RetainedBytes => new TraversalFile(File, 0).RetainedBytes + Checkpoint.RetainedBytes;
    }

    private sealed class MonitorScope
    {
        public DateTimeOffset? ObservationEndsAt;
        public DateOnly ReferenceDate;
        public int DateOffsetDays;
        public CountTimeResolution? Time;
        public SearchRequest Request = new();
        public List<MonitorFile> Files = [];
        public int NextFile;
        public int SweepRemaining;
        public bool SweepActive;
        public MonitorScope Clone() => new()
        { ObservationEndsAt = ObservationEndsAt, ReferenceDate = ReferenceDate, DateOffsetDays = DateOffsetDays, Time = Time, Request = Request,
            Files = Files.Select(file => file.Clone()).ToList(), NextFile = NextFile,
            SweepRemaining = SweepRemaining, SweepActive = SweepActive };
        public long RetainedBytes => 4096 + (Request.Query.Length + (Request.ExcludeQuery?.Length ?? 0) +
            (Request.FromTimestamp?.Length ?? 0) + (Request.ToTimestamp?.Length ?? 0)) * 2L + Files.Sum(file => file.RetainedBytes);
    }
}
