using HBSort.Core.Models.Pricing;

namespace HBSort.Core.Services;

/// <summary>
/// Phase 8 / UX#12: Schmaler Wrapper ueber IBlCacheRepository + IPriceProvider
/// der die Stale-While-Revalidate-Strategie kapselt.
///
/// Ablauf pro Lookup:
///   1) Cache pruefen (mit TTL aus Settings je nach itemType - Minifig vs Part).
///   2) Hit + frisch -> sofort zurueck (Source=Cache).
///   3) Hit + stale  -> Stale-Wert sofort liefern (Source=Stale) UND im
///                      Hintergrund Revalidation triggern (fire-and-forget).
///                      Beim naechsten DataChanged-Refresh sieht die UI den
///                      frischen Wert.
///   4) Miss         -> Provider live aufrufen. Bei Erfolg cachen + Live
///                      zurueckgeben. Bei Fehler: None mit ErrorMessage.
///
/// In-Flight-Schutz: ein Dictionary&lt;CacheKey, Task&gt; verhindert dass
/// derselbe Lookup zweimal parallel die API anfragt - auch wenn der User
/// den Refresh-Button doppelt klickt oder mehrere Subset-Teile dieselbe
/// PartNumber+ColorId haben.
/// </summary>
public interface IBlPriceCacheService
{
    /// <summary>
    /// Holt den Preis einer kompletten Minifig (color_id=0). Stale-While-
    /// Revalidate gegen die TTL aus AppSettings.Prices.BlPriceCacheTtlMinifigDays.
    /// </summary>
    Task<PriceLookupOutcome> GetMinifigPriceAsync(
        string blMinifigId, CancellationToken ct = default);

    /// <summary>
    /// Holt den Preis eines Einzelteils. TTL aus
    /// AppSettings.Prices.BlPriceCacheTtlPartDays.
    /// </summary>
    Task<PriceLookupOutcome> GetPartPriceAsync(
        string blPartNo, int blColorId, CancellationToken ct = default);

    /// <summary>
    /// Pro-Eintrag-Refresh: loescht den Cache-Eintrag fuer Komplett-Figur
    /// + alle uebergebenen Subset-Teile, sodass die naechsten Aufrufe von
    /// GetMinifigPriceAsync/GetPartPriceAsync live in die API gehen.
    /// Subset-Spezifikation: (PartNo, ColorId)-Paare. Convenience - ruft
    /// intern <see cref="DeleteMinifigPriceAsync"/> + <see cref="DeletePartPricesAsync"/>.
    /// </summary>
    Task DeleteForMinifigAsync(
        string blMinifigId,
        IReadOnlyList<(string PartNo, int ColorId)> subsetParts,
        CancellationToken ct = default);

    /// <summary>
    /// UX-Iteration X.10: nur die Komplett-Figur aus dem Cache loeschen.
    /// Wird vom Refresh-Icon der Komplett-Haelfte in MinifigPriceView genutzt.
    /// Aktiviert die GuideType/Region/Currency-Settings live aus
    /// AppSettings.Prices fuer den Loesch-Schluessel.
    /// </summary>
    Task DeleteMinifigPriceAsync(string blMinifigId, CancellationToken ct = default);

    /// <summary>
    /// UX-Iteration X.10: nur die Einzelteil-Eintraege aus dem Cache loeschen.
    /// Wird vom Refresh-Icon der Einzelteile-Haelfte in MinifigPriceView genutzt.
    /// </summary>
    Task DeletePartPricesAsync(
        IReadOnlyList<(string PartNo, int ColorId)> subsetParts,
        CancellationToken ct = default);

    /// <summary>Anzahl Eintraege im Preis-Cache (fuer Settings-Anzeige).</summary>
    Task<int> GetEntryCountAsync(CancellationToken ct = default);

    /// <summary>Loescht alle Cache-Eintraege.</summary>
    Task<int> ClearAllAsync(CancellationToken ct = default);
}
