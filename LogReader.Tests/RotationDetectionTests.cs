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

        // Give Task.Run a moment to start and record the initial file size as baseline.
        // 100ms is well above the typical thread-pool start time and shorter than the
        // 250ms poll interval, so the first append will be seen on the very next poll.
        await Task.Delay(100);
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

        // Give Task.Run a moment to start and snapshot the initial file size.
        // The truncation below will then be detected on the first poll at ~250ms.
        await Task.Delay(100);

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
        await File.WriteAllTextAsync(path, ""); // start empty so first write is detectable

        using var tailService = new FileTailService();
        var tcsReady = new TaskCompletionSource();
        int eventCount = 0;

        tailService.LinesAppended += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref eventCount);
                tcsReady.TrySetResult(); // signal that the service is actively polling
            }
        };

        tailService.StartTailing(path, FileEncoding.Utf8);

        // Give Task.Run a moment to start and record lastSize=0 before we write.
        await Task.Delay(100);

        // Write content and wait for the event — this confirms the service is polling
        // and has completed at least one full cycle.
        await File.AppendAllTextAsync(path, "Warmup line\n");
        var readyResult = await Task.WhenAny(tcsReady.Task, Task.Delay(5000));
        Assert.Equal(tcsReady.Task, readyResult); // warmup event must arrive

        // StopTailing cancels the loop and blocks until the Task completes,
        // so the service is guaranteed stopped when this call returns.
        tailService.StopTailing(path);
        var countAfterStop = Volatile.Read(ref eventCount);

        // Append after stopping — the dead loop cannot fire any more events.
        await File.AppendAllTextAsync(path, "Should not trigger\n");

        // One poll cycle worth of observation to confirm silence.
        await Task.Delay(300);

        Assert.Equal(countAfterStop, Volatile.Read(ref eventCount));
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
