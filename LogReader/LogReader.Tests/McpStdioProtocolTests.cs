namespace LogReader.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using LogReader.App;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

public sealed class McpStdioProtocolTests
{
    [Fact]
    public async Task Executable_InitializesAndCallsEveryToolWithoutNonProtocolOutput()
    {
        var standardError = new ConcurrentQueue<string>();
        var executablePath = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
        var transportOptions = new StdioClientTransportOptions
        {
            Name = "weeztail-integration-test",
            Command = executablePath,
            Arguments = ["--mcp-stdio"],
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            ShutdownTimeout = TimeSpan.FromMilliseconds(500),
            StandardErrorLines = standardError.Enqueue
        };
        var transport = new StdioClientTransport(transportOptions, loggerFactory: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = await McpClient.CreateAsync(
            transport,
            clientOptions: null,
            loggerFactory: null,
            cancellation.Token);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellation.Token);
            var list = await client.CallToolAsync("list_log_tree", arguments: null, cancellationToken: cancellation.Token);
            var search = await client.CallToolAsync(
                "search_logs",
                new Dictionary<string, object?>
                {
                    ["targets"] = new[] { new { kind = "logFile", id = "missing-file" } },
                    ["query"] = "needle"
                },
                cancellationToken: cancellation.Token);
            var read = await client.CallToolAsync(
                "read_log_lines",
                new Dictionary<string, object?> { ["fileId"] = "missing-file" },
                cancellationToken: cancellation.Token);
            var tail = await client.CallToolAsync(
                "read_log_tail",
                new Dictionary<string, object?> { ["fileId"] = "missing-file" },
                cancellationToken: cancellation.Token);
            var status = await client.CallToolAsync("server_status", arguments: null, cancellationToken: cancellation.Token);

            Assert.Equal("weeztail", client.ServerInfo.Name);
            Assert.NotNull(client.ServerCapabilities.Tools);
            Assert.Null(client.ServerCapabilities.Resources);
            Assert.Null(client.ServerCapabilities.Prompts);
            Assert.Equal(
                ["list_log_tree", "read_log_lines", "read_log_tail", "search_logs", "server_status"],
                tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
            Assert.All([list, search, read, tail, status], AssertStructuredSuccess);
            Assert.Empty(standardError);
        }
        finally
        {
            await client.DisposeAsync();
        }

        var completion = Assert.IsType<StdioClientCompletionDetails>(await client.Completion);
        Assert.Null(completion.Exception);
        Assert.Empty(completion.StandardErrorTail ?? []);
    }

    [Fact]
    public async Task Executable_UnknownToolAndMalformedArgumentsUseProtocolErrors()
    {
        var executablePath = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "weeztail-invalid-request-test",
                Command = executablePath,
                Arguments = ["--mcp-stdio"],
                WorkingDirectory = Path.GetDirectoryName(executablePath),
                ShutdownTimeout = TimeSpan.FromMilliseconds(500)
            },
            loggerFactory: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(
            transport,
            clientOptions: null,
            loggerFactory: null,
            cancellation.Token);

        var malformed = await client.CallToolAsync(
            "search_logs",
            new Dictionary<string, object?>
            {
                ["targets"] = Array.Empty<object>()
            },
            cancellationToken: cancellation.Token);
        var integerEnum = await client.CallToolAsync(
            "search_logs",
            new Dictionary<string, object?>
            {
                ["targets"] = new[] { new { kind = 2, id = "missing-file" } },
                ["query"] = "needle"
            },
            cancellationToken: cancellation.Token);
        var unknownArgument = await client.CallToolAsync(
            "server_status",
            new Dictionary<string, object?> { ["unexpected"] = true },
            cancellationToken: cancellation.Token);
        var overLimit = await client.CallToolAsync(
            "read_log_lines",
            new Dictionary<string, object?>
            {
                ["fileId"] = "missing-file",
                ["count"] = 1_001
            },
            cancellationToken: cancellation.Token);

        Assert.True(malformed.IsError);
        Assert.True(integerEnum.IsError);
        AssertStructuredSuccess(unknownArgument);
        Assert.NotEqual(true, overLimit.IsError);
        Assert.Contains(
            overLimit.StructuredContent!.Value.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "invalid_line_count");
        await Assert.ThrowsAnyAsync<McpException>(async () =>
            await client.CallToolAsync("unknown_tool", arguments: null, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Executable_ExitsCleanlyWhenStdinClosesBeforeInitialize()
    {
        var executablePath = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--mcp-stdio",
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start WeezTail.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        process.StandardInput.Close();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(cancellation.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await stdout);
        Assert.Empty(await stderr);
    }

    private static void AssertStructuredSuccess(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(1, result.StructuredContent.Value.GetProperty("schemaVersion").GetInt32());
    }
}
