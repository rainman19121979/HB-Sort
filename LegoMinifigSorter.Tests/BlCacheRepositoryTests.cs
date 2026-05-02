using LegoMinifigSorter.Core.Models.Bricklink;
using LegoMinifigSorter.Core.Services;

namespace LegoMinifigSorter.Tests;

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
}
