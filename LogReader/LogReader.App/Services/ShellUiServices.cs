namespace LogReader.App.Services;

using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

internal interface ILogAppearanceService
{
    void Apply(AppSettings settings);
}

internal interface ITabLifecycleScheduler
{
    IDisposable ScheduleRecurring(TimeSpan dueTime, TimeSpan interval, Action callback);
}

internal sealed class WpfLogAppearanceService : ILogAppearanceService
{
    private readonly Func<Application?> _applicationProvider;

    public WpfLogAppearanceService(Func<Application?>? applicationProvider = null)
    {
        _applicationProvider = applicationProvider ?? (() => Application.Current);
    }

    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var application = _applicationProvider();
        if (application == null)
            return;

        var fontName = string.IsNullOrWhiteSpace(settings.LogFontFamily)
            ? "Consolas"
            : settings.LogFontFamily;

        application.Resources["LogFontFamilyResource"] = new FontFamily(fontName);
        application.Resources["LogViewportFontSizeResource"] =
            (double)SettingsViewModel.NormalizeLogFontSize(settings.LogFontSize);
    }
}

internal sealed class WpfTabLifecycleScheduler : ITabLifecycleScheduler
{
    private readonly Func<Application?> _applicationProvider;

    public WpfTabLifecycleScheduler(Func<Application?>? applicationProvider = null)
    {
        _applicationProvider = applicationProvider ?? (() => Application.Current);
    }

    public IDisposable ScheduleRecurring(TimeSpan dueTime, TimeSpan interval, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new System.Threading.Timer(_ => RunOnUiThread(callback), null, dueTime, interval);
        return new TimerRegistration(timer);
    }

    private void RunOnUiThread(Action callback)
    {
        var dispatcher = _applicationProvider()?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            callback();
            return;
        }

        _ = dispatcher.BeginInvoke(callback, DispatcherPriority.Background);
    }

    private sealed class TimerRegistration : IDisposable
    {
        private readonly System.Threading.Timer _timer;
        private int _disposed;

        public TimerRegistration(System.Threading.Timer timer)
        {
            _timer = timer;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _timer.Dispose();
        }
    }
}
