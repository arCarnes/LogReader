namespace LogReader.Core.Tests;

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;
using LogReader.Mcp;

public sealed partial class HeadlessLogQueryBackendTests
{
    private static LogMonitorQuery MonitorStart(string? query = null) => new()
    { Targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard")], Query = query };

    [Fact]
    public async Task Monitor_DeduplicatesAndFairlyChecksWholeScopeWithoutFalseIdle()
    {
        var first = await CreateFileAsync("monitor-first.log", "old\n");
        var second = await CreateFileAsync("monitor-second.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("first", first), ("alias", first), ("second", second)));
        var initial = await backend.MonitorLogsAsync(new LogMonitorQuery
        {
            Targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard"), new(ConfiguredLogTargetKind.LogFile, "first")], Query = "needle"
        });
        Assert.Empty(initial.Errors);
        Assert.Equal(2, initial.Result!.SelectedFileCount);
        Assert.Empty(initial.Result.Files.SelectMany(file => file.Events));
        Assert.False(initial.Result.IsCaughtUp);
        await File.AppendAllTextAsync(first, "miss\nneedle first\n");
        await File.AppendAllTextAsync(second, "needle quiet\n");
        var one = await backend.MonitorLogsAsync(new() { Cursor = initial.Result.NextCursor, MaxFiles = 1, MaxLines = 1 });
        Assert.False(one.Result!.IsIdle);
        Assert.Equal(1, one.Result.UncheckedFileCount);
        Assert.Equal(1, one.Result.BacklogFileCount);
        Assert.Empty(one.Result.Files.SelectMany(file => file.Events));
        var two = await backend.MonitorLogsAsync(new() { Cursor = one.Result.NextCursor, MaxFiles = 1 });
        Assert.True(two.Result!.IsScopeChecked);
        Assert.False(two.Result.IsCaughtUp);
        Assert.Equal("needle quiet", Assert.Single(Assert.Single(two.Result.Files).Events).Text);
        var three = await backend.MonitorLogsAsync(new() { Cursor = two.Result.NextCursor, MaxFiles = 1 });
        Assert.Equal("needle first", Assert.Single(Assert.Single(three.Result!.Files).Events).Text);
        var four = await backend.MonitorLogsAsync(new() { Cursor = three.Result.NextCursor, MaxFiles = 1 });
        Assert.True(four.Result!.IsCaughtUp);
        Assert.False(four.Result.IsIdle);
        var five = await backend.MonitorLogsAsync(new() { Cursor = four.Result.NextCursor, MaxFiles = 2 });
        Assert.True(five.Result!.IsIdle);
        Assert.True(five.Result.IsCaughtUp);
        Assert.DoesNotContain(_testDirectory, JsonSerializer.Serialize(five), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, (await backend.GetStatusAsync()).Result!.MappedLineOffsets);
    }

    [Fact]
    public async Task Monitor_InitializationRetainsAppendsToEarlierFileAndConcurrentPollReplays()
    {
        var first = await CreateFileAsync("monitor-init-a.log", "old\n");
        var second = await CreateFileAsync("monitor-init-b.log", "old\n");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new GatedMonitorScanner(async (path, _, ct) =>
        {
            if (path == second)
            {
                ready.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        }, path => { if (path == first) firstCaptured.TrySetResult(); });
        using var backend = CreateBackend(CreateSnapshot(("first", first), ("second", second)), scanner: scanner);
        var initialization = backend.MonitorLogsAsync(MonitorStart("needle"));
        try
        {
            await Task.WhenAll(ready.Task, firstCaptured.Task).WaitAsync(TimeSpan.FromSeconds(3));
            await File.AppendAllTextAsync(first, "needle during initialization\n");
        }
        finally
        {
            release.TrySetResult();
            await initialization.WaitAsync(TimeSpan.FromSeconds(3));
        }
        var initial = await initialization;
        var request = new LogMonitorQuery { Cursor = initial.Result!.NextCursor };
        var pages = await Task.WhenAll(backend.MonitorLogsAsync(request), backend.MonitorLogsAsync(request));
        Assert.Same(pages[0], pages[1]);
        Assert.Equal("needle during initialization", Assert.Single(pages[0].Result!.Files.SelectMany(file => file.Events)).Text);
    }

    [Fact]
    public async Task Monitor_CancellationRollsBackAndRetryDoesNotLoseMatches()
    {
        var path = await CreateFileAsync("monitor-cancel.log", "old\n");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new GatedMonitorScanner(async (_, call, ct) =>
        {
            if (call == 2)
            {
                ready.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        });
        using var backend = CreateBackend(CreateSnapshot(("file", path)), scanner: scanner);
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        await File.AppendAllTextAsync(path, "after\n");
        using var cancellation = new CancellationTokenSource();
        var poll = backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor }, cancellation.Token);
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally
        {
            cancellation.Cancel();
            await poll.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Null((await poll).Result);
        var retry = await backend.MonitorLogsAsync(new() { Cursor = initial.Result.NextCursor });
        Assert.Equal("after", Assert.Single(retry.Result!.Files.SelectMany(file => file.Events)).Text);
    }

    [Fact]
    public async Task Monitor_ExpiryCapacityReleaseAndBoundedBoundaryAreExplicit()
    {
        var now = DateTimeOffset.UtcNow;
        var path = await CreateFileAsync("monitor-capacity.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)), now: () => now,
            limits: LogQueryEffectiveLimits.Default with { MaximumContinuationSessions = 1 });
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        Assert.Equal("continuation_capacity_exceeded", Assert.Single((await backend.MonitorLogsAsync(MonitorStart())).Errors).Code);
        var release = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor, Release = true });
        Assert.Equal("released", release.Result!.State);
        var next = await backend.MonitorLogsAsync(MonitorStart());
        Assert.NotNull(next.Result!.NextCursor);
        now = now.AddMinutes(16);
        var expired = await backend.MonitorLogsAsync(new() { Cursor = next.Result.NextCursor });
        Assert.Equal("expired_monitor_cursor", Assert.Single(expired.Errors).Code);
        await File.WriteAllTextAsync(path, new string('x', 70_000));
        var unsafeBoundary = await backend.MonitorLogsAsync(MonitorStart());
        Assert.Equal("stopped", unsafeBoundary.Result!.State);
        Assert.Equal("monitor_boundary_unavailable", Assert.Single(unsafeBoundary.Result.Files).Error!.Code);
    }

    [Fact]
    public async Task Monitor_GlobalAndPerFileByteBudgetsLetQuietFilesAdvance()
    {
        var hot = await CreateFileAsync("monitor-hot.log", "old\n");
        var quiet = await CreateFileAsync("monitor-quiet.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("hot", hot), ("quiet", quiet)));
        var initial = await backend.MonitorLogsAsync(MonitorStart("needle"));
        await File.AppendAllTextAsync(hot, new string('x', 600_000) + "\n");
        await File.AppendAllTextAsync(quiet, "needle quiet\n");
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.True(page.Result!.BytesEvaluated <= 4 * 1024 * 1024);
        Assert.Equal(2, page.Result.CheckedFileCount);
        Assert.Equal(1, page.Result.BacklogFileCount);
        Assert.Equal("needle quiet", Assert.Single(page.Result.Files.SelectMany(file => file.Events)).Text);
        var cursor = page.Result.NextCursor;
        for (var index = 0; index < 5 && page.Result.BacklogFileCount > 0; index++)
        {
            page = await backend.MonitorLogsAsync(new() { Cursor = cursor });
            cursor = page.Result!.NextCursor;
        }
        Assert.True(page.Result.IsCaughtUp);
    }

    [Fact]
    public async Task Monitor_CatalogChangesRejectAdvanceAndReplayBeforeDiskAccess()
    {
        var path = await CreateFileAsync("monitor-auth.log", "old\n");
        var catalog = new MutableCatalogReader(CreateSnapshot(("file", path)));
        using var backend = CreateBackend(catalog.Snapshot, catalogReader: catalog);
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        catalog.Snapshot = CreateSnapshot(("different", path + ".other"));
        foreach (var cursor in new[] { initial.Result.NextCursor, page.Result!.NextCursor })
        {
            var rejected = await backend.MonitorLogsAsync(new() { Cursor = cursor });
            Assert.Equal("stale_monitor_cursor", Assert.Single(rejected.Errors).Code);
            Assert.Null(rejected.Result);
        }
    }

    [Fact]
    public async Task Monitor_UnavailableAndSlowFilesDoNotStarveHealthyFiles()
    {
        var missing = await CreateFileAsync("monitor-missing.log", "old\n");
        var slow = await CreateFileAsync("monitor-slow.log", "old\n");
        var healthy = await CreateFileAsync("monitor-healthy.log", "old\n");
        var scanner = new GatedMonitorScanner(async (path, call, ct) =>
        {
            if (path == slow && call > 3)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        using var backend = CreateBackend(CreateSnapshot(("missing", missing), ("slow", slow), ("healthy", healthy)), scanner: scanner);
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        File.Delete(missing);
        await File.AppendAllTextAsync(healthy, "healthy append\n");
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor, TimeoutMilliseconds = 1000 });
        Assert.Empty(page.Errors);
        Assert.True(page.IsPartial);
        Assert.Equal(3, page.Result!.CheckedFileCount);
        Assert.Equal("healthy append", Assert.Single(page.Result.Files.SelectMany(file => file.Events)).Text);
        Assert.Equal(2, page.Result.Files.Count(file => file.Error != null));
        Assert.False(page.Result.IsCaughtUp);
    }

    [Fact]
    public async Task Monitor_ScopeCapRejectsBeforeCapturingAnyBaseline()
    {
        var path = await CreateFileAsync("monitor-scope.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(Enumerable.Range(0, 101)
            .Select(index => ($"file-{index}", path + index)).ToArray()));
        var result = await backend.MonitorLogsAsync(MonitorStart());
        Assert.Equal("monitor_scope_limit", Assert.Single(result.Errors).Code);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task Monitor_PhysicalCapAllowsAliasesAcrossResolverPagesAndRetainsTheirProvenance()
    {
        var paths = await CreateFilesAsync("monitor-alias-cap", 100);
        var entries = Enumerable.Range(0, 300).Select(index => ($"file-{index:D3}", paths[index % paths.Length])).ToArray();
        var groups = Enumerable.Range(0, 3).Select(batch => new ConfiguredLogGroup(
            $"dashboard-{batch}", $"Dashboard {batch}", batch, "folder", LogGroupKind.Dashboard,
            entries.Skip(batch * 100).Take(100).Select(entry => entry.Item1).ToImmutableArray()));
        var snapshot = new ConfiguredLogCatalogSnapshot(1,
            [new("folder", "Folder", 0, null, LogGroupKind.Branch, []), .. groups],
            entries.Select(entry => new ConfiguredLogFile(entry.Item1, entry.Item2)));
        using var backend = CreateBackend(snapshot);
        var initial = await backend.MonitorLogsAsync(new() { Targets = [new(ConfiguredLogTargetKind.Folder, "folder")] });
        Assert.Empty(initial.Errors);
        Assert.Equal(100, initial.Result!.SelectedFileCount);
        Assert.All(initial.Result.Files, file => Assert.Equal(3, file.Provenance.Length));
        Assert.All(initial.Result.Files, file => Assert.Null(file.Error));
        await File.AppendAllTextAsync(paths[0], "once\n");
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result.NextCursor, MaxFiles = 100 });
        Assert.Equal("line-0once", Assert.Single(page.Result!.Files.SelectMany(file => file.Events)).Text);
        Assert.Equal(100, page.Result.CheckedFileCount);
    }

