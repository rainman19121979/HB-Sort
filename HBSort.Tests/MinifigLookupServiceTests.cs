using HBSort.Core.Models;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer den MinifigLookupService mit einem Fake-CatalogService.
/// Pruefen die Aufloesungs-Reihenfolge:
///   1. Rebrickable-URL aus external_sites
///   2. Brickognize-id falls "fig-XXXXXX"
///   3. Name-Suche
/// </summary>
public class MinifigLookupServiceTests
{
    [Fact]
    public async Task Lookup_via_rebrickable_url_takes_precedence()
    {
        var item = new BrickognizeItem
        {
            Id = "cty0969",
            Name = "Construction Worker - Female",
            Type = "fig",
            ExternalSites = new List<BrickognizeExternalSite>
            {
                // BrickLink + Rebrickable beide vorhanden – der Resolver soll
                // primaer die Rebrickable-URL nutzen.
                new() { Name = "bricklink",
                        Url = "https://www.bricklink.com/v2/catalog/catalogitem.page?M=cty0969" },
                new() { Name = "rebrickable",
                        Url = "https://rebrickable.com/minifigs/fig-001234/" }
            }
        };

        var catalog = new FakeCatalog();
        catalog.Minifigs["fig-001234"] = new CatalogMinifig
        {
            FigNum = "fig-001234", Name = "Construction Worker - Female", NumParts = 4
        };

        var sut = new MinifigLookupService(catalog, new ExternalIdResolver());
        var result = await sut.LookupAsync(item);

        Assert.Equal(MinifigResolutionMethod.RebrickableUrl, result.Method);
        Assert.Equal("fig-001234", result.Minifig!.FigNum);
    }

    [Fact]
    public async Task Lookup_falls_back_to_brickognize_id_when_it_looks_like_fig_num()
    {
        // Brickognize-id sieht aus wie ein fig_num
        var item = new BrickognizeItem
        {
            Id = "fig-005059",
            Name = "Deathstroke",
            Type = "fig",
            ExternalSites = new List<BrickognizeExternalSite>()
        };

        var catalog = new FakeCatalog();
        catalog.Minifigs["fig-005059"] = new CatalogMinifig
        {
            FigNum = "fig-005059", Name = "Deathstroke", NumParts = 3
        };

        var sut = new MinifigLookupService(catalog, new ExternalIdResolver());
        var result = await sut.LookupAsync(item);

        Assert.Equal(MinifigResolutionMethod.BrickognizeIdAsFigNum, result.Method);
        Assert.Equal("fig-005059", result.Minifig!.FigNum);
    }

    [Fact]
    public async Task Lookup_falls_back_to_unique_name_match()
    {
        var item = new BrickognizeItem
        {
            Id = "cty0969",
            Name = "Construction Worker - Female, Orange Vest",
            Type = "fig",
            ExternalSites = new List<BrickognizeExternalSite>
            {
                new() { Name = "bricklink",
                        Url = "https://www.bricklink.com/v2/catalog/catalogitem.page?M=cty0969" }
            }
        };

        var catalog = new FakeCatalog();
        catalog.NameSearchResults["Construction Worker - Female, Orange Vest"] = new List<CatalogMinifig>
        {
            new() { FigNum = "fig-007777", Name = "Construction Worker - Female, Orange Vest", NumParts = 4 }
        };

        var sut = new MinifigLookupService(catalog, new ExternalIdResolver());
        var result = await sut.LookupAsync(item);

        Assert.Equal(MinifigResolutionMethod.UniqueNameMatch, result.Method);
        Assert.Equal("fig-007777", result.Minifig!.FigNum);
    }

