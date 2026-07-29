using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NetAnalyzer;

/// <summary>
/// Visible when the bound enum matches any name in the parameter, e.g. "Stream" or
/// "Stream|Widget". Prefix the parameter with "!" to invert: "!Widget".
/// </summary>
public sealed class EnumVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var spec = parameter as string ?? "";
        var invert = spec.StartsWith('!');
        if (invert)
            spec = spec[1..];

        var name = value?.ToString() ?? "";
        var match = spec.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));

        return match != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
