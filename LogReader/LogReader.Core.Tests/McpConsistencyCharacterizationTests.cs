namespace LogReader.Core.Tests;

using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackendTests
{
    [Fact]
    public async Task Consistency_SnapshotCompletionDoesNotObserveLaterAppends()
    {
        var path = await CreateFileAsync("bounded-characterization.log", "needle\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var snapshot = await backend.SearchLogsAsync(new LogSearchQuery
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle"
        });
        Assert.True(snapshot.Result!.IsQueryComplete);
        Assert.Null(snapshot.Result.NextCursor);
        await File.AppendAllTextAsync(path, "needle later\n");
        Assert.Equal(1, snapshot.Result.MatchingLineCount);
        Assert.Single(snapshot.Result.Files.SelectMany(file => file.Hits));
    }

    [Fact]
    public async Task Consistency_EmptyFilteredTailIsNotIdleUntilBacklogExamined()
    {
        var path = await CreateFileAsync("tail-characterization.log", "old\n");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var initial = await backend.ReadLogTailAsync(new LogReadTailQuery { FileId = "file", Query = "needle" });
        await File.AppendAllTextAsync(path, "miss\nneedle\n");
        var page = await backend.ReadLogTailAsync(new LogReadTailQuery
        {
            FileId = "file", Query = "needle", Cursor = initial.Result!.NextCursor, MaxLines = 1
        });
        Assert.False(page.Result!.IsIdle);
        Assert.Empty(page.Result.File!.Lines);
        Assert.True(page.Result.RemainingLineCount > 0);
        Assert.NotEqual(initial.Result.NextCursor, page.Result.NextCursor);
    }
}
