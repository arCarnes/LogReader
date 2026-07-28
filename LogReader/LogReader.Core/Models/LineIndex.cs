namespace LogReader.Core.Models;

public class LineIndex : IDisposable
{
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public MappedLineOffsets LineOffsets { get; set; } = new();
    public int LineCount => LineOffsets.Count;
    internal FileGenerationToken GenerationToken { get; set; }
    internal bool ReplacesPriorGeneration { get; set; }
    internal long AutomaticReloadNotBeforeTimestamp { get; set; }

    internal void ResetAutomaticReloadDelay()
        => AutomaticReloadNotBeforeTimestamp = 0;

    public void Dispose()
    {
        LineOffsets.Dispose();
    }
}
