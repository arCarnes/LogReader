namespace LogReader.Tests;

/// <summary>Refuses dispatcher reuse if an action survives its failure drain.</summary>
internal sealed class WpfTestHostLifetime
{
    private readonly TimeSpan _failureDrainTimeout;
    private Exception? _failure;

    public WpfTestHostLifetime(TimeSpan? failureDrainTimeout = null)
        => _failureDrainTimeout = failureDrainTimeout ?? TimeSpan.FromSeconds(30);

    public void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _failure) is { } failure)
            throw new InvalidOperationException("The WPF test host cannot be reused because an earlier action did not drain.", failure);
    }

    public async Task DrainAsync(Task action, Task dispatcherFailure)
    {
        ThrowIfUnavailable();
        await Task.WhenAny(action, dispatcherFailure);
        if (!action.IsCompleted)
        {
            using var deadlineCancellation = new CancellationTokenSource();
            var deadline = Task.Delay(_failureDrainTimeout, deadlineCancellation.Token);
            if (await Task.WhenAny(action, deadline) != action && !action.IsCompleted)
            {
                var failure = new TimeoutException($"The WPF test action did not drain within {_failureDrainTimeout} after a dispatcher failure.");
                Interlocked.CompareExchange(ref _failure, failure, null);
                _ = action.ContinueWith(completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                throw failure;
            }
            deadlineCancellation.Cancel();
        }
        await action;
    }
}
