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
        var dashboardFontSize = SettingsViewModel.NormalizeDashboardFontSize(settings.DashboardFontSize);
        application.Resources["DashboardPrimaryFontSizeResource"] = (double)dashboardFontSize;
        application.Resources["DashboardMemberFontSizeResource"] = (double)(dashboardFontSize - 1);
        application.Resources["DashboardDetailFontSizeResource"] = (double)(dashboardFontSize - 2);
        var theme = settings.GetEffectiveTheme();
        application.Resources["AppThemeResource"] = theme;
        foreach (var (key, defaultColor, easyReadingColor, darkColor) in ThemeBrushes)
            application.Resources[key] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(theme switch
                {
                    AppTheme.EasyReading => easyReadingColor,
                    AppTheme.Dark => darkColor,
                    _ => defaultColor
                }));
        application.Resources[SystemColors.WindowBrushKey] = application.Resources["AppControlSurfaceBrush"];
        application.Resources[SystemColors.WindowTextBrushKey] = application.Resources["AppTextBrush"];
        application.Resources[SystemColors.ControlBrushKey] = application.Resources["AppSurfaceBrush"];
        application.Resources[SystemColors.ControlTextBrushKey] = application.Resources["AppTextBrush"];
        application.Resources[SystemColors.ControlLightBrushKey] = application.Resources["AppElevatedSurfaceBrush"];
        application.Resources[SystemColors.ControlDarkBrushKey] = application.Resources["AppBorderBrush"];
        application.Resources[SystemColors.GrayTextBrushKey] = application.Resources["AppDisabledTextBrush"];
        application.Resources[SystemColors.HighlightBrushKey] = application.Resources["AppSelectedMemberBrush"];
        application.Resources[SystemColors.HighlightTextBrushKey] = application.Resources["AppSelectedMemberTextBrush"];
        application.Resources[SystemColors.MenuBrushKey] = application.Resources["AppControlSurfaceBrush"];
        application.Resources[SystemColors.MenuTextBrushKey] = application.Resources["AppTextBrush"];
        application.Resources["SearchMatchHighlightingEnabledResource"] = settings.EnableSearchMatchHighlighting;
        application.Resources["SearchMatchHighlightBrushResource"] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(
                SettingsViewModel.NormalizeSearchMatchHighlightColor(settings.SearchMatchHighlightColor)));
    }

    private static readonly (string Key, string Default, string EasyReading, string Dark)[] ThemeBrushes =
    [
        ("AppBackgroundBrush", "#F7F8FA", "#E0E5EA", "#151A21"),
        ("AppSurfaceBrush", "#F4F6F8", "#D8DFE5", "#1C2530"),
        ("AppElevatedSurfaceBrush", "#FBFCFD", "#F1F3F6", "#232E3B"),
        ("AppViewportContentBrush", "#FCFDFE", "#E6EAEE", "#111820"),
        ("AppViewportCanvasBrush", "#F7F8FA", "#E6EAEE", "#151A21"),
        ("AppViewportChromeBrush", "#F5F7FA", "#D8DFE5", "#1B2530"),
        ("AppDashboardContentBrush", "#F7F8FA", "#D9E0E6", "#151A21"),
        ("AppScrollBarThumbBrush", "#AEBECB", "#AEBECB", "#536779"),
        ("AppScrollBarThumbHoverBrush", "#8FA4B8", "#8FA4B8", "#7892A8"),
        ("AppBranchRowBrush", "#F9FBFD", "#E8EDF1", "#202B36"),
        ("AppSearchPanelSurfaceBrush", "#F1F5F8", "#D8DFE5", "#1B2732"),
        ("AppFileHeaderBrush", "#F8FAFC", "#E8EDF1", "#212E3B"),
        ("AppTableHeaderBrush", "#F7F8FA", "#D8DFE5", "#1E2935"),
        ("AppBorderBrush", "#D6DEE6", "#BCC8D2", "#394A5B"),
        ("AppDividerBrush", "#E6EBF0", "#CBD4DD", "#2D3C4A"),
        ("AppSearchPanelBorderBrush", "#CDD8E2", "#BCC8D2", "#40556A"),
        ("AppResultsDividerBrush", "#C6D2DD", "#CBD4DD", "#45586A"),
        ("AppFocusBorderBrush", "#9CB8D4", "#9CB8D4", "#80B8ED"),
        ("AppTextBrush", "#1F2937", "#1F2937", "#E6EDF5"),
        ("AppMutedTextBrush", "#5B6470", "#5B6470", "#B0BFCE"),
        ("AppPlaceholderTextBrush", "#8F99A5", "#8F99A5", "#8899AA"),
        ("AppDisclosureBrush", "#666666", "#666666", "#B0BFCE"),
        ("AppSelectedRowBrush", "#EAF4FE", "#EAF4FE", "#253D55"),
        ("AppSelectedMemberBrush", "#CFE1F4", "#CFE1F4", "#31506B"),
        ("AppViewportSelectionBrush", "#B0D4FF", "#B0D4FF", "#31506B"),
        ("AppSelectedMemberTextBrush", "#143A5A", "#143A5A", "#EFF7FF"),
        ("AppErrorBrush", "#CC2222", "#CC2222", "#FF7B7B"),
        ("AppControlSurfaceBrush", "#FFFFFF", "#F1F3F6", "#202B36"),
        ("AppControlHoverBrush", "#EAF4FE", "#EAF4FE", "#30455C"),
        ("AppControlPressedBrush", "#DCEAF8", "#DCEAF8", "#385776"),
        ("AppDisabledTextBrush", "#8F99A5", "#8F99A5", "#8494A4"),
        ("AppWarningTextBrush", "#B7791F", "#B7791F", "#F5C36D"),
        ("AppWarningSurfaceBrush", "#FFF8E7", "#FFF8E7", "#3B311F"),
        ("AppWarningBorderBrush", "#E5C07B", "#E5C07B", "#8C6B36"),
        ("AppInfoTextBrush", "#355C7D", "#355C7D", "#9CCBEE")
    ];
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
