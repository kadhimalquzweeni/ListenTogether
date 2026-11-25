using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ListenTogether.App;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool flag ? !flag : DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool flag ? !flag : DependencyProperty.UnsetValue;
}