    [Fact]
    public async Task Monitor_AppendAtValidationRemainsBacklogAndIsDeliveredNextPoll()
    {
        var path = await CreateFileAsync("monitor-validation-append.log", "old\n");
        var scanner = new MonitorValidationScanner(() => File.AppendAllText(path, "second\n"));
        using var backend = CreateBackend(CreateSnapshot(("file", path)), scanner: scanner);
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        await File.AppendAllTextAsync(path, "first\n");
        var first = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.Equal("first", Assert.Single(first.Result!.Files.SelectMany(file => file.Events)).Text);
        Assert.Equal(1, first.Result.BacklogFileCount);
        Assert.False(first.Result.IsCaughtUp);
        var second = await backend.MonitorLogsAsync(new() { Cursor = first.Result.NextCursor });
        Assert.Equal("second", Assert.Single(second.Result!.Files.SelectMany(file => file.Events)).Text);
        Assert.True(second.Result.IsCaughtUp);
    }

    [Fact]
    public async Task Monitor_ControlledUncWorkRetainsUncGateAndAllowsConcurrentLocalObservation()
    {
        const string firstUnc = @"\\fixture\share\one.log";
        const string secondUnc = @"\\fixture\share\two.log";
        var local = await CreateFileAsync("monitor-local-gate.log", "local\n");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var localStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ScriptedLogScanner((path, _, _, _) => Task.FromResult(Result(path, "needle")));
        var blockPolls = 0;
        var scanner = new MonitorOpenHookScanner(inner, async (path, checkpoint, ct) =>
        {
            checkpoint.ObservedLength = checkpoint.Extent; // Controlled metadata, not a real share.
            if (!checkpoint.Done || Volatile.Read(ref blockPolls) == 0) return;
            if (path == firstUnc) { started.TrySetResult(); await release.Task.WaitAsync(ct); }
            else if (path == local) localStarted.TrySetResult();
        });
        using var backend = CreateBackend(CreateSnapshot(("unc-one", firstUnc), ("unc-two", secondUnc), ("local", local)),
            scanner: scanner, pathExists: _ => false, encodingDetection: new CountingEncodingDetectionService(FileEncoding.Utf8));
        var uncSource = await backend.SearchLogsAsync(new()
        { Targets = [new(ConfiguredLogTargetKind.LogFile, "unc-one"), new(ConfiguredLogTargetKind.LogFile, "unc-two")], Query = "needle", EnableHandoff = true });
        var localSource = await backend.SearchLogsAsync(new()
        { Targets = [new(ConfiguredLogTargetKind.LogFile, "local")], Query = "needle", EnableHandoff = true });
        var uncInitial = await backend.MonitorLogsAsync(new() { HandoffCursor = uncSource.Result!.HandoffCursor });
        var localInitial = await backend.MonitorLogsAsync(new() { HandoffCursor = localSource.Result!.HandoffCursor });
        Volatile.Write(ref blockPolls, 1);
        var unc = backend.MonitorLogsAsync(new() { Cursor = uncInitial.Result!.NextCursor });
        Task<LogOperationEnvelope<LogMonitorResult>>? localPoll = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            localPoll = backend.MonitorLogsAsync(new() { Cursor = localInitial.Result!.NextCursor });
            await localStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            release.TrySetResult();
            await unc.WaitAsync(TimeSpan.FromSeconds(3));
            if (localPoll != null) await localPoll.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Empty((await unc).Errors);
        Assert.Empty((await localPoll!).Errors);
        Assert.Equal(2, (await unc).Result!.CheckedFileCount);
    }

