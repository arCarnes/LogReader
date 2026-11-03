namespace LogReader.Core.Tests;

using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackendTests
{
    [Fact]
    public async Task Monitor_TotalBudgetRetainsUnconsumedSlotAndOrderedSweepProgress()
    {
        var hot = new List<string>();
        for (var i = 0; i < 16; i++) hot.Add(await CreateFileAsync($"ordered-hot-{i:D2}.log", "old\n"));
        var quiet = await CreateFileAsync("ordered-quiet.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(hot.Select((path, i) => ($"hot-{i:D2}", path))
            .Append(("quiet", quiet)).ToArray()));
        var initial = await backend.MonitorLogsAsync(MonitorStart("needle"));
        Assert.Empty(initial.Errors);
        foreach (var path in hot) await File.AppendAllTextAsync(path, new string('x', 300_000) + "\n");
        await File.AppendAllTextAsync(quiet, "needle quiet\n");

        var request = new LogMonitorQuery { Cursor = initial.Result!.NextCursor, MaxFiles = 100 };
        var bounded = await backend.MonitorLogsAsync(request);
        Assert.Empty(bounded.Errors);
        Assert.False(bounded.IsPartial);
        Assert.Equal(4 * 1024 * 1024, bounded.Result!.BytesEvaluated);
        Assert.Equal(16, bounded.Result.CheckedFileCount);
        Assert.Equal(1, bounded.Result.UncheckedFileCount);
        // The quiet slot has not been opened yet: its new bytes are unknown, while its
        // unchecked slot remains explicit. Backlog counts only the 16 observed hot files.
        Assert.Equal(16, bounded.Result.BacklogFileCount);
        Assert.False(bounded.Result.IsScopeChecked);
        Assert.False(bounded.Result.IsCaughtUp);
        Assert.False(bounded.Result.IsIdle);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => $"hot-{i:D2}"), bounded.Result.Files.Select(file => file.FileId));
        Assert.All(bounded.Result.Files, file => { Assert.Null(file.Error); Assert.Empty(file.Events); });
        Assert.Same(bounded, await backend.MonitorLogsAsync(request));

        var completed = await backend.MonitorLogsAsync(new() { Cursor = bounded.Result.NextCursor, MaxFiles = 100 });
        Assert.Empty(completed.Errors);
        Assert.Equal(1, completed.Result!.CheckedFileCount);
        Assert.Equal(0, completed.Result.UncheckedFileCount);
        Assert.Equal(16, completed.Result.BacklogFileCount);
        Assert.True(completed.Result.IsScopeChecked);
        Assert.False(completed.Result.IsCaughtUp);
        Assert.False(completed.Result.IsIdle);
        var file = Assert.Single(completed.Result.Files);
        Assert.Equal("quiet", file.FileId);
        Assert.Equal(new LogMonitorEvent("appended", 4, "needle quiet", false), Assert.Single(file.Events));

        var drained = await backend.MonitorLogsAsync(new() { Cursor = completed.Result.NextCursor, MaxFiles = 100 });
        Assert.Empty(drained.Errors);
        Assert.True(drained.Result!.IsCaughtUp);
        Assert.False(drained.Result.IsIdle);
        Assert.Equal(17, drained.Result.CheckedFileCount);
        Assert.Equal(0, drained.Result.BacklogFileCount);
        Assert.Empty(drained.Result.Files.SelectMany(item => item.Events));
        var released = await backend.MonitorLogsAsync(new() { Cursor = drained.Result.NextCursor, Release = true });
        Assert.Equal("released", released.Result!.State);
    }
}
