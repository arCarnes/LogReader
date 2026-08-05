namespace LogReader.App;

using LogReader.Mcp;

internal static class Program
{
    private const string McpStdioArgument = "--mcp-stdio";

    [STAThread]
    public static int Main(string[] args)
    {
        if (IsMcpStdioMode(args))
            return McpStdioHost.RunAsync().GetAwaiter().GetResult();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    internal static bool IsMcpStdioMode(IReadOnlyList<string>? args)
    {
        return args is { Count: 1 } &&
               string.Equals(args[0], McpStdioArgument, StringComparison.Ordinal);
    }
}
