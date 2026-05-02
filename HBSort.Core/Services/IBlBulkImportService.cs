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
    /// </summary>
    Task<BlBulkImportResult> ImportFromGitHubAsync(
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

/// <summary>Ergebnis eines Bulk-Imports.</summary>
public record BlBulkImportResult(
    int ItemsImported,
    int InventoriesImported,
    int FilesProcessed,
    int FilesSkipped,
    TimeSpan Duration,
    List<string> Errors);
