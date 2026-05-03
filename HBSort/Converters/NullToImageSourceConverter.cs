using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace HBSort.Converters;

/// <summary>
/// Schuetzt Image.Source-Bindings vor null/leerem-String.
///
/// Hintergrund: WPFs eingebauter ImageSourceConverter wirft eine
/// NotSupportedException, wenn ein Binding einen null-Wert liefert
/// ("Cannot convert '&lt;null&gt;' to ImageSource"). Das ist nur eine
/// Trace-Warnung, blaeht aber das Output-Fenster auf und macht echte
/// Fehler unsichtbar.
///
/// Verwendung:
///   &lt;Image Source="{Binding ImageUrl, Converter={StaticResource NullToImageSource}}"/&gt;
///
/// Strategie:
/// - null              -&gt; null   (Image.Source akzeptiert null)
/// - leerer String     -&gt; null
/// - Whitespace-String -&gt; null
/// - alles andere (string, Uri, ImageSource) wird unveraendert weitergereicht;
///   die Default-TypeConverter-Pipeline von WPF konvertiert dann string-&gt;BitmapImage.
/// </summary>
public class NullToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Direkter null-Schutz: Bindings ohne Quelle (z.B. PendingMinifig.ImageUrl
        // bevor der BL-Cache antwortet) wuerden hier unveraendert null liefern.
        if (value == null) return null;

        // Leere/Whitespace-Strings ebenfalls als null behandeln, damit das Image
        // einfach unsichtbar bleibt statt eine Exception zu werfen.
        if (value is string s && string.IsNullOrWhiteSpace(s)) return null;

        // Schon eine ImageSource? Direkt zurueckgeben (kein Roundtrip durch
        // den TypeConverter noetig).
        if (value is ImageSource src) return src;

        // Ansonsten den Wert (string-URL, Uri, Pfad) ungeaendert weiterreichen.
        // WPF wendet den eingebauten ImageSourceConverter an, der mit nicht-null-
        // Werten korrekt umgeht.
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Image.Source ist immer One-Way - ConvertBack wird nie aufgerufen.
        throw new NotImplementedException();
    }
}
