using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// Audit W-6: Tests fuer PartLookupService.
///
/// Schwerpunkte:
/// - AddPartToFloatingAsync: Stack-Verhalten (gleiches Teil + Farbe + Bin
///   wird summiert, statt neuen Eintrag anzulegen).
/// - AssignPartToMinifigAsync: QuantityCollected steigt, Auto-Complete bei
///   letztem Part.
/// - UnassignPartFromMinifigAsync: QuantityCollected zurueck auf 0, Status-
///   Reset bei Complete -> Waiting.
/// - FindFloatingLocationsAsync: Sortierung nach Quantity desc.
/// - DeleteFloatingPartAsync: Eintrag weg, true zurueck.
///
/// LookupPartAsync und CollectMinifigFromSupersetAsync sind nicht abgedeckt
/// weil sie IBlCatalogService brauchen - das Stub-Setup waere disproportional.
/// Beide laufen nicht durch geaenderte Code-Pfade dieser Iteration.
/// </summary>
public class PartLookupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly PartLookupService _sut;
    private readonly StubPersistence _persistence = new();

    public PartLookupServiceTests()
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
        _sut = new PartLookupService(_factory, new NotImplementedCatalog(), _persistence, new NotImplementedImageProvider());
    }

    public void Dispose() => _connection.Dispose();

    // ====================================================================
    // AddPartToFloatingAsync
    // ====================================================================

    [Fact]
    public async Task AddPartToFloating_creates_new_entry_when_no_match()
    {
        var binId = await SeedBinAsync("Box 01");

        var fp = await _sut.AddPartToFloatingAsync("3001", 11, "Brick 2x4", "Black",
            quantity: 3, storageBinId: binId);

        Assert.NotNull(fp);
        Assert.Equal(3, fp.Quantity);

        await using var ctx = await _factory.CreateDbContextAsync();
        var entries = await ctx.FloatingParts.ToListAsync();
        Assert.Single(entries);
        Assert.Equal("3001", entries[0].PartNumber);
        Assert.Equal(11, entries[0].ColorId);
        Assert.Equal(3, entries[0].Quantity);
    }

    [Fact]
    public async Task AddPartToFloating_stacks_on_existing_match_in_same_bin()
    {
        // Smart-Storage-Verhalten: gleiches (PartNo, Color, Bin) -> Quantity
        // wird auf den bestehenden Eintrag addiert.
        var binId = await SeedBinAsync("Box 02");
        await SeedFloatingAsync(binId, "3001", 11, qty: 5);

        var fp = await _sut.AddPartToFloatingAsync("3001", 11, "Brick 2x4", "Black",
            quantity: 2, storageBinId: binId);

        Assert.Equal(7, fp.Quantity); // 5 + 2

        await using var ctx = await _factory.CreateDbContextAsync();
        var entries = await ctx.FloatingParts.ToListAsync();
        Assert.Single(entries); // kein neuer Eintrag, Stack
    }

    [Fact]
    public async Task AddPartToFloating_creates_separate_entry_in_different_bin()
    {
        // Anderes Fach -> trotz gleichem (PartNo, Color) ein neuer Eintrag.
        var binA = await SeedBinAsync("Box A");
        var binB = await SeedBinAsync("Box B");
        await SeedFloatingAsync(binA, "3001", 11, qty: 5);

        await _sut.AddPartToFloatingAsync("3001", 11, "Brick 2x4", "Black", 2, binB);

        await using var ctx = await _factory.CreateDbContextAsync();
        var entries = await ctx.FloatingParts.OrderBy(f => f.StorageBinId).ToListAsync();
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task AddPartToFloating_throws_on_invalid_quantity()
    {
        var binId = await SeedBinAsync("Box 01");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.AddPartToFloatingAsync("3001", 11, "", "", 0, binId));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.AddPartToFloatingAsync("3001", 11, "", "", -1, binId));
    }

    [Fact]
    public async Task AddPartToFloating_throws_on_missing_bin()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.AddPartToFloatingAsync("3001", 11, "", "", 1, storageBinId: 9999));
    }

    [Fact]
    public async Task AddPartToFloating_raises_DataChanged()
    {
        var binId = await SeedBinAsync("Box 01");
        var beforeRaises = _persistence.RaiseCount;

        await _sut.AddPartToFloatingAsync("3001", 11, "", "", 1, binId);

        Assert.True(_persistence.RaiseCount > beforeRaises);
    }

    // ====================================================================
    // AssignPartToMinifigAsync
    // ====================================================================

    [Fact]
    public async Task AssignPartToMinifig_increments_quantity_collected()
    {
        var binId = await SeedBinAsync("Box 01");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 3, qtyCollected: 1);

        var result = await _sut.AssignPartToMinifigAsync(partId);

        Assert.True(result.Assigned);
        Assert.False(result.MinifigCompleted);
        Assert.Null(result.Released);
        await using var ctx = await _factory.CreateDbContextAsync();
        var part = await ctx.TrackedMinifigParts.SingleAsync(p => p.Id == partId);
        Assert.Equal(2, part.QuantityCollected); // 1 -> 2
    }

    [Fact]
    public async Task AssignPartToMinifig_caps_at_quantity_needed_and_marks_complete()
    {
        // Letztes fehlendes Teil -> Figur wird Complete.
        var binId = await SeedBinAsync("Box 01");
        var (minifigId, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 1, qtyCollected: 0);

        var result = await _sut.AssignPartToMinifigAsync(partId);

        Assert.True(result.Assigned);
        Assert.True(result.MinifigCompleted);
        await using var ctx = await _factory.CreateDbContextAsync();
        var minifig = await ctx.TrackedMinifigs.SingleAsync(m => m.Id == minifigId);
        Assert.Equal(TrackedMinifigStatus.Complete, minifig.Status);
        Assert.NotNull(minifig.CompletedAt);
    }

    [Fact]
    public async Task AssignPartToMinifig_returns_false_when_already_full()
    {
        // QuantityCollected == QuantityNeeded -> nichts zu tun.
        var binId = await SeedBinAsync("Box 01");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 1, qtyCollected: 1);

        var result = await _sut.AssignPartToMinifigAsync(partId);

        Assert.False(result.Assigned);
        Assert.False(result.MinifigCompleted);
    }

    // ====================================================================
    // v0.1.24-beta.13 (V5) — AssignPart mit BL-Reservierung
    // ====================================================================

    [Fact]
    public async Task AssignPart_V5_no_reservation_simple_increment()
    {
        // Szenario 1: Reserved=0 → normaler Konsum, kein Release.
        var (sut, _) = await CreateV5SutAsync();
        var binId = await SeedBinAsync("Box V5-1");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 3, qtyCollected: 0);

        var result = await sut.AssignPartToMinifigAsync(partId);

        Assert.True(result.Assigned);
        Assert.False(result.MinifigCompleted);
        Assert.Null(result.Released);

        await using var ctx = await _factory.CreateDbContextAsync();
        var p = await ctx.TrackedMinifigParts.SingleAsync(x => x.Id == partId);
        Assert.Equal(1, p.QuantityCollected);
        Assert.Equal(0, p.QuantityReservedFromBl);
    }

    [Fact]
    public async Task AssignPart_V5_reservation_below_need_releases_one()
    {
        // Szenario 2: Reserved>0, Effective<Need.
        // Need=3, Reserved=2, Collected=0, Effective=2 < 3.
        //
        // Scan 1: Release-Check (0+1)+2 = 3 > 3? Nein → kein Release.
        //         Coll=1, Res=2, Eff=3=Need.
        // Scan 2: Release-Check (1+1)+2 = 4 > 3? Ja UND Res>0 → Release.
        //         Res=1, dann Coll=2. Eff=2+1=3=Need.
        var (sut, _) = await CreateV5SutAsync();
        var binId = await SeedBinAsync("Box V5-2");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 3, qtyCollected: 0);
        await SeedBlReservationAsync(partId, lotId: 100, lotQuantity: 5, reservedCount: 2);

        var r1 = await sut.AssignPartToMinifigAsync(partId);
        Assert.True(r1.Assigned);
        Assert.Null(r1.Released);

        var r2 = await sut.AssignPartToMinifigAsync(partId);
        Assert.True(r2.Assigned);
        Assert.NotNull(r2.Released);
        Assert.Equal(100, r2.Released!.LotId);

        await using var ctx = await _factory.CreateDbContextAsync();
        var p = await ctx.TrackedMinifigParts.SingleAsync(x => x.Id == partId);
        Assert.Equal(2, p.QuantityCollected);
        Assert.Equal(1, p.QuantityReservedFromBl);
        // EffectiveCollected = 2 + 1 = 3 = Need → invariant geblieben

        var lot = await ctx.BlInventoryLots.SingleAsync(l => l.LotId == 100);
        Assert.Equal(1, lot.ReservedQuantity);
    }

    [Fact]
    public async Task AssignPart_V5_effective_meets_need_via_reservation_then_scan_releases()
    {
        // Szenario 3: Reserved>0 deckt komplett ab (Effective=Need),
        // User scannt trotzdem real → physisches gewinnt, Reservierung
        // wird ersetzt. EffectiveCollected bleibt invariant.
        //
        // Need=2, Reserved=2, Collected=0, Effective=2=Need.
        // Cap (physical Coll < Need) → läuft. Release-Check
        // (0+1)+2 = 3 > 2 → Release. Res=1, Coll=1. Eff=1+1=2=Need.
        var (sut, _) = await CreateV5SutAsync();
        var binId = await SeedBinAsync("Box V5-3");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 2, qtyCollected: 0);
        await SeedBlReservationAsync(partId, lotId: 200, lotQuantity: 5, reservedCount: 2);

        var result = await sut.AssignPartToMinifigAsync(partId);

        Assert.True(result.Assigned);
        Assert.NotNull(result.Released);
        Assert.Equal(200, result.Released!.LotId);
        // U-Lot (Default im Seed) — Caller wuerde Fall A (ShopReturn) zeigen.
        Assert.Equal("N", result.Released.LotCondition); // SeedBlReservationAsync setzt "N"

        await using var ctx = await _factory.CreateDbContextAsync();
        var p = await ctx.TrackedMinifigParts.SingleAsync(x => x.Id == partId);
        Assert.Equal(1, p.QuantityCollected);
        Assert.Equal(1, p.QuantityReservedFromBl);
        var lot = await ctx.BlInventoryLots.SingleAsync(l => l.LotId == 200);
        Assert.Equal(1, lot.ReservedQuantity);
    }

    [Fact]
    public async Task AssignPart_V5_cap_when_physical_already_full()
    {
        // Szenario 3b: Drift-Schutz. Coll bereits == Need (Daten-Drift
        // oder Doppelscan-Race). Egal ob Reserved>0, Cap greift.
        var (sut, _) = await CreateV5SutAsync();
        var binId = await SeedBinAsync("Box V5-3b");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 2, qtyCollected: 2);
        await SeedBlReservationAsync(partId, lotId: 250, lotQuantity: 5, reservedCount: 1);

        var result = await sut.AssignPartToMinifigAsync(partId);

        Assert.False(result.Assigned);
        Assert.Null(result.Released);

        await using var ctx = await _factory.CreateDbContextAsync();
        var p = await ctx.TrackedMinifigParts.SingleAsync(x => x.Id == partId);
        Assert.Equal(2, p.QuantityCollected);
        Assert.Equal(1, p.QuantityReservedFromBl);
        var lot = await ctx.BlInventoryLots.SingleAsync(l => l.LotId == 250);
        Assert.Equal(1, lot.ReservedQuantity);
    }

    // ---- Alte Vor-V5-Test-Skizze entfernt (Cap-Logik unterschied sich,
    // Mehrfach-Release-Pfad wird jetzt von
    // AssignPart_V5_full_reserved_coverage_releases_per_scan abgedeckt). ----
    /*
        // Szenario 4: Reserved=3, Collected=0, Need=3. Effective=3 vorab,
        // also Cap-Pfad. Variante mit Need=4, Reserved=3, Collected=0,
        // Effective=3<4. Scan 1: Collected=1, Effective=1+3=4=Need OK, kein
        // Release (1+3 == Need). Aber wir wollen das "Reservierung uebersteigt
        // Restbedarf" Szenario zeigen.
        //
        // Klarer Test: Need=3, Reserved=3, Collected=0, Effective=3=Need.
        // → Cap greift sofort, KEIN Release. Variante mit Effective<Need:
        // Need=4, Reserved=3, Collected=0, Effective=3<4. Erster Scan:
        // Collected=1, Effective=4=Need OK, kein Release noetig. Reserved
        // bleibt 3. Status Complete. Zweite Scans wuerden Effective>Need
        // wollen aber Cap greift → kein Release.
        //
        // → V5 hat KEIN "mehrfach Release" wenn die Figur normal komplettiert.
        // Mehrfach-Release entsteht nur wenn der User real mehr scannt als
        // Need erlaubt. Das ist durch den Cap geblockt.
        //
        // ECHTER Mehrfach-Release-Pfad: User reduziert manuell die Need
        // (existiert nicht) ODER hat manuell Collected reduziert. Wir simulieren
        // ueber Need=2, Reserved=2, Collected=0, dann 2 Scans:
        //   Scan 1: Effective=2=Need → Cap, kein Release
        //
        // Realer Test: Need=3, Reserved=2, Collected=1, Effective=3=Need →
        // Cap. Also: alle Konstellationen wo Effective>=Need fuehren zum Cap.
        // V5-Release nur wenn Effective<Need vor dem Scan und (Coll+1+Res)>Need.
        //
        // Das letzte ist nur moeglich wenn Reserved>0 und Effective<Need und
        // (Coll+1+Res)>Need bedeutet Coll < Need-Res < Coll+1, also
        // Need-Res zwischen Coll und Coll+1 — geht nur wenn Need-Res
        // ist KEIN Integer, was bei int unmoeglich ist. ↯
        //
        // Eigentlich: (Coll+1)+Res > Need ⇔ Coll > Need-Res-1 ⇔ Coll >= Need-Res
        // Coll < Need-Res würde Effective<Need bedeuten = OK.
        // Aber Effective>=Need ist genau Coll >= Need-Res ⇒ Cap-Pfad.
        // Also: V5-Release wird NIE getriggert?
        //
        // Wenn Reserved direkt aus Need entsteht (User reserviert Need-X,
        // hat schon X physisch), gibt es kein Szenario. Das echte Szenario
        // ist Reserved noch nicht in der Effective-Formel: NEIN, ist es schon.
        //
        // FAZIT: V5-Release wird NUR getriggert wenn Coll == Need-Res EXAKT,
        // weil dann (Coll+1)+Res = Need+1 > Need. Das ist Szenario 2 oben.
        // "Mehrfache Releases" sind moeglich wenn nach Release Need-Res' wieder
        // erreicht wird beim naechsten Scan. Beispiel: Need=4, Reserved=2,
        // Collected=0, Effective=2<4.
        //   Scan 1: 1+2=3<4 → kein Release, Coll=1
        //   Scan 2: 2+2=4=Need → kein Release (4 == 4 nicht > 4), Coll=2
        //   Status Complete jetzt. Reserved bleibt 2.
        // → Auch hier kein Release. Reserved bleibt im BL-Shop.
        //
        // Realer Trigger fuer V5: User reserviert, scannt, will dann scannen
        // obwohl schon Effective-komplett. Cap greift, kein Release noetig.
        //
        // Damit ist der einzige V5-Release-Pfad: physischer Scan trifft die
        // Schwelle (Coll+1)+Res > Need, was Coll >= Need-Res bedeutet, was
        // Effective vorher = Coll+Res = Need-Res+Res = Need bedeutet → Cap.
        //
        // ⇒ Mein V5-Code hat eine Bedingung die NIE feuert!
        // Korrektur: Bedingung muss heißen "(Coll+1) > Need - Res" oder
        // Reserved>0 UND Coll+Res < Need UND (Coll+1)+Res > Need.
        // Letzteres: Coll < Need-Res < Coll+1. Bei integers gibt es kein
        // strict-greater dazwischen — daher tatsaechlich nur =0-Volumen.
        //
        // Aber: bei Coll+Res < Need (z.B. Need=3, Coll=0, Res=2: Eff=2<3)
        // und naechster Scan: Coll=1, neu Eff=1+2=3=Need. KEIN Trigger.
        // Wir brauchen NOCH ein Szenario: Coll=2 jetzt: Eff=2+2=4 > 3.
        // Aber Coll geht nur dann auf 2 wenn vorheriges Assign mit Effective<Need
        // war (war's, 1+2=3=Need... nein wait, das war schon AT Need, naechster
        // Scan wuerde Effective>Need → Cap, kein Increment).
        //
        // Hmm, dann ist V5-Release tatsächlich seltsam.
        //
        // → V5 dokumentiert das gewuenschte Verhalten "physisches ersetzt
        // Reservierung", aber das einzige bug-fähige Szenario im Audit ist
        // wenn der User Reservierung+Scan mismatched. Tatsächlich wird V5-
        // Code defensiv und nur in inkonsistenten Daten-Situationen feuern.
        //
        // Realistisch: User reserviert 2 von Need=3, scannt 1 ein
        // (Collected=1, Reserved=2, Effective=3=Need=Complete), dann
        // klickt im Summary-Dialog Checkbox AB → Reserved bleibt 2, Collected=0.
        // Dann scannt User wieder, Effective=2<3. Scan: Collected=1, Eff=3=Need
        // — kein Release. Zweiter Scan: Cap.
        //
        // Konstruieren wir einen DIRTY-State-Test:
        // Need=2, Reserved=1, Collected=1 (Effective=2=Need=Complete).
        // Manuell setzen wir die Figur zurueck auf Waiting + Collected=0
        // ohne Reserved zu loeschen (manueller DB-Eingriff oder Race).
        // Need=2, Reserved=1, Collected=0, Effective=1<2.
        // Scan 1: Coll+1+Res = 0+1+1 = 2 == Need → kein Release. Coll=1, Eff=2.
        // Scan 2: Cap.
        //
        // Setzen wir Reserved=2 statt 1: Need=2, Res=2, Coll=0, Eff=2=Need → Cap.
        //
        // Setzen wir Need=2, Res=3 (over-reserved, inkonsistente Daten):
        // Eff=3>Need → Cap.
        //
        // Setzen wir Need=2, Res=2, Coll=1 (data drift, sollte nicht möglich sein):
        // Eff=3>Need → Cap.
        //
        // Reale Bug-Auslöser: NUR über Daten-Drift / Race-Condition zu erreichen.
        // Aber: ApplyBlReservationAsync setzt seinerseits den Cap nicht streng.
        // Lass uns einen "edge"-Test machen ohne Mehrfach-Release.
        //
        // Pragmatisch: Szenario 4 = Inkonsistenz-Schutz.
        // Need=2, Reserved=2, Collected=2 (Drift) → Cap, kein Release.
        var (sut, _) = await CreateV5SutAsync();
        var binId = await SeedBinAsync("Box V5-4");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 2, qtyCollected: 2);
        await SeedBlReservationAsync(partId, lotId: 400, lotQuantity: 5, reservedCount: 2);

        var result = await sut.AssignPartToMinifigAsync(partId);

        // Effective = 2+2 = 4 > Need = 2 → Cap, kein Increment, kein Release.
        Assert.False(result.Assigned);
        Assert.Equal(0, result.BlReservationsReleased);

        await using var ctx = await _factory.CreateDbContextAsync();
        var p = await ctx.TrackedMinifigParts.SingleAsync(x => x.Id == partId);
        Assert.Equal(2, p.QuantityCollected);
        Assert.Equal(2, p.QuantityReservedFromBl);
        var lot = await ctx.BlInventoryLots.SingleAsync(l => l.LotId == 400);
        Assert.Equal(2, lot.ReservedQuantity);
    */

    [Fact]
    public async Task AssignPart_V5_full_reserved_coverage_releases_per_scan()
    {
        // Szenario 4: Need=3, Reserved=3, Collected=0, Effective=3=Need.
        // Komplette Deckung durch Reservierungen. User scannt alle 3 Teile
        // real - jeder Scan verdraengt eine Reservierung.
        //
        // Scan 1: Coll<Need. Release-Check (0+1)+3=4>3 + Res>0 → Release.
        //         Res=2, Coll=1. Eff=1+2=3=Need.
        // Scan 2: Release-Check (1+1)+2=4>3 → Release. Res=1, Coll=2.
        // Scan 3: Release-Check (2+1)+1=4>3 → Release. Res=0, Coll=3.
        // Scan 4: Coll=3>=Need → Cap.
        var (sut, _) = await CreateV5SutAsync();
        var binId = await SeedBinAsync("Box V5-4b");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 3, qtyCollected: 0);
        await SeedBlReservationAsync(partId, lotId: 410, lotQuantity: 10, reservedCount: 3);

        var r1 = await sut.AssignPartToMinifigAsync(partId);
        Assert.True(r1.Assigned);
        Assert.NotNull(r1.Released);

        var r2 = await sut.AssignPartToMinifigAsync(partId);
        Assert.True(r2.Assigned);
        Assert.NotNull(r2.Released);

        var r3 = await sut.AssignPartToMinifigAsync(partId);
        Assert.True(r3.Assigned);
        Assert.NotNull(r3.Released);

        var r4 = await sut.AssignPartToMinifigAsync(partId);
        Assert.False(r4.Assigned);
        Assert.Null(r4.Released);

        await using var ctx = await _factory.CreateDbContextAsync();
        var p = await ctx.TrackedMinifigParts.SingleAsync(x => x.Id == partId);
        Assert.Equal(3, p.QuantityCollected);
        Assert.Equal(0, p.QuantityReservedFromBl);
        var lot = await ctx.BlInventoryLots.SingleAsync(l => l.LotId == 410);
        Assert.Equal(0, lot.ReservedQuantity);
    }

    // ====================================================================
    // v0.1.24-beta.13 (V5 Fortsetzung) — LookupPart-Filter:
    // Kandidaten-Liste enthaelt Figuren mit Reservierung UND Complete-via-
    // Reservierung-Figuren; Sortierung Waiting vor Complete.
    // ====================================================================

    [Fact]
    public async Task LookupPart_includes_waiting_minifig_with_reservation()
    {
        // Waiting-Figur mit Coll<Need, Res>0, Eff=Need → frueher
        // (Effective-Filter) ausgefiltert; jetzt soll sie in der Liste sein.
        var (sut, _) = await CreateLookupSutAsync();
        var binId = await SeedBinAsync("Box LP-Wait");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 2, qtyCollected: 0);
        await SeedBlReservationAsync(partId, lotId: 510, lotQuantity: 5, reservedCount: 2);

        var result = await sut.LookupPartAsync("3001", 11);

        Assert.Single(result.WaitingMatches);
        var match = result.WaitingMatches[0];
        Assert.Equal(partId, match.TrackedMinifigPartId);
        Assert.Equal(2, match.QuantityReservedFromBl);
        Assert.False(match.IsMinifigComplete);
    }

    [Fact]
    public async Task LookupPart_includes_complete_via_reservation_minifig()
    {
        // Complete-Figur: Coll<Need, Res>0, alle Parts effektiv gedeckt.
        // Status=Complete. Soll trotzdem als Kandidat erscheinen weil
        // ein Part physische Luecke hat — V5-Aufloesung waere moeglich.
        var (sut, _) = await CreateLookupSutAsync();
        var binId = await SeedBinAsync("Box LP-Cmpl");
        await using (var seedCtx = await _factory.CreateDbContextAsync())
        {
            var m = new TrackedMinifig
            {
                FigNum = "arc007",
                BricklinkId = "arc007",
                Name = "Complete-via-Res Figur",
                CreatedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Status = TrackedMinifigStatus.Complete,
                StorageBinId = binId,
                RequiredParts = new List<TrackedMinifigPart>
                {
                    new()
                    {
                        PartNumber = "3001",
                        ColorId = 11,
                        QuantityNeeded = 1,
                        QuantityCollected = 0,
                        QuantityReservedFromBl = 1
                    }
                }
            };
            seedCtx.TrackedMinifigs.Add(m);
            await seedCtx.SaveChangesAsync();
        }

        var result = await sut.LookupPartAsync("3001", 11);

        Assert.Single(result.WaitingMatches);
        var match = result.WaitingMatches[0];
        Assert.True(match.IsMinifigComplete);
        Assert.Equal(1, match.QuantityReservedFromBl);
    }

    [Fact]
    public async Task LookupPart_sorts_waiting_before_complete()
    {
        // Eine Waiting + eine Complete-via-Res. Sortierung: Waiting zuerst.
        var (sut, _) = await CreateLookupSutAsync();
        var binA = await SeedBinAsync("Box A");
        var binB = await SeedBinAsync("Box B");

        // Complete-via-Res
        await using (var seedCtx = await _factory.CreateDbContextAsync())
        {
            seedCtx.TrackedMinifigs.Add(new TrackedMinifig
            {
                FigNum = "arc008",
                BricklinkId = "arc008",
                Name = "Complete Fig",
                CreatedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Status = TrackedMinifigStatus.Complete,
                StorageBinId = binA,
                RequiredParts = new List<TrackedMinifigPart>
                {
                    new()
                    {
                        PartNumber = "3001", ColorId = 11,
                        QuantityNeeded = 1, QuantityCollected = 0,
                        QuantityReservedFromBl = 1
                    }
                }
            });
            await seedCtx.SaveChangesAsync();
        }
        // Waiting
        await SeedWaitingMinifigAsync(binB, "arc009", "3001", 11,
            qtyNeeded: 2, qtyCollected: 0);

        var result = await sut.LookupPartAsync("3001", 11);

        Assert.Equal(2, result.WaitingMatches.Count);
        // Waiting muss zuerst kommen (IsMinifigComplete=false)
        Assert.False(result.WaitingMatches[0].IsMinifigComplete);
        Assert.True(result.WaitingMatches[1].IsMinifigComplete);
    }

    /// <summary>
    /// Setup eines SUTs der LookupPartAsync ausfuehren kann. Catalog-Stub
    /// liefert minimale Stammdaten (Part-Name, Color-Name), kein Reverse-
    /// Lookup im BL-Cache (leere Liste).
    /// </summary>
    private async Task<(PartLookupService Sut, IBlInventoryService BlInv)> CreateLookupSutAsync()
    {
        await Task.CompletedTask;
        var blInv = new BlInventoryService(_factory, new ThrowBricklinkClient());
        var sut = new PartLookupService(
            _factory, new MinimalCatalog(), _persistence,
            new NotImplementedImageProvider(),
            cache: null, binService: null, blInventory: blInv);
        return (sut, blInv);
    }

    // ----- V5 Test-Helpers -----

    /// <summary>
    /// Erstellt einen V5-faehigen SUT (PartLookupService mit echtem
    /// BlInventoryService-Stub). Verwendet dieselbe DbContextFactory damit
    /// die Tests Reservierungen seeden koennen.
    /// </summary>
    private async Task<(PartLookupService Sut, IBlInventoryService BlInv)> CreateV5SutAsync()
    {
        await Task.CompletedTask;
        var blInv = new BlInventoryService(_factory, new ThrowBricklinkClient());
        var sut = new PartLookupService(
            _factory, new NotImplementedCatalog(), _persistence,
            new NotImplementedImageProvider(),
            cache: null, binService: null, blInventory: blInv);
        return (sut, blInv);
    }

    /// <summary>
    /// Seedet einen BlInventoryLot + einen offenen BlInventoryReservation-
    /// ScanEvent pro reservierter Einheit (LIFO-Reihenfolge via Timestamp).
    /// </summary>
    private async Task SeedBlReservationAsync(
        int trackedMinifigPartId, int lotId, int lotQuantity, int reservedCount)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.BlInventoryLots.Add(new BlInventoryLot
        {
            LotId = lotId,
            ItemType = "P",
            ItemNo = "3001",
            ColorId = 11,
            Quantity = lotQuantity,
            ReservedQuantity = reservedCount,
            UnitPrice = 0.1m,
            Condition = "N",
            LastSyncedAt = DateTime.UtcNow
        });
        // Pro reservierter Einheit einen ScanEvent mit UndoSnapshot.
        var now = DateTime.UtcNow;
        for (int i = 0; i < reservedCount; i++)
        {
            var snap = new UndoSnapshotBlReservation
            {
                TrackedMinifigPartId = trackedMinifigPartId,
                LotId = lotId
            };
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = now.AddSeconds(-i), // juengste zuerst gefunden (LIFO)
                Type = ScanType.BlInventoryReservation,
                RecognizedId = lotId.ToString(),
                ResultDescription = $"Test-Reservierung Lot {lotId}",
                WasUndone = false,
                UndoData = System.Text.Json.JsonSerializer.Serialize(snap)
            });
        }
        // Part muss QuantityReservedFromBl = reservedCount haben.
        var part = await ctx.TrackedMinifigParts
            .FirstAsync(p => p.Id == trackedMinifigPartId);
        part.QuantityReservedFromBl = reservedCount;
        await ctx.SaveChangesAsync();
    }

    // ====================================================================
    // UnassignPartFromMinifigAsync
    // ====================================================================

    [Fact]
    public async Task UnassignPart_resets_quantity_to_zero()
    {
        var binId = await SeedBinAsync("Box 01");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 3, qtyCollected: 2);

        var changed = await _sut.UnassignPartFromMinifigAsync(partId);

        Assert.True(changed);
        await using var ctx = await _factory.CreateDbContextAsync();
        var part = await ctx.TrackedMinifigParts.SingleAsync(p => p.Id == partId);
        Assert.Equal(0, part.QuantityCollected);
    }

    [Fact]
    public async Task UnassignPart_reverts_complete_to_waiting()
    {
        // Figur ist Complete (alle Parts voll). Wir nehmen ein Part-Slot
        // zurueck -> Status muss auf Waiting zurueck.
        var binId = await SeedBinAsync("Box 01");
        var (minifigId, partId) = await SeedCompleteMinifigAsync(binId, "arc007", "3001", 11);

        var changed = await _sut.UnassignPartFromMinifigAsync(partId);

        Assert.True(changed);
        await using var ctx = await _factory.CreateDbContextAsync();
        var minifig = await ctx.TrackedMinifigs.SingleAsync(m => m.Id == minifigId);
        Assert.Equal(TrackedMinifigStatus.Waiting, minifig.Status);
        Assert.Null(minifig.CompletedAt);
    }

    [Fact]
    public async Task UnassignPart_reverting_complete_decrements_DailyStats_and_writes_audit_event()
    {
        // UX X.20 Teil 7e: wenn der User durch Unassign eine komplette Figur
        // zurueck auf Waiting bringt, soll der DailyStats-Komplettierungs-
        // Counter am Tag des damaligen CompletedAt um eins reduziert werden.
        var binId = await SeedBinAsync("Box 01");
        var (minifigId, partId) = await SeedCompleteMinifigAsync(binId, "arc007", "3001", 11);

        // Den DailyStats-Eintrag fuer "heute UTC" anlegen mit Counter=1
        // (so wuerde er nach einem PersistAndStore-Auto-Complete aussehen).
        await using (var seedCtx = await _factory.CreateDbContextAsync())
        {
            var todayUtc = DateTime.UtcNow.Date;
            seedCtx.DailyStats.Add(new DailyStats
            {
                Date = todayUtc,
                ScanCount = 1,
                MinifigsCompletedCount = 1
            });
            // Damit das CompletedAt der Figur und der DailyStats-Tag uebereinstimmen,
            // setzen wir CompletedAt explizit auf "heute UTC" mit Mittagszeit.
            var m = await seedCtx.TrackedMinifigs.FirstAsync(m => m.Id == minifigId);
            m.CompletedAt = todayUtc.AddHours(12);
            await seedCtx.SaveChangesAsync();
        }

        var changed = await _sut.UnassignPartFromMinifigAsync(partId);
        Assert.True(changed);

        await using var ctx = await _factory.CreateDbContextAsync();
        var statsToday = await ctx.DailyStats.FirstAsync();
        Assert.Equal(0, statsToday.MinifigsCompletedCount); // Decrement!
        Assert.Equal(1, statsToday.ScanCount);              // andere Counter unangetastet

        // ScanEvent als Audit-Trail muss den Status-Wechsel deutlich machen.
        var scanEvent = await ctx.ScanEvents.SingleAsync();
        Assert.Contains("Komplett", scanEvent.ResultDescription);
        Assert.Contains("Wartend", scanEvent.ResultDescription);
    }

    [Fact]
    public async Task UnassignPart_on_waiting_minifig_does_not_touch_DailyStats()
    {
        // Negativ-Pfad: bei einer wartenden Figur (war nie komplett) darf
        // der Counter nicht angefasst werden, auch wenn am selben Tag ein
        // anderer Eintrag in DailyStats existiert.
        var binId = await SeedBinAsync("Box 01");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 3, qtyCollected: 2);

        await using (var seedCtx = await _factory.CreateDbContextAsync())
        {
            seedCtx.DailyStats.Add(new DailyStats
            {
                Date = DateTime.UtcNow.Date,
                MinifigsCompletedCount = 5  // bewusst hoch
            });
            await seedCtx.SaveChangesAsync();
        }

        await _sut.UnassignPartFromMinifigAsync(partId);

        await using var ctx = await _factory.CreateDbContextAsync();
        var stats = await ctx.DailyStats.FirstAsync();
        Assert.Equal(5, stats.MinifigsCompletedCount); // unveraendert
    }

    [Fact]
    public async Task UnassignPart_returns_false_when_nothing_collected()
    {
        var binId = await SeedBinAsync("Box 01");
        var (_, partId) = await SeedWaitingMinifigAsync(binId, "arc007", "3001", 11,
            qtyNeeded: 3, qtyCollected: 0);

        var changed = await _sut.UnassignPartFromMinifigAsync(partId);
        Assert.False(changed);
    }

    // ====================================================================
    // FindFloatingLocationsAsync + DeleteFloatingPartAsync
    // ====================================================================

    [Fact]
    public async Task FindFloatingLocations_sorts_by_quantity_desc()
    {
        var binA = await SeedBinAsync("Box A");
        var binB = await SeedBinAsync("Box B");
        var binC = await SeedBinAsync("Box C");
        await SeedFloatingAsync(binA, "3001", 11, qty: 2);
        await SeedFloatingAsync(binB, "3001", 11, qty: 7);
        await SeedFloatingAsync(binC, "3001", 11, qty: 4);

        var locations = await _sut.FindFloatingLocationsAsync("3001", 11);

        Assert.Equal(3, locations.Count);
        Assert.Equal(7, locations[0].TotalQuantity); // groesste zuerst
        Assert.Equal(4, locations[1].TotalQuantity);
        Assert.Equal(2, locations[2].TotalQuantity);
    }

    [Fact]
    public async Task DeleteFloatingPart_removes_entry()
    {
        var binId = await SeedBinAsync("Box 01");
        var fpId = await SeedFloatingAsync(binId, "3001", 11, qty: 3);

        var ok = await _sut.DeleteFloatingPartAsync(fpId);
        Assert.True(ok);

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.FloatingParts.ToListAsync());
    }

    [Fact]
    public async Task DeleteFloatingPart_returns_false_for_unknown_id()
    {
        var ok = await _sut.DeleteFloatingPartAsync(9999);
        Assert.False(ok);
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

    private async Task<(int MinifigId, int PartId)> SeedWaitingMinifigAsync(
        int binId, string blId, string partNo, int colorId,
        int qtyNeeded, int qtyCollected)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var minifig = new TrackedMinifig
        {
            FigNum = blId,
            BricklinkId = blId,
            Name = $"Test {blId}",
            CreatedAt = DateTime.UtcNow,
            Status = TrackedMinifigStatus.Waiting,
            StorageBinId = binId,
            RequiredParts = new List<TrackedMinifigPart>
            {
                new()
                {
                    PartNumber = partNo,
                    ColorId = colorId,
                    QuantityNeeded = qtyNeeded,
                    QuantityCollected = qtyCollected
                }
            }
        };
        ctx.TrackedMinifigs.Add(minifig);
        await ctx.SaveChangesAsync();
        return (minifig.Id, minifig.RequiredParts[0].Id);
    }

    private async Task<(int MinifigId, int PartId)> SeedCompleteMinifigAsync(
        int binId, string blId, string partNo, int colorId)
    {
        // QuantityNeeded=1, QuantityCollected=1 -> Status=Complete.
        await using var ctx = await _factory.CreateDbContextAsync();
        var minifig = new TrackedMinifig
        {
            FigNum = blId,
            BricklinkId = blId,
            Name = $"Test {blId}",
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            Status = TrackedMinifigStatus.Complete,
            StorageBinId = binId,
            RequiredParts = new List<TrackedMinifigPart>
            {
                new()
                {
                    PartNumber = partNo,
                    ColorId = colorId,
                    QuantityNeeded = 1,
                    QuantityCollected = 1
                }
            }
        };
        ctx.TrackedMinifigs.Add(minifig);
        await ctx.SaveChangesAsync();
        return (minifig.Id, minifig.RequiredParts[0].Id);
    }

    // ----- Stubs -----

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    private sealed class StubPersistence : IMinifigPersistenceService
    {
        public int RaiseCount { get; private set; }
        public event EventHandler? DataChanged;
        public void RaiseDataChanged() { RaiseCount++; DataChanged?.Invoke(this, EventArgs.Empty); }
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

    /// <summary>
    /// v0.1.24-beta.13 (V5 Fortsetzung): Catalog-Stub fuer LookupPart-Tests.
    /// Liefert minimale Stammdaten + leere Reverse-Listen, der Rest wirft.
    /// </summary>
    private sealed class MinimalCatalog : IBlCatalogService
    {
        public Task<BlItem?> GetPartDetailsAsync(string blPartNo, CancellationToken ct = default)
            => Task.FromResult<BlItem?>(new BlItem
            {
                ItemType = "P",
                ItemNo = blPartNo,
                Name = $"Part {blPartNo}"
            });
        public Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<BlColor>
            {
                new() { ColorId = 11, Name = "Black", Rgb = "212121" }
            });
        public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(string blPartNo, int blColorId, CancellationToken ct = default)
            => Task.FromResult(new List<BlMinifigSubsetMatch>());
        public Task<BlItem?> GetMinifigDetailsAsync(string blMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlSubset>> GetMinifigPartsAsync(string blMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> FindWaitingMinifigsForPartAsync(string blPartNo, int blColorId, IEnumerable<string> waitingMinifigIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BlCacheStats> GetCacheStatsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearCacheAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearStaleAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlSubset>> GetSupersetsAsync(string blPartNo, int blColorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> EnsureFullSubsetsAsync(string blMinifigId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlColor>> GetKnownColorsAsync(string blPartNo, CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>
    /// v0.1.24-beta.13 (V5): Stub fuer IBricklinkClient. Alle API-Methoden
    /// werfen — die V5-Pfade rufen nichts davon (nur DB-Operationen).
    /// </summary>
    private sealed class ThrowBricklinkClient : IBricklinkClient
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
}
