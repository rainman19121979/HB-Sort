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
///     <b>Mass-Update-Export</b> (Phase 4 / beta.12, implementiert):
///     <see cref="GenerateMassUpdateXmlAsync"/> erzeugt das BL-Mass-Update-
///     XML fuer alle Lots mit offener Reservierung (DELETE wenn Reserved
///     &gt;= Quantity, sonst QTY-Reduktion) und speichert pro Lot einen
///     <see cref="HBSort.Core.Models.PendingExport"/>-Snapshot mit den
///     Soll-Werten. <see cref="VerifyExportAsync"/> vergleicht den
///     Snapshot mit einem frischen BL-Inventar und konvertiert pro
///     erfolgreichem Lot die offenen <c>BlInventoryReservation</c>-
///     ScanEvents in Collected-Buchungen (<c>QuantityCollected += 1</c>,
///     <c>QuantityReservedFromBl -= 1</c>) - EffectiveCollected bleibt
///     invariant, nur die Quelle wechselt von "im BL reserviert" zu
///     "physisch entnommen". Bei vollstaendigem Erfolg laeuft danach
///     ein Auto-Sync damit der lokale Spiegel mit BL synchron ist.
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
    public async Task<bool> ReserveForPartAsync(
        int trackedMinifigPartId, int lotId, CancellationToken ct = default)
    {
        // Phase 1: BL-Lot reservieren. Atomar via SaveChangesAsync,
        // ReservedQuantity-Check eingebaut.
        var reserved = await ReserveAsync(lotId, 1, ct);
        if (!reserved) return false;

        // Phase 2: TrackedMinifigPart.QuantityReservedFromBl + ScanEvent
        // in eigener Transaktion. Bei Fehler: Lot-Reserve via ReleaseAsync
        // kompensieren.
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
            var dbPart = await ctx.TrackedMinifigParts
                .Include(p => p.TrackedMinifig)
                .FirstOrDefaultAsync(p => p.Id == trackedMinifigPartId, ct);
            if (dbPart == null)
            {
                await ReleaseAsync(lotId, 1, ct);
                Log.Warning("ReserveForPart: TrackedMinifigPart {Id} nicht gefunden - Reserve auf Lot {Lot} kompensiert",
                    trackedMinifigPartId, lotId);
                return false;
            }

            // Lot-Details fuer ResultDescription (PartName/Condition).
            var lot = await ctx.BlInventoryLots
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.LotId == lotId, ct);
            var conditionLabel = lot?.Condition == "N" ? "Neu" : "Gebraucht";

            dbPart.QuantityReservedFromBl += 1;

            var undoSnapshot = new UndoSnapshotBlReservation
            {
                TrackedMinifigPartId = dbPart.Id,
                LotId = lotId
            };
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = DateTime.UtcNow,
                Type = ScanType.BlInventoryReservation,
                RecognizedId = lotId.ToString(),
                ResultDescription = $"BL-Reservierung: {dbPart.PartName} ({conditionLabel}) " +
                                    $"fuer '{dbPart.TrackedMinifig.Name}'",
                UndoData = System.Text.Json.JsonSerializer.Serialize(undoSnapshot),
                WasUndone = false
            });

            await ctx.SaveChangesAsync(ct);
            Log.Information(
                "ReserveForPart: Part {PartId} - Lot {LotId} reserviert; QuantityReservedFromBl={Q}",
                trackedMinifigPartId, lotId, dbPart.QuantityReservedFromBl);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ReserveForPart: DB-Update fehlgeschlagen - releasing Lot {LotId}", lotId);
            try { await ReleaseAsync(lotId, 1, ct); }
            catch (Exception relEx) { Log.Warning(relEx, "Release nach DB-Fehler ebenfalls geworfen"); }
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<(int? ScanEventId, int LotId)> FindLatestReservationScanEventAsync(
        int trackedMinifigPartId, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        // Materialisierung in-memory: SQLite kann das UndoData-JSON nicht
        // server-seitig deserialisieren. Offene Reservierungen sind in der
        // Praxis < 100, also OK.
        var open = await ctx.ScanEvents
            .AsNoTracking()
            .Where(e => e.Type == ScanType.BlInventoryReservation && !e.WasUndone)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(ct);

        foreach (var ev in open)
        {
            if (string.IsNullOrEmpty(ev.UndoData)) continue;
            try
            {
                var snap = System.Text.Json.JsonSerializer
                    .Deserialize<UndoSnapshotBlReservation>(ev.UndoData);
                if (snap != null && snap.TrackedMinifigPartId == trackedMinifigPartId)
                    return (ev.Id, snap.LotId);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "FindLatestReservationScanEvent: ScanEvent {Id} UndoData unparsable", ev.Id);
            }
        }
        return (null, 0);
    }

    /// <inheritdoc />
    public async Task<List<BlReservationDetail>> GetReservationsForLotAsync(
        int lotId, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var lot = await ctx.BlInventoryLots
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.LotId == lotId, ct);
        if (lot == null || lot.ReservedQuantity <= 0)
            return new List<BlReservationDetail>();

        // Offene Reservierungs-ScanEvents materialisieren und in-memory
        // ueber UndoSnapshotBlReservation gegen die LotId filtern (JSON-
        // Where ist auf SQLite ineffizient; offene Reservierungen sind
        // typischerweise < 100).
        var open = await ctx.ScanEvents
            .AsNoTracking()
            .Where(e => e.Type == ScanType.BlInventoryReservation && !e.WasUndone)
            .ToListAsync(ct);

        var matching = new List<(ScanEvent Ev, UndoSnapshotBlReservation Snap)>();
        foreach (var ev in open)
        {
            if (string.IsNullOrEmpty(ev.UndoData)) continue;
            try
            {
                var snap = System.Text.Json.JsonSerializer
                    .Deserialize<UndoSnapshotBlReservation>(ev.UndoData);
                if (snap != null && snap.LotId == lotId)
                    matching.Add((ev, snap));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "GetReservationsForLot: ScanEvent {Id} UndoData unparsable", ev.Id);
            }
        }

        // Lookup TrackedMinifigPart (+ TrackedMinifig) fuer die matching Snaps.
        var partIds = matching.Select(m => m.Snap.TrackedMinifigPartId).Distinct().ToList();
        var partLookup = await ctx.TrackedMinifigParts
            .AsNoTracking()
            .Include(p => p.TrackedMinifig)
            .Where(p => partIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var details = new List<BlReservationDetail>();
        foreach (var (ev, snap) in matching.OrderByDescending(m => m.Ev.Timestamp))
        {
            partLookup.TryGetValue(snap.TrackedMinifigPartId, out var part);
            var fig = part?.TrackedMinifig;
            details.Add(new BlReservationDetail
            {
                ScanEventId = ev.Id,
                LotId = lotId,
                Quantity = 1,
                TrackedMinifigId = fig?.Id,
                FigureName = fig?.Name ?? string.Empty,
                FigureBricklinkId = fig?.BricklinkId,
                IsOrphan = fig == null,
                ReservedAt = ev.Timestamp
            });
        }

        // Lot-Drift: Lot zeigt mehr reservierte Stueck als matching
        // ScanEvents. Pro Diff-Stueck eine ScanEvent-lose Orphan-Zeile -
        // diese kann nur ueber reine Lot-Dekrementierung freigegeben werden.
        var drift = lot.ReservedQuantity - matching.Count;
        for (int i = 0; i < drift; i++)
        {
            details.Add(new BlReservationDetail
            {
                ScanEventId = null,
                LotId = lotId,
                Quantity = 1,
                IsOrphan = true,
                FigureName = string.Empty
            });
        }

        return details;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseSingleReservationAsync(
        int? scanEventId, int lotId, CancellationToken ct = default)
    {
        // --- Pfad A: reine Drift-Bereinigung (ohne ScanEvent-Bezug) ---
        if (scanEventId == null)
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
            var lot = await ctx.BlInventoryLots
                .FirstOrDefaultAsync(l => l.LotId == lotId, ct);
            if (lot == null || lot.ReservedQuantity <= 0) return false;

            lot.ReservedQuantity -= 1;
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = DateTime.UtcNow,
                Type = ScanType.BlInventoryRelease,
                RecognizedId = lotId.ToString(),
                ResultDescription = $"BL-Reservierung freigegeben (Lot {lotId}, manuelle Drift-Bereinigung - " +
                                    $"kein ScanEvent zur Original-Reservierung)",
                WasUndone = false
            });
            await ctx.SaveChangesAsync(ct);
            Log.Information(
                "ReleaseSingleReservation[drift]: Lot {LotId} -1 -> Reserviert {R}/{T}",
                lotId, lot.ReservedQuantity, lot.Quantity);
            InventoryChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // --- Pfad B: ScanEvent-basiert ---
        await using var ctx2 = await _ctxFactory.CreateDbContextAsync(ct);
        var ev = await ctx2.ScanEvents
            .FirstOrDefaultAsync(e => e.Id == scanEventId.Value, ct);
        if (ev == null || ev.WasUndone || ev.Type != ScanType.BlInventoryReservation)
            return false;

        UndoSnapshotBlReservation? snap = null;
        try
        {
            if (!string.IsNullOrEmpty(ev.UndoData))
                snap = System.Text.Json.JsonSerializer
                    .Deserialize<UndoSnapshotBlReservation>(ev.UndoData);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReleaseSingleReservation: ScanEvent {Id} UndoData unparsable", ev.Id);
        }
        if (snap == null) return false;

        var bLot = await ctx2.BlInventoryLots
            .FirstOrDefaultAsync(l => l.LotId == snap.LotId, ct);
        if (bLot == null || bLot.ReservedQuantity <= 0) return false;

        bLot.ReservedQuantity -= 1;
        var now = DateTime.UtcNow;
        ev.WasUndone = true;
        ev.UndoneAt = now;

        // Figur-/Part-Bezug optional: Cascade-Delete bei Figur-Zerlegung kann
        // Part bereits entfernt haben (Orphan-ScanEvent). In dem Fall nur Lot+Event.
        var part = await ctx2.TrackedMinifigParts
            .Include(p => p.TrackedMinifig)
                .ThenInclude(m => m!.RequiredParts)
            .FirstOrDefaultAsync(p => p.Id == snap.TrackedMinifigPartId, ct);

        string descPart = "(verwaist)";
        string descFig  = "(verwaist)";
        if (part != null)
        {
            descPart = part.PartName;
            part.QuantityReservedFromBl = Math.Max(0, part.QuantityReservedFromBl - 1);

            var fig = part.TrackedMinifig;
            if (fig != null)
            {
                descFig = fig.Name;
                if (fig.Status == TrackedMinifigStatus.Complete)
                {
                    // RequiredParts in-memory: der bereits dekrementierte
                    // part wird ueber p.Id gefunden (gleiche Tracked-Instance).
                    bool stillComplete = fig.RequiredParts.All(p =>
                        (p.QuantityCollected + p.QuantityReservedFromBl) >= p.QuantityNeeded);
                    if (!stillComplete)
                    {
                        fig.Status = TrackedMinifigStatus.Waiting;
                        fig.CompletedAt = null;
                    }
                }
            }
        }

        ctx2.ScanEvents.Add(new ScanEvent
        {
            Timestamp = now,
            Type = ScanType.BlInventoryRelease,
            RecognizedId = snap.LotId.ToString(),
            ResultDescription = part != null
                ? $"BL-Reservierung aufgehoben: {descPart} (Lot {snap.LotId}) fuer '{descFig}'"
                : $"BL-Reservierung aufgehoben (Lot {snap.LotId}, ScanEvent {ev.Id} - " +
                  $"verwaiste Reservierung ohne lebende Figur)",
            WasUndone = false
        });

        await ctx2.SaveChangesAsync(ct);
        Log.Information(
            "ReleaseSingleReservation[event]: ScanEvent {EvId} freigegeben - Lot {LotId} -1 -> Reserviert {R}/{T}, PartId {Part}",
            ev.Id, snap.LotId, bLot.ReservedQuantity, bLot.Quantity, snap.TrackedMinifigPartId);
        InventoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ====================================================================
    // v0.1.24-beta.12 Phase 4: Mass-Update-Export
    // ====================================================================

    /// <inheritdoc />
    public async Task<MassUpdateExportResult> GenerateMassUpdateXmlAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // 1) Alle Lots mit offenen Reservierungen sammeln.
        var lotsWithRes = await ctx.BlInventoryLots
            .AsNoTracking()
            .Where(l => l.ReservedQuantity > 0)
            .OrderBy(l => l.LotId)
            .ToListAsync(ct);

        // 2) Alte PendingExports komplett ersetzen (User soll keine
        //    Restwerte aus einem vorherigen Versuch verwalten muessen).
        var oldPending = await ctx.PendingExports.ToListAsync(ct);
        if (oldPending.Count > 0) ctx.PendingExports.RemoveRange(oldPending);

        // 3) XML bauen + neuen Snapshot pro Lot anlegen.
        var doc = new System.Xml.Linq.XElement("INVENTORY");
        int deleted = 0, reduced = 0;
        var now = DateTime.UtcNow;

        foreach (var lot in lotsWithRes)
        {
            bool del = lot.ReservedQuantity >= lot.Quantity;
            int expected = del ? 0 : lot.Quantity - lot.ReservedQuantity;

            if (del)
            {
                doc.Add(new System.Xml.Linq.XElement("ITEM",
                    new System.Xml.Linq.XElement("LOTID", lot.LotId),
                    new System.Xml.Linq.XElement("DELETE")));
                deleted++;
            }
            else
            {
                // QTY ist negativ (Reduktion) - BL-Mass-Update interpretiert
                // negative QTY als "um X reduzieren".
                doc.Add(new System.Xml.Linq.XElement("ITEM",
                    new System.Xml.Linq.XElement("LOTID", lot.LotId),
                    new System.Xml.Linq.XElement("QTY", $"-{lot.ReservedQuantity}")));
                reduced++;
            }

            ctx.PendingExports.Add(new PendingExport
            {
                LotId = lot.LotId,
                ExpectedQuantity = expected,
                ShouldDelete = del,
                ExportedAt = now
            });
        }

        await ctx.SaveChangesAsync(ct);

        var xml = new System.Xml.Linq.XDocument(
            new System.Xml.Linq.XDeclaration("1.0", "UTF-8", null),
            doc).ToString();

        Log.Information(
            "Mass-Update XML generiert: {Total} Lots ({Deleted} DELETE, {Reduced} QTY-Reduktion)",
            lotsWithRes.Count, deleted, reduced);

        return new MassUpdateExportResult
        {
            Xml = xml,
            TotalLots = lotsWithRes.Count,
            DeletedLots = deleted,
            ReducedLots = reduced
        };
    }

    /// <inheritdoc />
    public async Task<int> GetPendingExportCountAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        return await ctx.PendingExports.CountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<MassUpdateVerifyResult> VerifyExportAsync(CancellationToken ct = default)
    {
        // 1) Frisches BL-Inventar holen. Auth/Rate-Limit-Errors propagieren -
        //    UI-Layer macht den Toast.
        var freshLots = await _bricklinkClient.GetInventoryAsync(ct);
        var freshByLot = freshLots.ToDictionary(l => l.LotId, l => l);

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var pending = await ctx.PendingExports.ToListAsync(ct);
        if (pending.Count == 0)
        {
            return new MassUpdateVerifyResult { SuccessCount = 0, FailedCount = 0, RemainingCount = 0 };
        }

        // 2) Pro PendingExport pruefen + ggf. Reserviert→Collected konvertieren.
        var details = new List<MassUpdateVerifyDetail>();
        int success = 0, failed = 0;
        var now = DateTime.UtcNow;
        bool allSuccess = true;

        // ScanEvent-Lookup einmalig: alle offenen Reservierungen materialisieren,
        // damit wir pro Lot nicht N-mal in der DB lesen muessen.
        var openReservations = await ctx.ScanEvents
            .Where(e => e.Type == ScanType.BlInventoryReservation && !e.WasUndone)
            .ToListAsync(ct);
        var parsedByLot = new Dictionary<int, List<(ScanEvent Ev, UndoSnapshotBlReservation Snap)>>();
        foreach (var ev in openReservations)
        {
            if (string.IsNullOrEmpty(ev.UndoData)) continue;
            try
            {
                var snap = System.Text.Json.JsonSerializer
                    .Deserialize<UndoSnapshotBlReservation>(ev.UndoData);
                if (snap == null) continue;
                if (!parsedByLot.TryGetValue(snap.LotId, out var list))
                {
                    list = new();
                    parsedByLot[snap.LotId] = list;
                }
                list.Add((ev, snap));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "VerifyExport: ScanEvent {Id} UndoData unparsable", ev.Id);
            }
        }

        foreach (var p in pending)
        {
            bool ok;
            string reason;
            freshByLot.TryGetValue(p.LotId, out var fresh);

            if (p.ShouldDelete)
            {
                if (fresh == null)
                {
                    ok = true;
                    reason = "Lot wurde geloescht (BL).";
                }
                else
                {
                    ok = false;
                    reason = $"Lot existiert noch bei BL (Menge {fresh.Quantity}).";
                }
            }
            else
            {
                if (fresh == null)
                {
                    // Lot komplett weg obwohl wir nur reduzieren wollten -
                    // ungewoehnlich, aber Soll-Zustand "nicht mehr da" trifft
                    // formal nicht zu. Fehler.
                    ok = false;
                    reason = "Lot wurde komplett geloescht, erwartet war Reduktion.";
                }
                else if (fresh.Quantity == p.ExpectedQuantity)
                {
                    ok = true;
                    reason = $"Menge passt ({p.ExpectedQuantity}).";
                }
                else
                {
                    ok = false;
                    reason = $"Menge {fresh.Quantity}, erwartet {p.ExpectedQuantity}.";
                }
            }

            details.Add(new MassUpdateVerifyDetail { LotId = p.LotId, Success = ok, Reason = reason });
            if (!ok) { failed++; allSuccess = false; continue; }

            // Erfolg: Reserviert→Collected konvertieren + Lot.ReservedQuantity=0
            await ConvertReservationsForLotAsync(ctx, p.LotId, parsedByLot, now, ct);

            // PendingExport entfernen
            ctx.PendingExports.Remove(p);
            success++;
        }

        await ctx.SaveChangesAsync(ct);

        var remaining = await ctx.PendingExports.CountAsync(ct);

        Log.Information(
            "Mass-Update Verify: {Success} Erfolg, {Failed} Fehler, {Remaining} verbleibend",
            success, failed, remaining);

        // 3) Wenn ALLE erfolgreich + tatsaechlich was gemacht: frischen Full-Sync
        //    triggern damit der lokale Spiegel und die BL-Realitaet wieder
        //    komplett uebereinstimmen.
        if (allSuccess && success > 0)
        {
            try
            {
                await SyncInventoryAsync(ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Mass-Update Verify: Auto-Sync nach Erfolg fehlgeschlagen");
            }
        }
        else if (success > 0)
        {
            // Teil-Erfolg: trotzdem InventoryChanged feuern damit das UI
            // die Reservierungen aktualisiert (SaveChanges hat ReservedQuantity
            // veraendert).
            InventoryChanged?.Invoke(this, EventArgs.Empty);
        }

        return new MassUpdateVerifyResult
        {
            SuccessCount = success,
            FailedCount = failed,
            RemainingCount = remaining,
            Details = details
        };
    }

    /// <summary>
    /// Konvertiert pro Lot alle offenen BL-Reservierungs-ScanEvents in
    /// Collected-Buchungen. Lot.ReservedQuantity wird auf 0 gesetzt
    /// (Drift-Resteinheit ohne ScanEvent verschwindet damit ebenfalls -
    /// das ist beabsichtigt: nach erfolgreichem BL-Update ist die
    /// Reservierung weg, egal wie sie zustande kam).
    /// </summary>
    private async Task ConvertReservationsForLotAsync(
        UserDataContext ctx,
        int lotId,
        Dictionary<int, List<(ScanEvent Ev, UndoSnapshotBlReservation Snap)>> parsedByLot,
        DateTime now,
        CancellationToken ct)
    {
        var lot = await ctx.BlInventoryLots
            .FirstOrDefaultAsync(l => l.LotId == lotId, ct);
        // Lot kann inzwischen weg sein (Delete im frischen Inventar) - der
        // Sync am Ende raeumt das auf. Hier nur defensiv ueberspringen.
        if (lot != null) lot.ReservedQuantity = 0;

        if (!parsedByLot.TryGetValue(lotId, out var reservations) || reservations.Count == 0)
        {
            // Drift ohne ScanEvent oder gar keine Reservierungen mehr -
            // nichts zu konvertieren.
            return;
        }

        // Pro ScanEvent: Part-Update + Event als WasUndone + Audit-Event.
        // Parts in einem Bulk-Load um N-Queries zu vermeiden.
        var partIds = reservations.Select(r => r.Snap.TrackedMinifigPartId).Distinct().ToList();
        var parts = await ctx.TrackedMinifigParts
            .Where(p => partIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var (ev, snap) in reservations)
        {
            ev.WasUndone = true;
            ev.UndoneAt = now;

            string partDescr = "(verwaist)";
            if (parts.TryGetValue(snap.TrackedMinifigPartId, out var part))
            {
                // Kernoperation: QuantityReservedFromBl in QuantityCollected
                // umwandeln. Beide um 1 verschieben - EffectiveCollected
                // bleibt invariant.
                part.QuantityCollected += 1;
                part.QuantityReservedFromBl = Math.Max(0, part.QuantityReservedFromBl - 1);
                partDescr = part.PartName;
            }

            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = now,
                Type = ScanType.BlReservationConvertedToCollected,
                RecognizedId = lotId.ToString(),
                ResultDescription = part != null
                    ? $"BL-Reservierung physisch uebernommen: {partDescr} (Lot {lotId})"
                    : $"BL-Reservierung physisch uebernommen (Lot {lotId}, ScanEvent {ev.Id}, " +
                      $"Figur nicht mehr existent)",
                WasUndone = false
            });
        }
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
