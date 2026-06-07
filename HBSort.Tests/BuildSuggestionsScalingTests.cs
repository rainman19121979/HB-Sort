using System.Diagnostics;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Models.Pricing;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// BUILD-3 (N+1-Hang im Baubar-Tab): End-to-End-Skalierungs-Test.
///
/// HINTERGRUND: Bei Holgers echtem ~9k-BL-Inventar liefert
/// <see cref="IBlCacheRepository.FindMinifigsContainingPartsAsync"/> (seit
/// BUILD-1 + Temp-Tabellen-Fix) tausende Minifig-Kandidaten. Die alte N+1-
/// Schleife in <see cref="BuildSuggestionsViewModel.RefreshAsync"/> machte
/// PRO Kandidat zwei serialisierte Repository-Queries (GetSubsetsAsync +
/// GetItemAsync), AnalyzeBlShopHelpAsync zusaetzlich pro Subset eine
/// FindLotsForPartAsync. Alle unter dem repo-internen <c>_lock</c> -> bei
/// tausenden Kandidaten sekunden- bis minutenlanger UI-Hang.
///
/// WARUM ECHTE SERVICES (kein Fake): Die schnellen Dictionary-Fakes aus
/// <see cref="BuildSuggestionsViewModelTests"/> bilden die echte Per-Query-
/// Latenz unter dem Lock NICHT ab und wuerden den Hang daher GAR NICHT
/// fangen (Lehre aus dem 2000-Tuple-Test, der den Crash, nicht den Hang
/// fand). Dieser Test nutzt deshalb:
///   - das ECHTE <see cref="BlCacheRepository"/> (Tempfile-SQLite, Schema
///     via Embedded-Resource) mit realer Per-Query-Latenz unter <c>_lock</c>,
///   - den ECHTEN <see cref="BlInventoryService"/> ueber eine In-Memory-
///     SQLite-UserDataContext-Factory.
///
/// Datenmix (Haertetest mit leerem FloatingPool):
///   - <see cref="MinifigCount"/> Minifigs im BL-Cache, je <see cref="SubsetsPerMinifig"/>
///     Subsets -> alle werden Kandidaten (der Reverse-Lookup trifft sie alle,
///     weil ihre Teile im BL-Pool liegen). Hohe Kandidaten-Zahl = N+1-Stress.
///   - <see cref="PartUniverse"/> verfuegbare BL-Inventar-Lots (moderater Pool,
///     damit der hier nicht angefasste Temp-Tabellen-Root-Fix nicht selbst zum
///     Flaschenhals wird - siehe Datenmix-Kommentar).
///   - FloatingPool LEER (Haertetest: laeuft das durch, laeuft auch der Mischfall).
///   - Toggle = AN (BL-Pool aktiv + AnalyzeBlShopHelpAsync laeuft).
/// </summary>
public class BuildSuggestionsScalingTests : IDisposable
{
    // Datenmix-Parameter. Die Werte stressen gezielt die N+1-DIMENSION (Zahl
    // der Kandidaten-Minifigs), die diese Iteration fixt - NICHT die Pool-
    // Groesse, die in den (hier nicht angefassten) Temp-Tabellen-Root-Fix von
    // FindMinifigsContainingPartsAsync laeuft.
    //
    // Lehre aus der Diagnose: FindMinifigsContainingPartsAsync skaliert mit der
    // ZAHL der Pool-Tuples (Single-Row-INSERTs in die Temp-Tabelle), die N+1-
    // Schleife dagegen mit der ZAHL der KANDIDATEN. Wuerden wir den Pool gross
    // machen (5000+ Tuples), dominierte der untouchable Root-Fix das Timing und
    // der Test bewiese den N+1-Fix NICHT. Darum: Pool moderat (PartUniverse),
    // Kandidaten-Zahl hoch.
    //
    // GEMESSENE TRENNUNG bei diesen Werten (10000 Kandidaten, je 5 Subsets,
    // 400-Teile-Pool, FloatingPool leer, Toggle AN), je 3-4 Laeufe:
    //   - ALTER N+1-Code (GetSubsetsAsync + GetItemAsync pro Kandidat):
    //     2,27 / 2,41 / 2,48 s  -> reisst die 2s-Schranke zuverlaessig.
    //   - NEUER Bulk-Code (GetSubsetsBulkAsync + GetItemSummariesAsync +
    //     1x GetAvailableQuantitiesAsync, dann In-Memory-Iteration):
    //     1,63 / 1,65 / 1,74 s  -> haelt die 2s-Schranke zuverlaessig.
    // (Die urspruengliche Diagnose mass den End-to-End-Hang mit grossem Pool
    //  bei 6,5s - dort steckt der Root-Fix-Anteil mit drin.)
    private const int MinifigCount = 10000;      // Kandidaten-Zahl -> N+1-Stress
    private const int SubsetsPerMinifig = 5;     // ~50.000 bl_subsets-Eintraege
    private const int PartUniverse = 400;        // moderater Pool (400 BL-Lots/Tuples)

