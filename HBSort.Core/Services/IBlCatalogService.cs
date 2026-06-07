using HBSort.Core.Models.Bricklink;

namespace HBSort.Core.Services;

/// <summary>
/// Cache-First-Lookup gegen die BL-API. Zentraler Einstiegspunkt fuer
/// Catalog-Daten ueberall in der App - ScanViewModel und Co. nutzen NUR
/// diesen Service, nie direkt BricklinkClient oder BlCacheRepository.
///
/// Strategie:
///   1. Cache-Hit (Eintrag vorhanden + nicht stale): direkt zurueckgeben
///   2. Cache-Miss / Stale: BL-API + Cache aktualisieren
///   3. BL-Fehler: stale Cache-Eintrag zurueckgeben + Toast (caller-seitig)
///
/// Stale-Days kommen aus AppSettings.Bricklink.CacheStaleDays.
/// </summary>
public interface IBlCatalogService
{
    /// <summary>
    /// Holt Item-Details (Name, Image, Year, Weight, ...).
    /// Liefert null wenn das Item in BL nicht existiert (HTTP 404).
    /// Wirft <see cref="Models.Exceptions.BricklinkAuthException"/> bei 401/403 -
    /// der Aufrufer (UI) muss den User auf die Settings hinweisen.
    /// </summary>
    Task<BlItem?> GetMinifigDetailsAsync(string blMinifigId, CancellationToken ct = default);

    /// <summary>
    /// Holt die Teileliste (Subsets) der Minifigur. Cacht die enthaltenen Teile
    /// "uebergreifend" auch in bl_items mit DataCompleteness=Subset, damit
    /// spaetere Einzelteil-Scans direkt aus dem Cache bedient werden koennen.
    /// </summary>
    Task<List<BlSubset>> GetMinifigPartsAsync(string blMinifigId, CancellationToken ct = default);

    /// <summary>Holt Teil-Details (full oder subset reicht in Phase R2/R3).</summary>
    Task<BlItem?> GetPartDetailsAsync(string blPartNo, CancellationToken ct = default);

    /// <summary>
    /// Holt die komplette BL-Color-Liste. Beim ersten Mal: API-Call, dann lebenslang Cache.
    /// </summary>
    Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default);

    /// <summary>
    /// Schnitt zwischen "wartenden Minifig-IDs" und "Parents die dieses Teil in der Farbe enthalten".
    /// Komplett aus dem Cache (kein BL-Call).
    /// </summary>
    Task<List<string>> FindWaitingMinifigsForPartAsync(
        string blPartNo, int blColorId, IEnumerable<string> waitingMinifigIds, CancellationToken ct = default);

    /// <summary>Cache-Statistik fuer die Settings-UI.</summary>
    Task<BlCacheStats> GetCacheStatsAsync(CancellationToken ct = default);

    /// <summary>Cache komplett leeren (mit Bestaetigung im UI).</summary>
    Task ClearCacheAsync(CancellationToken ct = default);

    /// <summary>Stale-Eintraege loeschen (>cacheStaleDays Tage alt).</summary>
    Task<int> ClearStaleAsync(CancellationToken ct = default);

    // ========================================================================
    // Phase 5: Reverse-Lookup + KnownColors
    // ========================================================================

    /// <summary>
    /// "Wo kommt dieses Teil in dieser Farbe vor?" -> Liste der Minifig-Eintraege
    /// (parent_type='M'). Nutzt Cache (bl_subsets), faellt auf BL-Call (Supersets) zurueck.
    /// Bei BL-Fehler / Rate-Limit: Cache-Daten zurueck, sonst leer.
    /// Bei Erfolg: Cache (bl_subsets + bl_items als 'subset') wird mit den neuen Eintraegen befuellt.
    /// </summary>
    Task<List<BlSubset>> GetSupersetsAsync(string blPartNo, int blColorId, CancellationToken ct = default);

    /// <summary>
    /// Heuristik: Liefert true, wenn die gecachten Subsets fuer einen Parent
    /// "wahrscheinlich vollstaendig" sind (mehrere Eintraege + nicht stale).
    /// Sonst wird GetMinifigPartsAsync getriggert (1 BL-Call).
    /// </summary>
    Task<bool> EnsureFullSubsetsAsync(string blMinifigId, CancellationToken ct = default);

    /// <summary>
    /// Liefert die bekannten Farben fuer ein Teil (BL "GetKnownColors").
    /// Nutzt bl_known_colors als Cache; bei Miss BL-Call. Liefert die zugehoerigen
    /// BlColor-Objekte aus bl_colors (Name + RGB) - nicht nur die IDs.
    /// </summary>
    Task<List<BlColor>> GetKnownColorsAsync(string blPartNo, CancellationToken ct = default);

    /// <summary>
    /// Reverse-Lookup im bl_subsets-Cache: welche Minifigs verwenden dieses Teil
    /// in dieser Farbe? Reine Cache-Abfrage - kein BL-Call. Daten kommen aus den
    /// Subsets die schon mal via GetMinifigPartsAsync oder GetSupersetsAsync
    /// gecached wurden.
    /// </summary>
    Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(
        string blPartNo, int blColorId, CancellationToken ct = default);

    /// <summary>
    /// Liefert die Komponenten-Teile eines "Combined Part" (z.B. montierter Torso →
    /// bare Torso + Arme + Haende). REINER Cache-Lookup auf bl_subsets, KEIN BL-Call.
    /// Leere Liste wenn das Teil atomar ist (keine Subsets).
    ///
    /// Zwei Pfade (v0.1.25):
    ///   1. Direkt <c>GetSubsetsAsync("P", blPartNo)</c>.
    ///   2. Falls leer: Reverse-Fallback ueber <c>FindParentsByItemAsync</c> auf den
    ///      complete-Parent (cXX) und dessen Subsets - falls Brickognize beim Scan
    ///      die bare-ID statt der complete-ID liefert.
    ///
    /// Filter: <c>is_alternate=0 AND is_counterpart=0 AND is_from_supersets=0</c>.
    /// Namen/Farben werden aus dem lokalen Cache angereichert.
    /// </summary>
    Task<List<PartComponent>> GetPartComponentsAsync(
        string blPartNo, int blColorId, CancellationToken ct = default);
}
