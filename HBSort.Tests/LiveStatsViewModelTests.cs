using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// UX X.30 (v0.1.17) Block C: Tests fuer die erweiterte LiveStatsViewModel
/// (Lagerfach-Auslastung + Top-5 + Letzte Komplettierungen).
///
/// LiveStatsViewModel braucht Application.Current.Dispatcher fuer den
/// DataChanged-Hook. Wir umgehen das in den Tests indem wir RefreshAsync
/// direkt aufrufen statt ueber den Event - der Event-Handler wird ausschliesslich
/// im UI-Thread gehookt.
/// </summary>
public class LiveStatsViewModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly LiveStatsViewModel _sut;

    public LiveStatsViewModelTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<UserDataContext>()
            .UseSqlite(_connection).Options;
        using (var ctx = new UserDataContext(options))
            ctx.Database.EnsureCreated();
        _factory = new SimpleContextFactory(options);
        _sut = new LiveStatsViewModel(_factory, new NopPersistence());
    }

    public void Dispose()
    {
        _sut.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Empty_db_yields_zero_counts_and_empty_lists()
    {
        await _sut.RefreshAsync();
        Assert.Equal(0, _sut.CurrentWaitingCount);
        Assert.Equal(0, _sut.CurrentCompleteCount);
        Assert.Equal(0, _sut.CurrentFloatingTotal);
        Assert.Equal(0, _sut.CurrentFloatingEntries);
        Assert.Equal(0, _sut.BinsTotalCount);
        Assert.Equal(0, _sut.BinsOccupiedCount);
        Assert.Equal(0, _sut.BinsOccupiedPercent);
        Assert.Empty(_sut.TopBins);
        Assert.Empty(_sut.LastCompletions);
    }

    [Fact]
    public async Task Counts_reflect_db_content()
    {
        var bin1 = await SeedBinAsync("Box 001");
        await SeedMinifigAsync("c1", bin1, TrackedMinifigStatus.Complete);
        await SeedMinifigAsync("w1", bin1, TrackedMinifigStatus.Waiting);
        await SeedMinifigAsync("w2", bin1, TrackedMinifigStatus.Waiting);
        await SeedFloatingAsync(bin1, "3001", 11, qty: 5);
        await SeedFloatingAsync(bin1, "3002", 11, qty: 2);

        await _sut.RefreshAsync();

        Assert.Equal(2, _sut.CurrentWaitingCount);
        Assert.Equal(1, _sut.CurrentCompleteCount);
        Assert.Equal(2, _sut.CurrentFloatingEntries);
        Assert.Equal(7, _sut.CurrentFloatingTotal); // 5 + 2
    }

    [Fact]
    public async Task Bin_occupancy_reports_correct_ratio()
    {
        var bin1 = await SeedBinAsync("Box 001");
        var bin2 = await SeedBinAsync("Box 002");
        var bin3 = await SeedBinAsync("Box 003");
        await SeedMinifigAsync("m1", bin1, TrackedMinifigStatus.Waiting);
        await SeedFloatingAsync(bin2, "3001", 11, qty: 1);
        // bin3 bleibt leer

        await _sut.RefreshAsync();

        Assert.Equal(3, _sut.BinsTotalCount);
        Assert.Equal(2, _sut.BinsOccupiedCount);
        Assert.Equal(66.67, Math.Round(_sut.BinsOccupiedPercent, 2));
        Assert.Contains("2", _sut.BinsOccupancyText);
        Assert.Contains("3", _sut.BinsOccupancyText);
    }

    [Fact]
    public async Task TopBins_sorted_by_item_count_desc_max5()
    {
        var bin1 = await SeedBinAsync("Box 001");
        var bin2 = await SeedBinAsync("Box 002");
        var bin3 = await SeedBinAsync("Box 003");

        // bin1: 1 Minifig + 2 Floatings = 3
        await SeedMinifigAsync("m1", bin1);
        await SeedFloatingAsync(bin1, "3001", 11, 5);
        await SeedFloatingAsync(bin1, "3002", 11, 5);
        // bin2: 5 Minifigs
        for (int i = 0; i < 5; i++)
            await SeedMinifigAsync($"m{i+10}", bin2);
        // bin3: 1 Floating
        await SeedFloatingAsync(bin3, "3003", 11, 1);

        await _sut.RefreshAsync();

        Assert.Equal(3, _sut.TopBins.Count);
        Assert.Equal("Box 002", _sut.TopBins[0].Label); // 5 Items
        Assert.Equal(5, _sut.TopBins[0].ItemCount);
        Assert.Equal("Box 001", _sut.TopBins[1].Label); // 3 Items
        Assert.Equal(3, _sut.TopBins[1].ItemCount);
        Assert.Equal("Box 003", _sut.TopBins[2].Label); // 1 Item
    }

    [Fact]
    public async Task LastCompletions_sorted_by_completedAt_desc_max5()
    {
        var bin = await SeedBinAsync("Box");
        // 3 Complete-Figuren mit unterschiedlichen CompletedAt
        await SeedCompletedMinifigAsync("c1", bin, completedAt: DateTime.UtcNow.AddHours(-3));
        await SeedCompletedMinifigAsync("c2", bin, completedAt: DateTime.UtcNow.AddHours(-1));
        await SeedCompletedMinifigAsync("c3", bin, completedAt: DateTime.UtcNow.AddMinutes(-30));

        await _sut.RefreshAsync();

        Assert.Equal(3, _sut.LastCompletions.Count);
        Assert.Equal("c3", _sut.LastCompletions[0].BricklinkId); // neueste zuerst
        Assert.Equal("c2", _sut.LastCompletions[1].BricklinkId);
        Assert.Equal("c1", _sut.LastCompletions[2].BricklinkId);
    }

    // ---- Helpers ----

    private async Task<int> SeedBinAsync(string label)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = new StorageBin { Label = label, CreatedAt = DateTime.UtcNow };
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

    private async Task<int> SeedCompletedMinifigAsync(string blId, int binId, DateTime completedAt)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var m = new TrackedMinifig
        {
            FigNum = blId,
            BricklinkId = blId,
            Name = $"Complete {blId}",
            CreatedAt = completedAt.AddHours(-1),
            CompletedAt = completedAt,
            Status = TrackedMinifigStatus.Complete,
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

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    /// <summary>Minimal-Stub fuer IMinifigPersistenceService - LiveStatsViewModel
    /// hookt nur den DataChanged-Event, alles andere wird in Tests nicht aufgerufen.</summary>
    private sealed class NopPersistence : IMinifigPersistenceService
    {
        public event EventHandler? DataChanged;
        public void RaiseDataChanged() => DataChanged?.Invoke(this, EventArgs.Empty);
        public Task<PersistMinifigResult> PersistAndStoreAsync(PersistMinifigInput input, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(int trackedMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DismantleResult> DismantleAsync(int trackedMinifigId, IEnumerable<DismantlePartChoice> choices, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CleanupOldDismantledMinifigsAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> CleanupOnePartCompletesAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> RemoveExportedMinifigsAsync(IEnumerable<int> minifigIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> RemoveExportedFloatingPartsAsync(IEnumerable<int> floatingPartIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> CheckAndMarkCompleteAsync(int minifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReopenAsync(int minifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(int Minifigs, int FloatingParts)> DeleteSelectionAsync(IEnumerable<int> minifigIds, IEnumerable<int> floatingPartIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(int Minifigs, int FloatingParts)> MoveSelectionAsync(IEnumerable<int> minifigIds, IEnumerable<int> floatingPartIds, int targetBinId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}

/// <summary>
/// UX X.30 (v0.1.17) Block D: Tests fuer ScanResultCard.HotkeyLabel.
/// </summary>
public class ScanResultCardTests
{
    [Theory]
    [InlineData(1, "[1]")]
    [InlineData(2, "[2]")]
    [InlineData(3, "[3]")]
    [InlineData(4, "")]
    [InlineData(0, "")]
    public void HotkeyLabel_matches_rank(int rank, string expected)
    {
        var card = new ScanResultCard { Rank = rank };
        Assert.Equal(expected, card.HotkeyLabel);
    }
}
