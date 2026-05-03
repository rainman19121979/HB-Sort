using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer FloatingPartTransferService. Wie die anderen Service-Tests:
/// In-Memory-SQLite + ein Stub-IMinifigPersistenceService (Service ruft nur
/// RaiseDataChanged auf - das koennen wir leicht beobachten).
/// </summary>
public class FloatingPartTransferServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly StubPersistence _persistence = new();
    private readonly FloatingPartTransferService _sut;

    public FloatingPartTransferServiceTests()
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
        _sut = new FloatingPartTransferService(_factory, _persistence);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task FindFirstMatch_returns_null_when_no_floatingpart_exists()
    {
        await SeedBinAsync("Box 01");
        var match = await _sut.FindFirstMatchAsync("3001", 0);
        Assert.Null(match);
    }

    [Fact]
    public async Task FindFirstMatch_returns_oldest_bin_when_multiple_match()
    {
        // Zwei Bins mit demselben Teil - Box 01 zuerst angelegt, Box 02 spaeter.
        var b1 = await SeedBinAsync("Box 01");
        var b2 = await SeedBinAsync("Box 02");
        await SeedFloatingAsync(b2, "3001", 0, qty: 5, addedAt: DateTime.UtcNow);
        await SeedFloatingAsync(b1, "3001", 0, qty: 3, addedAt: DateTime.UtcNow.AddDays(-1));

        var match = await _sut.FindFirstMatchAsync("3001", 0);

        Assert.NotNull(match);
        Assert.Equal("Box 01", match!.StorageBinLabel);
        Assert.Equal(3, match.QuantityAvailable);
    }

    [Fact]
    public async Task TransferOne_decrements_quantity_and_keeps_entry()
    {
        var b1 = await SeedBinAsync("Box 01");
        await SeedFloatingAsync(b1, "3001", 0, qty: 3);

        var result = await _sut.TransferOneAsync("3001", 0, "Test-Figur");

        Assert.True(result.Success);
        Assert.Equal("Box 01", result.SourceBinLabel);
        Assert.False(result.BinFreedAfterTransfer);

        await using var ctx = await _factory.CreateDbContextAsync();
        var fp = await ctx.FloatingParts.SingleAsync();
        Assert.Equal(2, fp.Quantity);
    }

    [Fact]
    public async Task TransferOne_deletes_entry_when_quantity_reaches_zero()
    {
        var b1 = await SeedBinAsync("Box 01");
        await SeedFloatingAsync(b1, "3001", 0, qty: 1);

        var result = await _sut.TransferOneAsync("3001", 0, "Test-Figur");

        Assert.True(result.Success);

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.FloatingParts.ToListAsync());
    }

    [Fact]
    public async Task TransferOne_releases_bin_when_it_becomes_empty()
    {
        // Bin hatte nur das Teil drin und keine wartende Figur -> nach Transfer leer
        // -> FreedAt muss gesetzt werden.
        var b1 = await SeedBinAsync("Box 01");
        await SeedFloatingAsync(b1, "3001", 0, qty: 1);

        var result = await _sut.TransferOneAsync("3001", 0, "Test-Figur");

        Assert.True(result.BinFreedAfterTransfer);

        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = await ctx.StorageBins.SingleAsync(b => b.Id == b1);
        Assert.NotNull(bin.FreedAt);
    }

    [Fact]
    public async Task TransferOne_does_NOT_release_bin_when_other_floatingparts_remain()
    {
        var b1 = await SeedBinAsync("Box 01");
        await SeedFloatingAsync(b1, "3001", 0, qty: 1);
        await SeedFloatingAsync(b1, "3024", 11, qty: 4); // anderes Teil bleibt

        var result = await _sut.TransferOneAsync("3001", 0, "Test-Figur");

        Assert.False(result.BinFreedAfterTransfer);

        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = await ctx.StorageBins.SingleAsync(b => b.Id == b1);
        Assert.Null(bin.FreedAt);
    }

    [Fact]
    public async Task TransferOne_does_NOT_release_bin_when_waiting_minifig_remains()
    {
        var b1 = await SeedBinAsync("Box 01");
        await SeedFloatingAsync(b1, "3001", 0, qty: 1);
        await SeedWaitingMinifigInBinAsync(b1, "arc007");

        var result = await _sut.TransferOneAsync("3001", 0, "Test-Figur");

        Assert.False(result.BinFreedAfterTransfer);

        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = await ctx.StorageBins.SingleAsync(b => b.Id == b1);
        Assert.Null(bin.FreedAt);
    }

    [Fact]
    public async Task TransferOne_returns_failure_when_no_match_exists()
    {
        await SeedBinAsync("Box 01");
        var result = await _sut.TransferOneAsync("3001", 0, "Test-Figur");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task TransferOne_writes_scan_event_with_FloatingPartTransfer_type()
    {
        var b1 = await SeedBinAsync("Box 01");
        await SeedFloatingAsync(b1, "3001", 0, qty: 1);

        await _sut.TransferOneAsync("3001", 0, "Arctic-Forscher");

        await using var ctx = await _factory.CreateDbContextAsync();
        var ev = await ctx.ScanEvents.SingleAsync();
        Assert.Equal(ScanType.FloatingPartTransfer, ev.Type);
        Assert.Equal("3001", ev.RecognizedId);
        Assert.Contains("Box 01", ev.ResultDescription);
        Assert.Contains("Arctic-Forscher", ev.ResultDescription);
    }

    [Fact]
    public async Task TransferOne_raises_DataChanged_via_persistence_service()
    {
        var b1 = await SeedBinAsync("Box 01");
        await SeedFloatingAsync(b1, "3001", 0, qty: 1);

        await _sut.TransferOneAsync("3001", 0, "Test-Figur");

        Assert.Equal(1, _persistence.DataChangedCount);
    }

    // ---- Helpers ----

    private async Task<int> SeedBinAsync(string label)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = new StorageBin
        {
            Label = label,
            CreatedAt = DateTime.UtcNow
        };
        ctx.StorageBins.Add(bin);
        await ctx.SaveChangesAsync();
        return bin.Id;
    }

    private async Task SeedFloatingAsync(
        int binId, string partNo, int colorId, int qty, DateTime? addedAt = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.FloatingParts.Add(new FloatingPart
        {
            PartNumber = partNo,
            ColorId = colorId,
            ColorName = $"Color {colorId}",
            PartName = partNo,
            Quantity = qty,
            StorageBinId = binId,
            AddedAt = addedAt ?? DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedWaitingMinifigInBinAsync(int binId, string blId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.TrackedMinifigs.Add(new TrackedMinifig
        {
            BricklinkId = blId,
            FigNum = blId,
            Name = blId,
            CreatedAt = DateTime.UtcNow,
            Status = TrackedMinifigStatus.Waiting,
            StorageBinId = binId
        });
        await ctx.SaveChangesAsync();
    }

    private sealed class StubPersistence : IMinifigPersistenceService
    {
        public int DataChangedCount { get; private set; }
        public event EventHandler? DataChanged;

        public void RaiseDataChanged()
        {
            DataChangedCount++;
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<PersistMinifigResult> PersistAndStoreAsync(PersistMinifigInput input, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<DismantleResult> DismantleAsync(int trackedMinifigId, IEnumerable<DismantlePartChoice> choices, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> DeleteAsync(int trackedMinifigId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> CleanupOldDismantledMinifigsAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> CleanupOnePartCompletesAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> CheckAndMarkCompleteAsync(int trackedMinifigId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> ReopenAsync(int trackedMinifigId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> RemoveExportedMinifigsAsync(IEnumerable<int> minifigIds, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }
}
