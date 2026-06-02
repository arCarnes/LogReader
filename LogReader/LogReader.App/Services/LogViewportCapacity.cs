namespace LogReader.App.Services;

internal sealed class LogViewportCapacity
{
    internal const int DefaultLineCount = 50;

    private int _lineCount = DefaultLineCount;
    private int _version;

    public int LineCount => Volatile.Read(ref _lineCount);

    public int Version => Volatile.Read(ref _version);

    public event EventHandler? Changed;

    public bool UpdateLineCount(int lineCount)
    {
        if (lineCount <= 0)
            return false;

        while (true)
        {
            var currentLineCount = LineCount;
            if (currentLineCount == lineCount)
                return false;

            if (Interlocked.CompareExchange(ref _lineCount, lineCount, currentLineCount) != currentLineCount)
                continue;

            Interlocked.Increment(ref _version);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }
}
