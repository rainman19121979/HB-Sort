using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer das BlCacheRepository.
/// Eigene SQLite-Datei pro Testlauf damit Tests isoliert sind.
/// </summary>
public class BlCacheRepositoryTests : IDisposable
{
    private readonly string _testDir = Path.Combine(
        Path.GetTempPath(), $"lego-blcache-tests-{Guid.NewGuid():N}");

    private readonly BlCacheRepository _sut;

    public BlCacheRepositoryTests()
    {
        Directory.CreateDirectory(_testDir);
        _sut = new BlCacheRepository(Path.Combine(_testDir, "bl_cache.db"));
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task UpsertItem_then_GetItem_round_trip()
    {
        var item = new BlItem
        {
            ItemType = "M", ItemNo = "arc007",
            Name = "Arctic Forscher",
            YearReleased = 2014,
            ImageUrl = "https://img.bricklink.com/...",
            Weight = 2.5,
            CategoryId = 271,
            DataCompleteness = DataCompleteness.Full
        };
        await _sut.UpsertItemAsync(item);

        var loaded = await _sut.GetItemAsync("M", "arc007");
        Assert.NotNull(loaded);
        Assert.Equal("Arctic Forscher", loaded!.Name);
        Assert.Equal(DataCompleteness.Full, loaded.DataCompleteness);
        Assert.Equal(271, loaded.CategoryId);
    }

    [Fact]
    public async Task UpsertItem_subset_does_NOT_overwrite_full()
    {
        // Erst full
        await _sut.UpsertItemAsync(new BlItem
        {
            ItemType = "P", ItemNo = "3001",
            Name = "Brick 2 x 4",
            YearReleased = 1962,
            CategoryId = 11,
            DataCompleteness = DataCompleteness.Full
        });

        // Dann subset (mit weniger Infos)
        await _sut.UpsertItemAsync(new BlItem
        {
            ItemType = "P", ItemNo = "3001",
            Name = "Brick 2 x 4 (subset)",   // anderer Name
            YearReleased = null,             // weniger Infos
            CategoryId = null,
            DataCompleteness = DataCompleteness.Subset
        });

        var loaded = await _sut.GetItemAsync("P", "3001");
        // Kern-Test: full-Daten muessen erhalten bleiben
        Assert.NotNull(loaded);
        Assert.Equal("Brick 2 x 4", loaded!.Name);
        Assert.Equal(DataCompleteness.Full, loaded.DataCompleteness);
        Assert.Equal(11, loaded.CategoryId);
        Assert.Equal(1962, loaded.YearReleased);
    }

    [Fact]
    public async Task UpsertItem_full_overwrites_subset()
    {
        await _sut.UpsertItemAsync(new BlItem
        {
            ItemType = "P", ItemNo = "3024",
            Name = "Plate 1x1 (subset)",
            DataCompleteness = DataCompleteness.Subset
        });

        await _sut.UpsertItemAsync(new BlItem
        {
            ItemType = "P", ItemNo = "3024",
            Name = "Plate 1 x 1",
            CategoryId = 26,
            YearReleased = 1979,
            DataCompleteness = DataCompleteness.Full
        });

        var loaded = await _sut.GetItemAsync("P", "3024");
        Assert.NotNull(loaded);
        Assert.Equal("Plate 1 x 1", loaded!.Name);
        Assert.Equal(DataCompleteness.Full, loaded.DataCompleteness);
        Assert.Equal(26, loaded.CategoryId);
    }

    [Fact]
    public async Task UpsertItems_bulk_inserts_in_transaction()
    {
        var items = Enumerable.Range(0, 20).Select(i => new BlItem
        {
            ItemType = "P", ItemNo = $"part_{i}",
            Name = $"Part {i}",
            DataCompleteness = DataCompleteness.Subset
        });
        await _sut.UpsertItemsAsync(items);

        for (int i = 0; i < 20; i++)
        {
            var x = await _sut.GetItemAsync("P", $"part_{i}");
            Assert.NotNull(x);
        }
    }

    [Fact]
    public async Task IsItemStaleAsync_true_when_missing()
    {
        Assert.True(await _sut.IsItemStaleAsync("P", "9999", staleDays: 90));
    }

    [Fact]
    public async Task IsItemStaleAsync_false_for_fresh_entry()
    {
        await _sut.UpsertItemAsync(new BlItem
        {
            ItemType = "P", ItemNo = "1", Name = "x",
            DataCompleteness = DataCompleteness.Full,
            FetchedAt = DateTime.UtcNow
        });
        Assert.False(await _sut.IsItemStaleAsync("P", "1", staleDays: 90));
    }

    [Fact]
    public async Task ReplaceSubsets_removes_old_then_inserts_new()
    {
        // Initial 3 Subsets
        var first = new[]
        {
            MakeSubset("M", "arc007", "P", "3626c01", colorId: 1),
            MakeSubset("M", "arc007", "P", "973pb01",  colorId: 36),
            MakeSubset("M", "arc007", "P", "970c00",   colorId: 36)
        };
        await _sut.ReplaceSubsetsAsync("M", "arc007", first);
        Assert.Equal(3, (await _sut.GetSubsetsAsync("M", "arc007")).Count);

        // Ersetzen mit 2 anderen Eintraegen -> alte sollten weg sein
        var second = new[]
        {
            MakeSubset("M", "arc007", "P", "newpart1", colorId: 5),
            MakeSubset("M", "arc007", "P", "newpart2", colorId: 5)
        };
        await _sut.ReplaceSubsetsAsync("M", "arc007", second);

        var loaded = await _sut.GetSubsetsAsync("M", "arc007");
        Assert.Equal(2, loaded.Count);
        Assert.DoesNotContain(loaded, s => s.ItemNo == "3626c01");
    }

    [Fact]
    public async Task FindParents_returns_minifigs_containing_part_in_color()
    {
        // Zwei Minifigs, beide enthalten 3024 in Color 11
        await _sut.ReplaceSubsetsAsync("M", "fig1", new[]
        {
            MakeSubset("M", "fig1", "P", "3024", colorId: 11),
            MakeSubset("M", "fig1", "P", "9999", colorId: 99) // andere Farbe/Teil
        });
        await _sut.ReplaceSubsetsAsync("M", "fig2", new[]
        {
            MakeSubset("M", "fig2", "P", "3024", colorId: 11)
        });
        // Ein dritter Eintrag mit derselben Teil-Nr aber anderer Farbe
        await _sut.ReplaceSubsetsAsync("M", "fig3", new[]
        {
            MakeSubset("M", "fig3", "P", "3024", colorId: 5)
        });

        var parents = await _sut.FindParentsByItemAsync("P", "3024", colorId: 11);
        Assert.Contains("fig1", parents);
        Assert.Contains("fig2", parents);
        Assert.DoesNotContain("fig3", parents);
    }

    [Fact]
    public async Task UpsertColors_round_trip()
    {
        var colors = new[]
        {
            new BlColor { ColorId = 1, Name = "White", Rgb = "FFFFFF", Type = "Solid" },
            new BlColor { ColorId = 11, Name = "Black", Rgb = "000000", Type = "Solid" }
        };
        await _sut.UpsertColorsAsync(colors);

        var loaded = await _sut.GetAllColorsAsync();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, c => c.ColorId == 1 && c.Rgb == "FFFFFF");
    }

