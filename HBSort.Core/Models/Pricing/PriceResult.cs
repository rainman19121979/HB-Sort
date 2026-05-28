namespace HBSort.Core.Models.Pricing;

/// <summary>
/// Phase 8: Ein Preis-Lookup-Ergebnis fuer ein Item (Minifig oder Teil).
/// Werte sind die Roh-Daten vom Provider; Korrekturfaktoren werden erst im
/// PriceCalculationService aufgeschlagen.
/// </summary>
public record PriceResult
{
    /// <summary>Provider-Name fuer die Anzeige ("BrickLink Sold-Avg", "BL Stock-Min").</summary>
    public string ProviderLabel { get; init; } = string.Empty;

    public decimal? MinPrice { get; init; }
    public decimal? AvgPrice { get; init; }
    /// <summary>Quantity-gewichteter Durchschnitt (BrickStore-Default).</summary>
    public decimal? QtyAvgPrice { get; init; }
    public decimal? MaxPrice { get; init; }

    /// <summary>Anzahl unterschiedlicher Listings/Stores.</summary>
    public int UnitQuantity { get; init; }
    /// <summary>Gesamtmenge ueber alle Listings.</summary>
    public int TotalQuantity { get; init; }

    public string Currency { get; init; } = "EUR";

    /// <summary>UTC-Zeitpunkt des Lookups.</summary>
    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// v0.1.24-beta.11: True wenn dieses Resultat aus dem Cache kam (kein
    /// frischer API-Call). Wird vom <see cref="HBSort.Core.Services.BricklinkApiPriceProvider"/>
    /// in den Cache-Pfaden auf true gesetzt; die Bulk-"Alle Preise holen"-
    /// Funktion im Bauvorschlags-Tab nutzt die Flag fuer das Toast-Summary
    /// (z.B. "5 geholt, 12 aus Cache, 1 fehlgeschlagen").
    /// </summary>
    public bool FromCache { get; init; }

    /// <summary>True wenn ueberhaupt Daten vorhanden sind (mind. ein Preis-Wert != null).</summary>
    public bool HasAnyPrice =>
        MinPrice.HasValue || AvgPrice.HasValue || QtyAvgPrice.HasValue || MaxPrice.HasValue;
}
