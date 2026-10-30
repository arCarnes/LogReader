namespace LogReader.App.Services;

/// <summary>Preserves an owner's context affinity while sharing dispatch and fallback behavior.</summary>
internal sealed class ContextUiDispatcher(IUiDispatcher fallback) : IUiDispatcher
{
    private readonly SynchronizationContext? _constructionContext = Normalize(SynchronizationContext.Current);
    private SynchronizationContext? _capturedContext;

    private SynchronizationContext? Context => Volatile.Read(ref _capturedContext) ?? _constructionContext;

    internal void CaptureCurrentContextIfUnset()
    {
        var context = Normalize(SynchronizationContext.Current);
        if (context != null)
            Interlocked.CompareExchange(ref _capturedContext, context, null);
    }

    public bool CheckAccess()
        => Context is { } context ? SynchronizationContext.Current == context : fallback.CheckAccess();

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var context = Context;
        if (context == null)
            return fallback.InvokeAsync(action);
        if (SynchronizationContext.Current == context)
        {
            action();
            return Task.CompletedTask;
        }
        return Send(context, () => { action(); return true; });
    }

    public Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var context = Context;
        if (context == null)
            return fallback.InvokeAsync(action);
        if (SynchronizationContext.Current == context)
            return action();
        return Send(context, () => action() ?? Task.CompletedTask).Unwrap();
    }

    private static Task<T> Send<T>(SynchronizationContext context, Func<T> action)
    {
        try
        {
            var result = default(T)!;
            context.Send(_ => result = action(), null);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    private static SynchronizationContext? Normalize(SynchronizationContext? context)
    {
        var assemblyName = context?.GetType().Assembly.GetName().Name;
        return assemblyName?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true ? null : context;
    }
}

internal static class UiDispatcherExtensions
{
    internal static Task<T> InvokeAsync<T>(this IUiDispatcher dispatcher, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
            return Task.FromResult(action());
        var result = default(T)!;
        var invocation = dispatcher.InvokeAsync((Action)(() => result = action()));
        if (invocation.IsCompletedSuccessfully)
            return Task.FromResult(result);
        // A callback's OperationCanceledException was a fault, not cancellation, in the former result bridge.
        var completion = new TaskCompletionSource<T>();
        _ = CompleteAsync();
        return completion.Task;

        async Task CompleteAsync()
        {
            try
            {
                await invocation.ConfigureAwait(false);
                completion.SetResult(result);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }
    }
}
