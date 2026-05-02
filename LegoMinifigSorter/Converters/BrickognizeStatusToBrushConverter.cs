using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LegoMinifigSorter.Core.Services;

namespace LegoMinifigSorter.Converters;

/// <summary>
/// Mappt BrickognizeStatus auf eine Brush fuer die Status-LED.
/// Online=gruen, Slow=gelb, Offline=rot, Unknown=grau.
/// </summary>
public class BrickognizeStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is BrickognizeStatus s)
        {
            return s switch
            {
                BrickognizeStatus.Online  => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                BrickognizeStatus.Slow    => new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                BrickognizeStatus.Offline => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                _                          => new SolidColorBrush(Color.FromRgb(158, 158, 158))
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
