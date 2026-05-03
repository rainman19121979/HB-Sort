using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer MinifigPersistenceService - aktuell nur die Phase-7-Erweiterung
/// RemoveExportedFloatingPartsAsync. Der grosse PersistAndStoreAsync-Pfad hat
/// noch keine Tests, das ist eine separate Aufgabe.
/// </summary>
public class MinifigPersistenceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly MinifigPersistenceService _sut;

    public MinifigPersistenceServiceTests()
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
        _sut = new MinifigPersistenceService(_factory);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task RemoveExportedFloatingParts_returns_zero_for_empty_input()
    {
        var count = await _sut.RemoveExportedFloatingPartsAsync(Array.Empty<int>());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task RemoveExportedFloatingParts_deletes_entries_and_writes_audit_event()
    {
        var binId = await SeedBinAsync("Box 01");
        var fpId = await SeedFloatingAsync(binId, "3001", 11, qty: 5,
            partName: "Brick 2x4", colorName: "Black");

        var removed = await _sut.RemoveExportedFloatingPartsAsync(new[] { fpId });

        Assert.Equal(1, removed);

        await using var ctx = await _factory.CreateDbContextAsync();
        // FloatingPart geloescht
        Assert.Empty(await ctx.FloatingParts.ToListAsync());

        // ScanEvent als Audit-Trail
        var ev = await ctx.ScanEvents.SingleAsync();
        Assert.Equal(ScanType.FloatingPartExported, ev.Type);
        Assert.Equal("3001", ev.RecognizedId);
        // Audit muss Quell-Bin-Label enthalten damit man auch nach Bin-Freigabe
        // noch nachvollziehen kann woher das Teil kam.
        Assert.Contains("Box 01", ev.ResultDescription);
        Assert.Contains("3001", ev.ResultDescription);
        Assert.Contains("11", ev.ResultDescription); // ColorId
        Assert.Contains("5", ev.ResultDescription);  // Quantity
    }

    [Fact]
    public async Task RemoveExportedFloatingParts_handles_multiple_entries()
    {
        var binId = await SeedBinAsync("Box 01");
        var fp1 = await SeedFloatingAsync(binId, "3001", 11, qty: 2);
        var fp2 = await SeedFloatingAsync(binId, "3024", 0,  qty: 1);

        var removed = await _sut.RemoveExportedFloatingPartsAsync(new[] { fp1, fp2 });

        Assert.Equal(2, removed);
        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.FloatingParts.ToListAsync());
        Assert.Equal(2, await ctx.ScanEvents.CountAsync());
    }

    [Fact]
    public async Task RemoveExportedFloatingParts_silently_ignores_missing_ids()
    {
        var binId = await SeedBinAsync("Box 01");
        var fpId = await SeedFloatingAsync(binId, "3001", 0, qty: 1);

        // Mische gueltige + nicht-existente IDs
        var removed = await _sut.RemoveExportedFloatingPartsAsync(new[] { fpId, 9999, 8888 });

        Assert.Equal(1, removed); // nur der echte wurde entfernt
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

    private async Task<int> SeedFloatingAsync(
        int binId, string partNo, int colorId, int qty,
        string partName = "", string colorName = "")
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var fp = new FloatingPart
        {
            PartNumber = partNo,
            ColorId = colorId,
            Quantity = qty,
            PartName = partName,
            ColorName = colorName,
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
}
