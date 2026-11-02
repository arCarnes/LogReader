namespace LogReader.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LogReader.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

public sealed class McpStdioProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executable_InitializesAndCallsEveryToolWithoutNonProtocolOutput(bool populatedCatalog)
    {
        await using var deployment = new Deployment(populatedCatalog);
        var standardError = new ConcurrentQueue<string>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = await deployment.CreateClientAsync("weeztail-integration-test", cancellation.Token, standardError.Enqueue);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellation.Token);
            var list = await client.CallToolAsync("list_log_tree", arguments: null, cancellationToken: cancellation.Token);
            var rejectedList = await client.CallToolAsync("list_log_tree",
                new Dictionary<string, object?> { ["rootGroupId"] = "missing-dashboard" },
                cancellationToken: cancellation.Token);
            var search = await client.CallToolAsync(
                "search_logs",
                new Dictionary<string, object?>
                {
                    ["targets"] = new[] { new { kind = "logFile", id = "missing-file" } },
                    ["query"] = "needle",
                    ["resultMode"] = "countsOnly"
                },
                cancellationToken: cancellation.Token);
            var count = await client.CallToolAsync(
                "count_logs",
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
            var monitor = await client.CallToolAsync("monitor_logs",
                new Dictionary<string, object?> { ["targets"] = new[] { new { kind = "logFile", id = "missing-file" } } },
                cancellationToken: cancellation.Token);
            var status = await client.CallToolAsync("server_status", arguments: null, cancellationToken: cancellation.Token);

            Assert.Equal("weeztail", client.ServerInfo.Name);
            Assert.NotNull(client.ServerInstructions);
            Assert.InRange(client.ServerInstructions.Length, 1, 512);
            Assert.Contains("untrusted", client.ServerInstructions, StringComparison.Ordinal);
            Assert.NotNull(client.ServerCapabilities.Tools);
            Assert.Null(client.ServerCapabilities.Resources);
            Assert.Null(client.ServerCapabilities.Prompts);
            Assert.Equal(
                ["count_logs", "list_log_tree", "monitor_logs", "read_log_lines", "read_log_tail", "search_logs", "server_status"],
                tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
            AssertStructuredSuccess(status);
            AssertStructuredSuccess(list);
            var discovery = list.StructuredContent!.Value;
            Assert.Empty(discovery.GetProperty("errors").EnumerateArray());
            var tree = discovery.GetProperty("result");
            Assert.Empty(tree.GetProperty("errors").EnumerateArray());
            Assert.Equal(populatedCatalog ? new[] { "fixture-dashboard", "fixture-file" } : [],
                tree.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("id").GetString()));

            Assert.True(rejectedList.IsError);
            var rejectedDiscovery = rejectedList.StructuredContent!.Value;
            Assert.Equal("unknown_target", Assert.Single(rejectedDiscovery.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());
            var rejectedTree = rejectedDiscovery.GetProperty("result");
            Assert.Empty(rejectedTree.GetProperty("nodes").EnumerateArray());
            Assert.Equal("unknown_target", Assert.Single(rejectedTree.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());
            Assert.Equal(tree.GetProperty("catalogRevision").GetString(), rejectedTree.GetProperty("catalogRevision").GetString());

            Assert.All([search, count, read, tail, monitor], response =>
            {
                Assert.True(response.IsError);
                Assert.Equal("unknown_target", Assert.Single(response.StructuredContent!.Value.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());
                Assert.False(response.StructuredContent.Value.TryGetProperty("result", out _));
            });
            Assert.Empty(standardError);
        }
        finally
        {
            await deployment.CloseClientAsync(client);
        }

        var completion = Assert.IsType<StdioClientCompletionDetails>(await client.Completion);
        Assert.Null(completion.Exception);
        Assert.Empty(completion.StandardErrorTail ?? []);
    }

    [Fact]
    public async Task Executable_TwoClientsRemainIndependentWhenOneCloses()
    {
        await using var deployment = new Deployment();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clients = new List<McpClient>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                clients.Add(await deployment.CreateClientAsync($"weeztail-concurrent-{i}", cancellation.Token));
            }
            var statuses = await Task.WhenAll(clients.Select(client => client.CallToolAsync(
                "server_status", arguments: null, cancellationToken: cancellation.Token).AsTask()));
            Assert.All(statuses, AssertStructuredSuccess);
            await deployment.CloseClientAsync(clients[0]);
            var completion = Assert.IsType<StdioClientCompletionDetails>(await clients[0].Completion);
            Assert.Null(completion.Exception);
            clients.RemoveAt(0);

            AssertStructuredSuccess(await clients[0].CallToolAsync(
                "server_status", arguments: null, cancellationToken: cancellation.Token));
        }
        finally
        {
            foreach (var client in clients)
                await deployment.CloseClientAsync(client);
        }
    }

    [Fact]
    public async Task Executable_UnknownToolAndMalformedArgumentsUseProtocolErrors()
    {
        await using var deployment = new Deployment();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = await deployment.CreateClientAsync("weeztail-invalid-request-test", cancellation.Token);

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
        Assert.True(overLimit.IsError);
        Assert.Contains(
            overLimit.StructuredContent!.Value.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "invalid_line_count");
        await Assert.ThrowsAnyAsync<McpException>(async () =>
            await client.CallToolAsync("unknown_tool", arguments: null, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Executable_ExitsCleanlyWhenStdinClosesBeforeInitialize()
    {
        await using var deployment = new Deployment();
        var startInfo = new ProcessStartInfo
        {
            FileName = deployment.ExecutablePath,
            WorkingDirectory = deployment.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the WeezTail MCP server.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            process.StandardInput.Close();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cancellation.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stdout);
            Assert.Empty(await stderr);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static void AssertStructuredSuccess(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(4, result.StructuredContent.Value.GetProperty("schemaVersion").GetInt32());
    }

    private sealed class Deployment : IAsyncDisposable
    {
        private readonly List<McpClient> _clients = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WeezTail-mcp-protocol-tests", Guid.NewGuid().ToString("N"));
        public string ExecutablePath => Path.Combine(Root, "WeezTail.Mcp.exe");

        public Deployment(bool populatedCatalog = false)
        {
            Directory.CreateDirectory(Root);
            try
            {
                var source = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
                File.Copy(Path.Combine(source, "WeezTail.Mcp.exe"), ExecutablePath);
                foreach (var file in Directory.EnumerateFiles(source))
                {
                    if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                        File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
                }
                File.WriteAllText(Path.Combine(Root, "WeezTail.install.json"),
                    """{"installMode":"Portable","storageMode":"ExeDirectory"}""");
                var data = Directory.CreateDirectory(Path.Combine(Root, "Data")).FullName;
                var logPath = Path.Combine(Root, "fixture.log");
                if (populatedCatalog)
                    File.WriteAllText(logPath, "2026-11-02 10:00:00 needle\n");
                WriteStore(Path.Combine(data, "loggroups.json"), populatedCatalog
                    ? new[] { new { id = "fixture-dashboard", name = "Fixture", sortOrder = 0, kind = "dashboard", fileIds = new[] { "fixture-file" } } } : []);
                WriteStore(Path.Combine(data, "logfiles.json"), populatedCatalog
                    ? new[] { new { id = "fixture-file", filePath = logPath } } : []);
                WriteStore(Path.Combine(data, "settings.json"), new { dateRollingPatterns = Array.Empty<object>() });
            }
            catch
            {
                Directory.Delete(Root, recursive: true);
                throw;
            }
        }

        private static void WriteStore<T>(string path, T data)
            => File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 1, data }));

        public async Task<McpClient> CreateClientAsync(string name, CancellationToken ct, Action<string>? standardErrorLines = null)
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = name,
                Command = ExecutablePath,
                Arguments = [],
                WorkingDirectory = Root,
                ShutdownTimeout = TimeSpan.FromMilliseconds(500),
                StandardErrorLines = standardErrorLines
            }, loggerFactory: null);
            // The SDK owns the child process, including cleanup if initialization fails.
            var client = await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: null, ct);
            _clients.Add(client);
            return client;
        }

        public async Task CloseClientAsync(McpClient client)
        {
            await client.DisposeAsync();
            await client.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            _clients.Remove(client);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients.ToArray())
                await CloseClientAsync(client);
            Directory.Delete(Root, recursive: true);
        }
    }
}
