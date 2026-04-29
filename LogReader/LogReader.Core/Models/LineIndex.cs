namespace LogReader.Core.Models;

public class LineIndex : IDisposable
{
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public ulong ContentFingerprint { get; set; }
    public MappedLineOffsets LineOffsets { get; set; } = new();
    public int LineCount => LineOffsets.Count;

    public void Dispose()
    {
        LineOffsets.Dispose();
    }
}
