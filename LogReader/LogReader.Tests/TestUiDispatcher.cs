namespace LogReader.Tests;

using System.Windows.Threading;
using LogReader.App.Services;

internal sealed class TestUiDispatcher : IUiDispatcher
{
    private static readonly TestUiDispatcher Immediate = new();

    // Capture this when constructing the fixture. UI tests retain real dispatch;
    // view-model unit tests do not depend on another test having created an App.
    public static IUiDispatcher Current =>
        SynchronizationContext.Current is DispatcherSynchronizationContext
            ? WpfUiDispatcher.Instance
            : Immediate;

    public bool CheckAccess() => true;

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task InvokeAsync(Func<Task> action) => action();
}
