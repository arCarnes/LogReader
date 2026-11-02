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
    public async Task Executable_MonitorWaitsForSplitUtf8CharacterAndReplaysPendingBatch()
    {
        await using var deployment = new Deployment(populatedCatalog: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = await deployment.CreateClientAsync("weeztail-split-utf8-test", cancellation.Token);
        var path = Path.Combine(deployment.Root, "fixture.log");
        var offset = new FileInfo(path).Length;
        async Task<JsonElement> MonitorAsync(Dictionary<string, object?> arguments)
        {
            var response = await client.CallToolAsync("monitor_logs", arguments, cancellationToken: cancellation.Token);
            AssertStructuredSuccess(response);
            return response.StructuredContent!.Value;
        }
        async Task AppendAsync(byte[] bytes)
        {
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            await stream.WriteAsync(bytes, cancellation.Token);
        }

        var initial = await MonitorAsync(new() { ["targets"] = new[] { new { kind = "logFile", id = "fixture-file" } } });
        await AppendAsync([0xC3]);
        var request = new Dictionary<string, object?> { ["cursor"] = initial.GetProperty("result").GetProperty("nextCursor").GetString() };
        var pending = await MonitorAsync(request);
        Assert.Equal(pending.GetRawText(), (await MonitorAsync(request)).GetRawText());
        var result = pending.GetProperty("result");
        Assert.Equal("observing", result.GetProperty("state").GetString());
        Assert.Equal(1, result.GetProperty("backlogFileCount").GetInt32());
        Assert.False(result.GetProperty("isCaughtUp").GetBoolean());
        Assert.False(result.GetProperty("isIdle").GetBoolean());
        Assert.Equal(0, result.GetProperty("examinedLineCount").GetInt32());
        Assert.Empty(result.GetProperty("files")[0].GetProperty("events").EnumerateArray());
        await AppendAsync([0xA9, 10]);
        var completed = (await MonitorAsync(new() { ["cursor"] = result.GetProperty("nextCursor").GetString() })).GetProperty("result");
        var item = Assert.Single(completed.GetProperty("files")[0].GetProperty("events").EnumerateArray());
        Assert.Equal("appended", item.GetProperty("kind").GetString());
        Assert.Equal("é", item.GetProperty("text").GetString());
        Assert.Equal(offset, item.GetProperty("byteOffset").GetInt64());
        Assert.True(completed.GetProperty("isCaughtUp").GetBoolean());
        var released = await MonitorAsync(new() { ["cursor"] = completed.GetProperty("nextCursor").GetString(), ["release"] = true });
        Assert.Equal("released", released.GetProperty("result").GetProperty("state").GetString());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deployment_TransportCompletionDoesNotReplaceWaitingForChildExit(bool cancelWait)
    {
        await using var deployment = new Deployment();
        var source = Path.GetDirectoryName(typeof(LogReader.Launcher.TestHost.Program).Assembly.Location)!;
        var executable = Path.Combine(deployment.Root, "LogReader.Launcher.TestHost.exe");
        File.Copy(Path.Combine(source, "LogReader.Launcher.TestHost.exe"), executable);
        File.WriteAllText(Path.Combine(deployment.Root, "mode.txt"), "hold-file");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            WorkingDirectory = deployment.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start the gated shutdown fixture.");
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ready = process.StandardOutput.ReadLineAsync(cancellation.Token).AsTask();
        Task? exit = null;
        try
        {
            Assert.Equal("ready", await ready);
            // Model SDK completion while the child still holds the deployment DLL.
            var completion = new StdioClientCompletionDetails { ProcessId = process.Id };
            exit = Deployment.WaitForProcessExitAsync(completion, cancellation.Token);
            Assert.False(exit.IsCompleted);
            Assert.Throws<IOException>(() =>
            {
                using var exclusive = new FileStream(Path.Combine(deployment.Root, "LogReader.Core.dll"),
                    FileMode.Open, FileAccess.Read, FileShare.None);
            });
            if (cancelWait)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exit);
                Assert.False(process.HasExited);
                Assert.True(Directory.Exists(deployment.Root));
            }
            process.StandardInput.Close();
            if (!cancelWait)
                await exit.WaitAsync(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            using var releasedFile = new FileStream(Path.Combine(deployment.Root, "LogReader.Core.dll"),
                FileMode.Open, FileAccess.Read, FileShare.None);
        }
        finally
        {
            cancellation.Cancel();
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (exit != null)
            {
                try { await exit; }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            try { await ready; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Assert.Empty(await stderr.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task Deployment_CompletionWithoutProcessIdentityFailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Deployment.WaitForProcessExitAsync(
            new StdioClientCompletionDetails(), CancellationToken.None));
    }

    [Fact]
    public async Task Deployment_DeletionWaitsForTemporaryDllLockAfterChildShutdown()
    {
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var deployment = new Deployment(cleanupRetry: () => retry.TrySetResult());
        using var heldFile = new FileStream(Path.Combine(deployment.Root, "LogReader.Core.dll"),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        Task? cleanup = null;
        try
        {
            cleanup = deployment.DisposeAsync().AsTask();
            await retry.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(cleanup.IsCompleted);
            heldFile.Dispose();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(Directory.Exists(deployment.Root));
        }
        finally
        {
            heldFile.Dispose();
            if (cleanup != null)
                await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
            else
                await deployment.DisposeAsync();
        }
    }

    [Fact]
    public async Task Deployment_PersistentDllLockFailsWithOriginalErrorAndRetainsDirectory()
    {
        await using var deployment = new Deployment();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var heldFile = new FileStream(Path.Combine(deployment.Root, "LogReader.Core.dll"),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        // Cancel at the first observed lock failure, without a timing-based wait.
        var error = await Assert.ThrowsAsync<IOException>(() => Deployment.DeleteDirectoryAsync(
            deployment.Root, cancellation.Token, cancellation.Cancel));
        Assert.True(error.InnerException is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(deployment.Root));
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
        private readonly Action? _cleanupRetry;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WeezTail-mcp-protocol-tests", Guid.NewGuid().ToString("N"));
        public string ExecutablePath => Path.Combine(Root, "WeezTail.Mcp.exe");

        public Deployment(bool populatedCatalog = false, Action? cleanupRetry = null)
        {
            _cleanupRetry = cleanupRetry;
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
            var completion = Assert.IsType<StdioClientCompletionDetails>(
                await client.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // SDK shutdown is best effort: completion can precede actual process exit.
            await WaitForProcessExitAsync(completion, cancellation.Token);
            _clients.Remove(client);
        }

        public static async Task WaitForProcessExitAsync(StdioClientCompletionDetails completion, CancellationToken ct)
        {
            if (completion.ProcessId is not { } pid)
                throw new InvalidOperationException("MCP child process identity is unavailable; deployment cleanup is unsafe.");
            Process process;
            try { process = Process.GetProcessById(pid); }
            catch (ArgumentException) { return; } // The process already exited and was reaped.
            using (process)
                await process.WaitForExitAsync(ct);
        }

        public static async Task DeleteDirectoryAsync(string root, CancellationToken ct, Action? retry = null)
        {
            Exception? lastError = null;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                    return;
                }
                catch (DirectoryNotFoundException) when (!Directory.Exists(root)) { return; }
                catch (Exception error) when ((error is IOException or UnauthorizedAccessException) &&
                    (error.HResult & 0xffff) is 5 or 32 or 33)
                {
                    // Retry Windows access/sharing/lock failures even after verified child exit.
                    lastError = error;
                    retry?.Invoke();
                }
                try { await Task.Delay(25, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
            throw new IOException("MCP deployment remained inaccessible after child shutdown; retaining remaining files.", lastError);
        }

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            foreach (var client in _clients.ToArray())
            {
                try { await CloseClientAsync(client); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0)
                throw new AggregateException("MCP child shutdown was not verified; retaining the deployment.", failures);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await DeleteDirectoryAsync(Root, cancellation.Token, _cleanupRetry);
        }
    }
}
