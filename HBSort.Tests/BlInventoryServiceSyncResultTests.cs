using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Exceptions;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// v0.1.24-beta.13: Tests fuer den erweiterten Rueckgabetyp von
/// <see cref="BlInventoryService.SyncInventoryAsync"/>. Vorher lieferte
/// die Methode <c>Task&lt;int&gt;</c> (LotCount); seit beta.13 ist es
/// ein <see cref="BlInventorySyncResult"/> mit LotCount,
/// RestoredReservations, CappedReservations, LostReservations + SyncedAt.
///
/// <para>Der Result-Record wird vom <c>MassUpdateExportViewModel</c>
/// benoetigt, um beim Oeffnen des Mass-Update-Dialogs einen "X
/// Reservierung(en) angepasst"-Hinweis anzuzeigen, falls beim Snapshot-
/// Replace V6-Cap zugeschlagen hat (BL-Quantity gesunken, lokale
/// Reservierung >= neue Quantity → gecappt).</para>
/// </summary>
public class BlInventoryServiceSyncResultTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;

    public BlInventoryServiceSyncResultTests()
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
    }

    public void Dispose() => _connection.Dispose();

    // ====================================================================
    // Happy-Path: Sync ohne Reservierungen liefert klare Zahlen
    // ====================================================================

    [Fact]
    public async Task Sync_returns_LotCount_from_BL_response()
    {
        var client = new FakeBricklinkClient(new List<BricklinkInventoryLotDto>
        {
            new() { LotId = 1, ItemType = "P", ItemNo = "3001", ColorId = 11, Quantity = 5, Condition = "N" },
            new() { LotId = 2, ItemType = "P", ItemNo = "3001", ColorId = 4,  Quantity = 3, Condition = "U" }
        });
        var sut = new BlInventoryService(_factory, client);

        var result = await sut.SyncInventoryAsync();

        Assert.Equal(2, result.LotCount);
        Assert.Equal(0, result.RestoredReservations);
        Assert.Equal(0, result.CappedReservations);
        Assert.Equal(0, result.LostReservations);
        Assert.True(result.SyncedAt <= DateTime.UtcNow);
        Assert.True(result.SyncedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    // ====================================================================
    // Edge: Reservierung erhalten (Lot weiterhin in BL-Antwort, Quantity
    // unveraendert)
    // ====================================================================

    [Fact]
    public async Task Sync_restores_reservation_when_lot_unchanged()
    {
        // Seed: Lot 42 mit Reserved=2 von Quantity=10.
        await SeedLotAsync(lotId: 42, quantity: 10, reserved: 2);
        var client = new FakeBricklinkClient(new List<BricklinkInventoryLotDto>
        {
            new() { LotId = 42, ItemType = "P", ItemNo = "3001", ColorId = 11, Quantity = 10, Condition = "N" }
        });
        var sut = new BlInventoryService(_factory, client);

        var result = await sut.SyncInventoryAsync();

        Assert.Equal(1, result.LotCount);
        Assert.Equal(1, result.RestoredReservations);
        Assert.Equal(0, result.CappedReservations);
        Assert.Equal(0, result.LostReservations);

        // DB-Bestaetigung: ReservedQuantity ist wirklich 2 nach Sync.
        await using var ctx = await _factory.CreateDbContextAsync();
        var lot = await ctx.BlInventoryLots.FirstAsync(l => l.LotId == 42);
        Assert.Equal(2, lot.ReservedQuantity);
    }

    // ====================================================================
    // V6-Cap (Audit H4): BL-Quantity gesunken, Reservierung wird gecappt
    // ====================================================================

    [Fact]
    public async Task Sync_caps_reservation_when_BL_quantity_drops_below_reserved()
    {
        // Seed: Lot mit Reserved=5 von Quantity=10. BL-Antwort liefert
        // Quantity=3 (User hat 7 Stueck im BL-Shop verkauft). Erwartet:
        // ReservedQuantity wird auf 3 gecappt; CappedReservations=1.
        await SeedLotAsync(lotId: 7, quantity: 10, reserved: 5);
        var client = new FakeBricklinkClient(new List<BricklinkInventoryLotDto>
        {
            new() { LotId = 7, ItemType = "P", ItemNo = "3001", ColorId = 11, Quantity = 3, Condition = "N" }
        });
        var sut = new BlInventoryService(_factory, client);

        var result = await sut.SyncInventoryAsync();

        Assert.Equal(1, result.LotCount);
        Assert.Equal(1, result.RestoredReservations);
        Assert.Equal(1, result.CappedReservations);
        Assert.Equal(0, result.LostReservations);

        await using var ctx = await _factory.CreateDbContextAsync();
        var lot = await ctx.BlInventoryLots.FirstAsync(l => l.LotId == 7);
        Assert.Equal(3, lot.ReservedQuantity); // gecappt
    }

    // ====================================================================
    // Lot ausverkauft / aus BL geloescht → Reservierung geht verloren
    // ====================================================================

    [Fact]
    public async Task Sync_counts_lost_reservation_when_lot_disappears_from_BL()
    {
        // Seed: Lot mit Reserved>0, aber BL liefert das Lot nicht mehr.
        await SeedLotAsync(lotId: 99, quantity: 5, reserved: 2);
        var client = new FakeBricklinkClient(new List<BricklinkInventoryLotDto>()); // leer
        var sut = new BlInventoryService(_factory, client);

        var result = await sut.SyncInventoryAsync();

        Assert.Equal(0, result.LotCount);
        Assert.Equal(0, result.RestoredReservations);
        Assert.Equal(0, result.CappedReservations);
        Assert.Equal(1, result.LostReservations);
    }

    // ====================================================================
    // Auth-/RateLimit-Errors propagieren (UI macht den User-Toast)
    // ====================================================================

    [Fact]
    public async Task Sync_propagates_BricklinkAuthException()
    {
        var client = new ThrowingBricklinkClient(new BricklinkAuthException("no tokens"));
        var sut = new BlInventoryService(_factory, client);

        await Assert.ThrowsAsync<BricklinkAuthException>(() => sut.SyncInventoryAsync());
    }

    [Fact]
    public async Task Sync_propagates_BricklinkRateLimitException()
    {
        var client = new ThrowingBricklinkClient(new BricklinkRateLimitException("429"));
        var sut = new BlInventoryService(_factory, client);

        await Assert.ThrowsAsync<BricklinkRateLimitException>(() => sut.SyncInventoryAsync());
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private async Task SeedLotAsync(int lotId, int quantity, int reserved)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.BlInventoryLots.Add(new BlInventoryLot
        {
            LotId = lotId,
            ItemType = "P",
            ItemNo = "3001",
            ColorId = 11,
            Quantity = quantity,
            ReservedQuantity = reserved,
            UnitPrice = 0.1m,
            Condition = "N",
            LastSyncedAt = DateTime.UtcNow.AddDays(-1)
        });
        await ctx.SaveChangesAsync();
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    /// <summary>Fake-Client der eine vorbereitete Lot-Liste zurueckgibt.</summary>
    private sealed class FakeBricklinkClient : IBricklinkClient
    {
        private readonly List<BricklinkInventoryLotDto> _lots;
        public FakeBricklinkClient(List<BricklinkInventoryLotDto> lots) => _lots = lots;
        public Task<List<BricklinkInventoryLotDto>> GetInventoryAsync(CancellationToken ct = default)
            => Task.FromResult(_lots);

        // Restliche Methoden werden in den Sync-Tests nicht angefasst.
        public Task<bool> IsConfiguredAsync() => Task.FromResult(true);
        public Task<BricklinkTestResult> TestConnectionAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BricklinkItemDto> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkSubsetDto>> GetSubsetsAsync(string itemType, string itemNo, bool breakMinifigs, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkColorDto>> GetColorListAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkSupersetEntryDto>> GetSupersetsAsync(string itemType, string itemNo, int colorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkKnownColorDto>> GetKnownColorsAsync(string itemType, string itemNo, CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>Wirft beim Inventar-Call eine vorbestimmte Exception.</summary>
    private sealed class ThrowingBricklinkClient : IBricklinkClient
    {
        private readonly Exception _ex;
        public ThrowingBricklinkClient(Exception ex) => _ex = ex;
        public Task<List<BricklinkInventoryLotDto>> GetInventoryAsync(CancellationToken ct = default)
            => Task.FromException<List<BricklinkInventoryLotDto>>(_ex);

        public Task<bool> IsConfiguredAsync() => Task.FromResult(true);
        public Task<BricklinkTestResult> TestConnectionAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BricklinkItemDto> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkSubsetDto>> GetSubsetsAsync(string itemType, string itemNo, bool breakMinifigs, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkColorDto>> GetColorListAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkSupersetEntryDto>> GetSupersetsAsync(string itemType, string itemNo, int colorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkKnownColorDto>> GetKnownColorsAsync(string itemType, string itemNo, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
