using HBSort.Core.Models;
using HBSort.Core.Models.Pricing;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer BlPriceCacheService - Stale-While-Revalidate-Strategie +
/// In-Flight-Schutz. Repo + Provider sind Stubs damit wir die Strategie
/// isoliert testen koennen.
/// </summary>
public class BlPriceCacheServiceTests : IDisposable
{
    private readonly string _testDir = Path.Combine(
        Path.GetTempPath(), $"hbsort-pricecache-tests-{Guid.NewGuid():N}");
    private readonly BlCacheRepository _repo;
    private readonly StubProvider _provider = new();
    private readonly StubProviderFactory _factory;
    private readonly StubSettings _settings = new();
    private readonly BlPriceCacheService _sut;

    public BlPriceCacheServiceTests()
    {
        Directory.CreateDirectory(_testDir);
        _repo = new BlCacheRepository(Path.Combine(_testDir, "bl_cache.db"));
        _factory = new StubProviderFactory(_provider);
        _sut = new BlPriceCacheService(_repo, _factory, _settings);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task GetMinifigPrice_returns_Cache_when_fresh_entry_in_db()
    {
        // Cache hat einen frischen Eintrag (1 Tag alt, TTL=90 -> nicht stale).
        await SeedCacheAsync("M", "arc007", 0, avg: 12m,
            fetchedAt: DateTime.UtcNow.AddDays(-1));

        var outcome = await _sut.GetMinifigPriceAsync("arc007");

        Assert.Equal(PriceLookupSource.Cache, outcome.Source);
        Assert.NotNull(outcome.Price);
        Assert.Equal(12m, outcome.Price!.AvgPrice);
        Assert.Equal(0, _provider.MinifigCallCount); // Provider NICHT aufgerufen
    }

    [Fact]
    public async Task GetMinifigPrice_returns_Stale_and_triggers_background_revalidation()
    {
        // Cache-Eintrag 100 Tage alt, TTL=90 -> stale.
        await SeedCacheAsync("M", "arc007", 0, avg: 10m,
            fetchedAt: DateTime.UtcNow.AddDays(-100));
        _provider.NextMinifigPrice = new PriceResult
        {
            AvgPrice = 15m, Currency = "EUR", FetchedAt = DateTime.UtcNow
        };

        var outcome = await _sut.GetMinifigPriceAsync("arc007");

        // User bekommt SOFORT den Stale-Wert.
        Assert.Equal(PriceLookupSource.Stale, outcome.Source);
        Assert.Equal(10m, outcome.Price!.AvgPrice);

        // Hintergrund-Revalidate ist fire-and-forget; wir geben ihm einen Moment.
        for (int i = 0; i < 50 && _provider.MinifigCallCount == 0; i++)
            await Task.Delay(20);

        Assert.Equal(1, _provider.MinifigCallCount);
    }

    [Fact]
    public async Task GetMinifigPrice_returns_Live_when_no_cache_entry()
    {
        _provider.NextMinifigPrice = new PriceResult
        {
            AvgPrice = 20m, Currency = "EUR", FetchedAt = DateTime.UtcNow
        };

        var outcome = await _sut.GetMinifigPriceAsync("arc007");

        Assert.Equal(PriceLookupSource.Live, outcome.Source);
        Assert.Equal(20m, outcome.Price!.AvgPrice);
        Assert.Equal(1, _provider.MinifigCallCount);
    }

    [Fact]
    public async Task GetMinifigPrice_returns_None_with_error_when_provider_throws()
    {
        _provider.MinifigShouldThrow = true;

        var outcome = await _sut.GetMinifigPriceAsync("arc007");

        Assert.Equal(PriceLookupSource.None, outcome.Source);
        Assert.Null(outcome.Price);
        Assert.NotNull(outcome.ErrorMessage);
    }

    [Fact]
    public async Task GetMinifigPrice_in_flight_guard_collapses_parallel_calls_into_one()
    {
        // Provider verzoegert antwortet, damit wir wirklich 5 Aufrufe parallel haben.
        _provider.MinifigDelayMs = 100;
        _provider.NextMinifigPrice = new PriceResult
        {
            AvgPrice = 99m, Currency = "EUR", FetchedAt = DateTime.UtcNow
        };

        // 5 parallele Aufrufe fuer dieselbe Minifig-Id.
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => _sut.GetMinifigPriceAsync("arc007"))
            .ToArray();
        await Task.WhenAll(tasks);

        // Trotz 5 Aufrufen sollte der Provider nur einmal in die API gegangen sein.
        Assert.Equal(1, _provider.MinifigCallCount);
        Assert.All(tasks, t => Assert.Equal(99m, t.Result.Price!.AvgPrice));
    }

