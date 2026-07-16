namespace LogReader.Infrastructure.Services;

using System.Buffers;
using System.Text;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class ChunkedLogReaderService : ILogReaderService
{
    private const int BufferSize = 64 * 1024; // 64KB buffer
    private const FileShare LogReadShare = FileShare.ReadWrite | FileShare.Delete;
    private const int GenerationStabilityAttemptCount = 2;
    private readonly Func<FileStream, DateTime> _lastWriteTimeUtcProvider;
    private readonly Func<FileStream, FileGenerationToken> _generationTokenProvider;

    public ChunkedLogReaderService()
        : this(GetLastWriteTimeUtc, FileGenerationTokenProvider.Capture)
    {
    }

    internal ChunkedLogReaderService(
        Func<FileStream, DateTime> lastWriteTimeUtcProvider,
        Func<FileStream, FileGenerationToken>? generationTokenProvider = null)
    {
        _lastWriteTimeUtcProvider = lastWriteTimeUtcProvider ?? throw new ArgumentNullException(nameof(lastWriteTimeUtcProvider));
        _generationTokenProvider = generationTokenProvider ?? FileGenerationTokenProvider.Capture;
    }

    public async Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < GenerationStabilityAttemptCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = OpenReadStream(filePath, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var index = await BuildIndexAsync(filePath, stream, encoding, ct).ConfigureAwait(false);
            if (IsCurrentPathGeneration(filePath, index.GenerationToken))
                return index;

            index.Dispose();
        }

        throw new IOException("The file changed repeatedly while its line index was being built.");
    }

    private async Task<LineIndex> BuildIndexAsync(
        string filePath,
        FileStream stream,
        FileEncoding encoding,
        CancellationToken ct)
    {
        var index = new LineIndex
        {
            FilePath = filePath,
            GenerationToken = GetGenerationTokenOrUnknown(stream)
        };
        index.LineOffsets.Add(0); // Seed first line candidate (trimmed for empty/BOM-only files)

        var initialLastWriteTimeUtc = GetLastWriteTimeUtcOrDefault(stream);

        var buffer = new byte[BufferSize];
        long position = 0;

        // Skip BOM if present
        if (encoding == FileEncoding.Utf16)
        {
            var bom = new byte[2];
            var bomRead = await stream.ReadAsync(bom, ct).ConfigureAwait(false);
            if (bomRead == 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            {
                position = 2;
                index.LineOffsets[0] = 2;
            }
            else
            {
                stream.Position = 0;
            }
        }
        else if (encoding is FileEncoding.Utf8 or FileEncoding.Utf8Bom)
        {
            var bom = new byte[3];
            var bomRead = await stream.ReadAsync(bom, ct).ConfigureAwait(false);
            if (bomRead == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            {
                position = 3;
                index.LineOffsets[0] = 3;
            }
            else
            {
                stream.Position = 0;
            }
        }
        else if (encoding == FileEncoding.Utf16Be)
        {
            var bom = new byte[2];
            var bomRead = await stream.ReadAsync(bom, ct).ConfigureAwait(false);
            if (bomRead == 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            {
                position = 2;
                index.LineOffsets[0] = 2;
            }
            else
            {
                stream.Position = 0;
            }
        }

        int bytesRead;
        var newlineScanState = new NewlineScanState();
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            ScanNewlines(buffer, bytesRead, encoding, position, index.LineOffsets, ref newlineScanState);
            position += bytesRead;
        }

        FlushPendingNewline(index.LineOffsets, ref newlineScanState);
        TrimTrailingEmptyLine(index.LineOffsets, position);
        TrimEmptyFileLine(index.LineOffsets, position);

        index.FileSize = position;
        index.LastWriteTimeUtc = ResolveStableSnapshotTimestamp(
            initialLastWriteTimeUtc,
            GetLastWriteTimeUtcOrDefault(stream));
        index.LineOffsets.Freeze();
        return index;
    }

    public async Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < GenerationStabilityAttemptCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = OpenReadStream(filePath, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var openedGenerationToken = GetGenerationTokenOrUnknown(stream);
            var currentSize = stream.Length;
            var openedLastWriteTimeUtc = GetLastWriteTimeUtcOrDefault(stream);

            if (RequiresRebuild(existingIndex, openedGenerationToken, currentSize))
            {
                var rebuiltIndex = await BuildIndexAsync(filePath, stream, encoding, ct).ConfigureAwait(false);
                rebuiltIndex.ReplacesPriorGeneration = true;
                if (IsCurrentPathGeneration(filePath, rebuiltIndex.GenerationToken))
                    return rebuiltIndex;

                rebuiltIndex.Dispose();
                continue;
            }

            // No new data.
            if (currentSize == existingIndex.FileSize)
            {
                if (!IsCurrentPathGeneration(filePath, openedGenerationToken))
                    continue;

                existingIndex.GenerationToken = ResolveGenerationToken(
                    existingIndex.GenerationToken,
                    openedGenerationToken);
                existingIndex.LastWriteTimeUtc = ResolveStableSnapshotTimestamp(
                    existingIndex.LastWriteTimeUtc,
                    openedLastWriteTimeUtc);
                return existingIndex;
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

                if (!IsCurrentPathGeneration(filePath, openedGenerationToken))
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

    private static async Task AppendOffsetsAsync(
        FileStream stream,
        LineIndex existingIndex,
        long currentSize,
        FileEncoding encoding,
        CancellationToken ct)
    {
        var boundary = await ClassifyAppendBoundaryAsync(
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

        // Seek to where we left off and scan new bytes
        stream.Position = existingIndex.FileSize;
        var buffer = new byte[BufferSize];
        long position = existingIndex.FileSize;
        int bytesRead;
        var newlineScanState = boundary == AppendBoundary.PendingCarriageReturn
            ? NewlineScanState.CreatePendingCarriageReturn(existingIndex.FileSize)
            : new NewlineScanState();

        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            ScanNewlines(buffer, bytesRead, encoding, position, existingIndex.LineOffsets, ref newlineScanState);
            position += bytesRead;
        }

        FlushPendingNewline(existingIndex.LineOffsets, ref newlineScanState);
        TrimTrailingEmptyLine(existingIndex.LineOffsets, position);
    }

    private static bool RequiresRebuild(
        LineIndex existingIndex,
        FileGenerationToken openedGenerationToken,
        long currentSize)
    {
        if (currentSize < existingIndex.FileSize)
            return true;

        if (existingIndex.GenerationToken.IsKnown != openedGenerationToken.IsKnown)
            return true;

        return existingIndex.GenerationToken.IsKnown &&
               existingIndex.GenerationToken != openedGenerationToken;
    }

    private static FileGenerationToken ResolveGenerationToken(
        FileGenerationToken existingToken,
        FileGenerationToken openedToken)
        => existingToken.IsKnown && openedToken.IsKnown && existingToken == openedToken
            ? openedToken
            : FileGenerationToken.Unknown;

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