    private sealed class MonitorOpenHookScanner(IResumableLogScanner inner,
        Func<string, ResumableFileCheckpoint, CancellationToken, Task> beforeOpen) : IResumableLogScanner
    {
        public Regex? PrepareRegex(SearchRequest request) => inner.PrepareRegex(request);
        public async Task<IResumableLogScan> OpenAsync(string path, ResumableFileCheckpoint checkpoint, CancellationToken ct)
        {
            await beforeOpen(path, checkpoint, ct);
            return await inner.OpenAsync(path, checkpoint, ct);
        }
    }

    private sealed class MonitorValidationScanner(Action append) : IResumableLogScanner
    {
        private readonly IResumableLogScanner _inner = new ResumableLogScanner(new FileEncodingDetectionService());
        private int _validations;
        private void BeforeValidation() { if (Interlocked.Increment(ref _validations) == 2) append(); }
        public Regex? PrepareRegex(SearchRequest request) => _inner.PrepareRegex(request);
        public async Task<IResumableLogScan> OpenAsync(string path, ResumableFileCheckpoint checkpoint, CancellationToken ct)
            => new Scan(this, await _inner.OpenAsync(path, checkpoint, ct));
        private sealed class Scan(MonitorValidationScanner owner, IResumableLogScan inner) : IResumableLogScan
        {
            public Task<EvaluatedScanLine?> ReadNextAsync(SearchRequest request, Regex? regex, ScanWorkBudget budget, int maxBytes, CancellationToken ct)
                => inner.ReadNextAsync(request, regex, budget, maxBytes, ct);
            public void ValidateAfterRead()
            {
                owner.BeforeValidation();
                inner.ValidateAfterRead();
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [Fact]
    public async Task Monitor_UnrepresentableDateReturnsStructuredErrorBeforeOpeningFiles()
    {
        var path = await CreateFileAsync("monitor-invalid-date.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var response = await new McpLogTools(backend).MonitorLogsAsync(
            [new(ConfiguredLogTargetKind.LogFile, "file")], dateOffsetDays: int.MaxValue);
        Assert.True(response.IsError);
        Assert.Equal("invalid_date_offset", response.StructuredContent!.Value.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(response.StructuredContent.Value.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Monitor_ExclusionOnlyRegexUsesSharedTimeOnlyPredicateRules()
    {
        var path = await CreateFileAsync("monitor-exclusion-only.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.MonitorLogsAsync(new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], ExcludeQuery = "drop$", UseRegex = true,
            StartTimestamp = "09:00", EndTimestamp = "11:00"
        });
        Assert.Empty(initial.Errors);
        await File.AppendAllTextAsync(path, "10:00:00 keep\n10:00:01 drop\n12:00:00 outside\n");
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.Equal("10:00:00 keep", Assert.Single(page.Result!.Files.SelectMany(file => file.Events)).Text);
        var invalid = await new McpLogTools(backend).MonitorLogsAsync(
            [new(ConfiguredLogTargetKind.LogFile, "file")], query: "");
        Assert.True(invalid.IsError);
        Assert.Equal("query_required", invalid.StructuredContent!.Value.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Monitor_InitialMissingFileDoesNotClaimRetainedBoundaryAndHealthyFileAdvances()
    {
        var missing = Path.Combine(_testDirectory, "monitor-initial-missing.log");
        var healthy = await CreateFileAsync("monitor-initial-healthy.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("missing", missing), ("healthy", healthy)));
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        Assert.True(initial.IsPartial);
        Assert.Equal(1, initial.Result!.StoppedFileCount);
        var stopped = initial.Result.Files[0];
        Assert.Equal("stopped", stopped.State);
        Assert.False(stopped.Error!.IsRetryable);
        Assert.Contains("could not start", stopped.Error.Message);
        Assert.DoesNotContain("retained", stopped.Error.Message);
        Assert.Contains("explicit intent", stopped.Error.Message);
        await File.AppendAllTextAsync(healthy, "healthy append\n");
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result.NextCursor });
        Assert.Equal("healthy append", Assert.Single(page.Result!.Files.SelectMany(file => file.Events)).Text);
        Assert.Equal(1, page.Result.StoppedFileCount);
    }

    private sealed class GatedMonitorScanner(Func<string, int, CancellationToken, Task> beforeOpen,
        Action<string>? afterOpen = null) : IResumableLogScanner
    {
        private readonly IResumableLogScanner _inner = new ResumableLogScanner(new FileEncodingDetectionService());
        private int _calls;
        public Regex? PrepareRegex(SearchRequest request) => _inner.PrepareRegex(request);
        public async Task<IResumableLogScan> OpenAsync(string path, ResumableFileCheckpoint checkpoint, CancellationToken ct)
        {
            await beforeOpen(path, Interlocked.Increment(ref _calls), ct);
            var reader = await _inner.OpenAsync(path, checkpoint, ct);
            try
            {
                afterOpen?.Invoke(path);
                return reader;
            }
            catch
            {
                await reader.DisposeAsync();
                throw;
            }
        }
    }
}
