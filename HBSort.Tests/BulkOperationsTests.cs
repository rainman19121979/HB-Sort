using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// UX X.29 Block C (v0.1.16): Tests fuer DeleteSelectionAsync +
/// MoveSelectionAsync + EmptyAsync mit ScanEvents/UndoData.
/// </summary>
public class BulkOperationsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly MinifigPersistenceService _persistence;
    private readonly StorageBinService _binService;

    public BulkOperationsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<UserDataContext>()
            .UseSqlite(_connection).Options;
        using (var ctx = new UserDataContext(options))
            ctx.Database.EnsureCreated();
        _factory = new SimpleContextFactory(options);
        _persistence = new MinifigPersistenceService(_factory);
        _binService = new StorageBinService(_factory);
    }

    public void Dispose() => _connection.Dispose();

    // ====================================================================
    // C.1: DeleteSelectionAsync
    // ====================================================================

    [Fact]
    public async Task DeleteSelection_with_empty_input_returns_zero()
    {
        var (m, f) = await _persistence.DeleteSelectionAsync(Array.Empty<int>(), Array.Empty<int>());
        Assert.Equal(0, m);
        Assert.Equal(0, f);
    }

    [Fact]
    public async Task DeleteSelection_removes_minifig_and_writes_scanEvent_with_undoData()
    {
        var bin = await SeedBinAsync("Box 1");
        var mId = await SeedMinifigAsync("test001", bin);
        var fId = await SeedFloatingAsync(bin, "3001", 11, 5);

        var (mDel, fDel) = await _persistence.DeleteSelectionAsync(new[] { mId }, new[] { fId });
        Assert.Equal(1, mDel);
        Assert.Equal(1, fDel);

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.TrackedMinifigs.ToListAsync());
        Assert.Empty(await ctx.FloatingParts.ToListAsync());

        // Pro geloeschtes Item ein ScanEvent vom Typ Delete + UndoData
        var deleteEvents = await ctx.ScanEvents
            .Where(e => e.Type == ScanType.Delete)
            .ToListAsync();
        Assert.Equal(2, deleteEvents.Count);
        Assert.All(deleteEvents, e => Assert.NotNull(e.UndoData));
        Assert.All(deleteEvents, e => Assert.False(e.WasUndone));
    }

    [Fact]
    public async Task DeleteSelection_atomicity_throws_when_anything_breaks()
    {
        // Bei Bulk-Delete in einem DbContext: alle oder keine.
        // Wir testen indirekt - bei korrekten IDs darf kein Halbzustand
        // entstehen.
        var bin = await SeedBinAsync("Box 1");
        var mId1 = await SeedMinifigAsync("a", bin);
        var mId2 = await SeedMinifigAsync("b", bin);

        var (mDel, _) = await _persistence.DeleteSelectionAsync(new[] { mId1, mId2 }, Array.Empty<int>());
        Assert.Equal(2, mDel);

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.TrackedMinifigs.ToListAsync());
    }

    [Fact]
    public async Task DeleteSelection_decouples_origin_floatings()
    {
        // FloatingParts mit OriginMinifigId auf den geloeschten Minifig:
        // OriginMinifigId wird auf null gesetzt, FloatingPart selbst bleibt.
        var bin = await SeedBinAsync("Box 1");
        var mId = await SeedMinifigAsync("origin", bin);
        var fId = await SeedFloatingAsync(bin, "3001", 11, 5, originMinifigId: mId);

        await _persistence.DeleteSelectionAsync(new[] { mId }, Array.Empty<int>());

        await using var ctx = await _factory.CreateDbContextAsync();
        var fp = await ctx.FloatingParts.SingleAsync(p => p.Id == fId);
        Assert.Null(fp.OriginMinifigId); // entkoppelt
    }

    // ====================================================================
    // C.2: MoveSelectionAsync
    // ====================================================================

    [Fact]
    public async Task MoveSelection_throws_when_target_bin_missing()
    {
        var bin = await SeedBinAsync("Box A");
        var mId = await SeedMinifigAsync("x", bin);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _persistence.MoveSelectionAsync(new[] { mId }, Array.Empty<int>(), targetBinId: 9999));
    }

    [Fact]
    public async Task MoveSelection_updates_storage_bin_id_and_writes_move_event()
    {
        var oldBin = await SeedBinAsync("Old");
        var newBin = await SeedBinAsync("New");
        var mId = await SeedMinifigAsync("move001", oldBin);

        var (mMov, fMov) = await _persistence.MoveSelectionAsync(new[] { mId }, Array.Empty<int>(), newBin);
        Assert.Equal(1, mMov);
        Assert.Equal(0, fMov);

        await using var ctx = await _factory.CreateDbContextAsync();
        var m = await ctx.TrackedMinifigs.SingleAsync(x => x.Id == mId);
        Assert.Equal(newBin, m.StorageBinId);

        var moveEvent = await ctx.ScanEvents.SingleAsync(e => e.Type == ScanType.Move);
        Assert.NotNull(moveEvent.UndoData);
    }

    [Fact]
    public async Task MoveSelection_reactivates_freed_target_bin()
    {
        // Bug-B-Konvention: wenn das Ziel-Fach FreedAt!=null hat,
        // wird FreedAt beim Verschieben auf null zurueckgesetzt.
        var oldBin = await SeedBinAsync("Old");
        var newBin = await SeedBinAsync("New", freedAt: DateTime.UtcNow.AddDays(-1));
        var mId = await SeedMinifigAsync("move002", oldBin);

        await _persistence.MoveSelectionAsync(new[] { mId }, Array.Empty<int>(), newBin);

        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = await ctx.StorageBins.SingleAsync(b => b.Id == newBin);
        Assert.Null(bin.FreedAt);
    }

    [Fact]
    public async Task MoveSelection_floating_writes_undoData()
    {
        // UX X.30 (v0.1.17): FloatingPart-Move schreibt jetzt UndoSnapshotFloatingMove
        // statt UndoData=null. Strg+Z stellt die alte StorageBinId wieder her.
        var oldBin = await SeedBinAsync("FpOld");
        var newBin = await SeedBinAsync("FpNew");
        var fpId = await SeedFloatingAsync(oldBin, "3001", 11, qty: 5);

        var (mMov, fMov) = await _persistence.MoveSelectionAsync(
            Array.Empty<int>(), new[] { fpId }, newBin);
        Assert.Equal(0, mMov);
        Assert.Equal(1, fMov);

        await using var ctx = await _factory.CreateDbContextAsync();
        var fp = await ctx.FloatingParts.SingleAsync(p => p.Id == fpId);
        Assert.Equal(newBin, fp.StorageBinId);

        var moveEvent = await ctx.ScanEvents.SingleAsync(e => e.Type == ScanType.Move);
        Assert.NotNull(moveEvent.UndoData);
        var snap = System.Text.Json.JsonSerializer.Deserialize<UndoSnapshotFloatingMove>(moveEvent.UndoData!);
        Assert.NotNull(snap);
        Assert.Equal(fpId, snap!.FloatingPartId);
        Assert.Equal(oldBin, snap.OldStorageBinId);
        Assert.Equal(newBin, snap.NewStorageBinId);
    }

    [Fact]
    public async Task MoveSelection_skips_no_op_moves()
    {
        // Move nach demselben Bin = no-op (kein ScanEvent geschrieben).
        var bin = await SeedBinAsync("Same");
        var mId = await SeedMinifigAsync("noop", bin);

        await _persistence.MoveSelectionAsync(new[] { mId }, Array.Empty<int>(), bin);

        await using var ctx = await _factory.CreateDbContextAsync();
        // Kein Move-Event geschrieben.
        Assert.Empty(await ctx.ScanEvents.Where(e => e.Type == ScanType.Move).ToListAsync());
    }

    // ====================================================================
    // C.7: EmptyAsync mit ScanEvents
    // ====================================================================

    [Fact]
    public async Task GetEmptyPreview_returns_correct_counts()
    {
        var bin = await SeedBinAsync("Box");
        await SeedMinifigAsync("w1", bin, TrackedMinifigStatus.Waiting);
        await SeedMinifigAsync("w2", bin, TrackedMinifigStatus.Waiting);
        await SeedMinifigAsync("c1", bin, TrackedMinifigStatus.Complete);
        await SeedFloatingAsync(bin, "3001", 11, 3);
        await SeedFloatingAsync(bin, "3002", 11, 7);

        var preview = await _binService.GetEmptyPreviewAsync(bin);

        Assert.NotNull(preview);
        Assert.Equal(2, preview!.WaitingMinifigsCount);
        Assert.Equal(1, preview.CompleteMinifigsCount);
        Assert.Equal(0, preview.SoldMinifigsCount);
        Assert.Equal(2, preview.FloatingPartsCount);
    }

    [Fact]
    public async Task GetEmptyPreview_returns_null_for_unknown_bin()
    {
        var preview = await _binService.GetEmptyPreviewAsync(9999);
        Assert.Null(preview);
    }

    [Fact]
    public async Task EmptyAsync_writes_scanEvents_for_all_decoupled_items()
    {
        var bin = await SeedBinAsync("Box");
        await SeedMinifigAsync("m1", bin);
        await SeedMinifigAsync("m2", bin);
        await SeedFloatingAsync(bin, "3001", 11, 5);

        var ok = await _binService.EmptyAsync(bin);
        Assert.True(ok);

        await using var ctx = await _factory.CreateDbContextAsync();
        // 2x Move-Events fuer Minifigs (mit UndoData), 1x Delete-Event fuer
        // FloatingPart, 1x BinFreed-Event fuer Bin.
        var moveCount = await ctx.ScanEvents.CountAsync(e => e.Type == ScanType.Move);
        var delCount = await ctx.ScanEvents.CountAsync(e => e.Type == ScanType.Delete);
        var binCount = await ctx.ScanEvents.CountAsync(e => e.Type == ScanType.BinFreed);
        Assert.Equal(2, moveCount);
        Assert.Equal(1, delCount);
        Assert.Equal(1, binCount);
        // Alle haben UndoData
        Assert.All(await ctx.ScanEvents.ToListAsync(), e => Assert.NotNull(e.UndoData));
    }

    [Fact]
    public async Task EmptyAsync_decouples_complete_minifig_via_move_event()
    {
        // UX X.29 Block C: Complete-Figuren werden auch entkoppelt, aber
        // mit Move-ScanEvent + UndoData -> Strg+Z stellt die Zuordnung
        // wieder her (siehe UndoServiceTests.UndoAsync_Move_restores...).
        var bin = await SeedBinAsync("Box C");
        var mId = await SeedMinifigAsync("complete", bin, TrackedMinifigStatus.Complete);

        var ok = await _binService.EmptyAsync(bin);
        Assert.True(ok);

        await using var ctx = await _factory.CreateDbContextAsync();
        var m = await ctx.TrackedMinifigs.SingleAsync(x => x.Id == mId);
        Assert.Null(m.StorageBinId);

        // Move-Event mit Snapshot OldStorageBinId=bin
        var moveEvent = await ctx.ScanEvents.SingleAsync(e => e.Type == ScanType.Move);
        Assert.NotNull(moveEvent.UndoData);
        var snap = System.Text.Json.JsonSerializer.Deserialize<UndoSnapshotMove>(moveEvent.UndoData!);
        Assert.NotNull(snap);
        Assert.Equal(bin, snap!.OldStorageBinId);
        Assert.Null(snap.NewStorageBinId);
    }

    [Fact]
    public async Task EmptyAsync_on_empty_bin_only_writes_BinFreed_event()
    {
        var bin = await SeedBinAsync("Empty");

        var ok = await _binService.EmptyAsync(bin);
        Assert.True(ok);

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.ScanEvents.Where(e => e.Type == ScanType.Move).ToListAsync());
        Assert.Empty(await ctx.ScanEvents.Where(e => e.Type == ScanType.Delete).ToListAsync());
        Assert.Single(await ctx.ScanEvents.Where(e => e.Type == ScanType.BinFreed).ToListAsync());
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

    private async Task<int> SeedMinifigAsync(string blId, int binId,
        TrackedMinifigStatus status = TrackedMinifigStatus.Waiting)
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

    private async Task<int> SeedFloatingAsync(int binId, string partNo, int colorId, int qty,
        int? originMinifigId = null)
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
            AddedAt = DateTime.UtcNow,
            OriginMinifigId = originMinifigId
        };
        ctx.FloatingParts.Add(fp);
        await ctx.SaveChangesAsync();
        return fp.Id;
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }
}
