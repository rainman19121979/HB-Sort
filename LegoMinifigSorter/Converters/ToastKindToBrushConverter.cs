using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LegoMinifigSorter.Services;

namespace LegoMinifigSorter.Converters;

/// <summary>
/// Mappt ToastKind auf eine Brush fuer den Hintergrund.
/// Info=blau, Success=gruen, Warning=gelb-orange, Error=rot.
/// </summary>
public class ToastKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ToastKind kind)
        {
            return kind switch
            {
                ToastKind.Success => new SolidColorBrush(Color.FromRgb(76, 175, 80)),    // Material green 500
                ToastKind.Warning => new SolidColorBrush(Color.FromRgb(255, 152, 0)),   // Material orange 500
                ToastKind.Error   => new SolidColorBrush(Color.FromRgb(244, 67, 54)),   // Material red 500
                _                 => new SolidColorBrush(Color.FromRgb(33, 150, 243))   // Material blue 500
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