    [Fact]
    public async Task GetStats_reports_counts_and_oldest()
    {
        await _sut.UpsertItemAsync(new BlItem
        {
            ItemType = "M", ItemNo = "arc007", Name = "Arctic", DataCompleteness = DataCompleteness.Full
        });
        await _sut.UpsertColorsAsync(new[]
        {
            new BlColor { ColorId = 1, Name = "White" }
        });

        var stats = await _sut.GetStatsAsync();
        Assert.Equal(1, stats.ItemCount);
        Assert.Equal(1, stats.ColorCount);
        Assert.NotNull(stats.OldestFetchedAt);
        Assert.True(stats.DbFileSizeBytes > 0);
    }

    [Fact]
    public async Task ClearAll_empties_all_tables()
    {
        await _sut.UpsertItemAsync(new BlItem
        {
            ItemType = "P", ItemNo = "1", Name = "x", DataCompleteness = DataCompleteness.Full
        });
        await _sut.UpsertColorsAsync(new[] { new BlColor { ColorId = 1, Name = "y" } });
        await _sut.ReplaceSubsetsAsync("M", "fig", new[] { MakeSubset("M", "fig", "P", "1", 0) });

        await _sut.ClearAllAsync();

        var stats = await _sut.GetStatsAsync();
        Assert.Equal(0, stats.ItemCount);
        Assert.Equal(0, stats.ColorCount);
        Assert.Equal(0, stats.SubsetCount);
    }

