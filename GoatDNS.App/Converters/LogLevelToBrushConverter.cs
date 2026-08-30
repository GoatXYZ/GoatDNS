using GoatDNS.Core.Config;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace GoatDNS.App.Converters;

/// <summary>Colours a log row by severity so errors stand out in the live stream.</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Error = new(Colors.IndianRed);
    private static readonly SolidColorBrush Verbose = new(Colors.Gray);
    private static readonly SolidColorBrush Debug = new(Colors.DimGray);

    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        LogVerbosity.ErrorsOnly => Error,
        LogVerbosity.Verbose => Verbose,
        LogVerbosity.Debug => Debug,
        // Normal (and anything else) uses the theme's default text colour.
        _ => Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush ?? Error,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
