namespace LogReader.App.Controls;

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LogReader.App.Services;

public class DashboardPathTextBlock : TextBlock
{
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath),
        typeof(string),
        typeof(DashboardPathTextBlock),
        new PropertyMetadata(string.Empty, OnDisplayInputChanged));

    public static readonly DependencyProperty FileNameProperty = DependencyProperty.Register(
        nameof(FileName),
        typeof(string),
        typeof(DashboardPathTextBlock),
        new PropertyMetadata(string.Empty, OnDisplayInputChanged));

    public static readonly DependencyProperty ShowFullPathProperty = DependencyProperty.Register(
        nameof(ShowFullPath),
        typeof(bool),
        typeof(DashboardPathTextBlock),
        new PropertyMetadata(false, OnDisplayInputChanged));

    public string FilePath
    {
        get => (string)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public string FileName
    {
        get => (string)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    public bool ShowFullPath
    {
        get => (bool)GetValue(ShowFullPathProperty);
        set => SetValue(ShowFullPathProperty, value);
    }

    public DashboardPathTextBlock()
    {
        Loaded += (_, _) => UpdateDisplayText();
        SizeChanged += (_, _) => UpdateDisplayText();
    }

    private static void OnDisplayInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DashboardPathTextBlock textBlock)
            textBlock.UpdateDisplayText();
    }

    private void UpdateDisplayText()
    {
        var fallbackFileName = string.IsNullOrWhiteSpace(FileName)
            ? Path.GetFileName(FilePath ?? string.Empty)
            : FileName;

        Text = DashboardPathFormatter.FormatToWidth(
            FilePath,
            fallbackFileName,
            ShowFullPath,
            ActualWidth,
            MeasureTextWidth);
    }

    private double MeasureTextWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize,
            Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        return formattedText.WidthIncludingTrailingWhitespace;
    }
}
