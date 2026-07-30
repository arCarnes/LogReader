namespace LogReader.App.Services;

internal sealed class LogViewportCapacity
{
    internal const int DefaultLineCount = 50;

    private int _lineCount = DefaultLineCount;

    public int LineCount => Volatile.Read(ref _lineCount);

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

            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }
}
