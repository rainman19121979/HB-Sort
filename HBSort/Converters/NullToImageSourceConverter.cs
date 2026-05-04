using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace HBSort.Converters;

/// <summary>
/// Schuetzt Image.Source-Bindings vor null/leerem-String UND lockt das
/// Bild nicht auf der Disk fest.
///
/// Hintergrund: WPFs eingebauter ImageSourceConverter wirft eine
/// NotSupportedException, wenn ein Binding einen null-Wert liefert
/// ("Cannot convert '&lt;null&gt;' to ImageSource"). Das ist nur eine
/// Trace-Warnung, blaeht aber das Output-Fenster auf und macht echte
/// Fehler unsichtbar.
///
/// Audit W-7 (2026-05-04): WPFs Default-Pipeline laedt Bilder lazy
/// (BitmapCacheOption.Default = OnDemand), d.h. der Stream/File-Handle
/// bleibt offen solange das Image im Visual-Tree haengt. Folge: der
/// PersistentImageCache kann gecachte Dateien auf der Disk nicht
/// loeschen oder ersetzen ("file is in use"). Wir bauen das BitmapImage
/// jetzt EXPLIZIT mit BitmapCacheOption.OnLoad und Freeze() - damit ist
/// das Bild komplett im Speicher und der Stream sofort wieder frei.
///
/// Verwendung:
///   &lt;Image Source="{Binding ImageUrl, Converter={StaticResource NullToImageSource}}"/&gt;
///
/// Strategie:
/// - null              -&gt; null
/// - leerer String     -&gt; null
/// - Whitespace-String -&gt; null
/// - schon ImageSource -&gt; unveraendert zurueckgeben
/// - string/Uri        -&gt; BitmapImage mit OnLoad + Freeze
/// - ungueltige URL    -&gt; null + Log.Warning, kein Crash
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

        // string oder Uri -> BitmapImage manuell mit OnLoad bauen.
        Uri? uri = value switch
        {
            Uri u                 => u,
            string str            => TryParseUri(str),
            _                     => null
        };

        if (uri == null) return null;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            // OnLoad = Stream/File komplett in den Speicher laden, dann sofort
            // schliessen. Andernfalls bleibt der File-Handle waehrend der
            // gesamten Lebenszeit des Image-Elements offen.
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // IgnoreImageCache umgeht den WPF-internen URI-Cache, damit wir
            // ein Bild auf der gleichen URL nach dem Refresh nicht versehentlich
            // alt sehen.
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = uri;
            bmp.EndInit();
            // Freeze macht das Bild Thread-sicher und WPF-rendering-effizient.
            // Es ist nach dem Freeze unveraenderlich - perfekt fuer ein gecachtes
            // Bild aus einer Datei.
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            // Ungueltige URL / fehlende Datei: kein Crash, nur ein Hinweis im Log.
            // UI zeigt dann gar kein Bild (so wie bei null).
            Log.Warning(ex, "Bild konnte nicht geladen werden: {Uri}", uri);
            return null;
        }
    }

    /// <summary>
    /// Parst entweder einen vollstaendigen URI (http://, file://, pack://) oder
    /// einen lokalen Datei-Pfad. Liefert null bei Mist statt zu werfen.
    /// </summary>
    private static Uri? TryParseUri(string str)
    {
        // Absolute URI inkl. file:/// und http(s)://
        if (Uri.TryCreate(str, UriKind.Absolute, out var abs)) return abs;

        // Relativer Pfad oder Windows-Pfad ohne Schema -> als file://-Uri.
        if (Uri.TryCreate(str, UriKind.RelativeOrAbsolute, out var rel)) return rel;

        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Image.Source ist immer One-Way - ConvertBack wird nie aufgerufen.
        throw new NotImplementedException();
    }
}
