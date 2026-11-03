namespace LogReader.Core.Tests.UncPerformance;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

internal static partial class McpWorkflowMeasurements
{
    internal static string Stage { get; private set; } = "setup";
    private sealed record Scope(string Label, FixtureFile[] Files);
    private sealed record Expected(long Matches, long Occurrences, string[] Hits, string[] FirstLines, long ScanBytes);
    private sealed record Sample(string Fixture, string Operation, int Files, long SourceBytes,
        double Milliseconds, long Units, string ExactResultSha256, int Calls, long ResponseBytes,
        int? PeakConcurrentDiskOperations, int? PeakConcurrentUncOperations, long WorkingSetBytes, long PrivateBytes,
        Progress[] SweepProgress, long? BytesEvaluated = null, double? ReleaseMilliseconds = null);
    private sealed record Progress(int CheckedFiles, int UncheckedFiles, int BacklogFiles,
        bool IsScopeChecked, bool IsIdle, long BytesEvaluated, int Events);

    internal static async Task RunAsync(string manifestPath, string output, string executable, CancellationToken ct)
    {
        manifestPath = FixtureStore.RequireLocalPath(manifestPath);
        output = FixtureStore.RequireLocalPath(output);
        executable = FixtureStore.RequireLocalPath(executable);
        if (File.Exists(output)) throw new InvalidOperationException("Measurement reports cannot overwrite evidence.");
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(await File.ReadAllTextAsync(manifestPath, ct), FixtureStore.Json)
            ?? throw new InvalidOperationException("Missing fixture manifest.");
        if (manifest.SchemaVersion != 1) throw new InvalidOperationException("Unsupported fixture manifest.");
        var scopes = new List<Scope>();
        var unavailable = new List<string>();
        foreach (var storage in new[] { "local", "unc" })
        {
            var available = manifest.Files.Where(f => storage == "local"
                    ? f.Label.Length == 9 && f.Label.StartsWith("local-", StringComparison.Ordinal) && char.IsAsciiDigit(f.Label[6])
                    : f.Label.StartsWith("unc-", StringComparison.Ordinal))
                .OrderBy(f => f.Label, StringComparer.Ordinal).ToArray();
            foreach (var count in new[] { 1, 2, 10, 100 })
            {
                var label = $"{storage}-{count}";
                var files = available.Take(count).ToArray();
                if (files.Length != count || files.Sum(f => f.Bytes) > FixtureStore.MaximumInvocationBytes || files.Any(f => f.Sha256.Length == 0))
                    unavailable.Add(label);
                else scopes.Add(new(label, files));
            }
        }
        foreach (var label in new[] { "local-large", "local-multi-quantum" })
            scopes.Add(new(label, [manifest.Files.Single(f => f.Label == label)]));
        if (scopes.Count == 0) throw new InvalidOperationException("No MCP scenarios executed.");
        var selected = scopes.SelectMany(s => s.Files).DistinctBy(f => f.Label).ToArray();
        foreach (var file in selected)
        {
            if (!Path.GetFullPath(file.Path).StartsWith(@"\\", StringComparison.Ordinal))
                FixtureStore.RequireDescendant(Path.GetDirectoryName(manifestPath)!, file.Path);
            await FixtureStore.VerifyAsync(file, true, ct);
        }

        Stage = "independent-reference";
        // Reference reads are outside timing, independent of the child's backend and protocol mapping.
        var referenceRoot = Path.Combine(Path.GetDirectoryName(output)!, "Reference-" + Guid.NewGuid().ToString("N"));
        using var state = AppPaths.BeginTestScope(rootPath: referenceRoot, localCacheDirectory: Path.Combine(referenceRoot, "Cache"));
        var expected = new Dictionary<string, Expected>();
        var reader = new ChunkedLogReaderService();
        var search = new SearchService();
        foreach (var file in selected)
        {
            var result = await search.SearchFileAsync(file.Path, new SearchRequest { Query = "needle" }, file.Encoding, ct);
            if (result.Error != null || result.WasCancelled || result.HitLimitExceeded)
                throw new IOException("Independent search reference was incomplete.");
            using var index = await reader.BuildIndexAsync(file.Path, file.Encoding, ct);
            var lines = await reader.ReadLinesAsync(file.Path, index, 0, 100, file.Encoding, ct);
            var preamble = EncodingHelper.GetEncoding(file.Encoding).GetPreamble();
            var contentOffset = 0;
            if (preamble.Length > 0)
            {
                using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var probe = new byte[preamble.Length];
                var read = await input.ReadAtLeastAsync(probe, probe.Length, throwOnEndOfStream: false, ct);
                if (read == preamble.Length && probe.AsSpan().SequenceEqual(preamble)) contentOffset = preamble.Length;
            }
            var extent = file.Encoding is FileEncoding.Utf16 or FileEncoding.Utf16Be ? file.Bytes & ~1L : file.Bytes;
            expected.Add(file.Label, new(result.MatchingLineCount, result.MatchOccurrenceCount,
                result.Hits.Select(h => $"{file.Label}:{h.LineNumber}:{h.MatchStart}:{h.MatchLength}").ToArray(), lines.ToArray(), extent - contentOffset));
        }

        var deployment = Path.Combine(Path.GetDirectoryName(output)!, "Mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(deployment);
        foreach (var source in Directory.EnumerateFiles(Path.GetDirectoryName(executable)!))
        {
            FixtureStore.RejectReparsePoint(source);
            if (source.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) || string.Equals(source, executable, StringComparison.OrdinalIgnoreCase))
                File.Copy(source, Path.Combine(deployment, Path.GetFileName(source)));
        }
        var childExecutable = Path.Combine(deployment, Path.GetFileName(executable));
        var data = Path.Combine(deployment, "Data");
        Directory.CreateDirectory(data);
        var activeDirectory = Path.Combine(deployment, "Active");
        Directory.CreateDirectory(activeDirectory);
        var activeFiles = Enumerable.Range(0, 100).Select(i =>
            (Id: $"active-{i:D3}", Path: Path.Combine(activeDirectory, $"active-{i:D3}.log"))).ToArray();
        foreach (var file in activeFiles) await File.WriteAllTextAsync(file.Path, "old\n", ct);
        await WriteAsync(Path.Combine(deployment, "WeezTail.install.json"), new { installMode = "Portable", storageMode = "ExeDirectory" });
        await WriteAsync(Path.Combine(data, "logfiles.json"), new
        {
            schemaVersion = 1, data = selected.Select(file => new { id = file.Label, filePath = file.Path })
                .Concat(activeFiles.Select(file => new { id = file.Id, filePath = file.Path })).ToArray()
        });
        await WriteAsync(Path.Combine(data, "loggroups.json"), new
        {
            schemaVersion = 1, data = scopes.Select((scope, i) => new
            {
                id = scope.Label, name = scope.Label, sortOrder = i, parentGroupId = (string?)null,
                kind = "dashboard", fileIds = scope.Files.Select(file => file.Label).ToArray()
            }).Concat(new[] { 1, 10, 100 }.Select(count => new
            {
                id = $"local-active-{count}", name = $"local-active-{count}", sortOrder = 100 + count, parentGroupId = (string?)null,
                kind = "dashboard", fileIds = activeFiles.Take(count).Select(file => file.Id).ToArray()
            })).ToArray()
        });
        await WriteAsync(Path.Combine(data, "settings.json"), new { schemaVersion = 1, data = new { dateRollingPatterns = Array.Empty<object>() } });

        var samples = new List<Sample>();
        Stage = "initialize-child";
        await using var client = McpMeasurementClient.Start(childExecutable);
        var startupMilliseconds = await client.InitializeAsync(ct);
        var tools = await client.ListToolsAsync(ct);
        var toolNames = tools.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order().ToArray();
        if (!toolNames.SequenceEqual(new[] { "count_logs", "list_log_tree", "monitor_logs", "read_log_lines", "read_log_tail", "search_logs", "server_status" }))
            throw new InvalidOperationException("MCP tool surface changed.");
        var status = await client.CallAsync("server_status", new { }, ct);
        var limits = status.Result.GetProperty("queryBackend").GetProperty("limits").Clone();
        var maximumDiskOperations = limits.GetProperty("maximumConcurrentDiskOperations").GetInt32();
        if (maximumDiskOperations is < 1 or > 8)
            throw new InvalidOperationException("Unsupported MCP disk cap for this measurement recipe.");

        foreach (var scope in scopes)
        {
            foreach (var file in scope.Files) await FixtureStore.VerifyAsync(file, true, ct);
            foreach (var operation in new[] { "search-snapshot", "count", "read-100", "monitor-initialize", "monitor-idle-sweep" })
            {
                Stage = scope.Label + "/" + operation;
                var warm = await ExecuteAsync(scope, operation);
                var measured = await ExecuteAsync(scope, operation);
                if (warm.Units != measured.Units || warm.ExactResultSha256 != measured.ExactResultSha256)
                    throw new InvalidOperationException("MCP warm/timed exact results differ.");
                samples.Add(measured);
            }
            foreach (var file in scope.Files) await FixtureStore.VerifyAsync(file, true, ct);
        }
        foreach (var count in new[] { 1, 10, 100 })
            foreach (var matching in new[] { false, true })
            {
                Stage = $"local-active-{count}/monitor-{(matching ? "match" : "nonmatch")}";
                var warm = await MeasureActiveAsync(client, activeFiles.Take(count).ToArray(), matching, ct);
                var measured = await MeasureActiveAsync(client, activeFiles.Take(count).ToArray(), matching, ct);
                if (warm.Units != measured.Units || warm.ExactResultSha256 != measured.ExactResultSha256)
                    throw new InvalidOperationException("Active monitor warm/timed results differ.");
                samples.Add(measured);
            }
        foreach (var scenario in LifecycleScenarios())
        {
            Stage = "local-lifecycle/" + scenario;
            var warm = await MeasureLifecycleAsync(client, activeFiles.Take(2).ToArray(), scenario, ct);
            var measured = await MeasureLifecycleAsync(client, activeFiles.Take(2).ToArray(), scenario, ct);
            if (warm.Units != measured.Units || warm.ExactResultSha256 != measured.ExactResultSha256)
                throw new InvalidOperationException("Lifecycle warm/timed results differ.");
            samples.Add(measured);
        }
        Stage = "shutdown-and-report";
        status = await client.CallAsync("server_status", new { }, ct);
        if (status.Result.GetProperty("queryBackend").GetProperty("activeIndexedSessions").GetInt32() != 0)
            throw new InvalidOperationException("MCP indexed leases remained active after requests.");
        var retainedIndexedSessions = status.Result.GetProperty("queryBackend").GetProperty("retainedIndexedSessions").GetInt32();
        var shutdownMilliseconds = await client.StopAsync(ct);
        await WriteAsync(output, new
        {
            schemaVersion = 1, utc = DateTime.UtcNow, cache = "warm/cache-uncontrolled", manifest.Recipe, manifest.RemoteStatus,
            manifestHash = Hash(await File.ReadAllBytesAsync(manifestPath, ct)),
            artifacts = new[] { executable, Path.Combine(Path.GetDirectoryName(executable)!, "LogReader.Infrastructure.dll"), Path.Combine(Path.GetDirectoryName(executable)!, "WeezTail.Mcp.dll") }
                .Where(File.Exists).Select(path => new { name = Path.GetFileName(path), sha256 = Hash(File.ReadAllBytes(path)) }).ToArray(),
            toolSchemaSha256 = Hash(Encoding.UTF8.GetBytes(tools.GetRawText())), limits, startupMilliseconds, shutdownMilliseconds,
            retainedIndexedSessions, activeIndexedSessions = 0, protocolParity = true, exitCode = 0, stderrCharacters = 0,
            activeRecipe = "old-LF then other-LF or needle-number-LF; 1/10/100 fresh owned local files per scenario",
            lifecycleRecipe = "v1: split UTF-8 with/without BOM; UTF-16 half-unit; ANSI; unfinished rematch; CRLF; replacement/truncation/encoding/missing-reappearance; search/count frozen-boundary handoff",
            coverage = "Real stdio snapshot/search/count/read and monitor initialization/full idle and active sweeps. Fixture/reference/mutation setup excluded from timing. Live scanner/buffer ownership requires gated backend tests; absent protocol metrics are null. Retained indexed cache entries are intentional and end at verified process exit.",
            unavailable, samples
        });
        Console.WriteLine(JsonSerializer.Serialize(new { workflow = "mcp", samples = samples.Count, unavailable, exitCode = 0 }));

        async Task WriteAsync(string path, object value)
            => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, FixtureStore.Json), ct);

        async Task<Sample> ExecuteAsync(Scope scope, string operation)
        {
            var calls = 0;
            long responseBytes = 0, working = 0, privateBytes = 0;
            var peakDisk = 0;
            var peakUnc = 0;
            var hasStatistics = false;
            var evaluatedBytes = 0L;
            var reference = scope.Files.Select(file => expected[file.Label]).ToArray();
            var units = 0L;
            double? releaseMilliseconds = null;
            var identities = new List<string>();
            var progress = new List<Progress>();
            async Task<McpMeasurementResponse> CallAsync(string tool, object arguments)
            {
                var response = await client.CallAsync(tool, arguments, ct);
                calls++;
                responseBytes += response.WireBytes;
                working = Math.Max(working, response.WorkingSetBytes);
                privateBytes = Math.Max(privateBytes, response.PrivateBytes);
                if (tool == "monitor_logs") progress.Add(GetProgress(response.Result));
                if (response.Result.TryGetProperty("statistics", out var stats))
                {
                    hasStatistics = true;
                    evaluatedBytes += stats.GetProperty("bytesEvaluated").GetInt64();
                    peakDisk = Math.Max(peakDisk, stats.GetProperty("peakConcurrentDiskOperations").GetInt32());
                    peakUnc = Math.Max(peakUnc, stats.GetProperty("peakConcurrentUncOperations").GetInt32());
                }
                if (peakDisk > maximumDiskOperations || peakUnc > maximumDiskOperations) throw new InvalidOperationException("MCP disk budget exceeded.");
                return response;
            }
            object[] Targets() => [new { kind = "dashboard", id = scope.Label }];
            var watch = Stopwatch.StartNew();
            if (operation is "search-snapshot" or "count")
            {
                string? cursor = null;
                JsonElement result;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                do
                {
                    var arguments = new Dictionary<string, object?>
                    {
                        ["targets"] = Targets(), ["query"] = "needle", ["includeStatistics"] = true,
                        ["timeoutMilliseconds"] = 30000, ["cursor"] = cursor
                    };
                    if (operation == "search-snapshot") arguments["resultMode"] = "matchesOnly";
                    var response = await CallAsync(operation == "count" ? "count_logs" : "search_logs", arguments);
                    result = response.Result;
                    RejectFileErrors(result);
                    if (operation == "search-snapshot")
                        foreach (var file in result.GetProperty("files").EnumerateArray())
                            foreach (var hit in file.GetProperty("hits").EnumerateArray())
                            {
                                var identity = $"{file.GetProperty("fileId").GetString()}:{hit.GetProperty("lineNumber").GetInt64()}:{hit.GetProperty("matchStart").GetInt32()}:{hit.GetProperty("matchLength").GetInt32()}";
                                if (!seen.Add(identity)) throw new InvalidOperationException("Snapshot returned duplicate hit identities.");
                                identities.Add(identity);
                            }
                    cursor = OptionalString(result, "nextCursor");
                    if (calls > 1024) throw new InvalidOperationException("Snapshot did not converge.");
                } while (cursor != null);
                if (!result.GetProperty(operation == "count" ? "isComplete" : "isQueryComplete").GetBoolean() ||
                    !result.GetProperty("isTraversalComplete").GetBoolean() || result.GetProperty("failedFileCount").GetInt32() != 0 ||
                    result.GetProperty("searchedFileCount").GetInt32() != scope.Files.Length ||
                    result.GetProperty("matchingLineCount").GetInt64() != reference.Sum(r => r.Matches) ||
                    result.GetProperty("matchOccurrenceCount").GetInt64() != reference.Sum(r => r.Occurrences) ||
                    !hasStatistics || evaluatedBytes != reference.Sum(r => r.ScanBytes))
                    throw new InvalidOperationException("Snapshot/count was incomplete or differs from its independent reference.");
                units = result.GetProperty("matchingLineCount").GetInt64();
                if (operation == "search-snapshot" && !identities.SequenceEqual(reference.SelectMany(r => r.Hits)))
                    throw new InvalidOperationException("Snapshot exact hit identities differ.");
                if (operation == "count") identities.Add($"{units}:{reference.Sum(r => r.Occurrences)}");
            }
            else if (operation == "read-100")
            {
                for (var i = 0; i < scope.Files.Length; i++)
                {
                    var response = await CallAsync("read_log_lines", new { fileId = scope.Files[i].Label, startLine = 1, count = 100 });
                    RejectIncompleteEnvelope(response);
                    RejectFileErrors(response.Result);
                    var lines = response.Result.GetProperty("file").GetProperty("lines").EnumerateArray().ToArray();
                    if (lines.Any(line => line.TryGetProperty("isTruncated", out var truncated) && truncated.GetBoolean()) ||
                        !lines.Select(line => line.GetProperty("text").GetString()).SequenceEqual(reference[i].FirstLines) ||
                        !lines.Select(line => line.GetProperty("lineNumber").GetInt32()).SequenceEqual(Enumerable.Range(1, lines.Length)))
                        throw new InvalidOperationException("Indexed read differs from the independent reference.");
                    units += lines.Length;
                    identities.Add(scope.Files[i].Label);
                    identities.AddRange(reference[i].FirstLines);
                }
            }
            else
            {
                string? cursor = null;
                try
                {
                    var initial = await CallAsync("monitor_logs", new { targets = Targets(), query = "needle", timeoutMilliseconds = 30000 });
                    cursor = OptionalString(initial.Result, "nextCursor");
                    RejectIncompleteEnvelope(initial);
                    RejectFileErrors(initial.Result);
                    if (cursor == null || initial.Result.GetProperty("selectedFileCount").GetInt32() != scope.Files.Length ||
                        initial.Result.GetProperty("stoppedFileCount").GetInt32() != 0 ||
                        !initial.Result.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("fileId").GetString())
                            .SequenceEqual(scope.Files.Select(f => f.Label)) ||
                        initial.Result.GetProperty("files").EnumerateArray().Any(f => f.GetProperty("events").GetArrayLength() != 0))
                        throw new InvalidOperationException("Monitor initialization did not establish every baseline.");
                    if (operation == "monitor-idle-sweep")
                    {
                        // Initialization is measured separately. Warm and time a COMPLETE sweep, not one page.
                        watch.Restart(); calls = 0; responseBytes = 0; working = 0; privateBytes = 0; progress.Clear();
                        var checkedFiles = new HashSet<string>(StringComparer.Ordinal);
                        while (true)
                        {
                            var submitted = cursor;
                            var poll = await CallAsync("monitor_logs", new { cursor, maxFiles = 10, timeoutMilliseconds = 30000 });
                            var result = poll.Result;
                            cursor = OptionalString(result, "nextCursor");
                            RejectIncompleteEnvelope(poll);
                            RejectFileErrors(result);
                            var files = result.GetProperty("files").EnumerateArray().ToArray();
                            if (files.Length != result.GetProperty("checkedFileCount").GetInt32() ||
                                files.Any(f => f.GetProperty("events").GetArrayLength() != 0) ||
                                result.GetProperty("backlogFileCount").GetInt32() != 0 ||
                                result.GetProperty("bytesEvaluated").GetInt64() != 0 || cursor == null)
                                throw new InvalidOperationException("Idle sweep had activity, missing slots or backlog.");
                            foreach (var file in files)
                                if (!checkedFiles.Add(file.GetProperty("fileId").GetString()!))
                                    throw new InvalidOperationException("Idle sweep checked a file twice.");
                            var complete = result.GetProperty("isScopeChecked").GetBoolean();
                            if (result.GetProperty("uncheckedFileCount").GetInt32() != scope.Files.Length - checkedFiles.Count ||
                                result.GetProperty("isIdle").GetBoolean() != complete || result.GetProperty("isCaughtUp").GetBoolean() != complete)
                                throw new InvalidOperationException("Idle sweep progress fields were not truthful.");
                            if (complete)
                            {
                                watch.Stop();
                                if (!checkedFiles.SetEquals(scope.Files.Select(f => f.Label)))
                                    throw new InvalidOperationException("Idle scope incomplete.");
                                units = checkedFiles.Count;
                                identities.AddRange(checkedFiles.Order(StringComparer.Ordinal));
                                // Replay validation is outside timing and the forward-call counters.
                                var replay = await client.CallAsync("monitor_logs", new { cursor = submitted, maxFiles = 10, timeoutMilliseconds = 30000 }, ct);
                                if (replay.Envelope.GetRawText() != poll.Envelope.GetRawText())
                                    throw new InvalidOperationException("Monitor replay changed the committed envelope.");
                                break;
                            }
                            if (calls > scope.Files.Length + 1) throw new InvalidOperationException("Idle sweep did not converge.");
                        }
                    }
                    else
                    {
                        watch.Stop();
                        units = scope.Files.Length;
                        identities.AddRange(initial.Result.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("fileId").GetString()!));
                    }
                }
                finally
                {
                    if (cursor != null)
                    {
                        var released = await client.CallAsync("monitor_logs", new { cursor, release = true }, ct);
                        releaseMilliseconds = released.Milliseconds;
                        if (released.Result.GetProperty("state").GetString() != "released")
                            throw new InvalidOperationException("Monitor release was not terminal.");
                    }
                }
            }
            watch.Stop();
            return new(scope.Label, operation, scope.Files.Length, scope.Files.Sum(f => f.Bytes), watch.Elapsed.TotalMilliseconds,
                units, Hash(Encoding.UTF8.GetBytes(string.Join('\n', identities))), calls, responseBytes,
                hasStatistics ? peakDisk : null, hasStatistics ? peakUnc : null, working, privateBytes, progress.ToArray(),
                hasStatistics ? evaluatedBytes : null, releaseMilliseconds);
        }
    }

    private static void RejectFileErrors(JsonElement result)
    {
        IEnumerable<JsonElement> files = result.TryGetProperty("files", out var many) ? many.EnumerateArray() :
            result.TryGetProperty("file", out var one) ? new[] { one } : [];
        if (files.Any(file => file.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null))
            throw new InvalidOperationException("A partial per-file error cannot count as a speedup.");
    }

    private static void RejectIncompleteEnvelope(McpMeasurementResponse response)
    {
        if (response.Envelope.TryGetProperty("isPartial", out var partial) && partial.GetBoolean() ||
            response.Envelope.TryGetProperty("isTruncated", out var truncated) && truncated.GetBoolean())
            throw new InvalidOperationException("Incomplete monitor/read work cannot count as a speedup.");
    }

    private static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Progress GetProgress(JsonElement result) => new(
        result.GetProperty("checkedFileCount").GetInt32(), result.GetProperty("uncheckedFileCount").GetInt32(),
        result.GetProperty("backlogFileCount").GetInt32(), result.GetProperty("isScopeChecked").GetBoolean(),
        result.GetProperty("isIdle").GetBoolean(), result.GetProperty("bytesEvaluated").GetInt64(),
        result.GetProperty("files").EnumerateArray().Sum(f => f.GetProperty("events").GetArrayLength()));
}
