namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

public interface ILogQueryBackend : IDisposable
{
    Task<LogOperationEnvelope<ConfiguredLogTreeResult>> ListLogTreeAsync(
        ConfiguredLogTreeRequest request,
        CancellationToken ct = default);

    Task<LogOperationEnvelope<LogSearchResult>> SearchLogsAsync(
        LogSearchQuery request,
        CancellationToken ct = default);

    Task<LogOperationEnvelope<LogCountResult>> CountLogsAsync(
        LogCountQuery request,
        CancellationToken ct = default);

    Task<LogOperationEnvelope<LogReadLinesResult>> ReadLogLinesAsync(
        LogReadLinesQuery request,
        CancellationToken ct = default);

    Task<LogOperationEnvelope<LogReadTailResult>> ReadLogTailAsync(
        LogReadTailQuery request,
        CancellationToken ct = default);

    Task<LogOperationEnvelope<LogQueryStatus>> GetStatusAsync(CancellationToken ct = default);
}
