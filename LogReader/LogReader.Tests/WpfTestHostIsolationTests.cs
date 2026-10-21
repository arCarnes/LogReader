namespace LogReader.Tests;

using System.Windows.Threading;

public class WpfTestHostIsolationTests
{
    [Fact]
    public async Task DispatcherFailure_DrainsActionAndCleanupBeforeAdmittingNextInvocation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished = false;
        var nextEnteredBeforeCleanup = false;
        Dispatcher? dispatcher = null;
        var failure = new InvalidOperationException("Synthetic dispatcher failure");
        var first = WpfTestHost.RunAsync(async () =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.BeginInvoke(new Action(() => throw failure));
            started.TrySetResult();
            try
            {
                await releaseAction.Task;
            }
            finally
            {
                await releaseCleanup.Task;
                cleanupFinished = true;
            }
        });

        Task? second = null;
        var returnedBeforeAction = false;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await dispatcher!.InvokeAsync(() => { }, DispatcherPriority.Background);
            returnedBeforeAction = first.IsCompleted;
            second = WpfTestHost.RunAsync(() =>
            {
                nextEnteredBeforeCleanup = !cleanupFinished;
                return Task.CompletedTask;
            });
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally
        {
            releaseAction.TrySetResult();
            releaseCleanup.TrySetResult();
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, actual);
            if (second != null)
                await second.WaitAsync(TimeSpan.FromSeconds(5));
            // Drain the legacy action even when running this regression before the fix.
            if (dispatcher != null)
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        Assert.False(returnedBeforeAction);
        Assert.True(cleanupFinished);
        Assert.False(nextEnteredBeforeCleanup);
    }

    [Fact]
    public async Task DispatcherFailure_CancelsActionAndAwaitsItsFinally()
    {
        var started = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("cancel this fixture");
        var canceled = false;
        var first = WpfTestHost.RunAsync(async ct =>
        {
            started.SetResult(Dispatcher.CurrentDispatcher);
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally
            {
                canceled = ct.IsCancellationRequested;
                cleanupStarted.SetResult();
                await releaseCleanup.Task;
            }
        });
        try
        {
            var dispatcher = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _ = dispatcher.BeginInvoke(new Action(() => throw failure));
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
            Assert.True(canceled);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(5))));
        }
    }

    [Fact]
    public async Task DispatcherFailure_PreservesOriginalAndWindowCleanupErrors()
    {
        var failure = new InvalidOperationException("dispatcher failed");
        var cleanupFailure = new InvalidOperationException("window cleanup failed");
        var errors = await Assert.ThrowsAsync<AggregateException>(() => WpfTestHost.RunAsync(async () =>
        {
            var window = new System.Windows.Window();
            window.Closed += (_, _) => throw cleanupFailure;
            WpfTestHost.ShowHidden(window);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => throw failure));
            await WpfTestHost.FlushAsync();
        }));
        Assert.Equal(2, errors.InnerExceptions.Count);
        Assert.Same(failure, errors.InnerExceptions[0]);
        Assert.Same(cleanupFailure, errors.InnerExceptions[1]);
        await WpfTestHost.RunAsync(() => Task.CompletedTask);
    }

}
