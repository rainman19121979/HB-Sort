using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// Audit K-2: Tests fuer das IDisposable-Pattern an den Singleton-VMs,
/// die IMinifigPersistenceService.DataChanged abonnieren. Ohne Unsubscribe
/// haengen die Handler unbeschraenkt am Service - in Production-Singleton-
/// Lifetime kein Problem (lebt bis App-Ende), aber:
/// 1) Tests die VM-Setup wiederholen sammeln Handler an,
/// 2) Future-Refactor (z.B. Tab-Lazy-Init) wird damit zur echten Leak-Quelle.
///
/// Stub-Strategie: ein eigener IMinifigPersistenceService-Stub mit
/// add/remove-Akzessoren am Event, der einen Subscriber-Counter fuehrt.
/// So koennen wir vor und nach Dispose() den Counter pruefen.
///
/// BuildSuggestionsViewModel ist hier NICHT getestet, weil sein
/// Konstruktor zusaetzlich ein IBlCacheRepository braucht und das
/// Interface ~30 Methoden hat - der Stub-Aufwand waere disproportional.
/// Das Pattern wird durch die drei anderen VMs ausreichend belegt;
/// BuildSuggestionsViewModel folgt exakt demselben Code-Pattern.
/// </summary>
public class SingletonViewModelDisposeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SimpleContextFactory _ctxFactory;

    public SingletonViewModelDisposeTests()
    {
        // In-Memory-SQLite + EF: jede VM ruft im Konstruktor RefreshAsync auf,
        // braucht also eine funktionierende DbContextFactory. Das Schema wird
        // ueber EnsureCreatedAsync angelegt.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<UserDataContext>()
            .UseSqlite(_connection)
            .Options;
        using (var ctx = new UserDataContext(options))
        {
            ctx.Database.EnsureCreated();
        }
        _ctxFactory = new SimpleContextFactory(options);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public void LiveStatsViewModel_subscribes_in_ctor_and_unsubscribes_on_dispose()
    {
        var stub = new CountingPersistenceStub();
        Assert.Equal(0, stub.SubscriberCount);

        var vm = new LiveStatsViewModel(_ctxFactory, stub);
        Assert.Equal(1, stub.SubscriberCount);

        vm.Dispose();
        Assert.Equal(0, stub.SubscriberCount);
    }

    [Fact]
    public void WaitingDetailViewModel_subscribes_in_ctor_and_unsubscribes_on_dispose()
    {
        var stub = new CountingPersistenceStub();
        var vm = new WaitingDetailViewModel(_ctxFactory, stub);
        Assert.Equal(1, stub.SubscriberCount);

        vm.Dispose();
        Assert.Equal(0, stub.SubscriberCount);
    }

    [Fact]
    public void RecentScansViewModel_subscribes_in_ctor_and_unsubscribes_on_dispose()
    {
        var stub = new CountingPersistenceStub();
        var vm = new RecentScansViewModel(_ctxFactory, stub);
        Assert.Equal(1, stub.SubscriberCount);

        vm.Dispose();
        Assert.Equal(0, stub.SubscriberCount);
    }

    [Fact]
    public void Dispose_called_twice_is_safe()
    {
        // Doppeltes Dispose darf nicht crashen. Der zweite -=  ist ein no-op
        // weil der Handler nicht mehr registriert ist. Counter darf nicht
        // unerwartet weiter sinken.
        var stub = new CountingPersistenceStub();
        var vm = new RecentScansViewModel(_ctxFactory, stub);

        vm.Dispose();
        var afterFirstDispose = stub.SubscriberCount;
        vm.Dispose();

        // Nach dem zweiten Dispose: kein Crash, Counter ist <= 0 (im Stub
        // zaehlen wir bei jedem -= runter, also gehen wir auf -1; das ist
        // nicht idiomatisch, aber dokumentiert das Verhalten).
        Assert.True(stub.SubscriberCount <= afterFirstDispose);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Stub mit Subscriber-Counter am DataChanged-Event. Wir ueberschreiben
    /// nur die add/remove-Akzessoren; alle anderen Methoden werfen
    /// NotImplementedException - werden in den Tests nicht aufgerufen
    /// (der initiale RefreshAsync-Aufruf laeuft nur gegen die DbContextFactory).
    /// </summary>
    private sealed class CountingPersistenceStub : IMinifigPersistenceService
    {
        private EventHandler? _handler;
        public int SubscriberCount { get; private set; }

        public event EventHandler? DataChanged
        {
            add { _handler += value; SubscriberCount++; }
            remove { _handler -= value; SubscriberCount--; }
        }

        public void RaiseDataChanged() => _handler?.Invoke(this, EventArgs.Empty);

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
