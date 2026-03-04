namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Monitors log files for new content and rotation events via polling.
/// </summary>
public interface IFileTailService : IDisposable
{
    /// <summary>Raised when new lines are appended to a tailed file.</summary>
    event EventHandler<TailEventArgs>? LinesAppended;

    /// <summary>Raised when a file is rotated (truncated, deleted and recreated, or replaced).</summary>
    event EventHandler<FileRotatedEventArgs>? FileRotated;

    /// <summary>Begins polling the file for changes.</summary>
    void StartTailing(string filePath, FileEncoding encoding);

    /// <summary>Stops polling a specific file.</summary>
    void StopTailing(string filePath);

    /// <summary>Stops polling all files.</summary>
    void StopAll();
}

public class TailEventArgs : EventArgs
{
    public string FilePath { get; init; } = string.Empty;
}

public class FileRotatedEventArgs : EventArgs
{
    public string FilePath { get; init; } = string.Empty;
}
