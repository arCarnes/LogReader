namespace LogReader.Core.Tests;

using System.Reflection;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class RotationDetectionTests : IAsyncLifetime
{
    private string _testDir = null!;
    private IDisposable? _appPathsScope;
    private readonly List<Task> _tailWorkers = new();

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "WeezTailTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        _appPathsScope = AppPaths.BeginTestScope(rootPath: _testDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try { await Task.WhenAll(_tailWorkers).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally
        {
            _appPathsScope?.Dispose();
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public async Task TailService_CommittedBaselineDetectsGrowthBeforeFirstProbe()
    {
        using var firstProbe = new ManualResetEventSlim();
        using var releaseProbe = new ManualResetEventSlim();
        using var tail = new FileTailService(_ =>
        {
            firstProbe.Set();
            Assert.True(releaseProbe.Wait(TimeSpan.FromSeconds(5)));
            return new(true, 20, "0000000000000001:0000000000000001");
        });
        var appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tail.LinesAppended += (_, _) => appended.TrySetResult();
        ((IFileTailBaselineService)tail).StartTailing("baseline.log", FileEncoding.Utf8,
            new(10, FileGenerationToken.Create(1, 1)), 100);
        var worker = GetTailTask(tail, "baseline.log");
        try
        {
            Assert.True(firstProbe.Wait(TimeSpan.FromSeconds(5)));
            releaseProbe.Set();
            await appended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseProbe.Set();
            tail.StopAll();
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task TailService_JoiningSharedMonitorPreservesBaselineAndReference()
    {
        var length = 10L;
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tail = new FileTailService(_ =>
        {
            var snapshot = new FileTailService.TailFileSnapshot(true, Volatile.Read(ref length), "0000000000000001:0000000000000001");
            initialized.TrySetResult();
            return snapshot;
        });
        var appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tail.LinesAppended += (_, _) => appended.TrySetResult();
        var baselineService = (IFileTailBaselineService)tail;
        baselineService.StartTailing("shared.log", FileEncoding.Utf8, new(10, FileGenerationToken.Create(1, 1)), 100);
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        baselineService.StartTailing("shared.log", FileEncoding.Utf8, new(20, FileGenerationToken.Create(1, 1)), 100);
        tail.StopTailing("shared.log");
        Volatile.Write(ref length, 20);
        await appended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tail.StopTailing("shared.log");
    }

    [Fact]
    public async Task TailService_DetectsNewContent()
    {
        var path = Path.Combine(_testDir, "tail.log");
        await File.WriteAllTextAsync(path, "Initial line\n");

        using var tailService = new FileTailService();
        var tcs = new TaskCompletionSource<TailEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        tailService.LinesAppended += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(e);
        };

        StartFromCurrentBaseline(tailService, path);

        await File.AppendAllTextAsync(path, "Appended line\n");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Equal(tcs.Task, result);

        var args = await tcs.Task;
        Assert.Equal(path, args.FilePath);
    }

    [Fact]
    public void TailService_RemovingFastReferences_RestoresSlowPollingInterval()
    {
        using var tail = new FileTailService(_ => new(true, 10, "same-file"));
        var intervalService = (IFileTailPollingService)tail;
        tail.StartTailing("shared.log", FileEncoding.Utf8, 30000);
        tail.StartTailing("shared.log", FileEncoding.Ansi, 250);
        tail.StartTailing("shared.log", FileEncoding.Utf16, 250);
        Assert.Equal(250, GetMonitorPollingInterval(tail, "shared.log"));

        intervalService.StopTailing("shared.log", 250);
        Assert.Equal(250, GetMonitorPollingInterval(tail, "shared.log"));
        intervalService.StopTailing("shared.log", 250);
        Assert.Equal(30000, GetMonitorPollingInterval(tail, "shared.log"));
        intervalService.StopTailing("shared.log", 30000);
        Assert.Null(GetMonitorPollingInterval(tail, "shared.log"));
    }

    [Fact]
    public async Task TailService_ForegroundReference_WakesSlowMonitorPromptly()
    {
        var length = 10L;
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tail = new FileTailService(_ =>
        {
            var snapshot = new FileTailService.TailFileSnapshot(true, Volatile.Read(ref length), "same-file");
            initialized.TrySetResult();
            return snapshot;
        });
        var appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tail.LinesAppended += (_, _) => appended.TrySetResult();
        tail.StartTailing("shared.log", FileEncoding.Utf8, 30000);
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref length, 20);

        tail.StartTailing("shared.log", FileEncoding.Ansi, 100);
        await appended.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static int? GetMonitorPollingInterval(FileTailService tail, string filePath)
    {
        var states = (System.Collections.IDictionary)typeof(FileTailService)
            .GetField("_tailedFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tail)!;
        var state = states[filePath];
        return state == null ? null : (int)state.GetType().GetProperty("PollingIntervalMs")!.GetValue(state)!;
    }

    [Fact]
    public async Task TailService_DetectsRotation_ByTruncation()
    {
        var path = Path.Combine(_testDir, "rotate.log");
        await File.WriteAllTextAsync(path, "Line 1\nLine 2\nLine 3\n");

        using var tailService = new FileTailService();
        var tcs = new TaskCompletionSource<FileRotatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        tailService.FileRotated += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(e);
        };

        StartFromCurrentBaseline(tailService, path);

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
        var tcsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int eventCount = 0;

        tailService.LinesAppended += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref eventCount);
                tcsReady.TrySetResult(); // signal that the service is actively polling
            }
        };

        StartFromCurrentBaseline(tailService, path);

        // Write content and wait for the event — this confirms the service is polling
        // and has completed at least one full cycle.
        await File.AppendAllTextAsync(path, "Warmup line\n");
        var readyResult = await Task.WhenAny(tcsReady.Task, Task.Delay(5000));
        Assert.Equal(tcsReady.Task, readyResult); // warmup event must arrive

        // Await actual worker completion before asserting silence.
        var tailTask = GetTailTask(tailService, path);
        tailService.StopTailing(path);
        await tailTask.WaitAsync(TimeSpan.FromSeconds(5));
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
        StartFromCurrentBaseline(tailService, path);

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
        StartFromCurrentBaseline(inspectionTailService, path);

        var tailTask = GetTailTask(inspectionTailService, path);

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
        var tcs = new TaskCompletionSource<TailErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        StartFromCurrentBaseline(tailService, path);
        await File.AppendAllTextAsync(path, "trigger\n");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Equal(tcs.Task, result);
        var error = await tcs.Task;
        Assert.Equal(path, error.FilePath);
        Assert.Contains("boom", error.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TailService_RecoverableMetadataFailure_DoesNotStopTailing()
    {
        var path = Path.Combine(_testDir, "tail-recoverable.log");
        var probeCalls = 0;
        using var tailService = new FileTailService(_ =>
        {
            var call = Interlocked.Increment(ref probeCalls);
            return call switch
            {
                1 => new FileTailService.TailFileSnapshot(true, 0, "same-file"),
                2 => throw new IOException("temporary UNC metadata failure"),
                _ => new FileTailService.TailFileSnapshot(true, 12, "same-file")
            };
        });

        var appendedTcs = new TaskCompletionSource<TailEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tailErrorTcs = new TaskCompletionSource<TailErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        tailService.LinesAppended += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                appendedTcs.TrySetResult(e);
        };
        tailService.TailError += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                tailErrorTcs.TrySetResult(e);
        };

        tailService.StartTailing(path, FileEncoding.Utf8, pollingIntervalMs: 100);

        var appendedResult = await Task.WhenAny(appendedTcs.Task, Task.Delay(5000));
        Assert.Same(appendedTcs.Task, appendedResult);
        Assert.False(tailErrorTcs.Task.IsCompleted);
        Assert.True(Volatile.Read(ref probeCalls) >= 3);
    }

    [Fact]
    public async Task TailService_IgnoresUnchangedSizeAndIdentity()
    {
        var path = Path.Combine(_testDir, "tail-unchanged-size.log");
        var probeCalls = 0;
        using var tailService = new FileTailService(_ =>
        {
            var call = Interlocked.Increment(ref probeCalls);
            return call < 2
                ? new FileTailService.TailFileSnapshot(true, 12, "same-file")
                : new FileTailService.TailFileSnapshot(true, 12, "same-file");
        });

        var rotatedTcs = new TaskCompletionSource<FileRotatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        tailService.FileRotated += (_, e) =>
        {
            if (string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                rotatedTcs.TrySetResult(e);
        };

        tailService.StartTailing(path, FileEncoding.Utf8, pollingIntervalMs: 100);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref probeCalls) < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.True(Volatile.Read(ref probeCalls) >= 2);
        var rotatedResult = await Task.WhenAny(rotatedTcs.Task, Task.Delay(300));
        Assert.NotSame(rotatedTcs.Task, rotatedResult);
    }

    [Fact]
    public async Task TailService_DeleteRecreateEmptyFile_RaisesSingleRotation()
    {
        var path = Path.Combine(_testDir, "tail-recreate-empty.log");
        var probeCalls = 0;
        using var tailService = new FileTailService(_ =>
        {
            var call = Interlocked.Increment(ref probeCalls);
            return call switch
            {
                1 => new FileTailService.TailFileSnapshot(true, 0, "old-file"),
                2 => FileTailService.TailFileSnapshot.Missing,
                _ => new FileTailService.TailFileSnapshot(true, 0, "new-file")
            };
        });

        var rotationCount = 0;
        var availabilityChangeCount = 0;
        var firstRotationTcs = new TaskCompletionSource<FileRotatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRotationTcs = new TaskCompletionSource<FileRotatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        tailService.FileRotated += (_, e) =>
        {
            if (!string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase))
                return;

            if (Interlocked.Increment(ref rotationCount) == 1)
                firstRotationTcs.TrySetResult(e);
            else
                secondRotationTcs.TrySetResult(e);
        };

        tailService.FileAvailabilityChanged += (_, _) => Interlocked.Increment(ref availabilityChangeCount);
        tailService.StartTailing(path, FileEncoding.Utf8, pollingIntervalMs: 100);

        var firstRotationResult = await Task.WhenAny(firstRotationTcs.Task, Task.Delay(5000));
        Assert.Same(firstRotationTcs.Task, firstRotationResult);

        await WaitForProbeCallsAsync(() => Volatile.Read(ref probeCalls), 4);

        var secondRotationResult = await Task.WhenAny(secondRotationTcs.Task, Task.Delay(300));
        Assert.NotSame(secondRotationTcs.Task, secondRotationResult);
        Assert.Equal(1, Volatile.Read(ref rotationCount));
        Assert.Equal(0, Volatile.Read(ref availabilityChangeCount));
    }

    [Fact]
    public async Task TailService_SustainedMissingFile_PublishesOrderedMissingAndAvailableTransitions()
    {
        var path = Path.Combine(_testDir, "tail-missing-state.log");
        var recovered = 0;
        var probeCalls = 0;
        using var tailService = new FileTailService(
            _ =>
            {
                var call = Interlocked.Increment(ref probeCalls);
                if (call == 1)
                    return new FileTailService.TailFileSnapshot(true, 12, "old-file");

                return Volatile.Read(ref recovered) == 0
                    ? FileTailService.TailFileSnapshot.Missing
                    : new FileTailService.TailFileSnapshot(true, 4, "new-file");
            },
            missingGracePeriod: TimeSpan.Zero);
        var transitions = new List<FileAvailabilityChangedEventArgs>();
        var missingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var availableTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tailService.FileAvailabilityChanged += (_, e) =>
        {
            lock (transitions)
                transitions.Add(e);

            if (e.IsAvailable)
                availableTcs.TrySetResult();
            else
                missingTcs.TrySetResult();
        };

        tailService.StartTailing(path, FileEncoding.Utf8, pollingIntervalMs: 100);

        await missingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref recovered, 1);
        await availableTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        FileAvailabilityChangedEventArgs[] snapshot;
        lock (transitions)
            snapshot = transitions.ToArray();
        Assert.Collection(
            snapshot,
            missing =>
            {
                Assert.False(missing.IsAvailable);
                Assert.Equal(path, missing.FilePath);
            },
            available =>
            {
                Assert.True(available.IsAvailable);
                Assert.Equal(path, available.FilePath);
            });
        Assert.True(snapshot[1].Sequence > snapshot[0].Sequence);
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

        StartFromCurrentBaseline(tailService, path);
        await File.AppendAllTextAsync(path, "first\n");

        var tailErrorResult = await Task.WhenAny(tailErrorTcs.Task, Task.Delay(5000));
        Assert.Same(tailErrorTcs.Task, tailErrorResult);
        Assert.Contains("append boom", (await tailErrorTcs.Task).ErrorMessage, StringComparison.OrdinalIgnoreCase);

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

        StartFromCurrentBaseline(tailService, path);

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
        var firstWarmup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWarmup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int eventCount = 0;
        tailService.LinesAppended += (_, e) =>
        {
            if (string.Equals(e.FilePath, path1, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.FilePath, path2, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref eventCount);
                if (e.FilePath == path1) firstWarmup.TrySetResult();
                if (e.FilePath == path2) secondWarmup.TrySetResult();
            }
        };

        StartFromCurrentBaseline(tailService, path1);
        StartFromCurrentBaseline(tailService, path2);

        await File.AppendAllTextAsync(path1, "warmup-1\n");
        await File.AppendAllTextAsync(path2, "warmup-2\n");
        await Task.WhenAll(firstWarmup.Task, secondWarmup.Task).WaitAsync(TimeSpan.FromSeconds(5));
        var workers = Task.WhenAll(GetTailTask(tailService, path1), GetTailTask(tailService, path2));

        tailService.Dispose();
        await workers.WaitAsync(TimeSpan.FromSeconds(5));
        var countAfterDispose = Volatile.Read(ref eventCount);

        await File.AppendAllTextAsync(path1, "after-dispose-1\n");
        await File.AppendAllTextAsync(path2, "after-dispose-2\n");
        await Task.Delay(350);

        Assert.Equal(countAfterDispose, Volatile.Read(ref eventCount));
    }

    [Fact]
    public async Task TailService_OneFalseGrowthObservation_DoesNotPublishOrPoisonBaseline()
    {
        var path = Path.Combine(_testDir, "tail-false-growth.log");
        var probeCalls = 0;
        using var tailService = new FileTailService(_ =>
        {
            var call = Interlocked.Increment(ref probeCalls);
            return call switch
            {
                1 => new FileTailService.TailFileSnapshot(true, 10, "same-file"),
                2 => new FileTailService.TailFileSnapshot(true, 1000, "same-file"),
                _ => new FileTailService.TailFileSnapshot(true, 10, "same-file")
            };
        });
        var appendCount = 0;
        var rotationCount = 0;
        tailService.LinesAppended += (_, _) => Interlocked.Increment(ref appendCount);
        tailService.FileRotated += (_, _) => Interlocked.Increment(ref rotationCount);

        tailService.StartTailing(path, FileEncoding.Utf8, pollingIntervalMs: 100);
        await WaitForProbeCallsAsync(() => Volatile.Read(ref probeCalls), 4);
        await Task.Delay(250);

        Assert.Equal(0, Volatile.Read(ref appendCount));
        Assert.Equal(0, Volatile.Read(ref rotationCount));
    }

    [Fact]
    public async Task TailService_TwoStableIdentityChanges_PublishesTypedRotation()
    {
        var path = Path.Combine(_testDir, "tail-stable-identity.log");
        var probeCalls = 0;
        using var tailService = new FileTailService(_ =>
        {
            var call = Interlocked.Increment(ref probeCalls);
            return call == 1
                ? new FileTailService.TailFileSnapshot(true, 10, "old-file")
                : new FileTailService.TailFileSnapshot(true, 10, "new-file");
        });
        var rotated = new TaskCompletionSource<FileRotatedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        tailService.FileRotated += (_, e) => rotated.TrySetResult(e);

        tailService.StartTailing(path, FileEncoding.Utf8, pollingIntervalMs: 100);
        var result = await rotated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(path, result.FilePath);
        Assert.Equal(FileChangeHint.IdentityChanged, result.ChangeHint);
    }

    [Fact]
    public async Task TailService_TransientMissingBeforeGrace_DoesNotRotate()
    {
        var path = Path.Combine(_testDir, "tail-transient-missing.log");
        var probeCalls = 0;
        using var tailService = new FileTailService(
            _ =>
            {
                var call = Interlocked.Increment(ref probeCalls);
                return call == 2
                    ? FileTailService.TailFileSnapshot.Missing
                    : new FileTailService.TailFileSnapshot(true, 10, "same-file");
            },
            missingGracePeriod: TimeSpan.FromSeconds(5));
        var rotationCount = 0;
        var availabilityCount = 0;
        tailService.FileRotated += (_, _) => Interlocked.Increment(ref rotationCount);
        tailService.FileAvailabilityChanged += (_, _) => Interlocked.Increment(ref availabilityCount);

        tailService.StartTailing(path, FileEncoding.Utf8, pollingIntervalMs: 100);
        await WaitForProbeCallsAsync(() => Volatile.Read(ref probeCalls), 4);
        await Task.Delay(250);

        Assert.Equal(0, Volatile.Read(ref rotationCount));
        Assert.Equal(0, Volatile.Read(ref availabilityCount));
    }

    [Fact]
    public async Task UpdateIndex_DetectsTruncation_AsRotation()
    {
        var reader = new ChunkedLogReaderService();
        var path = Path.Combine(_testDir, "rotated.log");
        await File.WriteAllTextAsync(path, "Line 1\nLine 2\nLine 3\n");

        using var index = await reader.BuildIndexAsync(path, FileEncoding.Utf8);
        Assert.Equal(3, index.LineCount);

        // Simulate rotation: truncate file
        await File.WriteAllTextAsync(path, "Rotated line 1\n");

        using var updated = await reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        // Should have rebuilt (file smaller = truncated)
        Assert.Equal(1, updated.LineCount);
        var line = await reader.ReadLineAsync(path, updated, 0, FileEncoding.Utf8);
        Assert.Equal("Rotated line 1", line);
    }

    private void StartFromCurrentBaseline(FileTailService tail, string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var baseline = new FileTailBaseline(stream.Length, FileGenerationTokenProvider.Capture(stream));
        ((IFileTailBaselineService)tail).StartTailing(path, FileEncoding.Utf8, baseline, 250);
        _tailWorkers.Add(GetTailTask(tail, path));
    }

    private static Task GetTailTask(FileTailService tail, string path)
    {
        var states = (System.Collections.IDictionary)typeof(FileTailService)
            .GetField("_tailedFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tail)!;
        var state = states[path];
        Assert.NotNull(state);
        return (Task)state.GetType().GetProperty("Task")!.GetValue(state)!;
    }

    private static async Task WaitForProbeCallsAsync(Func<int> getProbeCalls, int expected)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(5);
        while (getProbeCalls() < expected && DateTime.UtcNow < timeoutAt)
            await Task.Delay(25);

        Assert.True(getProbeCalls() >= expected);
    }
}
