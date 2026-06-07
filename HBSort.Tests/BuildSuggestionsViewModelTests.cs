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
/// Charakterisierungs-/Sicherheitsnetz-Tests fuer
/// <see cref="BuildSuggestionsViewModel.RefreshAsync"/>.
///
/// HINTERGRUND: Die gesamte Baubar-Logik liegt in der VM (kein dedizierter
/// Service) und war bis hierher KOMPLETT ungetestet. Diese Tests sind das
/// Regressions-Sicherheitsnetz VOR dem BUILD-1-Fix.
///
/// BUILD-1 wird den Kandidaten-Pool um BL-Inventar-Lots erweitern (rein-BL-
/// baubare Figuren sollen erscheinen) + den Early-Return bei aktivem Toggle
/// aufweichen. Diese Tests sichern daher die INVARIANTEN ab, die BUILD-1
/// NICHT aendern darf, und halten das (noch falsche) BL-only-Verhalten in
/// EINEM klar markierten Charakterisierungs-Test fest.
///
/// Stub-Strategie:
///   - In-Memory-SQLite + EnsureCreated (identisch zu InventoryListViewModelTests).
///   - FakeBlCacheRepository: nur FindMinifigsContainingPartsAsync / GetSubsetsAsync
///     / GetItemAsync haben echtes Verhalten (datengetrieben ueber Dictionaries),
///     alle anderen ~40 Methoden werfen NotImplementedException.
///   - FakeBlInventoryService: HasAnyInventoryAsync / FindLotsForPartAsync /
///     GetInventoryAsync datengetrieben, Rest NotImplementedException.
///   - Settings/Notification/ImageProvider/PriceFactory: minimale Stubs.
/// </summary>
public class BuildSuggestionsViewModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<UserDataContext> _options;
    private readonly SimpleContextFactory _ctxFactory;

    private readonly FakeBlCacheRepository _blCache = new();
    private readonly FakeBlInventoryService _blInventory = new();
    private readonly StubSettings _settings = new();
    private readonly StubPersistence _persistence = new();

    public BuildSuggestionsViewModelTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<UserDataContext>()
            .UseSqlite(_connection)
            .Options;
        using (var ctx = new UserDataContext(_options))
        {
            ctx.Database.EnsureCreated();
        }
        _ctxFactory = new SimpleContextFactory(_options);
    }

    public void Dispose() => _connection.Dispose();

    // ====================================================================
    // Test-Setup-Helfer
    // ====================================================================

    /// <summary>
    /// Erzeugt die VM und wartet den initialen RefreshAsync (im ctor gestartet)
    /// ab, indem wir RefreshAsync nochmal explizit awaiten. Der ctor-Refresh ist
    /// fire-and-forget; ein zweiter awaited Aufruf liefert den deterministischen
    /// Endzustand.
    /// </summary>
    private async Task<BuildSuggestionsViewModel> CreateAndRefreshAsync(bool includeBl = false)
    {
        var vm = new BuildSuggestionsViewModel(
            _ctxFactory,
            _blCache,
            new StubImageProvider(),
            _persistence,
            _blInventory,
            _settings,
            new StubPriceFactory(),
            new StubNotification());

        if (includeBl)
        {
            // Setzt das Property; OnIncludeBlInventoryChanged loest einen Refresh aus.
            vm.IncludeBlInventory = true;
        }

        // Deterministischer Endzustand - ueberschreibt das Ergebnis des
        // fire-and-forget-ctor-Refreshes.
        await vm.RefreshAsync();
        return vm;
    }

    private async Task AddFloatingPartAsync(string partNo, int colorId, int qty)
    {
        await using var ctx = new UserDataContext(_options);
        // FloatingPart braucht ein existierendes Bin (FK). Wir legen einmalig
        // ein Sammel-Bin an.
        var bin = await ctx.StorageBins.FirstOrDefaultAsync();
        if (bin == null)
        {
            bin = new StorageBin { Label = "TestBin", CreatedAt = DateTime.UtcNow, Kind = StorageBinKind.Floating };
            ctx.StorageBins.Add(bin);
            await ctx.SaveChangesAsync();
        }
        ctx.FloatingParts.Add(new FloatingPart
        {
            PartNumber = partNo,
            ColorId = colorId,
            ColorName = "Color",
            PartName = "Part",
            Quantity = qty,
            StorageBinId = bin.Id,
            AddedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    private async Task AddTrackedMinifigAsync(string bricklinkId, TrackedMinifigStatus status)
    {
        await using var ctx = new UserDataContext(_options);
        ctx.TrackedMinifigs.Add(new TrackedMinifig
        {
            FigNum = bricklinkId,
            BricklinkId = bricklinkId,
            Name = "Tracked " + bricklinkId,
            Status = status,
            CreatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    private async Task AddIgnoredAsync(string bricklinkId)
    {
        await using var ctx = new UserDataContext(_options);
        ctx.IgnoredBuildSuggestions.Add(new IgnoredBuildSuggestion
        {
            BricklinkId = bricklinkId,
            IgnoredAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Definiert eine Minifig im BL-Cache-Fake: Reverse-Lookup-Treffer + Subsets +
    /// Item-Name. So entsteht ein Kandidat fuer RefreshAsync.
    /// </summary>
    private void DefineMinifig(string blId, string name, params (string partNo, int colorId, int qty)[] parts)
    {
        _blCache.Minifigs[blId] = name;
        _blCache.Subsets[blId] = parts.Select(p => new BlSubset
        {
            ParentType = "M",
            ParentNo = blId,
            ItemType = "P",
            ItemNo = p.partNo,
            ColorId = p.colorId,
            Quantity = p.qty,
            IsFromSupersets = false
        }).ToList();
    }

    // ====================================================================
    // Invariante: FloatingPool-basierter Vollmatch erscheint
    // ====================================================================

    [Fact]
    public async Task FullyCoverableFromFloatingPool_appears_as_full_match()
    {
        // Figur braucht 1x 3001/red, 1x 3002/blue. Beide voll im Pool.
        DefineMinifig("fig100", "Full Fig", ("3001", 4, 1), ("3002", 1, 1));
        await AddFloatingPartAsync("3001", 4, 1);
        await AddFloatingPartAsync("3002", 1, 1);

        var vm = await CreateAndRefreshAsync();

        var item = Assert.Single(vm.Suggestions, s => s.BricklinkId == "fig100");
        Assert.Equal(100, item.MatchPercent);
        Assert.Equal(0, item.MissingPartsCount);
        Assert.False(item.IsBlShopAddition);
    }

    // ====================================================================
    // Invariante: Threshold-Verhalten (ScoreThresholdMin)
    // ====================================================================

    [Fact]
    public async Task PartialMatch_justBelowThreshold_isExcluded()
    {
        // Schwellwert 0.5 -> 50%. Figur mit 2 von 5 Teilen = 40% -> unter 50%.
        _settings.ScoreThresholdMin = 0.5;
        DefineMinifig("figLow", "Low Match",
            ("3001", 4, 1), ("3002", 1, 1), ("3003", 1, 1), ("3004", 1, 1), ("3005", 1, 1));
        await AddFloatingPartAsync("3001", 4, 1);
        await AddFloatingPartAsync("3002", 1, 1);

        var vm = await CreateAndRefreshAsync();

        Assert.DoesNotContain(vm.Suggestions, s => s.BricklinkId == "figLow");
    }

    [Fact]
    public async Task PartialMatch_atThreshold_isIncluded()
    {
        // Schwellwert 0.5 -> 50%. Figur mit 3 von 6 Teilen = exakt 50% -> >= Schwelle.
        _settings.ScoreThresholdMin = 0.5;
        DefineMinifig("figMid", "Mid Match",
            ("3001", 4, 1), ("3002", 1, 1), ("3003", 1, 1),
            ("3004", 1, 1), ("3005", 1, 1), ("3006", 1, 1));
        await AddFloatingPartAsync("3001", 4, 1);
        await AddFloatingPartAsync("3002", 1, 1);
        await AddFloatingPartAsync("3003", 1, 1);

        var vm = await CreateAndRefreshAsync();

        var item = Assert.Single(vm.Suggestions, s => s.BricklinkId == "figMid");
        Assert.Equal(50, item.MatchPercent);
    }

    // ====================================================================
    // Invariante: getrackte Figuren ausgeschlossen (alle Status)
    // ====================================================================

    [Fact]
    public async Task TrackedMinifig_status_Complete_isExcluded()
    {
        DefineMinifig("figC", "Complete Tracked", ("3001", 4, 1));
        await AddFloatingPartAsync("3001", 4, 1);
        await AddTrackedMinifigAsync("figC", TrackedMinifigStatus.Complete);

        var vm = await CreateAndRefreshAsync();

        Assert.DoesNotContain(vm.Suggestions, s => s.BricklinkId == "figC");
    }

    [Fact]
    public async Task TrackedMinifig_status_Waiting_isExcluded()
    {
        // BUILD-3-Regressionsschutz: getrackte WARTENDE Figuren bleiben aus den
        // Bauvorschlaegen ausgeschlossen. Diese Invariante MUSS dokumentiert
        // bleiben bis BUILD-3 separat darueber entscheidet.
        DefineMinifig("figW", "Waiting Tracked", ("3001", 4, 1));
        await AddFloatingPartAsync("3001", 4, 1);
        await AddTrackedMinifigAsync("figW", TrackedMinifigStatus.Waiting);

        var vm = await CreateAndRefreshAsync();

        Assert.DoesNotContain(vm.Suggestions, s => s.BricklinkId == "figW");
    }

    // ====================================================================
    // Invariante: Ignorier-Filter (IgnoredBuildSuggestion)
    // ====================================================================

    [Fact]
    public async Task IgnoredMinifig_isExcluded_and_counted()
    {
        DefineMinifig("figIgn", "Ignored Fig", ("3001", 4, 1));
        await AddFloatingPartAsync("3001", 4, 1);
        await AddIgnoredAsync("figIgn");

        var vm = await CreateAndRefreshAsync();

        Assert.DoesNotContain(vm.Suggestions, s => s.BricklinkId == "figIgn");
        Assert.Equal(1, vm.IgnoredCount);
        Assert.True(vm.HasIgnored);
    }

    // ====================================================================
    // Invariante: leerer Floating-Pool -> Early-Return + Status-Meldung
    // ====================================================================

    [Fact]
    public async Task EmptyFloatingPool_returns_empty_with_status_message()
    {
        // Kein FloatingPart angelegt. Auch wenn ein Kandidat im Cache existiert,
        // darf nichts erscheinen (Early-Return bei floats.Count == 0).
        DefineMinifig("figX", "Should Not Appear", ("3001", 4, 1));

        var vm = await CreateAndRefreshAsync();

        Assert.Empty(vm.Suggestions);
        Assert.Equal("Keine losen Teile vorhanden.", vm.SummaryText);
        // Reverse-Lookup darf bei leerem Pool gar nicht erst aufgerufen werden.
        Assert.False(_blCache.FindMinifigsCalled);
    }

    // ====================================================================
    // Invariante: Take(20)-Limit
    // ====================================================================

    [Fact]
    public async Task MoreThan20_candidates_are_capped_at_20()
    {
        // 25 voll-baubare Figuren, alle teilen sich dasselbe eine Teil.
        await AddFloatingPartAsync("3001", 4, 100);
        for (int i = 0; i < 25; i++)
        {
            DefineMinifig($"figMany{i:00}", $"Fig {i}", ("3001", 4, 1));
        }

        var vm = await CreateAndRefreshAsync();

        Assert.Equal(20, vm.Suggestions.Count);
    }

    // ====================================================================
    // CHARAKTERISIERUNG (vor BUILD-1): BL-only-Figur erscheint NICHT
    // ====================================================================

    [Fact]
    public async Task BlOnlyFigure_withToggleOn_doesNotAppear_currentBehavior()
    {
        // CHARAKTERISIERUNG: aktuelles Verhalten vor BUILD-1-Fix - eine Figur
        // die KEIN Teil im FloatingPool hat, aber komplett aus BL-Inventar
        // baubar waere, erscheint NICHT. Grund: der Kandidaten-Pool wird
        // ausschliesslich aus FindMinifigsContainingPartsAsync (FloatingPool)
        // gebildet - BL-Lots erweitern den Pool nicht.
        // Dieser Test wird mit BUILD-1 invertiert/ersetzt.

        // FloatingPool enthaelt nur ein irrelevantes Teil (sonst Early-Return),
        // das zu KEINER Figur gehoert.
        await AddFloatingPartAsync("9999", 0, 1);

        // figBl braucht 3001/red, liegt NICHT im Pool, aber im BL-Shop.
        DefineMinifig("figBl", "BL Only Fig", ("3001", 4, 1));
        _blInventory.HasAny = true;
        _blInventory.Lots["3001:4"] = new List<BlInventoryLot>
        {
            new() { LotId = 1, ItemType = "P", ItemNo = "3001", ColorId = 4, Quantity = 5, ReservedQuantity = 0, Condition = "U" }
        };

        var vm = await CreateAndRefreshAsync(includeBl: true);

        Assert.DoesNotContain(vm.Suggestions, s => s.BricklinkId == "figBl");
    }

    // ====================================================================
    // Toggle-Verhalten: BL-Shop-Ergaenzung nur bei IncludeBlInventory=true
    // ====================================================================

    [Fact]
    public async Task BlShopAddition_onlyAppears_whenToggleOn()
    {
        // Figur hat 1 von 2 Teilen im Pool (50%), das fehlende Teil liegt im
        // BL-Shop. Toggle AUS: erscheint als Teilmatch (50%, kein Shop-Badge).
        // Toggle AN: wird zu Kat.2 (Shop-completable, 100% + Shop-Badge).
        _settings.ScoreThresholdMin = 0.5;
        DefineMinifig("figShop", "Shop Completable", ("3001", 4, 1), ("3002", 1, 1));
        await AddFloatingPartAsync("3001", 4, 1);

        _blInventory.HasAny = true;
        _blInventory.Lots["3002:1"] = new List<BlInventoryLot>
        {
            new() { LotId = 2, ItemType = "P", ItemNo = "3002", ColorId = 1, Quantity = 3, ReservedQuantity = 0, Condition = "U" }
        };

        // --- Toggle AUS ---
        var vmOff = await CreateAndRefreshAsync(includeBl: false);
        var itemOff = Assert.Single(vmOff.Suggestions, s => s.BricklinkId == "figShop");
        Assert.Equal(50, itemOff.MatchPercent);
        Assert.False(itemOff.IsBlShopAddition);
        Assert.False(itemOff.HasBlShopContribution);

        // --- Toggle AN ---
        var vmOn = await CreateAndRefreshAsync(includeBl: true);
        var itemOn = Assert.Single(vmOn.Suggestions, s => s.BricklinkId == "figShop");
        Assert.True(itemOn.IsBlShopAddition);
        Assert.Equal(100, itemOn.MatchPercent);
        Assert.Equal(1, itemOn.BlShopPartCount);
    }

    // ====================================================================
    // Fakes / Stubs
    // ====================================================================

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Datengetriebener BL-Cache-Fake. Nur die drei von RefreshAsync genutzten
    /// Methoden haben echtes Verhalten; der Rest wirft (im Test nicht aufgerufen).
    /// </summary>
    private sealed class FakeBlCacheRepository : IBlCacheRepository
    {
        /// <summary>blId -> Name (definiert via DefineMinifig).</summary>
        public Dictionary<string, string> Minifigs { get; } = new();
        /// <summary>blId -> Subset-Liste.</summary>
        public Dictionary<string, List<BlSubset>> Subsets { get; } = new();
        public bool FindMinifigsCalled { get; private set; }

        public Task<List<string>> FindMinifigsContainingPartsAsync(
            IReadOnlyList<(string PartNo, int ColorId)> parts, CancellationToken ct = default)
        {
            FindMinifigsCalled = true;
            var have = parts.ToHashSet();
            // Liefert alle definierten Minifigs, die mindestens eines der
            // uebergebenen (PartNo, ColorId)-Paare in ihren Subsets haben.
            var result = Subsets
                .Where(kv => kv.Value.Any(s => have.Contains((s.ItemNo, s.ColorId))))
                .Select(kv => kv.Key)
                .ToList();
            return Task.FromResult(result);
        }

        public Task<List<BlSubset>> GetSubsetsAsync(string parentType, string parentNo, CancellationToken ct = default)
        {
            Subsets.TryGetValue(parentNo, out var list);
            return Task.FromResult(list ?? new List<BlSubset>());
        }

        public Task<BlItem?> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default)
        {
            if (Minifigs.TryGetValue(itemNo, out var name))
                return Task.FromResult<BlItem?>(new BlItem { ItemType = "M", ItemNo = itemNo, Name = name });
            return Task.FromResult<BlItem?>(null);
        }

        // --- Rest: nicht von RefreshAsync genutzt ---
        public Task<Dictionary<string, string>> GetItemNamesAsync(IEnumerable<string> partNumbers, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertItemAsync(BlItem item, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertItemsAsync(IEnumerable<BlItem> items, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> IsItemStaleAsync(string itemType, string itemNo, int staleDays, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<string, int>> GetCategoryIdsForPartsAsync(IEnumerable<string> partNumbers, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<(string ItemType, string ItemNo), BlItemSummary>> GetItemSummariesAsync(IEnumerable<(string ItemType, string ItemNo)> keys, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplaceSubsetsAsync(string parentType, string parentNo, IEnumerable<BlSubset> subsets, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> BulkInsertSubsetsAsync(IEnumerable<BlSubset> subsets, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> FindParentsByItemAsync(string itemType, string itemNo, int colorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(string blPartNo, int blColorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertColorsAsync(IEnumerable<BlColor> colors, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<int>> GetKnownColorIdsAsync(string partNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DateTime?> GetKnownColorsFetchedAtAsync(string partNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplaceKnownColorsAsync(string partNo, IEnumerable<int> colorIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BlCacheStats> GetStatsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearStaleAsync(int staleDays, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task LogApiCallAsync(string method, string? itemType, string? itemNo, int responseTimeMs, int statusCode, bool success, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetCallCountInWindowAsync(TimeSpan window, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetCallCountSinceAsync(DateTime since, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DateTime?> GetOldestCallInWindowAsync(TimeSpan window, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneApiCallLogAsync(int olderThanDays = 7, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PriceResult?> GetCachedPriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, int staleDays, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task UpsertPriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, PriceResult price, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task<CachedPriceLookup?> GetCachedPriceWithStaleFlagAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, int ttlDays, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task<bool> DeletePriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task<int> ClearAllPricesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearEmptyPricesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetPriceCacheCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>
    /// Datengetriebener BL-Inventar-Fake. HasAnyInventoryAsync + FindLotsForPartAsync
    /// haben echtes Verhalten; der Rest wirft (von RefreshAsync nicht aufgerufen).
    /// </summary>
    private sealed class FakeBlInventoryService : IBlInventoryService
    {
        public bool HasAny { get; set; }
        /// <summary>Schluessel "partNo:colorId" -> Lots.</summary>
        public Dictionary<string, List<BlInventoryLot>> Lots { get; } = new();

        public event EventHandler? InventoryChanged;

        public Task<bool> HasAnyInventoryAsync(CancellationToken ct = default) => Task.FromResult(HasAny);

        public Task<List<BlInventoryLot>> FindLotsForPartAsync(string blPartNo, int? colorId, CancellationToken ct = default)
        {
            var key = $"{blPartNo}:{colorId}";
            Lots.TryGetValue(key, out var list);
            // Echte Implementierung filtert Available <= 0 raus.
            var available = (list ?? new List<BlInventoryLot>())
                .Where(l => l.Quantity - l.ReservedQuantity > 0)
                .ToList();
            return Task.FromResult(available);
        }

        public void RaiseInventoryChanged() => InventoryChanged?.Invoke(this, EventArgs.Empty);

        public Task<BlInventorySyncResult> SyncInventoryAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlInventoryLot>> GetInventoryAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReserveAsync(int lotId, int qty = 1, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReleaseAsync(int lotId, int qty = 1, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReserveForPartAsync(int trackedMinifigPartId, int lotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlReservationDetail>> GetReservationsForLotAsync(int lotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(int? ScanEventId, int LotId)> FindLatestReservationScanEventAsync(int trackedMinifigPartId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReleaseSingleReservationAsync(int? scanEventId, int lotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ReleaseAllForMinifigsAsync(IEnumerable<int> trackedMinifigIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MassUpdateExportResult> GenerateMassUpdateXmlAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MassUpdateVerifyResult> VerifyExportAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetPendingExportCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubSettings : ISettingsService
    {
        public double ScoreThresholdMin { get; set; } = 0.5;
        private readonly AppSettings _settings = new();

        public AppSettings Current
        {
            get
            {
                _settings.ScoreThresholdMin = ScoreThresholdMin;
                return _settings;
            }
        }

        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }

    private sealed class StubImageProvider : IPartImageProvider
    {
        public Task<string> GetImageFileAsync(BrickognizeItem item, int? bricklinkColorId, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> GetImageFileByBlAsync(string bricklinkType, string bricklinkId, int? bricklinkColorId, CancellationToken ct = default) => Task.FromResult(string.Empty);
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
