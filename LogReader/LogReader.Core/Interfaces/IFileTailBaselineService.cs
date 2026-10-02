namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>Starts new monitors from committed index state rather than a later file probe.</summary>
internal interface IFileTailBaselineService
{
    void StartTailing(string filePath, FileEncoding encoding, FileTailBaseline baseline, int pollingIntervalMs);
}

internal readonly record struct FileTailBaseline(long FileSize, FileGenerationToken GenerationToken);
