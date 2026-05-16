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

        // UX X.32 Block C (v0.1.19): ConsumedFloatingParts ist befuellt mit
        // pro Konsum-Zeile einem Eintrag (Quell-Bin-Label aus Include).
        Assert.Equal(2, result.ConsumedFloatingParts.Count);
        Assert.All(result.ConsumedFloatingParts,
            c => Assert.Equal("Box 99-floating", c.SourceBinLabel));
        var brick = result.ConsumedFloatingParts.Single(c => c.BlPartNo == "3001");
        Assert.Equal(11, brick.BlColorId);
        Assert.Equal(1, brick.Quantity);
        var plate = result.ConsumedFloatingParts.Single(c => c.BlPartNo == "3024");
        Assert.Equal(0, plate.BlColorId);
        Assert.Equal(2, plate.Quantity);
    }

    [Fact]
    public async Task PersistAndStore_no_match_leaves_ConsumedFloatingParts_empty()
    {
        // Kein FloatingPart vorhanden -> keine Konsum-Eintraege.
        var binId = await SeedBinAsync("Box 02b");
        var input = MakeInput("arc008b", "X", binId, parts:
        [
            ("3001", 11, 1)
        ]);

        var result = await _sut.PersistAndStoreAsync(input);

        Assert.Empty(result.ConsumedFloatingParts);
        Assert.Equal(0, result.ReverseMatchedFloating);
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

    // ====================================================================
    // v0.1.24-beta.1 Phase 2a-Polish: ConsumeFloatingParts-Flag
    // ====================================================================

    [Fact]
    public async Task PersistAndStore_with_ConsumeFloatingParts_true_consumes_pool()
    {
        // Default-Verhalten (= BuildSuggestion-Pfad): Flag steht auf true,
        // Service durchsucht FloatingPool und konsumiert passende Teile.
        var binId = await SeedBinAsync("Box T01-target");
        var fpBinId = await SeedBinAsync("Box T01-pool");
        await SeedFloatingAsync(fpBinId, "3001", 11, qty: 1,
            partName: "Brick", colorName: "Black");
        await SeedFloatingAsync(fpBinId, "3024", 0, qty: 2,
            partName: "Plate", colorName: "White");

        // Explizit ConsumeFloatingParts=true setzen (auch wenn Default).
        var input = new PersistMinifigInput
        {
            BricklinkId = "polish-true-001",
            Name = "Polish True",
            StorageBinId = binId,
            ConsumeFloatingParts = true,
            RequiredParts = new List<PersistMinifigPart>
            {
                new() { BricklinkPartNo = "3001", BricklinkColorId = 11, QuantityNeeded = 1, QuantityCollected = 0 },
                new() { BricklinkPartNo = "3024", BricklinkColorId = 0,  QuantityNeeded = 2, QuantityCollected = 0 }
            }
        };

        var result = await _sut.PersistAndStoreAsync(input);

        Assert.True(result.IsFullyComplete);
        Assert.Equal(3, result.ReverseMatchedFloating);
        Assert.Equal(2, result.ConsumedFloatingParts.Count);

        // FloatingParts sind weg, QuantityCollected hochgezaehlt.
        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.FloatingParts.ToListAsync());
        var saved = await ctx.TrackedMinifigs
            .Include(m => m.RequiredParts)
            .SingleAsync(m => m.BricklinkId == "polish-true-001");
        Assert.All(saved.RequiredParts, p => Assert.Equal(p.QuantityNeeded, p.QuantityCollected));
    }

    [Fact]
    public async Task PersistAndStore_with_ConsumeFloatingParts_false_does_not_touch_pool()
    {
        // Flag false (= MinifigDetailView/PersistPending-Pfad): Service
        // konsumiert NICHT selbst. FloatingPool bleibt unveraendert.
        // Required-Parts behalten ihr QuantityCollected aus dem Input.
        var binId = await SeedBinAsync("Box T02-target");
        var fpBinId = await SeedBinAsync("Box T02-pool");
        var fpId1 = await SeedFloatingAsync(fpBinId, "3001", 11, qty: 1);
        var fpId2 = await SeedFloatingAsync(fpBinId, "3024", 0, qty: 2);

        var input = new PersistMinifigInput
        {
            BricklinkId = "polish-false-001",
            Name = "Polish False NoConsume",
            StorageBinId = binId,
            ConsumeFloatingParts = false,
            RequiredParts = new List<PersistMinifigPart>
            {
                new() { BricklinkPartNo = "3001", BricklinkColorId = 11, QuantityNeeded = 1, QuantityCollected = 0 },
                new() { BricklinkPartNo = "3024", BricklinkColorId = 0,  QuantityNeeded = 2, QuantityCollected = 0 }
            }
        };

        var result = await _sut.PersistAndStoreAsync(input);

        Assert.False(result.IsFullyComplete);
        Assert.Equal(0, result.ReverseMatchedFloating);
        Assert.Empty(result.ConsumedFloatingParts);
        Assert.Equal(TrackedMinifigStatus.Waiting, result.SavedMinifig.Status);

        // FloatingParts unveraendert (Quantity gleich, Eintraege da).
        await using var ctx = await _factory.CreateDbContextAsync();
        var fp1 = await ctx.FloatingParts.SingleAsync(f => f.Id == fpId1);
        var fp2 = await ctx.FloatingParts.SingleAsync(f => f.Id == fpId2);
        Assert.Equal(1, fp1.Quantity);
        Assert.Equal(2, fp2.Quantity);

        // QuantityCollected bleibt 0 (Service hat nichts dazugefuegt).
        var saved = await ctx.TrackedMinifigs
            .Include(m => m.RequiredParts)
            .SingleAsync(m => m.BricklinkId == "polish-false-001");
        Assert.All(saved.RequiredParts, p => Assert.Equal(0, p.QuantityCollected));
    }

    [Fact]
    public async Task PersistAndStore_with_ConsumeFloatingParts_false_persists_explicit_quantities()
    {
        // Flag false, aber RequiredParts haben QuantityCollected vorbefuellt
        // (vom UI ueber explizite "Aus Fach"-Klicks). Service uebernimmt
        // diese Werte 1:1, fasst aber den Pool nicht an. IsFullyComplete
        // basiert auf der vorbefuellten Menge.
        var binId = await SeedBinAsync("Box T03-target");
        var fpBinId = await SeedBinAsync("Box T03-pool");
        var fpId = await SeedFloatingAsync(fpBinId, "3001", 11, qty: 3);

        var input = new PersistMinifigInput
        {
            BricklinkId = "polish-false-002",
            Name = "Polish False ExplicitQty",
            StorageBinId = binId,
            ConsumeFloatingParts = false,
            RequiredParts = new List<PersistMinifigPart>
            {
                // UI hat 2 von 3 als gesammelt vorbefuellt.
                new() { BricklinkPartNo = "3001", BricklinkColorId = 11, QuantityNeeded = 3, QuantityCollected = 2 }
            }
        };

        var result = await _sut.PersistAndStoreAsync(input);

        // 2/3 -> nicht komplett, ReverseMatched=0 (kein Pool-Konsum).
        Assert.False(result.IsFullyComplete);
        Assert.Equal(0, result.ReverseMatchedFloating);
        Assert.Empty(result.ConsumedFloatingParts);

        // Pool unveraendert.
        await using var ctx = await _factory.CreateDbContextAsync();
        var fp = await ctx.FloatingParts.SingleAsync(f => f.Id == fpId);
        Assert.Equal(3, fp.Quantity);

        // QuantityCollected=2 in DB persistiert (nicht auf 0 gestellt, nicht auf 3 erhoeht).
        var saved = await ctx.TrackedMinifigs
            .Include(m => m.RequiredParts)
            .SingleAsync(m => m.BricklinkId == "polish-false-002");
        var part = Assert.Single(saved.RequiredParts);
        Assert.Equal(2, part.QuantityCollected);
        Assert.Equal(3, part.QuantityNeeded);
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

    // ====================================================================
    // UX X.25: DismantleAsync mit Direkt-Zuordnung zu wartender Figur
    // ====================================================================

    [Fact]
    public async Task Dismantle_throws_when_TargetBin_and_AssignTo_both_set()
    {
        var binId = await SeedBinAsync("Box 001");
        var input = MakeInput("set000", "TestSet", binId,
            new[] { ("3001", 11, 1) });
        var stored = await _sut.PersistAndStoreAsync(input);
        var partId = await GetFirstPartIdAsync(stored.SavedMinifig.Id);

        var choices = new[]
        {
            new DismantlePartChoice
            {
                TrackedMinifigPartId = partId,
                IsKept = true,
                TargetBinId = binId,
                AssignToTrackedMinifigPartId = 999  // BEIDE gesetzt -> Validierungs-Fehler
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.DismantleAsync(stored.SavedMinifig.Id, choices));
    }

    [Fact]
    public async Task Dismantle_throws_when_neither_TargetBin_nor_AssignTo_set_and_kept()
    {
        var binId = await SeedBinAsync("Box 001");
        var input = MakeInput("set000", "TestSet", binId,
            new[] { ("3001", 11, 1) });
        var stored = await _sut.PersistAndStoreAsync(input);
        var partId = await GetFirstPartIdAsync(stored.SavedMinifig.Id);

        var choices = new[]
        {
            new DismantlePartChoice
            {
                TrackedMinifigPartId = partId,
                IsKept = true,
                TargetBinId = null,
                AssignToTrackedMinifigPartId = null  // BEIDE null + IsKept=true -> Fehler
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.DismantleAsync(stored.SavedMinifig.Id, choices));
    }

    [Fact]
    public async Task Dismantle_assigns_part_to_waiting_minifig_when_AssignTo_set()
    {
        var binA = await SeedBinAsync("Box 001"); // wartende Figur
        var binB = await SeedBinAsync("Box 002"); // zu zerlegende Figur

        // Wartende Figur die das Teil 3001/11 noch braucht
        var waiting = await _sut.PersistAndStoreAsync(MakeInput(
            "wait1", "WaitingFig", binA, new[] { ("3001", 11, 2), ("9999", 0, 1) }));

        // Zu zerlegende Figur mit dem benoetigten Teil
        var donor = await _sut.PersistAndStoreAsync(MakeInput(
            "don1", "Donor", binB, new[] { ("3001", 11, 1) }));
        var donorPartId = await GetFirstPartIdAsync(donor.SavedMinifig.Id);

        // Den wartenden TrackedMinifigPart finden den wir zuweisen wollen
        int waitingPartId;
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            waitingPartId = await ctx.TrackedMinifigParts
                .Where(p => p.TrackedMinifigId == waiting.SavedMinifig.Id && p.PartNumber == "3001")
                .Select(p => p.Id)
                .SingleAsync();
        }

        var choices = new[]
        {
            new DismantlePartChoice
            {
                TrackedMinifigPartId = donorPartId,
                IsKept = true,
                AssignToTrackedMinifigPartId = waitingPartId
            }
        };

        var result = await _sut.DismantleAsync(donor.SavedMinifig.Id, choices);

        Assert.True(result.Success);
        Assert.Equal(0, result.CreatedFloatingParts);
        Assert.Equal(1, result.AssignedToWaitingCount);
        Assert.Empty(result.CompletedMinifigNames); // Wartende braucht 2x, hat erst 1x

        await using var verify = await _factory.CreateDbContextAsync();
        var part = await verify.TrackedMinifigParts.SingleAsync(p => p.Id == waitingPartId);
        Assert.Equal(1, part.QuantityCollected);
        // Donor-Figur ist weg (Cascade)
        Assert.False(await verify.TrackedMinifigs.AnyAsync(m => m.Id == donor.SavedMinifig.Id));
    }

    [Fact]
    public async Task Dismantle_completes_waiting_minifig_when_assignment_fills_last_part()
    {
        var binA = await SeedBinAsync("Box 001");
        var binB = await SeedBinAsync("Box 002");

        // Wartende Figur die NUR noch dieses eine Teil braucht (1x)
        var waiting = await _sut.PersistAndStoreAsync(MakeInput(
            "wait2", "FastWait", binA, new[] { ("3001", 11, 1) }));

        var donor = await _sut.PersistAndStoreAsync(MakeInput(
            "don2", "Donor", binB, new[] { ("3001", 11, 1) }));
        var donorPartId = await GetFirstPartIdAsync(donor.SavedMinifig.Id);

        int waitingPartId;
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            waitingPartId = await ctx.TrackedMinifigParts
                .Where(p => p.TrackedMinifigId == waiting.SavedMinifig.Id)
                .Select(p => p.Id)
                .SingleAsync();
        }

        var result = await _sut.DismantleAsync(donor.SavedMinifig.Id, new[]
        {
            new DismantlePartChoice
            {
                TrackedMinifigPartId = donorPartId,
                IsKept = true,
                AssignToTrackedMinifigPartId = waitingPartId
            }
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.AssignedToWaitingCount);
        Assert.Single(result.CompletedMinifigNames);
        Assert.Equal("FastWait", result.CompletedMinifigNames[0]);

        await using var verify = await _factory.CreateDbContextAsync();
        var fig = await verify.TrackedMinifigs
            .SingleAsync(m => m.Id == waiting.SavedMinifig.Id);
        Assert.Equal(TrackedMinifigStatus.Complete, fig.Status);
        Assert.NotNull(fig.CompletedAt);
    }

    [Fact]
    public async Task Dismantle_mixed_choices_assign_and_floating_in_one_call()
    {
        var binA = await SeedBinAsync("Box 001");
        var binB = await SeedBinAsync("Box 002");

        var waiting = await _sut.PersistAndStoreAsync(MakeInput(
            "wait3", "MixWait", binA, new[] { ("3001", 11, 1), ("9999", 0, 1) }));

        var donor = await _sut.PersistAndStoreAsync(MakeInput(
            "don3", "MixDonor", binB,
            new[] { ("3001", 11, 1), ("3024", 5, 2) }));

        int donorPart3001Id, donorPart3024Id, waitingPart3001Id;
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            donorPart3001Id = await ctx.TrackedMinifigParts
                .Where(p => p.TrackedMinifigId == donor.SavedMinifig.Id && p.PartNumber == "3001")
                .Select(p => p.Id).SingleAsync();
            donorPart3024Id = await ctx.TrackedMinifigParts
                .Where(p => p.TrackedMinifigId == donor.SavedMinifig.Id && p.PartNumber == "3024")
                .Select(p => p.Id).SingleAsync();
            waitingPart3001Id = await ctx.TrackedMinifigParts
                .Where(p => p.TrackedMinifigId == waiting.SavedMinifig.Id && p.PartNumber == "3001")
                .Select(p => p.Id).SingleAsync();
        }

        var result = await _sut.DismantleAsync(donor.SavedMinifig.Id, new[]
        {
            // 3001 -> wartende Figur direkt zuordnen
            new DismantlePartChoice
            {
                TrackedMinifigPartId = donorPart3001Id,
                IsKept = true,
                AssignToTrackedMinifigPartId = waitingPart3001Id
            },
            // 3024 -> normaler FloatingPart-Pfad
            new DismantlePartChoice
            {
                TrackedMinifigPartId = donorPart3024Id,
                IsKept = true,
                TargetBinId = binB
            }
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.AssignedToWaitingCount);
        Assert.Equal(1, result.CreatedFloatingParts);
        Assert.Equal(2, result.TotalPartsTransferred); // 3024 hat collected=0 -> qty=needed=2

        await using var verify = await _factory.CreateDbContextAsync();
        var floating = await verify.FloatingParts
            .Where(fp => fp.PartNumber == "3024" && fp.StorageBinId == binB)
            .SingleAsync();
        Assert.Equal(2, floating.Quantity);
    }

    [Fact]
    public async Task Dismantle_skips_assignment_when_waiting_part_already_full()
    {
        var binA = await SeedBinAsync("Box 001");
        var binB = await SeedBinAsync("Box 002");

        // Wartende Figur, Teil ist schon voll (collected=1, needed=1)
        var waiting = await _sut.PersistAndStoreAsync(MakeInput(
            "wait4", "FullWait", binA, new[] { ("3001", 11, 1), ("9999", 0, 1) }));

        // Wartende Figur ist noch nicht komplett (9999 fehlt), aber 3001 ist schon 1/1
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var p = await ctx.TrackedMinifigParts
                .SingleAsync(x => x.TrackedMinifigId == waiting.SavedMinifig.Id && x.PartNumber == "3001");
            p.QuantityCollected = 1;
            await ctx.SaveChangesAsync();
        }

        var donor = await _sut.PersistAndStoreAsync(MakeInput(
            "don4", "Donor", binB, new[] { ("3001", 11, 1) }));
        var donorPartId = await GetFirstPartIdAsync(donor.SavedMinifig.Id);

        int waitingPartId;
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            waitingPartId = await ctx.TrackedMinifigParts
                .Where(p => p.TrackedMinifigId == waiting.SavedMinifig.Id && p.PartNumber == "3001")
                .Select(p => p.Id).SingleAsync();
        }

        var result = await _sut.DismantleAsync(donor.SavedMinifig.Id, new[]
        {
            new DismantlePartChoice
            {
                TrackedMinifigPartId = donorPartId,
                IsKept = true,
                AssignToTrackedMinifigPartId = waitingPartId
            }
        });

        Assert.True(result.Success);
        // Skip-Pfad: waiting-Teil schon voll -> nichts inkrementiert, kein Floating angelegt.
        // assignedCount BLEIBT 0 weil der Pfad durchs continue ueberspringt.
        Assert.Equal(0, result.AssignedToWaitingCount);
        Assert.Equal(0, result.CreatedFloatingParts);
    }

    private async Task<int> GetFirstPartIdAsync(int trackedMinifigId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.TrackedMinifigParts
            .Where(p => p.TrackedMinifigId == trackedMinifigId)
            .Select(p => p.Id)
            .FirstAsync();
    }

    // ====================================================================
    // UX X.28 Bug A (v0.1.15): wartende Figur darf StorageBinId NICHT
    // verlieren wenn sie via UX-X.25-Direkt-Zuordnung komplett wird.
    // Praxis-Befund 2026-05-08: cty0685 (Volcano Explorer) lag in Box 001
    // (StorageBinId=1), wurde durch Zerlege-Direkt-Zuordnung komplett, war
    // danach aber StorageBinId=null in der DB.
    // ====================================================================

    [Fact]
    public async Task Dismantle_DirectAssign_PreservesWaitingMinifigStorageBin()
    {
        var binA = await SeedBinAsync("Box A"); // wartende Figur
        var binB = await SeedBinAsync("Box B"); // Donor

        // Wartende Figur die nur noch das Teil 3001/11 braucht (sonst komplett)
        var waiting = await _sut.PersistAndStoreAsync(MakeInput(
            "wait_bug_a", "WaitingFig-BugA", binA, new[] { ("3001", 11, 1) }));
        var donor = await _sut.PersistAndStoreAsync(MakeInput(
            "don_bug_a", "Donor-BugA", binB, new[] { ("3001", 11, 1) }));
        var donorPartId = await GetFirstPartIdAsync(donor.SavedMinifig.Id);

        int waitingPartId;
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            waitingPartId = await ctx.TrackedMinifigParts
                .Where(p => p.TrackedMinifigId == waiting.SavedMinifig.Id)
                .Select(p => p.Id).SingleAsync();
        }

        // Act: Direkt-Zuordnung -> waiting wird komplett
        await _sut.DismantleAsync(donor.SavedMinifig.Id, new[]
        {
            new DismantlePartChoice
            {
                TrackedMinifigPartId = donorPartId,
                IsKept = true,
                AssignToTrackedMinifigPartId = waitingPartId
            }
        });

        // Assert: wartende Figur ist Complete UND behaelt StorageBinId=binA
        await using var verify = await _factory.CreateDbContextAsync();
        var w = await verify.TrackedMinifigs.SingleAsync(m => m.Id == waiting.SavedMinifig.Id);
        Assert.Equal(TrackedMinifigStatus.Complete, w.Status);
        Assert.NotNull(w.StorageBinId);
        Assert.Equal(binA, w.StorageBinId);
    }

    [Fact]
    public async Task Dismantle_DirectAssign_NotCompleting_PreservesStorageBin()
    {
        // Variante des Bug-A-Tests: Direkt-Zuordnung ohne Komplettierung.
        // Wartende braucht 2x, hat 0 -> nach Zuordnung 1/2 (noch wartend).
        // StorageBinId muss trotzdem erhalten bleiben.
        var binA = await SeedBinAsync("Box A2");
        var binB = await SeedBinAsync("Box B2");

        var waiting = await _sut.PersistAndStoreAsync(MakeInput(
            "wait_bug_a2", "WaitingFig-A2", binA, new[] { ("3001", 11, 2), ("9999", 0, 1) }));
        var donor = await _sut.PersistAndStoreAsync(MakeInput(
            "don_bug_a2", "Donor-A2", binB, new[] { ("3001", 11, 1) }));
        var donorPartId = await GetFirstPartIdAsync(donor.SavedMinifig.Id);

        int waitingPartId;
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            waitingPartId = await ctx.TrackedMinifigParts
                .Where(p => p.TrackedMinifigId == waiting.SavedMinifig.Id && p.PartNumber == "3001")
                .Select(p => p.Id).SingleAsync();
        }

        await _sut.DismantleAsync(donor.SavedMinifig.Id, new[]
        {
            new DismantlePartChoice
            {
                TrackedMinifigPartId = donorPartId,
                IsKept = true,
                AssignToTrackedMinifigPartId = waitingPartId
            }
        });

        await using var verify = await _factory.CreateDbContextAsync();
        var w = await verify.TrackedMinifigs.SingleAsync(m => m.Id == waiting.SavedMinifig.Id);
        Assert.Equal(TrackedMinifigStatus.Waiting, w.Status); // noch wartend
        Assert.NotNull(w.StorageBinId);
        Assert.Equal(binA, w.StorageBinId);
    }

    // ====================================================================
    // UX X.28 Bug B (v0.1.15): StorageBin.FreedAt muss zurueckgesetzt werden
    // wenn neue Items in einen freigegebenen Bin eingehen. Praxis-Befund:
    // Box 001 hatte FreedAt=2026-05-04, am 8.5. wurden dort Minifigs
    // angelegt, FreedAt blieb auf altem Wert -> Bin wirkt "frei" obwohl
    // belegt.
    // ====================================================================

    [Fact]
    public async Task PersistAndStore_into_freed_bin_clears_FreedAt()
    {
        var binId = await SeedBinAsync("Box freed");
        // Bin als freigegeben markieren (simuliert vergangene Freigabe)
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var bin = await ctx.StorageBins.SingleAsync(b => b.Id == binId);
            bin.FreedAt = DateTime.UtcNow.AddDays(-3);
            await ctx.SaveChangesAsync();
        }

        // Act: neue Minifig im freigegebenen Bin anlegen
        await _sut.PersistAndStoreAsync(MakeInput(
            "freed_bug_b", "Reactivated", binId, new[] { ("3001", 11, 1) }));

        // Assert: Bin ist nicht mehr "frei"
        await using var verify = await _factory.CreateDbContextAsync();
        var b2 = await verify.StorageBins.SingleAsync(b => b.Id == binId);
        Assert.Null(b2.FreedAt);
    }

    [Fact]
    public async Task Dismantle_floating_into_freed_bin_clears_FreedAt()
    {
        var donorBin = await SeedBinAsync("Box donor");
        var floatBin = await SeedBinAsync("Box freed-float");

        // floatBin als frei markieren
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var bin = await ctx.StorageBins.SingleAsync(b => b.Id == floatBin);
            bin.FreedAt = DateTime.UtcNow.AddDays(-2);
            await ctx.SaveChangesAsync();
        }

        var donor = await _sut.PersistAndStoreAsync(MakeInput(
            "don_bug_b2", "DonorB2", donorBin, new[] { ("3001", 11, 1) }));
        var donorPartId = await GetFirstPartIdAsync(donor.SavedMinifig.Id);

        // Donor zerlegen und Teil als FloatingPart in den freigegebenen Bin legen
        await _sut.DismantleAsync(donor.SavedMinifig.Id, new[]
        {
            new DismantlePartChoice
            {
                TrackedMinifigPartId = donorPartId,
                IsKept = true,
                TargetBinId = floatBin
            }
        });

        await using var verify = await _factory.CreateDbContextAsync();
        var b2 = await verify.StorageBins.SingleAsync(b => b.Id == floatBin);
        Assert.Null(b2.FreedAt);
    }

    // ====================================================================
    // UX X.28 Bug A (v0.1.15) Regression-Test:
    // App-Start-Cleanup-Pfad darf StorageBinId von Complete-Minifigs NIEMALS
    // antasten. Wenn eine zukuenftige Cleanup-Methode auf die alte Logik
    // zurueckfaellt, faengt dieser Test sie ab.
    // ====================================================================

    [Fact]
    public async Task AppStartCleanups_DoNotResetStorageBinIdOfCompleteMinifigs()
    {
        // Arrange: Bin + Complete Minifig mit StorageBinId.
        // Damit die Figur sofort Complete wird, seeden wir alle benoetigten
        // Teile als FloatingParts -> Reverse-Match konsumiert sie sofort.
        var binId = await SeedBinAsync("TestBox");
        var fpBinId = await SeedBinAsync("FloatingBox");
        await SeedFloatingAsync(fpBinId, "3001", 11, qty: 1);
        await SeedFloatingAsync(fpBinId, "3002", 11, qty: 1);

        var input = MakeInput(
            "test001", "Complete Test Fig", binId,
            new[] { ("3001", 11, 1), ("3002", 11, 1) });
        var result = await _sut.PersistAndStoreAsync(input);
        var minifigId = result.SavedMinifig.Id;

        // Sanity-Check: Figur ist jetzt Complete + hat StorageBinId
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var m = await ctx.TrackedMinifigs.SingleAsync(x => x.Id == minifigId);
            Assert.Equal(TrackedMinifigStatus.Complete, m.Status);
            Assert.Equal(binId, m.StorageBinId);
        }

        // Act: ALLE App-Start-Cleanup-Methoden simulieren
        // (alle die in App.xaml.cs OnStartup nach DataHeal aufgerufen werden).
        var dataHeal = new DataHealService(_factory);
        await dataHeal.HealAsync();
        await _sut.CleanupOldDismantledMinifigsAsync();
        await _sut.CleanupOnePartCompletesAsync();

        // Assert: Complete Minifig hat NACH allen Cleanups noch ihre StorageBinId
        await using var verify = await _factory.CreateDbContextAsync();
        var still = await verify.TrackedMinifigs.SingleAsync(x => x.Id == minifigId);
        Assert.Equal(TrackedMinifigStatus.Complete, still.Status);
        Assert.Equal(binId, still.StorageBinId);

        // Bin darf nicht als "frei" markiert sein (DataHeal heilt das, falls noetig)
        var bin = await verify.StorageBins.SingleAsync(b => b.Id == binId);
        Assert.Null(bin.FreedAt);
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
