using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// v0.1.23: Tests fuer die persistierte StorageBin.Kind-Spalte
/// (RecalculateKindAsync + GetEligibleBinsAsync) plus Strict-Mode
/// (InvalidBinKindException).
/// </summary>
public class StorageBinKindTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly StorageBinService _binSut;

    public StorageBinKindTests()
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
        _binSut = new StorageBinService(_factory);
    }

    public void Dispose() => _connection.Dispose();

    // ========================================================================
    // RecalculateKindAsync
    // ========================================================================

    [Fact]
    public async Task RecalculateKind_sets_Empty_when_no_content()
    {
        var b = await _binSut.CreateSingleAsync("Box 01");

        await _binSut.RecalculateKindAsync(b.Id);

        Assert.Equal(StorageBinKind.Empty, await GetKindAsync(b.Id));
    }

    [Fact]
    public async Task RecalculateKind_sets_Floating_when_only_floatings()
    {
        var b = await _binSut.CreateSingleAsync("Box 01");
        await SeedFloatingInBinAsync(b.Id, "3001", 3);

        await _binSut.RecalculateKindAsync(b.Id);

        Assert.Equal(StorageBinKind.Floating, await GetKindAsync(b.Id));
    }

    [Fact]
    public async Task RecalculateKind_sets_Waiting_when_waiting_present()
    {
        var b = await _binSut.CreateSingleAsync("Box 01");
        await SeedMinifigInBinAsync(b.Id, "arc007", TrackedMinifigStatus.Waiting);

        await _binSut.RecalculateKindAsync(b.Id);

        Assert.Equal(StorageBinKind.Waiting, await GetKindAsync(b.Id));
    }

    [Fact]
    public async Task RecalculateKind_sets_Waiting_when_waiting_plus_complete_Reifung()
    {
        // Reifungspfad: Mix wartend+komplett bleibt Waiting.
        var b = await _binSut.CreateSingleAsync("Box 01");
        await SeedMinifigInBinAsync(b.Id, "wait1", TrackedMinifigStatus.Waiting);
        await SeedMinifigInBinAsync(b.Id, "done1", TrackedMinifigStatus.Complete);

        await _binSut.RecalculateKindAsync(b.Id);

        Assert.Equal(StorageBinKind.Waiting, await GetKindAsync(b.Id));
    }

    [Fact]
    public async Task RecalculateKind_sets_Complete_when_only_complete()
    {
        var b = await _binSut.CreateSingleAsync("Box 01");
        await SeedMinifigInBinAsync(b.Id, "done", TrackedMinifigStatus.Complete);

        await _binSut.RecalculateKindAsync(b.Id);

        Assert.Equal(StorageBinKind.Complete, await GetKindAsync(b.Id));
    }

    // ========================================================================
    // GetEligibleBinsAsync (Index-WHERE auf Kind)
    // ========================================================================

    [Fact]
    public async Task GetEligibleBins_returns_only_allowed_kinds_for_waiting_target()
    {
        // Empty + Waiting + Complete OK fuer Wartend, Floating NICHT.
        var empty = await _binSut.CreateSingleAsync("Box 01");
        var waiting = await _binSut.CreateSingleAsync("Box 02");
        await SeedMinifigInBinAsync(waiting.Id, "wait1", TrackedMinifigStatus.Waiting);
        var complete = await _binSut.CreateSingleAsync("Box 03");
        await SeedMinifigInBinAsync(complete.Id, "done", TrackedMinifigStatus.Complete);
        var floating = await _binSut.CreateSingleAsync("Box 04");
        await SeedFloatingInBinAsync(floating.Id, "3001", 1);

        foreach (var b in new[] { empty, waiting, complete, floating })
            await _binSut.RecalculateKindAsync(b.Id);

        var bins = await _binSut.GetEligibleBinsAsync(BinTargetKind.WaitingMinifigTarget);

        var labels = bins.Select(b => b.Label).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "Box 01", "Box 02", "Box 03" }, labels);
        Assert.DoesNotContain("Box 04", labels);
    }

    [Fact]
    public async Task GetEligibleBins_excludes_floating_only_bins_from_complete_target()
    {
        var empty = await _binSut.CreateSingleAsync("Box 01");
        var floating = await _binSut.CreateSingleAsync("Box 02");
        await SeedFloatingInBinAsync(floating.Id, "3001", 1);
        foreach (var b in new[] { empty, floating })
            await _binSut.RecalculateKindAsync(b.Id);

        var bins = await _binSut.GetEligibleBinsAsync(BinTargetKind.CompleteMinifigTarget);

        Assert.Single(bins);
        Assert.Equal("Box 01", bins[0].Label);
    }

    [Fact]
    public async Task GetEligibleBins_returns_empty_and_floating_for_floating_target_without_partNo()
    {
        var empty = await _binSut.CreateSingleAsync("Box 01");
        var floating = await _binSut.CreateSingleAsync("Box 02");
        await SeedFloatingInBinAsync(floating.Id, "3001", 1);
        var waiting = await _binSut.CreateSingleAsync("Box 03");
        await SeedMinifigInBinAsync(waiting.Id, "w", TrackedMinifigStatus.Waiting);
        var complete = await _binSut.CreateSingleAsync("Box 04");
        await SeedMinifigInBinAsync(complete.Id, "c", TrackedMinifigStatus.Complete);
        foreach (var b in new[] { empty, floating, waiting, complete })
            await _binSut.RecalculateKindAsync(b.Id);

        // Ohne partNo: Waiting-Bin bekommt keinen Bypass.
        var bins = await _binSut.GetEligibleBinsAsync(BinTargetKind.FloatingTarget);

        var labels = bins.Select(b => b.Label).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "Box 01", "Box 02" }, labels);
    }

    [Fact]
    public async Task GetEligibleBins_includes_waiting_bins_for_floating_target_with_reverse_match()
    {
        var waiting = await _binSut.CreateSingleAsync("Box 01");
        var minifigId = await SeedMinifigInBinAsync(waiting.Id, "w", TrackedMinifigStatus.Waiting);
        await SeedRequiredPartAsync(minifigId, "3001", 11, needed: 2, collected: 0);
        await _binSut.RecalculateKindAsync(waiting.Id);

        var matches = await _binSut.GetEligibleBinsAsync(
            BinTargetKind.FloatingTarget, partNo: "3001", colorId: 11);
        var nonMatches = await _binSut.GetEligibleBinsAsync(
            BinTargetKind.FloatingTarget, partNo: "9999", colorId: 99);

        Assert.Single(matches);
        Assert.Equal("Box 01", matches[0].Label);
        Assert.Empty(nonMatches);
    }

    // ========================================================================
    // Strict-Mode (BinKindGuard via Service)
    // ========================================================================

    [Fact]
    public async Task AddPartToFloating_throws_when_target_bin_is_complete()
    {
        var bin = await _binSut.CreateSingleAsync("Box 01");
        await SeedMinifigInBinAsync(bin.Id, "done", TrackedMinifigStatus.Complete);
        await _binSut.RecalculateKindAsync(bin.Id);

        var partLookup = MakePartLookupSut();

        await Assert.ThrowsAsync<InvalidBinKindException>(
            () => partLookup.AddPartToFloatingAsync(
                "3001", 11, "Brick", "Black", 1, bin.Id));
    }

    [Fact]
    public async Task AddPartToFloating_throws_when_target_bin_is_waiting_without_match()
    {
        var bin = await _binSut.CreateSingleAsync("Box 01");
        var mId = await SeedMinifigInBinAsync(bin.Id, "w", TrackedMinifigStatus.Waiting);
        await SeedRequiredPartAsync(mId, "OTHER", 99, needed: 1, collected: 0);
        await _binSut.RecalculateKindAsync(bin.Id);

        var partLookup = MakePartLookupSut();

        // 3001/11 passt zu keiner wartenden Figur im Bin -> throw.
        await Assert.ThrowsAsync<InvalidBinKindException>(
            () => partLookup.AddPartToFloatingAsync(
                "3001", 11, "Brick", "Black", 1, bin.Id));
    }

    [Fact]
    public async Task AddPartToFloating_succeeds_when_target_bin_is_waiting_with_reverse_match()
    {
        var bin = await _binSut.CreateSingleAsync("Box 01");
        var mId = await SeedMinifigInBinAsync(bin.Id, "w", TrackedMinifigStatus.Waiting);
        await SeedRequiredPartAsync(mId, "3001", 11, needed: 2, collected: 0);
        await _binSut.RecalculateKindAsync(bin.Id);

        var partLookup = MakePartLookupSut();

        // 3001/11 passt zur wartenden Figur -> Bypass, kein throw.
        var fp = await partLookup.AddPartToFloatingAsync(
            "3001", 11, "Brick", "Black", 1, bin.Id);
        Assert.NotNull(fp);
    }

    [Fact]
    public async Task PersistAndStore_throws_when_target_bin_is_floating()
    {
        var bin = await _binSut.CreateSingleAsync("Box 01");
        await SeedFloatingInBinAsync(bin.Id, "3001", 1);
        await _binSut.RecalculateKindAsync(bin.Id);

        var persistence = new MinifigPersistenceService(_factory, null, _binSut);

        var input = new PersistMinifigInput
        {
            BricklinkId = "arc007",
            Name = "Test",
            StorageBinId = bin.Id,
            RequiredParts = new List<PersistMinifigPart>
            {
                new() { BricklinkPartNo = "973", BricklinkColorId = 1,
                        PartName = "Torso", ColorName = "Red", QuantityNeeded = 1 }
            }
        };

        await Assert.ThrowsAsync<InvalidBinKindException>(
            () => persistence.PersistAndStoreAsync(input));
    }

    // ========================================================================
    // Status-Wechsel-Pfade (S23 / S24)
    // ========================================================================

    [Fact]
    public async Task CheckAndMarkComplete_updates_bin_kind_when_last_waiting_becomes_complete()
    {
        var bin = await _binSut.CreateSingleAsync("Box 01");
        var mId = await SeedMinifigInBinAsync(bin.Id, "w", TrackedMinifigStatus.Waiting);
        await SeedRequiredPartAsync(mId, "3001", 11, needed: 1, collected: 1);
        await _binSut.RecalculateKindAsync(bin.Id);
        Assert.Equal(StorageBinKind.Waiting, await GetKindAsync(bin.Id));

        var persistence = new MinifigPersistenceService(_factory, null, _binSut);
        var marked = await persistence.CheckAndMarkCompleteAsync(mId);

        Assert.True(marked);
        Assert.Equal(StorageBinKind.Complete, await GetKindAsync(bin.Id));
    }

    [Fact]
    public async Task CheckAndMarkComplete_keeps_bin_kind_waiting_when_other_waiting_remain()
    {
        var bin = await _binSut.CreateSingleAsync("Box 01");
        var m1 = await SeedMinifigInBinAsync(bin.Id, "w1", TrackedMinifigStatus.Waiting);
        await SeedRequiredPartAsync(m1, "3001", 11, needed: 1, collected: 1);
        var m2 = await SeedMinifigInBinAsync(bin.Id, "w2", TrackedMinifigStatus.Waiting);
        await SeedRequiredPartAsync(m2, "973", 1, needed: 2, collected: 0);
        await _binSut.RecalculateKindAsync(bin.Id);

        var persistence = new MinifigPersistenceService(_factory, null, _binSut);
        await persistence.CheckAndMarkCompleteAsync(m1);

        // Eine wartende bleibt -> Kind muss Waiting bleiben.
        Assert.Equal(StorageBinKind.Waiting, await GetKindAsync(bin.Id));
    }

    [Fact]
    public async Task Reopen_updates_bin_kind_to_waiting_when_complete_reopens()
    {
        var bin = await _binSut.CreateSingleAsync("Box 01");
        var mId = await SeedMinifigInBinAsync(bin.Id, "done", TrackedMinifigStatus.Complete);
        await _binSut.RecalculateKindAsync(bin.Id);
        Assert.Equal(StorageBinKind.Complete, await GetKindAsync(bin.Id));

        var persistence = new MinifigPersistenceService(_factory, null, _binSut);
        await persistence.ReopenAsync(mId);

        Assert.Equal(StorageBinKind.Waiting, await GetKindAsync(bin.Id));
    }

    // ========================================================================
    // Backfill-Logik (entspricht App.xaml.cs::BackfillStorageBinKindAsync,
    // hier ueber RecalculateKindAsync getestet)
    // ========================================================================

    [Fact]
    public async Task Backfill_classifies_existing_bins_correctly()
    {
        // Vier Bins mit unterschiedlichem Inhalt; Kind ist beim Anlegen
        // Empty (Default). RecalculateKindAsync simuliert den Backfill-Loop.
        var empty = await _binSut.CreateSingleAsync("Box 01");
        var floating = await _binSut.CreateSingleAsync("Box 02");
        await SeedFloatingInBinAsync(floating.Id, "3001", 1);
        var waiting = await _binSut.CreateSingleAsync("Box 03");
        await SeedMinifigInBinAsync(waiting.Id, "w", TrackedMinifigStatus.Waiting);
        var complete = await _binSut.CreateSingleAsync("Box 04");
        await SeedMinifigInBinAsync(complete.Id, "c", TrackedMinifigStatus.Complete);

        foreach (var b in new[] { empty, floating, waiting, complete })
            await _binSut.RecalculateKindAsync(b.Id);

        Assert.Equal(StorageBinKind.Empty, await GetKindAsync(empty.Id));
        Assert.Equal(StorageBinKind.Floating, await GetKindAsync(floating.Id));
        Assert.Equal(StorageBinKind.Waiting, await GetKindAsync(waiting.Id));
        Assert.Equal(StorageBinKind.Complete, await GetKindAsync(complete.Id));
    }

    [Fact]
    public async Task Backfill_classifies_mix_bins_as_waiting()
    {
        // Mix Wartend+Floating: durch Anomalie waere das Floating, aber
        // Reifungs-zentrische Backfill-Logik klassifiziert als Waiting
        // (siehe App.xaml.cs::BackfillStorageBinKindAsync Reifungs-zentrisch
        // + Service.RecalculateKindAsync gleiche Regel).
        var bin = await _binSut.CreateSingleAsync("Box 01");
        await SeedMinifigInBinAsync(bin.Id, "w", TrackedMinifigStatus.Waiting);
        await SeedFloatingInBinAsync(bin.Id, "3001", 2);

        await _binSut.RecalculateKindAsync(bin.Id);

        Assert.Equal(StorageBinKind.Waiting, await GetKindAsync(bin.Id));
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private async Task<StorageBinKind> GetKindAsync(int binId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = await ctx.StorageBins.AsNoTracking().SingleAsync(b => b.Id == binId);
        return bin.Kind;
    }

    private async Task<int> SeedMinifigInBinAsync(int binId, string blId, TrackedMinifigStatus status)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var m = new TrackedMinifig
        {
            BricklinkId = blId,
            FigNum = blId,
            Name = blId,
            CreatedAt = DateTime.UtcNow,
            Status = status,
            StorageBinId = binId
        };
        ctx.TrackedMinifigs.Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    private async Task SeedRequiredPartAsync(int minifigId, string partNo, int colorId,
        int needed, int collected)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.TrackedMinifigParts.Add(new TrackedMinifigPart
        {
            TrackedMinifigId = minifigId,
            PartNumber = partNo,
            ColorId = colorId,
            PartName = partNo,
            ColorName = $"Color {colorId}",
            QuantityNeeded = needed,
            QuantityCollected = collected
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedFloatingInBinAsync(int binId, string partNo, int qty)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.FloatingParts.Add(new FloatingPart
        {
            PartNumber = partNo,
            ColorId = 0,
            ColorName = "Black",
            PartName = partNo,
            Quantity = qty,
            StorageBinId = binId,
            AddedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    private PartLookupService MakePartLookupSut()
    {
        // Minimaler PartLookupService fuer Strict-Mode-Tests. Catalog +
        // ImageProvider werden nicht aufgerufen im AddPartToFloating-Pfad.
        var persistence = new MinifigPersistenceService(_factory, null, _binSut);
        return new PartLookupService(
            _factory,
            catalog: new NotImplementedCatalog(),
            persistence: persistence,
            imageProvider: new NotImplementedImageProvider(),
            cache: null,
            binService: _binSut);
    }

    // NotImplemented-Stubs (Pflicht-Interface-Methoden, werden aber im
    // AddPartToFloating-Pfad nicht aufgerufen).
    private sealed class NotImplementedCatalog : IBlCatalogService
    {
        public Task<BlItem?> GetMinifigDetailsAsync(string blMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlSubset>> GetMinifigPartsAsync(string blMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BlItem?> GetPartDetailsAsync(string blPartNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> FindWaitingMinifigsForPartAsync(string blPartNo, int blColorId, IEnumerable<string> waitingMinifigIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BlCacheStats> GetCacheStatsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearCacheAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearStaleAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlSubset>> GetSupersetsAsync(string blPartNo, int blColorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> EnsureFullSubsetsAsync(string blMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(string blPartNo, int blColorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlColor>> GetKnownColorsAsync(string blPartNo, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class NotImplementedImageProvider : IPartImageProvider
    {
        public Task<string> GetImageFileAsync(BrickognizeItem item, int? bricklinkColorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetImageFileByBlAsync(string bricklinkType, string bricklinkId, int? bricklinkColorId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }
}
