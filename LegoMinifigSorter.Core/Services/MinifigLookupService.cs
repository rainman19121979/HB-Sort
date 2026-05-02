using System.Text.RegularExpressions;
using LegoMinifigSorter.Core.Models;
using Serilog;

namespace LegoMinifigSorter.Core.Services;

/// <summary>
/// Default-Implementierung des Minifig-Lookups. Setzt den ICatalogService und den
/// IExternalIdResolver zusammen und macht die Fallback-Kette transparent fuers UI.
/// </summary>
public partial class MinifigLookupService : IMinifigLookupService
{
    /// <summary>fig_num-Pattern: "fig-" + mind. eine Ziffer.</summary>
    [GeneratedRegex(@"^fig-\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex FigNumRegex();

    /// <summary>Maximale Anzahl Kandidaten bei der Name-Suche.</summary>
    private const int NameSearchLimit = 5;

    private readonly ICatalogService _catalog;
    private readonly IExternalIdResolver _idResolver;

    public MinifigLookupService(ICatalogService catalog, IExternalIdResolver idResolver)
    {
        _catalog = catalog;
        _idResolver = idResolver;
    }

    public async Task<MinifigLookupResult> LookupAsync(BrickognizeItem item, CancellationToken ct = default)
    {
        // === Schritt 1: Rebrickable-URL aus external_sites parsen ===
        var ids = _idResolver.Resolve(item.ExternalSites, BrickognizeItemType.Minifig);
        if (!string.IsNullOrEmpty(ids.RebrickableId))
        {
            var minifig = await _catalog.GetMinifigAsync(ids.RebrickableId, ct);
            if (minifig != null)
            {
                Log.Information("MinifigLookup: gefunden via Rebrickable-URL fig_num={FigNum}", minifig.FigNum);
                return await BuildResultAsync(minifig, MinifigResolutionMethod.RebrickableUrl, ct);
            }
            Log.Debug("MinifigLookup: Rebrickable-fig_num {FigNum} nicht in catalog.db", ids.RebrickableId);
        }

        // === Schritt 2: Brickognize-id pruefen ob "fig-XXXXXX" ===
        if (!string.IsNullOrEmpty(item.Id) && FigNumRegex().IsMatch(item.Id))
        {
            var minifig = await _catalog.GetMinifigAsync(item.Id, ct);
            if (minifig != null)
            {
                Log.Information("MinifigLookup: gefunden via Brickognize.id als fig_num={FigNum}", minifig.FigNum);
                return await BuildResultAsync(minifig, MinifigResolutionMethod.BrickognizeIdAsFigNum, ct);
            }
        }

        // === Schritt 3: Name-Suche ===
        // Brickognize liefert oft sehr lange beschreibende Namen wie
        // "Construction Worker - Female, Orange Safety Vest, ...".
        // Wir nehmen den vollen Namen fuer LIKE %name% – Catalog-Eintraege
        // tragen aehnliche Namen.
        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            // Erst LIKE %fullName% – gibts oft genau einen Treffer.
            var candidates = await _catalog.SearchMinifigsByNameAsync(item.Name, NameSearchLimit, ct);

            // Falls nichts gefunden: ersten Teil des Namens vor dem ersten Komma probieren.
            if (candidates.Count == 0)
            {
                var firstPart = item.Name.Split(',')[0].Trim();
                if (!string.IsNullOrWhiteSpace(firstPart) && firstPart != item.Name)
                {
                    candidates = await _catalog.SearchMinifigsByNameAsync(firstPart, NameSearchLimit, ct);
                }
            }

            if (candidates.Count == 1)
            {
                Log.Information("MinifigLookup: eindeutiger Name-Match fig_num={FigNum} fuer '{Name}'",
                    candidates[0].FigNum, item.Name);
                return await BuildResultAsync(candidates[0], MinifigResolutionMethod.UniqueNameMatch, ct);
            }

            if (candidates.Count > 1)
            {
                Log.Information("MinifigLookup: {Count} Name-Matches fuer '{Name}', nehme den ersten ({FigNum})",
                    candidates.Count, item.Name, candidates[0].FigNum);
                var result = await BuildResultAsync(candidates[0], MinifigResolutionMethod.AmbiguousNameMatch, ct);
                return new MinifigLookupResult
                {
                    Method = result.Method,
                    Minifig = result.Minifig,
                    Parts = result.Parts,
                    AmbiguousCandidates = candidates // alle Kandidaten zur Anzeige
                };
            }
        }

        Log.Information("MinifigLookup: keine Catalog-Aufloesung fuer Brickognize-Item id={Id} name='{Name}'",
            item.Id, item.Name);
        return new MinifigLookupResult { Method = MinifigResolutionMethod.NotFound };
    }

    private async Task<MinifigLookupResult> BuildResultAsync(
        CatalogMinifig minifig, MinifigResolutionMethod method, CancellationToken ct)
    {
        var parts = await _catalog.GetMinifigPartsAsync(minifig.FigNum, ct);
        return new MinifigLookupResult
        {
            Method = method,
            Minifig = minifig,
            Parts = parts
        };
    }
}
