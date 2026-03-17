namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

public interface ILogTimestampNavigationService
{
    Task<TimestampNavigationResult> FindNearestLineAsync(
        string filePath,
        ParsedTimestamp targetTimestamp,
        FileEncoding encoding,
        CancellationToken ct = default);
}
