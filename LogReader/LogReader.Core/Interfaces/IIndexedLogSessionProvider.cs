namespace LogReader.Core.Interfaces;

using System.Collections.Immutable;
using LogReader.Core.Models;

/// <summary>
/// Supplies process-owned line indexes to bounded query code without exposing their backing storage.
/// </summary>
public interface IIndexedLogSessionProvider : IDisposable
{
    IndexedLogSessionProviderSnapshot GetProviderSnapshot();

    IIndexedLogSessionLease AcquireSession(
        string filePath,
        FileEncoding requestedEncoding = FileEncoding.Auto);
}

public interface IIndexedLogSessionLease : IDisposable
{
    string FilePath { get; }

    FileEncoding Encoding { get; }

    Task<T> UseCurrentIndexAsync<T>(
        Func<LineIndex, FileEncoding, CancellationToken, Task<T>> operation,
        CancellationToken ct = default);

    Task<IndexedLogReadSnapshot> CaptureCurrentIndexAsync(
        IReadOnlyList<IndexedLogReadRange> ranges,
        CancellationToken ct = default);

    Task<bool> RevalidateCurrentIndexAsync(
        IndexedLogReadSnapshot snapshot,
        CancellationToken ct = default);
}

public sealed record IndexedLogReadRange(int StartLine, int Count);

public sealed record IndexedLogLineBounds(int LineNumber, long StartOffset, long EndOffset);

/// <summary>
/// A bounded heap snapshot of line offsets copied from a process-owned index.
/// It owns no mapped memory and remains safe to use after the index read lease is released.
/// </summary>
public sealed class IndexedLogReadSnapshot
{
    public const int MaximumCapturedLines = 25_000;

    private IndexedLogReadSnapshot(
        LineIndex sourceIndex,
        FileEncoding encoding,
        ImmutableArray<IndexedLogLineBounds> lines)
    {
        SourceIndex = sourceIndex;
        Encoding = encoding;
        TotalLineCount = sourceIndex.LineCount;
        FileSize = sourceIndex.FileSize;
        LastWriteTimeUtc = sourceIndex.LastWriteTimeUtc;
        GenerationToken = sourceIndex.GenerationToken;
        Lines = lines;
    }

    public FileEncoding Encoding { get; }

    public int TotalLineCount { get; }

    public long FileSize { get; }

    public DateTime LastWriteTimeUtc { get; }

    public ImmutableArray<IndexedLogLineBounds> Lines { get; }

    internal LineIndex SourceIndex { get; }

    internal FileGenerationToken GenerationToken { get; }

    internal static IndexedLogReadSnapshot Capture(
        LineIndex index,
        FileEncoding encoding,
        IReadOnlyList<IndexedLogReadRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(ranges);
        var selected = new SortedDictionary<int, IndexedLogLineBounds>();
        foreach (var range in ranges)
        {
            ArgumentNullException.ThrowIfNull(range);
            if (range.StartLine < 0)
                throw new ArgumentOutOfRangeException(nameof(ranges));
            if (range.Count < 0)
                throw new ArgumentOutOfRangeException(nameof(ranges));
            if (range.Count == 0 || range.StartLine >= index.LineCount)
                continue;

            var endExclusive = (int)Math.Min(
                index.LineCount,
                Math.Min(int.MaxValue, (long)range.StartLine + range.Count));
            for (var lineNumber = range.StartLine; lineNumber < endExclusive; lineNumber++)
            {
                if (selected.ContainsKey(lineNumber))
                    continue;
                if (selected.Count >= MaximumCapturedLines)
                    throw new ArgumentOutOfRangeException(nameof(ranges), "Too many indexed lines were requested.");

                var startOffset = index.LineOffsets[lineNumber];
                var endOffset = lineNumber + 1 < index.LineCount
                    ? index.LineOffsets[lineNumber + 1]
                    : index.FileSize;
                selected.Add(
                    lineNumber,
                    new IndexedLogLineBounds(lineNumber, startOffset, Math.Max(startOffset, endOffset)));
            }
        }

        return new IndexedLogReadSnapshot(index, encoding, selected.Values.ToImmutableArray());
    }

    internal bool HasSameSourceAs(IndexedLogReadSnapshot other)
        => ReferenceEquals(SourceIndex, other.SourceIndex) &&
           GenerationToken == other.GenerationToken;

    public bool TryGetLineBounds(int lineNumber, out IndexedLogLineBounds? bounds)
    {
        var index = Lines.BinarySearch(
            new IndexedLogLineBounds(lineNumber, 0, 0),
            IndexedLogLineBoundsComparer.Instance);
        if (index >= 0)
        {
            bounds = Lines[index];
            return true;
        }

        bounds = null;
        return false;
    }

    private sealed class IndexedLogLineBoundsComparer : IComparer<IndexedLogLineBounds>
    {
        public static IndexedLogLineBoundsComparer Instance { get; } = new();

        public int Compare(IndexedLogLineBounds? x, IndexedLogLineBounds? y)
            => (x?.LineNumber ?? -1).CompareTo(y?.LineNumber ?? -1);
    }
}

public sealed record IndexedLogSessionProviderSnapshot(
    int ActiveSessions,
    int RetainedSessions,
    int MappedLineOffsets,
    int MaximumSessions,
    int MaximumMappedLineOffsets,
    TimeSpan WarmRetentionDuration);
