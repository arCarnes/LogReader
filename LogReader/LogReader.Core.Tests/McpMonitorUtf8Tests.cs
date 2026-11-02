namespace LogReader.Core.Tests;

using System.Text;
using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackendTests
{
    public static IEnumerable<object[]> SplitUtf8Characters()
    {
        foreach (var character in new[] { "é", "€", "😀" })
            for (var split = 1; split < Encoding.UTF8.GetByteCount(character); split++)
                foreach (var bom in new[] { false, true })
                    yield return [character, split, bom];
    }

    [Theory]
    [MemberData(nameof(SplitUtf8Characters))]
    public async Task MonitorUtf8_SplitCharacterWaitsWithoutEvaluatingAndResumesByIdentity(string character, int split, bool bom)
    {
        var path = await CreateFileAsync("utf8-split.log", "");
        await File.WriteAllTextAsync(path, "old\n", new UTF8Encoding(bom));
        var offset = new FileInfo(path).Length;
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        var bytes = Encoding.UTF8.GetBytes(character);
        await AppendMonitorBytesAsync(path, bytes.AsMemory(0, split));
        var request = new LogMonitorQuery { Cursor = initial.Result!.NextCursor };
        var pending = await backend.MonitorLogsAsync(request);
        AssertUtf8Pending(pending);
        Assert.Same(pending, await backend.MonitorLogsAsync(request));

        var unchanged = await backend.MonitorLogsAsync(new() { Cursor = pending.Result!.NextCursor });
        AssertUtf8Pending(unchanged);
        Assert.Equal(0, unchanged.Result!.BytesEvaluated);
        await AppendMonitorBytesAsync(path, bytes.AsMemory(split));
        var completed = await backend.MonitorLogsAsync(new() { Cursor = unchanged.Result.NextCursor });
        Assert.Empty(completed.Errors);
        var file = Assert.Single(completed.Result!.Files);
        Assert.Equal(new LogMonitorEvent("appended", offset, character, false), Assert.Single(file.Events));
        Assert.Equal(pending.Result.Files[0].Generation, file.Generation);
        Assert.Equal(1, completed.Result.ExaminedLineCount);
        Assert.Equal(0, completed.Result.BacklogFileCount);
        Assert.True(completed.Result.IsCaughtUp);

        await AppendMonitorBytesAsync(path, new byte[] { 10 });
        var terminated = await backend.MonitorLogsAsync(new() { Cursor = completed.Result.NextCursor });
        Assert.Equal(new LogMonitorEvent("updated", offset, character, false),
            Assert.Single(Assert.Single(terminated.Result!.Files).Events));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitorUtf8_InitializationDuringSplitWriteRetainsUtf8Boundary(bool bom)
    {
        var path = await CreateFileAsync("utf8-initial.log", "");
        await File.WriteAllTextAsync(path, "old\n", new UTF8Encoding(bom));
        var offset = new FileInfo(path).Length;
        await AppendMonitorBytesAsync(path, new byte[] { 0xC3 });
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        Assert.Empty(initial.Errors);
        Assert.Equal(0, initial.Result!.StoppedFileCount);
        Assert.NotNull(initial.Result.NextCursor);
        await AppendMonitorBytesAsync(path, new byte[] { 0xA9, 10 });
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result.NextCursor });
        Assert.Equal(new LogMonitorEvent("appended", offset, "é", false),
            Assert.Single(Assert.Single(page.Result!.Files).Events));
    }

    [Theory]
    [InlineData("update", "updated")]
    [InlineData("regex", "removed")]
    [InlineData("exclude", "removed")]
    public async Task MonitorUtf8_PublishedLineChangesOnlyAfterCharacterCompletes(string filter, string kind)
    {
        var path = await CreateFileAsync("utf8-published.log", "");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.MonitorLogsAsync(new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")],
            Query = filter == "regex" ? "^a$" : "a", UseRegex = filter == "regex",
            ExcludeQuery = filter == "exclude" ? "é" : null
        });
        await AppendMonitorBytesAsync(path, new byte[] { (byte)'a' });
        var published = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.Equal(new LogMonitorEvent("appended", 0, "a", false),
            Assert.Single(Assert.Single(published.Result!.Files).Events));
        await AppendMonitorBytesAsync(path, new byte[] { 0xC3 });
        var pending = await backend.MonitorLogsAsync(new() { Cursor = published.Result.NextCursor });
        AssertUtf8Pending(pending);
        await AppendMonitorBytesAsync(path, new byte[] { 0xA9, 10 });
        var completed = await backend.MonitorLogsAsync(new() { Cursor = pending.Result!.NextCursor });
        Assert.Equal(new LogMonitorEvent(kind, 0, kind == "updated" ? "aé" : null, false),
            Assert.Single(Assert.Single(completed.Result!.Files).Events));
        Assert.Equal(published.Result.Files[0].Generation, completed.Result.Files[0].Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitorUtf8_HandoffWaitsAtCopiedBoundary(bool count)
    {
        var path = await CreateFileAsync("utf8-handoff.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var targets = new[] { new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file") };
        var handoff = count
            ? (await backend.CountLogsAsync(new() { Targets = targets, Query = "é", EnableHandoff = true })).Result!.HandoffCursor
            : (await backend.SearchLogsAsync(new() { Targets = targets, Query = "é", EnableHandoff = true })).Result!.HandoffCursor;
        await AppendMonitorBytesAsync(path, new byte[] { 0xC3 });
        var pending = await backend.MonitorLogsAsync(new() { HandoffCursor = handoff });
        AssertUtf8Pending(pending);
        await AppendMonitorBytesAsync(path, new byte[] { 0xA9, 10 });
        var completed = await backend.MonitorLogsAsync(new() { Cursor = pending.Result!.NextCursor });
        Assert.Equal(new LogMonitorEvent("appended", 4, "é", false),
            Assert.Single(Assert.Single(completed.Result!.Files).Events));
    }

    [Fact]
    public async Task MonitorUtf8_HandoffPreservesEstablishedAnsiEncoding()
    {
        var path = await CreateFileAsync("utf8-ansi-handoff.log", "");
        await File.WriteAllBytesAsync(path, new byte[] { 0xE9 });
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var source = await backend.SearchLogsAsync(new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "é", EnableHandoff = true
        });
        Assert.Equal("windows-1252", Assert.Single(source.Result!.Files).Encoding);
        var initial = await backend.MonitorLogsAsync(new() { HandoffCursor = source.Result.HandoffCursor });
        Assert.Empty(initial.Errors);
        Assert.Equal(0, initial.Result!.StoppedFileCount);
        await AppendMonitorBytesAsync(path, new byte[] { (byte)'x', 10 });
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result.NextCursor });
        Assert.Equal(new LogMonitorEvent("updated", 0, "éx", false),
            Assert.Single(Assert.Single(page.Result!.Files).Events));
        Assert.True(page.Result.IsCaughtUp);
    }

    [Fact]
    public async Task MonitorUtf8_WaitingFileDoesNotStarveHealthyFile()
    {
        var waiting = await CreateFileAsync("utf8-waiting.log", "old\n");
        var healthy = await CreateFileAsync("utf8-healthy.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("waiting", waiting), ("healthy", healthy)));
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        await AppendMonitorBytesAsync(waiting, new byte[] { 0xC3 });
        await File.AppendAllTextAsync(healthy, "healthy\n");
        var page = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        Assert.Empty(page.Errors);
        Assert.Equal(2, page.Result!.CheckedFileCount);
        Assert.Equal(1, page.Result.BacklogFileCount);
        Assert.False(page.Result.IsCaughtUp);
        Assert.Equal("healthy", Assert.Single(page.Result.Files.SelectMany(file => file.Events)).Text);
        Assert.All(page.Result.Files, file => Assert.Null(file.Error));
    }

    [Fact]
    public async Task MonitorUtf8_CancellationPreservesWaitingBytesForRetry()
    {
        var path = await CreateFileAsync("utf8-cancel.log", "old\n");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new GatedMonitorScanner(async (_, call, ct) =>
        {
            if (call == 3)
            {
                ready.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        });
        using var backend = CreateBackend(CreateSnapshot(("file", path)), scanner: scanner);
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        await AppendMonitorBytesAsync(path, new byte[] { 0xC3 });
        var pending = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        AssertUtf8Pending(pending);
        await AppendMonitorBytesAsync(path, new byte[] { 0xA9, 10 });
        using var cancellation = new CancellationTokenSource();
        var request = new LogMonitorQuery { Cursor = pending.Result!.NextCursor };
        var poll = backend.MonitorLogsAsync(request, cancellation.Token);
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally
        {
            cancellation.Cancel();
            await poll.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Null((await poll).Result);
        var retry = await backend.MonitorLogsAsync(request);
        Assert.Equal(new LogMonitorEvent("appended", 4, "é", false),
            Assert.Single(Assert.Single(retry.Result!.Files).Events));
    }

    [Fact]
    public async Task MonitorUtf8_MalformedContinuationStillStopsOnEncodingChange()
    {
        var path = await CreateFileAsync("utf8-invalid.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.MonitorLogsAsync(MonitorStart());
        await AppendMonitorBytesAsync(path, new byte[] { 0xC3 });
        var pending = await backend.MonitorLogsAsync(new() { Cursor = initial.Result!.NextCursor });
        AssertUtf8Pending(pending);
        await AppendMonitorBytesAsync(path, new byte[] { (byte)'x', 10 });
        var stopped = await backend.MonitorLogsAsync(new() { Cursor = pending.Result!.NextCursor });
        Assert.Equal("stopped", stopped.Result!.State);
        Assert.Null(stopped.Result.NextCursor);
        Assert.Equal("log_encoding_changed", Assert.Single(stopped.Result.Files).Error!.Code);
    }

    private static void AssertUtf8Pending(LogOperationEnvelope<LogMonitorResult> response)
    {
        Assert.Empty(response.Errors);
        Assert.False(response.IsPartial);
        var result = Assert.IsType<LogMonitorResult>(response.Result);
        Assert.NotNull(result.NextCursor);
        Assert.Equal(0, result.StoppedFileCount);
        Assert.Equal(1, result.BacklogFileCount);
        Assert.False(result.IsCaughtUp);
        Assert.False(result.IsIdle);
        Assert.Equal(0, result.ExaminedLineCount);
        Assert.Equal(0, result.TimestampCoverage.EvaluatedLineCount);
        var file = Assert.Single(result.Files);
        Assert.Equal("backlog", file.State);
        Assert.Null(file.Error);
        Assert.Empty(file.Events);
    }

    private static async Task AppendMonitorBytesAsync(string path, ReadOnlyMemory<byte> bytes)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await stream.WriteAsync(bytes);
    }
}
