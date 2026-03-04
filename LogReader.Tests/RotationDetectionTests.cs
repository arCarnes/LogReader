namespace LogReader.Tests;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class RotationDetectionTests : IAsyncLifetime
{
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TailService_DetectsNewContent()
    {
        var path = Path.Combine(_testDir, "tail.log");
        await File.WriteAllTextAsync(path, "Initial line\n");

        using var tailService = new FileTailService();
        var tcs = new TaskCompletionSource<TailEventArgs>();

        tailService.LinesAppended += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(e);
        };

        tailService.StartTailing(path, FileEncoding.Utf8);

        // Wait a bit then append
        await Task.Delay(400);
        await File.AppendAllTextAsync(path, "Appended line\n");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Equal(tcs.Task, result);

        var args = await tcs.Task;
        Assert.Equal(path, args.FilePath);
    }

    [Fact]
    public async Task TailService_DetectsRotation_ByTruncation()
    {
        var path = Path.Combine(_testDir, "rotate.log");
        await File.WriteAllTextAsync(path, "Line 1\nLine 2\nLine 3\n");

        using var tailService = new FileTailService();
        var tcs = new TaskCompletionSource<FileRotatedEventArgs>();

        tailService.FileRotated += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(e);
        };

        tailService.StartTailing(path, FileEncoding.Utf8);

        // Wait for initial read
        await Task.Delay(600);

        // Simulate rotation: truncate and write new content
        await File.WriteAllTextAsync(path, "New content\n");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Equal(tcs.Task, result);

        var args = await tcs.Task;
        Assert.Equal(path, args.FilePath);
    }

    [Fact]
    public async Task TailService_StopTailing_StopsEvents()
    {
        var path = Path.Combine(_testDir, "stop.log");
        await File.WriteAllTextAsync(path, "Initial\n");

        using var tailService = new FileTailService();
        int eventCount = 0;

        tailService.LinesAppended += (_, _) => Interlocked.Increment(ref eventCount);

        tailService.StartTailing(path, FileEncoding.Utf8);
        await Task.Delay(400);

        tailService.StopTailing(path);
        await Task.Delay(200);

        // Append after stopping - should not trigger events
        await File.AppendAllTextAsync(path, "Should not trigger\n");
        await Task.Delay(600);

        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task UpdateIndex_DetectsTruncation_AsRotation()
    {
        var reader = new ChunkedLogReaderService();
        var path = Path.Combine(_testDir, "rotated.log");
        await File.WriteAllTextAsync(path, "Line 1\nLine 2\nLine 3\n");

        var index = await reader.BuildIndexAsync(path, FileEncoding.Utf8);
        Assert.Equal(3, index.LineCount);

        // Simulate rotation: truncate file
        await File.WriteAllTextAsync(path, "Rotated line 1\n");

        var updated = await reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        // Should have rebuilt (file smaller = truncated)
        Assert.Equal(1, updated.LineCount);
        var line = await reader.ReadLineAsync(path, updated, 0, FileEncoding.Utf8);
        Assert.Equal("Rotated line 1", line);
    }
}
