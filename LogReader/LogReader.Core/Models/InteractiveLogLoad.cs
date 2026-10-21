namespace LogReader.Core.Models;

internal static class InteractiveLogLoadLimits
{
    public const int PreviewBytes = 1024 * 1024;
    public const int PreviewValidationBytes = 384;
    public const int PreviewLines = 200;
    public const int PreviewLineCharacters = 4096;
    public const int PreviewCharacters = 200_000;
    public const int ProgressIntervalMilliseconds = 150;
    public const int ScanQuantumBytes = 4 * 1024 * 1024;
}

internal readonly record struct InteractiveIndexProgress(long ProcessedBytes, long TotalBytes, int Attempt, bool IsComplete);

internal sealed record RecentLogPreviewLine(long ByteOffset, string Text, bool IsTruncated);

internal sealed record RecentLogPreview(
    IReadOnlyList<RecentLogPreviewLine> Lines,
    FileGenerationToken Generation,
    long CapturedLength,
    FileEncoding Encoding,
    int BytesRead,
    string? UnavailableReason = null)
{
    public bool IsAvailable => UnavailableReason == null;
}
