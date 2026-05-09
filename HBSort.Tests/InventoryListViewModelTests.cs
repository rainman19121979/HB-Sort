using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using HBSort.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// Regressions-Tests fuer den Export-Button-Aktivierungs-Bug:
/// "Wenn nur Einzelteile (FloatingParts) per Checkbox ausgewaehlt sind,
/// ist der Exportieren-Button ausgegraut".
///
/// Der aktuelle Code (UX X.6 + X.11) hat das Property-Setup
/// HasSelectedExportables = SelectedCompletes + SelectedFloatings &gt; 0
/// schon kombiniert. Diese Tests sichern den Vertrag damit kuenftige
/// Refactors nicht versehentlich auf Komplett-only zurueckfallen.
/// </summary>
public class InventoryListViewModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly StubPersistence _persistence = new();
    private readonly InventoryListViewModel _sut;

    public InventoryListViewModelTests()
    {
        // SQLite-In-Memory damit die ItemsView-CollectionView ein "richtiges"
        // Items-Backing hat. Wir rufen LoadAsync nie auf - die VM-Properties
        // werden ausschliesslich ueber direktes Items-Add + RecalculateSelection
        // getestet.
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

        _sut = new InventoryListViewModel(
            _factory,
            new NotImplementedCatalog(),
            new NotImplementedImageProvider(),
            _persistence);
    }

    public void Dispose() => _connection.Dispose();

    // ===== Bug-Szenarien =====

    [Fact]
    public void OnlyFloatingSelected_HasSelectedExportables_isTrue()
    {
        var floating = MakeFloating(id: 1);
        _sut.Items.Add(floating);

        floating.IsSelected = true;
        _sut.RecalculateSelection();

        Assert.True(_sut.HasSelectedExportables,
            "Eine markierte Einzelteil-Zeile MUSS den Exportieren-Button aktivieren.");
        Assert.Equal(1, _sut.SelectedExportableCount);
    }

    // ===== UX X.13b: realistische Property-getriebene Selektion =====
    // Der echte Pfad bei einem Checkbox-Klick im DataGrid ist:
    //   IsChecked-Toggle -> TwoWay-Binding setzt InventoryRowItem.IsSelected
    //   -> Property-Setter feuert SelectionChanged -> VM ruft RecalculateSelection
    // Diese Tests setzen IsSelected DIREKT (ohne manuelles RecalculateSelection)
    // und pruefen ob der Counter trotzdem stimmt. Das fangen die alten Tests
    // nicht ab, weil sie immer manuell RecalculateSelection() aufgerufen haben.

    [Fact]
    public void Setting_IsSelected_directly_updates_SelectedExportableCount()
    {
        var floating = MakeFloating(id: 1);
        _sut.Items.Add(floating);

        // KEIN manuelles RecalculateSelection - genau wie bei einem User-Klick.
        floating.IsSelected = true;

        Assert.Equal(1, _sut.SelectedExportableCount);
        Assert.True(_sut.HasSelectedExportables);
    }

    [Fact]
    public void Setting_IsSelected_on_multiple_rows_increments_count()
    {
        var f1 = MakeFloating(id: 1);
        var f2 = MakeFloating(id: 2);
        var c1 = MakeComplete(id: 3);
        _sut.Items.Add(f1);
        _sut.Items.Add(f2);
        _sut.Items.Add(c1);

        f1.IsSelected = true;
        f2.IsSelected = true;
        c1.IsSelected = true;

        Assert.Equal(3, _sut.SelectedExportableCount);
    }

    [Fact]
    public void Unsetting_IsSelected_decrements_count()
    {
        var f1 = MakeFloating(id: 1);
        var f2 = MakeFloating(id: 2);
        _sut.Items.Add(f1);
        _sut.Items.Add(f2);
        f1.IsSelected = true;
        f2.IsSelected = true;
        Assert.Equal(2, _sut.SelectedExportableCount);

        f1.IsSelected = false;

        Assert.Equal(1, _sut.SelectedExportableCount);
    }

    [Fact]
    public void Setting_IsSelected_fires_PropertyChanged_for_SelectedExportableCount()
    {
        var floating = MakeFloating(id: 1);
        _sut.Items.Add(floating);

        var raised = new List<string?>();
        _sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        floating.IsSelected = true;

        Assert.Contains(nameof(_sut.SelectedExportableCount), raised);
        Assert.Contains(nameof(_sut.HasSelectedExportables), raised);
    }

    [Fact]
    public void Removing_a_row_unsubscribes_so_late_changes_are_ignored()
    {
        // Wenn eine Zeile aus Items entfernt wird (z.B. nach Reload), darf ein
        // spaeteres IsSelected-Toggle den Counter nicht mehr beeinflussen -
        // sonst kriegen wir Geister-Selektionen aus alten Items.
        var floating = MakeFloating(id: 1);
        _sut.Items.Add(floating);
        _sut.Items.Remove(floating);

        floating.IsSelected = true;

        Assert.Equal(0, _sut.SelectedExportableCount);
    }

    [Fact]
    public void OnlyCompleteSelected_HasSelectedExportables_isTrue()
    {
        var complete = MakeComplete(id: 1);
        _sut.Items.Add(complete);

        complete.IsSelected = true;
        _sut.RecalculateSelection();

        Assert.True(_sut.HasSelectedExportables);
        Assert.Equal(1, _sut.SelectedExportableCount);
    }

    [Fact]
    public void MixedSelection_HasSelectedExportables_isTrue()
    {
        var complete = MakeComplete(id: 1);
        var floating = MakeFloating(id: 2);
        _sut.Items.Add(complete);
        _sut.Items.Add(floating);

        complete.IsSelected = true;
        floating.IsSelected = true;
        _sut.RecalculateSelection();

        Assert.True(_sut.HasSelectedExportables);
        Assert.Equal(2, _sut.SelectedExportableCount);
    }

    [Fact]
    public void NothingSelected_HasSelectedExportables_isFalse()
    {
        _sut.Items.Add(MakeComplete(id: 1));
        _sut.Items.Add(MakeFloating(id: 2));

        _sut.RecalculateSelection();

        Assert.False(_sut.HasSelectedExportables);
        Assert.Equal(0, _sut.SelectedExportableCount);
    }

    [Fact]
    public void OnlyWaitingRows_HasAnyExportable_isFalse()
    {
        // Wartende Figuren haben keine Checkbox - die Action-Bar mit dem
        // Exportieren-Button wird ueber HasAnyExportable ausgeblendet.
        _sut.Items.Add(MakeWaiting(id: 1));
        _sut.Items.Add(MakeWaiting(id: 2));

        Assert.False(_sut.HasAnyExportable);

        // Auch wenn der User per Code-Trick IsSelected setzt: SelectedExportables
        // filtert diese raus, weil Status != Complete/Floating.
        _sut.Items[0].IsSelected = true;
        _sut.RecalculateSelection();
        Assert.False(_sut.HasSelectedExportables);
    }

    // ===== Aktions-Commands =====

    [Fact]
    public void SelectAllExportable_marks_completes_AND_floatings_skips_waiting()
    {
        _sut.Items.Add(MakeComplete(id: 1));
        _sut.Items.Add(MakeWaiting(id: 2));     // soll uebersprungen werden
        _sut.Items.Add(MakeFloating(id: 3));

        _sut.SelectAllExportableCommand.Execute(null);

        Assert.True(_sut.Items[0].IsSelected);
        Assert.False(_sut.Items[1].IsSelected); // Wartende NICHT markiert
        Assert.True(_sut.Items[2].IsSelected);
        Assert.Equal(2, _sut.SelectedExportableCount);
        Assert.True(_sut.HasSelectedExportables);
    }

    [Fact]
    public void DeselectAllExportable_clears_both_completes_and_floatings()
    {
        var complete = MakeComplete(id: 1);
        var floating = MakeFloating(id: 2);
        _sut.Items.Add(complete);
        _sut.Items.Add(floating);
        complete.IsSelected = true;
        floating.IsSelected = true;
        _sut.RecalculateSelection();
        Assert.Equal(2, _sut.SelectedExportableCount);

        _sut.DeselectAllExportableCommand.Execute(null);

        Assert.False(complete.IsSelected);
        Assert.False(floating.IsSelected);
        Assert.Equal(0, _sut.SelectedExportableCount);
        Assert.False(_sut.HasSelectedExportables);
    }

    [Fact]
    public void SelectedFloatings_yields_only_floating_rows_with_underlying_id()
    {
        _sut.Items.Add(MakeComplete(id: 10));
        var floating = MakeFloating(id: 20);
        _sut.Items.Add(floating);
        floating.IsSelected = true;

        var floatings = _sut.SelectedFloatings.ToList();

        Assert.Single(floatings);
        Assert.Equal(20, floatings[0].UnderlyingFloatingId);
    }

    // ===== Helpers =====

    private static InventoryRowItem MakeComplete(int id) => new()
    {
        Status = StatusKind.Complete,
        Type = InventoryItemType.Minifig,
        UnderlyingMinifigId = id,
        ItemId = $"figure-{id}",
        Description = "Test Minifig"
    };

    private static InventoryRowItem MakeFloating(int id) => new()
    {
        Status = StatusKind.Floating,
        Type = InventoryItemType.FloatingPart,
        UnderlyingFloatingId = id,
        ItemId = $"part-{id}",
        Description = "Test Part"
    };

    private static InventoryRowItem MakeWaiting(int id) => new()
    {
        Status = StatusKind.Waiting,
        Type = InventoryItemType.Minifig,
        UnderlyingMinifigId = id,
        ItemId = $"waiting-{id}",
        Description = "Wartende Figur"
    };

    /// <summary>
    /// Stub fuer IMinifigPersistenceService - braucht das DataChanged-Event
    /// damit der VM-Konstruktor abonnieren kann. Alle anderen Methoden werfen
    /// (im Test nicht aufgerufen).
    /// </summary>
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

    /// <summary>
    /// Stubs fuer IBlCatalogService und IPartImageProvider - werden von den
    /// Tests nicht aufgerufen, aber der ctor braucht non-null Instanzen.
    /// </summary>
    private sealed class NotImplementedCatalog : IBlCatalogService
    {
        public Task<BlItem?> GetMinifigDetailsAsync(string blMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlSubset>> GetMinifigPartsAsync(string blMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BlItem?> GetPartDetailsAsync(string blPartNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> FindWaitingMinifigsForPartAsync(string blPartNo, int blColorId, IEnumerable<string> waitingMinifigIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BlCacheStats> GetCacheStatsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearCacheAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearStaleAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlSubset>> GetSupersetsAsync(string blPartNo, int blColorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> EnsureFullSubsetsAsync(string blMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(string blPartNo, int blColorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlColor>> GetKnownColorsAsync(string blPartNo, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class NotImplementedImageProvider : IPartImageProvider
    {
        public Task<string> GetImageFileAsync(BrickognizeItem item, int? bricklinkColorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetImageFileByBlAsync(string bricklinkType, string bricklinkId, int? bricklinkColorId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }
}
