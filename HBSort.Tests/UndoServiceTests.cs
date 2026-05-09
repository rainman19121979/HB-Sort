using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// UX X.29 (v0.1.16): Tests fuer UndoService.
///
/// Strategie: in-memory SQLite + EF, jeweils ein ScanEvent vom passenden
/// Type in der DB anlegen, dann UndoAsync aufrufen und Effekt verifizieren.
/// </summary>
public class UndoServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly UndoService _sut;
    private readonly TestPersistenceStub _persistence;

    public UndoServiceTests()
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
        _persistence = new TestPersistenceStub();
        _sut = new UndoService(_factory, _persistence);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetUndoableActions_returns_empty_on_fresh_db()
    {
        var list = await _sut.GetUndoableActionsAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task UndoAsync_returns_fail_when_event_not_found()
    {
        var result = await _sut.UndoAsync(scanEventId: 9999);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task UndoAsync_blocks_already_undone_event()
    {
        var eventId = await SeedScanEventAsync(ScanType.Delete,
            new UndoSnapshotMinifigDelete { OriginalMinifigId = 1, BricklinkId = "x", Name = "X" },
            wasUndone: true);

        var result = await _sut.UndoAsync(eventId);
        Assert.False(result.Success);
        Assert.Contains("bereits", result.ErrorMessage);
    }

    [Fact]
    public async Task UndoAsync_returns_fail_for_non_undoable_type()
    {
        var eventId = await SeedScanEventRawAsync(ScanType.MinifigScan,
            description: "Scan",
            undoData: null);

        var result = await _sut.UndoAsync(eventId);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task UndoAsync_DeleteMinifig_recreates_minifig_with_required_parts()
    {
        var snap = new UndoSnapshotMinifigDelete
        {
            OriginalMinifigId = 42,
            BricklinkId = "arc007",
            FigNum = "arc007",
            Name = "Arctic Driver",
            Status = "Waiting",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            RequiredParts = new List<UndoSnapshotPart>
            {
                new() { PartNumber = "3001", ColorId = 11, PartName = "Brick", ColorName = "Black",
                        QuantityNeeded = 2, QuantityCollected = 1 }
            }
        };
        var eventId = await SeedScanEventAsync(ScanType.Delete, snap);

        var result = await _sut.UndoAsync(eventId);

        Assert.True(result.Success);

        await using var ctx = await _factory.CreateDbContextAsync();
        var restored = await ctx.TrackedMinifigs.Include(m => m.RequiredParts)
            .SingleAsync(m => m.BricklinkId == "arc007");
        Assert.Equal("Arctic Driver", restored.Name);
        Assert.Single(restored.RequiredParts);
        Assert.Equal(1, restored.RequiredParts[0].QuantityCollected);

        // ScanEvent ist als WasUndone markiert.
        var ev = await ctx.ScanEvents.SingleAsync(e => e.Id == eventId);
        Assert.True(ev.WasUndone);
        Assert.NotNull(ev.UndoneAt);

        // Audit-Eintrag (Type=UndoApplied) wurde geschrieben.
        var audit = await ctx.ScanEvents
            .Where(e => e.Type == ScanType.UndoApplied)
            .SingleAsync();
        Assert.Contains("Rueckgaengig", audit.ResultDescription);
    }

    [Fact]
    public async Task UndoAsync_DeleteFloating_recreates_floating_part()
    {
        var bin = await SeedBinAsync("Box 1");
        var snap = new UndoSnapshotFloatingDelete
        {
            OriginalFloatingId = 99,
            PartNumber = "3024",
            ColorId = 11,
            PartName = "Plate 1x1",
            ColorName = "Black",
            Quantity = 5,
            StorageBinId = bin,
            AddedAt = DateTime.UtcNow.AddHours(-1)
        };
        var eventId = await SeedScanEventAsync(ScanType.Delete, snap);

        var result = await _sut.UndoAsync(eventId);
        Assert.True(result.Success);

        await using var ctx = await _factory.CreateDbContextAsync();
        var fp = await ctx.FloatingParts.SingleAsync(p => p.PartNumber == "3024");
        Assert.Equal(5, fp.Quantity);
        Assert.Equal(bin, fp.StorageBinId);
    }

    [Fact]
    public async Task UndoAsync_Move_restores_old_storage_bin()
    {
        var oldBin = await SeedBinAsync("Old Box");
        var newBin = await SeedBinAsync("New Box");
        var minifigId = await SeedMinifigAsync("test001", newBin);

        var snap = new UndoSnapshotMove
        {
            MinifigId = minifigId,
            OldStorageBinId = oldBin,
            NewStorageBinId = newBin
        };
        var eventId = await SeedScanEventAsync(ScanType.Move, snap);

        var result = await _sut.UndoAsync(eventId);
        Assert.True(result.Success);

        await using var ctx = await _factory.CreateDbContextAsync();
        var m = await ctx.TrackedMinifigs.SingleAsync(x => x.Id == minifigId);
        Assert.Equal(oldBin, m.StorageBinId);
    }

    [Fact]
    public async Task UndoAsync_BinFreed_clears_freed_at()
    {
        var binId = await SeedBinAsync("Box", freedAt: DateTime.UtcNow);
        var snap = new UndoSnapshotBinFreed { BinId = binId };
        var eventId = await SeedScanEventAsync(ScanType.BinFreed, snap);

        var result = await _sut.UndoAsync(eventId);
        Assert.True(result.Success);

        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = await ctx.StorageBins.SingleAsync(b => b.Id == binId);
        Assert.Null(bin.FreedAt);
    }

    [Fact]
    public async Task UndoAsync_BinCreated_deletes_empty_bin()
    {
        var binId = await SeedBinAsync("Empty Box");
        var snap = new UndoSnapshotBinCreated { BinIds = new List<int> { binId } };
        var eventId = await SeedScanEventAsync(ScanType.BinCreated, snap);

        var result = await _sut.UndoAsync(eventId);
        Assert.True(result.Success);

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.StorageBins.Where(b => b.Id == binId).ToListAsync());
    }

    [Fact]
    public async Task UndoAsync_BinCreated_skips_bin_with_content()
    {
        var binId = await SeedBinAsync("Used Box");
        await SeedMinifigAsync("inhabit", binId);
        var snap = new UndoSnapshotBinCreated { BinIds = new List<int> { binId } };
        var eventId = await SeedScanEventAsync(ScanType.BinCreated, snap);

        var result = await _sut.UndoAsync(eventId);
        // Alle uebergebenen Bins haben Inhalt -> Fail.
        Assert.False(result.Success);

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.NotNull(await ctx.StorageBins.SingleAsync(b => b.Id == binId));
    }

    [Fact]
    public async Task GetLastUndoableAsync_returns_newest_undoable()
    {
        var snap1 = new UndoSnapshotBinFreed { BinId = 1 };
        var snap2 = new UndoSnapshotBinFreed { BinId = 2 };
        var bin1 = await SeedBinAsync("Bin A", freedAt: DateTime.UtcNow);
        var bin2 = await SeedBinAsync("Bin B", freedAt: DateTime.UtcNow);
        snap1 = snap1 with { BinId = bin1 };
        snap2 = snap2 with { BinId = bin2 };

        var ev1 = await SeedScanEventAsync(ScanType.BinFreed, snap1, timestamp: DateTime.UtcNow.AddMinutes(-5));
        var ev2 = await SeedScanEventAsync(ScanType.BinFreed, snap2, timestamp: DateTime.UtcNow);

        var last = await _sut.GetLastUndoableAsync();
        Assert.NotNull(last);
        Assert.Equal(ev2, last!.Id);
    }

    [Fact]
    public async Task GetHistoryAsync_returns_all_events_sorted_desc()
    {
        await SeedScanEventRawAsync(ScanType.MinifigScan, "Scan A", null,
            timestamp: DateTime.UtcNow.AddMinutes(-10));
        await SeedScanEventRawAsync(ScanType.PartScan, "Scan B", null,
            timestamp: DateTime.UtcNow.AddMinutes(-5));

        var history = await _sut.GetHistoryAsync();
        Assert.Equal(2, history.Count);
        Assert.True(history[0].Timestamp > history[1].Timestamp);
        // PartScan/MinifigScan haben kein UndoData -> CanUndo=false
        Assert.All(history, h => Assert.False(h.CanUndo));
    }

    [Fact]
    public async Task DoubleUndo_is_blocked()
    {
        var snap = new UndoSnapshotMinifigDelete
        {
            OriginalMinifigId = 1,
            BricklinkId = "x",
            FigNum = "x",
            Name = "X",
            Status = "Waiting"
        };
        var eventId = await SeedScanEventAsync(ScanType.Delete, snap);

        var first = await _sut.UndoAsync(eventId);
        Assert.True(first.Success);

        var second = await _sut.UndoAsync(eventId);
        Assert.False(second.Success);
    }

    // ---- Helpers ----

    private async Task<int> SeedScanEventAsync<T>(
        ScanType type, T snapshot,
        bool wasUndone = false,
        DateTime? timestamp = null) where T : class
    {
        return await SeedScanEventRawAsync(
            type,
            description: "Test",
            undoData: System.Text.Json.JsonSerializer.Serialize(snapshot),
            wasUndone: wasUndone,
            timestamp: timestamp);
    }

    private async Task<int> SeedScanEventRawAsync(
        ScanType type, string description, string? undoData,
        bool wasUndone = false,
        DateTime? timestamp = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var ev = new ScanEvent
        {
            Timestamp = timestamp ?? DateTime.UtcNow,
            Type = type,
            ResultDescription = description,
            WasUndone = wasUndone,
            UndoneAt = wasUndone ? DateTime.UtcNow : null,
            UndoData = undoData
        };
        ctx.ScanEvents.Add(ev);
        await ctx.SaveChangesAsync();
        return ev.Id;
    }

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

    private async Task<int> SeedMinifigAsync(string blId, int? binId = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var m = new TrackedMinifig
        {
            FigNum = blId,
            BricklinkId = blId,
            Name = $"Test {blId}",
            CreatedAt = DateTime.UtcNow,
            Status = TrackedMinifigStatus.Waiting,
            StorageBinId = binId
        };
        ctx.TrackedMinifigs.Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Stub-Implementation von IMinifigPersistenceService - der UndoService
    /// braucht nur RaiseDataChanged(); der Rest wird in den Tests nicht
    /// aufgerufen.
    /// </summary>
    private sealed class TestPersistenceStub : IMinifigPersistenceService
    {
        public event EventHandler? DataChanged;
        public int DataChangedCount { get; private set; }
        public void RaiseDataChanged()
        {
            DataChangedCount++;
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<PersistMinifigResult> PersistAndStoreAsync(PersistMinifigInput input, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(int trackedMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DismantleResult> DismantleAsync(int trackedMinifigId, IEnumerable<DismantlePartChoice> choices, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CleanupOldDismantledMinifigsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CleanupOnePartCompletesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> RemoveExportedMinifigsAsync(IEnumerable<int> minifigIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> RemoveExportedFloatingPartsAsync(IEnumerable<int> floatingPartIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(int Minifigs, int FloatingParts)> DeleteSelectionAsync(IEnumerable<int> minifigIds, IEnumerable<int> floatingPartIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(int Minifigs, int FloatingParts)> MoveSelectionAsync(IEnumerable<int> minifigIds, IEnumerable<int> floatingPartIds, int targetBinId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> CheckAndMarkCompleteAsync(int minifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReopenAsync(int minifigId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
