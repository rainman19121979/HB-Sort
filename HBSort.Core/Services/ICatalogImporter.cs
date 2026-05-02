namespace HBSort.Core.Services;

/// <summary>
/// Importiert die Rebrickable-CSV-Daten in die catalog.db.
/// Quelle entweder Embedded Resources (Erstinstallation) oder
/// vom User per File-Picker gewaehlte ZIPs (spaeteres Update).
/// </summary>
public interface ICatalogImporter
{
    /// <summary>
    /// Erstinitialisierung: Liest die Embedded ZIPs aus der Core-DLL und
    /// schreibt sie in die uebergebene catalog.db.
    /// </summary>
    /// <param name="targetDbPath">Pfad zu der zu erstellenden catalog.db.</param>
    /// <param name="progress">Callback fuer Fortschritts-Updates an die UI (Splash).</param>
    /// <param name="cancellationToken">Abbruch-Token (falls User die App schliesst).</param>
    Task ImportFromEmbeddedAsync(
        string targetDbPath,
        IProgress<CatalogImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spaeteres Update: Liest die uebergebenen ZIP-Dateien (vom File-Picker) und
    /// schreibt sie in eine neue catalog.db.
    /// Erwartet alle 11 Dateien (colors, parts, minifigs, ...). Fehlt eine, wird
    /// ein Fehler geworfen.
    /// </summary>
    /// <param name="zipPaths">Pfade zu den ZIP-Dateien.</param>
    /// <param name="targetDbPath">Pfad zu der zu erstellenden DB-Datei.</param>
    /// <param name="progress">Callback fuer Fortschritts-Updates.</param>
    /// <param name="cancellationToken">Abbruch-Token.</param>
    Task ImportFromZipsAsync(
        IEnumerable<string> zipPaths,
        string targetDbPath,
        IProgress<CatalogImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fortschritts-Meldung an die UI waehrend des Imports.
/// </summary>
public class CatalogImportProgress
{
    /// <summary>Aktueller Schritt (1-basiert).</summary>
    public int CurrentStep { get; set; }

    /// <summary>Gesamtanzahl der Schritte.</summary>
    public int TotalSteps { get; set; }

    /// <summary>Beschreibung des aktuellen Schritts (z.B. "Importiere colors..."). Wird der UI angezeigt.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Anzahl der bislang verarbeiteten Zeilen im aktuellen Schritt (optional).</summary>
    public long? RowsProcessed { get; set; }
}
