using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Models.Pricing;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// UX X.34 v0.1.20-beta.2: Tests fuer den FirstRunService - alle 4
/// Kombinationen von (HasCatalog, HasBins) ergeben den richtigen Status.
/// Stub fuer IBlCacheRepository simuliert nur den ItemCount; in-memory
/// SQLite fuer UserDataContext ermoeglicht echtes EF-Verhalten.
/// </summary>
public class FirstRunServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly StubBlCache _cache = new();
    private readonly FirstRunService _sut;

    public FirstRunServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<UserDataContext>()
            .UseSqlite(_connection).Options;
        using (var ctx = new UserDataContext(options))
            ctx.Database.EnsureCreated();

        _factory = new SimpleContextFactory(options);
        _sut = new FirstRunService(_cache, _factory);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task FrischeInstallation_NeedsAll()
    {
        // Kein Catalog, keine Bins -> NeedsAll
        _cache.SetItemCount(0);
        var status = await _sut.CheckStatusAsync();
        Assert.Equal(FirstRunStatus.NeedsAll, status);
    }

    [Fact]
    public async Task NurCatalog_NeedsBins()
    {
        _cache.SetItemCount(110_000);  // realistischer BL-Catalog
        var status = await _sut.CheckStatusAsync();
        Assert.Equal(FirstRunStatus.NeedsBins, status);
    }

    [Fact]
    public async Task NurBins_NeedsCatalog()
    {
        _cache.SetItemCount(0);
        await SeedBinAsync("Box 01");
        var status = await _sut.CheckStatusAsync();
        Assert.Equal(FirstRunStatus.NeedsCatalog, status);
    }

    [Fact]
    public async Task BeideVorhanden_Complete()
    {
        _cache.SetItemCount(110_000);
        await SeedBinAsync("Box 01");
        var status = await _sut.CheckStatusAsync();
        Assert.Equal(FirstRunStatus.Complete, status);
    }

    [Fact]
    public async Task EinzigerBin_reicht_fuer_HasBins()
    {
        // Sicherstellen dass AnyAsync (intern LIMIT 1) verwendet wird,
        // nicht eine Mindest-Anzahl-Heuristik.
        _cache.SetItemCount(110_000);
        await SeedBinAsync("Box 01");
        var status = await _sut.CheckStatusAsync();
        Assert.Equal(FirstRunStatus.Complete, status);
    }

    private async Task SeedBinAsync(string label)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.StorageBins.Add(new StorageBin { Label = label, CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    // ---- Stubs ----

    /// <summary>
    /// Minimal-Stub fuer IBlCacheRepository - nur GetStatsAsync wird vom
    /// FirstRunService aufgerufen. Restliche Methoden werfen NotImplemented.
    /// </summary>
    private sealed class StubBlCache : IBlCacheRepository
    {
        private int _itemCount;
        public void SetItemCount(int count) => _itemCount = count;

        public Task<BlCacheStats> GetStatsAsync(CancellationToken ct = default) =>
            Task.FromResult(new BlCacheStats(_itemCount, 0, 0, 0, null));

        // Restliche Methoden: NotImplemented, werden vom FirstRunService nicht aufgerufen.
        public Task<BlItem?> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertItemAsync(BlItem item, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertItemsAsync(IEnumerable<BlItem> items, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> IsItemStaleAsync(string itemType, string itemNo, int staleDays, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<string, int>> GetCategoryIdsForPartsAsync(IEnumerable<string> partNumbers, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<string, string>> GetItemNamesAsync(IEnumerable<string> partNumbers, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<(string ItemType, string ItemNo), BlItemSummary>> GetItemSummariesAsync(IEnumerable<(string ItemType, string ItemNo)> keys, CancellationToken ct = default) => throw new NotImplementedException();
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
        public Task<int> ClearStaleAsync(int staleDays, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task LogApiCallAsync(string method, string? itemType, string? itemNo, int responseTimeMs, int statusCode, bool success, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetCallCountInWindowAsync(TimeSpan window, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetCallCountSinceAsync(DateTime since, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DateTime?> GetOldestCallInWindowAsync(TimeSpan window, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneApiCallLogAsync(int olderThanDays = 7, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PriceResult?> GetCachedPriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, int staleDays, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task UpsertPriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, PriceResult price, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task<CachedPriceLookup?> GetCachedPriceWithStaleFlagAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, int ttlDays, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task<bool> DeletePriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task<int> ClearAllPricesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearEmptyPricesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetPriceCacheCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }
}
