using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Models.Exceptions;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer den BlCatalogService mit echtem Repository (eigene SQLite) und
/// einem Fake-BricklinkClient. Kein Netzwerk involviert.
/// </summary>
public class BlCatalogServiceTests : IDisposable
{
    private readonly string _testDir = Path.Combine(
        Path.GetTempPath(), $"lego-blcatsvc-tests-{Guid.NewGuid():N}");
    private readonly BlCacheRepository _repo;
    private readonly FakeBricklinkClient _client = new();
    private readonly InMemorySettings _settings = new();
    private readonly BricklinkRateLimiter _limiter;
    // v0.1.20.1: Default-Stub haelt Tokens fuer "vorhanden" damit die bestehenden
    // Tests den API-Pfad weiter testen. Tests die explizit das "keine Tokens"-
    // Verhalten beweisen wollen, instantiieren ihren eigenen sut mit
    // HasTokens=false (siehe GetAllColors_*_without_tokens-Tests).
    private readonly TokenStub _tokens = new() { HasAny = true };
    private readonly BlCatalogService _sut;

    public BlCatalogServiceTests()
    {
        Directory.CreateDirectory(_testDir);
        _repo = new BlCacheRepository(Path.Combine(_testDir, "bl_cache.db"));
        _limiter = new BricklinkRateLimiter(_repo, _settings);
        _sut = new BlCatalogService(_repo, _client, _settings, _limiter, _tokens);
    }

    private sealed class TokenStub : IBricklinkTokenStorage
    {
        public bool HasAny { get; set; }
        public bool HasTokens() => HasAny;
        public Task<BricklinkTokens?> LoadAsync() => Task.FromResult<BricklinkTokens?>(null);
        public Task SaveAsync(BricklinkTokens tokens) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task GetMinifigParts_first_call_fetches_from_BL_and_caches()
    {
        _client.SubsetsResponses["M:arc007"] = new List<BricklinkSubsetDto>
        {
            new() { ItemType = "P", ItemNo = "3626c01", ColorId = 1, Quantity = 1, ItemName = "Head" },
            new() { ItemType = "P", ItemNo = "973pb01", ColorId = 36, Quantity = 1, ItemName = "Torso" }
        };

        var subsets = await _sut.GetMinifigPartsAsync("arc007");

        Assert.Equal(2, subsets.Count);
        Assert.Equal(1, _client.SubsetsCallCount);

        // Uebergreifend gecacht: bl_items hat jetzt zwei Subset-Eintraege
        var head = await _repo.GetItemAsync("P", "3626c01");
        var torso = await _repo.GetItemAsync("P", "973pb01");
        Assert.NotNull(head);
        Assert.Equal("Head", head!.Name);
        Assert.Equal(DataCompleteness.Subset, head.DataCompleteness);
        Assert.NotNull(torso);
    }

    [Fact]
    public async Task GetMinifigParts_second_call_hits_cache()
    {
        _client.SubsetsResponses["M:arc007"] = new List<BricklinkSubsetDto>
        {
            new() { ItemType = "P", ItemNo = "3626c01", ColorId = 1, Quantity = 1 }
        };

        await _sut.GetMinifigPartsAsync("arc007");
        await _sut.GetMinifigPartsAsync("arc007");

        Assert.Equal(1, _client.SubsetsCallCount); // genau ein API-Call
    }

    [Fact]
    public async Task GetMinifigParts_propagates_AuthException()
    {
        _client.ThrowOnSubsets = new BricklinkAuthException("401");

        await Assert.ThrowsAsync<BricklinkAuthException>(
            async () => await _sut.GetMinifigPartsAsync("arc007"));
    }

    [Fact]
    public async Task GetMinifigParts_RateLimit_serves_stale_cache()
    {
        // Vorher etwas im Cache (manuell)
        await _repo.ReplaceSubsetsAsync("M", "arc007", new[]
        {
            new BlSubset { ParentType = "M", ParentNo = "arc007",
                ItemType = "P", ItemNo = "3626c01", ColorId = 1, Quantity = 1,
                FetchedAt = DateTime.UtcNow.AddDays(-200) /* stale */ }
        });

        _client.ThrowOnSubsets = new BricklinkRateLimitException("429");

        var result = await _sut.GetMinifigPartsAsync("arc007");
        // Stale-served: wir bekommen den alten Eintrag zurueck statt einer Exception
        Assert.Single(result);
    }

    [Fact]
    public async Task GetPartDetails_subset_cache_is_returned_without_api_call()
    {
        // Subset-Eintrag im Cache (z.B. aus einem vorherigen Subsets-Call)
        await _repo.UpsertItemAsync(new BlItem
        {
            ItemType = "P", ItemNo = "3024", Name = "Plate 1x1",
            DataCompleteness = DataCompleteness.Subset,
            FetchedAt = DateTime.UtcNow
        });

        var item = await _sut.GetPartDetailsAsync("3024");
        Assert.NotNull(item);
        Assert.Equal("Plate 1x1", item!.Name);
        Assert.Equal(0, _client.GetItemCallCount); // KEIN BL-Call
    }

