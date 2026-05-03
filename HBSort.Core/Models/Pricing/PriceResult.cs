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

    /// <summary>True wenn ueberhaupt Daten vorhanden sind (mind. ein Preis-Wert != null).</summary>
    public bool HasAnyPrice =>
        MinPrice.HasValue || AvgPrice.HasValue || QtyAvgPrice.HasValue || MaxPrice.HasValue;
}
