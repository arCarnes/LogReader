namespace LogReader.Tests;

using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using LogReader.App.Services;
using LogReaderApplication = LogReader.App.App;

internal static class WpfTestHost
{
    private const double HiddenWindowCoordinate = -32000;

    private static readonly SemaphoreSlim TestGate = new(1, 1);
    private static readonly Lazy<Task<Dispatcher>> SharedDispatcher = new(StartDispatcher);

    // WPF supports one Application per process. Keep its resource package alive,
    // and never run the production startup/storage dialogs in a UI fixture.
    private sealed class CanceledStartup : IStartupStorageCoordinator, IAppInstanceCoordinator
    {
        public StartupStorageResult EnsureStorageReady() => StartupStorageResult.Canceled;
        public bool TryAcquire() => true;
    }

    private static Task<Dispatcher> StartDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var canceledStartup = new CanceledStartup();
                var application = new LogReaderApplication(
                    () => new AppStartupRunner(
                        canceledStartup,
                        new AppBootstrapper(),
                        new StubMessageBoxService(),
                        () => throw new InvalidOperationException("Test startup must not access the cache."),
                        LogReaderApplication.BuildStartupFailureMessage,
                        appInstanceCoordinator: canceledStartup),
                    startupUiCoordinator: null,
                    shutdownAction: () => { },
                    startupShutdownModeCoordinator: null);
                application.InitializeComponent();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ready.SetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        }) { IsBackground = true, Name = nameof(WpfTestHost) };
        thread.SetApartmentState(ApartmentState.STA);
        // Each invocation below supplies its own execution context (including AppPaths).
        using (ExecutionContext.SuppressFlow())
            thread.Start();
        return ready.Task;
    }

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RunAsync(() => { action(); return Task.CompletedTask; }).GetAwaiter().GetResult();
    }

    public static async Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await TestGate.WaitAsync();
        try
        {
            var dispatcher = await SharedDispatcher.Value;
            await dispatcher.InvokeAsync(() =>
            {
                var capturedExceptions = new List<ExceptionDispatchInfo>();
                DispatcherFrame? activeFrame = null;
                void Capture(Exception exception)
                    => capturedExceptions.Add(ExceptionDispatchInfo.Capture(exception));
                DispatcherUnhandledExceptionEventHandler handler = (_, e) =>
                {
                    Capture(e.Exception);
                    e.Handled = true;
                    if (activeFrame != null)
                        activeFrame.Continue = false;
                };
                dispatcher.UnhandledException += handler;
                try
                {
                    PumpTask(action(), () => capturedExceptions.Count > 0, frame => activeFrame = frame);
                }
                catch (Exception ex)
                {
                    Capture(ex);
                }
                finally
                {
                    TryCleanup(() => CloseOpenWindows(Application.Current), Capture);
                    Application.Current.MainWindow = null;
                    dispatcher.UnhandledException -= handler;
                }
                ThrowCapturedExceptions(capturedExceptions);
            });
        }
        finally
        {
            TestGate.Release();
        }
    }

    public static void ShowHidden(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = HiddenWindowCoordinate;
        window.Top = HiddenWindowCoordinate;
        window.Opacity = 0;
        window.ShowInTaskbar = false;
        window.ShowActivated = true;
        if (window.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue)
            window.Style = new Style(typeof(Window));

        window.Show();
        window.Opacity = 1;
    }

    public static Task FlushAsync()
        => Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background).Task;

    private static void PumpTask(
        Task task,
        Func<bool> shouldStop,
        Action<DispatcherFrame?> setActiveFrame)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(shouldStop);
        ArgumentNullException.ThrowIfNull(setActiveFrame);

        if (!task.IsCompleted && !shouldStop())
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            setActiveFrame(frame);

            task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => frame.Continue = false)),
                TaskScheduler.Default);

            Dispatcher.PushFrame(frame);
            setActiveFrame(null);
        }

        if (shouldStop())
        {
            ObserveFault(task);
            return;
        }

        task.GetAwaiter().GetResult();
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void TryCleanup(Action cleanup, Action<Exception> capture)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            capture(ex);
        }
    }

    private static void ThrowCapturedExceptions(IReadOnlyList<ExceptionDispatchInfo> capturedExceptions)
    {
        if (capturedExceptions.Count == 0)
            return;
        if (capturedExceptions.Count == 1)
            capturedExceptions[0].Throw();

        throw new AggregateException(capturedExceptions.Select(captured => captured.SourceException));
    }

    private static void CloseOpenWindows(Application application)
    {
        foreach (Window window in application.Windows.OfType<Window>().ToArray())
            window.Close();
    }
}
