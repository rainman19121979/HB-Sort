namespace HBSort.Core.Models;

/// <summary>
/// Statistik-Snapshot fuer den persistenten Bild-Cache.
/// LimitBytes = 0 bedeutet "unbegrenzt".
/// </summary>
public record CacheStats(
    int FileCount,
    long TotalSizeBytes,
    long LimitBytes,
    DateTime? OldestAccess);

/// <summary>
/// Einstellungen fuer den persistenten Bild-Cache.
/// Werden im AppSettings.ImageCache-Subobjekt gespeichert.
/// </summary>
public class ImageCacheSettings
{
    /// <summary>
    /// Maximale Cache-Groesse in Megabyte.
    /// Default: 1024 MB (1 GB).
    /// 0 = unbegrenzt (kein Eviction).
    /// </summary>
    public int LimitMb { get; set; } = 1024;

    /// <summary>
    /// BrickLink-Bilder bevorzugen (echte Farb-Fotos) statt der Brickognize-
    /// Graustufen-Renderings. Bei false wird immer das Brickognize-Bild
    /// aus der API-Antwort genutzt.
    /// </summary>
    public bool PreferBricklinkImages { get; set; } = true;

    /// <summary>
    /// Wenn eine Minifigur erkannt wurde: im Hintergrund die Teile-Bilder
    /// schon vorab in den Cache laden, damit der Modus-A-Workflow
    /// (Phase 3+) sofort responsive ist.
    /// </summary>
    public bool PreloadOnMinifigScan { get; set; } = true;
}
