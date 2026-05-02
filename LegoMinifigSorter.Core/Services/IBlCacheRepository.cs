using LegoMinifigSorter.Core.Models.Bricklink;

namespace LegoMinifigSorter.Core.Services;

/// <summary>
/// Direkt-Zugriff auf bl_cache.db. Microsoft.Data.Sqlite, kein EF.
/// Keine Cache-Logik (wann refresh, wann fallback) – die liegt im BlCatalogService.
/// </summary>
public interface IBlCacheRepository
{
    // --- Items ---

    Task<BlItem?> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default);

    /// <summary>
    /// Speichert oder aktualisiert einen Item-Eintrag. Wichtige Schutz-Regel:
    /// ein bestehender 'Full'-Eintrag wird NIE durch einen 'Subset'-Eintrag
    /// ueberschrieben. Andersrum darf 'Subset' durch 'Full' aufgewertet werden.
    /// </summary>
    Task UpsertItemAsync(BlItem item, CancellationToken ct = default);

    /// <summary>Bulk-Variante mit derselben Schutz-Regel pro Eintrag.</summary>
    Task UpsertItemsAsync(IEnumerable<BlItem> items, CancellationToken ct = default);

    /// <summary>
    /// True wenn der Eintrag fehlt oder aelter als <paramref name="staleDays"/> Tage ist.
    /// </summary>
    Task<bool> IsItemStaleAsync(string itemType, string itemNo, int staleDays, CancellationToken ct = default);

    // --- Subsets ---

    Task<List<BlSubset>> GetSubsetsAsync(string parentType, string parentNo, CancellationToken ct = default);

    /// <summary>
    /// Loescht alle Subsets fuer (parentType,parentNo) und schreibt die neue Liste
    /// in einer Transaction. So bleiben "weggefallene" Eintraege nicht zurueck.
    /// </summary>
    Task ReplaceSubsetsAsync(string parentType, string parentNo, IEnumerable<BlSubset> subsets, CancellationToken ct = default);

    /// <summary>
    /// Reverse-Lookup: welche Parent-Items enthalten dieses Item in dieser Farbe?
    /// (Phase 5 nutzt das fuer "wartende Figuren brauchen Teil X").
    /// </summary>
    Task<List<string>> FindParentsByItemAsync(string itemType, string itemNo, int colorId, CancellationToken ct = default);

    // --- Colors ---

    Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default);

    Task UpsertColorsAsync(IEnumerable<BlColor> colors, CancellationToken ct = default);

    // --- Known-Colors per Part (Phase 5) ---

    /// <summary>Liste der gecachten Color-IDs fuer ein Teil. Leer wenn nicht gecached.</summary>
    Task<List<int>> GetKnownColorIdsAsync(string partNo, CancellationToken ct = default);

    /// <summary>Aelteste fetched_at fuer einen part_no-Eintrag, oder null falls nicht gecached.</summary>
    Task<DateTime?> GetKnownColorsFetchedAtAsync(string partNo, CancellationToken ct = default);

    /// <summary>Loescht alte Eintraege fuer partNo und schreibt die neue Liste in einer Transaction.</summary>
    Task ReplaceKnownColorsAsync(string partNo, IEnumerable<int> colorIds, CancellationToken ct = default);

    // --- Maintenance ---

    Task<BlCacheStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>Loescht Items + Subsets aelter als <paramref name="staleDays"/>. Returns Anzahl geloeschter Eintraege.</summary>
    Task<int> ClearStaleAsync(int staleDays, CancellationToken ct = default);

    /// <summary>Loescht den ganzen Cache (DELETEs in den Tabellen, DB-File bleibt).</summary>
    Task ClearAllAsync(CancellationToken ct = default);

    // --- API-Call-Log (Phase R2.5) ---

    /// <summary>Schreibt einen Eintrag in api_call_log.</summary>
    Task LogApiCallAsync(string method, string? itemType, string? itemNo,
        int responseTimeMs, int statusCode, bool success, CancellationToken ct = default);

    /// <summary>Anzahl Eintraege im rollenden Zeitfenster (z.B. letzte 24h).</summary>
    Task<int> GetCallCountInWindowAsync(TimeSpan window, CancellationToken ct = default);

    /// <summary>Anzahl Eintraege seit einem konkreten Zeitpunkt (z.B. heute 00:00 lokal).</summary>
    Task<int> GetCallCountSinceAsync(DateTime since, CancellationToken ct = default);

    /// <summary>Aeltester Eintrag im rollenden Window (zum Anzeigen wann das Window resettet).</summary>
    Task<DateTime?> GetOldestCallInWindowAsync(TimeSpan window, CancellationToken ct = default);

    /// <summary>Loescht api_call_log-Eintraege aelter als N Tage (Wartung beim App-Start).</summary>
    Task<int> PruneApiCallLogAsync(int olderThanDays = 7, CancellationToken ct = default);
}
