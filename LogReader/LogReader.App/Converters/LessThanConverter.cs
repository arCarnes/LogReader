namespace LogReader.App.Converters;

using System;
using System.Globalization;
using System.Windows.Data;

public class LessThanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double currentWidth || parameter == null)
            return false;

        return double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            && currentWidth < threshold;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
