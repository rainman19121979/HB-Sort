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
/// Hinweis: FloatingParts duerfen aus ANDEREN Faechern stammen - der User wird
/// per Toast informiert ("Verschiebe Teil X von Box 7 in Box 3"). Die Daten-
/// Anpassung passiert hier; der Toast ist Sache des aufrufenden ViewModels.
/// </summary>
public class MinifigPersistenceService : IMinifigPersistenceService
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly ICategoryBinMappingService? _categoryMapping;
    private readonly IStorageBinService? _binService;
    private readonly IBlInventoryService? _blInventory;

    public event EventHandler? DataChanged;

    /// <summary>
    /// Pre-Tag-Fix v0.1.19-beta.7: <paramref name="categoryMapping"/> ist
    /// optional damit bestehende Tests den Service ohne den neuen Service
    /// instanziieren koennen. Wenn null: BrickognizeCategory wird beim
    /// Zerlegen auf "Unbekannt" gesetzt (Default-Regel-konform). Production
    /// (DI) reicht den echten Service durch -> Heuristik ueber den PartName
    /// findet typischerweise die Kategorie.
    ///
    /// v0.1.23: <paramref name="binService"/> ist optional. Wenn null,
    /// werden RecalculateKindAsync-Aufrufe nach Schreib-Aktionen
    /// uebersprungen (Test-Backwards-Compat). Production (DI) reicht den
    /// Service durch, damit Bin.Kind nach jeder Aktion korrekt ist.
    ///
    /// v0.1.24-beta.10 V1: <paramref name="blInventory"/> ist optional. Wenn
    /// null, werden BL-Reservierungen vor Loesch-/Zerlegungs-/Cleanup-Pfaden
    /// NICHT freigegeben (Tests). Production (DI) reicht den Service durch,
    /// damit Geist-Reservierungen verhindert werden (Audit-Befund H1).
    /// </summary>
    public MinifigPersistenceService(
        IDbContextFactory<UserDataContext> ctxFactory,
        ICategoryBinMappingService? categoryMapping = null,
        IStorageBinService? binService = null,
        IBlInventoryService? blInventory = null)
    {
        _ctxFactory = ctxFactory;
        _categoryMapping = categoryMapping;
        _binService = binService;
        _blInventory = blInventory;
    }

    /// <summary>
    /// v0.1.24-beta.10 V1 (Audit-Befund H1): vor jedem Loesch-/Cleanup-Pfad
    /// die BL-Reservierungen der betroffenen Minifigs freigeben. No-Op wenn
    /// kein IBlInventoryService injiziert ist (Test-Pfade) oder die ID-Liste
    /// leer ist. Fehler werden geloggt aber nicht propagiert - das Loeschen
    /// darf nie an einer Reservierungs-Buchhaltungs-Stoerung scheitern.
    /// </summary>
    private async Task ReleaseBlReservationsForAsync(IEnumerable<int> minifigIds, CancellationToken ct)
    {
        if (_blInventory == null) return;
        try
        {
            await _blInventory.ReleaseAllForMinifigsAsync(minifigIds, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReleaseAllForMinifigsAsync geworfen - Loesch-Pfad faehrt trotzdem fort");
        }
    }

    /// <summary>
    /// v0.1.23 Helper: ruft RecalculateKindAsync fuer alle uebergebenen
    /// Bin-Ids (Duplikate werden gefiltert, null gefiltert).
    /// </summary>
    private async Task RecalcBinKindsAsync(IEnumerable<int?> binIds, CancellationToken ct)
    {
        if (_binService == null) return;
        var distinct = binIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        foreach (var binId in distinct)
        {
            try { await _binService.RecalculateKindAsync(binId, ct); }
            catch (Exception ex) { Log.Warning(ex, "RecalculateKindAsync({BinId}) geworfen", binId); }
        }
    }

    public void RaiseDataChanged() => DataChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Phase 6: hochzaehlen einer DailyStats-Spalte fuer "heute".
    /// Legt den Tageseintrag an wenn er noch nicht existiert.
    /// Aufrufer muss SaveChangesAsync selbst triggern (wir haengen uns in den
    /// vorhandenen Save am Ende der Operation ein).
    /// </summary>
    private static async Task IncrementDailyStatAsync(UserDataContext ctx,
        Action<DailyStats> mutate, CancellationToken ct)
    {
        // Audit M-2 (2026-05-04): DailyStats.Date wird konsequent in UTC
        // gespeichert (DateTime.UtcNow.Date). Vorher: DateTime.Today (lokal).
        // Lese-Pfade (LiveStatsViewModel, SettingsViewModel-Statistik) sind
        // ebenfalls auf UTC umgestellt. Alte Eintraege aus dem lokalen Schema
        // bleiben as-is in der DB - bei Zeitzonen-Wechsel des Users kann ein
        // alter Eintrag um max. einen Tag versetzt erscheinen, wir nehmen das
        // bewusst in Kauf.
        var today = DateTime.UtcNow.Date;
        var stat = await ctx.DailyStats.FirstOrDefaultAsync(s => s.Date == today, ct);
        if (stat == null)
        {
            stat = new DailyStats { Date = today };
            ctx.DailyStats.Add(stat);
        }
        mutate(stat);
    }

    public async Task<bool> DeleteAsync(int trackedMinifigId, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var minifig = await ctx.TrackedMinifigs
            .Include(m => m.RequiredParts)
            .FirstOrDefaultAsync(m => m.Id == trackedMinifigId, ct);
        if (minifig == null) return false;

        // FloatingParts mit OriginMinifigId=this auf null setzen (nicht loeschen -
        // der User hat die Teile ja im Pool, nur die Origin-Verbindung verschwindet).
        var origins = await ctx.FloatingParts
            .Where(fp => fp.OriginMinifigId == trackedMinifigId)
            .ToListAsync(ct);
        foreach (var fp in origins) fp.OriginMinifigId = null;

        // UX X.29 (v0.1.16): Undo-Snapshot vor dem Loeschen erfassen.
        var snapshot = new UndoSnapshotMinifigDelete
        {
            OriginalMinifigId = minifig.Id,
            BricklinkId = minifig.BricklinkId ?? string.Empty,
            FigNum = minifig.FigNum,
            Name = minifig.Name,
            ImageUrl = minifig.ImageUrl,
            LocalImagePath = minifig.LocalImagePath,
            UserNotes = minifig.UserNotes,
            CreatedAt = minifig.CreatedAt,
            CompletedAt = minifig.CompletedAt,
            Status = minifig.Status.ToString(),
            StorageBinId = minifig.StorageBinId,
            RequiredParts = minifig.RequiredParts.Select(p => new UndoSnapshotPart
            {
                PartNumber = p.PartNumber,
                ColorId = p.ColorId,
                PartName = p.PartName,
                ColorName = p.ColorName,
                QuantityNeeded = p.QuantityNeeded,
                QuantityCollected = p.QuantityCollected
            }).ToList()
        };

        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.Delete,
            RecognizedId = minifig.BricklinkId ?? minifig.FigNum,
            ResultDescription = $"Figur '{minifig.Name}' geloescht (Status war: {minifig.Status})",
            WasUndone = false,
            UndoData = System.Text.Json.JsonSerializer.Serialize(snapshot)
        });

        var affectedBin = minifig.StorageBinId;

        // v0.1.24-beta.10 V1: BL-Reservierungen freigeben BEVOR die Figur
        // weg ist - sonst bleiben Geist-Reservierungen im Lot-Spiegel.
        // Eigener Service-Context; Cascade-Delete im Aufrufer-Context
        // raeumt die Part-Felder ohnehin weg.
        await ReleaseBlReservationsForAsync(new[] { trackedMinifigId }, ct);

        // RequiredParts werden via Cascade-Delete entfernt (EF Core: Cascade auf TrackedMinifig).
        ctx.TrackedMinifigs.Remove(minifig);
        await ctx.SaveChangesAsync(ct);

        // v0.1.23: Bin.Kind neu berechnen nach Loeschen.
        await RecalcBinKindsAsync(new[] { affectedBin }, ct);

        Log.Information("Figur '{Name}' (Id={Id}, Status={Status}) geloescht - {OriginCount} FloatingParts entkoppelt",
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
        var assignedCount = 0;
        var completedNames = new List<string>();

        foreach (var c in choiceList.Where(c => c.IsKept))
        {
            var part = minifig.RequiredParts.FirstOrDefault(p => p.Id == c.TrackedMinifigPartId);
            if (part == null) continue;

            // UX X.25 Validierung: bei IsKept=true muss GENAU EINE der beiden
            // Ziel-Properties gesetzt sein - TargetBinId (FloatingPart-Pfad)
            // ODER AssignToTrackedMinifigPartId (Direkt-Zuordnung-Pfad).
            var hasBin = c.TargetBinId.HasValue;
            var hasAssign = c.AssignToTrackedMinifigPartId.HasValue;
            if (hasBin && hasAssign)
            {
                throw new InvalidOperationException(
                    $"Teil '{part.PartName}': TargetBinId UND AssignToTrackedMinifigPartId " +
                    "koennen nicht gleichzeitig gesetzt sein - genau einer von beiden noetig.");
            }
            if (!hasBin && !hasAssign)
            {
                throw new InvalidOperationException(
                    $"Teil '{part.PartName}': weder TargetBinId noch " +
                    "AssignToTrackedMinifigPartId gesetzt - einer von beiden noetig bei IsKept=true.");
            }

            // Quantity: was wirklich da ist (collected) hat Vorrang. Wenn 0,
            // fallen wir auf needed zurueck (alte Code-Pfade die vor T3 liefen).
            var qty = part.QuantityCollected > 0
                ? part.QuantityCollected
                : part.QuantityNeeded;
            if (qty <= 0) continue;

            // ===== UX X.25 Direkt-Zuordnung-Pfad =====
            if (hasAssign)
            {
                // Wir laden den wartenden TrackedMinifigPart inline (gleiche
                // Transaction wie der Rest von DismantleAsync). AssignAsync-
                // Logik aus PartLookupService duplizieren statt aufrufen, damit
                // alles atomar in einer SaveChangesAsync landet.
                var waitingPart = await ctx.TrackedMinifigParts
                    .Include(p => p.TrackedMinifig)
                        .ThenInclude(m => m.RequiredParts)
                    .FirstOrDefaultAsync(p => p.Id == c.AssignToTrackedMinifigPartId!.Value, ct);

                if (waitingPart == null)
                {
                    Log.Warning("DismantleAsync: AssignToTrackedMinifigPartId={Id} nicht gefunden",
                        c.AssignToTrackedMinifigPartId);
                    continue;
                }

                // v0.1.24-beta.8 Phase 3: physisches Cap auf QuantityCollected.
                if (waitingPart.QuantityCollected >= waitingPart.QuantityNeeded)
                {
                    Log.Information("DismantleAsync: Teil {Part} schon komplett bei wartender Figur, ueberspringen",
                        waitingPart.PartNumber);
                    continue;
                }

                waitingPart.QuantityCollected++;
                assignedCount++;

                // Komplettierungs-Check ueber Effective (physisch + BL-reserviert).
                var allComplete = waitingPart.TrackedMinifig.RequiredParts.All(p =>
                    (p.Id == waitingPart.Id ? waitingPart.QuantityCollected : p.QuantityCollected)
                    + p.QuantityReservedFromBl >= p.QuantityNeeded);
                if (allComplete && waitingPart.TrackedMinifig.Status != TrackedMinifigStatus.Complete)
                {
                    waitingPart.TrackedMinifig.Status = TrackedMinifigStatus.Complete;
                    waitingPart.TrackedMinifig.CompletedAt = now;
                    completedNames.Add(waitingPart.TrackedMinifig.Name);
                    Log.Information("UX X.25: Wartende Figur '{Name}' wurde durch Zerlege-Zuordnung komplett",
                        waitingPart.TrackedMinifig.Name);
                }

                ctx.ScanEvents.Add(new ScanEvent
                {
                    Timestamp = now,
                    Type = ScanType.PartScan,
                    RecognizedId = waitingPart.PartNumber,
                    ResultDescription = $"Teil aus Zerlegung von '{originName}' -> wartende Figur " +
                        $"'{waitingPart.TrackedMinifig.Name}' ({waitingPart.QuantityCollected}/{waitingPart.QuantityNeeded})",
                    WasUndone = false
                });
                continue;
            }

            // ===== Standard: FloatingPart-Pfad =====
            var binId = c.TargetBinId!.Value;

            // Bug B Fix (UX X.28): wenn das Ziel-Fach als "frei" markiert war,
            // jetzt wieder als belegt markieren. Bin existiert-Check inklusive.
            // v0.1.23: inkl. TrackedMinifigs+RequiredParts fuer Strict-Mode-Bypass.
            var targetBin = await ctx.StorageBins
                .Include(b => b.TrackedMinifigs)
                    .ThenInclude(m => m.RequiredParts)
                .FirstOrDefaultAsync(b => b.Id == binId, ct);
            if (targetBin == null)
            {
                Log.Warning("DismantleAsync: TargetBinId={BinId} nicht gefunden, Teil {Part} wird uebersprungen",
                    binId, part.PartNumber);
                continue;
            }
            // v0.1.23 Strict-Mode (S2): Floating darf nur in Empty/Floating
            // oder Waiting-mit-Match.
            BinKindGuard.EnsureBinAcceptsFloatingPart(
                targetBin, part.PartNumber, part.ColorId,
                $"Teil aus Zerlegung in Fach '{targetBin.Label}' ablegen");
            if (targetBin.FreedAt != null)
            {
                Log.Information("Fach '{Label}' war als frei markiert (seit {FreedAt}) - wird durch Zerlege-Teil wieder belegt",
                    targetBin.Label, targetBin.FreedAt);
                targetBin.FreedAt = null;
            }

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
                // Pre-Tag-Fix v0.1.19-beta.7: BrickognizeCategory IMMER setzen
                // (Heuristik via PartName-Praefix, Fallback "Unbekannt").
                // Sonst greift die Default-Regel "max 1 PartId pro Kategorie
                // pro Bin" nicht und gleichkategorische Teile (z.B. zwei
                // verschiedene Koepfe) wuerden im selben Fach landen.
                var derivedCategory = _categoryMapping?.DeriveCategoryFromPartName(part.PartName)
                    ?? ICategoryBinMappingService.UnknownCategory;
                ctx.FloatingParts.Add(new FloatingPart
                {
                    PartNumber = part.PartNumber,
                    ColorId = part.ColorId,
                    ColorName = part.ColorName,
                    PartName = part.PartName,
                    Quantity = qty,
                    StorageBinId = binId,
                    BrickognizeCategory = derivedCategory,
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
                ? $"Figur '{originName}' zerlegt - {totalQty} Einzelteil(e) in Pool uebernommen"
                : $"Figur '{originName}' zerlegt (keine Teile uebernommen)",
            WasUndone = false
        });

        // Phase 6: Tagesstatistik fuer "zerlegt" hochzaehlen.
        await IncrementDailyStatAsync(ctx, s => s.MinifigsDismantledCount++, ct);

        // v0.1.23: Bin-Liste fuer Recalc sammeln. Quell-Bin (Figur war drin)
        // PLUS alle Ziel-Bins aus den FloatingPart-Choices PLUS Bins der
        // wartenden Figuren bei Direkt-Zuordnung (die aendern ihren Status).
        var affectedBins = new List<int?>
        {
            minifig.StorageBinId
        };
        affectedBins.AddRange(choiceList
            .Where(c => c.IsKept && c.TargetBinId.HasValue)
            .Select(c => (int?)c.TargetBinId!.Value));
        var assignPartIds = choiceList
            .Where(c => c.IsKept && c.AssignToTrackedMinifigPartId.HasValue)
            .Select(c => c.AssignToTrackedMinifigPartId!.Value)
            .ToList();
        if (assignPartIds.Count > 0)
        {
            var assignedMinifigBins = await ctx.TrackedMinifigParts
                .Where(p => assignPartIds.Contains(p.Id))
                .Select(p => p.TrackedMinifig.StorageBinId)
                .ToListAsync(ct);
            affectedBins.AddRange(assignedMinifigBins);
        }

        // v0.1.24-beta.10 V1: BL-Reservierungen freigeben BEVOR die Figur
        // weg ist (siehe DeleteAsync). Beim Zerlegen besonders relevant:
        // User-Pfad fuer "wartende Figur aufgeben" geht oft ueber Dismantle.
        await ReleaseBlReservationsForAsync(new[] { trackedMinifigId }, ct);

        // Figur und ihre RequiredParts loeschen (Cascade entfernt RequiredParts).
        ctx.TrackedMinifigs.Remove(minifig);
        await ctx.SaveChangesAsync(ct);

        // v0.1.23: Bin.Kind neu berechnen nach Zerlegen.
        await RecalcBinKindsAsync(affectedBins, ct);

        Log.Information(
            "Figur '{Name}' (Id={Id}) zerlegt: {Count} Teile-Eintraege ({Qty} Stueck) in Pool, Figur geloescht",
            originName, trackedMinifigId, createdCount, totalQty);

        DataChanged?.Invoke(this, EventArgs.Empty);

        return new DismantleResult
        {
            Success = true,
            CreatedFloatingParts = createdCount,
            TotalPartsTransferred = totalQty,
            AssignedToWaitingCount = assignedCount,
            CompletedMinifigNames = completedNames
        };
    }

    public async Task<int> CleanupOldDismantledMinifigsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var stale = await ctx.TrackedMinifigs
            .Where(m => m.Status == TrackedMinifigStatus.Dismantled)
            .ToListAsync(ct);
        if (stale.Count == 0) return 0;

        // v0.1.24-beta.10 V1: BL-Reservierungen freigeben. DISMANTLED-Figuren
        // sollten normal keine offenen Reservierungen mehr haben (Dismantle
        // ruft Release schon), aber defensiv pro Bulk auch hier.
        await ReleaseBlReservationsForAsync(stale.Select(m => m.Id), ct);

        var affectedBins = stale.Select(m => m.StorageBinId).ToList();
        ctx.TrackedMinifigs.RemoveRange(stale);
        await ctx.SaveChangesAsync(ct);
        await RecalcBinKindsAsync(affectedBins, ct);
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

        // v0.1.24-beta.10 V1: BL-Reservierungen freigeben.
        await ReleaseBlReservationsForAsync(toDelete.Select(m => m.Id), ct);

        var affectedBins = toDelete.Select(m => m.StorageBinId).ToList();
        ctx.TrackedMinifigs.RemoveRange(toDelete);
        await ctx.SaveChangesAsync(ct);
        await RecalcBinKindsAsync(affectedBins, ct);
        Log.Information("Cleanup: {Count} Pseudo-1/1-Figuren geloescht", toDelete.Count);
        DataChanged?.Invoke(this, EventArgs.Empty);
        return toDelete.Count;
    }

    public async Task<int> RemoveExportedMinifigsAsync(
        IEnumerable<int> minifigIds, CancellationToken ct = default)
    {
        var ids = minifigIds?.ToList() ?? new List<int>();
        if (ids.Count == 0) return 0;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // FloatingParts mit Origin auf den zu loeschenden Figuren entkoppeln.
        // Sie bleiben als lose Teile im Pool bestehen.
        var origins = await ctx.FloatingParts
            .Where(fp => fp.OriginMinifigId != null
                      && ids.Contains(fp.OriginMinifigId.Value))
            .ToListAsync(ct);
        foreach (var fp in origins) fp.OriginMinifigId = null;

        var minifigs = await ctx.TrackedMinifigs
            .Where(m => ids.Contains(m.Id))
            .ToListAsync(ct);

        var removed = minifigs.Count;
        if (removed == 0)
        {
            // Trotzdem Floating-Origins committen falls welche entkoppelt wurden.
            if (origins.Count > 0) await ctx.SaveChangesAsync(ct);
            return 0;
        }

        var affectedBins = minifigs.Select(m => m.StorageBinId).ToList();

        // v0.1.24-beta.10 V1: BL-Reservierungen freigeben. Beim BSX-Export
        // ist die Semantik temporaer suboptimal: die Reservierungen wurden
        // implizit "verkauft" (User packt die Figur fuer den BL-Kunden) —
        // die Lot.Quantity-Reduktion macht aber erst Phase 4. Aktuell
        // released V1 nur die ReservedQuantity ohne Quantity zu kuerzen.
        // Naechster Sync holt den aktuellen Stand frisch von BL.
        await ReleaseBlReservationsForAsync(ids, ct);

        ctx.TrackedMinifigs.RemoveRange(minifigs);

        // Audit-Trail (ScanEvent ist die einzige Event-Tabelle die wir haben).
        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.MinifigScan,
            ResultDescription = $"BSX-Export: {removed} Figur(en) entfernt",
            WasUndone = false
        });

        await ctx.SaveChangesAsync(ct);
        await RecalcBinKindsAsync(affectedBins, ct);
        Log.Information(
            "BSX-Export-Cleanup: {Count} Figuren geloescht, {Origins} Floating-Parts entkoppelt",
            removed, origins.Count);
        DataChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public async Task<int> RemoveExportedFloatingPartsAsync(
        IEnumerable<int> floatingPartIds,
        CancellationToken ct = default)
    {
        var ids = floatingPartIds?.ToList() ?? new List<int>();
        if (ids.Count == 0) return 0;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Mit StorageBin laden, damit wir das Bin-Label fuer den Audit-Trail haben.
        var floats = await ctx.FloatingParts
            .Include(p => p.StorageBin)
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(ct);

        if (floats.Count == 0) return 0;

        // Pro entferntem Eintrag ein ScanEvent. ResultDescription enthaelt
        // Quell-Bin-Label, Part-No, Color-Id und Quantity - so bleibt
        // nachvollziehbar wo das Teil herkam, auch nachdem das Fach evtl.
        // wieder freigegeben wurde.
        foreach (var fp in floats)
        {
            ct.ThrowIfCancellationRequested();
            var binLabel = fp.StorageBin?.Label ?? "(?)";
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = DateTime.UtcNow,
                Type = ScanType.FloatingPartExported,
                RecognizedId = fp.PartNumber,
                ResultDescription =
                    $"BSX-Export: Einzelteil aus '{binLabel}' entfernt - " +
                    $"{fp.PartNumber}, Farbe {fp.ColorId} ({fp.ColorName}), " +
                    $"Quantity {fp.Quantity}",
                WasUndone = false
            });
        }

        var affectedBins = floats.Select(fp => (int?)fp.StorageBinId).ToList();
        ctx.FloatingParts.RemoveRange(floats);
        await ctx.SaveChangesAsync(ct);
        await RecalcBinKindsAsync(affectedBins, ct);

        Log.Information(
            "BSX-Export-Cleanup: {Count} FloatingPart-Eintrag(e) entfernt",
            floats.Count);
        DataChanged?.Invoke(this, EventArgs.Empty);
        return floats.Count;
    }

    public async Task<bool> CheckAndMarkCompleteAsync(int minifigId,
        CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var m = await ctx.TrackedMinifigs
            .Include(x => x.RequiredParts)
            .FirstOrDefaultAsync(x => x.Id == minifigId, ct);
        if (m == null) return false;
        if (m.Status != TrackedMinifigStatus.Waiting) return false;
        if (m.RequiredParts.Count == 0) return false;

        // v0.1.24-beta.8 Phase 3: Komplett-Check ueber Effective.
        var allComplete = m.RequiredParts
            .All(p => (p.QuantityCollected + p.QuantityReservedFromBl) >= p.QuantityNeeded);
        if (!allComplete) return false;

        m.Status = TrackedMinifigStatus.Complete;
        m.CompletedAt = DateTime.UtcNow;

        await IncrementDailyStatAsync(ctx, s => s.MinifigsCompletedCount++, ct);
        await ctx.SaveChangesAsync(ct);

        // v0.1.23 S23: Status-Wechsel Waiting->Complete kann Bin.Kind aendern
        // (letzte wartende komplettiert -> Bin wird Complete).
        await RecalcBinKindsAsync(new[] { m.StorageBinId }, ct);

        Log.Information("Figur '{Name}' (Id={Id}) als COMPLETE markiert",
            m.Name, m.Id);
        DataChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> ReopenAsync(int minifigId, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var m = await ctx.TrackedMinifigs
            .FirstOrDefaultAsync(x => x.Id == minifigId, ct);
        if (m == null) return false;
        if (m.Status != TrackedMinifigStatus.Complete) return false;

        m.Status = TrackedMinifigStatus.Waiting;
        m.CompletedAt = null;
        // DailyStats absichtlich NICHT veraendern - der Tag der Komplettierung
        // bleibt historisch korrekt gezaehlt.
        await ctx.SaveChangesAsync(ct);

        // v0.1.23 S24: Status-Wechsel Complete->Waiting setzt Bin.Kind auf Waiting.
        await RecalcBinKindsAsync(new[] { m.StorageBinId }, ct);

        Log.Information("Figur '{Name}' (Id={Id}) wieder auf Waiting gesetzt",
            m.Name, m.Id);
        DataChanged?.Invoke(this, EventArgs.Empty);
        return true;
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

        // Bin existiert? Inkl. TrackedMinifigs+RequiredParts fuer Strict-Mode-Check.
        var bin = await ctx.StorageBins
            .Include(b => b.TrackedMinifigs)
                .ThenInclude(m => m.RequiredParts)
            .FirstOrDefaultAsync(b => b.Id == input.StorageBinId, ct);
        if (bin == null)
            throw new InvalidOperationException($"Lagerfach {input.StorageBinId} existiert nicht.");

        // v0.1.23 Strict-Mode (S1): typ-Vertraeglichkeit pruefen.
        BinKindGuard.EnsureBinAcceptsMinifig(bin, $"Figur '{input.Name}' anlegen");

        // Bug B Fix (UX X.28): wenn das Fach als "frei" markiert war, jetzt wieder als belegt markieren.
        if (bin.FreedAt != null)
        {
            Log.Information("Fach '{Label}' war als frei markiert (seit {FreedAt}) - wird durch neue Figur wieder belegt",
                bin.Label, bin.FreedAt);
            bin.FreedAt = null;
        }

        // 1) TrackedMinifig + RequiredParts anlegen
        var minifig = new TrackedMinifig
        {
            // CLAUDE.md sieht FigNum (Rebrickable) und BricklinkId vor. Wir sind aktuell
            // BL-first ohne Rebrickable-Resolve - also legen wir die BL-ID auch in FigNum
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
                // BL-first: BricklinkPartNo + BricklinkColorId aus dem Input
                // landen direkt als (PartNumber, ColorId) in der Entity. Beides
                // sind BL-IDs - der spaetere BSX-Export braucht keine Konvertierung.
                PartNumber = p.BricklinkPartNo,
                ColorId = p.BricklinkColorId,
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
        // v0.1.23: Bin-Ids aus Reverse-Match sammeln fuer RecalcKind am Ende.
        var reverseMatchBinIds = new HashSet<int>();
        // UX X.32 Block C (v0.1.19): pro Konsum-Vorgang ein Info-Eintrag
        // fuers Sammel-Popup. Aggregiert pro (PartNo, ColorId, BinId) -
        // wenn ein Required-Part aus 2 Faechern bedient wird, zwei Eintraege.
        var consumed = new List<ConsumedFloatingPartInfo>();

        // v0.1.24-beta.1 Phase 2a-Polish: Reverse-Match nur wenn der Aufrufer
        // ihn explizit anfordert (Default true => BuildSuggestion-Pfad bleibt
        // unveraendert). MinifigDetailView/PersistPending setzt false und liefert
        // QuantityCollected schon aus expliziten "Aus Fach"-Klicks vorbefuellt.
        if (input.ConsumeFloatingParts)
        {
            // v0.1.24-beta.11: Per-Part-Skip-Liste fuer User die explizit
            // "BL-Shop" gewaehlt haben — interne Floats werden NICHT mehr
            // angetastet. Vergleich (PartNo, ColorId) als HashSet-Lookup.
            var skipKeys = input.RequiredParts
                .Where(p => p.SkipReverseMatch)
                .Select(p => (p.BricklinkPartNo, p.BricklinkColorId))
                .ToHashSet();

            Log.Information(
                "PersistAndStore Reverse-Match: Minifig='{Name}' (BL:{Bl}), {Cnt} RequiredParts, {Skip} via Skip-Flag uebersprungen",
                minifig.Name, minifig.BricklinkId, minifig.RequiredParts.Count, skipKeys.Count);

            foreach (var required in minifig.RequiredParts)
            {
                if (skipKeys.Contains((required.PartNumber, required.ColorId)))
                {
                    Log.Information(
                        "  RequiredPart {Part}/{Color} uebersprungen (SkipReverseMatch=true - User-Wahl BL-Shop)",
                        required.PartNumber, required.ColorId);
                    continue;
                }
                // Alle passenden FloatingParts (egal in welchem Fach), aelteste zuerst.
                // Include(StorageBin) damit wir das Fach-Label fuer das Sammel-Popup
                // ohne Extra-Query haben.
                var candidates = await ctx.FloatingParts
                    .Include(fp => fp.StorageBin)
                    .Where(fp => fp.PartNumber == required.PartNumber
                              && fp.ColorId == required.ColorId)
                    .OrderBy(fp => fp.AddedAt)
                    .ToListAsync(ct);

                Log.Information(
                    "  RequiredPart {Part}/{Color} (need={Need}, have={Have}): {Cands} FloatingPart-Kandidaten",
                    required.PartNumber, required.ColorId,
                    required.QuantityNeeded, required.QuantityCollected,
                    candidates.Count);
                foreach (var c in candidates)
                {
                    Log.Information("    -> FP Id={Id} '{Name}' Qty={Qty} Bin='{Bin}'",
                        c.Id, c.PartName, c.Quantity, c.StorageBin?.Label ?? "?");
                }

                foreach (var fp in candidates)
                {
                    var stillNeeded = required.QuantityNeeded - required.QuantityCollected;
                    if (stillNeeded <= 0) break;

                    var take = Math.Min(stillNeeded, fp.Quantity);
                    required.QuantityCollected += take;
                    fp.Quantity -= take;
                    reverseMatched += take;

                    Log.Information("    KONSUMIERT: nimm {Take} aus FP Id={Id} (Bin '{Bin}'); FP-Rest={Rest}, Required jetzt {Have}/{Need}",
                        take, fp.Id, fp.StorageBin?.Label ?? "?", fp.Quantity,
                        required.QuantityCollected, required.QuantityNeeded);

                    consumed.Add(new ConsumedFloatingPartInfo
                    {
                        BlPartNo = required.PartNumber,
                        BlColorId = required.ColorId,
                        PartName = required.PartName,
                        ColorName = required.ColorName,
                        Quantity = take,
                        SourceBinLabel = fp.StorageBin?.Label ?? string.Empty
                    });

                    // v0.1.23: Quell-Bin merken fuer RecalcKind.
                    reverseMatchBinIds.Add(fp.StorageBinId);

                    if (fp.Quantity <= 0)
                    {
                        ctx.FloatingParts.Remove(fp);
                    }
                }

                if (required.QuantityCollected >= required.QuantityNeeded)
                    completedParts++;
            }
        }
        else
        {
            // Flag=false: Service konsumiert nicht selbst. Trotzdem
            // completedParts korrekt zaehlen, damit IsFullyComplete
            // anhand der vom UI vorbefuellten QuantityCollected korrekt
            // berechnet werden kann.
            Log.Information(
                "PersistAndStore ohne Reverse-Match (ConsumeFloatingParts=false): Minifig='{Name}' (BL:{Bl})",
                minifig.Name, minifig.BricklinkId);

            foreach (var required in minifig.RequiredParts)
            {
                if (required.QuantityCollected >= required.QuantityNeeded)
                    completedParts++;
            }
        }

        // v0.1.24-beta.8 Phase 3: Komplett-Check ueber Effective
        // (physisch + BL-reserviert).
        var isComplete = minifig.RequiredParts.All(p =>
                             (p.QuantityCollected + p.QuantityReservedFromBl) >= p.QuantityNeeded)
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

        // Phase 6: DailyStats hochzaehlen.
        // Scan zaehlt immer; Komplettierung nur wenn die Figur durch Reverse-Match
        // bei Speicherung schon komplett ist (CheckAndMarkCompleteAsync zaehlt
        // den manuellen Pfad ueber den Pending-Klick).
        await IncrementDailyStatAsync(ctx, s =>
        {
            s.ScanCount++;
            if (isComplete) s.MinifigsCompletedCount++;
        }, ct);

        await ctx.SaveChangesAsync(ct);

        // v0.1.23: Bin.Kind neu berechnen fuer Ziel-Bin + alle Quell-Bins
        // aus dem Reverse-Match.
        var binsToRecalc = new List<int?> { input.StorageBinId };
        binsToRecalc.AddRange(reverseMatchBinIds.Select(id => (int?)id));
        await RecalcBinKindsAsync(binsToRecalc, ct);

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
            IsFullyComplete = isComplete,
            ConsumedFloatingParts = consumed
        };
    }

    // ====================================================================
    // UX X.29 Block C (v0.1.16): Bulk-Operationen fuer die Lagerliste
    // ====================================================================

    public async Task<(int Minifigs, int FloatingParts)> DeleteSelectionAsync(
        IEnumerable<int> minifigIds,
        IEnumerable<int> floatingPartIds,
        CancellationToken ct = default)
    {
        var mIds = minifigIds?.ToList() ?? new List<int>();
        var fIds = floatingPartIds?.ToList() ?? new List<int>();
        if (mIds.Count == 0 && fIds.Count == 0) return (0, 0);

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Minifigs laden inkl. RequiredParts fuer den Snapshot.
        var minifigs = await ctx.TrackedMinifigs
            .Include(m => m.RequiredParts)
            .Where(m => mIds.Contains(m.Id))
            .ToListAsync(ct);

        // FloatingParts (mit OriginMinifigId-Verbindung zu loeschenden Minifigs entkoppeln).
        var floats = await ctx.FloatingParts
            .Where(p => fIds.Contains(p.Id))
            .ToListAsync(ct);

        // OriginMinifigId-Verbindungen kappen, damit Cascade-Delete der Minifigs
        // nicht versucht die FloatingParts mit zu loeschen.
        var minifigIdsToDelete = minifigs.Select(m => m.Id).ToHashSet();
        if (minifigIdsToDelete.Count > 0)
        {
            var origins = await ctx.FloatingParts
                .Where(fp => fp.OriginMinifigId != null
                          && minifigIdsToDelete.Contains(fp.OriginMinifigId.Value))
                .ToListAsync(ct);
            foreach (var fp in origins) fp.OriginMinifigId = null;
        }

        var now = DateTime.UtcNow;

        // ScanEvent + UndoData pro Minifig.
        foreach (var minifig in minifigs)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = new UndoSnapshotMinifigDelete
            {
                OriginalMinifigId = minifig.Id,
                BricklinkId = minifig.BricklinkId ?? string.Empty,
                FigNum = minifig.FigNum,
                Name = minifig.Name,
                ImageUrl = minifig.ImageUrl,
                LocalImagePath = minifig.LocalImagePath,
                UserNotes = minifig.UserNotes,
                CreatedAt = minifig.CreatedAt,
                CompletedAt = minifig.CompletedAt,
                Status = minifig.Status.ToString(),
                StorageBinId = minifig.StorageBinId,
                RequiredParts = minifig.RequiredParts.Select(p => new UndoSnapshotPart
                {
                    PartNumber = p.PartNumber,
                    ColorId = p.ColorId,
                    PartName = p.PartName,
                    ColorName = p.ColorName,
                    QuantityNeeded = p.QuantityNeeded,
                    QuantityCollected = p.QuantityCollected
                }).ToList()
            };
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = now,
                Type = ScanType.Delete,
                RecognizedId = minifig.BricklinkId ?? minifig.FigNum,
                ResultDescription = $"Bulk-Delete: Figur '{minifig.Name}' geloescht",
                WasUndone = false,
                UndoData = System.Text.Json.JsonSerializer.Serialize(snapshot)
            });
        }

        // ScanEvent + UndoData pro FloatingPart.
        foreach (var fp in floats)
        {
            ct.ThrowIfCancellationRequested();
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
                Timestamp = now,
                Type = ScanType.Delete,
                RecognizedId = fp.PartNumber,
                ResultDescription = $"Bulk-Delete: Einzelteil '{fp.PartName}' (BL:{fp.PartNumber}/{fp.ColorId}) x{fp.Quantity} geloescht",
                WasUndone = false,
                UndoData = System.Text.Json.JsonSerializer.Serialize(snapshot)
            });
        }

        // v0.1.23: betroffene Bins aus beiden Quellen sammeln vor dem Remove.
        var affectedBins = minifigs.Select(m => m.StorageBinId)
            .Concat(floats.Select(fp => (int?)fp.StorageBinId))
            .ToList();

        // v0.1.24-beta.10 V1: BL-Reservierungen freigeben fuer ALLE
        // Bulk-Delete-Minifigs. Eigene Service-Transaction, dann hier
        // weiter mit Remove.
        if (minifigs.Count > 0)
            await ReleaseBlReservationsForAsync(minifigs.Select(m => m.Id), ct);

        ctx.TrackedMinifigs.RemoveRange(minifigs);
        ctx.FloatingParts.RemoveRange(floats);
        await ctx.SaveChangesAsync(ct);
        await RecalcBinKindsAsync(affectedBins, ct);

        Log.Information("Bulk-Delete: {M} Minifigs, {F} FloatingParts geloescht",
            minifigs.Count, floats.Count);
        DataChanged?.Invoke(this, EventArgs.Empty);
        return (minifigs.Count, floats.Count);
    }

    public async Task<(int Minifigs, int FloatingParts)> MoveSelectionAsync(
        IEnumerable<int> minifigIds,
        IEnumerable<int> floatingPartIds,
        int targetBinId,
        CancellationToken ct = default)
    {
        var mIds = minifigIds?.ToList() ?? new List<int>();
        var fIds = floatingPartIds?.ToList() ?? new List<int>();
        if (mIds.Count == 0 && fIds.Count == 0) return (0, 0);

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Ziel-Bin existiert? Sonst werfen - kein Halbzustand.
        // v0.1.23 Strict-Mode: inkl. Minifigs+RequiredParts fuer Floating-Bypass.
        var targetBin = await ctx.StorageBins
            .Include(b => b.TrackedMinifigs)
                .ThenInclude(m => m.RequiredParts)
            .FirstOrDefaultAsync(b => b.Id == targetBinId, ct);
        if (targetBin == null)
            throw new InvalidOperationException($"Lagerfach {targetBinId} existiert nicht.");

        var minifigs = await ctx.TrackedMinifigs
            .Where(m => mIds.Contains(m.Id))
            .ToListAsync(ct);
        var floats = await ctx.FloatingParts
            .Where(p => fIds.Contains(p.Id))
            .ToListAsync(ct);

        // v0.1.23 Strict-Mode (S7): pro Item-Typ pruefen ob Ziel-Bin akzeptiert.
        if (minifigs.Any(m => m.StorageBinId != targetBinId))
            BinKindGuard.EnsureBinAcceptsMinifig(targetBin, "Figuren in Bulk verschieben");
        foreach (var fp in floats)
        {
            if (fp.StorageBinId == targetBinId) continue;
            BinKindGuard.EnsureBinAcceptsFloatingPart(
                targetBin, fp.PartNumber, fp.ColorId, "Einzelteil in Bulk verschieben");
        }

        // Bug-B-Fix-Konvention: wenn Ziel-Bin als frei markiert war, reaktivieren.
        if (targetBin.FreedAt != null)
        {
            Log.Information("Fach '{Label}' war als frei markiert (seit {FreedAt}) - wird durch Bulk-Verschieben wieder belegt",
                targetBin.Label, targetBin.FreedAt);
            targetBin.FreedAt = null;
        }

        // v0.1.23: betroffene Bins (Quell + Ziel) sammeln vor dem Move.
        var affectedBins = minifigs.Select(m => m.StorageBinId)
            .Concat(floats.Select(fp => (int?)fp.StorageBinId))
            .Concat(new[] { (int?)targetBinId })
            .ToList();

        var now = DateTime.UtcNow;

        foreach (var m in minifigs)
        {
            if (m.StorageBinId == targetBinId) continue; // No-op
            var snap = new UndoSnapshotMove
            {
                MinifigId = m.Id,
                OldStorageBinId = m.StorageBinId,
                NewStorageBinId = targetBinId
            };
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = now,
                Type = ScanType.Move,
                RecognizedId = m.BricklinkId ?? m.FigNum,
                ResultDescription = $"Bulk-Move: Figur '{m.Name}' nach '{targetBin.Label}' verschoben",
                WasUndone = false,
                UndoData = System.Text.Json.JsonSerializer.Serialize(snap)
            });
            m.StorageBinId = targetBinId;
        }

        // FloatingParts: Move mit eigenem Snapshot-Typ UndoSnapshotFloatingMove
        // (UX X.30 / v0.1.17). UndoService unterscheidet jetzt Minifig- vs.
        // FloatingPart-Move-Snapshots und stellt jeweils die alte StorageBinId
        // wieder her.
        var floatingMoved = 0;
        foreach (var fp in floats)
        {
            if (fp.StorageBinId == targetBinId) continue; // No-op
            var fpSnap = new UndoSnapshotFloatingMove
            {
                FloatingPartId = fp.Id,
                OldStorageBinId = fp.StorageBinId,
                NewStorageBinId = targetBinId
            };
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = now,
                Type = ScanType.Move,
                RecognizedId = fp.PartNumber,
                ResultDescription = $"Bulk-Move: Einzelteil '{fp.PartName}' (BL:{fp.PartNumber}/{fp.ColorId}) nach '{targetBin.Label}' verschoben",
                WasUndone = false,
                UndoData = System.Text.Json.JsonSerializer.Serialize(fpSnap)
            });
            fp.StorageBinId = targetBinId;
            floatingMoved++;
        }

        await ctx.SaveChangesAsync(ct);
        await RecalcBinKindsAsync(affectedBins, ct);

        Log.Information("Bulk-Move: {M} Minifigs, {F} FloatingParts nach '{Bin}' verschoben",
            minifigs.Count, floatingMoved, targetBin.Label);
        DataChanged?.Invoke(this, EventArgs.Empty);
        return (minifigs.Count, floatingMoved);
    }
}
