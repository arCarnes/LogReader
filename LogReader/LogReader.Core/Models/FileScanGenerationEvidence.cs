namespace LogReader.Core.Models;

internal enum FileGenerationCorrelation
{
    Unknown,
    Current,
    Stale
}

internal readonly record struct FileScanGenerationEvidence(
    FileGenerationToken Token,
    FileGenerationCorrelation Correlation)
{
    public static FileScanGenerationEvidence Unknown { get; } =
        new(FileGenerationToken.Unknown, FileGenerationCorrelation.Unknown);
}
