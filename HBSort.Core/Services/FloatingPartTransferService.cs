using HBSort.Core.Database;
using HBSort.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Default-Implementierung des FloatingPart-Transfer-Service.
///
/// Auswahl-Strategie bei mehreren passenden FloatingParts (gleicher Part-No
/// + Farb-ID in mehreren Faechern): aelteste-zuerst. Sortier-Kriterium ist
/// AddedAt aufsteigend - "first in, first out". Spec sagt "erstes gefundenes
/// Fach reicht"; AddedAt-Order sorgt dafuer dass die Wahl reproduzierbar ist
/// und nicht zufaellig schwankt.
/// </summary>
public class FloatingPartTransferService : IFloatingPartTransferService
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IMinifigPersistenceService _persistence;

    public FloatingPartTransferService(
        IDbContextFactory<UserDataContext> ctxFactory,
        IMinifigPersistenceService persistence)
    {
        _ctxFactory = ctxFactory;
        _persistence = persistence;
    }

    public async Task<FloatingPartMatch?> FindFirstMatchAsync(
        string blPartNo, int blColorId, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var fp = await ctx.FloatingParts.AsNoTracking()
            .Include(p => p.StorageBin)
            .Where(p => p.PartNumber == blPartNo
                     && p.ColorId == blColorId
                     && p.Quantity > 0)
            .OrderBy(p => p.AddedAt)
            .FirstOrDefaultAsync(ct);

        if (fp == null) return null;

        return new FloatingPartMatch(
            FloatingPartId: fp.Id,
            StorageBinId: fp.StorageBinId,
            StorageBinLabel: fp.StorageBin?.Label ?? "?",
            QuantityAvailable: fp.Quantity);
    }

    public async Task<FloatingPartTransferResult> TransferOneAsync(
        string blPartNo, int blColorId,
        string targetMinifigDescription,
        CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // 1) Den ersten passenden FloatingPart innerhalb der DbContext-Tracking-
        //    Zone laden (kein AsNoTracking, weil wir gleich aendern wollen).
        var fp = await ctx.FloatingParts
            .Include(p => p.StorageBin)
            .Where(p => p.PartNumber == blPartNo
                     && p.ColorId == blColorId
                     && p.Quantity > 0)
            .OrderBy(p => p.AddedAt)
            .FirstOrDefaultAsync(ct);

        if (fp == null)
        {
            // Race-Condition oder UI-Stand veraltet: kein Match mehr.
            Log.Warning(
                "FloatingPartTransfer: kein Match fuer {PartNo}/{ColorId} - " +
                "vermutlich Race-Condition.",
                blPartNo, blColorId);
            return new FloatingPartTransferResult(
                Success: false, SourceBinLabel: null,
                BinFreedAfterTransfer: false,
                ErrorMessage: "Teil nicht mehr im Fach verfuegbar.");
        }

        var sourceBinLabel = fp.StorageBin?.Label ?? "?";
        var sourceBinId = fp.StorageBinId;

        // 2) Quantity reduzieren oder Eintrag loeschen.
        fp.Quantity -= 1;
        if (fp.Quantity <= 0)
        {
            ctx.FloatingParts.Remove(fp);
        }

        // 3) Audit-Trail: ScanEvent.
        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.FloatingPartTransfer,
            RecognizedId = blPartNo,
            ResultDescription =
                $"Teil aus '{sourceBinLabel}' uebernommen -> {targetMinifigDescription} " +
                $"({blPartNo}, Farbe {blColorId})",
            WasUndone = false
        });

        await ctx.SaveChangesAsync(ct);

        // 4) Bin-Freigabe-Check: ist das Fach jetzt komplett leer?
        //    "Leer" = keine FloatingParts UND keine wartenden Minifigs mehr.
        //    Bins mit COMPLETE/DISMANTLED-Minifigs zaehlen nicht als belegt
        //    (analog zu IStorageBinService.GetFreeAsync).
        bool binFreed = false;
        var bin = await ctx.StorageBins
            .Include(b => b.FloatingParts)
            .Include(b => b.TrackedMinifigs)
            .FirstOrDefaultAsync(b => b.Id == sourceBinId, ct);

        if (bin != null
            && bin.FreedAt == null
            && !bin.FloatingParts.Any()
            && !bin.TrackedMinifigs.Any(m => m.Status == TrackedMinifigStatus.Waiting))
        {
            bin.FreedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync(ct);
            binFreed = true;
            Log.Information(
                "FloatingPartTransfer: Fach '{Label}' freigegeben (war nach Transfer leer).",
                bin.Label);
        }

        Log.Information(
            "FloatingPartTransfer: 1x {PartNo}/{ColorId} aus '{Bin}' -> {Target}, BinFreed={BinFreed}",
            blPartNo, blColorId, sourceBinLabel, targetMinifigDescription, binFreed);

        // 5) DataChanged feuern damit Live-VMs (BuildSuggestions, BinOverview etc.)
        //    refreshen.
        _persistence.RaiseDataChanged();

        return new FloatingPartTransferResult(
            Success: true,
            SourceBinLabel: sourceBinLabel,
            BinFreedAfterTransfer: binFreed,
            ErrorMessage: null);
    }
}
