namespace LogReader.Tests;

using LogReader.App.Services;

public sealed class TabMemberRefreshSchedulerTests
{
    [Fact]
    public async Task Queue_WhileRefreshIsRunning_CoalescesNotificationsIntoOnePendingBatch()
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executedRequests = new List<TabMemberRefreshRequest>();
        var executionCount = 0;
        var scheduler = new TabMemberRefreshScheduler(async (request, ct) =>
        {
            var currentExecution = Interlocked.Increment(ref executionCount);
            lock (executedRequests)
                executedRequests.Add(request);

            if (currentExecution == 1)
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(ct);
            }
        });

        var runningTask = scheduler.Queue(Targeted(("file-a", @"C:\logs\a.log")));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pendingTask = scheduler.Queue(Targeted(("file-b", @"C:\logs\b-0.log")));
        Task? lastMergedTask = null;
        for (var i = 1; i <= 10_000; i++)
            lastMergedTask = scheduler.Queue(Targeted(("file-b", $@"C:\logs\b-{i}.log")));
        var disjointMergedTask = scheduler.Queue(Targeted(("file-c", @"C:\logs\c.log")));

        Assert.Same(pendingTask, lastMergedTask);
        Assert.Same(pendingTask, disjointMergedTask);
        Assert.Equal(1, Volatile.Read(ref executionCount));

        releaseFirst.TrySetResult(true);
        await Task.WhenAll(runningTask, pendingTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, Volatile.Read(ref executionCount));
        var pendingRequest = executedRequests[1];
        Assert.False(pendingRequest.RequiresFullRefresh);
        Assert.Equal(2, pendingRequest.ChangedFilePaths.Count);
        Assert.Equal(@"C:\logs\b-10000.log", pendingRequest.ChangedFilePaths["file-b"]);
        Assert.Equal(@"C:\logs\c.log", pendingRequest.ChangedFilePaths["file-c"]);
    }

    [Fact]
    public async Task Queue_FullRefreshJoinsPendingBatch_AndDominatesTargetedRequests()
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executedRequests = new List<TabMemberRefreshRequest>();
        var scheduler = new TabMemberRefreshScheduler(async (request, ct) =>
        {
            executedRequests.Add(request);
            if (executedRequests.Count == 1)
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(ct);
            }
        });

        var runningTask = scheduler.Queue(Targeted(("file-a", @"C:\logs\a.log")));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pendingTask = scheduler.Queue(Targeted(("file-b", @"C:\logs\b.log")));
        var fullTask = scheduler.Queue(Full());
        var laterTargetedTask = scheduler.Queue(Targeted(("file-c", @"C:\logs\c.log")));

        Assert.Same(pendingTask, fullTask);
        Assert.Same(pendingTask, laterTargetedTask);

        releaseFirst.TrySetResult(true);
        await Task.WhenAll(runningTask, pendingTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, executedRequests.Count);
        Assert.True(executedRequests[1].RequiresFullRefresh);
        Assert.Empty(executedRequests[1].ChangedFilePaths);
    }

    [Fact]
    public async Task Queue_WhenRunningBatchFails_StartsPendingBatch()
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var scheduler = new TabMemberRefreshScheduler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref executionCount) == 1)
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(ct);
                throw new InvalidOperationException("Expected refresh failure.");
            }

            secondCompleted.TrySetResult(true);
        });

        var failedTask = scheduler.Queue(Targeted(("file-a", @"C:\logs\a.log")));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pendingTask = scheduler.Queue(Targeted(("file-b", @"C:\logs\b.log")));

        releaseFirst.TrySetResult(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failedTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Expected refresh failure.", exception.Message);
        await pendingTask.WaitAsync(TimeSpan.FromSeconds(5));
        await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref executionCount));
    }

    [Fact]
    public async Task Shutdown_CancelsRunningAndPendingBatches_AndRejectsLaterRequests()
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var scheduler = new TabMemberRefreshScheduler(async (_, ct) =>
        {
            Interlocked.Increment(ref executionCount);
            firstStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        var runningTask = scheduler.Queue(Targeted(("file-a", @"C:\logs\a.log")));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pendingTask = scheduler.Queue(Targeted(("file-b", @"C:\logs\b.log")));

        scheduler.Shutdown();
        var rejectedTask = scheduler.Queue(Full());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runningTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pendingTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejectedTask);
        Assert.Equal(1, Volatile.Read(ref executionCount));
    }

    private static TabMemberRefreshRequest Targeted(params (string FileId, string FilePath)[] changedFiles)
        => new(
            false,
            changedFiles.ToDictionary(
                changedFile => changedFile.FileId,
                changedFile => changedFile.FilePath,
                StringComparer.Ordinal));

    private static TabMemberRefreshRequest Full()
        => new(true, new Dictionary<string, string>(StringComparer.Ordinal));
}
