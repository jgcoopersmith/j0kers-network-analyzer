using System.Globalization;
using System.Windows.Data;

namespace NetAnalyzer;

/// <summary>
/// True when the bound number equals the converter parameter. Used to give a group of menu
/// items radio behaviour: each one shows its tick only while it is the selected value.
/// </summary>
public sealed class ValueEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double actual ||
            !double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var expected))
            return false;

        // Values come from a fixed menu, so an exact-ish comparison is enough.
        return Math.Abs(actual - expected) < 0.001;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
