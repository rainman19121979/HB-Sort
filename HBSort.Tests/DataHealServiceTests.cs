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

    private async Task<int> SeedBinAsync(string label, DateTime? freedAt)
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

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }
}
