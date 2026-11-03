using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Core.Tests.UncPerformance;
using LogReader.Infrastructure.Services;

// Opt-in console entry point, separate from test discovery. Fail closed on empty/partial runs.
try
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    var ct = cancellation.Token;
    if (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration != "Release")
        throw new InvalidOperationException("Measurements require a Release build.");
    if (args.Length == 4 && args[0] == "mcp-sample")
    {
        await McpWorkflowMeasurements.RunAsync(args[1], args[2], args[3], ct);
        return 0;
    }
    if (args.Length == 3 && args[0] == "prepare" && args[2] is "local" or "unc")
    {
        var prepared = await FixtureStore.PrepareAsync(Path.GetFullPath(args[1]), args[2] == "unc", ct);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            prepared.Recipe, prepared.RemoteStatus, prepared.RemoteDiscoveredFiles,
            prepared.RemoteDiscoveredBytes, localFiles = prepared.Files.Count(f => f.Label.StartsWith("local-"))
        }));
        return 0;
    }
    if (args.Length != 3 || args[0] != "sample")
        throw new ArgumentException("Usage: prepare <new-local-fixture-directory> <local|unc> OR sample <private-manifest> <new-local-report>");

    var manifestPath = Path.GetFullPath(args[1]);
    var output = Path.GetFullPath(args[2]);
    if (manifestPath.StartsWith(@"\\") || output.StartsWith(@"\\") || File.Exists(output))
        throw new InvalidOperationException("Manifest and new report must be local; reports cannot overwrite evidence.");
    var manifest = JsonSerializer.Deserialize<FixtureManifest>(await File.ReadAllTextAsync(manifestPath, ct), FixtureStore.Json)
        ?? throw new InvalidOperationException("Fixture manifest is empty.");
    if (manifest.SchemaVersion != 1 || manifest.Files.Count == 0)
        throw new InvalidOperationException("Unsupported or empty fixture manifest.");

    var state = Path.Combine(Path.GetDirectoryName(output)!, "State-" + Guid.NewGuid().ToString("N"));
    using var scope = AppPaths.BeginTestScope(rootPath: state, localCacheDirectory: Path.Combine(state, "Cache"));
    var reader = new ChunkedLogReaderService();
    var search = new SearchService();
    var detector = new FileEncodingDetectionService();
    var scanner = (IResumableLogScanner)new ResumableLogScanner(detector);
    var samples = new List<Sample>();
    var unavailable = new List<string>();
    var checkpoints = new Dictionary<string, ResumableFileCheckpoint>();
    foreach (var storage in new[] { "local", "unc" })
    {
        var available = manifest.Files.Where(f => storage == "local"
                ? f.Label.Length == 9 && f.Label.StartsWith("local-", StringComparison.Ordinal) && char.IsAsciiDigit(f.Label[6])
                : f.Label.StartsWith("unc-", StringComparison.Ordinal))
            .OrderBy(f => f.Label, StringComparer.Ordinal).ToArray();
        foreach (var count in new[] { 1, 10, 100 })
        {
            var label = $"{storage}-{count}";
            var files = available.Take(count).ToArray();
            if (files.Length != count || files.Sum(f => f.Bytes) > FixtureStore.MaximumInvocationBytes || files.Any(f => f.Sha256.Length == 0))
            {
                unavailable.Add(label);
                continue;
            }
            await MeasureScopeAsync(label, files);
        }
    }
    foreach (var label in new[] { "local-large", "local-multi-quantum" })
        await MeasureScopeAsync(label, [manifest.Files.Single(f => f.Label == label)]);
    if (samples.Count == 0)
        throw new InvalidOperationException("No measurements executed.");
    var artifacts = new[] { typeof(ChunkedLogReaderService).Assembly, typeof(SearchRequest).Assembly, Assembly.GetExecutingAssembly() }
        .Select(assembly => new
        {
            name = assembly.GetName().Name,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
        }).ToArray();
    var report = new
    {
        schemaVersion = 1, utc = DateTime.UtcNow, runtime = RuntimeInformation.FrameworkDescription,
        os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        configuration = "Release", cache = "warm/cache-uncontrolled", manifest.Recipe, manifest.RemoteStatus,
        manifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, ct))),
        artifacts, unavailable,
        limits = new { dashboardGlobal = 24, searchGlobal = 32,
            mcpDefaultGlobal = LogQueryEffectiveLimits.Default.MaximumConcurrentDiskOperations, scanQuantumBytes = 4 * 1024 * 1024 },
        coverage = "Components only; no WPF publication, backend admission, MCP stdio, active lifecycle or desktop acceptance. Result validation is included in elapsed time. viewport-100 includes its index build. Peak fields count harness-owned component work, not SMB requests; null peaks are uninstrumented.",
        cleanup = "All component operations awaited; indexes, interactive builds and scanners disposed before report.",
        samples
    };
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, FixtureStore.Json), ct);
    Console.WriteLine(JsonSerializer.Serialize(new { samples = samples.Count, unavailable, manifest.RemoteStatus }));
    return 0;

    async Task MeasureScopeAsync(string fixture, FixtureFile[] files)
    {
        foreach (var file in files)
        {
            await FixtureStore.VerifyAsync(file, true, ct);
            if (!checkpoints.ContainsKey(file.Label))
            {
                var checkpoint = new ResumableFileCheckpoint();
                await using var scan = await scanner.OpenAsync(file.Path, checkpoint, ct);
                scan.ValidateAfterRead();
                checkpoint.Done = true;
                checkpoint.ReadOffset = checkpoint.Extent;
                checkpoints.Add(file.Label, checkpoint);
            }
        }
        var paths = files.Select(f => f.Path).ToList();
        var encodings = files.ToDictionary(f => f.Path, f => f.Encoding, StringComparer.OrdinalIgnoreCase);
        var request = new SearchRequest { Query = "needle", FilePaths = paths };
        foreach (var operation in new[] { "index", "interactive-initial-suspend", "search-policy", "filter-policy", "viewport-100", "idle-scanner" })
        {
            // Each API/scenario warms outside its timed sample. Both runs must agree exactly.
            var warm = await ExecuteAsync(operation);
            var watch = Stopwatch.StartNew();
            var measured = await ExecuteAsync(operation);
            watch.Stop();
            if (warm.Result != measured.Result || warm.Units != measured.Units)
                throw new IOException("Warm and timed exact results differ.");
            if (fixture.StartsWith("local-", StringComparison.Ordinal) && fixture is not "local-large" and not "local-multi-quantum")
            {
                var expected = operation is "search-policy" or "filter-policy" ? files.Length :
                    operation == "idle-scanner" ? files.Length : operation == "viewport-100" ? files.Length * 100L : files.Length * 250L;
                if (measured.Units != expected)
                    throw new IOException("Numbered local fixture exact count differs from its recipe.");
            }
            samples.Add(new(fixture, operation, files.Length, files.Sum(f => f.Bytes), watch.Elapsed.TotalMilliseconds,
                measured.Units, measured.Result, measured.Peak, measured.PeakReaders, measured.PeakReaders * 256L * 1024));
        }
        foreach (var file in files)
            await FixtureStore.VerifyAsync(file, true, ct);

        async Task<Measurement> ExecuteAsync(string operation)
        {
            if (operation is "search-policy" or "filter-policy")
            {
                if (operation == "search-policy")
                {
                    var results = await search.SearchFilesAsync(request, encodings, ct);
                    if (results.Count != files.Length || results.Any(r => r.Error != null || r.HitLimitExceeded || r.WasCancelled))
                        throw new IOException("Incomplete search cannot count as a measurement.");
                    return new(results.Sum(r => (long)r.Hits.Count), Digest(string.Join(";", results.Select((result, i) =>
                        $"{i}:" + string.Join(',', result.Hits.Select(h => $"{h.LineNumber}:{h.MatchStart}:{h.MatchLength}:{Digest(h.LineText)}"))))), null, 0);
                }
                var filtered = await search.FilterFilesAsync(request, encodings, ct);
                if (filtered.Count != files.Length || filtered.Any(r => r.Error != null || r.HitLimitExceeded))
                    throw new IOException("Incomplete filter cannot count as a measurement.");
                return new(filtered.Sum(r => (long)r.MatchingLineNumbers.Count),
                    Digest(string.Join(";", filtered.Select((result, i) => $"{i}:" + string.Join(',', result.MatchingLineNumbers)))), null, 0);
            }
            var active = 0;
            var peak = 0;
            var resultsByFile = new Measurement[files.Length];
            // Component baseline uses the preceding two-worker same-share scheduling shape.
            await Parallel.ForEachAsync(Enumerable.Range(0, files.Length),
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (i, token) =>
                {
                    var current = Interlocked.Increment(ref active);
                    int observed;
                    do { observed = Volatile.Read(ref peak); }
                    while (current > observed && Interlocked.CompareExchange(ref peak, current, observed) != observed);
                    try
                    {
                        var file = files[i];
                        if (operation == "idle-scanner")
                        {
                            var checkpoint = checkpoints[file.Label].Clone();
                            await using var scan = await scanner.OpenAsync(file.Path, checkpoint, token);
                            if (detector.ResolveEncodingDecision(file.Path, FileEncoding.Auto).ResolvedEncoding != checkpoint.Encoding)
                                throw new IOException("Idle scanner encoding changed.");
                            scan.ValidateAfterRead();
                            resultsByFile[i] = new(1, "idle", null, 1);
                        }
                        else if (operation == "interactive-initial-suspend")
                        {
                            await using var build = await ((IInteractiveLogReaderService)reader).CreateIndexBuildAsync(file.Path, file.Encoding, token);
                            if (!build.CanResume)
                                throw new IOException("Fixture does not support a resumable interactive build.");
                            await build.SuspendAsync();
                            while (!await build.AdvanceAsync(4 * 1024 * 1024, () => false, token))
                                await build.SuspendAsync();
                            using var index = build.TakeCompletedIndex();
                            resultsByFile[i] = IndexResult(index);
                        }
                        else
                        {
                            using var index = await reader.BuildIndexAsync(file.Path, file.Encoding, token);
                            if (operation == "viewport-100")
                            {
                                var lines = await reader.ReadLinesAsync(file.Path, index, Math.Max(0, index.LineCount - 100), 100, file.Encoding, token);
                                resultsByFile[i] = new(lines.Count, Digest(string.Join('\n', lines)), null, 0);
                            }
                            else
                                resultsByFile[i] = IndexResult(index);
                        }
                    }
                    finally { Interlocked.Decrement(ref active); }
                });
            if (active != 0)
                throw new InvalidOperationException("Component work did not drain.");
            return new(resultsByFile.Sum(r => r.Units), Digest(string.Join(';', resultsByFile.Select(r => r.Result))), peak,
                operation == "idle-scanner" ? peak : 0);
        }
    }
}
catch (Exception ex)
{
    // Exception messages may carry physical paths or log content; keep diagnostics normalized.
    Console.Error.WriteLine("UNC measurement failed: " + ex.GetType().Name +
        (args.FirstOrDefault() == "mcp-sample" ? " at " + McpWorkflowMeasurements.Stage : string.Empty));
    return 1;
}

static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

static Measurement IndexResult(LineIndex index)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    Span<byte> offset = stackalloc byte[8];
    for (var i = 0; i < index.LineCount; i++)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(offset, index.LineOffsets[i]);
        hash.AppendData(offset);
    }
    return new(index.LineCount, Convert.ToHexString(hash.GetHashAndReset()), null, 0);
}

internal sealed record Measurement(long Units, string Result, int? Peak, int PeakReaders);
internal sealed record Sample(string Fixture, string Operation, int Files, long SourceBytes, double Milliseconds,
    long Units, string ExactResultSha256, int? PeakComponentOperations, int PeakScannerBuffers, long ScannerBufferBytes);
