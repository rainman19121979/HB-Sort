using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Models.Pricing;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Audit W-6: Tests fuer BricklinkApiPriceProvider.
///
/// Strategie: wir testen den **Cache-First-Pfad** und die **Konfigurations-
/// Logik** ohne echten BricklinkSharp-Call. Sobald wir einen Cache-Hit
/// liefern oder das Rate-Limit blockiert, wird das interne CreateClientAsync
/// nicht aufgerufen - so vermeiden wir den OAuth-Setup-Aufwand im Test.
///
/// Was NICHT getestet wird:
/// - der echte HTTP-/OAuth-Pfad (braucht entweder mocken von BricklinkSharp
///   oder einen End-to-End-Test mit Live-Tokens; beides nicht im Audit-Scope).
/// - BulkImport-Service-Tests (braucht Mock-HttpClient + Test-ZIP, ueber
///   diesem Iteration-Scope).
/// </summary>
public class BricklinkApiPriceProviderTests
{
    private readonly StubTokenStorage _tokens = new();
    private readonly StubCache _cache = new();
    private readonly StubRateLimiter _limiter = new();
    private readonly StubSettings _settings = new();
    private readonly BricklinkApiPriceProvider _sut;

    public BricklinkApiPriceProviderTests()
    {
        _sut = new BricklinkApiPriceProvider(_tokens, _cache, _limiter, _settings);
    }

    // ====================================================================
    // IsConfiguredAsync
    // ====================================================================

    [Fact]
    public async Task IsConfigured_false_when_no_tokens()
    {
        _tokens.Stored = null;
        Assert.False(await _sut.IsConfiguredAsync());
    }

    [Fact]
    public async Task IsConfigured_true_when_tokens_complete()
    {
        _tokens.Stored = new BricklinkTokens
        {
            ConsumerKey = "ck", ConsumerSecret = "cs",
            TokenValue = "tv",  TokenSecret = "ts"
        };
        Assert.True(await _sut.IsConfiguredAsync());
    }

    // ====================================================================
    // Cache-First (kein API-Call wenn Cache-Hit)
    // ====================================================================

    [Fact]
    public async Task GetMinifigPrice_returns_cached_without_calling_api()
    {
        // Cache liefert einen frischen Eintrag -> Provider nutzt den, kein
        // API-Call (= Limiter wird gar nicht gefragt).
        _cache.NextCached = MakePrice(avg: 12.50m);

        var result = await _sut.GetMinifigPriceAsync("arc007");

        Assert.NotNull(result);
        Assert.Equal(12.50m, result!.AvgPrice);
        // Sicher dass kein API-Pfad eingeschlagen wurde:
        Assert.Equal(0, _limiter.CanMakeCallCount);
    }

    [Fact]
    public async Task GetMinifigPrice_uses_minifig_TTL()
    {
        // Audit W-8 Verifikation: M -> BlPriceCacheTtlMinifigDays.
        _settings.Current.Prices.BlPriceCacheTtlMinifigDays = 30;
        _settings.Current.Prices.BlPriceCacheTtlPartDays    = 60;
        _cache.NextCached = MakePrice(10m);

        await _sut.GetMinifigPriceAsync("arc007");

        Assert.Equal(30, _cache.LastStaleDays);
    }

    [Fact]
    public async Task GetPartPrice_uses_part_TTL()
    {
        _settings.Current.Prices.BlPriceCacheTtlMinifigDays = 30;
        _settings.Current.Prices.BlPriceCacheTtlPartDays    = 60;
        _cache.NextCached = MakePrice(0.5m);

        await _sut.GetPartPriceAsync("3001", 11);

        Assert.Equal(60, _cache.LastStaleDays);
    }

    // ====================================================================
    // Rate-Limit-Block -> Stale-Fallback (gibt vorhandenen oder null)
    // ====================================================================

    [Fact]
    public async Task GetPrice_falls_back_to_stale_when_rate_limited()
    {
        // Erste Cache-Frage (frisch) -> null. Zweite Cache-Frage (stale, ohne
        // TTL) -> liefert alten Wert. Limiter blockiert.
        _cache.NextCached = null;
        _cache.NextStale  = MakePrice(8m);
        _limiter.CanMakeCallResult = false;

        var result = await _sut.GetMinifigPriceAsync("arc007");

        Assert.NotNull(result);
        Assert.Equal(8m, result!.AvgPrice);
        // Provider-Label endet auf "(Cache)" um dem User klar zu machen
        // dass der Wert stale ist.
        Assert.Contains("(Cache)", result.ProviderLabel ?? "");
    }

