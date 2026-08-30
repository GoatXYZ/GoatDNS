using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace GoatDNS.App.Converters;

/// <summary>
/// Maps a bool to <see cref="Visibility"/>. Pass ConverterParameter="Invert" to flip it,
/// so a single shared instance covers both directions (WinUI has no built-in equivalent).
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;
        if (IsInvert(parameter)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        bool visible = value is Visibility v && v == Visibility.Visible;
        return IsInvert(parameter) ? !visible : visible;
    }

    private static bool IsInvert(object parameter) =>
        parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
}
