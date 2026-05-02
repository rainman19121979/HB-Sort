using System.Text.RegularExpressions;
using System.Web;
using LegoMinifigSorter.Core.Models;
using Serilog;

namespace LegoMinifigSorter.Core.Services;

/// <summary>
/// Default-Implementierung des Resolvers. Erkennt URLs von BrickLink, Rebrickable
/// und BrickOwl per Regex und Query-String-Parsing.
///
/// Patterns siehe BRICKOGNIZE_API.md. Wenn eine URL nicht erkannt wird, geben wir
/// eine Warnung ins Log – so koennen wir fehlende Patterns spaeter ergaenzen.
/// </summary>
public partial class ExternalIdResolver : IExternalIdResolver
{
    // Rebrickable-Pfade nach dem Domain-Teil:
    //   /minifigs/fig-001549/...   -> fig_num "fig-001549"
    //   /parts/3001/...            -> part_num "3001"
    //   /sets/75300-1/...          -> set_num "75300-1"
    [GeneratedRegex(@"^/(minifigs|parts|sets)/([^/?#]+)/?", RegexOptions.IgnoreCase)]
    private static partial Regex RebrickablePathRegex();

    // BrickOwl-Catalog-URLs typischerweise: /catalog/{boid} oder /catalog/{boid}-{slug}
    [GeneratedRegex(@"^/catalog/([0-9A-Za-z\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BrickOwlPathRegex();

    public ExternalIds Resolve(IEnumerable<BrickognizeExternalSite> sites, BrickognizeItemType type)
    {
        string? rebrickableId = null;
        string? bricklinkId = null;
        string? brickOwlId = null;

        foreach (var site in sites)
        {
            if (string.IsNullOrWhiteSpace(site.Url)) continue;

            // Wir parsen die URL einmal sauber. Fehler hier sollten nicht den Scan abbrechen.
            if (!Uri.TryCreate(site.Url, UriKind.Absolute, out var uri))
            {
                Log.Warning("ExternalIdResolver: Ungueltige URL '{Url}' bei Site '{Name}'", site.Url, site.Name);
                continue;
            }

            // Site-Name ist nicht unbedingt zuverlaessig (lowercase in echten Responses) –
            // wir entscheiden anhand der Domain.
            var host = uri.Host.ToLowerInvariant();

            if (host.Contains("bricklink.com"))
            {
                bricklinkId = ParseBrickLinkId(uri, type) ?? bricklinkId;
            }
            else if (host.Contains("rebrickable.com"))
            {
                rebrickableId = ParseRebrickableId(uri) ?? rebrickableId;
            }
            else if (host.Contains("brickowl.com"))
            {
                brickOwlId = ParseBrickOwlId(uri) ?? brickOwlId;
            }
            else
            {
                Log.Warning("ExternalIdResolver: Unbekannter Host '{Host}' (Site '{Name}', URL '{Url}')",
                    host, site.Name, site.Url);
            }
        }

        // Fallback fuer Teile (validiert am 2026-04-30): die BrickLink-Part-ID
        // ist in den allermeisten Faellen identisch mit dem Rebrickable-part_num
        // (z.B. "3001" = "3001"). Brickognize liefert in echten Responses NUR die
        // BrickLink-Site, nie eine Rebrickable-Site. Wir kopieren daher die BL-ID
        // als plausiblen Default, damit der Catalog-Lookup ueberhaupt eine Chance hat.
        // Fuer Minifiguren ist das NICHT moeglich (BL "cty0969" != fig_num "fig-XXXXXX") –
        // dort muss Phase 3 einen anderen Weg finden (Name-Matching im Catalog).
        if (rebrickableId == null && bricklinkId != null && type == BrickognizeItemType.Part)
        {
            rebrickableId = bricklinkId;
        }

        return new ExternalIds(rebrickableId, bricklinkId, brickOwlId);
    }

    /// <summary>
    /// BrickLink-URLs haben die ID im Query-String:
    ///   ?P=3001    Part
    ///   ?M=sw0001  Minifig
    ///   ?S=75300-1 Set
    /// Wir versuchen alle drei und nehmen den ersten Treffer.
    /// </summary>
    private static string? ParseBrickLinkId(Uri uri, BrickognizeItemType type)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);

        // Reihenfolge nach Item-Typ optimieren – aber alle pruefen.
        var keysInOrder = type switch
        {
            BrickognizeItemType.Minifig => new[] { "M", "P", "S" },
            BrickognizeItemType.Part    => new[] { "P", "M", "S" },
            BrickognizeItemType.Set     => new[] { "S", "P", "M" },
            _                            => new[] { "M", "P", "S" }
        };

        foreach (var key in keysInOrder)
        {
            // ParseQueryString ist case-sensitiv – BrickLink benutzt aber Grossbuchstaben.
            var v = query[key];
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }

        // Query-String hatte keinen unserer drei Keys -> unbekanntes Pattern
        Log.Warning("ExternalIdResolver: BrickLink-URL ohne ?P/?M/?S erkannt: {Url}", uri);
        return null;
    }

    /// <summary>Rebrickable-URLs: /minifigs/{fig}/, /parts/{p}/, /sets/{s}/.</summary>
    private static string? ParseRebrickableId(Uri uri)
    {
        var match = RebrickablePathRegex().Match(uri.AbsolutePath);
        if (match.Success)
        {
            return match.Groups[2].Value;
        }

        Log.Warning("ExternalIdResolver: Rebrickable-URL nicht erkannt: {Url}", uri);
        return null;
    }

    /// <summary>BrickOwl-URLs: /catalog/{boid}-{slug}/.</summary>
    private static string? ParseBrickOwlId(Uri uri)
    {
        var match = BrickOwlPathRegex().Match(uri.AbsolutePath);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        Log.Warning("ExternalIdResolver: BrickOwl-URL nicht erkannt: {Url}", uri);
        return null;
    }
}
