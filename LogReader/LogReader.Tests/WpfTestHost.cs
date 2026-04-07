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
        var completed = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                var application = new LogReaderApplication();
                application.InitializeComponent();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await action();
                    }
                    catch (Exception ex)
                    {
                        capturedException = ExceptionDispatchInfo.Capture(ex);
                    }
                    finally
                    {
                        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                });

                Dispatcher.Run();
                application.Shutdown();
            }
            catch (Exception ex)
            {
                capturedException ??= ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                ResetApplicationSingleton();
                completed.Set();
            }
        })
        {
            IsBackground = true,
            Name = nameof(WpfTestHost)
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        completed.Wait();
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
}
