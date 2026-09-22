namespace LogReader.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

public sealed class McpStdioProtocolTests
{
    [Fact]
    public async Task Executable_WqlReadsSavedProfileAndReturnsTypedHitsWithoutChangingStores()
    {
        // A separate portable installation exercises production composition without using personal settings.
        var directory = Directory.CreateTempSubdirectory("WeezTailWqlStdio_");
        try
        {
            var source = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
            foreach (var file in Directory.EnumerateFiles(source))
            {
                if (Path.GetExtension(file) is ".dll" or ".exe" or ".json")
                    File.Copy(file, Path.Combine(directory.FullName, Path.GetFileName(file)));
            }
            foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "runtimes"), "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(directory.FullName, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, AppPaths.InstallConfigFileName),
                "{\"installMode\":\"Portable\",\"storageMode\":\"ExeDirectory\"}");
            var data = Directory.CreateDirectory(Path.Combine(directory.FullName, "Data"));
            var logPath = Path.Combine(directory.FullName, "payments.log");
            const string matchingLine = "ERROR duration=842";
            await File.WriteAllTextAsync(logPath, matchingLine + "\nINFO duration=10\nERROR duration=bad\nother\n");
            var profile = FieldProfilesViewModelTests.Example();
            var stores = new Dictionary<string, object>
            {
                ["loggroups.json"] = new[] { new LogGroup { Id = "payments", Name = "Payments", FileIds = ["payment-log"] } },
                ["logfiles.json"] = new[] { new LogFileEntry { Id = "payment-log", FilePath = logPath } },
                ["settings.json"] = new AppSettings { FieldProfiles = [profile] }
            };
            var saved = new Dictionary<string, (string Json, DateTime LastWrite)>();
            foreach (var store in stores)
            {
                var path = Path.Combine(data.FullName, store.Key);
                var json = JsonSerializer.Serialize(new { schemaVersion = 1, data = store.Value }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await File.WriteAllTextAsync(path, json);
                saved.Add(path, (json, File.GetLastWriteTimeUtc(path)));
            }

            var stderr = new ConcurrentQueue<string>();
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "weeztail-wql-walkthrough",
                Command = Path.Combine(directory.FullName, "WeezTail.Mcp.exe"),
                Arguments = [], WorkingDirectory = directory.FullName,
                ShutdownTimeout = TimeSpan.FromMilliseconds(500), StandardErrorLines = stderr.Enqueue
            }, loggerFactory: null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using (var client = await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: null, cancellation.Token))
            {
                var discovery = await client.CallToolAsync("list_field_profiles", arguments: null, cancellationToken: cancellation.Token);
                AssertStructuredSuccess(discovery);
                var declared = Assert.Single(discovery.StructuredContent!.Value.GetProperty("result").GetProperty("profiles").EnumerateArray());
                Assert.Equal("payments", declared.GetProperty("id").GetString());
                Assert.Equal(2, declared.GetProperty("fields").GetArrayLength());
                Assert.DoesNotContain("pattern", discovery.StructuredContent.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);

                const string expression = "level = \"ERROR\" AND duration_ms > 500";
                var response = await client.CallToolAsync("query_logs", new Dictionary<string, object?>
                {
                    ["targets"] = new[] { new { kind = "logFile", id = "payment-log" } },
                    ["query"] = expression, ["profileId"] = "payments"
                }, cancellationToken: cancellation.Token);
                AssertStructuredSuccess(response);
                var envelope = response.StructuredContent!.Value;
                Assert.Empty(envelope.GetProperty("errors").EnumerateArray());
                var result = envelope.GetProperty("result");
                Assert.True(result.GetProperty("isQueryComplete").GetBoolean(), envelope.GetRawText());
                var file = Assert.Single(result.GetProperty("files").EnumerateArray());
                var hit = Assert.Single(file.GetProperty("hits").EnumerateArray());
                Assert.Equal(1, hit.GetProperty("lineNumber").GetInt32());
                Assert.Equal(matchingLine, hit.GetProperty("text").GetString());
                var expected = WqlCompiler.Compile(expression, profile).Evaluate(matchingLine, 1);
                Assert.Equal(expected.Fields["duration_ms"].Number, hit.GetProperty("fields").GetProperty("duration_ms").GetProperty("number").GetDecimal());
                Assert.Equal("ERROR", hit.GetProperty("fields").GetProperty("level").GetProperty("text").GetString());
                var parsing = file.GetProperty("parsing");
                Assert.True(parsing.GetProperty("isScanComplete").GetBoolean());
                Assert.Equal(4, parsing.GetProperty("evaluatedLineCount").GetInt64());
                Assert.Equal(1, parsing.GetProperty("fields").GetProperty("duration_ms").GetProperty("invalidCount").GetInt64());
                Assert.DoesNotContain(directory.FullName, envelope.GetRawText(), StringComparison.OrdinalIgnoreCase);
            }
            Assert.Empty(stderr);
            foreach (var store in saved)
            {
                Assert.Equal(store.Value.Json, await File.ReadAllTextAsync(store.Key));
                Assert.Equal(store.Value.LastWrite, File.GetLastWriteTimeUtc(store.Key));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Executable_InitializesAndCallsEveryToolWithoutNonProtocolOutput()
    {
        var standardError = new ConcurrentQueue<string>();
        var executablePath = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
        var transportOptions = new StdioClientTransportOptions
        {
            Name = "weeztail-integration-test",
            Command = executablePath,
            Arguments = [],
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
            var status = await client.CallToolAsync("server_status", arguments: null, cancellationToken: cancellation.Token);
            var profiles = await client.CallToolAsync("list_field_profiles", arguments: null, cancellationToken: cancellation.Token);
            var wql = await client.CallToolAsync("query_logs", new Dictionary<string, object?>
            {
                ["targets"] = new[] { new { kind = "logFile", id = "missing-file" } },
                ["query"] = "raw CONTAINS \"needle\""
            }, cancellationToken: cancellation.Token);

            Assert.Equal("weeztail", client.ServerInfo.Name);
            Assert.NotNull(client.ServerCapabilities.Tools);
            Assert.Null(client.ServerCapabilities.Resources);
            Assert.Null(client.ServerCapabilities.Prompts);
            Assert.Equal(
                ["count_logs", "list_field_profiles", "list_log_tree", "query_logs", "read_log_lines", "read_log_tail", "search_logs", "server_status"],
                tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
            Assert.All([list, search, count, read, tail, status, profiles, wql], AssertStructuredSuccess);
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
                Arguments = [],
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
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the WeezTail MCP server.");
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
        Assert.Equal(3, result.StructuredContent.Value.GetProperty("schemaVersion").GetInt32());
    }
}
