namespace LogReader.Tests;

using System.Reflection;
using LogReader.Core;
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
        AppPaths.SetRootPathForTests(_testDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        AppPaths.SetRootPathForTests(null);
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

        // StopTailing cancels asynchronously; give the loop a moment to observe cancellation.
        tailService.StopTailing(path);
        await Task.Delay(100);
        var countAfterStop = Volatile.Read(ref eventCount);

        // Append after stopping — the dead loop cannot fire any more events.
        await File.AppendAllTextAsync(path, "Should not trigger\n");

        // One poll cycle worth of observation to confirm silence.
        await Task.Delay(300);

        Assert.Equal(countAfterStop, Volatile.Read(ref eventCount));
    }

    [Fact]
    public async Task TailService_StopTailing_ReturnsQuickly()
    {
        var path = Path.Combine(_testDir, "stop-fast.log");
        await File.WriteAllTextAsync(path, "Initial\n");

        using var tailService = new FileTailService();
        tailService.StartTailing(path, FileEncoding.Utf8);
        await Task.Delay(100);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        tailService.StopTailing(path);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 250,
            $"StopTailing took {sw.ElapsedMilliseconds}ms; expected non-blocking cancellation");
    }

    [Fact]
    public async Task TailService_Dispose_WaitsForTrackedTailTaskToFinish()
    {
        var path = Path.Combine(_testDir, "dispose-waits.log");
        await File.WriteAllTextAsync(path, "Initial\n");

        using var inspectionTailService = new FileTailService();
        inspectionTailService.StartTailing(path, FileEncoding.Utf8);
        await Task.Delay(100);

        var tailedFilesField = typeof(FileTailService).GetField("_tailedFiles", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(tailedFilesField);
        var tailedFiles = tailedFilesField!.GetValue(inspectionTailService)!;
        var tryGetValue = tailedFiles.GetType().GetMethod("TryGetValue");
        Assert.NotNull(tryGetValue);

        var args = new object?[] { path, null };
        Assert.True((bool)tryGetValue!.Invoke(tailedFiles, args)!);
        var tailState = args[1];
        Assert.NotNull(tailState);

        var taskProperty = tailState!.GetType().GetProperty("Task", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(taskProperty);
        var tailTask = (Task)taskProperty!.GetValue(tailState)!;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        inspectionTailService.Dispose();
        sw.Stop();

        var completed = await Task.WhenAny(tailTask, Task.Delay(5000));
        Assert.Same(tailTask, completed);
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"Dispose took {sw.ElapsedMilliseconds}ms; expected bounded shutdown.");
    }

    [Fact]
    public async Task TailService_UnexpectedError_RaisesTailError()
    {
        var path = Path.Combine(_testDir, "tail-error.log");
        await File.WriteAllTextAsync(path, string.Empty);

        using var tailService = new FileTailService();
        var tcs = new TaskCompletionSource<TailErrorEventArgs>();
        tailService.TailError += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(e);
        };

        tailService.LinesAppended += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("boom");
        };

        tailService.StartTailing(path, FileEncoding.Utf8);
        await Task.Delay(100);
        await File.AppendAllTextAsync(path, "trigger\n");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Equal(tcs.Task, result);
        var error = await tcs.Task;
        Assert.Equal(path, error.FilePath);
        Assert.Contains("boom", error.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TailService_ThrowingLinesAppendedSubscriber_DoesNotStopTailing()
    {
        var path = Path.Combine(_testDir, "tail-error-continues.log");
        await File.WriteAllTextAsync(path, string.Empty);

        using var tailService = new FileTailService();
        var tailErrorTcs = new TaskCompletionSource<TailErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAppendTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appendNotifications = 0;

        tailService.TailError += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                tailErrorTcs.TrySetResult(e);
        };

        tailService.LinesAppended += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("append boom");
        };

        tailService.LinesAppended += (_, e) =>
        {
            if (!string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                return;

            if (Interlocked.Increment(ref appendNotifications) >= 2)
                secondAppendTcs.TrySetResult();
        };

        tailService.StartTailing(path, FileEncoding.Utf8);
        await Task.Delay(100);
        await File.AppendAllTextAsync(path, "first\n");

        var tailErrorResult = await Task.WhenAny(tailErrorTcs.Task, Task.Delay(5000));
        Assert.Same(tailErrorTcs.Task, tailErrorResult);
        Assert.Contains("append boom", (await tailErrorTcs.Task).ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await Task.Delay(100);
        await File.AppendAllTextAsync(path, "second\n");

        var secondAppendResult = await Task.WhenAny(secondAppendTcs.Task, Task.Delay(5000));
        Assert.Same(secondAppendTcs.Task, secondAppendResult);
        Assert.True(Volatile.Read(ref appendNotifications) >= 2);
    }

    [Fact]
    public async Task TailService_ThrowingFileRotatedSubscriber_OnTruncation_DoesNotStopTailing()
    {
        var path = Path.Combine(_testDir, "tail-rotation-error-continues.log");
        await File.WriteAllTextAsync(path, "Line 1\nLine 2\nLine 3\nLine 4\n");

        using var tailService = new FileTailService();
        var tailErrorTcs = new TaskCompletionSource<TailErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileRotatedTcs = new TaskCompletionSource<FileRotatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var appendedAfterRotationTcs = new TaskCompletionSource<TailEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        tailService.TailError += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                tailErrorTcs.TrySetResult(e);
        };

        tailService.FileRotated += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("rotation boom");
        };

        tailService.FileRotated += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                fileRotatedTcs.TrySetResult(e);
        };

        tailService.StartTailing(path, FileEncoding.Utf8);
        await Task.Delay(100);

        // This intentionally exercises the FileRotated notification through the
        // truncation branch, which is the stable rotation proxy in the current service.
        await File.WriteAllTextAsync(path, "R\n");

        var fileRotatedResult = await Task.WhenAny(fileRotatedTcs.Task, Task.Delay(5000));
        Assert.Same(fileRotatedTcs.Task, fileRotatedResult);

        var tailErrorResult = await Task.WhenAny(tailErrorTcs.Task, Task.Delay(5000));
        Assert.Same(tailErrorTcs.Task, tailErrorResult);
        Assert.Contains("rotation boom", (await tailErrorTcs.Task).ErrorMessage, StringComparison.OrdinalIgnoreCase);

        tailService.LinesAppended += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                appendedAfterRotationTcs.TrySetResult(e);
        };

        await Task.Delay(100);
        await File.AppendAllTextAsync(path, "After rotation\n");

        var appendedResult = await Task.WhenAny(appendedAfterRotationTcs.Task, Task.Delay(5000));
        Assert.Same(appendedAfterRotationTcs.Task, appendedResult);
    }

    [Fact]
    public async Task TailService_Dispose_WithMultipleFiles_StopsFurtherEvents()
    {
        var path1 = Path.Combine(_testDir, "dispose-multi-1.log");
        var path2 = Path.Combine(_testDir, "dispose-multi-2.log");
        await File.WriteAllTextAsync(path1, string.Empty);
        await File.WriteAllTextAsync(path2, string.Empty);

        using var tailService = new FileTailService();
        int eventCount = 0;
        tailService.LinesAppended += (_, e) =>
        {
            if (string.Equals(e.FilePath, path1, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.FilePath, path2, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref eventCount);
            }
        };

        tailService.StartTailing(path1, FileEncoding.Utf8);
        tailService.StartTailing(path2, FileEncoding.Utf8);
        await Task.Delay(100);

        await File.AppendAllTextAsync(path1, "warmup-1\n");
        await File.AppendAllTextAsync(path2, "warmup-2\n");
        await Task.Delay(350);

        tailService.Dispose();
        var countAfterDispose = Volatile.Read(ref eventCount);

        await File.AppendAllTextAsync(path1, "after-dispose-1\n");
        await File.AppendAllTextAsync(path2, "after-dispose-2\n");
        await Task.Delay(350);

        Assert.Equal(countAfterDispose, Volatile.Read(ref eventCount));
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

        using var updated = await reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        // Should have rebuilt (file smaller = truncated)
        Assert.Equal(1, updated.LineCount);
        var line = await reader.ReadLineAsync(path, updated, 0, FileEncoding.Utf8);
        Assert.Equal("Rotated line 1", line);
    }
}
