namespace LogReader.App.Services;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LogReader.Core.Models;

internal static class WindowTitleBarTheme
{
    private const int UseImmersiveDarkModeAttribute = 20;
    private const int CaptionColorAttribute = 35;
    private const int TextColorAttribute = 36;
    private const int DefaultColor = -1;
    private const int LightCaptionColor = 0x00E5DFD8; // COLORREF for #D8DFE5.
    private const int LightTextColor = 0x0037291F; // COLORREF for #1F2937.
    private const int DarkCaptionColor = 0x00211A15; // COLORREF for #151A21.
    private const int DarkTextColor = 0x00F5EDE6; // COLORREF for #E6EDF5.

    public static readonly DependencyProperty ThemeProperty = DependencyProperty.RegisterAttached(
        "Theme",
        typeof(AppTheme),
        typeof(WindowTitleBarTheme),
        new PropertyMetadata(AppTheme.Default, OnThemeChanged));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(WindowTitleBarTheme),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty IsSourceInitializedSubscribedProperty = DependencyProperty.RegisterAttached(
        "IsSourceInitializedSubscribed",
        typeof(bool),
        typeof(WindowTitleBarTheme),
        new PropertyMetadata(false));

    public static AppTheme GetTheme(Window window)
        => (AppTheme)window.GetValue(ThemeProperty);

    public static void SetTheme(Window window, AppTheme value)
        => window.SetValue(ThemeProperty, value);

    public static bool GetIsEnabled(Window window)
        => (bool)window.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Window window, bool value)
        => window.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is Window window && (bool)e.NewValue)
            SubscribeAndApply(window);
    }

    private static void OnThemeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Window window)
            return;

        if (GetIsEnabled(window))
            SubscribeAndApply(window);
    }

    private static void SubscribeAndApply(Window window)
    {
        if (!(bool)window.GetValue(IsSourceInitializedSubscribedProperty))
        {
            window.SetValue(IsSourceInitializedSubscribedProperty, true);
            window.SourceInitialized += OnSourceInitialized;
        }

        Apply(window);
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
            Apply(window);
    }

    private static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var theme = GetTheme(window);
        var immersiveDarkMode = theme == AppTheme.Dark ? 1 : 0;
        var captionColor = theme switch
        {
            AppTheme.EasyReading => LightCaptionColor,
            AppTheme.Dark => DarkCaptionColor,
            _ => DefaultColor
        };
        var textColor = theme switch
        {
            AppTheme.EasyReading => LightTextColor,
            AppTheme.Dark => DarkTextColor,
            _ => DefaultColor
        };

        // Older Windows versions may not support these attributes. Keep the standard frame if so.
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeAttribute, ref immersiveDarkMode, sizeof(int));
        _ = DwmSetWindowAttribute(handle, CaptionColorAttribute, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, TextColorAttribute, ref textColor, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int valueSize);
}
