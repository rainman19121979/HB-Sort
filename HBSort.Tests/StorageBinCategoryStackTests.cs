using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Models.Pricing;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// UX X.32 v0.1.19-beta.4 Block C: Tests fuer den Kategorie-Stapel-Pfad
/// im StorageBinService. Eigene Test-Klasse weil der Konstruktor jetzt
/// optional ein <see cref="IBlCacheRepository"/> erwartet - hier mit
/// Mock-Repo das per PartNo-Praefix BL-Kategorien zurueckliefert.
/// </summary>
public class StorageBinCategoryStackTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly StorageBinService _sut;
    private readonly StubBlCache _blCache = new();

    public StorageBinCategoryStackTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<UserDataContext>()
            .UseSqlite(_connection)
            .Options;
        using (var ctx = new UserDataContext(options))
        {
            ctx.Database.EnsureCreated();
        }
        _factory = new SimpleContextFactory(options);
        _sut = new StorageBinService(_factory, _blCache);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Limit1_with_existing_floating_picks_new_free_bin_for_different_category()
    {
        // Box 01 hat einen Head-FloatingPart drin. Wir scannen einen Torso
        // (andere Kategorie) -> Limit=1 -> NICHT in Box 01 mischen, sondern
        // Box 02 nehmen.
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "3626pb1234", 11, qty: 1); // Head

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "973pb5678", 11, maxCategoriesPerBin: 1);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    // ===== UX X.32 v0.1.19-beta.4 (User-Befund): strikte Kategorie-Trennung =====

    [Fact]
    public async Task Limit1_same_category_different_partno_goes_to_separate_bin()
    {
        // Box 01 hat schon ein Bein-Teil. Wir scannen ein ANDERES Bein-Teil
        // (verschiedene PartNo aber gleiche Kategorie). User-Spec:
        // strikte Trennung pro Kategorie - das neue Bein muss in ein
        // anderes Fach. Step 1 (gleicher PartNo+ColorId) trifft nicht
        // (verschiedene PartNos), Heuristik aliasiert beide auf 3815
        // -> Bin 01 wird ausgefiltert.
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "3815", 11, qty: 1);

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "3816", 11, maxCategoriesPerBin: 1);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task Heuristic_separates_head_from_torso_at_limit1()
    {
        // Heuristik: 3626* = Head (Praefix 3626), 973* = Torso (973).
        // Verschiedene Praefixe -> verschiedene Kategorien -> bei Limit=1
        // strikte Trennung.
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "3626pb01", 11, qty: 1); // Head

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "973pb01", 11, maxCategoriesPerBin: 1);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task Limit2_with_one_existing_category_mixes_into_same_bin()
    {
        // Box 01 hat Head, Box 02 ist frei. Wir scannen Torso, Limit=2.
        // -> Box 01 wird vorgeschlagen (Mix erlaubt, Stapel waechst).
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "3626pb1234", 11, qty: 1); // Head

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "973pb5678", 11, maxCategoriesPerBin: 2);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 01", suggestion!.Label);
    }

    [Fact]
    public async Task Limit3_full_falls_back_to_free_bin()
    {
        // Box 01 hat Head + Torso + Legs (3 Kategorien). Limit=3 -> voll.
        // 4. Kategorie (Hat) muss in Box 02.
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "3626pb1", 11, qty: 1); // Head
        await SeedFloatingAsync("Box 01", "973pb1",  11, qty: 1); // Torso
        await SeedFloatingAsync("Box 01", "970pb1",  11, qty: 1); // Legs

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "3878", 11, maxCategoriesPerBin: 3); // Hat - Sentinel-Kategorie

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SamePartSameColor_always_stacks_regardless_of_limit()
    {
        // Step 1 (gleicher Stapel) gewinnt unabhaengig vom Limit. Box 01 hat
        // schon Head 3626pb1234 - neuer Scan vom gleichen Teil -> Box 01,
        // egal was MaxCategoriesPerBin sagt.
        await CreateBins("Box 01", "Box 02", "Box 03");
        await SeedFloatingAsync("Box 01", "3626pb1234", 11, qty: 5);

        // Limit=1 - sollte trotzdem Box 01 sein (Stapel waechst).
        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "3626pb1234", 11, maxCategoriesPerBin: 1);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 01", suggestion!.Label);
    }

    [Fact]
    public async Task Limit2_picks_fuller_bin_when_multiple_under_limit()
    {
        // Box 01 hat Head, Box 02 hat Head + Torso. Wir scannen Hat (3.).
        // Limit=3 -> beide unter Limit, Box 02 ist voller -> wird gewaehlt.
        await CreateBins("Box 01", "Box 02", "Box 03");
        await SeedFloatingAsync("Box 01", "3626pb1", 11, qty: 1); // Head
        await SeedFloatingAsync("Box 02", "3626pb2", 11, qty: 1); // Head
        await SeedFloatingAsync("Box 02", "973pb1",  11, qty: 1); // Torso

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "3878", 11, maxCategoriesPerBin: 3);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task WithoutBlCache_strict_limit1_falls_back_to_truly_free()
    {
        // Defensiver Fall: StorageBinService ohne IBlCacheRepository
        // konstruiert. Heuristik via PartNo-Praefix funktioniert weiter.
        // Bei Limit=1 strikte Trennung -> verschiedene Kategorien
        // bekommen verschiedene Bins.
        var sutNoCache = new StorageBinService(_factory);
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "3626pb1234", 11, qty: 1);

        var suggestion = await sutNoCache.SuggestBinForFloatingPartAsync(
            "973pb5678", 11, maxCategoriesPerBin: 1);

        Assert.NotNull(suggestion);
        // Phase A: 973-Cat in Box 01? Nein. Phase B nicht aktiv (Limit=1).
        // Stufe 3 (truly free) -> Box 02.
        Assert.Equal("Box 02", suggestion!.Label);
    }

    // ===== Name-Heuristik (User-Befund "Beine landen in 1 Fach") =====

    [Fact]
    public async Task NameHeuristic_separates_legs_with_same_partname_prefix_into_different_bin()
    {
        // User-Spec: pro Kategorie max EIN unique Item pro Fach. Beide
        // Bein-Teile haben PartName "Minifig Hips and Legs, ..." -> selbe
        // Kategorie aus Name-Heuristik. Verschiedene PartNos -> Step 1
        // (gleicher PartNo+ColorId) trifft nicht. Phase B: Box 01 hat die
        // Kategorie schon -> ausgefiltert -> Box 02.
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "PARTA", 11, qty: 1,
            partName: "Minifig Hips and Legs, Black");

        var sut = new StorageBinService(_factory);
        var suggestion = await sut.SuggestBinForFloatingPartAsync(
            "PARTB", 11,
            maxCategoriesPerBin: 1,
            partName: "Minifig Hips and Legs, Blue");

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task NameHeuristic_separates_head_from_legs_at_limit1()
    {
        // PartName-Praefix "Minifig Head" vs "Minifig Hips and Legs" ->
        // verschiedene Kategorien, Limit=1 -> verschiedene Bins.
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "X1", 11, qty: 1,
            partName: "Minifig Head, Female with Pink Lips");

        var sut = new StorageBinService(_factory);
        var suggestion = await sut.SuggestBinForFloatingPartAsync(
            "X2", 11,
            maxCategoriesPerBin: 1,
            partName: "Minifig Hips and Legs, Brown");

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task NameHeuristic_takes_priority_over_partno_prefix()
    {
        // User-Befund (BOX19-16): Box 01 hat schon eine Kopfbedeckung
        // (Helm-Variante A). Wir scannen eine andere Kopfbedeckung (Helm
        // B). PartNo-Praefixe sind verschieden (61190, 30362), aber beide
        // Teile sind laut Name "Minifig Headgear, ..." - Name-Heuristik
        // gewinnt und erkennt sie als gleiche Kategorie. User-Spec:
        // pro Kategorie nur 1 unique Item -> Helm B in anderes Fach.
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "61190", 11, qty: 1,
            partName: "Minifig Headgear, Helmet SW Pilot");

        var sut = new StorageBinService(_factory);
        var suggestion = await sut.SuggestBinForFloatingPartAsync(
            "30362", 11,
            maxCategoriesPerBin: 1,
            partName: "Minifig Headgear, Helmet Castle");

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    // ===== Mix-Verhalten bei Limit > 1 (User-Spec: andere Kategorien duerfen mit) =====

    [Fact]
    public async Task NameHeuristic_other_category_can_share_bin_at_limit_999()
    {
        // User-Modell: Box 01 hat schon eine Kopfbedeckung. Wir scannen
        // einen Kopf (andere Kategorie). Mit Limit > 1 darf der Kopf
        // ins gleiche Fach (Mix) - aber pro Kategorie nur 1 unique.
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "61190", 11, qty: 1,
            partName: "Minifig Headgear, Helmet SW Pilot");

        var sut = new StorageBinService(_factory);
        var suggestion = await sut.SuggestBinForFloatingPartAsync(
            "3626", 11,
            maxCategoriesPerBin: 999,
            partName: "Minifig Head, Female with Pink Lips");

        Assert.NotNull(suggestion);
        // Andere Kategorie + Mix erlaubt -> Box 01 (vollster passender).
        Assert.Equal("Box 01", suggestion!.Label);
    }

    [Fact]
    public async Task NameHeuristic_same_category_separates_even_at_limit_999()
    {
        // Auch bei "unbegrenztem" Limit darf eine Kategorie nicht zweimal
        // im selben Fach landen. Box 01 hat eine Kopfbedeckung; ein
        // anderer Helm (gleiche Kategorie) muss in ein anderes Fach.
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "61190", 11, qty: 1,
            partName: "Minifig Headgear, Helmet SW Pilot");

        var sut = new StorageBinService(_factory);
        var suggestion = await sut.SuggestBinForFloatingPartAsync(
            "30362", 11,
            maxCategoriesPerBin: 999,
            partName: "Minifig Headgear, Helmet Castle");

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task NameHeuristic_real_BL_naming_Minifigure_comma_BodyPart()
    {
        // User-Befund (Box19-16, echte DB-Daten): BL-Naming-Konvention ist
        // "Minifigure, BodyPart ..." (Komma nach "Minifigure", nicht
        // "Minifig BodyPart, ..."). Heuristik muss beide Helm-Eintraege
        // als gleiche Kategorie "Headgear" erkennen.
        await CreateBins("Box 01", "Box 02");
        await SeedFloatingAsync("Box 01", "2446", 1, qty: 1,
            partName: "Minifigure, Headgear Helmet Motorcycle (Standard)");

        var sut = new StorageBinService(_factory);
        var suggestion = await sut.SuggestBinForFloatingPartAsync(
            "193a2", 1,
            maxCategoriesPerBin: 999,
            partName: "Minifigure, Headgear Helmet Space / Town with Thin Chin Strap");

        Assert.NotNull(suggestion);
        // Beide haben Body-Part "Headgear" -> Heuristik erkennt sie als
        // gleiche Kategorie -> der zweite Helm muss in ein anderes Fach.
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task NameHeuristic_real_BL_naming_HeadBeard_vs_Headgear_are_separate()
    {
        // "Minifigure, Head Beard ..." -> Body-Part "Head"
        // "Minifigure, Headgear Helmet ..." -> Body-Part "Headgear"
        // Beide MUESSEN unterschiedliche Kategorien liefern (sonst landet
        // ein Helm in einem Bin wo schon ein Kopf ist und der naechste
        // Helm geht ploetzlich in ein anderes Fach).
        await CreateBins("Box 01", "Box 02", "Box 03");
        await SeedFloatingAsync("Box 01", "3626pb0196", 3, qty: 1,
            partName: "Minifigure, Head Beard Brown Angular with White Pupils");

        var sut = new StorageBinService(_factory);
        // Erster Helm passt zur Box 01 (Head ist andere Kategorie als Headgear).
        var first = await sut.SuggestBinForFloatingPartAsync(
            "2446", 1,
            maxCategoriesPerBin: 999,
            partName: "Minifigure, Headgear Helmet Motorcycle (Standard)");
        Assert.NotNull(first);
        Assert.Equal("Box 01", first!.Label);

        // Zweiten Helm zu Box 01 hinzu-seeden, dann den dritten Helm scannen.
        await SeedFloatingAsync("Box 01", "2446", 1, qty: 1,
            partName: "Minifigure, Headgear Helmet Motorcycle (Standard)");

        var second = await sut.SuggestBinForFloatingPartAsync(
            "193a2", 1,
            maxCategoriesPerBin: 999,
            partName: "Minifigure, Headgear Helmet Space / Town with Thin Chin Strap");
        Assert.NotNull(second);
        // Box 01 hat schon Headgear (2446) -> muss in anderes Fach.
        Assert.Equal("Box 02", second!.Label);
    }

    // --- Helpers ---

    private async Task CreateBins(params string[] labels)
    {
        await _sut.CreateBulkAsync(labels);
    }

    private async Task SeedFloatingAsync(
        string binLabel, string partNo, int colorId, int qty,
        string? partName = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = await ctx.StorageBins.FirstAsync(b => b.Label == binLabel);
        ctx.FloatingParts.Add(new FloatingPart
        {
            PartNumber = partNo,
            ColorId = colorId,
            ColorName = "Black",
            PartName = partName ?? partNo,
            Quantity = qty,
            StorageBinId = bin.Id,
            AddedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Mini-Mock fuer IBlCacheRepository: bestimmt die Kategorie via
    /// PartNo-Praefix. Fuer den Service-Test reichen ein paar bekannte
    /// Praefixe + Sentinel -1 ist NICHT noetig (der Service bekommt
    /// "kein Eintrag" wenn der Praefix nicht matcht).
    /// </summary>
    private sealed class StubBlCache : IBlCacheRepository
    {
        public Task<Dictionary<string, int>> GetCategoryIdsForPartsAsync(
            IEnumerable<string> partNumbers, CancellationToken ct = default)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in partNumbers ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                // Praefix-Mapping: 3626 -> Head (65), 973 -> Torso (66), 970 -> Legs (67)
                if      (p.StartsWith("3626", StringComparison.OrdinalIgnoreCase)) result[p] = 65;
                else if (p.StartsWith("973",  StringComparison.OrdinalIgnoreCase)) result[p] = 66;
                else if (p.StartsWith("970",  StringComparison.OrdinalIgnoreCase)) result[p] = 67;
                else if (p.StartsWith("3878", StringComparison.OrdinalIgnoreCase)) result[p] = 68; // Hat
                // Sonst: nicht im Result -> Service nutzt Sentinel -1.
            }
            return Task.FromResult(result);
        }

        // Restliche Methoden: NotImplementedException.
        public Task<BlItem?> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertItemAsync(BlItem item, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertItemsAsync(IEnumerable<BlItem> items, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> IsItemStaleAsync(string itemType, string itemNo, int staleDays, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlSubset>> GetSubsetsAsync(string parentType, string parentNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplaceSubsetsAsync(string parentType, string parentNo, IEnumerable<BlSubset> subsets, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> BulkInsertSubsetsAsync(IEnumerable<BlSubset> subsets, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> FindParentsByItemAsync(string itemType, string itemNo, int colorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(string blPartNo, int blColorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> FindMinifigsContainingPartsAsync(IReadOnlyList<(string PartNo, int ColorId)> parts, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertColorsAsync(IEnumerable<BlColor> colors, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<int>> GetKnownColorIdsAsync(string partNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DateTime?> GetKnownColorsFetchedAtAsync(string partNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplaceKnownColorsAsync(string partNo, IEnumerable<int> colorIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BlCacheStats> GetStatsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearStaleAsync(int staleDays, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task LogApiCallAsync(string method, string? itemType, string? itemNo, int responseTimeMs, int statusCode, bool success, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetCallCountInWindowAsync(TimeSpan window, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetCallCountSinceAsync(DateTime since, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DateTime?> GetOldestCallInWindowAsync(TimeSpan window, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneApiCallLogAsync(int olderThanDays = 7, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PriceResult?> GetCachedPriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, int staleDays, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertPriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, PriceResult price, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CachedPriceLookup?> GetCachedPriceWithStaleFlagAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, int ttlDays, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeletePriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearAllPricesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetPriceCacheCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }
}
