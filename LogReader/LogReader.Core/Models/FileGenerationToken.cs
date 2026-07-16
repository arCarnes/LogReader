namespace LogReader.Core.Models;

/// <summary>
/// Opaque, best-effort identity for one durable file generation.
/// </summary>
internal readonly record struct FileGenerationToken(
    bool IsKnown,
    ulong VolumeId,
    ulong FileId)
{
    public static FileGenerationToken Unknown { get; } = default;

    public static FileGenerationToken Create(ulong volumeId, ulong fileId)
        => new(true, volumeId, fileId);
}
