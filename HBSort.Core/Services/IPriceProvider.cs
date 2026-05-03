using HBSort.Core.Models.Pricing;

namespace HBSort.Core.Services;

/// <summary>
/// Phase 8: Abstraktion ueber Preis-Quellen (BL-API, spaeter HB-Price-Tool).
/// Implementierungen sollen Cache + Rate-Limit selbst handhaben - die
/// PriceCalculationService-Schicht ruft nur die eine Methode auf.
/// </summary>
public interface IPriceProvider
{
    /// <summary>Anzeige-Name fuer die UI ("BrickLink", "HB-Price-Tool", "Keine Preise").</summary>
    string Name { get; }

    /// <summary>True wenn der Provider gerade nutzbar ist (Tokens, URL etc. da).</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    /// <summary>Holt einen Preis-Datensatz fuer eine Minifig (color_id=0).</summary>
    Task<PriceResult?> GetMinifigPriceAsync(string blMinifigId, CancellationToken ct = default);

    /// <summary>
    /// Holt einen Preis-Datensatz fuer ein Teil. <paramref name="blColorId"/>
    /// muss die BL-Color-ID sein (Brickognize liefert die direkt).
    /// </summary>
    Task<PriceResult?> GetPartPriceAsync(string blPartNo, int blColorId, CancellationToken ct = default);
}
