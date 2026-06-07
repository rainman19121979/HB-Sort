using HBSort.Core.Models.Exceptions;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;

namespace HBSort.Tests;

/// <summary>
/// v0.1.24-beta.13: Tests fuer <see cref="MassUpdateExportViewModel"/>,
/// speziell den neuen <see cref="MassUpdateExportViewModel.InitializeAsync"/>-
/// Pfad der beim Dialog-Open zuerst einen Inventar-Sync macht und danach
/// das XML generiert. Fehlerbehandlung der Sync-Phase (Auth/RateLimit/
/// Netzwerk) muss IMMER in ein Generate-Fallback uebergehen damit der User
/// trotzdem mit Stale-Daten exportieren kann (CLAUDE.md-Konvention "BL
/// offline -> stale Cache mit Toast-Hinweis").
/// </summary>
public class MassUpdateExportViewModelTests
{
    [Fact]
    public async Task InitializeAsync_runs_sync_then_generate_on_happy_path()
    {
        var inv = new StubInventoryService
        {
            SyncResult = new BlInventorySyncResult
            {
                LotCount = 5,
                RestoredReservations = 1,
                CappedReservations = 0,
                LostReservations = 0,
                SyncedAt = new DateTime(2026, 5, 28, 14, 30, 0, DateTimeKind.Utc)
            },
            GenerateResult = new MassUpdateExportResult
            {
                Xml = "<INVENTORY/>",
                TotalLots = 3,
                DeletedLots = 1,
                ReducedLots = 2
            }
        };
        var notify = new StubNotificationService();
        var vm = new MassUpdateExportViewModel(inv, notify);

        await vm.InitializeAsync();

        // Reihenfolge: Sync wurde aufgerufen, danach Generate.
        Assert.Equal(1, inv.SyncCallCount);
        Assert.Equal(1, inv.GenerateCallCount);

        // VM-State nach Init: Loading wieder false, XML und Counts gesetzt.
        Assert.False(vm.IsLoading);
        Assert.Equal("<INVENTORY/>", vm.XmlText);
        Assert.Equal(3, vm.PendingCount);
        Assert.Contains("Inventar synchronisiert um", vm.SyncInfoText);
        Assert.Contains("3 Lot(s) betroffen", vm.InfoText);
    }

    [Fact]
    public async Task InitializeAsync_appends_cap_hint_when_reservations_were_capped()
    {
        // V6-Cap zugeschlagen: User soll im Dialog explizit sehen dass HBSort
        // Reservierungen wegen geaenderter BL-Mengen angepasst hat.
        var inv = new StubInventoryService
        {
            SyncResult = new BlInventorySyncResult
            {
                LotCount = 2,
                CappedReservations = 2,
                SyncedAt = DateTime.UtcNow
            },
            GenerateResult = new MassUpdateExportResult { Xml = "<x/>", TotalLots = 1 }
        };
        var vm = new MassUpdateExportViewModel(inv, new StubNotificationService());

        await vm.InitializeAsync();

        Assert.Contains("2 Reservierung(en)", vm.SyncInfoText);
        Assert.Contains("angepasst", vm.SyncInfoText);
    }

    [Fact]
    public async Task InitializeAsync_falls_back_to_Generate_on_BricklinkAuthException()
    {
        // Sync wirft Auth-Exception -> trotzdem Generate ausfuehren, im
        // SyncInfoText steht der Stale-Hinweis (kein Crash, keine Exception).
        var inv = new StubInventoryService
        {
            SyncException = new BricklinkAuthException("no tokens"),
            GenerateResult = new MassUpdateExportResult { Xml = "<x/>", TotalLots = 2 }
        };
        var vm = new MassUpdateExportViewModel(inv, new StubNotificationService());

        await vm.InitializeAsync();

        Assert.Equal(1, inv.SyncCallCount);
        Assert.Equal(1, inv.GenerateCallCount);
        Assert.False(vm.IsLoading);
        Assert.Contains("Sync fehlgeschlagen", vm.SyncInfoText);
        Assert.Contains("Tokens", vm.SyncInfoText);
        Assert.Equal(2, vm.PendingCount); // Stale-Generate hat geliefert
    }

