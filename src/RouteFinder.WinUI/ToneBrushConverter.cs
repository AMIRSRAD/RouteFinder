using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace RouteFinder.WinUI;

/// <summary>Maps a route quality tone to a semantic color. Pass "background" for the soft pill fill.</summary>
public sealed partial class ToneBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        int tone = value is int number ? number : 0;
        string key = tone switch
        {
            1 => "SystemFillColorSuccessBrush",
            2 => "SystemFillColorCautionBrush",
            3 or 4 => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush"
        };
        var brush = Application.Current.Resources[key] as SolidColorBrush ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
        return parameter as string == "background"
            ? new SolidColorBrush(brush.Color) { Opacity = tone == 0 ? 0.08 : 0.16 }
            : brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
