using HBSort.Core.Database;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Core.Services;

/// <summary>
/// UX X.34 v0.1.20-beta.2: Default-Implementation des First-Run-Checks.
/// Liest BL-Catalog-Stand via IBlCacheRepository.GetStatsAsync (COUNT
/// gegen bl_items) und Lagerfach-Stand via UserDataContext.StorageBins
/// (COUNT gegen die EF-Tabelle). Beide Calls sind einmalig beim App-
/// Start und kosten praktisch nichts (~1ms).
/// </summary>
public class FirstRunService : IFirstRunService
{
    private readonly IBlCacheRepository _blCache;
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;

    public FirstRunService(
        IBlCacheRepository blCache,
        IDbContextFactory<UserDataContext> ctxFactory)
    {
        _blCache = blCache;
        _ctxFactory = ctxFactory;
    }

    public async Task<FirstRunStatus> CheckStatusAsync(CancellationToken ct = default)
    {
        // 1) BL-Catalog-Stand (bl_items.COUNT > 0).
        var stats = await _blCache.GetStatsAsync(ct);
        var hasCatalog = stats.ItemCount > 0;

        // 2) Lagerfach-Stand. AnyAsync ist schneller als CountAsync weil
        //    SQL die Pruefung mit LIMIT 1 ausfuehren kann.
        bool hasBins;
        await using (var db = await _ctxFactory.CreateDbContextAsync(ct))
        {
            hasBins = await db.StorageBins.AnyAsync(ct);
        }

        return (hasCatalog, hasBins) switch
        {
            (true, true)   => FirstRunStatus.Complete,
            (false, false) => FirstRunStatus.NeedsAll,
            (false, true)  => FirstRunStatus.NeedsCatalog,
            (true, false)  => FirstRunStatus.NeedsBins
        };
    }
}
