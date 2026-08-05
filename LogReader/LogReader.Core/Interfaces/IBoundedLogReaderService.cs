namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Opt-in bounded indexing and random-access reads for non-interactive consumers.
/// Existing interactive callers continue to use <see cref="ILogReaderService"/>.
/// </summary>
public interface IBoundedLogReaderService
{
    Task<LineIndex> BuildBoundedIndexAsync(
        string filePath,
        FileEncoding encoding,
        int maximumLineCount,
        CancellationToken ct = default);

    Task<LineIndex> UpdateBoundedIndexAsync(
        string filePath,
        LineIndex existingIndex,
        FileEncoding encoding,
        int maximumLineCount,
        CancellationToken ct = default);

    Task<IReadOnlyList<BoundedIndexedLine>> ReadBoundedLinesAsync(
        string filePath,
        LineIndex index,
        int startLine,
        int count,
        FileEncoding encoding,
        int maximumCharactersPerLine,
        int maximumTotalCharacters,
        CancellationToken ct = default);

    Task<IReadOnlyList<BoundedIndexedLine>> ReadBoundedLinesAsync(
        string filePath,
        IndexedLogReadSnapshot snapshot,
        int maximumCharactersPerLine,
        int maximumTotalCharacters,
        CancellationToken ct = default)
        => throw new NotSupportedException("Bounded index snapshots are not supported by this reader.");
}