    [Fact]
    public async Task GetPartPrice_uses_separate_TTL_setting()
    {
        // 50 Tage alter Eintrag. Mit Part-TTL=30 -> stale; mit Mini-TTL=90 waere er frisch.
        await SeedCacheAsync("P", "3001", 11, avg: 0.05m,
            fetchedAt: DateTime.UtcNow.AddDays(-50));
        _settings.Current.Prices.BlPriceCacheTtlPartDays = 30;
        _settings.Current.Prices.BlPriceCacheTtlMinifigDays = 90;

        var outcome = await _sut.GetPartPriceAsync("3001", 11);

        Assert.Equal(PriceLookupSource.Stale, outcome.Source);
    }

    [Fact]
    public async Task DeleteForMinifig_removes_complete_entry_and_subset_entries()
    {
        await SeedCacheAsync("M", "arc007", 0, avg: 12m, fetchedAt: DateTime.UtcNow);
        await SeedCacheAsync("P", "3001", 11, avg: 0.05m, fetchedAt: DateTime.UtcNow);
        await SeedCacheAsync("P", "3024", 0,  avg: 0.02m, fetchedAt: DateTime.UtcNow);

        await _sut.DeleteForMinifigAsync("arc007", new[]
        {
            ("3001", 11),
            ("3024", 0)
        });

        Assert.Equal(0, await _sut.GetEntryCountAsync());
    }

    [Fact]
    public async Task ClearAll_resets_count_to_zero()
    {
        await SeedCacheAsync("M", "arc007", 0, avg: 12m, fetchedAt: DateTime.UtcNow);
        await SeedCacheAsync("P", "3001", 11, avg: 0.05m, fetchedAt: DateTime.UtcNow);

        var removed = await _sut.ClearAllAsync();

        Assert.Equal(2, removed);
        Assert.Equal(0, await _sut.GetEntryCountAsync());
    }

    // ---- Helpers ----

    private Task SeedCacheAsync(
        string itemType, string itemNo, int colorId,
        decimal avg, DateTime fetchedAt)
    {
        var cfg = _settings.Current.Prices;
        return _repo.UpsertPriceAsync(
            itemType, itemNo, colorId,
            cfg.GuideType, "U", cfg.Region, cfg.Currency,
            new PriceResult
            {
                AvgPrice = avg, Currency = cfg.Currency, FetchedAt = fetchedAt
            });
    }

    /// <summary>Stub-Provider mit einstellbaren Antworten.</summary>
    private sealed class StubProvider : IPriceProvider
    {
        public string Name => "Stub";
        public bool IsConfigured { get; set; } = true;

        public PriceResult? NextMinifigPrice { get; set; }
        public PriceResult? NextPartPrice { get; set; }
        public bool MinifigShouldThrow { get; set; }
        public bool PartShouldThrow { get; set; }
        public int MinifigDelayMs { get; set; }
        public int MinifigCallCount;
        public int PartCallCount;

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default)
            => Task.FromResult(IsConfigured);

        public async Task<PriceResult?> GetMinifigPriceAsync(string blMinifigId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref MinifigCallCount);
            if (MinifigDelayMs > 0) await Task.Delay(MinifigDelayMs, ct);
            if (MinifigShouldThrow) throw new InvalidOperationException("simuliert");
            return NextMinifigPrice;
        }

        public Task<PriceResult?> GetPartPriceAsync(string blPartNo, int blColorId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PartCallCount);
            if (PartShouldThrow) throw new InvalidOperationException("simuliert");
            return Task.FromResult(NextPartPrice);
        }
    }

    private sealed class StubProviderFactory : IPriceProviderFactory
    {
        private readonly IPriceProvider _p;
        public StubProviderFactory(IPriceProvider p) => _p = p;
        public IPriceProvider GetActiveProvider() => _p;
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; } = new()
        {
            Prices = new PriceSettings
            {
                Provider = "BricklinkApi",
                Currency = "EUR",
                Region = "europe",
                GuideType = "sold",
                BlPriceCacheTtlMinifigDays = 90,
                BlPriceCacheTtlPartDays = 90
            }
        };
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }
}
