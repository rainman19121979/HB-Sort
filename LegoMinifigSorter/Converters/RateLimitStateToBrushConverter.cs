using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LegoMinifigSorter.Core.Models.Bricklink;

namespace LegoMinifigSorter.Converters;

/// <summary>
/// Mappt RateLimitState auf eine Brush fuer den Counter-Text im Status-Balken.
/// Ok=gruen, Warning=gelb-orange, Critical=orange, Blocked=rot.
/// </summary>
public class RateLimitStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is RateLimitState s)
        {
            return s switch
            {
                RateLimitState.Ok       => new SolidColorBrush(Color.FromRgb(76, 175, 80)),     // gruen
                RateLimitState.Warning  => new SolidColorBrush(Color.FromRgb(255, 165, 0)),     // gelb-orange
                RateLimitState.Critical => new SolidColorBrush(Color.FromRgb(255, 102, 0)),     // orange
                RateLimitState.Blocked  => new SolidColorBrush(Color.FromRgb(204, 0, 0)),       // rot
                _                        => Brushes.Gray
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