    [Fact]
    public async Task Lookup_marks_ambiguous_when_name_returns_many_matches()
    {
        var item = new BrickognizeItem
        {
            Id = "ggg",
            Name = "Stormtrooper",
            Type = "fig",
            ExternalSites = new List<BrickognizeExternalSite>()
        };

        var catalog = new FakeCatalog();
        catalog.NameSearchResults["Stormtrooper"] = new List<CatalogMinifig>
        {
            new() { FigNum = "fig-001", Name = "Stormtrooper" },
            new() { FigNum = "fig-002", Name = "Stormtrooper Sergeant" },
            new() { FigNum = "fig-003", Name = "Stormtrooper Officer" }
        };

        var sut = new MinifigLookupService(catalog, new ExternalIdResolver());
        var result = await sut.LookupAsync(item);

        Assert.Equal(MinifigResolutionMethod.AmbiguousNameMatch, result.Method);
        Assert.Equal("fig-001", result.Minifig!.FigNum); // erster Treffer wird genommen
        Assert.Equal(3, result.AmbiguousCandidates.Count);
    }

    [Fact]
    public async Task Lookup_returns_NotFound_when_nothing_matches()
    {
        var item = new BrickognizeItem
        {
            Id = "unknown",
            Name = "Some Unknown Minifig",
            Type = "fig",
            ExternalSites = new List<BrickognizeExternalSite>()
        };

        var catalog = new FakeCatalog();
        var sut = new MinifigLookupService(catalog, new ExternalIdResolver());
        var result = await sut.LookupAsync(item);

        Assert.Equal(MinifigResolutionMethod.NotFound, result.Method);
        Assert.Null(result.Minifig);
    }

    [Fact]
    public async Task Lookup_returns_parts_when_minifig_found()
    {
        var item = new BrickognizeItem
        {
            Id = "fig-005059", Type = "fig", Name = "Deathstroke",
            ExternalSites = new List<BrickognizeExternalSite>()
        };

        var catalog = new FakeCatalog();
        catalog.Minifigs["fig-005059"] = new CatalogMinifig
        {
            FigNum = "fig-005059", Name = "Deathstroke", NumParts = 3
        };
        catalog.MinifigParts["fig-005059"] = new List<CatalogMinifigPart>
        {
            new() { PartNumber = "3626", ColorId = 71, ColorName = "Light Bluish Gray", Quantity = 1 },
            new() { PartNumber = "973", ColorId = 0, ColorName = "Black", Quantity = 1 },
            new() { PartNumber = "970", ColorId = 0, ColorName = "Black", Quantity = 1 }
        };

        var sut = new MinifigLookupService(catalog, new ExternalIdResolver());
        var result = await sut.LookupAsync(item);

        Assert.Equal(3, result.Parts.Count);
    }

    /// <summary>
    /// Minimaler Fake-CatalogService: alle Methoden, die der MinifigLookupService
    /// wirklich aufruft, werden ueber Dictionaries beantwortet. Andere Methoden
    /// werfen NotImplementedException, falls je gerufen.
    /// </summary>
    private sealed class FakeCatalog : ICatalogService
    {
        public Dictionary<string, CatalogMinifig> Minifigs { get; } = new();
        public Dictionary<string, List<CatalogMinifigPart>> MinifigParts { get; } = new();
        public Dictionary<string, List<CatalogMinifig>> NameSearchResults { get; } = new();

        public bool CatalogExists() => true;

        public Task<CatalogMinifig?> GetMinifigAsync(string figNum, CancellationToken ct = default)
            => Task.FromResult(Minifigs.TryGetValue(figNum, out var m) ? m : null);

        public Task<List<CatalogMinifigPart>> GetMinifigPartsAsync(string figNum, CancellationToken ct = default)
            => Task.FromResult(MinifigParts.TryGetValue(figNum, out var p) ? p : new List<CatalogMinifigPart>());

        public Task<List<CatalogMinifig>> SearchMinifigsByNameAsync(string query, int limit, CancellationToken ct = default)
            => Task.FromResult(NameSearchResults.TryGetValue(query, out var r)
                ? r.Take(limit).ToList()
                : new List<CatalogMinifig>());

        // Nicht vom Lookup-Service aufgerufen:
        public Task<CatalogMetadata?> GetMetadataAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<CatalogColor>> GetAllColorsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CatalogColor?> GetColorAsync(int colorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CatalogColor?> GetColorByNameAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CatalogPart?> GetPartByNumAsync(string partNum, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
