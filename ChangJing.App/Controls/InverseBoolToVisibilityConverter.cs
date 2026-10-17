using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ChangJing.App.Controls;

/// <summary>bool → Visibility，与标准转换器相反（true 折叠、false 显示）</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
