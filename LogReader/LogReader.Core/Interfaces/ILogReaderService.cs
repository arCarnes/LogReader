namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Reads log files using a byte-offset line index for random access.
/// </summary>
public interface ILogReaderService
{
    /// <summary>Scans the file and builds a line-offset index from scratch.</summary>
    Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default);

    /// <summary>Extends an existing index with any new bytes appended since the last scan.</summary>
    Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default);

    /// <summary>Extends an existing index, using a best-effort hint from the file monitor.</summary>
    async Task<LineIndex> UpdateIndexAsync(
        string filePath,
        LineIndex existingIndex,
        FileEncoding encoding,
        FileChangeHint changeHint,
        CancellationToken ct = default)
    {
        if (changeHint == FileChangeHint.None)
        {
            return await UpdateIndexAsync(
                filePath,
                existingIndex,
                encoding,
                ct).ConfigureAwait(false);
        }

        var rebuiltIndex = await BuildIndexAsync(
            filePath,
            encoding,
            ct).ConfigureAwait(false);
        rebuiltIndex.ReplacesPriorGeneration = true;
        return rebuiltIndex;
    }

    /// <summary>Reads a range of lines using the prebuilt index.</summary>
    Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default);

    /// <summary>Reads a single line by its zero-based line number.</summary>
    Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default);
}
