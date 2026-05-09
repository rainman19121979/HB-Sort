using System.Text.Json;
using HBSort.Core.Database;
using HBSort.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// UX X.29 (v0.1.16): Implementation des Undo-Service.
///
/// Kontrakte:
/// - WasUndone+UndoneAt blockiert ein zweites Undo desselben Events.
/// - Pro erfolgreichem Undo wird ein Audit-ScanEvent vom Typ UndoApplied
///   geschrieben (Description: "Undo: ..."). Dieser Audit-Eintrag ist
///   selbst nicht undobar.
/// - Bei Fehler waehrend Undo: keine partiellen Aenderungen
///   (alles in einer SaveChangesAsync-Transaktion).
/// </summary>
public class UndoService : IUndoService
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IMinifigPersistenceService _persistence;

    public UndoService(
        IDbContextFactory<UserDataContext> ctxFactory,
        IMinifigPersistenceService persistence)
    {
        _ctxFactory = ctxFactory;
        _persistence = persistence;
    }

    public async Task<List<UndoableAction>> GetUndoableActionsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var events = await ctx.ScanEvents.AsNoTracking()
            .Where(e => !e.WasUndone && e.UndoData != null
                && e.Type != ScanType.UndoApplied)
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .ToListAsync(ct);
        return events
            .Where(e => IsUndoableType(e.Type))
            .Select(e => new UndoableAction(e.Id, e.Timestamp, e.Type, e.ResultDescription))
            .ToList();
    }

    public async Task<UndoableAction?> GetLastUndoableAsync(CancellationToken ct = default)
    {
        var list = await GetUndoableActionsAsync(limit: 1, ct);
        return list.FirstOrDefault();
    }

    public async Task<List<HistoryEntry>> GetHistoryAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var events = await ctx.ScanEvents.AsNoTracking()
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .ToListAsync(ct);
        return events.Select(e => new HistoryEntry(
            e.Id,
            e.Timestamp,
            e.Type,
            e.ResultDescription,
            CanUndo: !e.WasUndone
                     && IsUndoableType(e.Type)
                     && e.UndoData != null,
            WasUndone: e.WasUndone,
            UndoneAt: e.UndoneAt
        )).ToList();
    }

    public async Task<UndoResult> UndoAsync(int scanEventId, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var ev = await ctx.ScanEvents.FirstOrDefaultAsync(e => e.Id == scanEventId, ct);
        if (ev == null) return UndoResult.Fail("Aktion nicht gefunden.");
        if (ev.WasUndone) return UndoResult.Fail("Aktion wurde bereits rueckgaengig gemacht.");
        if (!IsUndoableType(ev.Type)) return UndoResult.Fail("Aktion ist nicht rueckgaengigmachbar.");
        if (string.IsNullOrEmpty(ev.UndoData)) return UndoResult.Fail("Fuer diese Aktion liegen keine Undo-Daten vor.");

        try
        {
            var auditDescription = ev.Type switch
            {
                ScanType.Delete => await UndoDeleteAsync(ctx, ev, ct),
                ScanType.Move => await UndoMoveAsync(ctx, ev, ct),
                ScanType.Complete => await UndoCompleteAsync(ctx, ev, ct),
                ScanType.BinFreed => await UndoBinFreedAsync(ctx, ev, ct),
                ScanType.BinCreated => await UndoBinCreatedAsync(ctx, ev, ct),
                _ => null
            };
            if (auditDescription == null)
                return UndoResult.Fail("Undo fuer diesen Aktions-Typ ist nicht implementiert.");

            ev.WasUndone = true;
            ev.UndoneAt = DateTime.UtcNow;

            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = DateTime.UtcNow,
                Type = ScanType.UndoApplied,
                ResultDescription = "Rueckgaengig: " + auditDescription,
                WasUndone = false
            });

            await ctx.SaveChangesAsync(ct);

            // DataChanged feuern damit Live-VMs (Lagerliste, BuildSuggestions, ...)
            // refreshen.
            _persistence.RaiseDataChanged();

            Log.Information("Undo erfolgreich: ScanEvent Id={Id} ({Type}) - {Desc}",
                ev.Id, ev.Type, ev.ResultDescription);
            return UndoResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Undo fehlgeschlagen fuer ScanEvent Id={Id}", scanEventId);
            return UndoResult.Fail("Fehler beim Rueckgaengigmachen: " + ex.Message);
        }
    }

    private static bool IsUndoableType(ScanType t) => t switch
    {
        ScanType.Delete => true,
        ScanType.Move => true,
        ScanType.Complete => true,
        ScanType.BinFreed => true,
        ScanType.BinCreated => true,
        _ => false
    };

    // ====================================================================
    // Per-Type Undo-Logik
    // ====================================================================

    private static async Task<string?> UndoDeleteAsync(UserDataContext ctx, ScanEvent ev, CancellationToken ct)
    {
        // UndoData kann ein Minifig-Snapshot oder ein FloatingPart-Snapshot sein.
        // Wir versuchen erst Minifig (haeufiger), dann FloatingPart.
        var minifigSnap = TryDeserialize<UndoSnapshotMinifigDelete>(ev.UndoData);
        if (minifigSnap != null && !string.IsNullOrEmpty(minifigSnap.BricklinkId))
        {
            // Pruefen ob die ID bereits wieder existiert (User hat z.B. neu angelegt).
            var conflict = await ctx.TrackedMinifigs
                .AnyAsync(m => m.Id == minifigSnap.OriginalMinifigId, ct);
            if (conflict)
                throw new InvalidOperationException(
                    "Eine Figur mit der urspruenglichen ID existiert bereits.");

            // Ziel-Bin existiert noch? Falls geloescht, wird StorageBinId=null
            // gesetzt - User kann manuell neu zuweisen.
            int? binId = null;
            if (minifigSnap.StorageBinId.HasValue)
            {
                var binExists = await ctx.StorageBins
                    .AnyAsync(b => b.Id == minifigSnap.StorageBinId.Value, ct);
                binId = binExists ? minifigSnap.StorageBinId.Value : null;
            }

            var newMinifig = new TrackedMinifig
            {
                FigNum = minifigSnap.FigNum,
                BricklinkId = minifigSnap.BricklinkId,
                Name = minifigSnap.Name,
                ImageUrl = minifigSnap.ImageUrl,
                LocalImagePath = minifigSnap.LocalImagePath,
                UserNotes = minifigSnap.UserNotes,
                CreatedAt = minifigSnap.CreatedAt,
                CompletedAt = minifigSnap.CompletedAt,
                Status = ParseStatus(minifigSnap.Status),
                StorageBinId = binId,
                RequiredParts = minifigSnap.RequiredParts.Select(p => new TrackedMinifigPart
                {
                    PartNumber = p.PartNumber,
                    ColorId = p.ColorId,
                    PartName = p.PartName,
                    ColorName = p.ColorName,
                    QuantityNeeded = p.QuantityNeeded,
                    QuantityCollected = p.QuantityCollected
                }).ToList()
            };
            ctx.TrackedMinifigs.Add(newMinifig);
            await Task.CompletedTask;
            return $"Figur '{minifigSnap.Name}' wiederhergestellt";
        }

        var fpSnap = TryDeserialize<UndoSnapshotFloatingDelete>(ev.UndoData);
        if (fpSnap != null && !string.IsNullOrEmpty(fpSnap.PartNumber))
        {
            int? binId = null;
            if (fpSnap.StorageBinId.HasValue)
            {
                var binExists = await ctx.StorageBins
                    .AnyAsync(b => b.Id == fpSnap.StorageBinId.Value, ct);
                binId = binExists ? fpSnap.StorageBinId.Value : null;
                if (binId.HasValue)
                {
                    // Bin reaktivieren falls er als frei markiert war.
                    var bin = await ctx.StorageBins.FirstAsync(b => b.Id == binId.Value, ct);
                    if (bin.FreedAt != null) bin.FreedAt = null;
                }
            }
            ctx.FloatingParts.Add(new FloatingPart
            {
                PartNumber = fpSnap.PartNumber,
                ColorId = fpSnap.ColorId,
                PartName = fpSnap.PartName,
                ColorName = fpSnap.ColorName,
                Quantity = fpSnap.Quantity,
                StorageBinId = binId ?? throw new InvalidOperationException(
                    "Lagerfach des Original-Einzelteils existiert nicht mehr - kein Undo moeglich."),
                AddedAt = fpSnap.AddedAt,
                OriginMinifigId = fpSnap.OriginMinifigId
            });
            return $"Einzelteil '{fpSnap.PartName}' wiederhergestellt";
        }

        throw new InvalidOperationException(
            "UndoData fuer Delete-Event ist weder Minifig- noch FloatingPart-Snapshot.");
    }

    private static async Task<string?> UndoMoveAsync(UserDataContext ctx, ScanEvent ev, CancellationToken ct)
    {
        // UX X.30 (v0.1.17): unterscheidet Minifig-Move (UndoSnapshotMove) von
        // FloatingPart-Move (UndoSnapshotFloatingMove). Beide Records haben
        // unterschiedliche Property-Namen (MinifigId vs FloatingPartId), so
        // dass die JSON-Deserialisierung klar entscheiden kann.

        // Versuch 1: Minifig-Move (haeufigster Pfad).
        var minifigSnap = TryDeserialize<UndoSnapshotMove>(ev.UndoData);
        if (minifigSnap != null && minifigSnap.MinifigId > 0)
        {
            var minifig = await ctx.TrackedMinifigs.FirstOrDefaultAsync(m => m.Id == minifigSnap.MinifigId, ct);
            if (minifig == null)
                throw new InvalidOperationException("Figur existiert nicht mehr - kein Undo moeglich.");

            if (minifigSnap.OldStorageBinId.HasValue)
            {
                var oldBin = await ctx.StorageBins.FirstOrDefaultAsync(b => b.Id == minifigSnap.OldStorageBinId.Value, ct);
                if (oldBin == null)
                    throw new InvalidOperationException(
                        "Das urspruengliche Lagerfach existiert nicht mehr - kein Undo moeglich.");
                if (oldBin.FreedAt != null) oldBin.FreedAt = null;
            }
            minifig.StorageBinId = minifigSnap.OldStorageBinId;
            return $"Figur '{minifig.Name}' zurueck in altes Fach";
        }

        // Versuch 2: FloatingPart-Move (UX X.30 neu).
        var fpSnap = TryDeserialize<UndoSnapshotFloatingMove>(ev.UndoData);
        if (fpSnap != null && fpSnap.FloatingPartId > 0)
        {
            var fp = await ctx.FloatingParts.FirstOrDefaultAsync(p => p.Id == fpSnap.FloatingPartId, ct);
            if (fp == null)
                throw new InvalidOperationException("Einzelteil existiert nicht mehr - kein Undo moeglich.");

            if (fpSnap.OldStorageBinId.HasValue)
            {
                var oldBin = await ctx.StorageBins.FirstOrDefaultAsync(b => b.Id == fpSnap.OldStorageBinId.Value, ct);
                if (oldBin == null)
                    throw new InvalidOperationException(
                        "Das urspruengliche Lagerfach existiert nicht mehr - kein Undo moeglich.");
                if (oldBin.FreedAt != null) oldBin.FreedAt = null;
                fp.StorageBinId = oldBin.Id;
            }
            else
            {
                // FloatingPart hat kein nullable StorageBinId in der DB - falls
                // OldStorageBinId null waere (theoretisch nicht moeglich, weil
                // FloatingPart immer einem Bin gehoert), wuerde der Cast scheitern.
                throw new InvalidOperationException(
                    "Einzelteil hatte kein urspruengliches Lagerfach - inkonsistenter Snapshot.");
            }
            return $"Einzelteil '{fp.PartName}' zurueck in altes Fach";
        }

        throw new InvalidOperationException(
            "Move-Snapshot ist weder Minifig- noch FloatingPart-Variante.");
    }

    private static async Task<string?> UndoCompleteAsync(UserDataContext ctx, ScanEvent ev, CancellationToken ct)
    {
        var snap = TryDeserialize<UndoSnapshotComplete>(ev.UndoData)
            ?? throw new InvalidOperationException("Complete-Snapshot konnte nicht gelesen werden.");

        var part = await ctx.TrackedMinifigParts
            .Include(p => p.TrackedMinifig)
            .FirstOrDefaultAsync(p => p.Id == snap.TrackedMinifigPartId, ct);
        if (part == null)
            throw new InvalidOperationException("Teil existiert nicht mehr - kein Undo moeglich.");

        part.QuantityCollected = snap.OldQuantityCollected;
        if (part.TrackedMinifig != null)
        {
            part.TrackedMinifig.Status = ParseStatus(snap.OldStatus);
            part.TrackedMinifig.CompletedAt = snap.OldCompletedAt;
        }
        return $"Teil-Markierung an '{part.PartName}' rueckgesetzt";
    }

    private static async Task<string?> UndoBinFreedAsync(UserDataContext ctx, ScanEvent ev, CancellationToken ct)
    {
        var snap = TryDeserialize<UndoSnapshotBinFreed>(ev.UndoData)
            ?? throw new InvalidOperationException("BinFreed-Snapshot konnte nicht gelesen werden.");
        var bin = await ctx.StorageBins.FirstOrDefaultAsync(b => b.Id == snap.BinId, ct);
        if (bin == null)
            throw new InvalidOperationException("Lagerfach existiert nicht mehr.");
        bin.FreedAt = null;
        return $"Lagerfach '{bin.Label}' wieder als belegt markiert";
    }

    private static async Task<string?> UndoBinCreatedAsync(UserDataContext ctx, ScanEvent ev, CancellationToken ct)
    {
        var snap = TryDeserialize<UndoSnapshotBinCreated>(ev.UndoData)
            ?? throw new InvalidOperationException("BinCreated-Snapshot konnte nicht gelesen werden.");

        // Nur leere Bins loeschen - mit Inhalt waeren wir destruktiv.
        var bins = await ctx.StorageBins
            .Where(b => snap.BinIds.Contains(b.Id))
            .ToListAsync(ct);

        var deletedLabels = new List<string>();
        var skippedLabels = new List<string>();

        foreach (var bin in bins)
        {
            var hasMinifigs = await ctx.TrackedMinifigs.AnyAsync(m => m.StorageBinId == bin.Id, ct);
            var hasFloatings = await ctx.FloatingParts.AnyAsync(f => f.StorageBinId == bin.Id, ct);
            if (hasMinifigs || hasFloatings)
            {
                skippedLabels.Add(bin.Label);
                continue;
            }
            ctx.StorageBins.Remove(bin);
            deletedLabels.Add(bin.Label);
        }

        if (deletedLabels.Count == 0 && skippedLabels.Count > 0)
            throw new InvalidOperationException(
                "Alle angelegten Lagerfaecher haben inzwischen Inhalt - kein Undo moeglich.");

        var msg = $"{deletedLabels.Count} Lagerfach/-faecher geloescht";
        if (skippedLabels.Count > 0)
            msg += $" ({skippedLabels.Count} uebersprungen, da Inhalt da ist)";
        return msg;
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TrackedMinifigStatus ParseStatus(string s)
    {
        if (Enum.TryParse<TrackedMinifigStatus>(s, ignoreCase: true, out var parsed))
            return parsed;
        return TrackedMinifigStatus.Waiting;
    }
}
