using HBSort.Core.Models.Pricing;

namespace HBSort.Core.Services;

/// <summary>
/// Phase 8: berechnet die Verkaufsempfehlung fuer eine TrackedMinifig.
/// Holt parallel den Minifig-Preis und alle Required-Part-Preise vom
/// aktiven Provider, wendet die Korrekturfaktoren an und leitet die
/// SalesAdvice ab.
/// </summary>
public interface IPriceCalculationService
{
    /// <summary>
    /// Liefert die SalesRecommendation fuer die Figur. Wenn der aktive Provider
    /// "None" ist oder nicht konfiguriert: SalesAdvice=NoData (UI versteckt
    /// dann den Block).
    /// </summary>
    Task<SalesRecommendation> CalculateForMinifigAsync(int trackedMinifigId, CancellationToken ct = default);
}
