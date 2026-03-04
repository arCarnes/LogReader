namespace LogReader.App.Converters;

using System.Globalization;
using System.Windows.Data;
using LogReader.Core.Models;

public class EncodingToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FileEncoding current && parameter is string param && Enum.TryParse<FileEncoding>(param, out var target))
            return current == target;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string param && Enum.TryParse<FileEncoding>(param, out var target))
            return target;
        return Binding.DoNothing; // Unchecking a radio button should not update the binding
    }
}
