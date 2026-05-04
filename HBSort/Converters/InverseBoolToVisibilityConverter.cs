using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HBSort.Converters;

/// <summary>
/// Konvertiert einen bool-Wert umgekehrt in Visibility:
/// true → Collapsed (unsichtbar), false → Visible (sichtbar).
///
/// Wird verwendet um z.B. einen Platzhalter-Text anzuzeigen
/// wenn die Kamera NICHT laeuft (IsCameraRunning = false → Text sichtbar).
///
/// Ein Converter ist in WPF die Standard-Methode um Werte zwischen
/// ViewModel und View umzurechnen.
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            // Umgekehrt: true = verstecken, false = anzeigen
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // ConvertBack wird hier nicht gebraucht (nur Einweg-Binding)
        throw new NotImplementedException();
    }
}
