using HBSort.Core.Models.Pricing;

namespace HBSort.Core.Services;

/// <summary>
/// Gemeinsame Preis-Helper. Wird von <see cref="PriceCalculationService"/>
/// (Komplett-Workflow nach Komplettierung) und vom WPF-seitigen
/// MinifigPriceViewModel (Live-Anzeige im Sortier-Tab) genutzt - so kann
/// die Logik nicht zwischen den beiden Pfaden auseinanderlaufen.
///
/// Bugfix Phase-8 #3 (2026-05-04): vorher waren PickValue und ApplyCorrection
/// nur in PriceCalculationService und der Live-Pfad hat die User-Settings
/// (PriceColumn, CorrectionMinifigPercent, CorrectionPartsPercent) komplett
/// ignoriert. Durch das Extrahieren ist die Logik testbar und garantiert
/// identisch.
/// </summary>
public static class PriceMath
{
    /// <summary>
    /// Liest die vom User gewaehlte Preis-Spalte (PriceColumn-Setting) aus
    /// einem PriceResult. Default fuer "qty_avg" ist QtyAvgPrice mit
    /// Fallback auf AvgPrice (wenn das Item zu wenige Listings hat um einen
    /// Quantity-Avg zu berechnen).
    ///
    /// priceColumn: "min" / "avg" / "max" / "qty_avg" (alles andere -&gt; qty_avg).
    /// </summary>
    public static decimal? PickValue(PriceResult? p, string? priceColumn)
    {
        if (p == null) return null;
        return priceColumn switch
        {
            "min" => p.MinPrice,
            "avg" => p.AvgPrice,
            "max" => p.MaxPrice,
            _     => p.QtyAvgPrice ?? p.AvgPrice
        };
    }

    /// <summary>
    /// Wendet einen Prozent-Korrekturfaktor auf einen Roh-Wert an.
    /// -10 = 10% Abschlag, +5 = 5% Aufschlag, 0 = unveraendert. Null bleibt null.
    /// Rundet auf 4 Nachkommastellen kaufmaennisch (AwayFromZero) passend zur
    /// BL-Preis-Praezision - BL liefert Preise mit 4 Nachkommastellen, und
    /// kleine Standardteile (z.B. 0.0234 EUR) wuerden bei 2-Stellen-Rundung
    /// ihre Aussagekraft verlieren.
    /// </summary>
    public static decimal? ApplyCorrection(decimal? raw, decimal correctionPercent)
    {
        if (!raw.HasValue) return null;
        var factor = 1m + correctionPercent / 100m;
        return Math.Round(raw.Value * factor, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Wie <see cref="ApplyCorrection(decimal?, decimal)"/>, aber mit
    /// Default 0 statt null fuer NULL-Inputs - bequemer fuer Aufrufer
    /// die direkt eine Summe weiterrechnen wollen.
    /// </summary>
    public static decimal ApplyCorrectionOrZero(decimal raw, decimal correctionPercent)
        => ApplyCorrection(raw, correctionPercent) ?? 0m;
}