    private static BlSubset MakeSubset(string pt, string pn, string it, string itemNo, int colorId, int matchId = 0)
        => new()
        {
            ParentType = pt, ParentNo = pn, ItemType = it, ItemNo = itemNo,
            ColorId = colorId, Quantity = 1, MatchId = matchId,
            FetchedAt = DateTime.UtcNow
        };

    // ====================================================================
    // Phase 8 / UX#12 Stale-While-Revalidate
    // ====================================================================

    [Fact]
    public async Task GetCachedPriceWithStaleFlag_returns_null_when_no_entry()
    {
        var hit = await _sut.GetCachedPriceWithStaleFlagAsync(
            "M", "arc007", 0, "sold", "U", "europe", "EUR", ttlDays: 90);
        Assert.Null(hit);
    }

    [Fact]
    public async Task GetCachedPriceWithStaleFlag_returns_fresh_entry_with_IsStale_false()
    {
        await _sut.UpsertPriceAsync("M", "arc007", 0, "sold", "U", "europe", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult
            {
                AvgPrice = 12.50m, Currency = "EUR", FetchedAt = DateTime.UtcNow
            });

        var hit = await _sut.GetCachedPriceWithStaleFlagAsync(
            "M", "arc007", 0, "sold", "U", "europe", "EUR", ttlDays: 90);

        Assert.NotNull(hit);
        Assert.False(hit!.IsStale);
        Assert.Equal(12.50m, hit.Price.AvgPrice);
    }

    [Fact]
    public async Task GetCachedPriceWithStaleFlag_marks_old_entry_as_stale()
    {
        // FetchedAt vor 100 Tagen, TTL=90 -> stale
        await _sut.UpsertPriceAsync("M", "arc007", 0, "sold", "U", "europe", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult
            {
                AvgPrice = 12.50m, Currency = "EUR",
                FetchedAt = DateTime.UtcNow.AddDays(-100)
            });

        var hit = await _sut.GetCachedPriceWithStaleFlagAsync(
            "M", "arc007", 0, "sold", "U", "europe", "EUR", ttlDays: 90);

        Assert.NotNull(hit);
        Assert.True(hit!.IsStale);
        Assert.Equal(12.50m, hit.Price.AvgPrice); // Wert wird trotzdem geliefert
    }

