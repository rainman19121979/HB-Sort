using HBSort.Core.Database;
using HBSort.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// EF-Core-Implementierung der Minifig-Persistenz mit Reverse-Match.
///
/// Reverse-Match-Logik:
/// 1. Figur + RequiredParts werden angelegt (Status=Waiting, Bin zugewiesen)
/// 2. Suche FloatingParts in JEDEM Fach, die zu Required-Parts passen
///    (Match: BL-Part-No + BL-Color-Id identisch)
/// 3. Pro Treffer: FloatingPart-Quantity bis QuantityNeeded auf das Required-Part
///    "uebertragen" (QuantityCollected erhoehen). Rest des FloatingPart bleibt bestehen.
///    Wenn FloatingPart komplett verbraucht: loeschen.
/// 4. Status auf Complete setzen wenn alle RequiredParts vollstaendig.
///
/// Hinweis: FloatingParts duerfen aus ANDEREN Faechern stammen – der User wird
/// per Toast informiert ("Verschiebe Teil X von Box 7 in Box 3"). Die Daten-
/// Anpassung passiert hier; der Toast ist Sache des aufrufenden ViewModels.
/// </summary>
public class MinifigPersistenceService : IMinifigPersistenceService
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;

    public event EventHandler? DataChanged;

    public MinifigPersistenceService(IDbContextFactory<UserDataContext> ctxFactory)
    {
        _ctxFactory = ctxFactory;
    }

    public void RaiseDataChanged() => DataChanged?.Invoke(this, EventArgs.Empty);

    public async Task<bool> DeleteAsync(int trackedMinifigId, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var minifig = await ctx.TrackedMinifigs
            .Include(m => m.RequiredParts)
            .FirstOrDefaultAsync(m => m.Id == trackedMinifigId, ct);
        if (minifig == null) return false;

        // FloatingParts mit OriginMinifigId=this auf null setzen (nicht loeschen –
        // der User hat die Teile ja im Pool, nur die Origin-Verbindung verschwindet).
        var origins = await ctx.FloatingParts
            .Where(fp => fp.OriginMinifigId == trackedMinifigId)
            .ToListAsync(ct);
        foreach (var fp in origins) fp.OriginMinifigId = null;

        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.MinifigScan,
            RecognizedId = minifig.BricklinkId ?? minifig.FigNum,
            ResultDescription = $"Figur '{minifig.Name}' geloescht (Status war: {minifig.Status})",
            WasUndone = false
        });

        // RequiredParts werden via Cascade-Delete entfernt (EF Core: Cascade auf TrackedMinifig).
        ctx.TrackedMinifigs.Remove(minifig);
        await ctx.SaveChangesAsync(ct);

        Log.Information("Figur '{Name}' (Id={Id}, Status={Status}) geloescht – {OriginCount} FloatingParts entkoppelt",
            minifig.Name, minifig.Id, minifig.Status, origins.Count);

        DataChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<DismantleResult> DismantleAsync(int trackedMinifigId,
        IEnumerable<DismantlePartChoice> choices,
        CancellationToken ct = default)
    {
        var choiceList = choices.ToList();
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        var minifig = await ctx.TrackedMinifigs
            .Include(m => m.RequiredParts)
            .FirstOrDefaultAsync(m => m.Id == trackedMinifigId, ct);
        if (minifig == null) return new DismantleResult { Success = false };

        var originName = minifig.Name;
        var originBlId = minifig.BricklinkId ?? minifig.FigNum;
        var now = DateTime.UtcNow;
        var createdCount = 0;
        var totalQty = 0;

        foreach (var c in choiceList.Where(c => c.IsKept))
        {
            var part = minifig.RequiredParts.FirstOrDefault(p => p.Id == c.TrackedMinifigPartId);
            if (part == null) continue;

            // Quantity: was wirklich da ist (collected) hat Vorrang. Wenn 0,
            // fallen wir auf needed zurueck (alte Code-Pfade die vor T3 liefen).
            var qty = part.QuantityCollected > 0
                ? part.QuantityCollected
                : part.QuantityNeeded;
            if (qty <= 0) continue;

            var binId = c.TargetBinId
                ?? throw new InvalidOperationException(
                    $"Lagerfach fehlt fuer Teil '{part.PartName}'");

            // Zusammenfuehren wenn schon vorhanden (Part+Color+Bin).
            var existing = await ctx.FloatingParts.FirstOrDefaultAsync(
                fp => fp.PartNumber == part.PartNumber
                   && fp.ColorId == part.ColorId
                   && fp.StorageBinId == binId, ct);

            if (existing != null)
            {
                existing.Quantity += qty;
            }
            else
            {
                ctx.FloatingParts.Add(new FloatingPart
                {
                    PartNumber = part.PartNumber,
                    ColorId = part.ColorId,
                    BricklinkColorId = part.BricklinkColorId ?? part.ColorId,
                    ColorName = part.ColorName,
                    PartName = part.PartName,
                    Quantity = qty,
                    StorageBinId = binId,
                    AddedAt = now
                });
            }
            createdCount++;
            totalQty += qty;
        }

        // Backwards-Compat: alte FloatingParts mit OriginMinifigId=this entkoppeln,
        // damit das anschliessende Loeschen der Figur nicht von alten Verweisen blockt.
        var oldOrigins = await ctx.FloatingParts
            .Where(fp => fp.OriginMinifigId == trackedMinifigId)
            .ToListAsync(ct);
        foreach (var fp in oldOrigins) fp.OriginMinifigId = null;

        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = now,
            Type = ScanType.MinifigScan,
            RecognizedId = originBlId,
            ResultDescription = totalQty > 0
                ? $"Figur '{originName}' zerlegt – {totalQty} Einzelteil(e) in Pool uebernommen"
                : $"Figur '{originName}' zerlegt (keine Teile uebernommen)",
            WasUndone = false
        });

        // Figur und ihre RequiredParts loeschen (Cascade entfernt RequiredParts).
        ctx.TrackedMinifigs.Remove(minifig);
        await ctx.SaveChangesAsync(ct);

        Log.Information(
            "Figur '{Name}' (Id={Id}) zerlegt: {Count} Teile-Eintraege ({Qty} Stueck) in Pool, Figur geloescht",
            originName, trackedMinifigId, createdCount, totalQty);

        DataChanged?.Invoke(this, EventArgs.Empty);

        return new DismantleResult
        {
            Success = true,
            CreatedFloatingParts = createdCount,
            TotalPartsTransferred = totalQty
        };
    }

    public async Task<int> CleanupOldDismantledMinifigsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var stale = await ctx.TrackedMinifigs
            .Where(m => m.Status == TrackedMinifigStatus.Dismantled)
            .ToListAsync(ct);
        if (stale.Count == 0) return 0;

        ctx.TrackedMinifigs.RemoveRange(stale);
        await ctx.SaveChangesAsync(ct);
        Log.Information("Cleanup: {Count} alte DISMANTLED-Figuren geloescht", stale.Count);
        DataChanged?.Invoke(this, EventArgs.Empty);
        return stale.Count;
    }

    public async Task<int> CleanupOnePartCompletesAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var pseudo = await ctx.TrackedMinifigs
            .Where(m => m.Status == TrackedMinifigStatus.Complete)
            .Include(m => m.RequiredParts)
            .ToListAsync(ct);
        var toDelete = pseudo.Where(m => m.RequiredParts.Count == 1).ToList();
        if (toDelete.Count == 0) return 0;

        ctx.TrackedMinifigs.RemoveRange(toDelete);
        await ctx.SaveChangesAsync(ct);
        Log.Information("Cleanup: {Count} Pseudo-1/1-Figuren geloescht", toDelete.Count);
        DataChanged?.Invoke(this, EventArgs.Empty);
        return toDelete.Count;
    }

    public async Task<PersistMinifigResult> PersistAndStoreAsync(
        PersistMinifigInput input,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.BricklinkId))
            throw new ArgumentException("BricklinkId darf nicht leer sein", nameof(input));
        if (input.StorageBinId <= 0)
            throw new ArgumentException("StorageBinId muss gesetzt sein", nameof(input));

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Bin existiert?
        var bin = await ctx.StorageBins.FirstOrDefaultAsync(b => b.Id == input.StorageBinId, ct);
        if (bin == null)
            throw new InvalidOperationException($"Lagerfach {input.StorageBinId} existiert nicht.");

        // 1) TrackedMinifig + RequiredParts anlegen
        var minifig = new TrackedMinifig
        {
            // CLAUDE.md sieht FigNum (Rebrickable) und BricklinkId vor. Wir sind aktuell
            // BL-first ohne Rebrickable-Resolve – also legen wir die BL-ID auch in FigNum
            // ab (FigNum ist non-null). R4 raeumt das spaeter ggf. auf.
            FigNum = input.BricklinkId,
            BricklinkId = input.BricklinkId,
            Name = input.Name,
            ImageUrl = input.ImageUrl,
            LocalImagePath = input.LocalImagePath,
            UserNotes = string.IsNullOrWhiteSpace(input.UserNotes) ? null : input.UserNotes,
            CreatedAt = DateTime.UtcNow,
            Status = TrackedMinifigStatus.Waiting,
            StorageBinId = input.StorageBinId,
            RequiredParts = input.RequiredParts.Select(p => new TrackedMinifigPart
            {
                // Wir sind BL-first: BL-Part-No und BL-Color-Id landen in den
                // "primaeren" Feldern (PartNumber/ColorId). BricklinkColorId
                // doppeln wir, damit der spaetere BSX-Export ohne Mapping arbeiten kann.
                PartNumber = p.BricklinkPartNo,
                ColorId = p.BricklinkColorId,
                BricklinkColorId = p.BricklinkColorId,
                PartName = p.PartName,
                ColorName = p.ColorName,
                QuantityNeeded = p.QuantityNeeded,
                // Manuelle Vorab-Markierung des Users; cap auf QuantityNeeded damit
                // der nachfolgende Reverse-Match sich nicht verrechnet.
                QuantityCollected = Math.Min(Math.Max(0, p.QuantityCollected), p.QuantityNeeded)
            }).ToList()
        };
        ctx.TrackedMinifigs.Add(minifig);
        await ctx.SaveChangesAsync(ct);

        // 2) Reverse-Match: pro Required-Part nachschauen ob in irgendeinem Fach
        //    bereits FloatingParts mit derselben Part-No+Color-Id liegen.
        var reverseMatched = 0;
        var completedParts = 0;

        foreach (var required in minifig.RequiredParts)
        {
            // Alle passenden FloatingParts (egal in welchem Fach), aelteste zuerst
            var candidates = await ctx.FloatingParts
                .Where(fp => fp.PartNumber == required.PartNumber
                          && fp.ColorId == required.ColorId)
                .OrderBy(fp => fp.AddedAt)
                .ToListAsync(ct);

            foreach (var fp in candidates)
            {
                var stillNeeded = required.QuantityNeeded - required.QuantityCollected;
                if (stillNeeded <= 0) break;

                var take = Math.Min(stillNeeded, fp.Quantity);
                required.QuantityCollected += take;
                fp.Quantity -= take;
                reverseMatched += take;

                if (fp.Quantity <= 0)
                {
                    ctx.FloatingParts.Remove(fp);
                }
            }

            if (required.QuantityCollected >= required.QuantityNeeded)
                completedParts++;
        }

        var isComplete = minifig.RequiredParts.All(p => p.QuantityCollected >= p.QuantityNeeded)
                         && minifig.RequiredParts.Count > 0;

        if (isComplete)
        {
            minifig.Status = TrackedMinifigStatus.Complete;
            minifig.CompletedAt = DateTime.UtcNow;
        }

        // 3) ScanEvent fuer die Statistik / Undo-Liste
        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.MinifigScan,
            RecognizedId = input.BricklinkId,
            Confidence = input.Confidence,
            ImagePath = input.ScanImagePath,
            ResultDescription = isComplete
                ? $"Minifigur '{input.Name}' direkt komplett (Reverse-Match)"
                : $"Minifigur '{input.Name}' im Fach '{bin.Label}' angelegt",
            WasUndone = false
        });

        await ctx.SaveChangesAsync(ct);

        Log.Information(
            "Minifigur '{Name}' (BL:{Bl}) gespeichert. Bin={Bin}, ReverseMatched={Rm}, Complete={Done}",
            minifig.Name, minifig.BricklinkId, bin.Label, reverseMatched, isComplete);

        // Listener (z.B. Wartende-Figuren-Liste) ueber Aenderung benachrichtigen.
        DataChanged?.Invoke(this, EventArgs.Empty);

        return new PersistMinifigResult
        {
            SavedMinifig = minifig,
            ReverseMatchedFloating = reverseMatched,
            CompletedRequiredParts = completedParts,
            IsFullyComplete = isComplete
        };
    }
}