    [Fact]
    public async Task GetPrice_returns_null_when_rate_limited_and_no_cache()
    {
        _cache.NextCached = null;
        _cache.NextStale  = null;
        _limiter.CanMakeCallResult = false;

        var result = await _sut.GetMinifigPriceAsync("arc007");
        Assert.Null(result);
    }

    // ====================================================================
    // UX X.34 v0.1.20: VAT-Mode wird korrekt aus Settings durchgereicht
    // ====================================================================

    [Fact]
    public async Task GetPrice_passes_vat_Y_to_cache_lookup_by_default()
    {
        // Default-Settings: VatMode.Y -> Cache-Lookup mit "Y" filtern.
        _cache.NextCached = MakePrice(10m);

        await _sut.GetMinifigPriceAsync("arc007");

        Assert.Equal("Y", _cache.LastVatMode);
    }

    [Fact]
    public async Task GetPrice_passes_vat_N_to_cache_lookup_when_setting_changed()
    {
        _settings.Current.Prices.VatMode = VatMode.N;
        _cache.NextCached = MakePrice(10m);

        await _sut.GetMinifigPriceAsync("arc007");

        Assert.Equal("N", _cache.LastVatMode);
    }

    [Fact]
    public async Task GetPrice_provider_label_contains_vat_mode_text()
    {
        _settings.Current.Prices.VatMode = VatMode.Y;
        _cache.NextCached = MakePrice(10m);

        var result = await _sut.GetMinifigPriceAsync("arc007");

        Assert.NotNull(result);
        Assert.Contains("Brutto", result!.ProviderLabel ?? "");
    }

    [Fact]
    public async Task GetPrice_provider_label_with_netto_when_vat_N()
    {
        _settings.Current.Prices.VatMode = VatMode.N;
        _cache.NextCached = MakePrice(10m);

        var result = await _sut.GetMinifigPriceAsync("arc007");

        Assert.NotNull(result);
        Assert.Contains("Netto", result!.ProviderLabel ?? "");
    }

    // ====================================================================
    // UX X.34 v0.1.20: Either-Or Region/CountryCode wird beim Cache-Lookup
    // korrekt aufgeloest (CountryCode wins -> Region leer im Cache-Key).
    // ====================================================================

    [Fact]
    public async Task GetPrice_either_or_country_wins_over_region_in_cache_key()
    {
        // Wenn beide gesetzt sind (alter Bestand): CountryCode gewinnt,
        // Region wird beim Cache-Lookup auf "" gezwungen.
        _settings.Current.Prices.CountryCode = "DE";
        _settings.Current.Prices.Region      = "europe";
        _cache.NextCached = MakePrice(10m);

        await _sut.GetMinifigPriceAsync("arc007");

        Assert.Equal(string.Empty, _cache.LastRegion);
    }

    [Fact]
    public async Task GetPrice_either_or_region_only_passes_region_in_cache_key()
    {
        _settings.Current.Prices.CountryCode = string.Empty;
        _settings.Current.Prices.Region      = "europe";
        _cache.NextCached = MakePrice(10m);

        await _sut.GetMinifigPriceAsync("arc007");

        Assert.Equal("europe", _cache.LastRegion);
    }

