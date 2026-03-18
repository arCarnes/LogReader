namespace LogReader.App.Converters;

using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

public class HexColorToBrushConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> BrushCache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            return BrushCache.GetOrAdd(hex, h =>
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
                brush.Freeze();
                return brush;
            });
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
