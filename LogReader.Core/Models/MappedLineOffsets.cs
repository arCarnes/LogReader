using System.IO.MemoryMappedFiles;

namespace LogReader.Core.Models;

/// <summary>
/// A collection of Int64 line offsets that transitions from an in-memory list
/// (build mode) to a memory-mapped temp file (frozen mode) to reduce GC pressure.
/// </summary>
public sealed class MappedLineOffsets : IDisposable
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "LogReader", "idx");

    // Build-mode state
    private List<long>? _buildList;

    // Frozen-mode state
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private int _frozenCount;
    private List<long>? _overflow;
    private string? _tempFilePath;

    private bool _disposed;

    public MappedLineOffsets()
    {
        _buildList = new List<long>();
    }

    public int Count
    {
        get
        {
            if (_buildList != null) return _buildList.Count;
            return _frozenCount + (_overflow?.Count ?? 0);
        }
    }

    public long this[int index]
    {
        get
        {
            if (_buildList != null) return _buildList[index];
            if (index < _frozenCount)
                return _accessor!.ReadInt64(index * 8L);
            return _overflow![index - _frozenCount];
        }
        set
        {
            if (_buildList != null) { _buildList[index] = value; return; }
            if (index < _frozenCount)
                throw new InvalidOperationException("Cannot modify frozen entries.");
            _overflow![index - _frozenCount] = value;
        }
    }

    public void Add(long value)
    {
        if (_buildList != null) { _buildList.Add(value); return; }
        _overflow!.Add(value);
    }

    public void RemoveAt(int index)
    {
        if (_buildList != null) { _buildList.RemoveAt(index); return; }

        int totalCount = _frozenCount + _overflow!.Count;
        if (index != totalCount - 1)
            throw new InvalidOperationException("Only removal of the last element is supported after freeze.");
        if (index >= _frozenCount)
            _overflow.RemoveAt(index - _frozenCount);
        else
            _frozenCount--; // shrink frozen view (last frozen entry)
    }

    /// <summary>
    /// Transitions from build mode to frozen mode. Writes all offsets to a local
    /// temp file and memory-maps it. The build list is released for GC.
    /// </summary>
    public void Freeze()
    {
        if (_buildList == null)
            throw new InvalidOperationException("Already frozen.");

        int count = _buildList.Count;

        if (count == 0)
        {
            // Empty index — no file needed, just switch to frozen mode
            _frozenCount = 0;
            _overflow = new List<long>();
            _buildList = null;
            return;
        }

        Directory.CreateDirectory(TempDir);
        _tempFilePath = Path.Combine(TempDir, $"idx_{Guid.NewGuid():N}.bin");

        long byteLength = count * 8L;

        using (var fs = new FileStream(_tempFilePath, FileMode.Create, FileAccess.Write,
                   FileShare.None, 64 * 1024, FileOptions.SequentialScan))
        {
            const int chunkSize = 8192; // longs per chunk
            var buffer = new byte[chunkSize * 8];
            int written = 0;
            while (written < count)
            {
                int batch = Math.Min(chunkSize, count - written);
                for (int i = 0; i < batch; i++)
                {
                    BitConverter.TryWriteBytes(buffer.AsSpan(i * 8, 8), _buildList[written + i]);
                }
                fs.Write(buffer, 0, batch * 8);
                written += batch;
            }
        }

        _mmf = MemoryMappedFile.CreateFromFile(_tempFilePath, FileMode.Open,
                   null, byteLength, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, byteLength, MemoryMappedFileAccess.Read);

        _frozenCount = count;
        _overflow = new List<long>();
        _buildList = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accessor?.Dispose();
        _mmf?.Dispose();
        TryDeleteFile(_tempFilePath);
        _buildList = null;
        _overflow = null;
    }

    private static void TryDeleteFile(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch { }
    }
}
