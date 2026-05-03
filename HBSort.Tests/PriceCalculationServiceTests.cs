using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Pricing;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer PriceCalculationService. Provider + SettingsService sind Stubs;
/// EF nutzt SQLite-In-Memory damit die Include-Logik realistisch laeuft.
/// </summary>
public class PriceCalculationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly StubProvider _provider = new();
    private readonly StubProviderFactory _providerFactory;
    private readonly StubSettings _settings = new();
    private readonly PriceCalculationService _sut;

    public PriceCalculationServiceTests()
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
        _providerFactory = new StubProviderFactory(_provider);
        _sut = new PriceCalculationService(_providerFactory, _factory, _settings);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Calculate_returns_NoData_when_provider_not_configured()
    {
        _provider.Configured = false;
        var id = await SeedMinifigAsync(blId: "arc007");

        var rec = await _sut.CalculateForMinifigAsync(id);

        Assert.Equal(SalesAdvice.NoData, rec.Advice);
    }

    [Fact]
    public async Task Calculate_returns_NoData_when_minifig_id_not_found_in_db()
    {
        var rec = await _sut.CalculateForMinifigAsync(9999);
        Assert.Equal(SalesAdvice.NoData, rec.Advice);
    }

    [Fact]
    public async Task Calculate_applies_minifig_correction_factor_minus_10_percent()
    {
        var id = await SeedMinifigAsync("arc007", parts: Array.Empty<(string, int, int)>());
        _provider.MinifigPrices["arc007"] = WithQtyAvg(10m);
        _settings.Current.Prices.CorrectionMinifigPercent = -10m;

        var rec = await _sut.CalculateForMinifigAsync(id);

        // 10.00 * 0.90 = 9.00
        Assert.Equal(9.00m, rec.CorrectedMinifigPrice);
    }

    [Fact]
    public async Task Calculate_sums_part_prices_weighted_by_quantity_and_applies_correction()
    {
        // 2x Teil A à 1.00 + 3x Teil B à 2.00 = 8.00
        // -15% Korrektur -> 8.00 * 0.85 = 6.80
        var id = await SeedMinifigAsync("arc007", parts: new[]
        {
            ("partA", 11, 2),
            ("partB", 11, 3),
        });
        _provider.MinifigPrices["arc007"] = WithQtyAvg(0m);     // ignored
        _provider.PartPrices[("partA", 11)] = WithQtyAvg(1m);
        _provider.PartPrices[("partB", 11)] = WithQtyAvg(2m);
        _settings.Current.Prices.CorrectionPartsPercent = -15m;

        var rec = await _sut.CalculateForMinifigAsync(id);

        Assert.Equal(8m, rec.PartsRawSum);
        Assert.Equal(6.80m, rec.CorrectedPartsSum);
    }

    [Fact]
    public async Task Calculate_returns_Equal_when_difference_below_10_percent()
    {
        // Minifig 100, Parts 95 -> 5% Differenz -> Equal
        var id = await SeedMinifigAsync("arc007", parts: new[] { ("partA", 0, 1) });
        _provider.MinifigPrices["arc007"] = WithQtyAvg(100m);
        _provider.PartPrices[("partA", 0)] = WithQtyAvg(95m);
        _settings.Current.Prices.CorrectionMinifigPercent = 0m;
        _settings.Current.Prices.CorrectionPartsPercent = 0m;

        var rec = await _sut.CalculateForMinifigAsync(id);

        Assert.Equal(SalesAdvice.Equal, rec.Advice);
    }

    [Fact]
    public async Task Calculate_returns_CompleteWorthIt_when_minifig_clearly_more_valuable()
    {
        var id = await SeedMinifigAsync("arc007", parts: new[] { ("partA", 0, 1) });
        _provider.MinifigPrices["arc007"] = WithQtyAvg(100m);
        _provider.PartPrices[("partA", 0)] = WithQtyAvg(50m);
        _settings.Current.Prices.CorrectionMinifigPercent = 0m;
        _settings.Current.Prices.CorrectionPartsPercent = 0m;

        var rec = await _sut.CalculateForMinifigAsync(id);

        Assert.Equal(SalesAdvice.CompleteWorthIt, rec.Advice);
        Assert.True(rec.Difference > 0);
    }

    [Fact]
    public async Task Calculate_returns_PartsWorthIt_when_parts_clearly_more_valuable()
    {
        var id = await SeedMinifigAsync("arc007", parts: new[] { ("partA", 0, 1) });
        _provider.MinifigPrices["arc007"] = WithQtyAvg(20m);
        _provider.PartPrices[("partA", 0)] = WithQtyAvg(50m);
        _settings.Current.Prices.CorrectionMinifigPercent = 0m;
        _settings.Current.Prices.CorrectionPartsPercent = 0m;

        var rec = await _sut.CalculateForMinifigAsync(id);

        Assert.Equal(SalesAdvice.PartsWorthIt, rec.Advice);
        Assert.True(rec.Difference < 0);
    }

    [Fact]
    public async Task Calculate_counts_parts_with_and_without_price()
    {
        // 3 Teile, 2 mit Preis, 1 ohne
        var id = await SeedMinifigAsync("arc007", parts: new[]
        {
            ("partA", 0, 1),
            ("partB", 0, 1),
            ("partC", 0, 1),
        });
        _provider.MinifigPrices["arc007"] = WithQtyAvg(50m);
        _provider.PartPrices[("partA", 0)] = WithQtyAvg(1m);
        _provider.PartPrices[("partB", 0)] = WithQtyAvg(2m);
        // partC: kein Eintrag -> Provider liefert null

        var rec = await _sut.CalculateForMinifigAsync(id);

        Assert.Equal(2, rec.PartsWithPriceCount);
        Assert.Equal(1, rec.PartsMissingPriceCount);
    }

    [Fact]
    public async Task Calculate_returns_NoData_when_minifig_price_missing()
    {
        // Parts da, aber kein Minifig-Preis -> NoData (siehe Service-Logik).
        var id = await SeedMinifigAsync("arc007", parts: new[] { ("partA", 0, 1) });
        _provider.PartPrices[("partA", 0)] = WithQtyAvg(5m);
        // Kein MinifigPrice eingetragen -> Provider liefert null

        var rec = await _sut.CalculateForMinifigAsync(id);

        Assert.Equal(SalesAdvice.NoData, rec.Advice);
    }

    [Fact]
    public async Task Calculate_uses_PriceColumn_setting_to_pick_min_avg_qtyavg_or_max()
    {
        var id = await SeedMinifigAsync("arc007", parts: Array.Empty<(string, int, int)>());
        _provider.MinifigPrices["arc007"] = new PriceResult
        {
            MinPrice = 5m, AvgPrice = 10m, QtyAvgPrice = 12m, MaxPrice = 20m
        };
        _settings.Current.Prices.CorrectionMinifigPercent = 0m;

        // min
        _settings.Current.Prices.PriceColumn = "min";
        Assert.Equal(5m, (await _sut.CalculateForMinifigAsync(id)).CorrectedMinifigPrice);

        // avg
        _settings.Current.Prices.PriceColumn = "avg";
        Assert.Equal(10m, (await _sut.CalculateForMinifigAsync(id)).CorrectedMinifigPrice);

        // qty_avg (Default)
        _settings.Current.Prices.PriceColumn = "qty_avg";
        Assert.Equal(12m, (await _sut.CalculateForMinifigAsync(id)).CorrectedMinifigPrice);

        // max
        _settings.Current.Prices.PriceColumn = "max";
        Assert.Equal(20m, (await _sut.CalculateForMinifigAsync(id)).CorrectedMinifigPrice);
    }

    // --- Helpers ---

    private static PriceResult WithQtyAvg(decimal val) => new() { QtyAvgPrice = val };

    private async Task<int> SeedMinifigAsync(
        string blId,
        (string PartNo, int ColorId, int Qty)[]? parts = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var m = new TrackedMinifig
        {
            BricklinkId = blId,
            FigNum = blId,
            Name = blId,
            CreatedAt = DateTime.UtcNow,
            Status = TrackedMinifigStatus.Complete,
            RequiredParts = (parts ?? Array.Empty<(string, int, int)>())
                .Select(p => new TrackedMinifigPart
                {
                    PartNumber = p.PartNo,
                    ColorId = p.ColorId,
                    QuantityNeeded = p.Qty,
                    QuantityCollected = p.Qty
                }).ToList()
        };
        ctx.TrackedMinifigs.Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    // ---- Stubs ----

    private sealed class StubProvider : IPriceProvider
    {
        public bool Configured { get; set; } = true;
        public string Name => "Stub";
        public Dictionary<string, PriceResult?> MinifigPrices { get; } = new();
        public Dictionary<(string, int), PriceResult?> PartPrices { get; } = new();

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(Configured);

        public Task<PriceResult?> GetMinifigPriceAsync(string blMinifigId, CancellationToken ct = default)
            => Task.FromResult(MinifigPrices.TryGetValue(blMinifigId, out var v) ? v : null);

        public Task<PriceResult?> GetPartPriceAsync(string blPartNo, int blColorId, CancellationToken ct = default)
            => Task.FromResult(PartPrices.TryGetValue((blPartNo, blColorId), out var v) ? v : null);
    }

    private sealed class StubProviderFactory : IPriceProviderFactory
    {
        private readonly IPriceProvider _p;
        public StubProviderFactory(IPriceProvider p) => _p = p;
        public IPriceProvider GetActiveProvider() => _p;
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }
}