    [Fact]
    public async Task InitializeAsync_falls_back_to_Generate_on_BricklinkRateLimitException()
    {
        var inv = new StubInventoryService
        {
            SyncException = new BricklinkRateLimitException("429"),
            GenerateResult = new MassUpdateExportResult { Xml = "<x/>", TotalLots = 1 }
        };
        var vm = new MassUpdateExportViewModel(inv, new StubNotificationService());

        await vm.InitializeAsync();

        Assert.Equal(1, inv.GenerateCallCount);
        Assert.Contains("Sync fehlgeschlagen", vm.SyncInfoText);
        Assert.Contains("Rate-Limit", vm.SyncInfoText);
    }

    [Fact]
    public async Task InitializeAsync_falls_back_to_Generate_on_generic_exception()
    {
        var inv = new StubInventoryService
        {
            SyncException = new HttpRequestException("connect timeout"),
            GenerateResult = new MassUpdateExportResult { Xml = "<x/>", TotalLots = 0 }
        };
        var vm = new MassUpdateExportViewModel(inv, new StubNotificationService());

        await vm.InitializeAsync();

        Assert.Equal(1, inv.GenerateCallCount);
        Assert.Contains("Sync fehlgeschlagen", vm.SyncInfoText);
        Assert.Contains("BL nicht erreichbar", vm.SyncInfoText);
    }

    [Fact]
    public async Task InitializeAsync_resets_IsLoading_even_when_Generate_throws()
    {
        var inv = new StubInventoryService
        {
            SyncResult = new BlInventorySyncResult { LotCount = 1, SyncedAt = DateTime.UtcNow },
            GenerateException = new InvalidOperationException("db down")
        };
        var vm = new MassUpdateExportViewModel(inv, new StubNotificationService());

        await vm.InitializeAsync();

        // GenerateAsync faengt intern - InfoText zeigt den Fehler, IsLoading false.
        Assert.False(vm.IsLoading);
        Assert.Contains("Fehler beim Erzeugen", vm.InfoText);
    }

    // ====================================================================
    // Stubs
    // ====================================================================

    private sealed class StubInventoryService : IBlInventoryService
    {
        public BlInventorySyncResult? SyncResult { get; set; }
        public Exception? SyncException { get; set; }
        public MassUpdateExportResult? GenerateResult { get; set; }
        public Exception? GenerateException { get; set; }

        public int SyncCallCount { get; private set; }
        public int GenerateCallCount { get; private set; }

        // Im Test nie gefeuert - das ViewModel abonniert das Event nicht.
#pragma warning disable CS0067
        public event EventHandler? InventoryChanged;
#pragma warning restore CS0067

        public Task<BlInventorySyncResult> SyncInventoryAsync(CancellationToken ct = default)
        {
            SyncCallCount++;
            if (SyncException != null) return Task.FromException<BlInventorySyncResult>(SyncException);
            return Task.FromResult(SyncResult ?? new BlInventorySyncResult { SyncedAt = DateTime.UtcNow });
        }

        public Task<MassUpdateExportResult> GenerateMassUpdateXmlAsync(CancellationToken ct = default)
        {
            GenerateCallCount++;
            if (GenerateException != null) return Task.FromException<MassUpdateExportResult>(GenerateException);
            return Task.FromResult(GenerateResult ?? new MassUpdateExportResult());
        }

        // Restliche Interface-Methoden werden in den InitializeAsync-Tests
        // nicht benutzt - bewusst NotImplementedException damit ein
        // ungewollter Aufruf sofort kracht.
        public Task<List<HBSort.Core.Models.BlInventoryLot>> GetInventoryAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> HasAnyInventoryAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HBSort.Core.Models.BlInventoryLot>> FindLotsForPartAsync(string blPartNo, int? colorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<(string PartNo, int ColorId)>> GetAvailablePartTuplesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<(string PartNo, int ColorId), int>> GetAvailableQuantitiesAsync(IEnumerable<(string PartNo, int ColorId)> parts, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReserveAsync(int lotId, int qty = 1, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReleaseAsync(int lotId, int qty = 1, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReserveForPartAsync(int trackedMinifigPartId, int lotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlReservationDetail>> GetReservationsForLotAsync(int lotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(int? ScanEventId, int LotId)> FindLatestReservationScanEventAsync(int trackedMinifigPartId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReleaseSingleReservationAsync(int? scanEventId, int lotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ReleaseAllForMinifigsAsync(IEnumerable<int> trackedMinifigIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MassUpdateVerifyResult> VerifyExportAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetPendingExportCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubNotificationService : INotificationService
    {
        public void ShowInfo(string message) { }
        public void ShowSuccess(string message, string? imageUrl = null) { }
        public void ShowWarning(string message) { }
        public void ShowError(string message) { }
    }
}
