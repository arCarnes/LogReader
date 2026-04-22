namespace LogReader.Infrastructure.Services;

using System.Buffers;
using System.Text;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class ChunkedLogReaderService : ILogReaderService
{
    private const int BufferSize = 64 * 1024; // 64KB buffer

    public async Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
    {
        var index = new LineIndex { FilePath = filePath };
        index.LineOffsets.Add(0); // Seed first line candidate (trimmed for empty/BOM-only files)

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);

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

        TrimTrailingEmptyLine(index.LineOffsets, position);
        TrimEmptyFileLine(index.LineOffsets, position);

        index.FileSize = position;
        index.LineOffsets.Freeze();
        return index;
    }

    public async Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var currentSize = stream.Length;

        // File was truncated/rotated - rebuild entirely
        if (currentSize < existingIndex.FileSize)
        {
            existingIndex.Dispose();
            return await BuildIndexAsync(filePath, encoding, ct).ConfigureAwait(false);
        }

        // No new data
        if (currentSize == existingIndex.FileSize)
        {
            return existingIndex;
        }

        // Check if we need to add the start-of-new-data as a new line offset.
        if (existingIndex.LineOffsets.Count > 0)
        {
            var lastOffset = existingIndex.LineOffsets[^1];
            if (lastOffset < existingIndex.FileSize)
            {
                bool endsWithNewline = false;
                var nlBuf = new byte[2];
                if (encoding == FileEncoding.Utf16 && existingIndex.FileSize >= 2)
                {
                    stream.Position = existingIndex.FileSize - 2;
                    var nlRead = await stream.ReadAsync(nlBuf.AsMemory(0, 2), ct).ConfigureAwait(false);
                    endsWithNewline = nlRead == 2 && nlBuf[0] == 0x0A && nlBuf[1] == 0x00; // UTF-16 LE newline
                }
                else if (encoding == FileEncoding.Utf16Be && existingIndex.FileSize >= 2)
                {
                    stream.Position = existingIndex.FileSize - 2;
                    var nlRead = await stream.ReadAsync(nlBuf.AsMemory(0, 2), ct).ConfigureAwait(false);
                    endsWithNewline = nlRead == 2 && nlBuf[0] == 0x00 && nlBuf[1] == 0x0A; // UTF-16 BE newline
                }
                else if (encoding is not (FileEncoding.Utf16 or FileEncoding.Utf16Be))
                {
                    stream.Position = existingIndex.FileSize - 1;
                    var nlRead = await stream.ReadAsync(nlBuf.AsMemory(0, 1), ct).ConfigureAwait(false);
                    endsWithNewline = nlRead == 1 && nlBuf[0] == (byte)'\n';
                }

                if (endsWithNewline)
                {
                    existingIndex.LineOffsets.Add(existingIndex.FileSize);
                }
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
        var newlineScanState = new NewlineScanState();

        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            ScanNewlines(buffer, bytesRead, encoding, position, existingIndex.LineOffsets, ref newlineScanState);
            position += bytesRead;
        }

        TrimTrailingEmptyLine(existingIndex.LineOffsets, position);

        existingIndex.FileSize = position;
        return existingIndex;
    }

    public async Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
    {
        if (startLine < 0 || startLine >= index.LineCount || count <= 0)
            return Array.Empty<string>();

        int endLine = Math.Min(startLine + count, index.LineCount) - 1;
        long startOffset = index.LineOffsets[startLine];
        long endOffset = endLine + 1 < index.LineCount ? index.LineOffsets[endLine + 1] : index.FileSize;
        long byteCount = endOffset - startOffset;

        if (byteCount <= 0) return Array.Empty<string>();

        int targetLineCount = endLine - startLine + 1;
        var result = new List<string>(targetLineCount);
        var currentLine = new StringBuilder();

        var enc = EncodingHelper.GetEncoding(encoding);
        var decoder = enc.GetDecoder();
        var byteBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var charBuffer = ArrayPool<char>.Shared.Rent(enc.GetMaxCharCount(BufferSize));

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.Asynchronous);
        stream.Position = startOffset;

        try
        {
            long remaining = byteCount;
            while (remaining > 0 && result.Count < targetLineCount)
            {
                ct.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(byteBuffer.Length, remaining);
                int read = await stream.ReadAsync(byteBuffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0) break;

                remaining -= read;
                bool flush = remaining == 0;
                int byteIndex = 0;
                bool completed;

                do
                {
                    decoder.Convert(
                        byteBuffer, byteIndex, read - byteIndex,
                        charBuffer, 0, charBuffer.Length,
                        flush,
                        out int bytesUsed, out int charsUsed, out completed);
                    byteIndex += bytesUsed;

                    for (int i = 0; i < charsUsed; i++)
                    {
                        var ch = charBuffer[i];
                        if (ch == '\n')
                        {
                            var line = currentLine.ToString();
                            if (line.EndsWith('\r'))
                                line = line[..^1];
                            result.Add(line);
                            currentLine.Clear();

                            if (result.Count == targetLineCount)
                                break;
                        }
                        else
                        {
                            currentLine.Append(ch);
                        }
                    }
                } while (!completed && result.Count < targetLineCount);
            }

            if (result.Count < targetLineCount && currentLine.Length > 0)
            {
                var line = currentLine.ToString();
                if (line.EndsWith('\r'))
                    line = line[..^1];
                result.Add(line);
            }

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuffer);
            ArrayPool<char>.Shared.Return(charBuffer);
        }
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
            ScanUtf16Newlines(buffer, bytesRead, basePosition, offsets, ref state, firstByte: 0x0A, secondByte: 0x00);
        }
        else if (encoding == FileEncoding.Utf16Be)
        {
            ScanUtf16Newlines(buffer, bytesRead, basePosition, offsets, ref state, firstByte: 0x00, secondByte: 0x0A);
        }
        else
        {
            // UTF-8 / ANSI: scan for \n byte
            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == (byte)'\n')
                    offsets.Add(basePosition + i + 1);
            }
        }
    }

    private static void ScanUtf16Newlines(
        byte[] buffer,
        int bytesRead,
        long basePosition,
        MappedLineOffsets offsets,
        ref NewlineScanState state,
        byte firstByte,
        byte secondByte)
    {
        var startIndex = 0;
        if (state.HasPendingByte)
        {
            if (bytesRead > 0 && state.PendingByte == firstByte && buffer[0] == secondByte)
                offsets.Add(basePosition + 1);

            state = default;
            startIndex = 1;
        }

        var i = startIndex;
        for (; i < bytesRead - 1; i += 2)
        {
            if (buffer[i] == firstByte && buffer[i + 1] == secondByte)
                offsets.Add(basePosition + i + 2);
        }

        if (i < bytesRead)
            state = new NewlineScanState(buffer[i]);
    }

    internal readonly record struct NewlineScanState(byte PendingByte)
    {
        public bool HasPendingByte { get; } = true;

        public NewlineScanState() : this(default)
        {
            HasPendingByte = false;
        }
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
