using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// v0.1.24-beta.1 Phase 2a Service-Tests fuer
/// <see cref="PartLookupService.CollectMinifigFromSupersetWithSelectionAsync"/>
/// mit neuem optionalem Parameter <c>manuellClaimedParts</c> (Konzept 6.3).
///
/// Schwerpunkte:
/// - manuell-markiert: QuantityCollected wird erhoeht, KEIN FloatingPart-
///   Remove (User hat das Teil unabhaengig vom Lager).
/// - Cap auf QuantityNeeded - schon volle Required-Parts werden uebersprungen.
/// - null/empty als manuellClaimedParts -> Backwards-Compat-Pfad unveraendert.
/// - Mixed-Pfad: Reverse-Match + Manuell zusammen.
/// </summary>
public class PartLookupServiceCollectMinifigTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly PartLookupService _sut;
    private readonly StubCatalog _catalog = new();

    public PartLookupServiceCollectMinifigTests()
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
        _sut = new PartLookupService(
            _factory, _catalog, new NoopPersistence(), new EmptyImageProvider());
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ManuellClaimed_increments_quantity_without_floating_remove()
    {
        // Setup: Minifig mit 2 RequiredParts: TRIG (Trigger) + MISS (Missing).
        // MISS liegt NICHT im Pool, soll aber als manuell-markiert eingetragen
        // werden (User hat das Teil in der Hand).
        var binId = await SeedBinAsync("Box Target");
        var pondBinId = await SeedBinAsync("Box Pool");
        // Setup einen FloatingPart der NICHT in der Konsum-Liste steht,
        // damit wir verifizieren koennen dass nichts entfernt wird.
        await SeedFloatingAsync(pondBinId, "OTHER", 99, qty: 5);

        _catalog.MinifigDetails = NewItem("fig1");
        _catalog.Subsets = new()
        {
            new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
            new() { ItemNo = "MISS", ColorId = 2, Quantity = 1 }
        };

        var result = await _sut.CollectMinifigFromSupersetWithSelectionAsync(
            "fig1", binId,
            triggerPartNo: "TRIG", triggerColorId: 1, triggerQuantity: 1,
            consumePartsFromFloating: new List<(string, int)> { ("TRIG", 1) },
            manuellClaimedParts: new List<(string, int)> { ("MISS", 2) });

        // QuantityCollected fuer MISS muss erhoeht sein.
        await using var ctx = await _factory.CreateDbContextAsync();
        var minifig = await ctx.TrackedMinifigs
            .Include(m => m.RequiredParts)
            .FirstAsync(m => m.BricklinkId == "fig1");
        var miss = minifig.RequiredParts.Single(p => p.PartNumber == "MISS");
        Assert.Equal(1, miss.QuantityCollected);

        // FloatingPart bleibt unveraendert - der OTHER-Eintrag ist NICHT
        // angetastet (manuell-markiert hat NICHTS mit FloatingParts zu tun).
        var floating = await ctx.FloatingParts.ToListAsync();
        Assert.Single(floating);
        Assert.Equal("OTHER", floating[0].PartNumber);
        Assert.Equal(5, floating[0].Quantity);

        // ConsumedFloatingParts darf das manuell-markierte Teil NICHT enthalten.
        Assert.DoesNotContain(result.ConsumedFloatingParts, c => c.BlPartNo == "MISS");

        // Figur ist komplett (alle RequiredParts gefuellt).
        Assert.True(result.IsFullyComplete);

        // ScanEvent enthaelt den Manuell-Suffix.
        var scan = await ctx.ScanEvents.OrderBy(s => s.Id).LastAsync();
        Assert.Contains("manuell markiert", scan.ResultDescription);
    }

    [Fact]
    public async Task ManuellClaimed_clamps_at_quantity_needed()
    {
        // Required-Part mit Quantity=2, Trigger fuellt schon 1, manuell soll
        // auf max QuantityNeeded clampen (in der Service-Impl: stillNeeded
        // wird zur Capacity, mit auf-needed-gesetzt).
        var binId = await SeedBinAsync("Box T");
        _catalog.MinifigDetails = NewItem("fig2");
        _catalog.Subsets = new()
        {
            new() { ItemNo = "P", ColorId = 5, Quantity = 2 },
            new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 }
        };

        var result = await _sut.CollectMinifigFromSupersetWithSelectionAsync(
            "fig2", binId,
            triggerPartNo: "TRIG", triggerColorId: 1, triggerQuantity: 1,
            consumePartsFromFloating: new List<(string, int)> { ("TRIG", 1) },
            manuellClaimedParts: new List<(string, int)> { ("P", 5) });

        await using var ctx = await _factory.CreateDbContextAsync();
        var minifig = await ctx.TrackedMinifigs
            .Include(m => m.RequiredParts)
            .FirstAsync(m => m.BricklinkId == "fig2");

        // P braucht 2x, manuell markiert hat noch 0 - service setzt
        // QuantityCollected auf QuantityNeeded (= 2). Kein Overflow.
        var p = minifig.RequiredParts.Single(r => r.PartNumber == "P");
        Assert.Equal(2, p.QuantityCollected);
        Assert.Equal(2, p.QuantityNeeded);
        Assert.True(result.IsFullyComplete);
    }

    [Fact]
    public async Task ManuellClaimed_null_backwards_compat()
    {
        // null als manuellClaimedParts -> kein Pfad-Aufruf, kein Suffix
        // im ResultDescription, Backwards-Compat zu vor-Phase-2a.
        var binId = await SeedBinAsync("Box BC");
        _catalog.MinifigDetails = NewItem("fig3");
        _catalog.Subsets = new()
        {
            new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
            new() { ItemNo = "P", ColorId = 2, Quantity = 1 }
        };

        var result = await _sut.CollectMinifigFromSupersetWithSelectionAsync(
            "fig3", binId,
            triggerPartNo: "TRIG", triggerColorId: 1, triggerQuantity: 1,
            consumePartsFromFloating: new List<(string, int)> { ("TRIG", 1) },
            manuellClaimedParts: null);

        // Figur ist NICHT komplett - P liegt nicht im Pool und ist nicht
        // manuell markiert -> bleibt wartend.
        Assert.False(result.IsFullyComplete);

        await using var ctx = await _factory.CreateDbContextAsync();
        var scan = await ctx.ScanEvents.OrderBy(s => s.Id).LastAsync();
        // Kein manuell-Suffix darf vorkommen.
        Assert.DoesNotContain("manuell markiert", scan.ResultDescription);
    }

    [Fact]
    public async Task ManuellClaimed_mixed_with_reverse_match()
    {
        // 3 RequiredParts: TRIG, IN_POOL (Reverse-Match), MANUELL.
        // Erwartung: TRIG durch Trigger, IN_POOL durch FloatingPart-Konsum,
        // MANUELL durch Manuell-Pfad. ScanEvent hat beide Hinweise.
        var binId = await SeedBinAsync("Box Mixed");
        var poolBinId = await SeedBinAsync("Box Pool");
        var fpId = await SeedFloatingAsync(poolBinId, "IN_POOL", 3, qty: 1);

        _catalog.MinifigDetails = NewItem("fig4");
        _catalog.Subsets = new()
        {
            new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
            new() { ItemNo = "IN_POOL", ColorId = 3, Quantity = 1 },
            new() { ItemNo = "MANUELL", ColorId = 7, Quantity = 1 }
        };

        var result = await _sut.CollectMinifigFromSupersetWithSelectionAsync(
            "fig4", binId,
            triggerPartNo: "TRIG", triggerColorId: 1, triggerQuantity: 1,
            consumePartsFromFloating: new List<(string, int)>
            {
                ("TRIG", 1), ("IN_POOL", 3)
            },
            manuellClaimedParts: new List<(string, int)> { ("MANUELL", 7) });

        // IN_POOL wurde via FloatingPart konsumiert -> ConsumedFloatingParts
        // enthaelt einen Eintrag, FloatingPart ist weg.
        Assert.Single(result.ConsumedFloatingParts);
        Assert.Equal("IN_POOL", result.ConsumedFloatingParts[0].BlPartNo);
        Assert.Equal("Box Pool", result.ConsumedFloatingParts[0].SourceBinLabel);

        await using var ctx = await _factory.CreateDbContextAsync();
        var floating = await ctx.FloatingParts.FindAsync(fpId);
        Assert.Null(floating); // verbraucht und entfernt

        // Alle 3 Required-Parts sind voll.
        var minifig = await ctx.TrackedMinifigs
            .Include(m => m.RequiredParts)
            .FirstAsync(m => m.BricklinkId == "fig4");
        Assert.All(minifig.RequiredParts, p => Assert.Equal(p.QuantityNeeded, p.QuantityCollected));
        Assert.True(result.IsFullyComplete);

        // ScanEvent dokumentiert beide Pfade. Bei Komplett-Pfad schreibt der
        // Service "direkt komplett (User-Auswahl + 1 manuell markiert)" -
        // wir verifizieren beide Phrasen einzeln.
        var scan = await ctx.ScanEvents.OrderBy(s => s.Id).LastAsync();
        Assert.Contains("User-Auswahl", scan.ResultDescription);
        Assert.Contains("manuell markiert", scan.ResultDescription);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private async Task<int> SeedBinAsync(string label)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = new StorageBin { Label = label, CreatedAt = DateTime.UtcNow };
        ctx.StorageBins.Add(bin);
        await ctx.SaveChangesAsync();
        return bin.Id;
    }

    private async Task<int> SeedFloatingAsync(int binId, string partNo, int colorId, int qty)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var fp = new FloatingPart
        {
            PartNumber = partNo,
            ColorId = colorId,
            Quantity = qty,
            StorageBinId = binId,
            AddedAt = DateTime.UtcNow
        };
        ctx.FloatingParts.Add(fp);
        await ctx.SaveChangesAsync();
        return fp.Id;
    }

    private static BlItem NewItem(string blId) => new()
    {
        ItemType = "M",
        ItemNo = blId,
        Name = $"Test {blId}",
        DataCompleteness = DataCompleteness.Full,
        FetchedAt = DateTime.UtcNow
    };

    // ----- Stubs -----

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    /// <summary>Minimaler Catalog-Stub mit settable Properties.</summary>
    private sealed class StubCatalog : IBlCatalogService
    {
        public BlItem? MinifigDetails { get; set; }
        public List<BlSubset> Subsets { get; set; } = new();

        public Task<BlItem?> GetMinifigDetailsAsync(string blMinifigId, CancellationToken ct = default)
            => Task.FromResult(MinifigDetails);
        public Task<List<BlSubset>> GetMinifigPartsAsync(string blMinifigId, CancellationToken ct = default)
            => Task.FromResult(Subsets);
        public Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<BlColor>());
        public Task<bool> EnsureFullSubsetsAsync(string blMinifigId, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<BlItem?> GetPartDetailsAsync(string blPartNo, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<string>> FindWaitingMinifigsForPartAsync(string blPartNo, int blColorId,
            IEnumerable<string> waitingMinifigIds, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<BlCacheStats> GetCacheStatsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearCacheAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearStaleAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlSubset>> GetSupersetsAsync(string blPartNo, int blColorId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<BlColor>> GetKnownColorsAsync(string blPartNo, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(string blPartNo,
            int blColorId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<PartComponent>> GetPartComponentsAsync(string blPartNo, int blColorId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    /// <summary>RaiseDataChanged-NoOp - die Anzahl ist fuer diese Tests irrelevant.</summary>
    private sealed class NoopPersistence : IMinifigPersistenceService
    {
        public event EventHandler? DataChanged;
        public void RaiseDataChanged() => DataChanged?.Invoke(this, EventArgs.Empty);
        public Task<PersistMinifigResult> PersistAndStoreAsync(PersistMinifigInput input, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DismantleResult> DismantleAsync(int trackedMinifigId, IEnumerable<DismantlePartChoice> choices, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> CheckAndMarkCompleteAsync(int trackedMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReopenAsync(int trackedMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(int trackedMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> RemoveExportedMinifigsAsync(IEnumerable<int> minifigIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> RemoveExportedFloatingPartsAsync(IEnumerable<int> floatingPartIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CleanupOldDismantledMinifigsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CleanupOnePartCompletesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(int Minifigs, int FloatingParts)> DeleteSelectionAsync(IEnumerable<int> minifigIds, IEnumerable<int> floatingPartIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(int Minifigs, int FloatingParts)> MoveSelectionAsync(IEnumerable<int> minifigIds, IEnumerable<int> floatingPartIds, int targetBinId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>Image-Provider, der leere Strings zurueckliefert (Bild ist optional).</summary>
    private sealed class EmptyImageProvider : IPartImageProvider
    {
        public Task<string> GetImageFileAsync(BrickognizeItem item, int? bricklinkColorId, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
        public Task<string> GetImageFileByBlAsync(string bricklinkType, string bricklinkId, int? bricklinkColorId, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }
}
