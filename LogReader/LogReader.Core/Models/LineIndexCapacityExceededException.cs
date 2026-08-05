namespace LogReader.Core.Models;

/// <summary>
/// Raised when a bounded line-index build would exceed its admitted offset capacity.
/// </summary>
public sealed class LineIndexCapacityExceededException : IOException
{
    public LineIndexCapacityExceededException(int maximumLineCount)
        : base($"The line index exceeds the admitted capacity of {maximumLineCount} lines.")
    {
        MaximumLineCount = maximumLineCount;
    }

    public int MaximumLineCount { get; }
}
