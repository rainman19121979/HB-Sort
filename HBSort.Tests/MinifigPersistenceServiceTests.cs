using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer MinifigPersistenceService.
///
/// Audit W-5 (2026-05-04): PersistAndStoreAsync ist jetzt mit
/// Reverse-Match / Auto-Complete / Bin-Belegen / Stats-Increment-Tests
/// abgedeckt. Vorher war nur die Phase-7-Erweiterung
/// RemoveExportedFloatingPartsAsync getestet.
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

    // ====================================================================
    // PersistAndStoreAsync (Audit W-5)
    // ====================================================================

    [Fact]
    public async Task PersistAndStore_no_match_creates_waiting_minifig()
    {
        // Frische DB, kein FloatingPart -> Reverse-Match ist 0, Figur bleibt
        // Waiting mit allen Parts auf QuantityCollected=0.
        var binId = await SeedBinAsync("Box 01");

        var input = MakeInput("arc007", "Arctic Driver", binId, parts:
        [
            ("3001", 11, 1),  // Brick 2x4 black, 1x
            ("3024",  0, 2)   // Plate 1x1 white, 2x
        ]);

        var result = await _sut.PersistAndStoreAsync(input);

        Assert.NotNull(result.SavedMinifig);
        Assert.Equal(TrackedMinifigStatus.Waiting, result.SavedMinifig.Status);
        Assert.Equal(0, result.ReverseMatchedFloating);
        Assert.Equal(0, result.CompletedRequiredParts);
        Assert.False(result.IsFullyComplete);

        await using var ctx = await _factory.CreateDbContextAsync();
        var saved = await ctx.TrackedMinifigs.Include(m => m.RequiredParts)
            .SingleAsync(m => m.BricklinkId == "arc007");
        Assert.Equal(2, saved.RequiredParts.Count);
        Assert.All(saved.RequiredParts, p => Assert.Equal(0, p.QuantityCollected));
    }

    [Fact]
    public async Task PersistAndStore_full_match_marks_complete_and_consumes_floating_parts()
    {
        // Alle benoetigten Teile liegen schon im Pool -> Reverse-Match deckt
        // alles ab, Figur wird sofort Complete und FloatingParts werden geloescht.
        var binId = await SeedBinAsync("Box 02");
        var fpBinId = await SeedBinAsync("Box 99-floating");
        await SeedFloatingAsync(fpBinId, "3001", 11, qty: 1);
        await SeedFloatingAsync(fpBinId, "3024",  0, qty: 2);

        var input = MakeInput("arc008", "Snow Trooper", binId, parts:
        [
            ("3001", 11, 1),
            ("3024",  0, 2)
        ]);

        var result = await _sut.PersistAndStoreAsync(input);

        Assert.True(result.IsFullyComplete);
        Assert.Equal(TrackedMinifigStatus.Complete, result.SavedMinifig.Status);
        Assert.NotNull(result.SavedMinifig.CompletedAt);
        Assert.Equal(3, result.ReverseMatchedFloating); // 1 + 2
        Assert.Equal(2, result.CompletedRequiredParts);

        // FloatingParts komplett konsumiert.
        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.FloatingParts.ToListAsync());
    }

    [Fact]
    public async Task PersistAndStore_partial_match_collects_partial_keeps_waiting()
    {
        // Ein Required-Part wird nur teilweise gefuellt: 1 von 3 verfuegbar.
        var binId = await SeedBinAsync("Box 03");
        var fpBinId = await SeedBinAsync("Box 99");
        await SeedFloatingAsync(fpBinId, "3001", 11, qty: 1); // nur 1 da, 3 gebraucht

        var input = MakeInput("arc009", "Polar Researcher", binId, parts:
        [
            ("3001", 11, 3)
        ]);

        var result = await _sut.PersistAndStoreAsync(input);

        Assert.False(result.IsFullyComplete);
        Assert.Equal(TrackedMinifigStatus.Waiting, result.SavedMinifig.Status);
        Assert.Null(result.SavedMinifig.CompletedAt);
        Assert.Equal(1, result.ReverseMatchedFloating);
        Assert.Equal(0, result.CompletedRequiredParts);

        await using var ctx = await _factory.CreateDbContextAsync();
        var part = await ctx.TrackedMinifigParts.SingleAsync();
        Assert.Equal(1, part.QuantityCollected);
        Assert.Equal(3, part.QuantityNeeded);

        // FloatingPart vollstaendig konsumiert (war nur 1 da).
        Assert.Empty(await ctx.FloatingParts.ToListAsync());
    }

    [Fact]
    public async Task PersistAndStore_assigns_minifig_to_chosen_bin()
    {
        // Sauberkeits-Check: das gewaehlte Bin landet als StorageBinId an
        // der Figur. Bin-Belegung selbst (Free-Flag etc.) ist Sache eines
        // anderen Service und nicht hier zu pruefen.
        var binId = await SeedBinAsync("Box 04-target");

        var input = MakeInput("arc010", "Mountain Climber", binId, parts:
        [
            ("3001", 11, 1)
        ]);

        var result = await _sut.PersistAndStoreAsync(input);

        Assert.Equal(binId, result.SavedMinifig.StorageBinId);
    }

    [Fact]
    public async Task PersistAndStore_increments_DailyStats_scan_count()
    {
        var binId = await SeedBinAsync("Box 05");

        var input = MakeInput("arc011", "Cave Explorer", binId, parts:
        [
            ("3001", 11, 1)
        ]);

        await _sut.PersistAndStoreAsync(input);

        await using var ctx = await _factory.CreateDbContextAsync();
        var todayUtc = DateTime.UtcNow.Date;
        var stat = await ctx.DailyStats.FirstOrDefaultAsync(s => s.Date == todayUtc);
        Assert.NotNull(stat);
        Assert.Equal(1, stat!.ScanCount);
        Assert.Equal(0, stat.MinifigsCompletedCount);
    }

    [Fact]
    public async Task PersistAndStore_full_match_also_increments_completed_count()
    {
        var binId = await SeedBinAsync("Box 06");
        var fpBinId = await SeedBinAsync("Box 99");
        await SeedFloatingAsync(fpBinId, "3001", 11, qty: 1);

        var input = MakeInput("arc012", "Arctic Captain", binId, parts:
        [
            ("3001", 11, 1)
        ]);

        await _sut.PersistAndStoreAsync(input);

        await using var ctx = await _factory.CreateDbContextAsync();
        var todayUtc = DateTime.UtcNow.Date;
        var stat = await ctx.DailyStats.SingleAsync(s => s.Date == todayUtc);
        Assert.Equal(1, stat.ScanCount);
        Assert.Equal(1, stat.MinifigsCompletedCount); // direkt komplett -> auch hochgezaehlt
    }

    [Fact]
    public async Task PersistAndStore_writes_ScanEvent_audit_trail()
    {
        var binId = await SeedBinAsync("Box 07");

        // PersistMinifigInput ist eine Klasse mit init-Properties, kein
        // record - daher direkt initialisieren statt with-Expression.
        var input = new PersistMinifigInput
        {
            BricklinkId = "arc013",
            Name = "Test Figure",
            StorageBinId = binId,
            Confidence = 0.92,
            RequiredParts = new List<PersistMinifigPart>
            {
                new() {
                    BricklinkPartNo = "3001",
                    BricklinkColorId = 11,
                    QuantityNeeded = 1
                }
            }
        };

        await _sut.PersistAndStoreAsync(input);

        await using var ctx = await _factory.CreateDbContextAsync();
        var ev = await ctx.ScanEvents.SingleAsync();
        Assert.Equal(ScanType.MinifigScan, ev.Type);
        Assert.Equal("arc013", ev.RecognizedId);
        Assert.Equal(0.92, ev.Confidence);
        Assert.Contains("Test Figure", ev.ResultDescription);
        Assert.Contains("Box 07", ev.ResultDescription);
        Assert.False(ev.WasUndone);
    }

    [Fact]
    public async Task PersistAndStore_partial_floating_keeps_remaining_quantity_in_pool()
    {
        // Wenn der FloatingPart mehr Quantity hat als gebraucht: Rest bleibt.
        var binId = await SeedBinAsync("Box 08");
        var fpBinId = await SeedBinAsync("Box 99");
        var fpId = await SeedFloatingAsync(fpBinId, "3001", 11, qty: 5); // 5 da, nur 2 gebraucht

        var input = MakeInput("arc014", "Quantity Test", binId, parts:
        [
            ("3001", 11, 2)
        ]);

        await _sut.PersistAndStoreAsync(input);

        await using var ctx = await _factory.CreateDbContextAsync();
        var fp = await ctx.FloatingParts.SingleAsync(f => f.Id == fpId);
        Assert.Equal(3, fp.Quantity); // 5 - 2 = 3 bleibt
    }

    [Fact]
    public async Task PersistAndStore_throws_on_missing_bin()
    {
        // Bin-Id 9999 existiert nicht.
        var input = MakeInput("arc015", "Bad Bin", 9999, parts:
        [
            ("3001", 11, 1)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.PersistAndStoreAsync(input));
    }

    [Fact]
    public async Task PersistAndStore_caps_user_quantityCollected_at_quantityNeeded()
    {
        // User hat manuell 5 Teile als "schon da" markiert, gebraucht werden aber
        // nur 2. Das Service muss das auf 2 cappen, sonst rechnet sich der
        // Reverse-Match falsch.
        var binId = await SeedBinAsync("Box 09");

        var input = new PersistMinifigInput
        {
            BricklinkId = "arc016",
            Name = "Cap Test",
            StorageBinId = binId,
            RequiredParts = new List<PersistMinifigPart>
            {
                new() {
                    BricklinkPartNo = "3001",
                    BricklinkColorId = 11,
                    QuantityNeeded = 2,
                    QuantityCollected = 5  // bewusst zu hoch
                }
            }
        };

        var result = await _sut.PersistAndStoreAsync(input);

        // Direkt komplett (2 von 2 nach Capping), kein Reverse-Match noetig.
        Assert.True(result.IsFullyComplete);

        await using var ctx = await _factory.CreateDbContextAsync();
        var part = await ctx.TrackedMinifigParts.SingleAsync();
        Assert.Equal(2, part.QuantityCollected); // gecapped auf QuantityNeeded
    }

    /// <summary>Hilfsfunktion: PersistMinifigInput mit Required-Parts bauen.</summary>
    private static PersistMinifigInput MakeInput(string bricklinkId, string name, int binId,
        IEnumerable<(string PartNo, int ColorId, int Qty)> parts)
        => new()
        {
            BricklinkId = bricklinkId,
            Name = name,
            StorageBinId = binId,
            RequiredParts = parts.Select(p => new PersistMinifigPart
            {
                BricklinkPartNo = p.PartNo,
                BricklinkColorId = p.ColorId,
                QuantityNeeded = p.Qty,
                QuantityCollected = 0
            }).ToList()
        };

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
