namespace LogReader.Tests;

using LogReader.App.Services;

public class ContextUiDispatcherTests
{
    [Fact]
    public async Task ConstructionContext_IsUsedUntilFirstOperationCapture()
    {
        var construction = new RecordingContext();
        var firstOperation = new RecordingContext();
        var laterOperation = new RecordingContext();
        var fallback = new RecordingDispatcher();
        var dispatcher = OnContext(construction, () => new ContextUiDispatcher(fallback));

        await dispatcher.InvokeAsync(() => Assert.True(dispatcher.CheckAccess()));
        Assert.Equal(1, construction.Sends);
        OnContext(firstOperation, () => { dispatcher.CaptureCurrentContextIfUnset(); return true; });
        OnContext(laterOperation, () => { dispatcher.CaptureCurrentContextIfUnset(); return true; });
        await dispatcher.InvokeAsync(() => Assert.True(dispatcher.CheckAccess()));
        Assert.Equal(1, firstOperation.Sends);
        Assert.Equal(0, laterOperation.Sends);
        Assert.Equal(0, fallback.Invocations);
    }

    [Fact]
    public async Task ConcurrentCapture_SelectsOneContextAndDoesNotReplaceIt()
    {
        var contexts = Enumerable.Range(0, 16).Select(_ => new RecordingContext()).ToArray();
        var dispatcher = OnContext(null, () => new ContextUiDispatcher(new RecordingDispatcher()));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = contexts.Select(context => Task.Run(async () =>
        {
            await release.Task;
            OnContext(context, () => { dispatcher.CaptureCurrentContextIfUnset(); return true; });
        })).ToArray();
        release.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.InvokeAsync(() => { });
        var selected = Assert.Single(contexts, context => context.Sends != 0);
        foreach (var context in contexts)
            OnContext(context, () => { dispatcher.CaptureCurrentContextIfUnset(); return true; });
        await dispatcher.InvokeAsync(() => { });
        Assert.Equal(2, selected.Sends);
        Assert.Equal(2, contexts.Sum(context => context.Sends));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fallback_HandlesActionsTasksAndTypedResults(bool access)
    {
        var fallback = new RecordingDispatcher { Access = access };
        IUiDispatcher dispatcher = OnContext(null, () => new ContextUiDispatcher(fallback));
        var invoked = 0;
        await dispatcher.InvokeAsync(() => invoked++);
        await dispatcher.InvokeAsync((Func<Task>)(() => { invoked++; return Task.CompletedTask; }));
        Assert.Equal(42, await dispatcher.InvokeAsync<int>(() => 42));
        Assert.Null(await dispatcher.InvokeAsync<string?>(() => null));
        Assert.Equal(2, invoked);
        Assert.Equal(access ? 2 : 4, fallback.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextHandoff_IsSynchronousAndUnwrapsAsyncCompletion(bool inline)
    {
        var context = new RecordingContext();
        var dispatcher = OnContext(context, () => new ContextUiDispatcher(new RecordingDispatcher()));
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = false;
        Task Invoke() => dispatcher.InvokeAsync((Func<Task>)(() =>
        {
            invoked = true;
            Assert.True(dispatcher.CheckAccess());
            dispatcher.InvokeAsync(() => Assert.True(dispatcher.CheckAccess())).GetAwaiter().GetResult();
            return complete.Task;
        }));
        var task = inline ? OnContext(context, Invoke) : Invoke();
        try
        {
            Assert.True(invoked);
            Assert.False(task.IsCompleted);
            Assert.Equal(inline ? 0 : 1, context.Sends);
        }
        finally
        {
            complete.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failures_PreserveInlineThrowsAndCrossContextFaultedTasks(bool inline)
    {
        var context = new RecordingContext();
        IUiDispatcher dispatcher = OnContext(context, () => new ContextUiDispatcher(new RecordingDispatcher()));
        var expected = new InvalidOperationException("callback");
        var callbacks = new Func<Task>[]
        {
            () => dispatcher.InvokeAsync((Action)(() => throw expected)),
            () => dispatcher.InvokeAsync<int>(() => throw expected),
            () => dispatcher.InvokeAsync((Func<Task>)(() => throw expected))
        };
        foreach (var callback in callbacks)
        {
            if (inline)
                Assert.Same(expected, Record.Exception(() => { OnContext(context, callback); }));
            else
                Assert.Same(expected, await Record.ExceptionAsync(callback));
        }
        Func<Task> asynchronous = () => dispatcher.InvokeAsync((Func<Task>)(() => Task.FromException(expected)));
        var task = inline ? OnContext(context, asynchronous) : asynchronous();
        Assert.Same(expected, await Record.ExceptionAsync(() => task));
    }

    [Fact]
    public async Task TypedCrossContextCancellationException_RemainsFaulted()
    {
        var context = new RecordingContext();
        IUiDispatcher dispatcher = OnContext(context, () => new ContextUiDispatcher(new RecordingDispatcher()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new OperationCanceledException(cancellation.Token);
        var task = dispatcher.InvokeAsync<int>(() => throw expected);

        Assert.Same(expected, await Record.ExceptionAsync(() => task));
        Assert.True(task.IsFaulted);
        Assert.False(task.IsCanceled);
    }

    [Fact]
    public async Task AsyncCallbackCancellation_RemainsCanceled()
    {
        var context = new RecordingContext();
        IUiDispatcher dispatcher = OnContext(context, () => new ContextUiDispatcher(new RecordingDispatcher()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var task = dispatcher.InvokeAsync((Func<Task>)(() => Task.FromCanceled(cancellation.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
    }

    [Fact]
    public void NullCallbacks_AreRejected()
    {
        IUiDispatcher dispatcher = OnContext(null, () => new ContextUiDispatcher(new RecordingDispatcher()));
        Assert.IsType<ArgumentNullException>(Record.Exception(() => { dispatcher.InvokeAsync((Action)null!); }));
        Assert.IsType<ArgumentNullException>(Record.Exception(() => { dispatcher.InvokeAsync((Func<Task>)null!); }));
        Assert.IsType<ArgumentNullException>(Record.Exception(() => { dispatcher.InvokeAsync<int>(null!); }));
    }

    [Fact]
    public async Task WorkerCallbacksAndAsyncContinuations_RunOnWpfOwner()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var owner = Environment.CurrentManagedThreadId;
            IUiDispatcher dispatcher = new ContextUiDispatcher(WpfUiDispatcher.Instance);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = Task.Run(() => dispatcher.InvokeAsync(async () =>
            {
                Assert.Equal(owner, Environment.CurrentManagedThreadId);
                ready.TrySetResult();
                await release.Task;
                Assert.Equal(owner, Environment.CurrentManagedThreadId);
            }));
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(worker.IsCompleted);
            }
            finally
            {
                release.TrySetResult();
                await worker.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(owner, await Task.Run(() => dispatcher.InvokeAsync<int>(() => Environment.CurrentManagedThreadId)));
            await Task.Run(() => dispatcher.InvokeAsync(() => Assert.Equal(owner, Environment.CurrentManagedThreadId)));
        });
    }

    private static T OnContext<T>(SynchronizationContext? context, Func<T> action)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            return action();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        private int _sends;
        public int Sends => Volatile.Read(ref _sends);

        public override void Send(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _sends);
            OnContext(this, () => { callback(state); return true; });
        }
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        private int _invocations;
        public int Invocations => Volatile.Read(ref _invocations);
        public bool Access { get; init; }
        public bool CheckAccess() => Access;

        public Task InvokeAsync(Action action)
        {
            Interlocked.Increment(ref _invocations);
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action)
        {
            Interlocked.Increment(ref _invocations);
            return action();
        }
    }
}