    [Fact]
    public async Task DeletePrice_removes_specific_entry_and_returns_true()
    {
        await _sut.UpsertPriceAsync("M", "arc007", 0, "sold", "U", "europe", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult { AvgPrice = 1m, Currency = "EUR" });

        var deleted = await _sut.DeletePriceAsync("M", "arc007", 0, "sold", "U", "europe", "EUR");
        Assert.True(deleted);

        var afterDelete = await _sut.GetCachedPriceWithStaleFlagAsync(
            "M", "arc007", 0, "sold", "U", "europe", "EUR", ttlDays: 90);
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task DeletePrice_returns_false_when_not_found()
    {
        var deleted = await _sut.DeletePriceAsync("M", "ghost", 0, "sold", "U", "europe", "EUR");
        Assert.False(deleted);
    }

    [Fact]
    public async Task ClearAllPrices_removes_everything_and_returns_count()
    {
        await _sut.UpsertPriceAsync("M", "arc007", 0, "sold", "U", "europe", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult { AvgPrice = 1m, Currency = "EUR" });
        await _sut.UpsertPriceAsync("P", "3001", 11, "sold", "U", "europe", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult { AvgPrice = 0.05m, Currency = "EUR" });

        var removed = await _sut.ClearAllPricesAsync();
        Assert.Equal(2, removed);
        Assert.Equal(0, await _sut.GetPriceCacheCountAsync());
    }

    [Fact]
    public async Task GetPriceCacheCount_increments_with_each_upsert()
    {
        Assert.Equal(0, await _sut.GetPriceCacheCountAsync());
        await _sut.UpsertPriceAsync("M", "arc007", 0, "sold", "U", "europe", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult { AvgPrice = 1m, Currency = "EUR" });
        Assert.Equal(1, await _sut.GetPriceCacheCountAsync());
    }

    // ====================================================================
    // UX X.34 v0.1.20: ClearEmptyPricesAsync - Anti-Vergiftung
    // ====================================================================

    [Fact]
    public async Task ClearEmptyPrices_removes_only_completely_empty_entries()
    {
        // Voll-leerer Eintrag (Bug-Resultat aus dem Empty-Filter-Pfad).
        await _sut.UpsertPriceAsync("M", "leer1", 0, "sold", "U", "", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult { Currency = "EUR" });
        // Eintrag mit nur AvgPrice gesetzt - bleibt drin.
        await _sut.UpsertPriceAsync("M", "halb", 0, "sold", "U", "", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult { AvgPrice = 1.23m, Currency = "EUR" });
        // Eintrag mit nur TotalQuantity > 0 (z.B. 0-Preis aber Quantity?) -
        // unwahrscheinlich, aber wird konservativ behalten weil Quantity-
        // Information existiert.
        await _sut.UpsertPriceAsync("M", "qty-only", 0, "sold", "U", "", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult { TotalQuantity = 5, Currency = "EUR" });
        // Zweiter voll-leerer Eintrag.
        await _sut.UpsertPriceAsync("P", "3001", 11, "sold", "U", "europe", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult { Currency = "EUR" });

        var removed = await _sut.ClearEmptyPricesAsync();

        Assert.Equal(2, removed);
        // Die beiden mit Daten muessen erhalten bleiben.
        Assert.Equal(2, await _sut.GetPriceCacheCountAsync());
    }

    [Fact]
    public async Task ClearEmptyPrices_returns_zero_when_no_empty_entries()
    {
        await _sut.UpsertPriceAsync("M", "voll", 0, "sold", "U", "", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult { AvgPrice = 1m, Currency = "EUR" });

        var removed = await _sut.ClearEmptyPricesAsync();

        Assert.Equal(0, removed);
        Assert.Equal(1, await _sut.GetPriceCacheCountAsync());
    }

    [Fact]
    public async Task ClearEmptyPrices_idempotent_second_call_returns_zero()
    {
        await _sut.UpsertPriceAsync("M", "leer", 0, "sold", "U", "", "EUR",
            new HBSort.Core.Models.Pricing.PriceResult { Currency = "EUR" });

        var first  = await _sut.ClearEmptyPricesAsync();
        var second = await _sut.ClearEmptyPricesAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    // ========================================================================
    // v0.1.20-beta.5: country_code als Cache-Dimension + vat_mode im PK
    // ========================================================================

    [Fact]
    public async Task Cache_keys_distinguish_country_DE_from_global()
    {
        // Vorher Bug: Country=DE und Global teilten sich denselben Cache-Eintrag
        // (region="" in beiden Faellen) und ueberschrieben sich gegenseitig.
        // Jetzt: country_code ist eigene Cache-Dimension - zwei separate Eintraege.
        var nowGlobal = DateTime.UtcNow.AddMinutes(-1);
        var nowDe     = DateTime.UtcNow;

        // Erst Global schreiben (countryCode="")
        await _sut.UpsertPriceAsync("M", "cty0703", 0, "stock", "U",
            region: "", currency: "EUR",
            price: new HBSort.Core.Models.Pricing.PriceResult
            {
                AvgPrice = 1.43m, Currency = "EUR", FetchedAt = nowGlobal
            },
            vatMode: "Y",
            countryCode: "");

        // Dann DE schreiben (countryCode="DE")
        await _sut.UpsertPriceAsync("M", "cty0703", 0, "stock", "U",
            region: "", currency: "EUR",
            price: new HBSort.Core.Models.Pricing.PriceResult
            {
                AvgPrice = 1.18m, Currency = "EUR", FetchedAt = nowDe
            },
            vatMode: "Y",
            countryCode: "DE");

        // Beide muessen separat zurueckkommen.
        var global = await _sut.GetCachedPriceAsync("M", "cty0703", 0, "stock", "U",
            region: "", currency: "EUR", staleDays: 30, vatMode: "Y", countryCode: "");
        var de = await _sut.GetCachedPriceAsync("M", "cty0703", 0, "stock", "U",
            region: "", currency: "EUR", staleDays: 30, vatMode: "Y", countryCode: "DE");

        Assert.NotNull(global);
        Assert.NotNull(de);
        Assert.Equal(1.43m, global!.AvgPrice);
        Assert.Equal(1.18m, de!.AvgPrice);
    }

    [Fact]
    public async Task Cache_keys_include_vat_mode()
    {
        // vat_mode ist seit beta.5 Teil des Primary Key. Ein Y-Eintrag und ein
        // N-Eintrag fuer dasselbe Item duerfen sich nicht gegenseitig ueberschreiben.
        var t = DateTime.UtcNow;

        await _sut.UpsertPriceAsync("M", "cty0703", 0, "stock", "U",
            region: "", currency: "EUR",
            price: new HBSort.Core.Models.Pricing.PriceResult
            {
                AvgPrice = 1.50m, Currency = "EUR", FetchedAt = t
            },
            vatMode: "Y", countryCode: "");

        await _sut.UpsertPriceAsync("M", "cty0703", 0, "stock", "U",
            region: "", currency: "EUR",
            price: new HBSort.Core.Models.Pricing.PriceResult
            {
                AvgPrice = 1.20m, Currency = "EUR", FetchedAt = t
            },
            vatMode: "N", countryCode: "");

        var brutto = await _sut.GetCachedPriceAsync("M", "cty0703", 0, "stock", "U",
            region: "", currency: "EUR", staleDays: 30, vatMode: "Y", countryCode: "");
        var netto  = await _sut.GetCachedPriceAsync("M", "cty0703", 0, "stock", "U",
            region: "", currency: "EUR", staleDays: 30, vatMode: "N", countryCode: "");

        Assert.NotNull(brutto);
        Assert.NotNull(netto);
        Assert.Equal(1.50m, brutto!.AvgPrice);
        Assert.Equal(1.20m, netto!.AvgPrice);
    }

    // ========================================================================
    // v0.1.22-beta.1 Fix 2026-05-12: GetItemNamesAsync Bulk-Lookup
    // ========================================================================

    [Fact]
    public async Task GetItemNamesAsync_returns_names_for_existing_part_numbers()
    {
        await _sut.UpsertItemsAsync(new[]
        {
            new BlItem { ItemType = "P", ItemNo = "2446", Name = "Minifigure, Headgear Helmet Motorcycle (Standard)",
                         DataCompleteness = DataCompleteness.Full, FetchedAt = DateTime.UtcNow },
            new BlItem { ItemType = "P", ItemNo = "93560", Name = "Minifigure, Headgear Helmet Sports / Flight",
                         DataCompleteness = DataCompleteness.Full, FetchedAt = DateTime.UtcNow },
            // Minifig - darf NICHT mit-gematcht werden (item_type='M').
            new BlItem { ItemType = "M", ItemNo = "2446", Name = "Bogus Minifig",
                         DataCompleteness = DataCompleteness.Full, FetchedAt = DateTime.UtcNow },
        });

        var result = await _sut.GetItemNamesAsync(new[] { "2446", "93560" });

        Assert.Equal(2, result.Count);
        Assert.Equal("Minifigure, Headgear Helmet Motorcycle (Standard)", result["2446"]);
        Assert.Equal("Minifigure, Headgear Helmet Sports / Flight", result["93560"]);
    }

    [Fact]
    public async Task GetItemNamesAsync_returns_empty_dict_when_no_match()
    {
        // Cache leer - die abgefragten Parts existieren nicht.
        var result = await _sut.GetItemNamesAsync(new[] { "9999", "ghost" });
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetItemNamesAsync_handles_empty_input_gracefully()
    {
        var result = await _sut.GetItemNamesAsync(Array.Empty<string>());
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ========================================================================
    // FindMinifigsContainingPartsAsync - Skalierung (Crash-Fix)
    // ========================================================================

    [Fact]
    public async Task FindMinifigsContainingPartsAsync_handles_empty_input()
    {
        // Leerer Input darf nie crashen und liefert eine leere Liste.
        var result = await _sut.FindMinifigsContainingPartsAsync(
            Array.Empty<(string, int)>());
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task FindMinifigsContainingPartsAsync_scales_to_large_pool_without_crash()
    {
        // Reproduktions-Test fuer den Skalierungs-Crash:
        // Frueher baute die Methode eine OR-Liste mit N Klauseln. Bei einem
        // grossen Pool (BUILD-1 uebergibt das ganze BL-Inventar, ~9.000+ Tuples)
        // sprengt das die SQLite-Limit "Expression tree is too large
        // (maximum depth 1000)".
        //
        // Wir legen einen Subset-Eintrag an, der auf eines der Tuples matcht,
        // und uebergeben dann einen sehr grossen Pool (2000 Tuples).

        // Ein "echter" Subset-Eintrag: Minifig "arc007" enthaelt Teil "3626" in Farbe 11.
        await _sut.ReplaceSubsetsAsync("M", "arc007", new[]
        {
            new BlSubset
            {
                ParentType = "M", ParentNo = "arc007",
                ItemType = "P", ItemNo = "3626", ColorId = 11,
                Quantity = 1, IsFromSupersets = false
            }
        });

        // Grosser Such-Pool: 2000 Tuples, weit ueber der 1000-Tiefe-Grenze.
        // Das erste Tuple matcht den eingefuegten Subset, der Rest ist Fuellmaterial.
        var pool = new List<(string PartNo, int ColorId)>
        {
            ("3626", 11)
        };
        for (int i = 0; i < 2000; i++)
            pool.Add(($"dummy{i}", i % 50));

        // Vor dem Fix: SqliteException "Expression tree is too large".
        // Nach dem Fix: kein Crash, korrektes Ergebnis.
        var result = await _sut.FindMinifigsContainingPartsAsync(pool);

        Assert.Contains("arc007", result);
    }

    // ========================================================================
    // BUILD-1 Perf-Fix (v0.1.25): Index auf der TEMP-Such-Tabelle.
    //
    // Gemessene Wurzel (Diagnose 2026-06-07, echte DB mit 1,38 Mio
    // bl_subsets-Zeilen): JOIN ohne Index auf temp_search_parts = 43,7s,
    // mit Index temp_search_parts(part_no, color_id) = 0,049s (identisch
    // 3651 Parents). Ohne Index waehlt SQLite einen quadratischen Join-Plan.
    //
    // Ein Timing-Test bei 1,3 Mio Zeilen ist fuer CI zu schwer. Deshalb
    // deterministisch via EXPLAIN QUERY PLAN: der Plan muss fuer
    // temp_search_parts einen indizierten Lookup (SEARCH ... USING INDEX)
    // zeigen, NICHT einen Full-Scan (SCAN temp_search_parts).
    // ========================================================================

    [Fact]
    public async Task FindMinifigsContainingPartsAsync_join_uses_index_on_temp_search_parts()
    {
        // Funktionskorrektheit zuerst absichern: ein Subset-Eintrag, der von
        // einem der ~100 Such-Tuples getroffen wird.
        await _sut.ReplaceSubsetsAsync("M", "arc007", new[]
        {
            new BlSubset
            {
                ParentType = "M", ParentNo = "arc007",
                ItemType = "P", ItemNo = "3626", ColorId = 11,
                Quantity = 1, IsFromSupersets = false
            }
        });

        var pool = new List<(string PartNo, int ColorId)> { ("3626", 11) };
        for (int i = 0; i < 100; i++)
            pool.Add(($"dummy{i}", i % 20));

        // Echten Methoden-Aufruf machen: legt die TEMP-Tabelle (und - nach dem
        // Fix - den Index) auf der langlebigen Connection an und fuellt sie.
        var result = await _sut.FindMinifigsContainingPartsAsync(pool);
        Assert.Contains("arc007", result); // Ergebnis-Korrektheit bleibt erhalten.

        // Den Query-Plan derselben JOIN-Query auf derselben Connection holen.
        var plan = _sut.GetFindMinifigsJoinQueryPlanForDiagnostics();
        var planText = string.Join(" | ", plan);

        // Beweis-Assertion: die Such-Tabelle (in der JOIN-Query als "t"
        // aliasiert) muss per Index durchsucht werden.
        //
        // SQLite nennt im Plan den Tabellen-ALIAS ("t"), nicht den vollen
        // Tabellen-Namen. Eindeutig und stabil ist dagegen der Index-Name
        // temp_idx_search_parts, der nur auf temp_search_parts existiert.
        // Vor dem Fix (kein Index): Plan enthaelt "SCAN t" -> ROT.
        // Nach dem Fix (CREATE INDEX): "SEARCH t USING INDEX
        // temp_idx_search_parts" -> GRUEN.
        Assert.Contains(plan, line =>
            line.Contains("SEARCH", StringComparison.OrdinalIgnoreCase)
            && line.Contains("temp_idx_search_parts", StringComparison.OrdinalIgnoreCase));

        // Gegenprobe: die Such-Tabelle "t" darf nicht voll gescannt werden.
        Assert.DoesNotContain(plan, line =>
            line.TrimStart().StartsWith("SCAN t", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase));

        // Sicherheitsnetz fuer den Fall dass die Plan-Texte sich aendern.
        Assert.False(string.IsNullOrWhiteSpace(planText), "Query-Plan war leer.");
    }
}
