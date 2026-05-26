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
/// <b>ReservedQuantity-Lebenszyklus</b> (Phase 1 legt die Spalte an;
/// schreibender Code kommt erst in Phasen 2-4):
/// </para>
/// <list type="number">
///   <item>
///     <b>Reservieren</b> (Phase 2/3): wenn HBSort ein Teil aus dem BL-
///     Inventar fuer eine wartende Figur einplant, wird
///     <c>ReservedQuantity</c> des passenden Lots um die geplante Menge
///     erhoeht. Verfuegbar zum Verkauf = <c>Quantity - ReservedQuantity</c>.
///   </item>
///   <item>
///     <b>Export</b> (Phase 4): der Export-Dialog erzeugt eine BL-
///     Mass-Update-XML mit den Adjustments (geplante Mengen pro Lot).
///     Nach erfolgreichem Hochladen / Bestaetigen setzt der Dialog
///     <c>ReservedQuantity = 0</c> fuer alle exportierten Lots zurueck.
///   </item>
///   <item>
///     <b>Sync nach Export</b>: der naechste <see cref="SyncInventoryAsync"/>-
///     Lauf holt die bereits reduzierten <c>Quantity</c>-Werte direkt von
///     BL. Da <c>ReservedQuantity=0</c> ist, stimmt der Stand wieder
///     ueberein - kein Doppel-Zaehlen.
///   </item>
/// </list>
/// <para>
/// <b>Snapshot-Replace mit Reservierungs-Erhalt:</b> Vor dem Loeschen
/// wird (LotId, ReservedQuantity) gemerkt; nach dem Insert wird der Wert
/// fuer jedes weiterhin existierende Lot wiederhergestellt. Lots die in
/// der neuen BL-Antwort nicht mehr vorkommen (ausverkauft / geloescht im
/// Store) verlieren ihre Reservierung - das ist Phase-1-akzeptabel.
/// </para>
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
        int restoredReservations = 0;
        foreach (var dto in lots)
        {
            var reserved = reservedByLot.TryGetValue(dto.LotId, out var r) ? r : 0;
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
            "{Restored} Reservierungen erhalten, {Lost} Reservierungen verloren " +
            "(Lots nicht mehr im BL-Store) (UTC {When:O})",
            deletedCount, lots.Count, restoredReservations,
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
}
