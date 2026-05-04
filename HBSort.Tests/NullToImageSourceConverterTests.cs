using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HBSort.Converters;

namespace HBSort.Tests;

/// <summary>
/// Audit W-7: Tests fuer den NullToImageSourceConverter.
///
/// Schwerpunkte:
/// - null/empty/whitespace -> null (kein Crash)
/// - schon-ImageSource bleibt unveraendert
/// - lokale Datei wird mit BitmapCacheOption.OnLoad geladen, sodass
///   die Datei nach dem Convert wieder geloescht werden kann
/// - ungueltige URL -> null statt Crash
/// </summary>
public class NullToImageSourceConverterTests : IDisposable
{
    private readonly NullToImageSourceConverter _sut = new();
    private readonly string _tempDir;

    public NullToImageSourceConverterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HBSort-NullToImageTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Convert_null_returns_null()
    {
        var result = _sut.Convert(null!, typeof(ImageSource), null!, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_empty_string_returns_null()
    {
        var result = _sut.Convert(string.Empty, typeof(ImageSource), null!, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_whitespace_string_returns_null()
    {
        var result = _sut.Convert("   ", typeof(ImageSource), null!, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_existing_imagesource_returns_it_unchanged()
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri("pack://application:,,,/HBSort;component/Resources/app.ico", UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        try { bmp.EndInit(); bmp.Freeze(); }
        catch
        {
            // Im Test-Hosting ist die pack-URI evtl. nicht aufloesbar -
            // in dem Fall ueberspringen wir den Test (keine Assertion zu pruefen).
            return;
        }

        var result = _sut.Convert(bmp, typeof(ImageSource), null!, CultureInfo.InvariantCulture);
        Assert.Same(bmp, result);
    }

    [Fact]
    public void Convert_local_file_uses_OnLoad_and_releases_handle()
    {
        // Eine winzige PNG erstellen (1x1 Pixel transparent), dann Converter
        // aufrufen, dann versuchen die Datei zu loeschen. Mit OnLoad muss
        // der Handle nach dem Convert wieder frei sein.
        var path = Path.Combine(_tempDir, "tiny.png");
        File.WriteAllBytes(path, MinimalPngBytes());

        var result = _sut.Convert(path, typeof(ImageSource), null!, CultureInfo.InvariantCulture);

        Assert.NotNull(result);
        Assert.IsAssignableFrom<ImageSource>(result);

        // Der entscheidende Test: die Datei darf direkt nach dem Convert
        // geloescht werden. Wenn WPFs Default-Pipeline (OnDemand) genutzt
        // wuerde, blieb die Datei gelockt bis ein Garbage-Collector laeuft.
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Convert_invalid_path_returns_null_without_crashing()
    {
        // Pfad zu einer nicht-existierenden Datei. Der BitmapImage-Loader
        // wirft eine FileNotFoundException - der Converter muss das fangen.
        var bogus = Path.Combine(_tempDir, "does-not-exist.png");

        var result = _sut.Convert(bogus, typeof(ImageSource), null!, CultureInfo.InvariantCulture);

        Assert.Null(result);
    }

    /// <summary>
    /// Minimal-valides PNG (1x1 Pixel, weiss). Reicht fuer den BitmapImage-Loader.
    /// Hex-Bytes aus einer bekannten Referenz - keine Library noetig.
    /// </summary>
    private static byte[] MinimalPngBytes() => new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG-Signature
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR-Chunk
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1x1
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, // IDAT
        0x78, 0x9C, 0x63, 0xFA, 0xCF, 0x00, 0x00, 0x00,
        0x02, 0x00, 0x01, 0xE5, 0x27, 0xDE, 0xFC,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, // IEND
        0xAE, 0x42, 0x60, 0x82
    };
}
