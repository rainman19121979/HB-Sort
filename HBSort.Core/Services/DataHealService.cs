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

    public DataHealService(IDbContextFactory<UserDataContext> ctxFactory)
    {
        _ctxFactory = ctxFactory;
    }

    public async Task<DataHealResult> HealAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Bug-A-Folgen: Complete-Figuren ohne StorageBinId. Wir koennen die
        // urspruengliche Fach-Zuordnung nicht mehr rekonstruieren - nur loggen,
        // damit der User es ueber Lagerliste -> Details -> Verschieben manuell
        // korrigieren kann.
        var orphanedComplete = await ctx.TrackedMinifigs
            .Where(m => m.Status == TrackedMinifigStatus.Complete && m.StorageBinId == null)
            .CountAsync(ct);

        if (orphanedComplete > 0)
        {
            Log.Warning(
                "DataHeal: {Count} komplette Figuren haben kein Lagerfach zugeordnet (Bug-A-Folge). " +
                "Bitte manuell ein Fach zuweisen via Lagerliste -> Details.",
                orphanedComplete);
        }

        // Bug-B-Folgen: Bins die belegt sind aber FreedAt!=null haben -> FreedAt zuruecksetzen.
        // "belegt" = mind. eine TrackedMinifig (egal welcher Status) ODER ein FloatingPart liegt drin.
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

        if (binsToHeal.Count > 0)
            await ctx.SaveChangesAsync(ct);

        return new DataHealResult(
            RestoredBinAssignments: 0,
            ResetFreedAtCount: binsToHeal.Count);
    }
}
