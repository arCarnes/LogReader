namespace LogReader.Core.Tests;

using System.Diagnostics;
using System.Text;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed partial class HeadlessLogQueryBackendTests
{
    [Theory]
    [InlineData("replace")]
    [InlineData("truncate")]
    [InlineData("encoding")]
    public async Task MonitorLifecycle_StopsChangedFileAndContinuesHealthyFile(string change)
    {
        var changed = await CreateFileAsync("lifecycle-changed.log", "long old content\n");
        var healthy = await CreateFileAsync("lifecycle-healthy.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("changed", changed), ("healthy", healthy)));
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        if (change == "replace") File.Delete(changed);
        if (change == "encoding") await File.WriteAllTextAsync(changed, "new utf16 content\n", Encoding.Unicode);
        else await File.WriteAllTextAsync(changed, "new\n");
        await File.AppendAllTextAsync(healthy, "healthy\n");
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.True(page.IsPartial);
        Assert.Equal(1, page.Result!.StoppedFileCount);
        Assert.Equal("stopped", page.Result.Files[0].State);
        Assert.NotNull(page.Result.Files[0].Error);
        Assert.Equal("healthy", Assert.Single(page.Result.Files[1].Events).Text);
        Assert.False(page.Result.IsIdle);
    }

    [Fact]
    public async Task MonitorLifecycle_UnfinishedMatchingLineIsUpdatedThenRemovedByIdentity()
    {
        var path = await CreateFileAsync("lifecycle-unfinished.log", "");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.MonitorLogsAsync(new()
        { Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "^needle$", UseRegex = true });
        await File.AppendAllTextAsync(path, "needle");
        var first = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        var hit = Assert.Single(Assert.Single(first.Result!.Files).Events);
        Assert.Equal("appended", hit.Kind);
        await File.AppendAllTextAsync(path, "x");
        var second = await backend.MonitorLogsAsync(new() { Cursor = first.Result.NextCursor });
        var removed = Assert.Single(Assert.Single(second.Result!.Files).Events);
        Assert.Equal("removed", removed.Kind);
        Assert.Equal(hit.ByteOffset, removed.ByteOffset);
        Assert.Equal(first.Result.Files[0].Generation, second.Result.Files[0].Generation);
        Assert.False(second.Result.IsIdle);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MonitorLifecycle_GrowthWhileEofMatcherPendingPreservesLineIdentity(bool regex, bool stopsMatching)
    {
        var path = await CreateFileAsync("lifecycle-pending-eof.log", "");
        ResumableFileCheckpoint? activeCheckpoint = null;
        var yieldAtMatcher = 1;
        var ticks = 0L;
        long Clock()
        {
            // Yield from actual matcher readiness, independently of clock call counts.
            if (Volatile.Read(ref yieldAtMatcher) != 0 && Volatile.Read(ref activeCheckpoint)?.Matcher != null)
                return Interlocked.Add(ref ticks, Stopwatch.Frequency);
            return Interlocked.Read(ref ticks);
        }
        var scanner = new MonitorOpenHookScanner(new ResumableLogScanner(new FileEncodingDetectionService()),
            (_, checkpoint, _) =>
            {
                Volatile.Write(ref activeCheckpoint, checkpoint);
                return Task.CompletedTask;
            });
        using var backend = CreateBackend(CreateSnapshot(("file", path)), scanner: scanner, scanTimestamp: Clock,
            limits: LogQueryEffectiveLimits.Default with { SearchWorkMilliseconds = 1 });
        var initial = await backend.MonitorLogsAsync(new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")],
            Query = regex ? stopsMatching ? "^a$" : "^a+$" : "a", UseRegex = regex
        });
        Assert.Empty(initial.Errors);
        await File.AppendAllTextAsync(path, "a");
        var pendingRequest = new LogMonitorQuery { Cursor = initial.Result!.NextCursor };
        var pending = await backend.MonitorLogsAsync(pendingRequest);
        Assert.Empty(pending.Errors);
        var checkpoint = Volatile.Read(ref activeCheckpoint)!;
        Assert.NotNull(checkpoint.Matcher);
        Assert.Equal(checkpoint.Extent, checkpoint.ReadOffset);
        Assert.False(checkpoint.Done);
        Assert.Empty(Assert.Single(pending.Result!.Files).Events);
        Assert.Equal(1, pending.Result.BacklogFileCount);
        Assert.False(pending.Result.IsCaughtUp);
        Assert.False(pending.Result.IsIdle);
        Assert.Same(pending, await backend.MonitorLogsAsync(pendingRequest));

        Volatile.Write(ref yieldAtMatcher, 0);
        await File.AppendAllTextAsync(path, stopsMatching ? "b\n" : "a\n");
        var resumeRequest = new LogMonitorQuery { Cursor = pending.Result.NextCursor };
        var resumed = await backend.MonitorLogsAsync(resumeRequest);
        Assert.Empty(resumed.Errors);
        var fragmentFile = Assert.Single(resumed.Result!.Files);
        var fragment = Assert.Single(fragmentFile.Events);
        Assert.Equal(new LogMonitorEvent("appended", 0, "a", false), fragment);
        Assert.Equal(1, resumed.Result.BacklogFileCount);
        Assert.True(fragmentFile.RemainingBytes > 0);
        Assert.False(resumed.Result.IsCaughtUp);
        Assert.False(resumed.Result.IsIdle);
        Assert.Same(resumed, await backend.MonitorLogsAsync(resumeRequest));

        var finishRequest = new LogMonitorQuery { Cursor = resumed.Result.NextCursor };
        var finished = await backend.MonitorLogsAsync(finishRequest);
        Assert.Empty(finished.Errors);
        var finalFile = Assert.Single(finished.Result!.Files);
        var finalEvent = Assert.Single(finalFile.Events);
        Assert.Equal(stopsMatching ? "removed" : "updated", finalEvent.Kind);
        Assert.Equal(stopsMatching ? null : "aa", finalEvent.Text);
        Assert.Equal(fragment.ByteOffset, finalEvent.ByteOffset);
        Assert.NotNull(fragmentFile.Generation);
        Assert.Equal(fragmentFile.Generation, finalFile.Generation);
        Assert.Equal(0, finished.Result.BacklogFileCount);
        Assert.True(finished.Result.IsCaughtUp);
        Assert.False(finished.Result.IsIdle);
        Assert.Same(finished, await backend.MonitorLogsAsync(finishRequest));
    }

    [Fact]
    public async Task MonitorLifecycle_NonmatchingFragmentBecomesNewMatch()
    {
        var path = await CreateFileAsync("lifecycle-first-match.log", "");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.MonitorLogsAsync(MonitorStart("needle"));
        await File.AppendAllTextAsync(path, "nee");
        var first = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        var firstFile = Assert.Single(first.Result!.Files);
        Assert.Empty(firstFile.Events);
        await File.AppendAllTextAsync(path, "dle\n");
        var second = await backend.MonitorLogsAsync(new() { Cursor = first.Result.NextCursor });
        var secondFile = Assert.Single(second.Result!.Files);
        Assert.Equal(new LogMonitorEvent("appended", 0, "needle", false), Assert.Single(secondFile.Events));
        Assert.Equal(firstFile.Generation, secondFile.Generation);
    }

    [Fact]
    public async Task MonitorLifecycle_RemovedUnfinishedLineRematchesAsAppended()
    {
        var path = await CreateFileAsync("lifecycle-rematch.log", "");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.MonitorLogsAsync(new()
        { Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "^a(?:bc)?$", UseRegex = true });
        await File.AppendAllTextAsync(path, "a");
        var first = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        var firstFile = Assert.Single(first.Result!.Files);
        Assert.Equal(new LogMonitorEvent("appended", 0, "a", false), Assert.Single(firstFile.Events));
        await File.AppendAllTextAsync(path, "b");
        var second = await backend.MonitorLogsAsync(new() { Cursor = first.Result.NextCursor });
        var secondFile = Assert.Single(second.Result!.Files);
        Assert.Equal(new LogMonitorEvent("removed", 0, null, false), Assert.Single(secondFile.Events));
        await File.AppendAllTextAsync(path, "c\n");
        var third = await backend.MonitorLogsAsync(new() { Cursor = second.Result.NextCursor });
        var thirdFile = Assert.Single(third.Result!.Files);
        Assert.Equal(new LogMonitorEvent("appended", 0, "abc", false), Assert.Single(thirdFile.Events));
        Assert.Equal(firstFile.Generation, secondFile.Generation);
        Assert.Equal(firstFile.Generation, thirdFile.Generation);
    }

    [Fact]
    public async Task MonitorLifecycle_InitialUnfinishedLineAndSplitCrLfHaveNoGapOrPhantomLine()
    {
        var path = await CreateFileAsync("lifecycle-crlf.log", "old\r");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        await File.AppendAllTextAsync(path, "\nnew");
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.Equal("new", Assert.Single(page.Result!.Files.SelectMany(file => file.Events)).Text);
        await File.AppendAllTextAsync(path, " extended\nnext\n");
        var second = await backend.MonitorLogsAsync(new() { Cursor = page.Result.NextCursor });
        Assert.Equal(new[] { "new extended", "next" }, second.Result!.Files.SelectMany(file => file.Events).Select(item => item.Text));
        Assert.Equal("updated", second.Result.Files[0].Events[0].Kind);
    }

    [Fact]
    public async Task MonitorLifecycle_EndPeriodIsTerminalAndDoesNoLaterDiskWork()
    {
        var now = DateTimeOffset.UtcNow;
        var path = await CreateFileAsync("lifecycle-period.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)), now: () => now);
        var initial = await backend.MonitorLogsAsync(new()
        { Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], ObserveForMilliseconds = 1000 });
        now = now.AddSeconds(1);
        await File.AppendAllTextAsync(path, "too late\n");
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.Equal("period_ended", page.Result!.State);
        Assert.Null(page.Result.NextCursor);
        Assert.Empty(page.Result.Files);
        Assert.False(page.Result.IsCaughtUp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handoff_CopiesSnapshotBoundariesWithoutGapsAndRetainsSource(bool count)
    {
        var path = await CreateFileAsync("handoff.log", "needle before\n");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new GatedMonitorScanner(async (_, call, ct) =>
        {
            if (call == 2) { ready.TrySetResult(); await release.Task.WaitAsync(ct); }
        });
        using var backend = CreateBackend(CreateSnapshot(("file", path)), scanner: scanner);
        var targets = new[] { new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file") };
        var cursor = count
            ? (await backend.CountLogsAsync(new() { Targets = targets, Query = "needle", EnableHandoff = true })).Result!.HandoffCursor
            : (await backend.SearchLogsAsync(new() { Targets = targets, Query = "needle", EnableHandoff = true })).Result!.HandoffCursor;
        Assert.NotNull(cursor);
        await File.AppendAllTextAsync(path, "needle between\n");
        var handoff = backend.MonitorLogsAsync(new() { HandoffCursor = cursor });
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await File.AppendAllTextAsync(path, "needle during\n");
        }
        finally { release.TrySetResult(); await handoff.WaitAsync(TimeSpan.FromSeconds(3)); }
        var initial = await handoff;
        Assert.Empty(initial.Errors);
        Assert.Equal(new[] { "needle between", "needle during" }, initial.Result!.Files.SelectMany(file => file.Events).Select(item => item.Text));
        await File.AppendAllTextAsync(path, "needle after\n");
        var poll = new LogMonitorQuery { Cursor = initial.Result.NextCursor };
        var page = await backend.MonitorLogsAsync(poll);
        Assert.Same(page, await backend.MonitorLogsAsync(poll));
        Assert.Equal("needle after", Assert.Single(page.Result!.Files.SelectMany(file => file.Events)).Text);
        var independent = await backend.MonitorLogsAsync(new() { HandoffCursor = cursor });
        Assert.Empty(independent.Errors); // Copying the same source never consumes or refreshes it.
        Assert.Equal(3, independent.Result!.Files.SelectMany(file => file.Events).Count());
    }

    [Fact]
    public async Task Handoff_UnfinishedLineUsesSnapshotMatchIdentityAndFrozenTimePredicate()
    {
        var path = await CreateFileAsync("handoff-unfinished.log", "10:00:00 needle");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var snapshot = await backend.SearchLogsAsync(new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", EnableHandoff = true,
            ExcludeQuery = "drop", StartTimestamp = "09:00", EndTimestamp = "11:00"
        });
        await File.AppendAllTextAsync(path, " drop\n10:00:01 needle\n12:00:00 needle outside\n");
        var handoff = await backend.MonitorLogsAsync(new() { HandoffCursor = snapshot.Result!.HandoffCursor });
        Assert.Empty(handoff.Errors);
        var events = handoff.Result!.Files.SelectMany(file => file.Events).ToArray();
        Assert.Equal(new[] { "removed", "appended" }, events.Select(item => item.Kind));
        Assert.Equal(0, events[0].ByteOffset);
        Assert.Equal("10:00:01 needle", events[1].Text);
        Assert.Equal(1, handoff.Result.TimestampCoverage.OutsideTimeRangeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handoff_NonmatchingUnfinishedLineBecomesNewMatch(bool count)
    {
        var path = await CreateFileAsync("handoff-first-match.log", "nee");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var targets = new[] { new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file") };
        string? cursor;
        if (count)
        {
            var snapshot = await backend.CountLogsAsync(new() { Targets = targets, Query = "needle", EnableHandoff = true });
            Assert.Empty(snapshot.Errors);
            Assert.Equal(0, snapshot.Result!.MatchingLineCount);
            cursor = snapshot.Result.HandoffCursor;
        }
        else
        {
            var snapshot = await backend.SearchLogsAsync(new() { Targets = targets, Query = "needle", EnableHandoff = true });
            Assert.Empty(snapshot.Errors);
            Assert.Equal(0, snapshot.Result!.MatchingLineCount);
            cursor = snapshot.Result.HandoffCursor;
        }
        Assert.NotNull(cursor);
        await File.AppendAllTextAsync(path, "dle\n");
        var handoff = await backend.MonitorLogsAsync(new() { HandoffCursor = cursor });
        Assert.Empty(handoff.Errors);
        Assert.Equal(new LogMonitorEvent("appended", 0, "needle", false),
            Assert.Single(Assert.Single(handoff.Result!.Files).Events));
        Assert.True(handoff.Result.IsCaughtUp);
    }

    [Fact]
    public async Task Handoff_PartialExpiredAndChangedCatalogSourcesRejectWithoutFallback()
    {
        var now = DateTimeOffset.UtcNow;
        var path = await CreateFileAsync("handoff-partial.log", "needle\nneedle\n");
        var catalog = new MutableCatalogReader(CreateSnapshot(("file", path)));
        using var backend = CreateBackend(catalog.Snapshot, now: () => now, catalogReader: catalog);
        var partial = await backend.SearchLogsAsync(new()
        { Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", EnableHandoff = true, MaxQueryHits = 1 });
        var rejected = await backend.MonitorLogsAsync(new() { HandoffCursor = partial.Result!.HandoffCursor });
        Assert.Equal("handoff_boundary_unavailable", Assert.Single(rejected.Errors).Code);
        Assert.Null(rejected.Result);
        now = now.AddMinutes(16);
        var expired = await backend.MonitorLogsAsync(new() { HandoffCursor = partial.Result.HandoffCursor });
        Assert.Equal("expired_monitor_cursor", Assert.Single(expired.Errors).Code);
    }
    [Fact]
    public async Task Handoff_CancellationLeavesSourceUsableAndCatalogChangeRejectsIt()
    {
        var path = await CreateFileAsync("handoff-cancel.log", "before\n");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new GatedMonitorScanner(async (_, call, ct) =>
        {
            if (call == 2) { ready.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        });
        var catalog = new MutableCatalogReader(CreateSnapshot(("file", path)));
        using var backend = CreateBackend(catalog.Snapshot, scanner: scanner, catalogReader: catalog);
        var source = await backend.SearchLogsAsync(new()
        { Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "before", EnableHandoff = true });
        using var cancellation = new CancellationTokenSource();
        var pending = backend.MonitorLogsAsync(new() { HandoffCursor = source.Result!.HandoffCursor }, cancellation.Token);
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { cancellation.Cancel(); await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Null((await pending).Result);
        var retry = await backend.MonitorLogsAsync(new() { HandoffCursor = source.Result.HandoffCursor });
        Assert.Empty(retry.Errors);
        Assert.NotNull(retry.Result!.NextCursor);
        catalog.Snapshot = CreateSnapshot(("changed", path));
        var rejected = await backend.MonitorLogsAsync(new() { HandoffCursor = source.Result.HandoffCursor });
        Assert.Equal("stale_monitor_cursor", Assert.Single(rejected.Errors).Code);
        Assert.Null(rejected.Result);
    }

    [Fact]
    public async Task MonitorLifecycle_SelectedDateDoesNotFollowAnotherDatedPathAtMidnight()
    {
        var date = new DateOnly(2026, 8, 4);
        var directory = Path.Combine(_testDirectory, "2026-08-03");
        Directory.CreateDirectory(directory);
        var selected = Path.Combine(directory, "app.log");
        await File.WriteAllTextAsync(selected, "old\n");
        var otherDirectory = Path.Combine(_testDirectory, "2026-08-04");
        Directory.CreateDirectory(otherDirectory);
        var other = Path.Combine(otherDirectory, "app.log");
        await File.WriteAllTextAsync(other, "other date\n");
        var snapshot = new ConfiguredLogCatalogSnapshot(1,
            [new("dashboard", "Dashboard", 0, null, LogGroupKind.Dashboard, ["file"])],
            [new("file", Path.Combine(_testDirectory, "current", "app.log"))],
            [new("pattern", "Pattern", "current", "{yyyy-MM-dd}")]);
        using var backend = CreateBackend(snapshot, today: () => date);
        var initial = await backend.MonitorLogsAsync(new()
        { Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], DateOffsetDays = 1 });
        date = date.AddDays(1);
        await File.AppendAllTextAsync(selected, "selected append\n");
        await File.AppendAllTextAsync(other, "wrong date append\n");
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.Equal("2026-08-03", page.Result!.SelectedDate);
        Assert.Equal("selected append", Assert.Single(page.Result.Files.SelectMany(file => file.Events)).Text);
    }

    [Fact]
    public async Task MonitorLifecycle_SplitCrLfDoesNotSwallowSubsequentEmptyLine()
    {
        var path = await CreateFileAsync("monitor-empty-after-crlf.log", "old\r");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        await File.AppendAllTextAsync(path, "\n");
        var completion = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.Empty(completion.Result!.Files.SelectMany(file => file.Events));
        await File.AppendAllTextAsync(path, "\n");
        var blank = await backend.MonitorLogsAsync(new() { Cursor = completion.Result.NextCursor });
        var line = Assert.Single(blank.Result!.Files.SelectMany(file => file.Events));
        Assert.Equal("", line.Text);
        Assert.Equal(5, line.ByteOffset);
    }

    [Fact]
    public async Task MonitorLifecycle_Utf16HalfCodeUnitRemainsBacklogUntilCompleted()
    {
        var path = Path.Combine(_testDirectory, "monitor-half-unit.log");
        await File.WriteAllTextAsync(path, "old\n", Encoding.Unicode);
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            await stream.WriteAsync(new byte[] { 65 });
        var half = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.Equal(1, half.Result!.BacklogFileCount);
        Assert.False(half.Result.IsCaughtUp);
        Assert.False(half.Result.IsIdle);
        Assert.Empty(half.Result.Files.SelectMany(file => file.Events));
        await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            await stream.WriteAsync(new byte[] { 0 });
        var whole = await backend.MonitorLogsAsync(new() { Cursor = half.Result.NextCursor });
        Assert.Equal("A", Assert.Single(whole.Result!.Files.SelectMany(file => file.Events)).Text);
        Assert.Equal(0, whole.Result.BacklogFileCount);
    }

    [Fact]
    public async Task Handoff_QueryCapCannotSkipPrefetchedUncommittedMatches()
    {
        var path = await CreateFileAsync("handoff-prefetched-cap.log", "needle one\nneedle two\nneedle three\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var source = await backend.SearchLogsAsync(new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", EnableHandoff = true,
            MaxQueryHits = 1, IncludeContextAfter = 2
        });
        Assert.False(source.Result!.IsTraversalComplete);
        Assert.Equal(1, source.Result.MatchingLineCount);
        var handoff = await backend.MonitorLogsAsync(new() { HandoffCursor = source.Result.HandoffCursor });
        Assert.Null(handoff.Result);
        Assert.Equal("handoff_boundary_unavailable", Assert.Single(handoff.Errors).Code);
    }

    [Theory]
    [InlineData(FileEncoding.Utf16)]
    [InlineData(FileEncoding.Utf16Be)]
    public async Task MonitorLifecycle_BomlessUtf16UnfinishedLineRetainsItsFirstCharacter(FileEncoding encoding)
    {
        var path = Path.Combine(_testDirectory, $"monitor-bomless-{encoding}.log");
        var decoder = EncodingHelper.GetEncoding(encoding);
        await File.WriteAllBytesAsync(path, decoder.GetBytes("first"));
        using var backend = CreateBackend(CreateSnapshot(("file", path)),
            encodingDetection: new CountingEncodingDetectionService(encoding));
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            await stream.WriteAsync(decoder.GetBytes(" more\n"));
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.Equal("first more", Assert.Single(page.Result!.Files.SelectMany(file => file.Events)).Text);
        Assert.True(page.Result.IsCaughtUp);
    }

}
