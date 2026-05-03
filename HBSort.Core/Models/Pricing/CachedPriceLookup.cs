namespace HBSort.Core.Models.Pricing;

/// <summary>
/// Cache-Lookup-Ergebnis das Stale-While-Revalidate erlaubt.
/// Im Gegensatz zu GetCachedPriceAsync(staleDays) liefert dieser Wrapper IMMER
/// den vorhandenen Eintrag, dazu ein IsStale-Flag (FetchedAt + TTL &lt; now).
/// Aufrufer entscheidet was er mit dem Stale-Wert macht.
/// </summary>
public record CachedPriceLookup(
    PriceResult Price,
    bool IsStale);
