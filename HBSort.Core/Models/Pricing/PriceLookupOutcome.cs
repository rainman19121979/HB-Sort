namespace HBSort.Core.Models.Pricing;

/// <summary>
/// Quelle des Preis-Werts in einem Lookup-Outcome.
///   Cache - frisch aus dem Cache (innerhalb TTL).
///   Stale - aus dem Cache, aber TTL ueberschritten. Aufrufer kann ggf.
///           ein Hintergrund-Revalidate getriggert haben.
///   Live  - direkt aus der BL-API geholt + Cache aktualisiert.
///   None  - kein Wert verfuegbar (Cache leer und API nicht erreichbar
///           bzw. nicht konfiguriert).
/// </summary>
public enum PriceLookupSource
{
    Cache,
    Stale,
    Live,
    None
}

/// <summary>
/// Ergebnis eines Cache-Service-Aufrufs (UX#12). Statt nur dem PriceResult
/// kommt zusaetzliche Meta-Info zurueck damit die UI passende Hinweise
/// anzeigen kann ("Daten vom DD.MM.", "Live-Update fehlgeschlagen", ...).
/// </summary>
public record PriceLookupOutcome(
    PriceResult? Price,
    PriceLookupSource Source,
    DateTime? FetchedAt,
    string? ErrorMessage)
{
    public bool HasPrice => Price != null;
    public bool IsCacheBased => Source is PriceLookupSource.Cache or PriceLookupSource.Stale;
}
