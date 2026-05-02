using LegoMinifigSorter.Core.Models;

namespace LegoMinifigSorter.Core.Services;

/// <summary>
/// Lese-Service auf der catalog.db.
/// Kapselt den direkten ADO.NET-Zugriff (Microsoft.Data.Sqlite).
/// </summary>
public interface ICatalogService
{
    /// <summary>True, wenn die catalog.db existiert und ein Schema enthaelt.</summary>
    bool CatalogExists();

    /// <summary>
    /// Liest die metadata-Tabelle. Gibt null zurueck wenn die DB nicht existiert
    /// oder keine Metadaten enthaelt.
    /// </summary>
    Task<CatalogMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>Liefert alle Farben sortiert nach Name.</summary>
    Task<List<CatalogColor>> GetAllColorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Sucht eine Farbe per ID. Null wenn nicht gefunden.</summary>
    Task<CatalogColor?> GetColorAsync(int colorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sucht eine Farbe per Name (case-insensitive). Null wenn nicht gefunden.
    /// Wird fuer den Brickognize-Farb-Lookup gebraucht, weil Brickognize NICHT
    /// Rebrickable-Color-IDs liefert (validiert 2026-04-30).
    /// </summary>
    Task<CatalogColor?> GetColorByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Sucht eine Minifigur per fig_num.</summary>
    Task<CatalogMinifig?> GetMinifigAsync(string figNum, CancellationToken cancellationToken = default);

    /// <summary>
    /// Liefert die Teileliste einer Minifigur. is_spare=1 wird ausgefiltert
    /// (siehe CLAUDE.md – Spare Parts ignorieren wir komplett).
    /// </summary>
    Task<List<CatalogMinifigPart>> GetMinifigPartsAsync(
        string figNum, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sucht Minifiguren per Volltextsuche im Namen (case-insensitive).
    /// Wird genutzt wenn Brickognize keine Rebrickable-fig_num liefert
    /// und wir per Name-Match auf den Catalog zurueckschliessen muessen.
    /// </summary>
    /// <param name="query">Suchstring (LIKE %query%).</param>
    /// <param name="limit">Maximal so viele Treffer.</param>
    Task<List<CatalogMinifig>> SearchMinifigsByNameAsync(
        string query, int limit, CancellationToken cancellationToken = default);

    /// <summary>Sucht ein Teil per part_num. Null wenn nicht gefunden.</summary>
    Task<CatalogPart?> GetPartByNumAsync(string partNum, CancellationToken cancellationToken = default);
}
