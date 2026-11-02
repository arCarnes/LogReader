namespace LogReader.Core.Tests;

using System.Text;
using LogReader.Core;
using LogReader.Core.Models;

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