    private readonly string _blCacheDir = Path.Combine(
        Path.GetTempPath(), $"lego-buildscale-{Guid.NewGuid():N}");
    private readonly BlCacheRepository _blCache;

    private readonly SqliteConnection _userConn;
    private readonly DbContextOptions<UserDataContext> _options;
    private readonly SimpleContextFactory _ctxFactory;
    private readonly BlInventoryService _blInventory;

    public BuildSuggestionsScalingTests()
    {
        // --- Echtes BL-Cache-Repository (Tempfile-SQLite) ---
        Directory.CreateDirectory(_blCacheDir);
        _blCache = new BlCacheRepository(Path.Combine(_blCacheDir, "bl_cache.db"));

        // --- Echte UserDataContext-Factory (In-Memory-SQLite) ---
        _userConn = new SqliteConnection("DataSource=:memory:");
        _userConn.Open();
        _options = new DbContextOptionsBuilder<UserDataContext>()
            .UseSqlite(_userConn)
            .Options;
        using (var ctx = new UserDataContext(_options))
            ctx.Database.EnsureCreated();
        _ctxFactory = new SimpleContextFactory(_options);

        // Echter BlInventoryService - braucht nur die DbContext-Factory fuer die
        // Read-Pfade (GetAvailablePartTuplesAsync / FindLotsForPartAsync /
        // HasAnyInventoryAsync), die RefreshAsync nutzt.
        _blInventory = new BlInventoryService(_ctxFactory, new ThrowingBricklinkClient());
    }

