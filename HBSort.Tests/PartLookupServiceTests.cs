using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// Audit W-6: Tests fuer PartLookupService.
///
/// Schwerpunkte:
/// - AddPartToFloatingAsync: Stack-Verhalten (gleiches Teil + Farbe + Bin
///   wird summiert, statt neuen Eintrag anzulegen).
/// - AssignPartToMinifigAsync: QuantityCollected steigt, Auto-Complete bei
///   letztem Part.
/// - UnassignPartFromMinifigAsync: QuantityCollected zurueck auf 0, Status-
///   Reset bei Complete -> Waiting.
/// - FindFloatingLocationsAsync: Sortierung nach Quantity desc.
/// - DeleteFloatingPartAsync: Eintrag weg, true zurueck.
///
/// LookupPartAsync und CollectMinifigFromSupersetAsync sind nicht abgedeckt
/// weil sie IBlCatalogService brauchen - das Stub-Setup waere disproportional.
/// Beide laufen nicht durch geaenderte Code-Pfade dieser Iteration.
/// </summary>
public class PartLookupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly PartLookupService _sut;
    private readonly StubPersistence _persistence = new();

    public PartLookupServiceTests()
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
        _sut = new PartLookupService(_factory, new NotImplementedCatalog(), _persistence, new NotImplementedImageProvider());
    }

    public void Dispose() => _connection.Dispose();

    // ====================================================================
    // AddPartToFloatingAsync
    // ====================================================================

    [Fact]
    public async Task AddPartToFloating_creates_new_entry_when_no_match()
    {
        var binId = await SeedBinAsync("Box 01");

        var fp = await _sut.AddPartToFloatingAsync("3001", 11, "Brick 2x4", "Black",
            quantity: 3, storageBinId: binId);

        Assert.NotNull(fp);
        Assert.Equal(3, fp.Quantity);

        await using var ctx = await _factory.CreateDbContextAsync();
        var entries = await ctx.FloatingParts.ToListAsync();
        Assert.Single(entries);
        Assert.Equal("3001", entries[0].PartNumber);
        Assert.Equal(11, entries[0].ColorId);
        Assert.Equal(3, entries[0].Quantity);
    }

    [Fact]
    public async Task AddPartToFloating_stacks_on_existing_match_in_same_bin()
    {
        // Smart-Storage-Verhalten: gleiches (PartNo, Color, Bin) -> Quantity
        // wird auf den bestehenden Eintrag addiert.
        var binId = await SeedBinAsync("Box 02");
        await SeedFloatingAsync(binId, "3001", 11, qty: 5);

        var fp = await _sut.AddPartToFloatingAsync("3001", 11, "Brick 2x4", "Black",
            quantity: 2, storageBinId: binId);

        Assert.Equal(7, fp.Quantity); // 5 + 2

        await using var ctx = await _factory.CreateDbContextAsync();
        var entries = await ctx.FloatingParts.ToListAsync();
        Assert.Single(entries); // kein neuer Eintrag, Stack
    }

    [Fact]
    public async Task AddPartToFloating_creates_separate_entry_in_different_bin()
    {
        // Anderes Fach -> trotz gleichem (PartNo, Color) ein neuer Eintrag.
        var binA = await SeedBinAsync("Box A");
        var binB = await SeedBinAsync("Box B");
        await SeedFloatingAsync(binA, "3001", 11, qty: 5);

        await _sut.AddPartToFloatingAsync("3001", 11, "Brick 2x4", "Black", 2, binB);

        await using var ctx = await _factory.CreateDbContextAsync();
        var entries = await ctx.FloatingParts.OrderBy(f => f.StorageBinId).ToListAsync();
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task AddPartToFloating_throws_on_invalid_quantity()
    {
        var binId = await SeedBinAsync("Box 01");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.AddPartToFloatingAsync("3001", 11, "", "", 0, binId));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.AddPartToFloatingAsync("3001", 11, "", "", -1, binId));
    }

    [Fact]
    public async Task AddPartToFloating_throws_on_missing_bin()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.AddPartToFloatingAsync("3001", 11, "", "", 1, storageBinId: 9999));
    }

    [Fact]
    public async Task AddPartToFloating_raises_DataChanged()
    {
        var binId = await SeedBinAsync("Box 01");
        var beforeRaises = _persistence.RaiseCount;

        await _sut.AddPartToFloatingAsync("3001", 11, "", "", 1, binId);

        Assert.True(_persistence.RaiseCount > beforeRaises);
    }

    // ====================================================================
    // AssignPartToMinifigAsync
    // ====================================================================

    [Fact]
    public async Task AssignPartToMinifig_increments_quantity_collected()
    {
        var binId = await SeedBinAsync("Box 01");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 3, qtyCollected: 1);

        var becameComplete = await _sut.AssignPartToMinifigAsync(partId);

        Assert.False(becameComplete);
        await using var ctx = await _factory.CreateDbContextAsync();
        var part = await ctx.TrackedMinifigParts.SingleAsync(p => p.Id == partId);
        Assert.Equal(2, part.QuantityCollected); // 1 -> 2
    }

    [Fact]
    public async Task AssignPartToMinifig_caps_at_quantity_needed_and_marks_complete()
    {
        // Letztes fehlendes Teil -> Figur wird Complete.
        var binId = await SeedBinAsync("Box 01");
        var (minifigId, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 1, qtyCollected: 0);

        var becameComplete = await _sut.AssignPartToMinifigAsync(partId);

        Assert.True(becameComplete);
        await using var ctx = await _factory.CreateDbContextAsync();
        var minifig = await ctx.TrackedMinifigs.SingleAsync(m => m.Id == minifigId);
        Assert.Equal(TrackedMinifigStatus.Complete, minifig.Status);
        Assert.NotNull(minifig.CompletedAt);
    }

    [Fact]
    public async Task AssignPartToMinifig_returns_false_when_already_full()
    {
        // QuantityCollected == QuantityNeeded -> nichts zu tun.
        var binId = await SeedBinAsync("Box 01");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 1, qtyCollected: 1);

        var becameComplete = await _sut.AssignPartToMinifigAsync(partId);

        Assert.False(becameComplete);
    }

    // ====================================================================
    // UnassignPartFromMinifigAsync
    // ====================================================================

    [Fact]
    public async Task UnassignPart_resets_quantity_to_zero()
    {
        var binId = await SeedBinAsync("Box 01");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 3, qtyCollected: 2);

        var changed = await _sut.UnassignPartFromMinifigAsync(partId);

        Assert.True(changed);
        await using var ctx = await _factory.CreateDbContextAsync();
        var part = await ctx.TrackedMinifigParts.SingleAsync(p => p.Id == partId);
        Assert.Equal(0, part.QuantityCollected);
    }

    [Fact]
    public async Task UnassignPart_reverts_complete_to_waiting()
    {
        // Figur ist Complete (alle Parts voll). Wir nehmen ein Part-Slot
        // zurueck -> Status muss auf Waiting zurueck.
        var binId = await SeedBinAsync("Box 01");
        var (minifigId, partId) = await SeedCompleteMinifigAsync(binId, "arc007", "3001", 11);

        var changed = await _sut.UnassignPartFromMinifigAsync(partId);

        Assert.True(changed);
        await using var ctx = await _factory.CreateDbContextAsync();
        var minifig = await ctx.TrackedMinifigs.SingleAsync(m => m.Id == minifigId);
        Assert.Equal(TrackedMinifigStatus.Waiting, minifig.Status);
        Assert.Null(minifig.CompletedAt);
    }

    [Fact]
    public async Task UnassignPart_returns_false_when_nothing_collected()
    {
        var binId = await SeedBinAsync("Box 01");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 3, qtyCollected: 0);

        var changed = await _sut.UnassignPartFromMinifigAsync(partId);
        Assert.False(changed);
    }

    // ====================================================================
    // FindFloatingLocationsAsync + DeleteFloatingPartAsync
    // ====================================================================

    [Fact]
    public async Task FindFloatingLocations_sorts_by_quantity_desc()
    {
        var binA = await SeedBinAsync("Box A");
        var binB = await SeedBinAsync("Box B");
        var binC = await SeedBinAsync("Box C");
        await SeedFloatingAsync(binA, "3001", 11, qty: 2);
        await SeedFloatingAsync(binB, "3001", 11, qty: 7);
        await SeedFloatingAsync(binC, "3001", 11, qty: 4);

        var locations = await _sut.FindFloatingLocationsAsync("3001", 11);

        Assert.Equal(3, locations.Count);
        Assert.Equal(7, locations[0].TotalQuantity); // groesste zuerst
        Assert.Equal(4, locations[1].TotalQuantity);
        Assert.Equal(2, locations[2].TotalQuantity);
    }

    [Fact]
    public async Task DeleteFloatingPart_removes_entry()
    {
        var binId = await SeedBinAsync("Box 01");
        var fpId = await SeedFloatingAsync(binId, "3001", 11, qty: 3);

        var ok = await _sut.DeleteFloatingPartAsync(fpId);
        Assert.True(ok);

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.FloatingParts.ToListAsync());
    }

    [Fact]
    public async Task DeleteFloatingPart_returns_false_for_unknown_id()
    {
        var ok = await _sut.DeleteFloatingPartAsync(9999);
        Assert.False(ok);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private async Task<int> SeedBinAsync(string label)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = new StorageBin { Label = label, CreatedAt = DateTime.UtcNow };
        ctx.StorageBins.Add(bin);
        await ctx.SaveChangesAsync();
        return bin.Id;
    }

    private async Task<int> SeedFloatingAsync(int binId, string partNo, int colorId, int qty)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var fp = new FloatingPart
        {
            PartNumber = partNo,
            ColorId = colorId,
            Quantity = qty,
            StorageBinId = binId,
            AddedAt = DateTime.UtcNow
        };
        ctx.FloatingParts.Add(fp);
        await ctx.SaveChangesAsync();
        return fp.Id;
    }

    private async Task<(int MinifigId, int PartId)> SeedWaitingMinifigAsync(
        int binId, string blId, string partNo, int colorId,
        int qtyNeeded, int qtyCollected)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var minifig = new TrackedMinifig
        {
            FigNum = blId,
            BricklinkId = blId,
            Name = $"Test {blId}",
            CreatedAt = DateTime.UtcNow,
            Status = TrackedMinifigStatus.Waiting,
            StorageBinId = binId,
            RequiredParts = new List<TrackedMinifigPart>
            {
                new()
                {
                    PartNumber = partNo,
                    ColorId = colorId,
                    QuantityNeeded = qtyNeeded,
                    QuantityCollected = qtyCollected
                }
            }
        };
        ctx.TrackedMinifigs.Add(minifig);
        await ctx.SaveChangesAsync();
        return (minifig.Id, minifig.RequiredParts[0].Id);
    }

    private async Task<(int MinifigId, int PartId)> SeedCompleteMinifigAsync(
        int binId, string blId, string partNo, int colorId)
    {
        // QuantityNeeded=1, QuantityCollected=1 -> Status=Complete.
        await using var ctx = await _factory.CreateDbContextAsync();
        var minifig = new TrackedMinifig
        {
            FigNum = blId,
            BricklinkId = blId,
            Name = $"Test {blId}",
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            Status = TrackedMinifigStatus.Complete,
            StorageBinId = binId,
            RequiredParts = new List<TrackedMinifigPart>
            {
                new()
                {
                    PartNumber = partNo,
                    ColorId = colorId,
                    QuantityNeeded = 1,
                    QuantityCollected = 1
                }
            }
        };
        ctx.TrackedMinifigs.Add(minifig);
        await ctx.SaveChangesAsync();
        return (minifig.Id, minifig.RequiredParts[0].Id);
    }

    // ----- Stubs -----

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    private sealed class StubPersistence : IMinifigPersistenceService
    {
        public int RaiseCount { get; private set; }
        public event EventHandler? DataChanged;
        public void RaiseDataChanged() { RaiseCount++; DataChanged?.Invoke(this, EventArgs.Empty); }
        public Task<PersistMinifigResult> PersistAndStoreAsync(PersistMinifigInput input, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DismantleResult> DismantleAsync(int trackedMinifigId, IEnumerable<DismantlePartChoice> choices, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> CheckAndMarkCompleteAsync(int trackedMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReopenAsync(int trackedMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(int trackedMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> RemoveExportedMinifigsAsync(IEnumerable<int> minifigIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> RemoveExportedFloatingPartsAsync(IEnumerable<int> floatingPartIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CleanupOldDismantledMinifigsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CleanupOnePartCompletesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

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
}
