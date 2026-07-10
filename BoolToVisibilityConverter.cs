using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MemeManager;

public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, System.Type targetType, object parameter, string language)
    {
        var b = value is bool bb && bb;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
    {
        if (value is Visibility v)
            return Invert ? v != Visibility.Visible : v == Visibility.Visible;
        return false;
    }
}
