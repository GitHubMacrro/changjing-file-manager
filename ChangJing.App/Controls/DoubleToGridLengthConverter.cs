using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ChangJing.App.Controls;

/// <summary>把 double 转成 GridLength，用于把 ViewModel 的侧栏宽度绑到 ColumnDefinition.Width</summary>
public sealed class DoubleToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && !double.IsNaN(d) && !double.IsInfinity(d) && d >= 0)
        {
            return new GridLength(d, GridUnitType.Pixel);
        }

        return GridLength.Auto;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is GridLength length && length.IsAbsolute)
        {
            return length.Value;
        }

        return Binding.DoNothing;
    }
}