    public void Dispose()
    {
        _blCache.Dispose();
        _userConn.Dispose();
        try { if (Directory.Exists(_blCacheDir)) Directory.Delete(_blCacheDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task RefreshAsync_withThousandsOfCandidates_completesUnderTwoSeconds()
    {
        await SeedAsync();

        var vm = new BuildSuggestionsViewModel(
            _ctxFactory,
            _blCache,
            new StubImageProvider(),
            new StubPersistence(),
            _blInventory,
            new StubSettings(),
            new StubPriceFactory(),
            new StubNotification());

        // Toggle aktivieren (loest einen fire-and-forget-Refresh aus). Danach
        // einen WARM-UP-Refresh awaiten, damit alle ctor-/Toggle-getriggerten
        // fire-and-forget-Refreshes durch sind und nicht waehrend der Messung
        // um den Repo-Lock konkurrieren (sonst verrauscht das Timing).
        vm.IncludeBlInventory = true;
        await vm.RefreshAsync(); // Warm-up, NICHT gemessen

        var sw = Stopwatch.StartNew();
        await vm.RefreshAsync();
        sw.Stop();

        // SCHRANKEN-BEGRUENDUNG: 2s liegt klar zwischen dem alten N+1-Code
        // (gemessen 2,27-2,48s bei dieser Datengroesse) und dem Bulk-Code
        // (gemessen 1,63-1,74s). ~0,3s Sicherheitsabstand zu beiden Seiten -
        // eng genug, um die N+1-Regression sicher zu fangen, weit genug, um
        // nicht zu flaken. (Siehe Messreihe im Datenmix-Kommentar oben.)
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(2),
            $"RefreshAsync brauchte {sw.Elapsed.TotalSeconds:F2}s - erwartet < 2s. " +
            "N+1-Regression? (Bulk-Pfad nicht aktiv.)");

        // Ergebnis-Plausibilitaet: bei leerem FloatingPool + Toggle AN muessen
        // die voll BL-baubaren Figuren als Shop-completable erscheinen (Take 20).
        Assert.NotEmpty(vm.Suggestions);
        Assert.All(vm.Suggestions, s => Assert.True(s.IsBlShopAddition));
    }

    // ====================================================================
    // Seeding
    // ====================================================================

    private async Task SeedAsync()
    {
        // Jede Minifig braucht SubsetsPerMinifig verschiedene Teile. Damit jede
        // Minifig komplett aus dem BL-Pool baubar ist, legen wir genau diese
        // Teile als verfuegbare BL-Lots an. Die ersten Teile teilen sich mehrere
        // Figuren (realistisch: gleiche Koepfe/Torsos), der Rest ist figur-eigen.
        var subsets = new List<BlSubset>(MinifigCount * SubsetsPerMinifig);
        var items = new List<BlItem>(MinifigCount);

        // Teile-Universum: ein gemeinsamer, moderater Pool, aus dem jede Figur
        // SubsetsPerMinifig Teile zieht. Bewusst klein, damit sich Teile stark
        // ueberlappen (so wie bei echten Minifigs - viele teilen sich Koepfe/
        // Torsos) und der untouchable Temp-Tabellen-Root-Fix nicht zum
        // Flaschenhals wird. Trotzdem werden ALLE 3000 Figuren Kandidaten.
        int partUniverse = PartUniverse;

        for (int m = 0; m < MinifigCount; m++)
        {
            var blId = $"fig{m:00000}";
            items.Add(new BlItem
            {
                ItemType = "M",
                ItemNo = blId,
                Name = $"Scaling Fig {m}",
                DataCompleteness = DataCompleteness.Subset
            });

            for (int p = 0; p < SubsetsPerMinifig; p++)
            {
                // Deterministische Teil-Auswahl mit Ueberlappung ueber Figuren.
                int partIndex = (m * SubsetsPerMinifig + p) % partUniverse;
                subsets.Add(new BlSubset
                {
                    ParentType = "M",
                    ParentNo = blId,
                    ItemType = "P",
                    ItemNo = $"part{partIndex:00000}",
                    ColorId = 1 + (partIndex % 10), // 10 Farben
                    Quantity = 1,
                    IsFromSupersets = false,
                    FetchedAt = DateTime.UtcNow
                });
            }
        }

        await _blCache.UpsertItemsAsync(items);
        await _blCache.BulkInsertSubsetsAsync(subsets);

        // BL-Inventar: pro Teil im Universum ein verfuegbares Lot. So ist jede
        // Figur komplett aus dem Shop baubar -> alle 3000 Figuren werden
        // Kandidaten (maximaler N+1-Stress).
        await using var ctx = new UserDataContext(_options);
        var lots = new List<BlInventoryLot>(PartUniverse);
        for (int i = 0; i < PartUniverse; i++)
        {
            lots.Add(new BlInventoryLot
            {
                LotId = i + 1,
                ItemType = "P",
                ItemNo = $"part{i:00000}",
                ColorId = 1 + (i % 10),
                Quantity = 10,
                ReservedQuantity = 0,
                Condition = "U",
                LastSyncedAt = DateTime.UtcNow
            });
        }
        ctx.BlInventoryLots.AddRange(lots);
        await ctx.SaveChangesAsync();
        // FloatingPool bleibt LEER (Haertetest).
    }

    // ====================================================================
    // Stubs (minimal, identisch zu BuildSuggestionsViewModelTests)
    // ====================================================================

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// BL-Client der nie aufgerufen werden darf - RefreshAsync nutzt nur die
    /// DB-Read-Pfade des BlInventoryService, keinen Sync. Jeder Aufruf kracht.
    /// </summary>
    private sealed class ThrowingBricklinkClient : IBricklinkClient
    {
        public Task<bool> IsConfiguredAsync() => throw new NotImplementedException();
        public Task<BricklinkTestResult> TestConnectionAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BricklinkItemDto> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkSubsetDto>> GetSubsetsAsync(string itemType, string itemNo, bool breakMinifigs, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkColorDto>> GetColorListAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkSupersetEntryDto>> GetSupersetsAsync(string itemType, string itemNo, int colorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkKnownColorDto>> GetKnownColorsAsync(string itemType, string itemNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BricklinkInventoryLotDto>> GetInventoryAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubImageProvider : IPartImageProvider
    {
        public Task<string> GetImageFileAsync(BrickognizeItem item, int? bricklinkColorId, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> GetImageFileByBlAsync(string bricklinkType, string bricklinkId, int? bricklinkColorId, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }

    private sealed class StubSettings : ISettingsService
    {
        private readonly AppSettings _settings = new() { ScoreThresholdMin = 0.5 };
        public AppSettings Current => _settings;
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }

    private sealed class StubPriceFactory : IPriceProviderFactory
    {
        public IPriceProvider GetActiveProvider() => new StubPriceProvider();
    }

    private sealed class StubPriceProvider : IPriceProvider
    {
        public string Name => "Stub";
        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<PriceResult?> GetMinifigPriceAsync(string blMinifigId, CancellationToken ct = default) => Task.FromResult<PriceResult?>(null);
        public Task<PriceResult?> GetPartPriceAsync(string blPartNo, int blColorId, CancellationToken ct = default) => Task.FromResult<PriceResult?>(null);
    }

    private sealed class StubNotification : INotificationService
    {
        public void ShowInfo(string message) { }
        public void ShowSuccess(string message, string? imageUrl = null) { }
        public void ShowWarning(string message) { }
        public void ShowError(string message) { }
    }

    private sealed class StubPersistence : IMinifigPersistenceService
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
}
