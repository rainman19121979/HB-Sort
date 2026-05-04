using HBSort.Core.Models.Bricklink;

namespace HBSort.Core.Services;

/// <summary>
/// Direkt-Zugriff auf bl_cache.db. Microsoft.Data.Sqlite, kein EF.
/// Keine Cache-Logik (wann refresh, wann fallback) - die liegt im BlCatalogService.
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
    /// Bulk-Insert von Subsets ohne vorheriges Loeschen pro Parent. Nutzt
    /// INSERT OR REPLACE auf den PRIMARY KEY. Genutzt vom BrickStore-Bulk-Import
    /// (Phase 5.5) fuer ~140.000 Eintraege in einer einzigen Transaction.
    /// </summary>
    Task<int> BulkInsertSubsetsAsync(IEnumerable<BlSubset> subsets, CancellationToken ct = default);

    /// <summary>
    /// Reverse-Lookup: welche Parent-Items enthalten dieses Item in dieser Farbe?
    /// (Phase 5 nutzt das fuer "wartende Figuren brauchen Teil X").
    /// </summary>
    Task<List<string>> FindParentsByItemAsync(string itemType, string itemNo, int colorId, CancellationToken ct = default);

    /// <summary>
    /// Reverse-Lookup mit Item-Daten: alle Minifig-Parents (parent_type='M') die ein
    /// Teil in einer Farbe enthalten, mit JOIN auf bl_items fuer Name + Image-URL.
    /// Sortierung nach Quantity (am haeufigsten zuerst), Limit 50.
    /// </summary>
    Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(
        string blPartNo, int blColorId, CancellationToken ct = default);

    /// <summary>
    /// Fuer BuildSuggestions: findet alle BL-Minifig-IDs (parent_type='M'),
    /// deren Subsets mindestens eines der uebergebenen (PartNo, ColorId)-Paare
    /// enthalten. Pseudo-Eintraege (is_from_supersets=1) werden ausgefiltert.
    /// Liefert die DISTINCT-Liste der Minifig-Parent-IDs - die Quantity-Berechnung
    /// macht der Aufrufer ueber GetSubsetsAsync.
    /// </summary>
    Task<List<string>> FindMinifigsContainingPartsAsync(
        IReadOnlyList<(string PartNo, int ColorId)> parts,
        CancellationToken ct = default);

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

    // --- Phase 8: Preis-Cache ---

    /// <summary>
    /// Holt einen gecachten Preis-Eintrag. Liefert null wenn keiner existiert ODER
    /// wenn der Eintrag aelter als <paramref name="staleDays"/> ist (Aufrufer holt
    /// dann neu vom Provider). Bei staleDays=0 wird auch ein stale Eintrag
    /// zurueckgegeben (Fallback-Pfad bei API-Fehler).
    /// </summary>
    Task<Models.Pricing.PriceResult?> GetCachedPriceAsync(
        string itemType, string itemNo, int colorId,
        string guideType, string newOrUsed,
        string region, string currency,
        int staleDays,
        CancellationToken ct = default);

    /// <summary>
    /// Speichert oder aktualisiert einen Preis-Eintrag (INSERT OR REPLACE auf
    /// dem PRIMARY KEY). FetchedAt wird auf jetzt gesetzt.
    /// </summary>
    Task UpsertPriceAsync(
        string itemType, string itemNo, int colorId,
        string guideType, string newOrUsed,
        string region, string currency,
        Models.Pricing.PriceResult price,
        CancellationToken ct = default);

    // ====================================================================
    // Phase 8 / UX#12 Stale-While-Revalidate - vier neue Operationen
    // ====================================================================

    /// <summary>
    /// Wie GetCachedPriceAsync, aber liefert IMMER den vorhandenen Eintrag
    /// mit einem IsStale-Flag (FetchedAt + ttlDays &lt; now). Liefert null
    /// nur wenn kein Eintrag in der DB ist. Fuer Stale-While-Revalidate.
    /// </summary>
    Task<Models.Pricing.CachedPriceLookup?> GetCachedPriceWithStaleFlagAsync(
        string itemType, string itemNo, int colorId,
        string guideType, string newOrUsed,
        string region, string currency,
        int ttlDays,
        CancellationToken ct = default);

    /// <summary>
    /// Loescht GENAU diesen Cache-Eintrag (PRIMARY-KEY-Match). Liefert true
    /// wenn ein Eintrag entfernt wurde. Fuer Pro-Eintrag-Refresh ueber das
    /// ↻-Icon in der UI.
    /// </summary>
    Task<bool> DeletePriceAsync(
        string itemType, string itemNo, int colorId,
        string guideType, string newOrUsed,
        string region, string currency,
        CancellationToken ct = default);

    /// <summary>Loescht ALLE Eintraege in bl_prices. Fuer "Cache leeren"-Button.</summary>
    Task<int> ClearAllPricesAsync(CancellationToken ct = default);

    /// <summary>Anzahl Eintraege in bl_prices. Fuer Settings-Anzeige.</summary>
    Task<int> GetPriceCacheCountAsync(CancellationToken ct = default);
}
