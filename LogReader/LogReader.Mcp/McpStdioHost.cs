namespace LogReader.Mcp;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public static class McpStdioHost
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var options = new McpServerOptions
            {
                ServerInfo = new Implementation
                {
                    Name = "weeztail",
                    Version = ResolveVersion()
                },
                ToolCollection = []
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
