namespace LogReader.Mcp;

using LogReader.Core.Interfaces;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public static class McpStdioHost
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var catalogReader = new PersistedDashboardSnapshotReader();
        var logReader = new ChunkedLogReaderService();
        var encodingDetection = new FileEncodingDetectionService();
        using var backend = new HeadlessLogQueryBackend(
            catalogReader,
            new SearchService(),
            encodingDetection,
            logReader,
            new IndexedLogSessionCache(logReader, encodingDetection));
        return await RunAsync(backend, cancellationToken).ConfigureAwait(false);
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