    [Fact]
    public async Task GetPrice_either_or_global_passes_empty_region_in_cache_key()
    {
        _settings.Current.Prices.CountryCode = string.Empty;
        _settings.Current.Prices.Region      = string.Empty;
        _cache.NextCached = MakePrice(10m);

        await _sut.GetMinifigPriceAsync("arc007");

        Assert.Equal(string.Empty, _cache.LastRegion);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private static PriceResult MakePrice(decimal avg) => new()
    {
        AvgPrice = avg,
        QtyAvgPrice = avg,
        MinPrice = avg * 0.8m,
        MaxPrice = avg * 1.2m,
        Currency = "EUR",
        FetchedAt = DateTime.UtcNow,
        UnitQuantity = 5
    };

    // ----- Stubs -----

    /// <summary>
    /// Stub fuer IBricklinkTokenStorage. HasTokens basiert auf Stored != null,
    /// LoadAsync gibt das Stored-Objekt zurueck.
    /// </summary>
    private sealed class StubTokenStorage : IBricklinkTokenStorage
    {
        public BricklinkTokens? Stored { get; set; }
        public bool HasTokens() => Stored != null;
        public Task<BricklinkTokens?> LoadAsync() => Task.FromResult(Stored);
        public Task SaveAsync(BricklinkTokens tokens) { Stored = tokens; return Task.CompletedTask; }
        public Task ClearAsync() { Stored = null; return Task.CompletedTask; }
    }

    /// <summary>
    /// Stub fuer IBlCacheRepository - nur die Preis-Cache-Operationen, alles
    /// andere wirft. NextCached liefert den ersten Aufruf (Cache-Lookup mit
    /// staleDays > 0); NextStale den zweiten Aufruf (Stale-Fallback mit
    /// staleDays = 0). LastStaleDays merkt sich den letzten genutzten Wert.
    /// </summary>
    private sealed class StubCache : IBlCacheRepository
    {
        public PriceResult? NextCached { get; set; }
        public PriceResult? NextStale { get; set; }
        public int LastStaleDays { get; private set; } = -1;

        public string? LastVatMode { get; private set; }
        public string? LastRegion { get; private set; }

        public Task<PriceResult?> GetCachedPriceAsync(
            string itemType, string itemNo, int colorId,
            string guideType, string newOrUsed,
            string region, string currency,
            int staleDays, CancellationToken ct = default,
            string vatMode = "N")
        {
            LastStaleDays = staleDays;
            LastVatMode = vatMode;
            LastRegion = region;
            // staleDays > 0 ist "frische Pruefung", staleDays == 0 ist Stale-Fallback.
            return Task.FromResult(staleDays > 0 ? NextCached : NextStale);
        }

        // Alle anderen Methoden werden in unseren Tests nicht aufgerufen.
        public Task<BlItem?> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertItemAsync(BlItem item, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertItemsAsync(IEnumerable<BlItem> items, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> IsItemStaleAsync(string itemType, string itemNo, int staleDays, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<string, int>> GetCategoryIdsForPartsAsync(IEnumerable<string> partNumbers, CancellationToken ct = default) => throw new NotImplementedException();
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
        public string? LastUpsertVatMode { get; private set; }

        public Task UpsertPriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, PriceResult price, CancellationToken ct = default, string vatMode = "N")
        {
            LastUpsertVatMode = vatMode;
            return Task.CompletedTask;
        }
        public Task<CachedPriceLookup?> GetCachedPriceWithStaleFlagAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, int ttlDays, CancellationToken ct = default, string vatMode = "N") => throw new NotImplementedException();
        public Task<bool> DeletePriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, CancellationToken ct = default, string vatMode = "N") => throw new NotImplementedException();
        public Task<int> ClearAllPricesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearEmptyPricesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetPriceCacheCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>
    /// Stub fuer IBricklinkRateLimiter. CanMakeCallAsync gibt CanMakeCallResult
    /// zurueck (Default true), zaehlt Aufrufe in CanMakeCallCount.
    /// </summary>
    private sealed class StubRateLimiter : IBricklinkRateLimiter
    {
        public bool CanMakeCallResult { get; set; } = true;
        public int CanMakeCallCount { get; private set; }

        public Task<bool> CanMakeCallAsync(CancellationToken ct = default)
        {
            CanMakeCallCount++;
            return Task.FromResult(CanMakeCallResult);
        }

        public event EventHandler<RateLimitStatus>? StatusChanged;
        public Task LogCallAsync(string method, string? itemType, string? itemNo, int responseTimeMs, int statusCode, bool success, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RateLimitStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task PruneOldEntriesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        // StatusChanged-Field wird genutzt - "ungenutzt"-Warnung vermeiden:
        private void Touch() => StatusChanged?.Invoke(this, null!);
    }

    /// <summary>Inline-AppSettings mit zugaenglichen Defaults.</summary>
    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; } = new()
        {
            Prices = new PriceSettings
            {
                Provider = "BricklinkApi",
                GuideType = "sold",
                Currency = "EUR",
                Region = "europe",
                CountryCode = "DE",
                BlPriceCacheTtlMinifigDays = 90,
                BlPriceCacheTtlPartDays = 90
            }
        };
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }
}
