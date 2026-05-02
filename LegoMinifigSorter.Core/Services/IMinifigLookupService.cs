using LegoMinifigSorter.Core.Models;

namespace LegoMinifigSorter.Core.Services;

/// <summary>
/// Verkettet einen Brickognize-Minifig-Treffer mit den Stammdaten in der catalog.db.
/// Zentraler Einstiegspunkt fuer den Modus-A-Workflow (Phase 3).
/// </summary>
public interface IMinifigLookupService
{
    /// <summary>
    /// Versucht eine Minifigur und ihre Teileliste anhand eines Brickognize-Treffers
    /// aus der catalog.db zu laden.
    ///
    /// Aufloesungs-Strategie (in dieser Reihenfolge):
    ///   1. Rebrickable-fig_num aus external_sites (per ExternalIdResolver)
    ///   2. Brickognize-Item-`id` falls sie wie "fig-XXXXXX" aussieht
    ///   3. Catalog-Volltextsuche per Name (Brickognize-Item-Name vs minifigs.name)
    /// </summary>
    /// <returns>
    /// Result mit Minifig + Parts + Resolution-Status. Bei "NotFound" sind die Felder leer.
    /// </returns>
    Task<MinifigLookupResult> LookupAsync(BrickognizeItem item, CancellationToken ct = default);
}

/// <summary>
/// Resultat einer Minifig-Aufloesung.
/// </summary>
public class MinifigLookupResult
{
    /// <summary>Wie wir auf den Catalog-Eintrag gekommen sind.</summary>
    public MinifigResolutionMethod Method { get; init; }

    /// <summary>Minifig-Stammdaten (null bei NotFound).</summary>
    public CatalogMinifig? Minifig { get; init; }

    /// <summary>Teileliste (leer bei NotFound oder wenn Catalog leer).</summary>
    public List<CatalogMinifigPart> Parts { get; init; } = new();

    /// <summary>
    /// Kandidaten-Liste bei mehrdeutiger Auswahl (z.B. Name-Suche mit mehreren Treffern).
    /// Aktuell waehlen wir bei Mehrdeutigkeit den ersten Treffer und melden die anderen
    /// hier mit – der UI kann dann optional einen Auswahl-Dialog anbieten.
    /// </summary>
    public List<CatalogMinifig> AmbiguousCandidates { get; init; } = new();
}

public enum MinifigResolutionMethod
{
    /// <summary>Nichts gefunden.</summary>
    NotFound,
    /// <summary>Rebrickable-URL aus external_sites geliefert die fig_num.</summary>
    RebrickableUrl,
    /// <summary>Brickognize-Item-`id` sah wie "fig-XXXXXX" aus.</summary>
    BrickognizeIdAsFigNum,
    /// <summary>Eindeutiger Name-Match in catalog.db.</summary>
    UniqueNameMatch,
    /// <summary>Mehrere Name-Matches – wir haben den ersten genommen.</summary>
    AmbiguousNameMatch
}
