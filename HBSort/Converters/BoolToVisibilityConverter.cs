using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HBSort.Converters;

/// <summary>
/// Standard-Konverter bool -> Visibility.
/// true  -> Visible
/// false -> Collapsed
///
/// Wird z.B. fuer den Fehler-Bereich im Splash-Fenster verwendet,
/// der nur bei HasError = true sichtbar ist.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
