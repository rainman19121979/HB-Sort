using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// Loest die Rebrickable-, BrickLink- und BrickOwl-IDs aus den external_sites-URLs
/// einer Brickognize-Response auf. Siehe BRICKOGNIZE_API.md fuer Patterns.
/// </summary>
public interface IExternalIdResolver
{
    /// <summary>
    /// Parst die uebergebenen Sites und liefert die extrahierten IDs.
    /// Felder im Result koennen null sein wenn die jeweilige Site fehlt
    /// oder die URL nicht zu einem bekannten Pattern passt.
    /// </summary>
    /// <param name="sites">Liste aus BrickognizeItem.ExternalSites.</param>
    /// <param name="type">Item-Typ (Part / Minifig / Set) – beeinflusst nur Loggen.</param>
    ExternalIds Resolve(IEnumerable<BrickognizeExternalSite> sites, BrickognizeItemType type);
}
