using HBSort.Core.Database;
using HBSort.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// v0.1.24-beta.6 Phase 1: Default-Implementation des BL-Inventar-Service.
/// Snapshot-Replace-Strategie: jeder Sync loescht den bestehenden Spiegel
/// komplett und schreibt die neue Liste neu. LastSyncedAt wird pro Eintrag
/// auf den Sync-Zeitpunkt gesetzt.
///
/// <para>
/// <b>ReservedQuantity-Lebenszyklus</b> — Stand v0.1.24-beta.10:
/// </para>
/// <list type="number">
///   <item>
///     <b>Reservieren</b> (Phase 3, implementiert): User klickt im
///     Summary-Dialog auf das BL-Shop-Badge eines fehlenden Required-Parts;
///     <see cref="ReserveAsync"/> erhoeht <c>BlInventoryLot.ReservedQuantity</c>
///     und <c>TrackedMinifigPart.QuantityReservedFromBl</c>. Verfuegbar zum
///     Verkauf = <c>Quantity - ReservedQuantity</c>.
///   </item>
///   <item>
///     <b>Manuelles Release</b> (Phase 3, implementiert): User entfernt den
///     Haken am Required-Part im Summary-Dialog;
///     <c>MinifigSummaryViewModel.ReleaseAllBlReservationsAsync</c> findet
///     die zugehoerigen ScanEvents per <see cref="UndoSnapshotBlReservation"/>-
///     JSON und ruft pro Reservierung <see cref="ReleaseAsync"/> (LIFO).
///   </item>
///   <item>
///     <b>Release bei Figur-Entfernung</b> (Phase V1 / beta.10, implementiert):
///     <see cref="ReleaseAllForMinifigsAsync"/> wird vom
///     <c>IMinifigPersistenceService</c> VOR jedem Loesch-/Zerlegungs-/
///     Cleanup-Pfad aufgerufen. Damit bleiben keine Geist-Reservierungen
///     zurueck wenn die Figur weg ist.
///   </item>
///   <item>
///     <b>Snapshot-Replace mit Reservierungs-Erhalt</b> (Phase 1, implementiert):
///     <see cref="SyncInventoryAsync"/> merkt sich vor dem Snapshot-Replace
///     <c>(LotId, ReservedQuantity)</c> und stellt den Wert nach dem Insert
///     fuer jedes weiterhin existierende Lot wieder her — gecappt auf die
///     neue <c>Quantity</c> (V6 / beta.10). Lots die in der neuen BL-Antwort
///     nicht mehr vorkommen verlieren ihre Reservierung; das ist beabsichtigt
///     (Phase-1-Trade-off, dokumentiert).
///   </item>
///   <item>
///     <b>Export / Mass-Update</b> (Phase 4, NOCH NICHT IMPLEMENTIERT):
///     vorgesehen ist ein BSX/Mass-Update-Dialog der die Reservierungen
///     auf BL-Seite reduziert (<c>Quantity</c> sinkt) und danach
///     <c>ReservedQuantity = 0</c> setzt. Der naechste Sync holt dann den
///     reduzierten <c>Quantity</c>-Wert direkt von BL. Bis Phase 4 fehlt
///     dieser Pfad — User muss aktuell die Reservierungen entweder manuell
///     im Summary-Dialog aufheben oder die Figur aufgeben (was V1 jetzt
///     korrekt freigibt).
///   </item>
/// </list>
/// </summary>
public class BlInventoryService : IBlInventoryService
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IBricklinkClient _bricklinkClient;

    public BlInventoryService(
        IDbContextFactory<UserDataContext> ctxFactory,
        IBricklinkClient bricklinkClient)
    {
        _ctxFactory = ctxFactory;
        _bricklinkClient = bricklinkClient;
    }

    /// <inheritdoc />
    public event EventHandler? InventoryChanged;

    public async Task<int> SyncInventoryAsync(CancellationToken ct = default)
    {
        // 1) Frischen Snapshot von der BL-API ziehen. Auth/RateLimit-Errors
        //    propagieren - der UI-Layer macht den User-freundlichen Toast.
        var lots = await _bricklinkClient.GetInventoryAsync(ct);
        var syncedAt = DateTime.UtcNow;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // 2) ReservedQuantity-Snapshot pro LotId merken BEVOR der Snapshot-
        //    Replace zuschlaegt. Lots die nach dem Sync nicht mehr in der
        //    BL-Antwort vorkommen (gelistet/ausverkauft) verlieren ihre
        //    Reservierung - das ist Phase-1-akzeptabel (Phase 2 koennte
        //    solche "Waisen" als Warning auflisten).
        var reservedByLot = await ctx.BlInventoryLots
            .AsNoTracking()
            .Where(l => l.ReservedQuantity > 0)
            .ToDictionaryAsync(l => l.LotId, l => l.ReservedQuantity, ct);

        // 3) Snapshot-Replace: alle alten Lots loeschen, neue Lots einfuegen.
        //    ExecuteDeleteAsync ist effizienter als Bulk-RemoveRange (kein
        //    Materialisieren noetig). EF Core 7+ Pflicht.
        var deletedCount = await ctx.BlInventoryLots.ExecuteDeleteAsync(ct);

        // 4) Neuen Stand einfuegen. Pro Lot: ReservedQuantity aus dem
        //    Snapshot wiederherstellen (0 wenn das Lot neu ist oder vorher
        //    keine Reservierung hatte).
        //
        // V6 (beta.10 / Audit H4): Reserved wird auf die neue Quantity
        // gecappt. Szenario: User reserviert 5 von 10, verkauft im BL-Shop
        // 7 Stueck (Quantity auf 3), Sync laeuft. Ohne Cap waere
        // ReservedQuantity=5 > Quantity=3 -> Available=-2, das Lot wird
        // von FindLotsForPartAsync ausgefiltert und die Buchhaltung ist
        // korrupt. Cap auf Quantity verhindert das; Diff wird geloggt.
        int restoredReservations = 0;
        int cappedReservations = 0;
        foreach (var dto in lots)
        {
            var rawReserved = reservedByLot.TryGetValue(dto.LotId, out var r) ? r : 0;
            var reserved = Math.Min(rawReserved, dto.Quantity);
            if (reserved < rawReserved)
            {
                Log.Warning(
                    "BL-Inventar-Sync: Lot {LotId} ReservedQuantity gecappt {Old} -> {New} (neue Quantity={Q})",
                    dto.LotId, rawReserved, reserved, dto.Quantity);
                cappedReservations++;
            }
            if (reserved > 0) restoredReservations++;
            ctx.BlInventoryLots.Add(new BlInventoryLot
            {
                LotId = dto.LotId,
                ItemType = dto.ItemType,
                ItemNo = dto.ItemNo,
                ColorId = dto.ColorId,
                ColorName = dto.ColorName,
                Description = dto.Description,
                Remarks = dto.Remarks,
                Quantity = dto.Quantity,
                UnitPrice = dto.UnitPrice,
                Condition = dto.Condition,
                ReservedQuantity = reserved,
                LastSyncedAt = syncedAt
            });
        }
        await ctx.SaveChangesAsync(ct);

        Log.Information(
            "BL-Inventar-Sync: {Old} alte Eintraege geloescht, {New} neue gespeichert, " +
            "{Restored} Reservierungen erhalten, {Capped} gecappt, " +
            "{Lost} Reservierungen verloren (Lots nicht mehr im BL-Store) (UTC {When:O})",
            deletedCount, lots.Count, restoredReservations, cappedReservations,
            reservedByLot.Count - restoredReservations,
            syncedAt);

        // v0.1.24-beta.7 Phase 2: alle Konsumenten informieren (Inventar-Tab
        // refreshed seine Liste, auch wenn der Sync aus dem Settings-Tab kam).
        InventoryChanged?.Invoke(this, EventArgs.Empty);
        return lots.Count;
    }

    public async Task<List<BlInventoryLot>> GetInventoryAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        return await ctx.BlInventoryLots
            .AsNoTracking()
            .OrderBy(l => l.ItemType)
            .ThenBy(l => l.ItemNo)
            .ThenBy(l => l.ColorId)
            .ToListAsync(ct);
    }

    // ====================================================================
    // v0.1.24-beta.8 Phase 3: Komplettieren-Integration
    // ====================================================================

    public async Task<bool> HasAnyInventoryAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        return await ctx.BlInventoryLots.AsNoTracking().AnyAsync(ct);
    }

    public async Task<List<BlInventoryLot>> FindLotsForPartAsync(
        string blPartNo, int? colorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blPartNo)) return new List<BlInventoryLot>();

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var query = ctx.BlInventoryLots
            .AsNoTracking()
            .Where(l => l.ItemNo == blPartNo);
        // Farblos-Lots (Minifig/Set) haben ColorId=null. Wenn der Aufrufer
        // colorId=null mitgibt, matched das auf null in der DB.
        if (colorId.HasValue)
            query = query.Where(l => l.ColorId == colorId.Value);
        else
            query = query.Where(l => l.ColorId == null);

        var lots = await query.ToListAsync(ct);
        // Verfuegbarkeit-Filter + Sortierung in-memory (Available ist
        // computed, kein DB-Spalte). Bei sehr grossen Stores immer noch
        // schnell (Pre-Filter ueber Index IX_BlInventoryLots_ItemType_ItemNo_ColorId).
        return lots
            .Where(l => l.Quantity - l.ReservedQuantity > 0)
            .OrderBy(l => l.Condition == "N" ? 0 : 1) // Neu vor Gebraucht
            .ThenByDescending(l => l.Quantity - l.ReservedQuantity)
            .ToList();
    }

    public async Task<bool> ReserveAsync(int lotId, int qty = 1, CancellationToken ct = default)
    {
        if (qty <= 0) return false;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var lot = await ctx.BlInventoryLots.FirstOrDefaultAsync(l => l.LotId == lotId, ct);
        if (lot == null) return false;
        var available = lot.Quantity - lot.ReservedQuantity;
        if (available < qty)
        {
            Log.Warning(
                "BL-Reserve abgewiesen: Lot {LotId} hat nur {Available} verfuegbar (angefragt {Qty})",
                lotId, available, qty);
            return false;
        }

        lot.ReservedQuantity += qty;
        await ctx.SaveChangesAsync(ct);
        Log.Information("BL-Reserve: Lot {LotId} +{Qty} -> Reserviert {Reserved}/{Total}",
            lotId, qty, lot.ReservedQuantity, lot.Quantity);

        InventoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> ReleaseAsync(int lotId, int qty = 1, CancellationToken ct = default)
    {
        if (qty <= 0) return false;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var lot = await ctx.BlInventoryLots.FirstOrDefaultAsync(l => l.LotId == lotId, ct);
        if (lot == null) return false;
        if (lot.ReservedQuantity < qty)
        {
            Log.Warning(
                "BL-Release abgewiesen: Lot {LotId} hat nur {Reserved} reserviert (angefragt {Qty})",
                lotId, lot.ReservedQuantity, qty);
            return false;
        }

        lot.ReservedQuantity -= qty;
        await ctx.SaveChangesAsync(ct);
        Log.Information("BL-Release: Lot {LotId} -{Qty} -> Reserviert {Reserved}/{Total}",
            lotId, qty, lot.ReservedQuantity, lot.Quantity);

        InventoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> ReleaseAllForMinifigsAsync(
        IEnumerable<int> trackedMinifigIds, CancellationToken ct = default)
    {
        var ids = trackedMinifigIds?.Where(id => id > 0).Distinct().ToList()
                  ?? new List<int>();
        if (ids.Count == 0) return 0;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // 1) Welche Required-Parts der Minifigs haben offene Reservierungen?
        var partsWithReservations = await ctx.TrackedMinifigParts
            .Where(p => ids.Contains(p.TrackedMinifigId)
                     && p.QuantityReservedFromBl > 0)
            .ToListAsync(ct);
        if (partsWithReservations.Count == 0) return 0;

        var partIdsLookup = partsWithReservations.Select(p => p.Id).ToHashSet();

        // 2) Reservierungs-ScanEvents materialisieren und in-memory ueber
        //    UndoSnapshotBlReservation.TrackedMinifigPartId matchen.
        var reservations = await ctx.ScanEvents
            .Where(e => e.Type == ScanType.BlInventoryReservation && !e.WasUndone)
            .ToListAsync(ct);

        var matching = new List<(ScanEvent Event, UndoSnapshotBlReservation Snap)>();
        foreach (var ev in reservations)
        {
            if (string.IsNullOrEmpty(ev.UndoData)) continue;
            try
            {
                var snap = System.Text.Json.JsonSerializer
                    .Deserialize<UndoSnapshotBlReservation>(ev.UndoData);
                if (snap != null && partIdsLookup.Contains(snap.TrackedMinifigPartId))
                    matching.Add((ev, snap));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ReleaseAllForMinifigs: ScanEvent {Id} UndoData unparsable - skipping", ev.Id);
            }
        }

        if (matching.Count == 0)
        {
            // Dateninkonsistenz: Parts sagen "X reserviert" aber keine matching
            // ScanEvents. Defensiv: trotzdem die Felder zuruecksetzen damit der
            // Cascade-Delete im Aufrufer-Context konsistent ist.
            Log.Warning(
                "ReleaseAllForMinifigs({Ids}): {PartCount} Parts haben Reservierungen aber keine matching ScanEvents - Felder werden ohne Lot-Release zurueckgesetzt",
                string.Join(",", ids), partsWithReservations.Count);
            foreach (var p in partsWithReservations) p.QuantityReservedFromBl = 0;
            await ctx.SaveChangesAsync(ct);
            return 0;
        }

        // 3) LIFO Release: neueste Reservierung zuerst freigeben.
        int released = 0;
        var now = DateTime.UtcNow;

        foreach (var (ev, snap) in matching.OrderByDescending(m => m.Event.Timestamp))
        {
            var lot = await ctx.BlInventoryLots
                .FirstOrDefaultAsync(l => l.LotId == snap.LotId, ct);
            if (lot == null)
            {
                Log.Warning(
                    "ReleaseAllForMinifigs: Lot {LotId} nicht mehr in DB (Sync hat es verloren) - ScanEvent {EvId} wird trotzdem als undone markiert",
                    snap.LotId, ev.Id);
                ev.WasUndone = true;
                ev.UndoneAt = now;
                continue;
            }
            if (lot.ReservedQuantity < 1)
            {
                Log.Warning(
                    "ReleaseAllForMinifigs: Lot {LotId} hat ReservedQuantity=0 - ScanEvent {EvId} bleibt offen (Drift)",
                    snap.LotId, ev.Id);
                continue;
            }

            lot.ReservedQuantity -= 1;
            ev.WasUndone = true;
            ev.UndoneAt = now;

            // Audit-Release-Event. Beschreibung verraet die Original-Reservierung
            // (PartName/Condition aus dem alten Event).
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = now,
                Type = ScanType.BlInventoryRelease,
                RecognizedId = snap.LotId.ToString(),
                ResultDescription = $"BL-Reservierung freigegeben (Lot {snap.LotId}, Auto-Release wegen Figur-Entfernung)",
                WasUndone = false
            });
            released++;
        }

        // 4) Part-Felder zuruecksetzen (defensiv fuer standalone-Aufrufe).
        //    Im Aufrufer-Kontext loescht Cascade-Delete die Parts gleich danach.
        foreach (var p in partsWithReservations) p.QuantityReservedFromBl = 0;

        await ctx.SaveChangesAsync(ct);

        Log.Information(
            "ReleaseAllForMinifigs({Ids}): {Released} Reservierungen freigegeben (von {Matching} matching ScanEvents)",
            string.Join(",", ids), released, matching.Count);

        if (released > 0) InventoryChanged?.Invoke(this, EventArgs.Empty);
        return released;
    }
}
