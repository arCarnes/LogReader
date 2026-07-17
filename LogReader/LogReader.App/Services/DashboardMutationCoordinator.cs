namespace LogReader.App.Services;

internal sealed class DashboardMutationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ExecuteAsync(Func<Task> mutationAsync)
    {
        ArgumentNullException.ThrowIfNull(mutationAsync);

        await _gate.WaitAsync();
        try
        {
            await mutationAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> mutationAsync)
    {
        ArgumentNullException.ThrowIfNull(mutationAsync);

        await _gate.WaitAsync();
        try
        {
            return await mutationAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
