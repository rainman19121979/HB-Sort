using System.Text.Json;
using HBSort.Core.Database;
using HBSort.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// UX X.28 (2026-05-08): Daten-Heal beim App-Start.
/// Korrigiert Bug-B-Folgen (Bins die belegt sind aber FreedAt!=null haben).
/// Bug-A-Folgen (Complete-Figuren ohne StorageBinId) koennen nicht automatisch
/// geheilt werden - dafuer gibt es nur eine Log-Warnung.
/// </summary>
public class DataHealService : IDataHealService
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IStorageBinService? _binService;

    public DataHealService(
        IDbContextFactory<UserDataContext> ctxFactory,
        IStorageBinService? binService = null)
    {
        _ctxFactory = ctxFactory;
        _binService = binService;
    }

    public async Task<DataHealResult> HealAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // ============================================================
        // Bug-A-Folgen (UX X.30 / v0.1.17): Auto-Heal-Versuch.
        // ============================================================
        // Komplette Figuren ohne StorageBinId. Wir versuchen die letzte
        // bekannte Bin-Zuordnung aus den ScanEvents zu rekonstruieren -
        // konservativ: nur wenn EINDEUTIG. Bei Mehrdeutigkeit lieber
        // Warnung wie bisher.
        var orphans = await ctx.TrackedMinifigs
            .Where(m => m.Status == TrackedMinifigStatus.Complete && m.StorageBinId == null)
            .ToListAsync(ct);

        var restoredCount = 0;
        var unrecoveredCount = 0;

        foreach (var orphan in orphans)
        {
            ct.ThrowIfCancellationRequested();
            var lastKnownBin = await TryFindLastKnownBinAsync(ctx, orphan, ct);
            if (lastKnownBin.HasValue)
            {
                orphan.StorageBinId = lastKnownBin.Value;
                restoredCount++;
                Log.Information(
                    "DataHeal: Figur '{Name}' (BL:{BL}) auf letztes bekanntes Lagerfach Id={Bin} wiederhergestellt",
                    orphan.Name, orphan.BricklinkId, lastKnownBin.Value);
            }
            else
            {
                unrecoveredCount++;
            }
        }

        if (unrecoveredCount > 0)
        {
            Log.Warning(
                "DataHeal: {Count} komplette Figuren haben kein Lagerfach zugeordnet und konnten nicht aus der " +
                "Verlaufs-Historie rekonstruiert werden. Bitte manuell ein Fach zuweisen via Temporäres Inventar -> Details.",
                unrecoveredCount);
        }

        // ============================================================
        // Bug-B-Folgen: Bins die belegt sind aber FreedAt!=null haben.
        // ============================================================
        var binsToHeal = await ctx.StorageBins
            .Where(b => b.FreedAt != null
                && (ctx.TrackedMinifigs.Any(m => m.StorageBinId == b.Id)
                    || ctx.FloatingParts.Any(f => f.StorageBinId == b.Id)))
            .ToListAsync(ct);

        foreach (var bin in binsToHeal)
        {
            Log.Information(
                "DataHeal: Bin '{Label}' war fehlerhaft als frei markiert (FreedAt={FreedAt}), wird auf belegt korrigiert",
                bin.Label, bin.FreedAt);
            bin.FreedAt = null;
        }

        if (binsToHeal.Count > 0 || restoredCount > 0)
            await ctx.SaveChangesAsync(ct);

        // v0.1.23: Bin.Kind fuer geheilte Bins + alle Bins die wartende
        // Figuren aus der Orphan-Heilung dazubekamen neu berechnen.
        if (_binService != null)
        {
            var affectedBins = binsToHeal.Select(b => b.Id)
                .Concat(orphans.Where(o => o.StorageBinId.HasValue).Select(o => o.StorageBinId!.Value))
                .Distinct()
                .ToList();
            foreach (var binId in affectedBins)
            {
                try { await _binService.RecalculateKindAsync(binId, ct); }
                catch (Exception ex) { Log.Warning(ex, "RecalculateKindAsync({BinId}) im DataHeal geworfen", binId); }
            }
        }

        return new DataHealResult(
            RestoredBinAssignments: restoredCount,
            ResetFreedAtCount: binsToHeal.Count);
    }

    /// <summary>
    /// UX X.30 (v0.1.17): findet das letzte bekannte Lagerfach einer Figur
    /// aus der ScanEvent-Historie. Konservativ:
    /// - Sucht den neuesten Move-Event (Type=Move, RecognizedId=BL-Id) mit
    ///   einem OldStorageBinId in UndoSnapshotMove.
    /// - Falls kein Move: sucht den neuesten Delete-Event (Type=Delete,
    ///   RecognizedId=BL-Id) mit StorageBinId in UndoSnapshotMinifigDelete.
    /// - Pruefen dass der gefundene Bin noch existiert.
    /// - Bei Mehrdeutigkeit (z.B. unterschiedliche Bins ueber die Zeit):
    ///   nimmt den NEUESTEN Eintrag - das ist die letzte Position vor
    ///   dem Verlust.
    /// Liefert null wenn nichts rekonstruierbar.
    /// </summary>
    private static async Task<int?> TryFindLastKnownBinAsync(
        UserDataContext ctx, TrackedMinifig orphan, CancellationToken ct)
    {
        var blId = orphan.BricklinkId;
        if (string.IsNullOrEmpty(blId)) return null;

        // Move-Events mit UndoData (haben OldStorageBinId UND NewStorageBinId)
        // sind die beste Quelle - sie zeigen wohin die Figur zuletzt gebracht
        // wurde. Neueste zuerst.
        var moveEvents = await ctx.ScanEvents.AsNoTracking()
            .Where(e => e.Type == ScanType.Move
                     && e.RecognizedId == blId
                     && e.UndoData != null)
            .OrderByDescending(e => e.Timestamp)
            .Take(20) // Performance-Limit
            .ToListAsync(ct);

        foreach (var ev in moveEvents)
        {
            try
            {
                var snap = JsonSerializer.Deserialize<UndoSnapshotMove>(ev.UndoData!);
                if (snap == null || snap.MinifigId != orphan.Id) continue;
                // NewStorageBinId ist das Ziel des Moves - dort lag die Figur
                // nach dem Move. Wenn der Move via WasUndone=true rueckgaengig
                // gemacht wurde, ist sie wieder am Old-Ort.
                int? candidate = ev.WasUndone ? snap.OldStorageBinId : snap.NewStorageBinId;
                if (candidate.HasValue
                    && await ctx.StorageBins.AnyAsync(b => b.Id == candidate.Value, ct))
                {
                    return candidate.Value;
                }
            }
            catch (JsonException) { /* invalides Snapshot, naechstes versuchen */ }
        }

        // Fallback: Delete-Events mit Snapshot des Stands vor dem Loeschen.
        // Falls die Figur wiederhergestellt wurde, koennen wir aus dem
        // Snapshot StorageBinId entnehmen.
        var deleteEvents = await ctx.ScanEvents.AsNoTracking()
            .Where(e => e.Type == ScanType.Delete
                     && e.RecognizedId == blId
                     && e.UndoData != null)
            .OrderByDescending(e => e.Timestamp)
            .Take(20)
            .ToListAsync(ct);

        foreach (var ev in deleteEvents)
        {
            try
            {
                var snap = JsonSerializer.Deserialize<UndoSnapshotMinifigDelete>(ev.UndoData!);
                if (snap == null) continue;
                if (snap.StorageBinId.HasValue
                    && await ctx.StorageBins.AnyAsync(b => b.Id == snap.StorageBinId.Value, ct))
                {
                    return snap.StorageBinId.Value;
                }
            }
            catch (JsonException) { }
        }

        return null;
    }
}
