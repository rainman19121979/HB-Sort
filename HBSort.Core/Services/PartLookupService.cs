using HBSort.Core.Database;
using HBSort.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

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
    private readonly IPartImageProvider _imageProvider;
    private readonly IBlCacheRepository? _cache;
    private readonly IStorageBinService? _binService;

    public PartLookupService(
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCatalogService catalog,
        IMinifigPersistenceService persistence,
        IPartImageProvider imageProvider,
        IBlCacheRepository? cache = null,
        IStorageBinService? binService = null)
    {
        _ctxFactory = ctxFactory;
        _catalog = catalog;
        _persistence = persistence;
        _imageProvider = imageProvider;
        // v0.1.22-beta.1 Fix 2026-05-12: BlCacheRepository ist optional damit
        // bestehende Test-Konstruktoren (4 Params) weiterhin laufen. Produktiv
        // (App.xaml.cs DI) wird der Cache immer injiziert - die zwei
        // Collect-Pfade unten brauchen GetItemNamesAsync fuer den PartName-
        // Bulk-Lookup. Null-Fall faellt auf das alte Verhalten zurueck.
        _cache = cache;
        // v0.1.23: binService optional fuer Tests. Production reicht den Service
        // durch, damit Bin.Kind nach jeder Schreib-Aktion neu berechnet wird.
        _binService = binService;
    }

    private async Task RecalcBinKindAsync(int? binId, CancellationToken ct)
    {
        if (_binService == null || !binId.HasValue) return;
        try { await _binService.RecalculateKindAsync(binId.Value, ct); }
        catch (Exception ex) { Log.Warning(ex, "RecalculateKindAsync({BinId}) geworfen", binId); }
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

        // Reverse-Match in userdata.db. v0.1.24-beta.8 Phase 3: nur Teile
        // die effektiv (physisch + BL-reserviert) noch nicht gedeckt sind
        // werden gematched. Bereits via BL reservierte Teile verschwinden
        // damit aus dem "noch fehlend"-Set.
        await using var dbCtx = await _ctxFactory.CreateDbContextAsync(ct);
        var query = dbCtx.TrackedMinifigParts.AsNoTracking()
            .Where(p => p.PartNumber == blPartNo
                     && p.ColorId == blColorId
                     && (p.QuantityCollected + p.QuantityReservedFromBl) < p.QuantityNeeded
                     && p.TrackedMinifig.Status == TrackedMinifigStatus.Waiting)
            .Include(p => p.TrackedMinifig)
                .ThenInclude(m => m.StorageBin)
            // Sortierung: zuerst Figuren denen am meisten zum Komplett fehlt,
            // dann nach Bin-Label.
            .OrderByDescending(p => p.QuantityNeeded - (p.QuantityCollected + p.QuantityReservedFromBl))
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

        // Zusaetzlich: Reverse-Match im BL-Catalog-Cache. Filtert die Wartenden raus,
        // damit der User nicht doppelt sieht was schon in der oberen Liste steht.
        var blCandidates = await _catalog.FindMinifigsContainingPartAsync(blPartNo, blColorId, ct);
        var waitingBlIds = matches.Select(m => m.BlMinifigId).ToHashSet();
        var blMatches = blCandidates
            .Where(c => !waitingBlIds.Contains(c.MinifigBlId))
            .Select(c => new BlCatalogMatch(
                c.MinifigBlId, c.MinifigName, c.MinifigImageUrl, c.QuantityInMinifig))
            .ToList();

        Log.Information("PartLookup BL:{No}/C:{C} -> {W} wartend, {B} im BL-Cache",
            blPartNo, blColorId, matches.Count, blMatches.Count);

        return new PartLookupResult(blPartNo, blColorId, partName, colorName, colorRgb,
            matches, blMatches);
    }

    public async Task<bool> AssignPartToMinifigAsync(int trackedMinifigPartId, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var part = await ctx.TrackedMinifigParts
            .Include(p => p.TrackedMinifig)
                .ThenInclude(m => m.RequiredParts)
            .FirstOrDefaultAsync(p => p.Id == trackedMinifigPartId, ct);
        if (part == null) return false;

        // v0.1.24-beta.8 Phase 3: physisches Cap auf QuantityCollected
        // (Reservierungen werden hier nicht beruehrt).
        if (part.QuantityCollected >= part.QuantityNeeded)
        {
            // Schon komplett; nichts zu tun.
            return false;
        }

        part.QuantityCollected++;
        bool minifigCompleted = false;

        // Komplett-Check ueber EFFECTIVE (physisch + BL-reserviert). Damit
        // gilt die Figur als komplett wenn alle Teile entweder physisch da
        // sind ODER im BL-Shop reserviert.
        var allComplete = part.TrackedMinifig.RequiredParts.All(p =>
            (p.Id == part.Id ? part.QuantityCollected : p.QuantityCollected)
            + p.QuantityReservedFromBl >= p.QuantityNeeded);

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

        // v0.1.23: bei Status-Wechsel Waiting->Complete kann Bin.Kind kippen.
        if (minifigCompleted)
            await RecalcBinKindAsync(part.TrackedMinifig.StorageBinId, ct);

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

        // UX X.20 Teil 7e: wenn die Figur vorher COMPLETE war, zurueck auf
        // WAITING. Zusaetzlich:
        //   - DailyStats am Tag des CompletedAt um eins reduzieren (Audit-
        //     Konsistenz: wenn der User die Komplettierung zurueckdreht,
        //     soll der Counter es widerspiegeln).
        //   - ScanEvent als Audit-Trail mit Status-Wechsel-Hinweis.
        // Wir lesen CompletedAt VOR der Aenderung; das DailyStats-Update ist
        // best-effort - wenn am damaligen Tag kein Eintrag existiert (z.B.
        // wegen DB-Cleanup), unterbleibt das Decrement still.
        bool wasComplete = part.TrackedMinifig.Status == TrackedMinifigStatus.Complete;
        DateTime? completionDay = part.TrackedMinifig.CompletedAt?.Date;

        if (wasComplete)
        {
            part.TrackedMinifig.Status = TrackedMinifigStatus.Waiting;
            part.TrackedMinifig.CompletedAt = null;

            if (completionDay.HasValue)
            {
                var stat = await ctx.DailyStats.FirstOrDefaultAsync(
                    s => s.Date == completionDay.Value, ct);
                if (stat != null && stat.MinifigsCompletedCount > 0)
                {
                    stat.MinifigsCompletedCount--;
                }
            }
        }

        // ScanEvent fuer Audit-Trail. Bei Status-Wechsel deutlicher Text;
        // ohne Wechsel der bisherige Standard-Eintrag.
        var description = wasComplete
            ? $"Teil {part.PartNumber}/{part.ColorId} entfernt aus Figur '{part.TrackedMinifig.Name}' - Status: Komplett -> Wartend"
            : $"Teil {part.PartNumber}/{part.ColorId} entfernt aus Figur '{part.TrackedMinifig.Name}'";
        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.PartScan,
            RecognizedId = part.PartNumber,
            ResultDescription = description,
            WasUndone = false
        });

        await ctx.SaveChangesAsync(ct);

        // v0.1.23: bei Status-Wechsel Complete->Waiting kippt Bin auf Waiting.
        if (wasComplete)
            await RecalcBinKindAsync(part.TrackedMinifig.StorageBinId, ct);

        _persistence.RaiseDataChanged();

        Log.Information("Part-Zuordnung entfernt: {Part}/{Color} aus Figur '{Name}' (wasComplete={WC})",
            part.PartNumber, part.ColorId, part.TrackedMinifig.Name, wasComplete);
        return true;
    }

    public async Task<FloatingPart> AddPartToFloatingAsync(
        string blPartNo, int blColorId, string partName, string colorName,
        int quantity, int storageBinId,
        string? brickognizeCategory = null,
        CancellationToken ct = default)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity muss > 0 sein", nameof(quantity));

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Bin existiert? Inkl. Minifigs+RequiredParts fuer Strict-Mode-Bypass.
        var bin = await ctx.StorageBins
            .Include(b => b.TrackedMinifigs)
                .ThenInclude(m => m.RequiredParts)
            .FirstOrDefaultAsync(b => b.Id == storageBinId, ct)
            ?? throw new InvalidOperationException($"Lagerfach {storageBinId} existiert nicht.");

        // v0.1.23 Strict-Mode (S8): Floating darf nur in Empty/Floating/Waiting-mit-Match.
        BinKindGuard.EnsureBinAcceptsFloatingPart(
            bin, blPartNo, blColorId,
            $"Einzelteil {blPartNo}/{blColorId} in Fach '{bin.Label}' lagern");

        // Bug B Fix (UX X.28): wenn das Fach als "frei" markiert war, jetzt wieder als belegt markieren.
        if (bin.FreedAt != null)
        {
            Log.Information("Fach '{Label}' war als frei markiert (seit {FreedAt}) - wird durch neues Einzelteil wieder belegt",
                bin.Label, bin.FreedAt);
            bin.FreedAt = null;
        }

        // Existierender FloatingPart in selben Bin?
        var existing = await ctx.FloatingParts
            .FirstOrDefaultAsync(fp => fp.PartNumber == blPartNo
                                    && fp.ColorId == blColorId
                                    && fp.StorageBinId == storageBinId, ct);

        FloatingPart result;
        if (existing != null)
        {
            existing.Quantity += quantity;
            // UX X.33 Block N: BrickognizeCategory nur befuellen wenn vorher
            // leer war - Stapel-Match darf eine bestehende Kategorie nicht
            // ueberschreiben (Brickognize kann pro Scan abweichende Werte
            // liefern, der Bestand gilt).
            if (string.IsNullOrEmpty(existing.BrickognizeCategory) && !string.IsNullOrWhiteSpace(brickognizeCategory))
                existing.BrickognizeCategory = brickognizeCategory;
            result = existing;
        }
        else
        {
            result = new FloatingPart
            {
                PartNumber = blPartNo,
                ColorId = blColorId,
                ColorName = colorName,
                PartName = partName,
                Quantity = quantity,
                StorageBinId = storageBinId,
                AddedAt = DateTime.UtcNow,
                BrickognizeCategory = string.IsNullOrWhiteSpace(brickognizeCategory) ? null : brickognizeCategory
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
        await RecalcBinKindAsync(storageBinId, ct);
        _persistence.RaiseDataChanged();

        Log.Information("Floating-Part angelegt: {Part}/{Color} x{Qty} in Bin '{Bin}'",
            blPartNo, blColorId, quantity, bin.Label);
        return result;
    }

    public async Task<Dictionary<(string PartNo, int ColorId), List<FloatingPartLocation>>>
        FindFloatingLocationsForManyAsync(
            IReadOnlyCollection<(string PartNo, int ColorId)> keys,
            CancellationToken ct = default)
    {
        var result = new Dictionary<(string, int), List<FloatingPartLocation>>();
        if (keys == null || keys.Count == 0) return result;

        // Vor-Filter: nur Teile aus den angefragten PartNo + ColorId-Sets aus
        // der DB ziehen. Das kann false-positives ergeben (z.B. (A,1) + (B,2)
        // angefragt -> auch (A,2) und (B,1) durchgelassen), die per HashSet
        // unten gefiltert werden. Lohnt sich trotzdem, weil EF-Where mit zwei
        // Contains() ueber Sets eine einzige SQL-IN-Klausel je Spalte erzeugt.
        var partNos = keys.Select(k => k.PartNo).Distinct().ToList();
        var colorIds = keys.Select(k => k.ColorId).Distinct().ToList();
        var keySet = keys.ToHashSet();

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var raw = await ctx.FloatingParts
            .AsNoTracking()
            .Where(fp => partNos.Contains(fp.PartNumber) && colorIds.Contains(fp.ColorId))
            .Include(fp => fp.StorageBin)
            .ToListAsync(ct);

        // Exact-Filter + pro-Key gruppieren + pro-Bin aggregieren.
        foreach (var grp in raw
                     .Where(fp => fp.StorageBin != null
                                  && keySet.Contains((fp.PartNumber, fp.ColorId)))
                     .GroupBy(fp => (fp.PartNumber, fp.ColorId)))
        {
            var locations = grp
                .GroupBy(fp => new { fp.StorageBinId, BinLabel = fp.StorageBin!.Label })
                .Select(g => new FloatingPartLocation(
                    g.Key.StorageBinId,
                    g.Key.BinLabel,
                    g.Sum(fp => fp.Quantity)))
                .OrderByDescending(l => l.TotalQuantity)
                .ToList();
            result[grp.Key] = locations;
        }

        Log.Debug(
            "FindFloatingLocationsForManyAsync: {Keys} Anfragen -> {Raw} rohe Eintraege, " +
            "{Hits} Keys mit Treffern",
            keys.Count, raw.Count, result.Count);

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

        // UX X.29 (v0.1.16): Undo-Snapshot vor dem Loeschen erfassen.
        var snapshot = new UndoSnapshotFloatingDelete
        {
            OriginalFloatingId = fp.Id,
            PartNumber = fp.PartNumber,
            ColorId = fp.ColorId,
            PartName = fp.PartName,
            ColorName = fp.ColorName,
            Quantity = fp.Quantity,
            StorageBinId = fp.StorageBinId,
            AddedAt = fp.AddedAt,
            OriginMinifigId = fp.OriginMinifigId
        };

        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.Delete,
            RecognizedId = fp.PartNumber,
            ResultDescription = $"Einzelteil '{fp.PartName}' (BL:{fp.PartNumber}/{fp.ColorId}) x{fp.Quantity} geloescht",
            WasUndone = false,
            UndoData = System.Text.Json.JsonSerializer.Serialize(snapshot)
        });

        var affectedBin = fp.StorageBinId;
        ctx.FloatingParts.Remove(fp);
        await ctx.SaveChangesAsync(ct);
        await RecalcBinKindAsync(affectedBin, ct);
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

        // 2b) Bild lokal cachen (best-effort). bl_items.image_url ist bei
        // Subset-Eintraegen oft leer, deshalb laed PartImageProvider via BL-Direct-URL.
        string? localImagePath = null;
        try
        {
            localImagePath = await _imageProvider.GetImageFileByBlAsync("M", blMinifigId, null);
        }
        catch (Exception imgEx)
        {
            Log.Debug(imgEx, "CollectMinifig: Bild fuer {Bl} nicht ladbar", blMinifigId);
        }

        // 3) RequiredParts aus den Subsets bauen (mit Color-Name aus bl_colors)
        var subsets = await _catalog.GetMinifigPartsAsync(blMinifigId, ct);
        var allColors = await _catalog.GetAllColorsAsync(ct);
        var colorMap = allColors.ToDictionary(c => c.ColorId);

        // v0.1.22-beta.1 Fix 2026-05-12: PartName aus bl_items vorab bulk-
        // laden. Vorher PartName=string.Empty -> DeriveCategoryFromPartName
        // liefert "Unbekannt" -> Default-Regel beim Zerlegen wirkungslos
        // (Mercedes-AMG-Helm-Fall mit Box19-01). Eine Query statt N+1.
        var partNos = subsets.Select(s => s.ItemNo).Distinct().ToList();
        var partNames = _cache != null
            ? await _cache.GetItemNamesAsync(partNos, ct)
            : new Dictionary<string, string>();

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var bin = await ctx.StorageBins
            .Include(b => b.TrackedMinifigs)
            .FirstOrDefaultAsync(b => b.Id == storageBinId, ct)
            ?? throw new InvalidOperationException($"Lagerfach {storageBinId} existiert nicht.");

        // v0.1.23 Strict-Mode (S10): Figur darf nicht in Floating-Bin.
        BinKindGuard.EnsureBinAcceptsMinifig(bin, $"Figur '{item.Name}' in Fach '{bin.Label}' anlegen");

        // Bug B Fix (UX X.28): wenn das Fach als "frei" markiert war, jetzt wieder als belegt markieren.
        if (bin.FreedAt != null)
        {
            Log.Information("Fach '{Label}' war als frei markiert (seit {FreedAt}) - wird durch neue Figur wieder belegt",
                bin.Label, bin.FreedAt);
            bin.FreedAt = null;
        }

        var minifig = new TrackedMinifig
        {
            FigNum = blMinifigId,
            BricklinkId = blMinifigId,
            Name = item.Name,
            ImageUrl = item.ImageUrl,
            LocalImagePath = localImagePath,
            CreatedAt = DateTime.UtcNow,
            Status = TrackedMinifigStatus.Waiting,
            StorageBinId = storageBinId,
            RequiredParts = subsets.Select(s =>
            {
                colorMap.TryGetValue(s.ColorId, out var color);
                partNames.TryGetValue(s.ItemNo, out var partName);
                return new TrackedMinifigPart
                {
                    PartNumber = s.ItemNo,
                    ColorId = s.ColorId,
                    PartName = partName ?? string.Empty, // siehe Bulk-Lookup oben
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
        // v0.1.23: betroffene Quell-Bins fuer RecalcKind sammeln.
        var reverseMatchBinIds = new HashSet<int>();
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
                reverseMatchBinIds.Add(fp.StorageBinId);
                if (fp.Quantity <= 0) ctx.FloatingParts.Remove(fp);
            }
        }

        // v0.1.24-beta.8 Phase 3: Komplett-Check ueber Effective
        // (QuantityCollected + QuantityReservedFromBl).
        var isComplete = minifig.RequiredParts.All(p =>
                             (p.QuantityCollected + p.QuantityReservedFromBl) >= p.QuantityNeeded)
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

        // v0.1.23: Ziel-Bin + alle Reverse-Match-Quell-Bins recalculaten.
        if (_binService != null)
        {
            await _binService.RecalculateKindAsync(storageBinId, ct);
            foreach (var bid in reverseMatchBinIds)
                await RecalcBinKindAsync(bid, ct);
        }

        _persistence.RaiseDataChanged();

        Log.Information("CollectMinifig: '{Name}' (BL:{Bl}) angelegt in '{Bin}', Trigger {Tp}/{Tc} x{Tq}, ReverseMatch={Rm}{Done}",
            minifig.Name, blMinifigId, bin.Label, triggerPartNo, triggerColorId, triggerQuantity,
            reverseMatched, isComplete ? " => COMPLETE" : "");

        return minifig;
    }

    /// <summary>
    /// UX X.32 v0.1.19-beta.4 Block D: erweiterte CollectMinifig-Methode mit
    /// User-Filter welche FloatingParts via Reverse-Match konsumiert werden.
    /// Logik analog zu <see cref="CollectMinifigFromSupersetAsync"/>, aber:
    ///   - Reverse-Match-Schleife laeuft nur fuer (PartNo, ColorId)-Paare die
    ///     in <paramref name="consumePartsFromFloating"/> stehen.
    ///   - Liefert ein <see cref="CollectMinifigResult"/> mit konsumierten
    ///     FloatingParts (Quell-Bin-Label) - der UI-Layer baut daraus das
    ///     Sammel-Popup.
    /// </summary>
    public async Task<CollectMinifigResult> CollectMinifigFromSupersetWithSelectionAsync(
        string blMinifigId, int storageBinId,
        string triggerPartNo, int triggerColorId, int triggerQuantity,
        IReadOnlyCollection<(string PartNo, int ColorId)> consumePartsFromFloating,
        IReadOnlyCollection<(string PartNo, int ColorId)>? manuellClaimedParts = null,
        CancellationToken ct = default)
    {
        // Filter-Set fuer schnellen Lookup pro Required-Part. Stringvergleich
        // case-insensitive damit Edge-Cases mit Gross-Klein-Schreibung
        // (z.B. "3001" vs "3001a") nicht zu Misses fuehren.
        var filter = new HashSet<(string, int)>(
            consumePartsFromFloating?.Select(x => (x.PartNo.ToLowerInvariant(), x.ColorId))
            ?? Enumerable.Empty<(string, int)>());

        // v0.1.24-beta.1 Phase 2a (Konzept 6.3 / Befund A3): manuell-markierte
        // Teile. Wird unten NACH dem Reverse-Match angewendet: QuantityCollected
        // erhoehen ohne FloatingPart-Effekt. Cap auf QuantityNeeded damit
        // doppelte Eintraege oder Race-mit-Trigger nicht ueber das Soll
        // schiessen.
        var manuellClaimed = new HashSet<(string, int)>(
            manuellClaimedParts?.Select(x => (x.PartNo.ToLowerInvariant(), x.ColorId))
            ?? Enumerable.Empty<(string, int)>());

        // 1) Sicherstellen dass die volle Subsets-Liste der Minifig im Cache ist.
        await _catalog.EnsureFullSubsetsAsync(blMinifigId, ct);

        var item = await _catalog.GetMinifigDetailsAsync(blMinifigId, ct)
            ?? throw new InvalidOperationException($"Minifig '{blMinifigId}' nicht in BL gefunden.");

        string? localImagePath = null;
        try { localImagePath = await _imageProvider.GetImageFileByBlAsync("M", blMinifigId, null); }
        catch (Exception imgEx) { Log.Debug(imgEx, "CollectMinifig (Selection): Bild nicht ladbar"); }

        var subsets = await _catalog.GetMinifigPartsAsync(blMinifigId, ct);
        var allColors = await _catalog.GetAllColorsAsync(ct);
        var colorMap = allColors.ToDictionary(c => c.ColorId);

        // v0.1.22-beta.1 Fix 2026-05-12: PartName-Bulk-Lookup analog zum
        // Schwester-Pfad CollectMinifigFromSupersetAsync. Pflicht damit die
        // Default-Regel beim spaeteren Zerlegen via DeriveCategoryFromPartName
        // greifen kann.
        var partNos = subsets.Select(s => s.ItemNo).Distinct().ToList();
        var partNames = _cache != null
            ? await _cache.GetItemNamesAsync(partNos, ct)
            : new Dictionary<string, string>();

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var bin = await ctx.StorageBins
            .Include(b => b.TrackedMinifigs)
            .FirstOrDefaultAsync(b => b.Id == storageBinId, ct)
            ?? throw new InvalidOperationException($"Lagerfach {storageBinId} existiert nicht.");

        // v0.1.23 Strict-Mode (S10): Figur darf nicht in Floating-Bin.
        BinKindGuard.EnsureBinAcceptsMinifig(bin, $"Figur '{item.Name}' in Fach '{bin.Label}' anlegen");

        if (bin.FreedAt != null)
        {
            Log.Information("Fach '{Label}' war als frei markiert (seit {FreedAt}) - wird durch neue Figur wieder belegt",
                bin.Label, bin.FreedAt);
            bin.FreedAt = null;
        }

        var minifig = new TrackedMinifig
        {
            FigNum = blMinifigId,
            BricklinkId = blMinifigId,
            Name = item.Name,
            ImageUrl = item.ImageUrl,
            LocalImagePath = localImagePath,
            CreatedAt = DateTime.UtcNow,
            Status = TrackedMinifigStatus.Waiting,
            StorageBinId = storageBinId,
            RequiredParts = subsets.Select(s =>
            {
                colorMap.TryGetValue(s.ColorId, out var color);
                partNames.TryGetValue(s.ItemNo, out var partName);
                return new TrackedMinifigPart
                {
                    PartNumber = s.ItemNo,
                    ColorId = s.ColorId,
                    PartName = partName ?? string.Empty, // siehe Bulk-Lookup oben
                    ColorName = color?.Name ?? $"Color {s.ColorId}",
                    QuantityNeeded = s.Quantity,
                    QuantityCollected = 0
                };
            }).ToList()
        };

        // Trigger-Teil sofort auf collected setzen.
        var trigger = minifig.RequiredParts.FirstOrDefault(p =>
            p.PartNumber == triggerPartNo && p.ColorId == triggerColorId);
        if (trigger != null)
            trigger.QuantityCollected = Math.Min(triggerQuantity, trigger.QuantityNeeded);

        ctx.TrackedMinifigs.Add(minifig);
        await ctx.SaveChangesAsync(ct);

        // Reverse-Match NUR fuer Filter-Eintraege.
        var consumed = new List<ConsumedFloatingPartInfo>();
        var reverseMatched = 0;
        // v0.1.23: Quell-Bin-Ids fuer RecalcKind sammeln.
        var reverseMatchBinIds = new HashSet<int>();
        foreach (var required in minifig.RequiredParts)
        {
            var stillNeeded = required.QuantityNeeded - required.QuantityCollected;
            if (stillNeeded <= 0) continue;

            // User-Filter pruefen - wenn nicht in der Liste, NICHT konsumieren.
            var key = (required.PartNumber.ToLowerInvariant(), required.ColorId);
            if (!filter.Contains(key)) continue;

            var candidates = await ctx.FloatingParts
                .Include(fp => fp.StorageBin)
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
                reverseMatchBinIds.Add(fp.StorageBinId);

                consumed.Add(new ConsumedFloatingPartInfo
                {
                    BlPartNo = required.PartNumber,
                    BlColorId = required.ColorId,
                    PartName = required.PartName,
                    ColorName = required.ColorName,
                    Quantity = take,
                    SourceBinLabel = fp.StorageBin?.Label ?? string.Empty
                });

                if (fp.Quantity <= 0) ctx.FloatingParts.Remove(fp);
            }
        }

        // v0.1.24-beta.1 Phase 2a (Konzept 6.3): manuell-markierte Teile
        // direkt als gesammelt eintragen. KEIN FloatingPart-Effekt - der
        // User hat das Teil unabhaengig vom Lager (z.B. lose auf dem Tisch).
        // Nur Required-Parts beruecksichtigen die noch Bedarf haben (kein
        // Doppel-Eintrag wenn der Reverse-Match das Teil schon gefuellt
        // hat oder der Trigger es ist).
        var manuellClaimedApplied = 0;
        foreach (var required in minifig.RequiredParts)
        {
            var key = (required.PartNumber.ToLowerInvariant(), required.ColorId);
            if (!manuellClaimed.Contains(key)) continue;

            var stillNeeded = required.QuantityNeeded - required.QuantityCollected;
            if (stillNeeded <= 0)
            {
                Log.Warning(
                    "ManuellClaimed: Teil {Part}/{Color} schon voll bei Figur {Bl}, ueberspringe",
                    required.PartNumber, required.ColorId, blMinifigId);
                continue;
            }

            required.QuantityCollected = required.QuantityNeeded;
            manuellClaimedApplied++;
        }

        // v0.1.24-beta.8 Phase 3: Komplett-Check ueber Effective.
        var isComplete = minifig.RequiredParts.All(p =>
                             (p.QuantityCollected + p.QuantityReservedFromBl) >= p.QuantityNeeded)
                         && minifig.RequiredParts.Count > 0;
        if (isComplete)
        {
            minifig.Status = TrackedMinifigStatus.Complete;
            minifig.CompletedAt = DateTime.UtcNow;
        }

        // v0.1.24-beta.1 Phase 2a (Konzept 6.3): manuell-markierte Teile im
        // Audit-Trail sichtbar machen. Suffix nur wenn der Pfad genutzt wurde
        // - Backwards-Compat fuer den heutigen Aufrufer ohne manuellClaimed.
        var manuellSuffix = manuellClaimedApplied > 0
            ? $" + {manuellClaimedApplied} manuell markiert"
            : string.Empty;

        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.PartScan,
            RecognizedId = triggerPartNo,
            ResultDescription = isComplete
                ? $"Neue Figur '{minifig.Name}' direkt komplett (User-Auswahl{manuellSuffix})"
                : $"Neue Figur '{minifig.Name}' angelegt in '{bin.Label}' (User-Auswahl, {reverseMatched} Teile uebernommen{manuellSuffix})",
            WasUndone = false
        });

        await ctx.SaveChangesAsync(ct);

        // v0.1.23: Ziel-Bin + alle Quell-Bins aus Reverse-Match recalculaten.
        if (_binService != null)
        {
            await _binService.RecalculateKindAsync(storageBinId, ct);
            foreach (var bid in reverseMatchBinIds)
                await RecalcBinKindAsync(bid, ct);
        }

        _persistence.RaiseDataChanged();

        Log.Information("CollectMinifig (Selection): '{Name}' angelegt in '{Bin}', Trigger {Tp}/{Tc}, Filter={FilterCount}, Consumed={Cons}, ManuellClaimed={Mc}{Done}",
            minifig.Name, bin.Label, triggerPartNo, triggerColorId,
            filter.Count, consumed.Count, manuellClaimedApplied,
            isComplete ? " => COMPLETE" : "");

        return new CollectMinifigResult
        {
            SavedMinifig = minifig,
            IsFullyComplete = isComplete,
            ConsumedFloatingParts = consumed
        };
    }
}
