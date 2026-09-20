namespace LogReader.App.Services;

internal sealed class DashboardMutationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public Func<bool>? CanEdit { get; set; }

    public async Task ExecuteAsync(Func<Task> mutationAsync)
    {
        ArgumentNullException.ThrowIfNull(mutationAsync);

        await _gate.WaitAsync();
        try
        {
            EnsureEditable();
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
            EnsureEditable();
            return await mutationAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExecuteLibraryAsync(Func<Task> action)
    {
        await _gate.WaitAsync();
        try { await action(); }
        finally { _gate.Release(); }
    }

    private void EnsureEditable()
    {
        if (CanEdit?.Invoke() == false)
            throw new InvalidOperationException("This view is read-only or awaiting recovery. Copy it to My Views to edit it.");
    }
}
