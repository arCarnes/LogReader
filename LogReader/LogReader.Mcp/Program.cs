namespace LogReader.Mcp;

public static class Program
{
    public static int Main(string[] args)
    {
        if (!HasValidArguments(args))
        {
            Console.Error.WriteLine("WeezTail MCP server does not accept command-line arguments.");
            return 2;
        }

        return McpStdioHost.RunAsync().GetAwaiter().GetResult();
    }

    internal static bool HasValidArguments(IReadOnlyList<string>? args)
        => args is { Count: 0 };
}
