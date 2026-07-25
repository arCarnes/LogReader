namespace LogReader.Tests;

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using LogReaderApplication = LogReader.App.App;

internal static class WpfTestHost
{
    private const double HiddenWindowCoordinate = -32000;

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        ExceptionDispatchInfo? capturedException = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                capturedException = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = nameof(WpfTestHost)
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        capturedException?.Throw();
    }

    public static Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var capturedExceptions = new List<ExceptionDispatchInfo>();
        var thread = new Thread(() =>
        {
            Dispatcher? dispatcher = null;
            LogReaderApplication? application = null;
            DispatcherFrame? activeFrame = null;
            DispatcherUnhandledExceptionEventHandler? unhandledExceptionHandler = null;

            void Capture(Exception exception)
                => capturedExceptions.Add(ExceptionDispatchInfo.Capture(exception));

            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(dispatcher));

                application = new LogReaderApplication();
                application.InitializeComponent();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                unhandledExceptionHandler = (_, e) =>
                {
                    Capture(e.Exception);
                    e.Handled = true;
                    if (activeFrame != null)
                        activeFrame.Continue = false;
                };
                application.DispatcherUnhandledException += unhandledExceptionHandler;

                PumpTask(
                    action(),
                    () => capturedExceptions.Count > 0,
                    frame => activeFrame = frame);
            }
            catch (Exception ex)
            {
                Capture(ex);
            }
            finally
            {
                TryCleanup(() =>
                {
                    if (application != null)
                        CloseOpenWindows(application);
                }, Capture);
                TryCleanup(() => application?.Shutdown(), Capture);
                TryCleanup(() =>
                {
                    if (dispatcher is { HasShutdownStarted: false })
                        dispatcher.InvokeShutdown();
                }, Capture);
                if (application != null && unhandledExceptionHandler != null)
                    application.DispatcherUnhandledException -= unhandledExceptionHandler;

                TryCleanup(ResetApplicationSingleton, Capture);
            }
        })
        {
            IsBackground = true,
            Name = nameof(WpfTestHost)
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        ThrowCapturedExceptions(capturedExceptions);
        return Task.CompletedTask;
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

    private static void ResetApplicationSingleton()
    {
        const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;

        typeof(Application).GetField("_appInstance", Flags)?.SetValue(null, null);
        typeof(Application).GetField("_appCreatedInThisAppDomain", Flags)?.SetValue(null, false);
    }

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
