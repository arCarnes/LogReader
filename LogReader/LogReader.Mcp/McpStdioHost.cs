namespace LogReader.Mcp;

using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public static class McpStdioHost
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            CleanupIndexCacheDirectory();
            using var backend = new OwnedHeadlessLogQueryBackend();
            return await RunAsync(backend, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("WeezTail MCP server could not start or continue.");
            return 1;
        }
    }

    internal static void CleanupIndexCacheDirectory()
    {
        LineIndexCacheMaintenance.CleanupOrphanedOwners();
    }

    internal static async Task<int> RunAsync(
        ILogQueryBackend backend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        try
        {
            var options = new McpServerOptions
            {
                ServerInfo = new Implementation
                {
                    Name = "weeztail",
                    Version = ResolveVersion()
                },
                ToolCollection = McpLogTools.CreateToolCollection(backend)
            };

            await using var transport = new StdioServerTransport(options, loggerFactory: null);
            await using var server = McpServer.Create(transport, options);
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("WeezTail MCP server could not start or continue.");
            return 1;
        }
    }

    private static string ResolveVersion()
    {
        return typeof(McpStdioHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

internal sealed class OwnedHeadlessLogQueryBackend : ILogQueryBackend
{
    private readonly PersistedDashboardSnapshotReader _catalog;
    private readonly HeadlessLogQueryBackend _backend;

    public OwnedHeadlessLogQueryBackend()
    {
        _catalog = new PersistedDashboardSnapshotReader();
        var logReader = new ChunkedLogReaderService();
        var encodingDetection = new FileEncodingDetectionService();
        _backend = new HeadlessLogQueryBackend(
            _catalog,
            new SearchService(),
            encodingDetection,
            logReader,
            new IndexedLogSessionCache(logReader, encodingDetection));
    }

    public Task<LogOperationEnvelope<ConfiguredLogTreeResult>> ListLogTreeAsync(
        ConfiguredLogTreeRequest request,
        CancellationToken ct = default)
        => _backend.ListLogTreeAsync(request, ct);

    public Task<LogOperationEnvelope<LogSearchResult>> SearchLogsAsync(
        LogSearchQuery request,
        CancellationToken ct = default)
        => _backend.SearchLogsAsync(request, ct);

    public Task<LogOperationEnvelope<LogReadLinesResult>> ReadLogLinesAsync(
        LogReadLinesQuery request,
        CancellationToken ct = default)
        => _backend.ReadLogLinesAsync(request, ct);

    public Task<LogOperationEnvelope<LogReadTailResult>> ReadLogTailAsync(
        LogReadTailQuery request,
        CancellationToken ct = default)
        => _backend.ReadLogTailAsync(request, ct);

    public Task<LogOperationEnvelope<LogQueryStatus>> GetStatusAsync(CancellationToken ct = default)
        => _backend.GetStatusAsync(ct);

    public void Dispose()
    {
        _backend.Dispose();
        _catalog.Dispose();
    }
}
