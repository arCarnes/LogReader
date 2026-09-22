namespace LogReader.Infrastructure.Services;

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackend
{
    private async Task<LogOperationEnvelope<LogReadTailResult>> ReadFilteredLogTailAsync(
        LogReadTailQuery request,
        int maxLines,
        TailCursorPayload? cursor,
        string requestId,
        CancellationToken ct)
    {
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
                    catalogRead.Snapshot!, request.FileId, request.DateOffsetDays, scope.Token).ConfigureAwait(false);
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
                    return Rejected<LogReadTailResult>(requestId,
                        [Error("invalid_tail_cursor", "The tail cursor does not belong to the selected configured log file.")],
                        selection.CatalogRevision);
                }

                var provenanceBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters / 4);
                var retainedProvenance = RetainProvenance(file.Provenance, provenanceBudget);
                var responseBudget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters - provenanceBudget.Consumed);
                LogReadTailResult result;
                try
                {
                    result = await ExecuteDiskOperationAsync(
                        file.PhysicalPath,
                        async token =>
                        {
                            var regex = request.UseRegex ? RegexPatternFactory.Create(request.Query!, request.CaseSensitive) : null;
                            var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
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
                                var previousTailLineExtended = cursor != null && !generationChanged &&
                                    cursor.LastLineNumber > 0 && cursor.FileSize < metadata.FileSize &&
                                    metadata.TryGetLineBounds(cursor.LastLineNumber - 1, out var previousBounds) &&
                                    previousBounds!.EndOffset > cursor.FileSize &&
                                    await AppendStartsWithLineContentAsync(
                                        file.PhysicalPath, cursor.FileSize, metadata.FileSize, metadata.Encoding, token)
                                        .ConfigureAwait(false);
                                var startIndex = cursor == null || generationChanged
                                    ? Math.Max(0, metadata.TotalLineCount - maxLines)
                                    : previousTailLineExtended
                                        ? cursor.LastLineNumber - 1
                                        : Math.Min(cursor.LastLineNumber, metadata.TotalLineCount);
                                var snapshot = await lease.CaptureCurrentIndexAsync(
                                    [new IndexedLogReadRange(startIndex, maxLines)], token).ConfigureAwait(false);
                                if (!metadata.HasSameSourceAs(snapshot))
                                {
                                    if (attempt == 0)
                                        continue;
                                    throw new IOException("The log index changed while preparing the tail read.");
                                }

                                var mapped = ImmutableArray.CreateBuilder<LogLineResult>();
                                var lastLineNumber = startIndex;
                                var lastLineMatched = cursor != null && !generationChanged && cursor.LastLineNumber == startIndex
                                    && cursor.LastLineMatched;
                                var examined = 0;
                                var skipped = 0;
                                int? removedLineNumber = null;
                                var lastLineUpdated = false;
                                var stoppedBeforeUpdatedLine = false;
                                var endExclusive = Math.Min(snapshot.TotalLineCount, startIndex + maxLines);
                                for (var batchStart = startIndex; batchStart < endExclusive; batchStart += 32)
                                {
                                    var count = Math.Min(32, endExclusive - batchStart);
                                    var lines = await _logReader.ReadFullIndexedLinesAsync(
                                        file.PhysicalPath, snapshot, batchStart, count, token).ConfigureAwait(false);
                                    if (lines.Count != count ||
                                        !await lease.RevalidateCurrentIndexAsync(snapshot, token).ConfigureAwait(false))
                                        throw new IOException("The log index changed during the filtered tail read.");

                                    var stop = false;
                                    foreach (var line in lines)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        var isUpdatedLine = previousTailLineExtended && line.LineNumber == cursor!.LastLineNumber - 1;
                                        var match = regex?.Match(line.Text);
                                        var matchStart = match == null
                                            ? line.Text.IndexOf(request.Query!, comparison)
                                            : match.Success ? match.Index : -1;
                                        var matchLength = match?.Length ?? request.Query!.Length;
                                        if (matchStart >= 0 && responseBudget.Remaining == 0 && line.Text.Length > 0)
                                        {
                                            stoppedBeforeUpdatedLine = isUpdatedLine;
                                            stop = true;
                                            break;
                                        }

                                        examined++;
                                        lastLineNumber = line.LineNumber + 1;
                                        lastLineMatched = matchStart >= 0;
                                        if (isUpdatedLine)
                                        {
                                            lastLineUpdated = true;
                                            if (cursor!.LastLineMatched && !lastLineMatched)
                                                removedLineNumber = lastLineNumber;
                                        }
                                        if (!lastLineMatched)
                                        {
                                            skipped++;
                                            continue;
                                        }

                                        mapped.Add(RetainFilteredTailLine(
                                            line.LineNumber + 1, line.Text, matchStart, matchLength, responseBudget));
                                    }
                                    if (stop)
                                        break;
                                }

                                var lastOffset = TryGetLineStartOffset(snapshot, lastLineNumber, out var offset)
                                    ? offset
                                    : TryGetLineStartOffset(metadata, lastLineNumber, out offset) ? offset : 0;
                                var nextCursor = _cursorCodec.Encode(new TailCursorPayload(
                                    Version: 2,
                                    file.FileId,
                                    pathIdentity,
                                    snapshot.Encoding,
                                    generation,
                                    lastLineNumber,
                                    lastOffset,
                                    stoppedBeforeUpdatedLine ? cursor!.FileSize : snapshot.FileSize,
                                    _cursorCodec.GetFilterIdentity(request.Query!, request.UseRegex, request.CaseSensitive),
                                    lastLineMatched));
                                return new LogReadTailResult
                                {
                                    File = new LogReadFileResult(
                                        file.FileId, file.DisplayName, retainedProvenance.Items,
                                        EncodingName(snapshot.Encoding), generation, mapped.ToImmutable(), Error: null)
                                    {
                                        ProvenanceTotalCount = retainedProvenance.TotalCount,
                                        IsProvenanceTruncated = retainedProvenance.IsTruncated
                                    },
                                    NextCursor = nextCursor,
                                    GenerationChanged = generationChanged,
                                    LastLineUpdated = lastLineUpdated,
                                    TotalLineCount = snapshot.TotalLineCount,
                                    ExaminedLineCount = examined,
                                    SkippedLineCount = skipped,
                                    RemainingLineCount = snapshot.TotalLineCount - lastLineNumber,
                                    RemovedLineNumber = removedLineNumber
                                };
                            }

                            throw new IOException("The log index changed while preparing the tail read.");
                        }, scope.Token).ConfigureAwait(false);
                }
                catch (InvalidTailCursorException)
                {
                    return Rejected<LogReadTailResult>(requestId,
                        [Error("invalid_tail_cursor", "The tail cursor encoding no longer matches the configured log file.")],
                        selection.CatalogRevision);
                }
                catch (RegexMatchTimeoutException)
                {
                    return Rejected<LogReadTailResult>(requestId,
                        [Error("regex_match_timeout", "The tail regular expression timed out while matching a log line.", retryable: true)],
                        selection.CatalogRevision);
                }
                catch (Exception ex) when (IsPerFileException(ex))
                {
                    result = new LogReadTailResult { File = FailedReadFile(file, retainedProvenance, ex) };
                }

                var truncated = retainedProvenance.IsTruncated || responseBudget.IsExhausted ||
                                result.File?.Lines.Any(static line => line.IsTruncated) == true;
                return Envelope(requestId, selection.CatalogRevision,
                    isPartial: result.File?.Error != null,
                    truncated,
                    GetLineTruncationReasons(result.File?.Lines ?? [], responseBudget, retainedProvenance.IsTruncated),
                    errors: [], result);
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

    private LogLineResult RetainFilteredTailLine(
        int lineNumber,
        string fullLine,
        int matchStart,
        int matchLength,
        ResponseCharacterBudget budget)
    {
        var normalized = LogContentSanitizer.Normalize(fullLine);
        var length = Math.Min(normalized.Length, Math.Min(_limits.MaximumCharactersPerLine, budget.Remaining));
        var windowStart = Math.Clamp(
            matchStart - Math.Max(0, (length - Math.Min(matchLength, length)) / 2),
            0,
            normalized.Length - length);
        if (matchStart + matchLength > windowStart + length)
            windowStart = Math.Min(matchStart, normalized.Length - length);
        budget.Consume(length);
        return new LogLineResult(
            lineNumber,
            normalized.Substring(windowStart, length),
            windowStart != 0 || length != normalized.Length);
    }
}
