namespace LogReader.Infrastructure.Services;

using System.Buffers;
using System.Text;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class ChunkedLogReaderService : ILogReaderService, IBoundedLogReaderService
{
    private const int BufferSize = 64 * 1024; // 64KB buffer
    private const FileShare LogReadShare = FileShare.ReadWrite | FileShare.Delete;
    private const int GenerationStabilityAttemptCount = 2;
    private readonly Func<FileStream, DateTime> _lastWriteTimeUtcProvider;
    private readonly Func<FileStream, FileGenerationToken> _generationTokenProvider;
    private readonly AutomaticReloadAdmission _automaticReloadAdmission;

    public ChunkedLogReaderService()
        : this(GetLastWriteTimeUtc, FileGenerationTokenProvider.Capture)
    {
    }

    internal ChunkedLogReaderService(
        Func<FileStream, DateTime> lastWriteTimeUtcProvider,
        Func<FileStream, FileGenerationToken>? generationTokenProvider = null,
        Func<long>? timestampProvider = null)
    {
        _lastWriteTimeUtcProvider = lastWriteTimeUtcProvider ?? throw new ArgumentNullException(nameof(lastWriteTimeUtcProvider));
        _generationTokenProvider = generationTokenProvider ?? FileGenerationTokenProvider.Capture;
        _automaticReloadAdmission = new AutomaticReloadAdmission(timestampProvider);
    }

    public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        => BuildBoundedIndexAsync(filePath, encoding, int.MaxValue, ct);

    public async Task<LineIndex> BuildBoundedIndexAsync(
        string filePath,
        FileEncoding encoding,
        int maximumLineCount,
        CancellationToken ct = default)
    {
        if (maximumLineCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumLineCount));

        for (var attempt = 0; attempt < GenerationStabilityAttemptCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = OpenReadStream(filePath, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var snapshotLength = GetSnapshotLength(stream.Length, encoding);
            var index = await BuildIndexAsync(
                filePath,
                stream,
                snapshotLength,
                encoding,
                maximumLineCount,
                ct).ConfigureAwait(false);
            if (IsCurrentPathGeneration(filePath, index.GenerationToken))
                return index;

            index.Dispose();
        }

        throw new IOException("The file changed repeatedly while its line index was being built.");
    }

    private async Task<LineIndex> BuildIndexAsync(
        string filePath,
        FileStream stream,
        long snapshotLength,
        FileEncoding encoding,
        int maximumLineCount,
        CancellationToken ct)
    {
        var index = new LineIndex
        {
            FilePath = filePath,
            GenerationToken = GetGenerationTokenOrUnknown(stream),
            LineOffsets = new MappedLineOffsets(GetWorkingMaximumLineCount(maximumLineCount))
        };
        try
        {
            index.LineOffsets.Add(0); // Seed first line candidate (trimmed for empty/BOM-only files)

            var initialLastWriteTimeUtc = GetLastWriteTimeUtcOrDefault(stream);

            var buffer = new byte[BufferSize];
            var preamble = await ProbePreambleAsync(
                stream,
                snapshotLength,
                encoding,
                ct).ConfigureAwait(false);
            if (preamble.IsPartial)
                snapshotLength = 0;

            long position = preamble.ContentOffset;
            index.LineOffsets[0] = position;
            stream.Position = position;

            var newlineScanState = new NewlineScanState();
            while (stream.Position < snapshotLength)
            {
                var bytesRead = await ReadWithinSnapshotAsync(
                    stream,
                    buffer,
                    snapshotLength,
                    ct).ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                ct.ThrowIfCancellationRequested();
                ScanNewlines(buffer, bytesRead, encoding, position, index.LineOffsets, ref newlineScanState);
                position += bytesRead;
            }

            FlushPendingNewline(index.LineOffsets, ref newlineScanState);
            TrimTrailingEmptyLine(index.LineOffsets, position);
            TrimEmptyFileLine(index.LineOffsets, position);
            EnforceFinalLineCount(index.LineOffsets, maximumLineCount);

            index.FileSize = position;
            index.LastWriteTimeUtc = ResolveStableSnapshotTimestamp(
                initialLastWriteTimeUtc,
                GetLastWriteTimeUtcOrDefault(stream));
            index.LineOffsets.Freeze();
            return index;
        }
        catch (LineIndexCapacityExceededException) when (maximumLineCount != int.MaxValue)
        {
            index.Dispose();
            throw new LineIndexCapacityExceededException(maximumLineCount);
        }
        catch
        {
            index.Dispose();
            throw;
        }
    }

    public Task<LineIndex> UpdateIndexAsync(
        string filePath,
        LineIndex existingIndex,
        FileEncoding encoding,
        CancellationToken ct = default)
        => UpdateIndexAsync(filePath, existingIndex, encoding, FileChangeHint.None, ct);

    public async Task<LineIndex> UpdateBoundedIndexAsync(
        string filePath,
        LineIndex existingIndex,
        FileEncoding encoding,
        int maximumLineCount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(existingIndex);
        if (maximumLineCount < existingIndex.LineCount)
            throw new ArgumentOutOfRangeException(nameof(maximumLineCount));

        existingIndex.LineOffsets.SetMaximumCount(GetWorkingMaximumLineCount(maximumLineCount));
        var originalOffsetCount = existingIndex.LineCount;
        var originalFileSize = existingIndex.FileSize;
        var originalLastWriteTimeUtc = existingIndex.LastWriteTimeUtc;
        var originalGenerationToken = existingIndex.GenerationToken;
        LineIndex? updatedIndex = null;
        try
        {
            updatedIndex = await UpdateIndexAsync(
                filePath,
                existingIndex,
                encoding,
                FileChangeHint.None,
                ct).ConfigureAwait(false);
            EnforceFinalLineCount(updatedIndex.LineOffsets, maximumLineCount);
            return updatedIndex;
        }
        catch
        {
            if (updatedIndex != null && !ReferenceEquals(updatedIndex, existingIndex))
                updatedIndex.Dispose();
            else
            {
                RollBackAppendedOffsets(existingIndex.LineOffsets, originalOffsetCount);
                existingIndex.FileSize = originalFileSize;
                existingIndex.LastWriteTimeUtc = originalLastWriteTimeUtc;
                existingIndex.GenerationToken = originalGenerationToken;
            }
            throw;
        }
        finally
        {
            existingIndex.LineOffsets.SetMaximumCount(maximumLineCount);
        }
    }

    public async Task<LineIndex> UpdateIndexAsync(
        string filePath,
        LineIndex existingIndex,
        FileEncoding encoding,
        FileChangeHint changeHint,
        CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < GenerationStabilityAttemptCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = OpenReadStream(filePath, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var currentSize = GetSnapshotLength(stream.Length, encoding);
            var openedGenerationToken = GetGenerationTokenOrUnknown(stream);
            var openedLastWriteTimeUtc = GetLastWriteTimeUtcOrDefault(stream);
            var openedSnapshot = new FileMetadataSnapshot(currentSize, openedGenerationToken);

            var rebuildDecision = GetAutomaticRebuildDecision(
                existingIndex,
                openedSnapshot,
                changeHint);
            if (rebuildDecision.Reason != FileChangeHint.None)
            {
                return await BuildAutomaticReplacementIndexAsync(
                    filePath,
                    existingIndex,
                    encoding,
                    openedSnapshot,
                    rebuildDecision,
                    ct).ConfigureAwait(false);
            }

            // No new data.
            if (currentSize == existingIndex.FileSize)
            {
                existingIndex.GenerationToken = ResolveGenerationToken(
                    existingIndex.GenerationToken,
                    openedGenerationToken);
                existingIndex.LastWriteTimeUtc = ResolveStableSnapshotTimestamp(
                    existingIndex.LastWriteTimeUtc,
                    openedLastWriteTimeUtc);
                return existingIndex;
            }

            if (existingIndex.GenerationToken.IsKnown &&
                !openedGenerationToken.IsKnown)
            {
                throw new IOException(
                    "The file identity is temporarily unavailable; the existing index was left unchanged.");
            }

            var originalOffsetCount = existingIndex.LineOffsets.Count;
            try
            {
                await AppendOffsetsAsync(
                    stream,
                    existingIndex,
                    currentSize,
                    encoding,
                    ct).ConfigureAwait(false);

                if (!IsCurrentPathGenerationForAppend(
                        filePath,
                        existingIndex.GenerationToken,
                        openedGenerationToken))
                {
                    RollBackAppendedOffsets(existingIndex.LineOffsets, originalOffsetCount);
                    continue;
                }

                existingIndex.FileSize = stream.Position;
                existingIndex.GenerationToken = ResolveGenerationToken(
                    existingIndex.GenerationToken,
                    openedGenerationToken);
                existingIndex.LastWriteTimeUtc = existingIndex.LastWriteTimeUtc != default &&
                                                 existingIndex.LastWriteTimeUtc == openedLastWriteTimeUtc
                    ? ResolveStableSnapshotTimestamp(openedLastWriteTimeUtc, GetLastWriteTimeUtcOrDefault(stream))
                    : default;
                return existingIndex;
            }
            catch
            {
                RollBackAppendedOffsets(existingIndex.LineOffsets, originalOffsetCount);
                throw;
            }
        }

        throw new IOException("The file changed repeatedly while its line index was being updated.");
    }

    private async Task<LineIndex> BuildAutomaticReplacementIndexAsync(
        string filePath,
        LineIndex existingIndex,
        FileEncoding encoding,
        FileMetadataSnapshot openedSnapshot,
        AutomaticRebuildDecision rebuildDecision,
        CancellationToken ct)
    {
        FileMetadataSnapshot corroboratingSnapshot;
        try
        {
            corroboratingSnapshot = CaptureMetadataSnapshot(filePath, encoding);
        }
        catch (Exception ex) when (IsMetadataProbeException(ex))
        {
            throw new AutomaticReloadBlockedException(
                "Automatic tailing paused because the file metadata could not be corroborated.",
                innerException: ex);
        }

        if (!IsRebuildEvidenceCorroborated(
                existingIndex,
                openedSnapshot,
                corroboratingSnapshot,
                rebuildDecision))
        {
            throw new AutomaticReloadBlockedException(
                "Automatic tailing paused because the file metadata was inconsistent.");
        }

        FileStream scanStream;
        try
        {
            scanStream = OpenReadStream(
                filePath,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
        }
        catch (Exception ex) when (IsMetadataProbeException(ex))
        {
            throw new AutomaticReloadBlockedException(
                "Automatic tailing paused because the replacement could not be opened consistently.",
                innerException: ex);
        }

        await using var ownedScanStream = scanStream;
        FileMetadataSnapshot scanSnapshot;
        try
        {
            scanSnapshot = new FileMetadataSnapshot(
                GetSnapshotLength(ownedScanStream.Length, encoding),
                GetGenerationTokenOrUnknown(ownedScanStream));
        }
        catch (Exception ex) when (IsMetadataProbeException(ex))
        {
            throw new AutomaticReloadBlockedException(
                "Automatic tailing paused because the replacement metadata was unavailable.",
                innerException: ex);
        }

        if (!IsRebuildEvidenceCorroborated(
                existingIndex,
                corroboratingSnapshot,
                scanSnapshot,
                rebuildDecision))
        {
            throw new AutomaticReloadBlockedException(
                "Automatic tailing paused because the file changed while its replacement was being verified.");
        }

        if (!_automaticReloadAdmission.TryAdmit(
                existingIndex,
                scanSnapshot.Length,
                out var retryAfter))
        {
            throw new AutomaticReloadBlockedException(
                "Automatic tailing paused to prevent repeated full-file reloads.",
                retryAfter);
        }

        LineIndex? rebuiltIndex = null;
        try
        {
            rebuiltIndex = await BuildIndexAsync(
                filePath,
                ownedScanStream,
                scanSnapshot.Length,
                encoding,
                existingIndex.LineOffsets.MaximumCount,
                ct).ConfigureAwait(false);
            rebuiltIndex.ReplacesPriorGeneration = true;
            rebuiltIndex.AutomaticReloadNotBeforeTimestamp =
                existingIndex.AutomaticReloadNotBeforeTimestamp;

            if (!IsAutomaticRebuildCurrent(
                    filePath,
                    rebuiltIndex.GenerationToken,
                    scanSnapshot.Length,
                    encoding))
            {
                throw new AutomaticReloadBlockedException(
                    "Automatic tailing paused because the file changed during its reload.",
                    _automaticReloadAdmission.GetRetryAfter(existingIndex));
            }

            var result = rebuiltIndex;
            rebuiltIndex = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AutomaticReloadBlockedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AutomaticReloadBlockedException(
                "Automatic tailing paused after an automatic reload failed.",
                _automaticReloadAdmission.GetRetryAfter(existingIndex),
                ex);
        }
        finally
        {
            rebuiltIndex?.Dispose();
        }
    }

    private static async Task AppendOffsetsAsync(
        FileStream stream,
        LineIndex existingIndex,
        long currentSize,
        FileEncoding encoding,
        CancellationToken ct)
    {
        var scanStart = existingIndex.FileSize;
        var scanEnd = currentSize;
        var boundary = AppendBoundary.NoLineEnding;
        if (existingIndex.FileSize == 0 && existingIndex.LineOffsets.Count == 0)
        {
            var preamble = await ProbePreambleAsync(
                stream,
                currentSize,
                encoding,
                ct).ConfigureAwait(false);
            scanStart = preamble.ContentOffset;
            if (preamble.IsPartial)
                scanEnd = 0;

            if (scanStart < scanEnd)
                existingIndex.LineOffsets.Add(scanStart);
        }
        else
        {
            boundary = await ClassifyAppendBoundaryAsync(
                stream,
                existingIndex.FileSize,
                currentSize,
                encoding,
                ct).ConfigureAwait(false);

            // Check if we need to add the start-of-new-data as a new line offset.
            if (existingIndex.LineOffsets.Count > 0)
            {
                var lastOffset = existingIndex.LineOffsets[^1];
                if (lastOffset < existingIndex.FileSize)
                {
                    if (boundary == AppendBoundary.CompleteLineEnding)
                        existingIndex.LineOffsets.Add(existingIndex.FileSize);
                }
            }
            else
            {
                // Existing file had no readable lines (empty/BOM-only); appended data starts a new line.
                existingIndex.LineOffsets.Add(existingIndex.FileSize);
            }
        }

        // Seek to where we left off and scan new bytes
        stream.Position = scanStart;
        var buffer = new byte[BufferSize];
        long position = scanStart;
        var newlineScanState = boundary == AppendBoundary.PendingCarriageReturn
            ? NewlineScanState.CreatePendingCarriageReturn(existingIndex.FileSize)
            : new NewlineScanState();

        while (stream.Position < scanEnd)
        {
            var bytesRead = await ReadWithinSnapshotAsync(
                stream,
                buffer,
                scanEnd,
                ct).ConfigureAwait(false);
            if (bytesRead == 0)
                break;

            ct.ThrowIfCancellationRequested();
            ScanNewlines(buffer, bytesRead, encoding, position, existingIndex.LineOffsets, ref newlineScanState);
            position += bytesRead;
        }

        FlushPendingNewline(existingIndex.LineOffsets, ref newlineScanState);
        TrimTrailingEmptyLine(existingIndex.LineOffsets, position);
    }

    private static async ValueTask<PreambleProbe> ProbePreambleAsync(
        FileStream stream,
        long snapshotLength,
        FileEncoding encoding,
        CancellationToken ct)
    {
        byte[]? expectedPreamble = encoding switch
        {
            FileEncoding.Utf8 or FileEncoding.Utf8Bom => [0xEF, 0xBB, 0xBF],
            FileEncoding.Utf16 => [0xFF, 0xFE],
            FileEncoding.Utf16Be => [0xFE, 0xFF],
            _ => null
        };
        if (expectedPreamble == null)
            return default;

        stream.Position = 0;
        var observed = new byte[expectedPreamble.Length];
        var observedCount = await ReadWithinSnapshotAsync(
            stream,
            observed,
            snapshotLength,
            ct).ConfigureAwait(false);
        stream.Position = 0;

        var matchingCount = Math.Min(observedCount, expectedPreamble.Length);
        var matchesPrefix = observed.AsSpan(0, matchingCount)
            .SequenceEqual(expectedPreamble.AsSpan(0, matchingCount));
        if (!matchesPrefix)
            return default;

        if (observedCount < expectedPreamble.Length)
            return new PreambleProbe(0, IsPartial: observedCount > 0);

        return new PreambleProbe(expectedPreamble.Length, IsPartial: false);
    }

    private static async ValueTask<int> ReadWithinSnapshotAsync(
        FileStream stream,
        Memory<byte> buffer,
        long snapshotLength,
        CancellationToken ct)
    {
        var remaining = snapshotLength - stream.Position;
        if (remaining <= 0)
            return 0;

        var readLength = (int)Math.Min(buffer.Length, remaining);
        return await stream.ReadAsync(buffer[..readLength], ct).ConfigureAwait(false);
    }

    private static long GetSnapshotLength(long fileLength, FileEncoding encoding)
        => encoding is FileEncoding.Utf16 or FileEncoding.Utf16Be
            ? fileLength & ~1L
            : fileLength;

    private static AutomaticRebuildDecision GetAutomaticRebuildDecision(
        LineIndex existingIndex,
        FileMetadataSnapshot openedSnapshot,
        FileChangeHint changeHint)
    {
        if (openedSnapshot.Length < existingIndex.FileSize)
        {
            return new AutomaticRebuildDecision(
                FileChangeHint.Truncated,
                IsMonitorConfirmedTruncation: changeHint == FileChangeHint.Truncated);
        }

        if (existingIndex.GenerationToken.IsKnown &&
            openedSnapshot.GenerationToken.IsKnown &&
            existingIndex.GenerationToken != openedSnapshot.GenerationToken)
        {
            return new AutomaticRebuildDecision(
                FileChangeHint.IdentityChanged,
                IsMonitorConfirmedTruncation: false);
        }

        return changeHint switch
        {
            FileChangeHint.Truncated => new AutomaticRebuildDecision(
                changeHint,
                IsMonitorConfirmedTruncation: true),
            FileChangeHint.RecreatedAfterMissing => new AutomaticRebuildDecision(
                changeHint,
                IsMonitorConfirmedTruncation: false),
            FileChangeHint.UnspecifiedReplacement => new AutomaticRebuildDecision(
                changeHint,
                IsMonitorConfirmedTruncation: false),
            FileChangeHint.IdentityChanged
                when !existingIndex.GenerationToken.IsKnown ||
                     !openedSnapshot.GenerationToken.IsKnown =>
                new AutomaticRebuildDecision(
                    changeHint,
                    IsMonitorConfirmedTruncation: false),
            _ => default
        };
    }

    private static FileGenerationToken ResolveGenerationToken(
        FileGenerationToken existingToken,
        FileGenerationToken openedToken)
    {
        if (!existingToken.IsKnown)
            return FileGenerationToken.Unknown;

        return openedToken.IsKnown && existingToken == openedToken
            ? openedToken
            : existingToken;
    }

    private FileMetadataSnapshot CaptureMetadataSnapshot(
        string filePath,
        FileEncoding encoding)
    {
        using var stream = OpenReadStream(filePath, FileOptions.RandomAccess);
        return new FileMetadataSnapshot(
            GetSnapshotLength(stream.Length, encoding),
            GetGenerationTokenOrUnknown(stream));
    }

    private static bool IsRebuildEvidenceCorroborated(
        LineIndex existingIndex,
        FileMetadataSnapshot first,
        FileMetadataSnapshot second,
        AutomaticRebuildDecision rebuildDecision)
    {
        if (!HaveCompatibleIdentities(first.GenerationToken, second.GenerationToken))
            return false;

        return rebuildDecision.Reason switch
        {
            FileChangeHint.Truncated =>
                rebuildDecision.IsMonitorConfirmedTruncation ||
                first.Length < existingIndex.FileSize &&
                second.Length < existingIndex.FileSize,
            FileChangeHint.IdentityChanged =>
                IsCorroboratedIdentityChange(
                    existingIndex.GenerationToken,
                    first.GenerationToken,
                    second.GenerationToken),
            FileChangeHint.RecreatedAfterMissing => true,
            FileChangeHint.UnspecifiedReplacement => true,
            _ => false
        };
    }

    private static bool HaveCompatibleIdentities(
        FileGenerationToken first,
        FileGenerationToken second)
    {
        if (first.IsKnown != second.IsKnown)
            return false;

        return !first.IsKnown || first == second;
    }

    private static bool IsCorroboratedIdentityChange(
        FileGenerationToken existing,
        FileGenerationToken first,
        FileGenerationToken second)
    {
        if (!first.IsKnown || !second.IsKnown || first != second)
            return false;

        return !existing.IsKnown || existing != first;
    }

    private bool IsCurrentPathGenerationForAppend(
        string filePath,
        FileGenerationToken trustedToken,
        FileGenerationToken openedToken)
    {
        if (!trustedToken.IsKnown)
            return true;

        if (!openedToken.IsKnown || trustedToken != openedToken)
            return false;

        try
        {
            using var currentStream = OpenReadStream(filePath, FileOptions.RandomAccess);
            var currentToken = GetGenerationTokenOrUnknown(currentStream);
            return currentToken.IsKnown && currentToken == trustedToken;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private bool IsAutomaticRebuildCurrent(
        string filePath,
        FileGenerationToken scannedToken,
        long snapshotLength,
        FileEncoding encoding)
    {
        try
        {
            var current = CaptureMetadataSnapshot(filePath, encoding);
            if (current.Length < snapshotLength)
                return false;

            if (scannedToken.IsKnown != current.GenerationToken.IsKnown)
                return false;

            if (scannedToken.IsKnown)
                return scannedToken == current.GenerationToken;

            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void RollBackAppendedOffsets(MappedLineOffsets offsets, int originalCount)
    {
        while (offsets.Count > originalCount)
            offsets.RemoveAt(offsets.Count - 1);
    }

    private FileGenerationToken GetGenerationTokenOrUnknown(FileStream stream)
    {
        try
        {
            return _generationTokenProvider(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return FileGenerationToken.Unknown;
        }
    }

    private bool IsCurrentPathGeneration(string filePath, FileGenerationToken scannedToken)
    {
        if (!scannedToken.IsKnown)
            return true;

        try
        {
            using var currentStream = OpenReadStream(filePath, FileOptions.RandomAccess);
            var currentToken = GetGenerationTokenOrUnknown(currentStream);
            return !currentToken.IsKnown || currentToken == scannedToken;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static FileStream OpenReadStream(string filePath, FileOptions options)
        => new(filePath, FileMode.Open, FileAccess.Read, LogReadShare, BufferSize, options);

    private DateTime GetLastWriteTimeUtcOrDefault(FileStream stream)
    {
        try
        {
            return _lastWriteTimeUtcProvider(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return default;
        }
    }

    internal static DateTime GetLastWriteTimeUtc(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return File.GetLastWriteTimeUtc(stream.SafeFileHandle);
    }

    internal static DateTime ResolveStableSnapshotTimestamp(DateTime initialTimestamp, DateTime finalTimestamp)
        => initialTimestamp != default && initialTimestamp == finalTimestamp
            ? finalTimestamp
            : default;

    public async Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
    {
        if (startLine < 0 || startLine >= index.LineCount || count <= 0)
            return Array.Empty<string>();

        int endLine = Math.Min(startLine + count, index.LineCount) - 1;
        long startOffset = index.LineOffsets[startLine];
        long endOffset = endLine + 1 < index.LineCount ? index.LineOffsets[endLine + 1] : index.FileSize;
        long byteCount = endOffset - startOffset;

        if (byteCount <= 0) return Array.Empty<string>();

        var enc = EncodingHelper.GetEncoding(encoding);

        await using var stream = OpenReadStream(filePath, FileOptions.Asynchronous);

        var openedGenerationToken = GetGenerationTokenOrUnknown(stream);
        if (index.GenerationToken.IsKnown &&
            openedGenerationToken.IsKnown &&
            index.GenerationToken != openedGenerationToken)
        {
            throw new IOException("The file changed before the indexed lines could be read.");
        }

        if (stream.Length < index.FileSize)
            throw new IOException("The file was truncated before the indexed lines could be read.");

        var targetLineCount = endLine - startLine + 1;
        var result = new List<string>(targetLineCount);
        for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
        {
            var lineStartOffset = index.LineOffsets[lineNumber];
            var lineEndOffset = lineNumber + 1 < index.LineCount
                ? index.LineOffsets[lineNumber + 1]
                : index.FileSize;
            var lineByteCount = lineEndOffset - lineStartOffset;
            if (lineByteCount <= 0)
            {
                result.Add(string.Empty);
                continue;
            }

            result.Add(await ReadLineSegmentAsync(stream, lineStartOffset, lineByteCount, enc, ct).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
    {
        var lines = await ReadLinesAsync(filePath, index, lineNumber, 1, encoding, ct).ConfigureAwait(false);
        return lines.Count > 0 ? lines[0] : string.Empty;
    }

    private static int GetWorkingMaximumLineCount(int maximumLineCount)
        => maximumLineCount == int.MaxValue ? int.MaxValue : maximumLineCount + 1;

    private static void EnforceFinalLineCount(MappedLineOffsets offsets, int maximumLineCount)
    {
        if (offsets.Count > maximumLineCount)
            throw new LineIndexCapacityExceededException(maximumLineCount);

        offsets.SetMaximumCount(maximumLineCount);
    }

    public async Task<IReadOnlyList<BoundedIndexedLine>> ReadBoundedLinesAsync(
        string filePath,
        LineIndex index,
        int startLine,
        int count,
        FileEncoding encoding,
        int maximumCharactersPerLine,
        int maximumTotalCharacters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (maximumCharactersPerLine < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCharactersPerLine));
        if (maximumTotalCharacters < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalCharacters));
        if (startLine < 0 || startLine >= index.LineCount || count <= 0)
            return Array.Empty<BoundedIndexedLine>();

        var endLine = (int)Math.Min((long)startLine + count, index.LineCount) - 1;
        var enc = EncodingHelper.GetEncoding(encoding);
        await using var stream = OpenReadStream(filePath, FileOptions.Asynchronous);

        var openedGenerationToken = GetGenerationTokenOrUnknown(stream);
        if (index.GenerationToken.IsKnown &&
            openedGenerationToken.IsKnown &&
            index.GenerationToken != openedGenerationToken)
        {
            throw new IOException("The file changed before the indexed lines could be read.");
        }

        if (stream.Length < index.FileSize)
            throw new IOException("The file was truncated before the indexed lines could be read.");

        var result = new List<BoundedIndexedLine>(endLine - startLine + 1);
        var remainingCharacters = maximumTotalCharacters;
        for (var lineNumber = startLine; lineNumber <= endLine && remainingCharacters > 0; lineNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var lineStartOffset = index.LineOffsets[lineNumber];
            var lineEndOffset = lineNumber + 1 < index.LineCount
                ? index.LineOffsets[lineNumber + 1]
                : index.FileSize;
            var maximumCharacters = Math.Min(maximumCharactersPerLine, remainingCharacters);
            var line = await ReadBoundedLineSegmentAsync(
                stream,
                lineStartOffset,
                Math.Max(0, lineEndOffset - lineStartOffset),
                enc,
                maximumCharacters,
                ct).ConfigureAwait(false);
            result.Add(new BoundedIndexedLine(lineNumber, line.Text, line.IsTruncated));
            remainingCharacters -= line.Text.Length;
        }

        return result;
    }

    public async Task<IReadOnlyList<BoundedIndexedLine>> ReadBoundedLinesAsync(
        string filePath,
        IndexedLogReadSnapshot snapshot,
        int maximumCharactersPerLine,
        int maximumTotalCharacters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (maximumCharactersPerLine < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCharactersPerLine));
        if (maximumTotalCharacters < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalCharacters));
        if (snapshot.Lines.IsEmpty)
            return Array.Empty<BoundedIndexedLine>();

        var enc = EncodingHelper.GetEncoding(snapshot.Encoding);
        await using var stream = OpenReadStream(filePath, FileOptions.Asynchronous);
        var openedGenerationToken = GetGenerationTokenOrUnknown(stream);
        if (snapshot.GenerationToken.IsKnown &&
            openedGenerationToken.IsKnown &&
            snapshot.GenerationToken != openedGenerationToken)
        {
            throw new IOException("The file changed before the indexed lines could be read.");
        }

        if (stream.Length < snapshot.FileSize)
            throw new IOException("The file was truncated before the indexed lines could be read.");

        var result = new List<BoundedIndexedLine>(snapshot.Lines.Length);
        var remainingCharacters = maximumTotalCharacters;
        foreach (var bounds in snapshot.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (remainingCharacters == 0)
                break;

            var maximumCharacters = Math.Min(maximumCharactersPerLine, remainingCharacters);
            var line = await ReadBoundedLineSegmentAsync(
                stream,
                bounds.StartOffset,
                bounds.EndOffset - bounds.StartOffset,
                enc,
                maximumCharacters,
                ct).ConfigureAwait(false);
            result.Add(new BoundedIndexedLine(bounds.LineNumber, line.Text, line.IsTruncated));
            remainingCharacters -= line.Text.Length;
        }

        return result;
    }

    internal static void ScanNewlines(
        byte[] buffer,
        int bytesRead,
        FileEncoding encoding,
        long basePosition,
        MappedLineOffsets offsets,
        ref NewlineScanState state)
    {
        if (encoding == FileEncoding.Utf16)
        {
            ScanUtf16Newlines(buffer, bytesRead, basePosition, offsets, ref state, littleEndian: true);
        }
        else if (encoding == FileEncoding.Utf16Be)
        {
            ScanUtf16Newlines(buffer, bytesRead, basePosition, offsets, ref state, littleEndian: false);
        }
        else
        {
            // UTF-8 / ANSI: scan for CR, LF, and CRLF byte line endings.
            for (int i = 0; i < bytesRead; i++)
            {
                var current = buffer[i];
                if (state.HasPendingCarriageReturn)
                {
                    if (current == (byte)'\n')
                    {
                        offsets.Add(basePosition + i + 1);
                        state = state with { HasPendingCarriageReturn = false };
                        continue;
                    }

                    offsets.Add(state.PendingCarriageReturnOffset);
                    state = state with { HasPendingCarriageReturn = false };
                }

                if (current == (byte)'\r')
                {
                    state = state with
                    {
                        HasPendingCarriageReturn = true,
                        PendingCarriageReturnOffset = basePosition + i + 1
                    };
                }
                else if (current == (byte)'\n')
                {
                    offsets.Add(basePosition + i + 1);
                }
            }
        }
    }

    private static void ScanUtf16Newlines(
        byte[] buffer,
        int bytesRead,
        long basePosition,
        MappedLineOffsets offsets,
        ref NewlineScanState state,
        bool littleEndian)
    {
        var startIndex = 0;
        if (state.HasPendingByte)
        {
            if (bytesRead > 0)
            {
                var codeUnit = littleEndian
                    ? (char)(state.PendingByte | (buffer[0] << 8))
                    : (char)((state.PendingByte << 8) | buffer[0]);
                AddUtf16NewlineOffset(codeUnit, basePosition + 1, offsets, ref state);
            }

            state = state with { HasPendingByte = false };
            startIndex = 1;
        }

        var i = startIndex;
        for (; i < bytesRead - 1; i += 2)
        {
            var codeUnit = littleEndian
                ? (char)(buffer[i] | (buffer[i + 1] << 8))
                : (char)((buffer[i] << 8) | buffer[i + 1]);
            AddUtf16NewlineOffset(codeUnit, basePosition + i + 2, offsets, ref state);
        }

        if (i < bytesRead)
            state = state with { PendingByte = buffer[i], HasPendingByte = true };
    }

    private static void AddUtf16NewlineOffset(
        char codeUnit,
        long offsetAfterCodeUnit,
        MappedLineOffsets offsets,
        ref NewlineScanState state)
    {
        if (state.HasPendingCarriageReturn)
        {
            if (codeUnit == '\n')
            {
                offsets.Add(offsetAfterCodeUnit);
                state = state with { HasPendingCarriageReturn = false };
                return;
            }

            offsets.Add(state.PendingCarriageReturnOffset);
            state = state with { HasPendingCarriageReturn = false };
        }

        if (codeUnit == '\r')
        {
            state = state with
            {
                HasPendingCarriageReturn = true,
                PendingCarriageReturnOffset = offsetAfterCodeUnit
            };
        }
        else if (codeUnit == '\n')
        {
            offsets.Add(offsetAfterCodeUnit);
        }
    }

    private static void FlushPendingNewline(MappedLineOffsets offsets, ref NewlineScanState state)
    {
        if (!state.HasPendingCarriageReturn)
            return;

        offsets.Add(state.PendingCarriageReturnOffset);
        state = state with { HasPendingCarriageReturn = false };
    }

    internal readonly record struct NewlineScanState(
        byte PendingByte,
        bool HasPendingByte,
        bool HasPendingCarriageReturn,
        long PendingCarriageReturnOffset)
    {
        public NewlineScanState()
            : this(default, false, false, 0)
        {
        }

        public NewlineScanState(byte pendingByte)
            : this(pendingByte, true, false, 0)
        {
        }

        public static NewlineScanState CreatePendingCarriageReturn(long pendingCarriageReturnOffset)
            => new(default, false, true, pendingCarriageReturnOffset);
    }

    private static async Task<string> ReadLineSegmentAsync(
        FileStream stream,
        long offset,
        long byteCount,
        Encoding encoding,
        CancellationToken ct)
    {
        if (byteCount > int.MaxValue)
            throw new InvalidOperationException("Line is too large to read.");

        var rented = ArrayPool<byte>.Shared.Rent((int)byteCount);
        try
        {
            stream.Position = offset;
            var totalRead = 0;
            while (totalRead < byteCount)
            {
                var read = await stream.ReadAsync(
                    rented.AsMemory(totalRead, (int)byteCount - totalRead),
                    ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                totalRead += read;
            }

            return TrimLineEnding(encoding.GetString(rented, 0, totalRead));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task<(string Text, bool IsTruncated)> ReadBoundedLineSegmentAsync(
        FileStream stream,
        long offset,
        long byteCount,
        Encoding encoding,
        int maximumCharacters,
        CancellationToken ct)
    {
        if (byteCount <= 0)
            return (string.Empty, false);

        var maximumBytesPerCharacter = Math.Max(1, encoding.GetMaxByteCount(1));
        var maximumBytes = checked((long)(maximumCharacters + 1) * maximumBytesPerCharacter);
        var bytesToRead = (int)Math.Min(byteCount, maximumBytes);
        var rented = ArrayPool<byte>.Shared.Rent(bytesToRead);
        try
        {
            stream.Position = offset;
            var totalRead = 0;
            while (totalRead < bytesToRead)
            {
                var read = await stream.ReadAsync(
                    rented.AsMemory(totalRead, bytesToRead - totalRead),
                    ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                totalRead += read;
            }

            var text = encoding.GetString(rented, 0, totalRead);
            var content = totalRead >= byteCount ? TrimLineEnding(text) : text;
            var isTruncated = totalRead < byteCount || content.Length > maximumCharacters;
            if (content.Length > maximumCharacters)
                content = content[..maximumCharacters];

            return (content, isTruncated);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string TrimLineEnding(string line)
    {
        if (line.EndsWith("\r\n", StringComparison.Ordinal))
            return line[..^2];

        if (line.EndsWith('\n') || line.EndsWith('\r'))
            return line[..^1];

        return line;
    }

    private static async Task<AppendBoundary> ClassifyAppendBoundaryAsync(
        FileStream stream,
        long previousFileSize,
        long currentFileSize,
        FileEncoding encoding,
        CancellationToken ct)
    {
        if (previousFileSize <= 0)
            return AppendBoundary.NoLineEnding;

        if (encoding is FileEncoding.Utf16 or FileEncoding.Utf16Be)
        {
            if (previousFileSize < 2)
                return AppendBoundary.NoLineEnding;

            var buffer = new byte[2];
            stream.Position = previousFileSize - 2;
            var read = await stream.ReadAsync(buffer.AsMemory(0, 2), ct).ConfigureAwait(false);
            if (read != 2)
                return AppendBoundary.NoLineEnding;

            var previousCodeUnit = encoding == FileEncoding.Utf16
                ? (char)(buffer[0] | (buffer[1] << 8))
                : (char)((buffer[0] << 8) | buffer[1]);
            if (previousCodeUnit == '\n')
                return AppendBoundary.CompleteLineEnding;
            if (previousCodeUnit != '\r')
                return AppendBoundary.NoLineEnding;

            var appendedCodeUnit = await TryReadCodeUnitAsync(
                stream,
                previousFileSize,
                currentFileSize,
                encoding,
                ct).ConfigureAwait(false);
            return appendedCodeUnit == '\n'
                ? AppendBoundary.PendingCarriageReturn
                : AppendBoundary.CompleteLineEnding;
        }

        stream.Position = previousFileSize - 1;
        var previousByte = stream.ReadByte();
        return previousByte switch
        {
            '\n' => AppendBoundary.CompleteLineEnding,
            '\r' => TryReadByte(stream, previousFileSize, currentFileSize) == '\n'
                ? AppendBoundary.PendingCarriageReturn
                : AppendBoundary.CompleteLineEnding,
            _ => AppendBoundary.NoLineEnding
        };
    }

    private static async Task<char?> TryReadCodeUnitAsync(
        FileStream stream,
        long offset,
        long currentFileSize,
        FileEncoding encoding,
        CancellationToken ct)
    {
        if (currentFileSize - offset < 2)
            return null;

        var buffer = new byte[2];
        stream.Position = offset;
        var read = await stream.ReadAsync(buffer.AsMemory(0, 2), ct).ConfigureAwait(false);
        if (read != 2)
            return null;

        return encoding == FileEncoding.Utf16
            ? (char)(buffer[0] | (buffer[1] << 8))
            : (char)((buffer[0] << 8) | buffer[1]);
    }

    private static int? TryReadByte(FileStream stream, long offset, long currentFileSize)
    {
        if (currentFileSize <= offset)
            return null;

        stream.Position = offset;
        return stream.ReadByte();
    }

    private enum AppendBoundary
    {
        NoLineEnding,
        CompleteLineEnding,
        PendingCarriageReturn
    }

    private static bool IsMetadataProbeException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or NotSupportedException;

    private readonly record struct FileMetadataSnapshot(
        long Length,
        FileGenerationToken GenerationToken);

    private readonly record struct AutomaticRebuildDecision(
        FileChangeHint Reason,
        bool IsMonitorConfirmedTruncation);

    private readonly record struct PreambleProbe(long ContentOffset, bool IsPartial);

    private static void TrimTrailingEmptyLine(MappedLineOffsets offsets, long fileSize)
    {
        if (offsets.Count > 1 && offsets[^1] >= fileSize)
            offsets.RemoveAt(offsets.Count - 1);
    }

    private static void TrimEmptyFileLine(MappedLineOffsets offsets, long fileSize)
    {
        if (offsets.Count == 1 && offsets[0] >= fileSize)
            offsets.RemoveAt(0);
    }
}
