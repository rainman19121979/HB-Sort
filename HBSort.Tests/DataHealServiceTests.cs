using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// UX X.28 (2026-05-08): Tests fuer DataHealService.
/// Heal beim App-Start korrigiert Bug-B-Folgen aus alten Versionen.
/// </summary>
public class DataHealServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly DataHealService _sut;

    public DataHealServiceTests()
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
        _sut = new DataHealService(_factory);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Heal_on_empty_db_returns_zero()
    {
        var result = await _sut.HealAsync();
        Assert.Equal(0, result.RestoredBinAssignments);
        Assert.Equal(0, result.ResetFreedAtCount);
    }

    [Fact]
    public async Task Heal_on_clean_db_with_no_inconsistency_returns_zero()
    {
        // Arrange: Bin + wartende Minifig drin (Bin ist korrekt belegt, FreedAt=null)
        var binId = await SeedBinAsync("CleanBox", freedAt: null);
        await SeedMinifigAsync("clean001", binId, TrackedMinifigStatus.Waiting);

        var result = await _sut.HealAsync();

        Assert.Equal(0, result.ResetFreedAtCount);

        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = await ctx.StorageBins.SingleAsync(b => b.Id == binId);
        Assert.Null(bin.FreedAt);
    }

    [Fact]
    public async Task Heal_resets_FreedAt_when_bin_has_minifig()
    {
        // Arrange: Bin mit FreedAt=in-vergangenheit + Minifig drin
        var binId = await SeedBinAsync("InconsistentBox", freedAt: DateTime.UtcNow.AddDays(-3));
        await SeedMinifigAsync("incons001", binId, TrackedMinifigStatus.Complete);

        var result = await _sut.HealAsync();

        Assert.Equal(1, result.ResetFreedAtCount);

        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = await ctx.StorageBins.SingleAsync(b => b.Id == binId);
        Assert.Null(bin.FreedAt);
    }

    [Fact]
    public async Task Heal_resets_FreedAt_when_bin_has_floating_part()
    {
        // Arrange: Bin mit FreedAt=in-vergangenheit + FloatingPart drin
        var binId = await SeedBinAsync("FloatBox", freedAt: DateTime.UtcNow.AddDays(-1));
        await SeedFloatingAsync(binId, "3001", 11, qty: 5);

        var result = await _sut.HealAsync();

        Assert.Equal(1, result.ResetFreedAtCount);

        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = await ctx.StorageBins.SingleAsync(b => b.Id == binId);
        Assert.Null(bin.FreedAt);
    }

    [Fact]
    public async Task Heal_does_not_touch_truly_free_bins()
    {
        // Arrange: Bin mit FreedAt=set, aber WIRKLICH leer (keine Items drin)
        var freedAt = DateTime.UtcNow.AddDays(-5);
        var binId = await SeedBinAsync("FreedBox", freedAt: freedAt);

        var result = await _sut.HealAsync();

        Assert.Equal(0, result.ResetFreedAtCount);

        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = await ctx.StorageBins.SingleAsync(b => b.Id == binId);
        // FreedAt bleibt unveraendert - Bin ist tatsaechlich frei.
        Assert.NotNull(bin.FreedAt);
    }

    [Fact]
    public async Task Heal_is_idempotent()
    {
        var binId = await SeedBinAsync("IdempBox", freedAt: DateTime.UtcNow.AddDays(-2));
        await SeedMinifigAsync("idemp001", binId, TrackedMinifigStatus.Complete);

        // Erster Lauf heilt
        var first = await _sut.HealAsync();
        Assert.Equal(1, first.ResetFreedAtCount);

        // Zweiter Lauf darf nichts mehr finden
        var second = await _sut.HealAsync();
        Assert.Equal(0, second.ResetFreedAtCount);
    }

    // ====================================================================
    // UX X.30 (v0.1.17): Auto-Heal von Bug-A-Folgen via ScanEvent-History
    // ====================================================================

    [Fact]
    public async Task Heal_restores_orphan_complete_from_move_event()
    {
        // Setup: orphaned Complete-Figur + Move-Event mit Snapshot
        // (Snapshot.NewStorageBinId zeigt die letzte bekannte Position).
        var bin = await SeedBinAsync("Restored", freedAt: null);
        var minifigId = await SeedOrphanCompleteAsync("cty007");

        // Move-Event: Figur wurde zuletzt nach `bin` verschoben (NewStorageBinId=bin).
        await SeedMoveScanEventAsync("cty007", minifigId, oldBin: 999, newBin: bin);

        var result = await _sut.HealAsync();
        Assert.Equal(1, result.RestoredBinAssignments);

        await using var ctx = await _factory.CreateDbContextAsync();
        var m = await ctx.TrackedMinifigs.SingleAsync(x => x.Id == minifigId);
        Assert.Equal(bin, m.StorageBinId);
    }

    [Fact]
    public async Task Heal_does_not_restore_when_target_bin_no_longer_exists()
    {
        var minifigId = await SeedOrphanCompleteAsync("cty008");
        // Move-Event verweist auf Bin 999 - existiert nicht.
        await SeedMoveScanEventAsync("cty008", minifigId, oldBin: 998, newBin: 999);

        var result = await _sut.HealAsync();
        Assert.Equal(0, result.RestoredBinAssignments);

        await using var ctx = await _factory.CreateDbContextAsync();
        var m = await ctx.TrackedMinifigs.SingleAsync(x => x.Id == minifigId);
        Assert.Null(m.StorageBinId);
    }

    [Fact]
    public async Task Heal_does_not_restore_when_no_history_available()
    {
        // Orphan ohne ScanEvents -> Warnung, kein Heal.
        var minifigId = await SeedOrphanCompleteAsync("cty009");
        var result = await _sut.HealAsync();
        Assert.Equal(0, result.RestoredBinAssignments);
    }

    [Fact]
    public async Task Heal_uses_newest_move_event_when_multiple_exist()
    {
        // Setup: zwei Move-Events fuer die gleiche Figur, der neueste
        // zeigt nach binFinal.
        var binEarly = await SeedBinAsync("Early");
        var binFinal = await SeedBinAsync("Final");
        var minifigId = await SeedOrphanCompleteAsync("cty010");

        await SeedMoveScanEventAsync("cty010", minifigId, oldBin: 998, newBin: binEarly,
            timestamp: DateTime.UtcNow.AddHours(-2));
        await SeedMoveScanEventAsync("cty010", minifigId, oldBin: binEarly, newBin: binFinal,
            timestamp: DateTime.UtcNow.AddMinutes(-5));

        var result = await _sut.HealAsync();
        Assert.Equal(1, result.RestoredBinAssignments);

        await using var ctx = await _factory.CreateDbContextAsync();
        var m = await ctx.TrackedMinifigs.SingleAsync(x => x.Id == minifigId);
        Assert.Equal(binFinal, m.StorageBinId);
    }

    [Fact]
    public async Task Heal_falls_back_to_delete_event_snapshot()
    {
        // Setup: kein Move-Event, aber ein Delete-Event mit StorageBinId
        // im Snapshot (Figur wurde geloescht + restored).
        var bin = await SeedBinAsync("FromDelete");
        var minifigId = await SeedOrphanCompleteAsync("cty011");

        var snap = new HBSort.Core.Services.UndoSnapshotMinifigDelete
        {
            OriginalMinifigId = minifigId,
            BricklinkId = "cty011",
            FigNum = "cty011",
            Name = "Test",
            Status = "Complete",
            StorageBinId = bin
        };
        await SeedScanEventRawAsync(ScanType.Delete, "cty011",
            System.Text.Json.JsonSerializer.Serialize(snap));

        var result = await _sut.HealAsync();
        Assert.Equal(1, result.RestoredBinAssignments);

        await using var ctx = await _factory.CreateDbContextAsync();
        var m = await ctx.TrackedMinifigs.SingleAsync(x => x.Id == minifigId);
        Assert.Equal(bin, m.StorageBinId);
    }

    [Fact]
    public async Task Heal_handles_multiple_bins_in_one_pass()
    {
        var bin1 = await SeedBinAsync("Multi1", freedAt: DateTime.UtcNow.AddDays(-1));
        var bin2 = await SeedBinAsync("Multi2", freedAt: DateTime.UtcNow.AddDays(-2));
        var bin3 = await SeedBinAsync("Multi3", freedAt: DateTime.UtcNow.AddDays(-3));

        // bin1 hat eine Minifig, bin2 hat ein FloatingPart, bin3 ist tatsaechlich leer
        await SeedMinifigAsync("multi001", bin1, TrackedMinifigStatus.Complete);
        await SeedFloatingAsync(bin2, "3001", 11, qty: 2);

        var result = await _sut.HealAsync();

        Assert.Equal(2, result.ResetFreedAtCount);

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Null((await ctx.StorageBins.SingleAsync(b => b.Id == bin1)).FreedAt);
        Assert.Null((await ctx.StorageBins.SingleAsync(b => b.Id == bin2)).FreedAt);
        Assert.NotNull((await ctx.StorageBins.SingleAsync(b => b.Id == bin3)).FreedAt);
    }

    // ---- Helpers ----

    private async Task<int> SeedBinAsync(string label, DateTime? freedAt = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = new StorageBin
        {
            Label = label,
            CreatedAt = DateTime.UtcNow,
            FreedAt = freedAt
        };
        ctx.StorageBins.Add(bin);
        await ctx.SaveChangesAsync();
        return bin.Id;
    }

    private async Task<int> SeedMinifigAsync(string blId, int binId, TrackedMinifigStatus status)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var m = new TrackedMinifig
        {
            FigNum = blId,
            BricklinkId = blId,
            Name = $"Test {blId}",
            CreatedAt = DateTime.UtcNow,
            Status = status,
            CompletedAt = status == TrackedMinifigStatus.Complete ? DateTime.UtcNow : null,
            StorageBinId = binId
        };
        ctx.TrackedMinifigs.Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    private async Task<int> SeedFloatingAsync(int binId, string partNo, int colorId, int qty)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var fp = new FloatingPart
        {
            PartNumber = partNo,
            ColorId = colorId,
            Quantity = qty,
            PartName = string.Empty,
            ColorName = string.Empty,
            StorageBinId = binId,
            AddedAt = DateTime.UtcNow
        };
        ctx.FloatingParts.Add(fp);
        await ctx.SaveChangesAsync();
        return fp.Id;
    }

    /// <summary>UX X.30 Helper: Orphan-Complete-Figur ohne Bin anlegen.</summary>
    private async Task<int> SeedOrphanCompleteAsync(string blId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var m = new TrackedMinifig
        {
            FigNum = blId,
            BricklinkId = blId,
            Name = $"Orphan {blId}",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            CompletedAt = DateTime.UtcNow.AddHours(-1),
            Status = TrackedMinifigStatus.Complete,
            StorageBinId = null
        };
        ctx.TrackedMinifigs.Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    /// <summary>UX X.30 Helper: Move-ScanEvent mit UndoSnapshotMove anlegen.</summary>
    private async Task SeedMoveScanEventAsync(string blId, int minifigId,
        int oldBin, int newBin, DateTime? timestamp = null)
    {
        var snap = new HBSort.Core.Services.UndoSnapshotMove
        {
            MinifigId = minifigId,
            OldStorageBinId = oldBin,
            NewStorageBinId = newBin
        };
        await SeedScanEventRawAsync(ScanType.Move, blId,
            System.Text.Json.JsonSerializer.Serialize(snap), timestamp);
    }

    private async Task SeedScanEventRawAsync(ScanType type, string recognizedId,
        string? undoData, DateTime? timestamp = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = timestamp ?? DateTime.UtcNow,
            Type = type,
            RecognizedId = recognizedId,
            ResultDescription = "Test",
            WasUndone = false,
            UndoData = undoData
        });
        await ctx.SaveChangesAsync();
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }
}
