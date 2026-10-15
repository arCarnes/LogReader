namespace LogReader.Tests;

public class WpfTestHostLifetimeTests
{
    [Fact]
    public async Task FailureDrain_WaitsForActionAndRetainsItsException()
    {
        var lifetime = new WpfTestHostLifetime();
        var action = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = lifetime.DrainAsync(action.Task, failure.Task);
        failure.SetResult();
        Assert.False(drain.IsCompleted);
        var error = new InvalidOperationException("action cleanup failed");
        action.SetException(error);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => drain));
        lifetime.ThrowIfUnavailable();
    }

    [Fact]
    public async Task FailedDrain_RejectsFurtherInvocationsEvenAfterLateActionCompletion()
    {
        var lifetime = new WpfTestHostLifetime(TimeSpan.Zero);
        var action = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var timeout = await Assert.ThrowsAsync<TimeoutException>(() => lifetime.DrainAsync(action.Task, Task.CompletedTask));
            var invoked = false;
            var rejection = Assert.Throws<InvalidOperationException>(() =>
            {
                lifetime.ThrowIfUnavailable();
                invoked = true;
            });
            Assert.Same(timeout, rejection.InnerException);
            Assert.False(invoked);
        }
        finally { action.TrySetResult(); }

        Assert.Throws<InvalidOperationException>(lifetime.ThrowIfUnavailable);
    }

    [Fact]
    public async Task CompletedAction_DoesNotWaitForDispatcherFailure()
    {
        var lifetime = new WpfTestHostLifetime();
        var noFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await lifetime.DrainAsync(Task.CompletedTask, noFailure.Task);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lifetime.DrainAsync(Task.FromCanceled(new CancellationToken(true)), noFailure.Task));
        lifetime.ThrowIfUnavailable();
    }
}
