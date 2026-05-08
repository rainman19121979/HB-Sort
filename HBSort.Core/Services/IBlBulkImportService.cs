namespace HBSort.Core.Services;

/// <summary>
/// Phase 5.5: Bulk-Import von BL-Catalog-Daten aus dem BrickStore-XML-Format
/// (rgriebl/brickstore-database). Befuellt bl_items + bl_subsets in einem
/// Rutsch (~110.000 Items, ~140.000 Subsets in 1-3 Minuten).
///
/// Lizenz-Hinweis: BL-Daten sind Eigentum von BrickLink, Aufbereitung durch
/// BrickStore von Robert Griebl (GPL-3, brickstore.dev).
/// </summary>
public interface IBlBulkImportService
{
    /// <summary>
    /// Laedt downloads.zip vom brickstore-database GitHub-Release "latest" herunter,
    /// entpackt in einen Temp-Ordner und importiert. Loescht den Temp-Ordner danach.
    ///
    /// UX X.29 (v0.1.16): mit ETag-Caching + Inhalts-Hash-Check. Wenn
    /// <paramref name="previousEtag"/> uebergeben wird, sendet der Service
    /// einen If-None-Match-Header. Bei HTTP 304 wird sofort
    /// <see cref="BlBulkImportResult.Skipped"/>=true zurueckgegeben (kein
    /// Download-Body, kein Entpacken, keine DB-Updates). Falls der Server
    /// keinen ETag liefert oder der Cache verloren ist, wird das ZIP zwar
    /// heruntergeladen, aber der SHA256-Hash mit
    /// <paramref name="previousContentHash"/> verglichen - bei gleichem Hash
    /// wird der Import ebenfalls uebersprungen.
    /// </summary>
    Task<BlBulkImportResult> ImportFromGitHubAsync(
        string? previousEtag = null,
        string? previousContentHash = null,
        IProgress<BlBulkImportProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Importiert aus einem lokalen Ordner mit der erwarteten Struktur:
    ///   items/M.xml, items/P.xml, M/*.xml
    /// </summary>
    Task<BlBulkImportResult> ImportFromFolderAsync(
        string folderPath,
        IProgress<BlBulkImportProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>Progress-Meldung waehrend des Imports.</summary>
public record BlBulkImportProgress(
    string Phase,
    int Current,
    int Total,
    string CurrentItem);

/// <summary>
/// Ergebnis eines Bulk-Imports.
///
/// UX X.29 (v0.1.16): Skipped, SkipReason, NewEtag, NewContentHash neu.
/// Bei <see cref="Skipped"/>=true sind ItemsImported / InventoriesImported = 0.
/// Aufrufer (z.B. MainViewModel) verwendet NewEtag + NewContentHash um
/// die naechste Pruefung effizienter zu machen.
/// </summary>
public record BlBulkImportResult(
    int ItemsImported,
    int InventoriesImported,
    int FilesProcessed,
    int FilesSkipped,
    TimeSpan Duration,
    List<string> Errors)
{
    /// <summary>True wenn der Import uebersprungen wurde (304 oder gleicher Hash).</summary>
    public bool Skipped { get; init; }

    /// <summary>Begruendung fuer Skipped (z.B. "HTTP 304 Not Modified").</summary>
    public string? SkipReason { get; init; }

    /// <summary>ETag des heruntergeladenen ZIPs (wenn vorhanden) - vom Aufrufer in Settings persistieren.</summary>
    public string? NewEtag { get; init; }

    /// <summary>SHA256-Hash des heruntergeladenen ZIPs - vom Aufrufer in Settings persistieren.</summary>
    public string? NewContentHash { get; init; }
}