    [Fact]
    public async Task GetPartDetails_missing_triggers_api_call_and_caches_full()
    {
        _client.ItemResponses["P:3024"] = new BricklinkItemDto
        {
            ItemType = "P", ItemNo = "3024", Name = "Plate 1 x 1",
            CategoryId = 26, YearReleased = 1979
        };

        var item = await _sut.GetPartDetailsAsync("3024");
        Assert.NotNull(item);
        Assert.Equal("Plate 1 x 1", item!.Name);
        Assert.Equal(DataCompleteness.Full, item.DataCompleteness);
        Assert.Equal(1, _client.GetItemCallCount);
    }

    [Fact]
    public async Task GetAllColors_first_call_fetches_then_caches_lifelong()
    {
        _client.ColorListResponse = new List<BricklinkColorDto>
        {
            new() { ColorId = 1, Name = "White", Rgb = "FFFFFF", Type = "Solid" },
            new() { ColorId = 11, Name = "Black", Rgb = "212121", Type = "Solid" }
        };

        var first = await _sut.GetAllColorsAsync();
        var second = await _sut.GetAllColorsAsync();

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(1, _client.ColorListCallCount); // beim 2. Mal aus Cache
    }

    // ========================================================================
    // v0.1.20.1: Cache-First-Pattern fuer GetAllColors auch ohne Tokens
    // ========================================================================

    [Fact]
    public async Task GetAllColorsAsync_returns_cache_data_without_tokens()
    {
        // Cache befuellt (z.B. via BrickStore-ZIP-Import) - kein Token noetig.
        await _repo.UpsertColorsAsync(new[]
        {
            new BlColor { ColorId = 1,  Name = "White", Rgb = "FFFFFF", Type = "Solid", FetchedAt = DateTime.UtcNow },
            new BlColor { ColorId = 11, Name = "Black", Rgb = "212121", Type = "Solid", FetchedAt = DateTime.UtcNow },
            new BlColor { ColorId = 5,  Name = "Red",   Rgb = "B40000", Type = "Solid", FetchedAt = DateTime.UtcNow },
        });

        // SUT ohne Token-Storage
        var tokenless = new TokenStub { HasAny = false };
        var sutNoTokens = new BlCatalogService(_repo, _client, _settings, _limiter, tokenless);

        var result = await sutNoTokens.GetAllColorsAsync();

        Assert.Equal(3, result.Count);
        Assert.Equal(0, _client.ColorListCallCount); // KEIN API-Call
    }

    [Fact]
    public async Task GetAllColorsAsync_returns_empty_list_when_cache_empty_and_no_tokens()
    {
        // bl_colors leer + keine Tokens: graceful degradation statt
        // BricklinkAuthException.
        var tokenless = new TokenStub { HasAny = false };
        var sutNoTokens = new BlCatalogService(_repo, _client, _settings, _limiter, tokenless);

        var result = await sutNoTokens.GetAllColorsAsync();

        Assert.Empty(result);
        Assert.Equal(0, _client.ColorListCallCount); // KEIN API-Call
    }

    [Fact]
    public async Task GetAllColorsAsync_falls_back_to_api_when_cache_empty_and_tokens_set()
    {
        // Mit Tokens + leerem Cache: API-Call ist weiter erlaubt
        // (Default-Setup _sut hat HasAny=true).
        _client.ColorListResponse = new List<BricklinkColorDto>
        {
            new() { ColorId = 1, Name = "White", Rgb = "FFFFFF", Type = "Solid" }
        };

        var result = await _sut.GetAllColorsAsync();

        Assert.Single(result);
        Assert.Equal(1, _client.ColorListCallCount);
    }

    [Fact]
    public async Task RateLimit_blocked_serves_stale_cache_for_subsets()
    {
        // Setup: Hard-Threshold = 0 -> ab erstem Call blockiert
        _settings.Current.Bricklink.HardThreshold = 0;

        // Vorhandener stale Cache-Eintrag den wir zurueckbekommen sollten
        await _repo.ReplaceSubsetsAsync("M", "arc007", new[]
        {
            new BlSubset
            {
                ParentType = "M", ParentNo = "arc007",
                ItemType = "P", ItemNo = "3626c01", ColorId = 1, Quantity = 1,
                FetchedAt = DateTime.UtcNow.AddDays(-200) // stale
            }
        });

        _client.SubsetsResponses["M:arc007"] = new List<BricklinkSubsetDto>
        {
            new() { ItemType = "P", ItemNo = "neu", ColorId = 99, Quantity = 1 }
        };

        var result = await _sut.GetMinifigPartsAsync("arc007");

        // Erwartung: stale Cache zurueckgegeben, KEIN BL-Call
        Assert.Single(result);
        Assert.Equal("3626c01", result[0].ItemNo);
        Assert.Equal(0, _client.SubsetsCallCount);
    }

