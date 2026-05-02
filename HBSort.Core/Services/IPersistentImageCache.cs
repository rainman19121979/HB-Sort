using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// Persistenter Datei-Cache fuer Bilder, mit LRU-Eviction und SQLite-Sidecar
/// fuer Metadaten (Groesse, last_accessed_at, source_url).
/// Cache-Verzeichnis liegt unter %APPDATA%\HBSort\images\.
///
/// Cache-Keys nutzen ein Praefix-Schema:
///   part_{bl_part}_c{bl_color}.png   – Teil mit Farbe (oder c0 fuer ohne Farbe)
///   fig_{bl_minifig}.png             – Minifigur
///   set_{bl_set}.png                 – Set
///   bo_{md5(url)}.png                – Brickognize-Fallback
/// </summary>
public interface IPersistentImageCache
{
    /// <summary>
    /// Holt die lokale Datei aus dem Cache. Falls noch nicht vorhanden:
    /// laed von <paramref name="sourceUrl"/> herunter, speichert sie und
    /// vermerkt sie im Sidecar-DB.
    /// Bei Fehler beim Download wird eine Exception geworfen (Aufrufer entscheidet
    /// ueber Fallback).
    /// </summary>
    /// <param name="sourceUrl">HTTP(S)-URL des Bildes.</param>
    /// <param name="cacheKey">Eindeutiger Key (= Dateiname inkl. Endung).</param>
    /// <param name="ct">Abbruch-Token.</param>
    /// <returns>Absoluter lokaler Pfad zum gecachten Bild.</returns>
    Task<string> GetOrDownloadAsync(string sourceUrl, string cacheKey, CancellationToken ct = default);

    /// <summary>
    /// Aktualisiert das LastAccessedAt-Feld eines Eintrags (fuer LRU).
    /// Falls der Eintrag nicht existiert: kein Effekt.
    /// </summary>
    void TouchAccess(string cacheKey);

    /// <summary>
    /// Loescht den kompletten Cache (Dateien + Metadaten).
    /// </summary>
    /// <returns>Anzahl der geloeschten Dateien.</returns>
    Task<int> ClearAsync();

    /// <summary>
    /// Aktuelle Cache-Statistik fuer die Anzeige in Settings/Statusbalken.
    /// </summary>
    CacheStats GetStats();

    /// <summary>
    /// Wird ausgeloest wenn sich der Cache-Inhalt aendert (Add / Touch / Evict / Clear).
    /// Damit kann das UI den Status-Balken live aktualisieren.
    /// </summary>
    event EventHandler? StatsChanged;
}
