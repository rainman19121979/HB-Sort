namespace HBSort.Core.Models.Pricing;

/// <summary>
/// Empfehlung beim Verkauf: lohnt sich der Verkauf als Komplett-Figur
/// mehr als die Einzelteile?
/// </summary>
public enum SalesAdvice
{
    /// <summary>Keine Preise verfuegbar (Provider=None oder Lookup fehlgeschlagen).</summary>
    NoData,
    /// <summary>Komplett-Figur bringt mehr.</summary>
    CompleteWorthIt,
    /// <summary>Einzelteile bringen mehr.</summary>
    PartsWorthIt,
    /// <summary>Beide Werte liegen +/- 10% nah beieinander.</summary>
    Equal
}

/// <summary>
/// Phase 8: Aggregiertes Ergebnis des PriceCalculationService fuer eine Figur.
/// Enthaelt Roh-Preise + korrigierte Werte + Empfehlung.
/// </summary>
public record SalesRecommendation
{
    /// <summary>Provider-Info-Zeile fuer die UI ("Sold-Avg, Used, EUR-DE").</summary>
    public string ProviderInfo { get; init; } = string.Empty;

    /// <summary>Lookup-Ergebnis fuer die Komplett-Figur (BL).</summary>
    public PriceResult? MinifigPrice { get; init; }

    /// <summary>Aufgeschlagene Korrektur fuer Minifig (z.B. -10% = 0.90).</summary>
    public decimal CorrectedMinifigPrice { get; init; }

    /// <summary>Summe ueber alle Required-Parts (BL roh).</summary>
    public decimal? PartsRawSum { get; init; }

    /// <summary>Aufgeschlagene Korrektur fuer Parts (z.B. -15% = 0.85).</summary>
    public decimal CorrectedPartsSum { get; init; }

    /// <summary>Anzahl Required-Parts fuer die ein Preis gefunden wurde.</summary>
    public int PartsWithPriceCount { get; init; }

    /// <summary>Anzahl Required-Parts ohne Preis (UI-Hinweis "X Teile ohne Preis").</summary>
    public int PartsMissingPriceCount { get; init; }

    public SalesAdvice Advice { get; init; } = SalesAdvice.NoData;

    /// <summary>Differenz CorrectedMinifigPrice - CorrectedPartsSum (positiv = Komplett lohnt).</summary>
    public decimal Difference { get; init; }

    public string Currency { get; init; } = "EUR";
    public DateTime CalculatedAt { get; init; } = DateTime.UtcNow;
}
