namespace LogReader.Tests;

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using LogReaderApplication = LogReader.App.App;

internal static class WpfTestHost
{
    public static Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        ExceptionDispatchInfo? capturedException = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(dispatcher));

                var application = new LogReaderApplication();
                application.InitializeComponent();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                PumpTask(action());
                CloseOpenWindows(application);
                application.Shutdown();
            }
            catch (Exception ex)
            {
                capturedException ??= ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                ResetApplicationSingleton();
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
        return Task.CompletedTask;
    }

    public static Task FlushAsync()
        => Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background).Task;

    private static void ResetApplicationSingleton()
    {
        const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;

        typeof(Application).GetField("_appInstance", Flags)?.SetValue(null, null);
        typeof(Application).GetField("_appCreatedInThisAppDomain", Flags)?.SetValue(null, false);
    }

    private static void PumpTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();

            task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => frame.Continue = false)),
                TaskScheduler.Default);

            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
    }

    private static void CloseOpenWindows(Application application)
    {
        foreach (Window window in application.Windows.OfType<Window>().ToArray())
            window.Close();
    }
}
