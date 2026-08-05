namespace LogReader.Mcp;

using LogReader.Core.Interfaces;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public static class McpStdioHost
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var backend = new ArbitratingLogQueryBackend(
            async ct => await LiveLogIpcClientBackend.ConnectAsync(ct).ConfigureAwait(false),
            static () => new OwnedHeadlessLogQueryBackend());
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
