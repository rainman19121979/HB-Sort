using LegoMinifigSorter.Core.Database;
using LegoMinifigSorter.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace LegoMinifigSorter.Core.Services;

/// <summary>
/// Default-Implementierung des PartLookupService. Nutzt
/// <see cref="IDbContextFactory{UserDataContext}"/> fuer frische DbContexts pro
/// Operation, plus den BlCatalogService fuer Stammdaten- und Subset-Lookups.
/// </summary>
public class PartLookupService : IPartLookupService
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IBlCatalogService _catalog;
    private readonly IMinifigPersistenceService _persistence;

    public PartLookupService(
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCatalogService catalog,
        IMinifigPersistenceService persistence)
    {
        _ctxFactory = ctxFactory;
        _catalog = catalog;
        _persistence = persistence;
    }

    public async Task<PartLookupResult> LookupPartAsync(string blPartNo, int blColorId, CancellationToken ct = default)
    {
        // Stammdaten aus BL-Cache (kein API-Call wenn schon vorhanden).
        var item = await _catalog.GetPartDetailsAsync(blPartNo, ct);
        var partName = item?.Name ?? string.Empty;

        var allColors = await _catalog.GetAllColorsAsync(ct);
        var color = allColors.FirstOrDefault(c => c.ColorId == blColorId);
        var colorName = color?.Name ?? $"Color {blColorId}";
        var colorRgb = color?.Rgb;

        // Reverse-Match in userdata.db
        await using var dbCtx = await _ctxFactory.CreateDbContextAsync(ct);
        var query = dbCtx.TrackedMinifigParts.AsNoTracking()
            .Where(p => p.PartNumber == blPartNo
                     && p.ColorId == blColorId
                     && p.QuantityCollected < p.QuantityNeeded
                     && p.TrackedMinifig.Status == TrackedMinifigStatus.Waiting)
            .Include(p => p.TrackedMinifig)
                .ThenInclude(m => m.StorageBin)
            // Sortierung: zuerst Figuren denen am meisten zum Komplett fehlt,
            // dann nach Bin-Label.
            .OrderByDescending(p => p.QuantityNeeded - p.QuantityCollected)
            .ThenBy(p => p.TrackedMinifig.StorageBin!.Label);

        var matches = new List<WaitingMinifigMatch>();
        foreach (var p in await query.ToListAsync(ct))
        {
            matches.Add(new WaitingMinifigMatch(
                TrackedMinifigPartId: p.Id,
                TrackedMinifigId: p.TrackedMinifigId,
                BlMinifigId: p.TrackedMinifig.BricklinkId ?? p.TrackedMinifig.FigNum,
                MinifigName: p.TrackedMinifig.Name,
                MinifigImageUrl: p.TrackedMinifig.LocalImagePath ?? p.TrackedMinifig.ImageUrl,
                StorageBinLabel: p.TrackedMinifig.StorageBin?.Label,
                StorageBinId: p.TrackedMinifig.StorageBinId ?? 0,
                QuantityNeeded: p.QuantityNeeded,
                QuantityCollected: p.QuantityCollected,
                IsAlternate: false /* TrackedMinifigPart kennt das nicht; zukuenftig mappen */));
        }

        Log.Debug("PartLookup BL:{No}/C:{C} -> {Count} Treffer in wartenden Figuren",
            blPartNo, blColorId, matches.Count);

        return new PartLookupResult(blPartNo, blColorId, partName, colorName, colorRgb, matches);
    }

    public async Task<bool> AssignPartToMinifigAsync(int trackedMinifigPartId, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var part = await ctx.TrackedMinifigParts
            .Include(p => p.TrackedMinifig)
                .ThenInclude(m => m.RequiredParts)
            .FirstOrDefaultAsync(p => p.Id == trackedMinifigPartId, ct);
        if (part == null) return false;

        if (part.QuantityCollected >= part.QuantityNeeded)
        {
            // Schon komplett; nichts zu tun.
            return false;
        }

        part.QuantityCollected++;
        bool minifigCompleted = false;

        var allComplete = part.TrackedMinifig.RequiredParts.All(p =>
            (p.Id == part.Id ? part.QuantityCollected : p.QuantityCollected) >= p.QuantityNeeded);

        if (allComplete && part.TrackedMinifig.Status != TrackedMinifigStatus.Complete)
        {
            part.TrackedMinifig.Status = TrackedMinifigStatus.Complete;
            part.TrackedMinifig.CompletedAt = DateTime.UtcNow;
            minifigCompleted = true;
        }

        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.PartScan,
            RecognizedId = part.PartNumber,
            ResultDescription = minifigCompleted
                ? $"Teil {part.PartNumber}/{part.ColorId} -> Figur '{part.TrackedMinifig.Name}' komplett"
                : $"Teil {part.PartNumber}/{part.ColorId} -> Figur '{part.TrackedMinifig.Name}' ({part.QuantityCollected}/{part.QuantityNeeded})",
            WasUndone = false
        });

        await ctx.SaveChangesAsync(ct);
        _persistence.RaiseDataChanged();

        Log.Information("Part {Part}/{Color} zu Minifig '{Name}' zugeordnet ({Q}/{N}){Done}",
            part.PartNumber, part.ColorId, part.TrackedMinifig.Name,
            part.QuantityCollected, part.QuantityNeeded,
            minifigCompleted ? " => COMPLETE" : "");

        return minifigCompleted;
    }

    public async Task<bool> UnassignPartFromMinifigAsync(int trackedMinifigPartId, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var part = await ctx.TrackedMinifigParts
            .Include(p => p.TrackedMinifig)
            .FirstOrDefaultAsync(p => p.Id == trackedMinifigPartId, ct);
        if (part == null) return false;

        if (part.QuantityCollected == 0) return false;

        part.QuantityCollected = 0;

        // Wenn die Figur vorher COMPLETE war, zurueck auf WAITING.
        if (part.TrackedMinifig.Status == TrackedMinifigStatus.Complete)
        {
            part.TrackedMinifig.Status = TrackedMinifigStatus.Waiting;
            part.TrackedMinifig.CompletedAt = null;
        }

        await ctx.SaveChangesAsync(ct);
        _persistence.RaiseDataChanged();

        Log.Information("Part-Zuordnung entfernt: {Part}/{Color} aus Figur '{Name}'",
            part.PartNumber, part.ColorId, part.TrackedMinifig.Name);
        return true;
    }

    public async Task<FloatingPart> AddPartToFloatingAsync(
        string blPartNo, int blColorId, string partName, string colorName,
        int quantity, int storageBinId, CancellationToken ct = default)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity muss > 0 sein", nameof(quantity));

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Bin existiert?
        var bin = await ctx.StorageBins.FirstOrDefaultAsync(b => b.Id == storageBinId, ct)
            ?? throw new InvalidOperationException($"Lagerfach {storageBinId} existiert nicht.");

        // Existierender FloatingPart in selben Bin?
        var existing = await ctx.FloatingParts
            .FirstOrDefaultAsync(fp => fp.PartNumber == blPartNo
                                    && fp.ColorId == blColorId
                                    && fp.StorageBinId == storageBinId, ct);

        FloatingPart result;
        if (existing != null)
        {
            existing.Quantity += quantity;
            result = existing;
        }
        else
        {
            result = new FloatingPart
            {
                PartNumber = blPartNo,
                ColorId = blColorId,
                BricklinkColorId = blColorId,
                ColorName = colorName,
                PartName = partName,
                Quantity = quantity,
                StorageBinId = storageBinId,
                AddedAt = DateTime.UtcNow
            };
            ctx.FloatingParts.Add(result);
        }

        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.PartScan,
            RecognizedId = blPartNo,
            ResultDescription = $"Einzelteil {blPartNo}/{blColorId} in '{bin.Label}' gelagert (+{quantity})",
            WasUndone = false
        });

        await ctx.SaveChangesAsync(ct);
        _persistence.RaiseDataChanged();

        Log.Information("Floating-Part angelegt: {Part}/{Color} x{Qty} in Bin '{Bin}'",
            blPartNo, blColorId, quantity, bin.Label);
        return result;
    }

    public async Task<List<FloatingPartLocation>> FindFloatingLocationsAsync(
        string blPartNo, int blColorId, CancellationToken ct = default)
    {
        // Server-side filtern (Where + Include), Group/Sum/Sort dann client-side.
        // EF Core 8 / SQLite kann die Group-By-Sum-Anonymous-Projection nicht in SQL
        // uebersetzen, deshalb der Split. Da die Liste pro (Part, Color) klein ist
        // (typischerweise <10 Eintraege), ist Client-side Grouping unproblematisch.
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var rawParts = await ctx.FloatingParts
            .AsNoTracking()
            .Where(fp => fp.PartNumber == blPartNo && fp.ColorId == blColorId)
            .Include(fp => fp.StorageBin)
            .ToListAsync(ct);

        var locations = rawParts
            .Where(fp => fp.StorageBin != null)
            .GroupBy(fp => new { fp.StorageBinId, BinLabel = fp.StorageBin!.Label })
            .Select(g => new FloatingPartLocation(
                g.Key.StorageBinId,
                g.Key.BinLabel,
                g.Sum(fp => fp.Quantity)))
            .OrderByDescending(loc => loc.TotalQuantity)
            .ToList();

        Log.Debug("FindFloatingLocationsAsync({Part}/{Color}): {RawCount} rohe FloatingParts -> {Locations} Locations",
            blPartNo, blColorId, rawParts.Count, locations.Count);

        return locations;
    }

    public async Task<bool> DeleteFloatingPartAsync(int floatingPartId, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var fp = await ctx.FloatingParts.FirstOrDefaultAsync(p => p.Id == floatingPartId, ct);
        if (fp == null) return false;

        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.PartScan,
            RecognizedId = fp.PartNumber,
            ResultDescription = $"Einzelteil '{fp.PartName}' (BL:{fp.PartNumber}/{fp.ColorId}) x{fp.Quantity} geloescht",
            WasUndone = false
        });

        ctx.FloatingParts.Remove(fp);
        await ctx.SaveChangesAsync(ct);
        Log.Information("FloatingPart Id={Id} ({Part}/{Color} x{Qty}) geloescht",
            fp.Id, fp.PartNumber, fp.ColorId, fp.Quantity);
        _persistence.RaiseDataChanged();
        return true;
    }

    public async Task<TrackedMinifig> CollectMinifigFromSupersetAsync(
        string blMinifigId, int storageBinId,
        string triggerPartNo, int triggerColorId, int triggerQuantity,
        CancellationToken ct = default)
    {
        // 1) Sicherstellen dass die volle Subsets-Liste der Minifig im Cache ist.
        await _catalog.EnsureFullSubsetsAsync(blMinifigId, ct);

        // 2) Stammdaten holen (Name etc.)
        var item = await _catalog.GetMinifigDetailsAsync(blMinifigId, ct)
            ?? throw new InvalidOperationException($"Minifig '{blMinifigId}' nicht in BL gefunden.");

        // 3) RequiredParts aus den Subsets bauen (mit Color-Name aus bl_colors)
        var subsets = await _catalog.GetMinifigPartsAsync(blMinifigId, ct);
        var allColors = await _catalog.GetAllColorsAsync(ct);
        var colorMap = allColors.ToDictionary(c => c.ColorId);

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var bin = await ctx.StorageBins.FirstOrDefaultAsync(b => b.Id == storageBinId, ct)
            ?? throw new InvalidOperationException($"Lagerfach {storageBinId} existiert nicht.");

        var minifig = new TrackedMinifig
        {
            FigNum = blMinifigId,
            BricklinkId = blMinifigId,
            Name = item.Name,
            ImageUrl = item.ImageUrl,
            CreatedAt = DateTime.UtcNow,
            Status = TrackedMinifigStatus.Waiting,
            StorageBinId = storageBinId,
            RequiredParts = subsets.Select(s =>
            {
                colorMap.TryGetValue(s.ColorId, out var color);
                return new TrackedMinifigPart
                {
                    PartNumber = s.ItemNo,
                    ColorId = s.ColorId,
                    BricklinkColorId = s.ColorId,
                    PartName = string.Empty, // wird noch nicht gecached pro Item-Name; UI laed live
                    ColorName = color?.Name ?? $"Color {s.ColorId}",
                    QuantityNeeded = s.Quantity,
                    QuantityCollected = 0
                };
            }).ToList()
        };

        // 4) Trigger-Teil sofort auf collected setzen (cap auf QuantityNeeded).
        var trigger = minifig.RequiredParts.FirstOrDefault(p =>
            p.PartNumber == triggerPartNo && p.ColorId == triggerColorId);
        if (trigger != null)
        {
            trigger.QuantityCollected = Math.Min(triggerQuantity, trigger.QuantityNeeded);
        }

        ctx.TrackedMinifigs.Add(minifig);
        await ctx.SaveChangesAsync(ct);

        // 5) Reverse-Match Floating-Parts (wie in MinifigPersistenceService).
        var reverseMatched = 0;
        foreach (var required in minifig.RequiredParts)
        {
            var stillNeeded = required.QuantityNeeded - required.QuantityCollected;
            if (stillNeeded <= 0) continue;

            var candidates = await ctx.FloatingParts
                .Where(fp => fp.PartNumber == required.PartNumber
                          && fp.ColorId == required.ColorId)
                .OrderBy(fp => fp.AddedAt)
                .ToListAsync(ct);

            foreach (var fp in candidates)
            {
                if (stillNeeded <= 0) break;
                var take = Math.Min(stillNeeded, fp.Quantity);
                required.QuantityCollected += take;
                fp.Quantity -= take;
                stillNeeded -= take;
                reverseMatched += take;
                if (fp.Quantity <= 0) ctx.FloatingParts.Remove(fp);
            }
        }

        // Falls durch Reverse-Match komplett -> Status=Complete
        var isComplete = minifig.RequiredParts.All(p => p.QuantityCollected >= p.QuantityNeeded)
                         && minifig.RequiredParts.Count > 0;
        if (isComplete)
        {
            minifig.Status = TrackedMinifigStatus.Complete;
            minifig.CompletedAt = DateTime.UtcNow;
        }

        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.PartScan,
            RecognizedId = triggerPartNo,
            ResultDescription = isComplete
                ? $"Neue Figur '{minifig.Name}' direkt komplett (Reverse-Match)"
                : $"Neue Figur '{minifig.Name}' angelegt in '{bin.Label}', Trigger-Teil {triggerPartNo}",
            WasUndone = false
        });

        await ctx.SaveChangesAsync(ct);
        _persistence.RaiseDataChanged();

        Log.Information("CollectMinifig: '{Name}' (BL:{Bl}) angelegt in '{Bin}', Trigger {Tp}/{Tc} x{Tq}, ReverseMatch={Rm}{Done}",
            minifig.Name, blMinifigId, bin.Label, triggerPartNo, triggerColorId, triggerQuantity,
            reverseMatched, isComplete ? " => COMPLETE" : "");

        return minifig;
    }
}
