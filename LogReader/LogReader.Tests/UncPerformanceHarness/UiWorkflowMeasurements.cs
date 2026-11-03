namespace LogReader.Tests.UncPerformance;

using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Core.Tests.UncPerformance;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;

internal static class UiWorkflowMeasurements
{
    internal static string Stage { get; private set; } = "setup";
    internal sealed record Sample(string Fixture, string Operation, int Files, long SourceBytes,
        double Milliseconds, long Units, string ExactResultSha256, long WorkingSetBytes, string? PublicationKind = null,
        double? PreviewMilliseconds = null, int? PlannedConcurrency = null);
    private sealed record Reference(int Lines, string IndexDigest, string[] FirstLines, string[] Hits, int[] MatchingLines, RecentLogPreview Preview);

    internal static async Task RunAsync(string manifestPath, string output, CancellationToken ct)
    {
        InteractiveDesktop.RequireAvailable();
        manifestPath = FixtureStore.RequireLocalPath(manifestPath);
        output = FixtureStore.RequireLocalPath(output);
        if (File.Exists(output)) throw new InvalidOperationException("Reports cannot overwrite evidence.");
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(await File.ReadAllTextAsync(manifestPath, ct), FixtureStore.Json)
            ?? throw new InvalidOperationException("Missing fixture manifest.");
        if (manifest.SchemaVersion != 1) throw new InvalidOperationException("Unsupported manifest.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var samples = new List<Sample>();
        var unavailable = new List<string>();
        foreach (var storage in new[] { "local", "unc" })
        {
            var available = manifest.Files.Where(f => storage == "local"
                    ? f.Label.Length == 9 && f.Label.StartsWith("local-", StringComparison.Ordinal) && char.IsAsciiDigit(f.Label[6])
                    : f.Label.StartsWith("unc-", StringComparison.Ordinal))
                .OrderBy(f => f.Label, StringComparer.Ordinal).ToArray();
            foreach (var count in new[] { 1, 10, 100 })
            {
                var files = available.Take(count).ToArray();
                var label = $"{storage}-{count}";
                if (files.Length != count || files.Sum(f => f.Bytes) > FixtureStore.MaximumInvocationBytes || files.Any(f => f.Sha256.Length == 0))
                    unavailable.Add(label);
                else await MeasureAsync(label, files);
            }
        }
        foreach (var label in new[] { "local-large", "local-multi-quantum" })
            await MeasureAsync(label, [manifest.Files.Single(f => f.Label == label)]);
        if (samples.Count == 0) throw new InvalidOperationException("No UI samples executed.");
        Stage = "report";
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, measurementRecipe = "ui-v2", utc = DateTime.UtcNow, cache = "warm/cache-uncontrolled",
            manifestHash = Hash(await File.ReadAllBytesAsync(manifestPath, ct)),
            artifacts = new[] { typeof(MainViewModel).Assembly, typeof(ChunkedLogReaderService).Assembly, typeof(UiWorkflowMeasurements).Assembly }
                .Select(a => new { name = a.GetName().Name, sha256 = Hash(File.ReadAllBytes(a.Location)) }).ToArray(),
            coverage = "Real MainViewModel/dashboard scheduler/FileSession/reader/search/filter and native WPF viewport. Native polling is replaced with a synchronized registration-only tail double; no polling workers are created. Default auto-scroll is used for loading, then disabled for manual scrolling. View-model setup, observation and cleanup run on WpfTestHost. First-content timing records preview or indexed publication; raw preview latency is separate when observed. Indexed reads exclude index creation. Planned concurrency is a policy limit, not an observed admission peak. Working set is sampled, not a live-buffer proof.",
            desktopRequired = true, ownedWorkDrained = true, unavailable, samples
        }, FixtureStore.Json), ct);
        Console.WriteLine(JsonSerializer.Serialize(new { workflow = "ui", samples = samples.Count, unavailable, ownedWorkDrained = true }));

        async Task MeasureAsync(string label, FixtureFile[] files)
        {
            Stage = label + "/reference";
            foreach (var file in files) await FixtureStore.VerifyAsync(file, true, ct);
            var root = Path.Combine(Path.GetDirectoryName(output)!, "Ui-" + Guid.NewGuid().ToString("N"));
            var warm = await RunScopeAsync(label, files, root + "-warm", ct);
            var measured = await RunScopeAsync(label, files, root + "-measured", ct);
            if (warm.Count != measured.Count || !warm.Zip(measured).All(pair =>
                    pair.First.Operation == pair.Second.Operation && pair.First.Units == pair.Second.Units &&
                    pair.First.ExactResultSha256 == pair.Second.ExactResultSha256))
                throw new InvalidOperationException("UI warm/timed exact results differ.");
            samples.AddRange(measured);
            foreach (var file in files) await FixtureStore.VerifyAsync(file, true, ct);
        }
    }

    internal static async Task<List<Sample>> RunScopeAsync(string label, FixtureFile[] files, string root, CancellationToken ct)
    {
        Stage = label + "/setup";
        root = FixtureStore.RequireLocalPath(root);
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh UI state directory.");
        foreach (var file in files) await FixtureStore.VerifyAsync(file, true, ct);
        var result = new List<Sample>();
        await WpfTestHost.RunAsync(async hostToken =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, hostToken);
            var token = cancellation.Token;
            using var scope = AppPaths.BeginTestScope(rootPath: root, localCacheDirectory: Path.Combine(root, "Cache"));
            var reader = new ChunkedLogReaderService();
            var search = new SearchService();
            var references = new Dictionary<string, Reference>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                using var index = await reader.BuildIndexAsync(file.Path, file.Encoding, token);
                var first = await reader.ReadLinesAsync(file.Path, index, 0, 100, file.Encoding, token);
                var hits = await search.SearchFileAsync(file.Path, new SearchRequest { Query = "needle" }, file.Encoding, token);
                if (hits.Error != null || hits.WasCancelled || hits.HitLimitExceeded) throw new IOException("Reference search incomplete.");
                var preview = await ((IInteractiveLogReaderService)reader).ReadRecentPreviewAsync(file.Path, file.Encoding, token);
                references.Add(file.Path, new(index.LineCount, IndexDigest(index), first.ToArray(),
                    hits.Hits.Select(h => HitIdentity(file.Label, h.LineNumber, h.MatchStart, h.MatchLength, h.LineText)).ToArray(),
                    hits.Hits.Select(h => checked((int)h.LineNumber)).ToArray(), preview));
            }
            var fileRepo = new JsonLogFileRepository();
            var groupRepo = new JsonLogGroupRepository(fileRepo);
            // Seed the isolated fixture once, outside timing, rather than rewriting the
            // entire store for every entry. Preserve the same IDs and versioned format.
            var entries = files.Select(file => new LogFileEntry
                { Id = file.Label, DisplayName = file.Label, FilePath = file.Path }).ToList();
            await JsonStore.SaveAsync("logfiles.json", new VersionedRepositoryEnvelope<List<LogFileEntry>>
                { SchemaVersion = 1, Data = entries });
            var persisted = await fileRepo.GetAllAsync();
            if (!persisted.Select(entry => (entry.Id, entry.DisplayName, entry.FilePath))
                    .SequenceEqual(entries.Select(entry => (entry.Id, entry.DisplayName, entry.FilePath))))
                throw new InvalidOperationException("Seeded repository differs from the fixture entries.");
            await groupRepo.AddAsync(new LogGroup { Id = "measurement", Name = "Measurement", FileIds = files.Select(f => f.Label).ToList() });
            var tail = new RegistrationOnlyTailService();
            var vm = new MainViewModel(fileRepo, groupRepo, new JsonSettingsRepository(), reader, search, tail,
                new FileEncodingDetectionService(), enableLifecycleTimer: false,
                fileDialogService: ForbiddenUiService.Instance, messageBoxService: ForbiddenUiService.Instance,
                settingsDialogService: ForbiddenUiService.Instance, bulkOpenPathsDialogService: ForbiddenUiService.Instance);
            Window? window = null;
            Task? load = null;
            var clock = new Stopwatch();
            double? previewMs = null;
            var readable = new TaskCompletionSource<(double Time, string Kind)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlers = new List<(LogTabViewModel Tab, PropertyChangedEventHandler Handler, NotifyCollectionChangedEventHandler VisibleHandler)>();
            var observingLoad = true;
            void Observe()
            {
                if (!observingLoad || vm.SelectedTab is not { } tab || !clock.IsRunning) return;
                if (tab.HasPreview)
                {
                    previewMs ??= clock.Elapsed.TotalMilliseconds;
                    var expected = references[tab.FilePath].Preview.Lines;
                    if (!tab.PreviewLines.Select(l => (l.ByteOffset, l.Text, l.IsTruncated))
                            .SequenceEqual(expected.Select(l => (l.ByteOffset, l.Text, l.IsTruncated))))
                        throw new InvalidOperationException("Published preview differs from independent reference.");
                    readable.TrySetResult((clock.Elapsed.TotalMilliseconds, "preview"));
                }
                else if (tab.IsIndexReady && tab.VisibleLines.Count > 0)
                    readable.TrySetResult((clock.Elapsed.TotalMilliseconds, "indexed"));
            }
            void TabsChanged(object? sender, NotifyCollectionChangedEventArgs args)
            {
                if (args.NewItems == null) return;
                foreach (LogTabViewModel tab in args.NewItems)
                {
                    PropertyChangedEventHandler handler = (_, _) => Observe();
                    NotifyCollectionChangedEventHandler visibleHandler = (_, _) => Observe();
                    tab.PropertyChanged += handler;
                    tab.VisibleLines.CollectionChanged += visibleHandler;
                    handlers.Add((tab, handler, visibleHandler));
                }
            }
            PropertyChangedEventHandler selectedChanged = (_, _) => Observe();
            vm.Tabs.CollectionChanged += TabsChanged;
            vm.PropertyChanged += selectedChanged;
            try
            {
                await vm.InitializeAsync();
                window = new Window { Content = new LogViewportView { DataContext = vm }, Width = 900, Height = 650 };
                WpfTestHost.ShowHidden(window);
                window.UpdateLayout();
                Stage = label + "/dashboard-load";
                clock.Start();
                load = vm.HandleDashboardGroupInvokedAsync(vm.Groups.Single());
                await load.WaitAsync(TimeSpan.FromSeconds(30), token);
                Observe();
                var firstContent = await readable.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                await WpfTestHost.FlushAsync();
                clock.Stop();
                observingLoad = false;
                if (vm.Tabs.Count != files.Length || vm.Tabs.Any(t => !t.IsIndexReady || t.HasLoadError))
                    throw new InvalidOperationException("Dashboard load was incomplete.");
                foreach (var tab in vm.Tabs)
                    if (tab.TotalLines != references[tab.FilePath].Lines || IndexDigest(tab.ActiveSession.DebugLineIndex!) != references[tab.FilePath].IndexDigest)
                        throw new InvalidOperationException("Dashboard index differs from independent reference.");
                Add("dashboard-load", clock.Elapsed.TotalMilliseconds, vm.Tabs.Sum(t => (long)t.TotalLines),
                    string.Join(';', files.Select(f => references[f.Path].IndexDigest)));
                Add("preview-or-index-publication", firstContent.Time, 1, "validated-content", firstContent.Kind, previewMs);

                Stage = label + "/indexed-read";
                var text = new List<string>();
                clock.Restart();
                foreach (var file in files)
                {
                    var tab = vm.Tabs.Single(t => t.FilePath == file.Path);
                    var lines = await reader.ReadLinesAsync(file.Path, tab.ActiveSession.DebugLineIndex!, 0, 100, tab.EffectiveEncoding, token);
                    if (!lines.SequenceEqual(references[file.Path].FirstLines)) throw new IOException("Indexed read differs.");
                    text.AddRange(lines);
                }
                clock.Stop();
                Add("indexed-read-100", clock.Elapsed.TotalMilliseconds, text.Count, string.Join('\n', text));
                vm.GlobalAutoScrollEnabled = false;
                await vm.AutoScrollSyncTask;
                var selected = vm.SelectedTab!;
                await selected.RequestScrollTo(Math.Max(0, selected.TotalLines - 10));
                await WpfTestHost.FlushAsync();
                if (selected.ViewportStartLine <= 0 && selected.TotalLines > selected.ViewportLineCount)
                    throw new InvalidOperationException("Native viewport did not scroll.");
                var expectedVisible = await reader.ReadLinesAsync(selected.FilePath, selected.ActiveSession.DebugLineIndex!,
                    selected.ViewportStartLine, selected.VisibleLines.Count, selected.EffectiveEncoding, token);
                if (!selected.VisibleLines.Select(l => l.Text).SequenceEqual(expectedVisible))
                    throw new InvalidOperationException("Scrolled viewport content differs from the index.");

                Stage = label + "/ui-search";
                vm.SearchPanel.Query = "needle";
                vm.SearchPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
                vm.SearchPanel.SearchDataMode = SearchDataMode.DiskSnapshot;
                clock.Restart();
                await vm.SearchPanel.ExecuteSearchCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30), token);
                clock.Stop();
                var identities = vm.SearchPanel.Results.SelectMany(r => r.Hits.Select(h => HitIdentity(
                    files.Single(f => f.Path == r.FilePath).Label, h.LineNumber, h.MatchStart, h.MatchLength, h.LineText))).ToArray();
                if (vm.SearchPanel.Results.Any(r => r.Error != null) ||
                    !identities.SequenceEqual(files.SelectMany(f => references[f.Path].Hits)))
                    throw new InvalidOperationException("UI search results differ.");
                Add("ui-search", clock.Elapsed.TotalMilliseconds, identities.Length, string.Join(';', identities));

                Stage = label + "/ui-filter";
                vm.FilterPanel.Query = "needle";
                vm.FilterPanel.TargetMode = SearchFilterTargetMode.AllOpenTabs;
                clock.Restart();
                await vm.FilterPanel.ApplyFilterCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30), token);
                clock.Stop();
                foreach (var tab in vm.Tabs)
                    if (!tab.IsFilterActive || tab.CaptureActiveFilterSnapshot() is not { } snapshot ||
                        !snapshot.MatchingLineNumbers.SequenceEqual(references[tab.FilePath].MatchingLines))
                        throw new InvalidOperationException("UI filter results differ.");
                Add("ui-filter", clock.Elapsed.TotalMilliseconds, vm.Tabs.Sum(t => (long)t.FilteredLineCount),
                    string.Join(';', files.Select(f => f.Label + ":" + string.Join(',',
                        vm.Tabs.Single(t => t.FilePath == f.Path).CaptureActiveFilterSnapshot()!.MatchingLineNumbers))));
            }
            finally
            {
                var shutdown = Stopwatch.StartNew();
                vm.Tabs.CollectionChanged -= TabsChanged;
                vm.PropertyChanged -= selectedChanged;
                foreach (var (tab, handler, visibleHandler) in handlers)
                {
                    tab.PropertyChanged -= handler;
                    tab.VisibleLines.CollectionChanged -= visibleHandler;
                }
                vm.Dispose();
                await WpfTestHost.DrainOwnedWorkAsync(Task.WhenAll(load ?? Task.CompletedTask, vm.ShutdownTask));
                window?.Close();
                if (tail.ActiveRegistrations != 0) throw new InvalidOperationException("Tail registrations leaked.");
                tail.Dispose();
                shutdown.Stop();
                Add("shutdown", shutdown.Elapsed.TotalMilliseconds, 0, "drained");
            }

            void Add(string operation, double milliseconds, long units, string exact, string? kind = null, double? preview = null)
            {
                using var process = Process.GetCurrentProcess();
                var policy = operation == "ui-search" ? AdaptiveParallelismOperation.DiskSearch :
                    operation == "ui-filter" ? AdaptiveParallelismOperation.FilterApply : AdaptiveParallelismOperation.DashboardLoad;
                var plan = AdaptiveParallelismPolicy.CreatePlan(policy, files.Select(f => f.Path));
                result.Add(new(label, operation, files.Length, files.Sum(f => f.Bytes), milliseconds, units,
                    Hash(Encoding.UTF8.GetBytes(exact)), process.WorkingSet64, kind, preview,
                    operation == "shutdown" || operation == "indexed-read-100" ? null : plan.GlobalLimit));
            }
        });
        return result;
    }

    private static string HitIdentity(string id, long line, int start, int length, string text)
        => $"{id}:{line}:{start}:{length}:{Hash(Encoding.UTF8.GetBytes(text))}";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string IndexDigest(LineIndex index)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> value = stackalloc byte[8];
        for (var i = 0; i < index.LineCount; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(value, index.LineOffsets[i]);
            hash.AppendData(value);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class RegistrationOnlyTailService : IFileTailService, IFileTailBaselineService
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
        public int ActiveRegistrations { get { lock (_gate) return _counts.Values.Sum(); } }
        public event EventHandler<TailEventArgs>? LinesAppended { add { } remove { } }
        public event EventHandler<FileRotatedEventArgs>? FileRotated { add { } remove { } }
        public event EventHandler<FileAvailabilityChangedEventArgs>? FileAvailabilityChanged { add { } remove { } }
        public event EventHandler<TailErrorEventArgs>? TailError { add { } remove { } }
        public void StartTailing(string path, FileEncoding encoding, int pollingIntervalMs = 250)
        { lock (_gate) _counts[path] = _counts.GetValueOrDefault(path) + 1; }
        void IFileTailBaselineService.StartTailing(string path, FileEncoding encoding, FileTailBaseline baseline, int interval)
            => StartTailing(path, encoding, interval);
        public void StopTailing(string path)
        { lock (_gate) { if (!_counts.TryGetValue(path, out var count)) return; if (count == 1) _counts.Remove(path); else _counts[path] = count - 1; } }
        public void StopAll() { lock (_gate) _counts.Clear(); }
        public void Dispose() => StopAll();
    }
}
