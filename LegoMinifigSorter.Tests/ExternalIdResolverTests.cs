using LegoMinifigSorter.Core.Models;
using LegoMinifigSorter.Core.Services;

namespace LegoMinifigSorter.Tests;

/// <summary>
/// Tests fuer den ExternalIdResolver mit verschiedenen URL-Patterns.
/// Patterns gemaess BRICKOGNIZE_API.md.
/// </summary>
public class ExternalIdResolverTests
{
    private readonly ExternalIdResolver _resolver = new();

    [Fact]
    public void Resolves_minifig_from_bricklink_and_rebrickable_urls()
    {
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "BrickLink",
                Url = "https://www.bricklink.com/v2/catalog/catalogitem.page?M=sw0001" },
            new BrickognizeExternalSite { Name = "Rebrickable",
                Url = "https://rebrickable.com/minifigs/fig-001549/stormtrooper/" }
        };

        var result = _resolver.Resolve(sites, BrickognizeItemType.Minifig);

        Assert.Equal("sw0001", result.BricklinkId);
        Assert.Equal("fig-001549", result.RebrickableId);
        Assert.Null(result.BrickOwlId);
    }

    [Fact]
    public void Resolves_part_from_bricklink_and_rebrickable_urls()
    {
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "BrickLink",
                Url = "https://www.bricklink.com/v2/catalog/catalogitem.page?P=3001" },
            new BrickognizeExternalSite { Name = "Rebrickable",
                Url = "https://rebrickable.com/parts/3001/brick-2-x-4/" }
        };

        var result = _resolver.Resolve(sites, BrickognizeItemType.Part);

        Assert.Equal("3001", result.BricklinkId);
        Assert.Equal("3001", result.RebrickableId);
    }

    [Fact]
    public void Resolves_set_from_bricklink_and_rebrickable_urls()
    {
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "BrickLink",
                Url = "https://www.bricklink.com/v2/catalog/catalogitem.page?S=75300-1" },
            new BrickognizeExternalSite { Name = "Rebrickable",
                Url = "https://rebrickable.com/sets/75300-1/imperial-tie-fighter/" }
        };

        var result = _resolver.Resolve(sites, BrickognizeItemType.Set);

        Assert.Equal("75300-1", result.BricklinkId);
        Assert.Equal("75300-1", result.RebrickableId);
    }

    [Fact]
    public void Resolves_brickowl_from_catalog_path()
    {
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "BrickOwl",
                Url = "https://www.brickowl.com/catalog/lego-stormtrooper-sw0001-12345" }
        };

        var result = _resolver.Resolve(sites, BrickognizeItemType.Minifig);

        Assert.NotNull(result.BrickOwlId);
        Assert.StartsWith("lego-stormtrooper", result.BrickOwlId);
    }

    [Fact]
    public void Returns_null_for_empty_sites()
    {
        var result = _resolver.Resolve(Array.Empty<BrickognizeExternalSite>(), BrickognizeItemType.Unknown);

        Assert.Null(result.BricklinkId);
        Assert.Null(result.RebrickableId);
        Assert.Null(result.BrickOwlId);
    }

    [Fact]
    public void Ignores_invalid_urls()
    {
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "BrickLink", Url = "not-a-valid-url" },
            new BrickognizeExternalSite { Name = "Rebrickable", Url = "" }
        };

        var result = _resolver.Resolve(sites, BrickognizeItemType.Part);

        // Beide ungueltig -> alle Felder null, aber kein Crash
        Assert.Null(result.BricklinkId);
        Assert.Null(result.RebrickableId);
    }

    [Fact]
    public void Bricklink_url_without_known_query_returns_null()
    {
        // BrickLink-Domain, aber ohne ?P/?M/?S → Warning, null
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "BrickLink",
                Url = "https://www.bricklink.com/v2/catalog/catalogitem.page" }
        };

        var result = _resolver.Resolve(sites, BrickognizeItemType.Part);
        Assert.Null(result.BricklinkId);
    }

    [Fact]
    public void Resolver_picks_correct_query_param_per_type()
    {
        // Wenn z.B. eine BrickLink-URL sowohl ?P als auch ?M enthielte (sollte nicht
        // vorkommen, aber zur Sicherheit), sollte je nach Item-Typ die richtige
        // Praeferenz greifen.
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "BrickLink",
                Url = "https://www.bricklink.com/v2/catalog/catalogitem.page?P=3001&M=sw0001" }
        };

        var partResult = _resolver.Resolve(sites, BrickognizeItemType.Part);
        Assert.Equal("3001", partResult.BricklinkId);

        var figResult = _resolver.Resolve(sites, BrickognizeItemType.Minifig);
        Assert.Equal("sw0001", figResult.BricklinkId);
    }

    [Fact]
    public void Ignores_unknown_host_with_warning_only()
    {
        // Unbekannter Host -> kein Crash, einfach kein Eintrag
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "Unknown",
                Url = "https://example.com/foo/bar" }
        };

        var result = _resolver.Resolve(sites, BrickognizeItemType.Part);
        Assert.Null(result.BricklinkId);
        Assert.Null(result.RebrickableId);
        Assert.Null(result.BrickOwlId);
    }

    [Fact]
    public void Real_response_lowercase_name_is_handled_via_host()
    {
        // Validiert am 2026-04-30: echte Brickognize-Response hat name="bricklink"
        // (lowercase) und nur eine einzige external_site. Resolver entscheidet
        // ueber Host, nicht ueber Name -> muss trotzdem klappen.
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "bricklink",
                Url = "https://www.bricklink.com/v2/catalog/catalogitem.page?M=cty0969" }
        };

        var result = _resolver.Resolve(sites, BrickognizeItemType.Minifig);

        Assert.Equal("cty0969", result.BricklinkId);
        // Rebrickable-ID bleibt null fuer Minifiguren (kein Mapping moeglich)
        Assert.Null(result.RebrickableId);
    }

    [Fact]
    public void Part_uses_bricklink_id_as_rebrickable_fallback()
    {
        // Validiert am 2026-04-30: Brickognize liefert in der Praxis nur
        // BrickLink-Sites. Fuer Teile sind BL-Part-ID und Rebrickable-part_num
        // praktisch identisch (z.B. "3001"), daher als Fallback uebernehmen.
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "bricklink",
                Url = "https://www.bricklink.com/v2/catalog/catalogitem.page?P=3001" }
        };

        var result = _resolver.Resolve(sites, BrickognizeItemType.Part);

        Assert.Equal("3001", result.BricklinkId);
        Assert.Equal("3001", result.RebrickableId); // Fallback aktiv
    }

    [Fact]
    public void Minifig_does_not_use_bricklink_id_as_rebrickable_fallback()
    {
        // Negativ-Test: bei Minifiguren darf der Fallback NICHT greifen,
        // weil BL "cty0969" != Rebrickable "fig-XXXXXX".
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "bricklink",
                Url = "https://www.bricklink.com/v2/catalog/catalogitem.page?M=cty0969" }
        };

        var result = _resolver.Resolve(sites, BrickognizeItemType.Minifig);

        Assert.Equal("cty0969", result.BricklinkId);
        Assert.Null(result.RebrickableId);
    }

    [Fact]
    public void Explicit_rebrickable_url_takes_precedence_over_part_fallback()
    {
        // Wenn Brickognize doch mal eine Rebrickable-URL liefert, soll sie
        // gewinnen (sollte normal nicht passieren, aber Belt&Suspenders).
        var sites = new[]
        {
            new BrickognizeExternalSite { Name = "bricklink",
                Url = "https://www.bricklink.com/v2/catalog/catalogitem.page?P=3001" },
            new BrickognizeExternalSite { Name = "rebrickable",
                Url = "https://rebrickable.com/parts/3001-different/" }
        };

        var result = _resolver.Resolve(sites, BrickognizeItemType.Part);
        Assert.Equal("3001-different", result.RebrickableId);
    }
}