    [Fact]
    public async Task RateLimit_logs_call_after_successful_api_call()
    {
        _client.SubsetsResponses["M:arc007"] = new List<BricklinkSubsetDto>();

        await _sut.GetMinifigPartsAsync("arc007");

        var status = await _limiter.GetStatusAsync();
        Assert.Equal(1, status.CallsLast24h);
    }

    [Fact]
    public async Task FindWaitingMinifigsForPart_intersects_cache_with_waiting_set()
    {
        await _repo.ReplaceSubsetsAsync("M", "fig1", new[]
        {
            new BlSubset { ParentType = "M", ParentNo = "fig1", ItemType = "P", ItemNo = "3024", ColorId = 11, FetchedAt = DateTime.UtcNow }
        });
        await _repo.ReplaceSubsetsAsync("M", "fig2", new[]
        {
            new BlSubset { ParentType = "M", ParentNo = "fig2", ItemType = "P", ItemNo = "3024", ColorId = 11, FetchedAt = DateTime.UtcNow }
        });
        await _repo.ReplaceSubsetsAsync("M", "fig3", new[]
        {
            new BlSubset { ParentType = "M", ParentNo = "fig3", ItemType = "P", ItemNo = "3024", ColorId = 11, FetchedAt = DateTime.UtcNow }
        });

        var waiting = new[] { "fig2", "fig3", "figX" };
        var result = await _sut.FindWaitingMinifigsForPartAsync("3024", 11, waiting);

        Assert.Equal(2, result.Count);
        Assert.Contains("fig2", result);
        Assert.Contains("fig3", result);
        Assert.DoesNotContain("fig1", result); // nicht in waiting-Liste
    }

    // ========================================================================
    // Fakes
    // ========================================================================

    private sealed class InMemorySettings : ISettingsService
    {
        public AppSettings Current { get; } = new()
        {
            Bricklink = new BricklinkSettings { CacheStaleDays = 90 }
        };
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }

    private sealed class FakeBricklinkClient : IBricklinkClient
    {
        public Dictionary<string, BricklinkItemDto> ItemResponses { get; } = new();
        public Dictionary<string, List<BricklinkSubsetDto>> SubsetsResponses { get; } = new();
        public List<BricklinkColorDto>? ColorListResponse { get; set; }

        public int GetItemCallCount { get; private set; }
        public int SubsetsCallCount { get; private set; }
        public int ColorListCallCount { get; private set; }

        public Exception? ThrowOnSubsets { get; set; }
        public Exception? ThrowOnGetItem { get; set; }

        public Task<bool> IsConfiguredAsync() => Task.FromResult(true);

        public Task<BricklinkTestResult> TestConnectionAsync(CancellationToken ct = default)
            => Task.FromResult(new BricklinkTestResult { Success = true, ItemName = "fake" });

        public Task<BricklinkItemDto> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default)
        {
            GetItemCallCount++;
            if (ThrowOnGetItem != null) throw ThrowOnGetItem;
            if (ItemResponses.TryGetValue($"{itemType}:{itemNo}", out var dto)) return Task.FromResult(dto);
            throw new InvalidOperationException($"Test setup missing item {itemType}:{itemNo}");
        }

        public Task<List<BricklinkSubsetDto>> GetSubsetsAsync(string itemType, string itemNo, bool breakMinifigs, CancellationToken ct = default)
        {
            SubsetsCallCount++;
            if (ThrowOnSubsets != null) throw ThrowOnSubsets;
            if (SubsetsResponses.TryGetValue($"{itemType}:{itemNo}", out var list)) return Task.FromResult(list);
            return Task.FromResult(new List<BricklinkSubsetDto>());
        }

        public Task<List<BricklinkColorDto>> GetColorListAsync(CancellationToken ct = default)
        {
            ColorListCallCount++;
            return Task.FromResult(ColorListResponse ?? new List<BricklinkColorDto>());
        }

        public Dictionary<string, List<BricklinkSupersetEntryDto>> SupersetsResponses { get; } = new();
        public Dictionary<string, List<BricklinkKnownColorDto>> KnownColorsResponses { get; } = new();
        public int SupersetsCallCount { get; private set; }
        public int KnownColorsCallCount { get; private set; }

        public Task<List<BricklinkSupersetEntryDto>> GetSupersetsAsync(
            string itemType, string itemNo, int colorId, CancellationToken ct = default)
        {
            SupersetsCallCount++;
            var key = $"{itemType}:{itemNo}:{colorId}";
            return Task.FromResult(SupersetsResponses.TryGetValue(key, out var list)
                ? list
                : new List<BricklinkSupersetEntryDto>());
        }

        public Task<List<BricklinkKnownColorDto>> GetKnownColorsAsync(
            string itemType, string itemNo, CancellationToken ct = default)
        {
            KnownColorsCallCount++;
            var key = $"{itemType}:{itemNo}";
            return Task.FromResult(KnownColorsResponses.TryGetValue(key, out var list)
                ? list
                : new List<BricklinkKnownColorDto>());
        }
    }
}
