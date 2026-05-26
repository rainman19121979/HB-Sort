using HBSort.Core.Database;
using HBSort.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// EF-Core-Implementierung des Lagerfach-Service. Nutzt einen IDbContextFactory&lt;UserDataContext&gt;
/// damit jede Operation einen frischen Context bekommt - einfache Concurrency.
/// </summary>
public class StorageBinService : IStorageBinService
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IBlCacheRepository? _blCache;

    /// <summary>
    /// UX X.32 v0.1.19-beta.4: <paramref name="blCache"/> ist optional fuer
    /// Backwards-Compat in Tests die das Repository nicht mocken muessen.
    /// Wenn null: <see cref="SuggestBinForFloatingPartAsync"/> mit
    /// maxCategoriesPerBin &gt; 1 verhaelt sich wie bei Limit=1 (kein
    /// Kategorie-Mischen, weil ohne Cache keine Kategorie-Lookup-Daten).
    /// </summary>
    public StorageBinService(
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCacheRepository? blCache = null)
    {
        _ctxFactory = ctxFactory;
        _blCache = blCache;
    }

    // ---- Reads ----

    public async Task<List<StorageBin>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        return await ctx.StorageBins
            .AsNoTracking()
            .OrderBy(b => b.Label)
            .ToListAsync(ct);
    }

    public async Task<StorageBin?> GetByLabelAsync(string label, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        return await ctx.StorageBins
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Label == label, ct);
    }

    public async Task<StorageBin?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        return await ctx.StorageBins.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    // v0.1.23 (A10): GetFreeAsync / GetOccupiedAsync / GetNextFreeAsync entfernt.
    // GetEligibleBinsAsync + Suggest*-Methoden sind die korrekten Default-Pfade.

    // ====================================================================
    // UX X.31 (v0.1.18): Konsistente Bin-Vorschlaege pro Item-Typ.
    // v0.1.23: Nutzen die persistierte StorageBin.Kind-Spalte (Index
    // IX_StorageBins_Kind) statt aus den Inhalten zu rechnen.
    // Reifungspfad: Wartend+Complete-Bin = Waiting-Kind.
    // ====================================================================

    public async Task<StorageBin?> SuggestBinForWaitingMinifigAsync(
        int maxWaitingLimit = 1,
        CancellationToken ct = default)
    {
        // Defensive Clamping - UI sollte 1..999 garantieren, aber wir
        // verlassen uns nicht drauf. Werte unter 1 verhalten sich wie 1.
        if (maxWaitingLimit < 1) maxWaitingLimit = 1;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // v0.1.23: Bin.Kind direkt aus DB. Wartende Figur darf in:
        // Empty (immer), Waiting (Stapel-Wachstum unter Limit), NICHT in
        // Floating (Strict-Mode), NICHT in Complete (Default-Pfad
        // konservativ - User-Erwartung "neues Fach"; Reifung von Wartend
        // zu Complete passiert nur via Status-Wechsel).

        // 1) Bei Limit > 1: Waiting-Bin mit Platz bevorzugen.
        if (maxWaitingLimit > 1)
        {
            var stackCandidate = await ctx.StorageBins
                .AsNoTracking()
                .Where(b => b.Kind == StorageBinKind.Waiting && true)
                .Select(b => new
                {
                    Bin = b,
                    WaitingCount = b.TrackedMinifigs.Count(m => m.Status == TrackedMinifigStatus.Waiting)
                })
                .Where(x => x.WaitingCount < maxWaitingLimit)
                .OrderByDescending(x => x.WaitingCount)
                .ThenBy(x => x.Bin.Label)
                .FirstOrDefaultAsync(ct);
            if (stackCandidate != null) return stackCandidate.Bin;
        }

        // 2) Empty-Fach (Default-Vorschlag).
        return await ctx.StorageBins
            .AsNoTracking()
            .Where(b => b.Kind == StorageBinKind.Empty && true)
            .OrderBy(b => b.Label)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<StorageBin?> SuggestBinForCompleteMinifigAsync(int maxCompleteLimit, CancellationToken ct = default)
    {
        if (maxCompleteLimit < 1) maxCompleteLimit = 1;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // v0.1.23: Complete darf in Empty / Complete (Stapel) / Waiting
        // (Reifungs-Mix wartend+complete). NICHT in Floating-Bin.

        // 1) Bin mit bestehenden Complete-Figuren unter Limit (Kind=Complete
        //    oder Mix wartend+complete=Kind=Waiting). Stapel-Wachstum bevorzugt.
        var stackCandidate = await ctx.StorageBins
            .AsNoTracking()
            .Where(b => (b.Kind == StorageBinKind.Complete || b.Kind == StorageBinKind.Waiting)
                && true)
            .Select(b => new
            {
                Bin = b,
                CompleteCount = b.TrackedMinifigs.Count(m => m.Status == TrackedMinifigStatus.Complete)
            })
            .Where(x => x.CompleteCount > 0 && x.CompleteCount < maxCompleteLimit)
            .OrderByDescending(x => x.CompleteCount)
            .ThenBy(x => x.Bin.Label)
            .FirstOrDefaultAsync(ct);
        if (stackCandidate != null) return stackCandidate.Bin;

        // 2) Empty-Fach (Fallback).
        return await ctx.StorageBins
            .AsNoTracking()
            .Where(b => b.Kind == StorageBinKind.Empty && true)
            .OrderBy(b => b.Label)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<StorageBin?> SuggestBinForFloatingPartAsync(
        string blPartNo, int blColorId,
        string brickognizeCategory,
        int? excludeMinifigId = null,
        Dictionary<string, int>? userMapping = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blPartNo))
            throw new ArgumentException("BL-Part-No darf nicht leer sein.", nameof(blPartNo));

        // UX X.33 Block N + Pre-Tag-Fix (v0.1.19-beta.7): leere/null Kategorie
        // wird als Pseudo-Kategorie "Unbekannt" behandelt - Default-Regel
        // "max 1 PartId pro Kategorie pro Bin" greift damit auch fuer
        // Bestand-FloatingParts (Migration A hat Spalte mit null gefuellt)
        // und fuer Items wo die Heuristik kein Match findet (z.B.
        // "Minifigure Air Tanks" ohne Komma).
        var category = string.IsNullOrWhiteSpace(brickognizeCategory)
            ? ICategoryBinMappingService.UnknownCategory
            : brickognizeCategory;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // ============================================================
        // 1) Stapel-Match: Bin in dem das gleiche Teil (PartNo + ColorId)
        //    schon liegt. FIFO nach AddedAt. Hat absoluten Vorrang -
        //    gleiche Items stapeln sich immer.
        // ============================================================
        var existingFp = await ctx.FloatingParts
            .AsNoTracking()
            .Where(p => p.PartNumber == blPartNo && p.ColorId == blColorId)
            .OrderBy(p => p.AddedAt)
            .FirstOrDefaultAsync(ct);
        if (existingFp != null)
        {
            var bin = await ctx.StorageBins
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == existingFp.StorageBinId, ct);
            if (bin != null) return bin;
        }

        // ============================================================
        // 2) User-Mapping: wenn die Kategorie im
        //    AppSettings.CategoryToBinMapping einem Bin zugewiesen ist,
        //    wird das genommen - selbst wenn dort schon andere PartIds
        //    der gleichen Kategorie liegen (User hat das so gewollt).
        // ============================================================
        if (userMapping != null
            && !string.IsNullOrEmpty(category)
            && userMapping.TryGetValue(category, out var mappedBinId))
        {
            var mappedBin = await ctx.StorageBins
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == mappedBinId, ct);
            if (mappedBin != null) return mappedBin;
            // Mapping zeigt auf geloeschtes Bin -> Fall-through auf Default-Regel.
        }

        // ============================================================
        // 3) Default-Regel: erstes Bin OHNE einen FloatingPart der gleichen
        //    Brickognize-Kategorie aber anderer PartNo. Verschiedene
        //    Kategorien duerfen sich ein Bin teilen, gleiche Kategorie
        //    nicht (sonst landen Helm-A + Helm-B zusammen -> doppelter
        //    Stapel). Sortiert nach Label fuer Stabilitaet.
        // ============================================================
        // v0.1.24-beta.9 Hotfix: allowedKinds ist strikt [Empty, Floating] -
        // unabhaengig von excludeMinifigId. Damit ist Suggest exakt
        // symmetrisch zu GetEligibleBinsWithCountsAsync(FloatingTarget,
        // partNo=null) - die UI-Combobox (AvailableBins) zeigt ohnehin
        // weder Complete- noch Waiting-Bins ohne Reverse-Match-Bypass.
        //
        // <para>Historie:</para>
        // <list type="bullet">
        //   <item>v0.1.23: Pre-Filter ueber bin.Kind eingefuehrt; mit
        //   excludeMinifigId wurden [Empty, Floating, Waiting, Complete]
        //   zugelassen mit der Annahme "Bin der zu zerlegenden Figur wird
        //   gleich frei". -&gt; Discrepancy zu AvailableBins.</item>
        //   <item>v0.1.24-beta.4: Complete aus der Liste raus, Waiting blieb
        //   drin mit dem Argument "excluded-Figur in einem Waiting-Bin
        //   bleibt als Kandidat moeglich". -&gt; Bug blieb fuer den
        //   Pure-Waiting-Bin-Fall (zu zerlegende Figur allein im Bin).</item>
        //   <item>v0.1.24-beta.9: jetzt strikt [Empty, Floating]. Empty/
        //   Floating-Bins haben per definition keine TrackedMinifigs, daher
        //   ist die alte "TrackedMinifigs.Any"-Where-Klausel redundant
        //   und entfaellt. Reifungspfad (Mix-Bin mit Floating-Stapel im
        //   Waiting-Bin) wird ueber den Stapel-Match (Schritt 1, kein
        //   Kind-Filter) abgedeckt.</item>
        // </list>
        StorageBinKind[] allowedKinds = new[]
        {
            StorageBinKind.Empty, StorageBinKind.Floating
        };

        var candidates = await ctx.StorageBins
            .AsNoTracking()
            .Include(b => b.FloatingParts)
            .Where(b => allowedKinds.Contains(b.Kind))
            .OrderBy(b => b.Label)
            .ToListAsync(ct);

        foreach (var bin in candidates)
        {
            // Bin-Konflikt = ein anderer FloatingPart-Item (PartNo+ColorId
            // verschieden vom neuen Item) mit GLEICHER Brickognize-Kategorie.
            //
            // Stapel-Match (exakt gleiche PartNo+ColorId) wurde schon in
            // Schritt 1 abgehandelt - wenn wir hier sind, hat KEIN Stapel
            // gegriffen. Trotzdem kann das gleiche PartNo aber andere Farbe
            // im Bin liegen (z.B. 3001/Black + neue 3001/Red): das ist
            // ebenfalls ein "anderes Item" und kollidiert.
            //
            // Bestand mit BrickognizeCategory=null wird wie Pseudo-Kategorie
            // "Unbekannt" behandelt - wenn der neue Part auch in "Unbekannt"
            // faellt, kollidiert er mit allen ungelabelten Bestand-Parts.
            // So bleibt die Migration A sauber: ungelabelte Bestand-Stapel
            // werden aufgeteilt sobald neue Items mit "Unbekannt" reinkommen.
            var hasConflict = bin.FloatingParts.Any(fp =>
                (fp.PartNumber != blPartNo || fp.ColorId != blColorId)
                && (string.IsNullOrEmpty(fp.BrickognizeCategory)
                        ? ICategoryBinMappingService.UnknownCategory
                        : fp.BrickognizeCategory) == category);

            if (!hasConflict)
                return bin;
        }

        // ============================================================
        // 4) Kein passendes Bin -> null. Block-K-Konvention: Aufrufer
        //    zeigt Banner, kein Silent-Fallback.
        // ============================================================
        return null;
    }

    // ---- Create ----

    public async Task<StorageBin> CreateSingleAsync(string label, string? notes = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Label darf nicht leer sein.", nameof(label));

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Existenz-Check (ueber unique Index - aber bessere Fehlermeldung)
        if (await ctx.StorageBins.AnyAsync(b => b.Label == label, ct))
            throw new InvalidOperationException($"Lagerfach '{label}' existiert bereits.");

        var bin = new StorageBin
        {
            Label = label,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes,
            CreatedAt = DateTime.UtcNow,
            FreedAt = null
        };
        ctx.StorageBins.Add(bin);
        await ctx.SaveChangesAsync(ct);

        // UX X.29 (v0.1.16): Undo-ScanEvent fuer angelegtes Fach.
        var snapshot = new UndoSnapshotBinCreated { BinIds = new() { bin.Id } };
        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = ScanType.BinCreated,
            ResultDescription = $"Lagerfach '{bin.Label}' angelegt",
            WasUndone = false,
            UndoData = System.Text.Json.JsonSerializer.Serialize(snapshot)
        });
        await ctx.SaveChangesAsync(ct);

        Log.Information("Lagerfach angelegt: '{Label}' (Id={Id})", bin.Label, bin.Id);
        return bin;
    }

    public async Task<BulkCreateResult> CreateBulkAsync(IEnumerable<string> labels, CancellationToken ct = default)
    {
        var inputLabels = labels?.Where(l => !string.IsNullOrWhiteSpace(l))
                                .Select(l => l.Trim())
                                .ToList() ?? new List<string>();

        var result = new BulkCreateResult();
        if (inputLabels.Count == 0) return result;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Existierende Labels einmal abfragen, damit wir nicht pro Insert pruefen muessen.
        var existing = await ctx.StorageBins
            .Where(b => inputLabels.Contains(b.Label))
            .Select(b => b.Label)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);

        // Auch innerhalb der Bulk-Liste Duplikate filtern.
        var seenInBatch = new HashSet<string>(StringComparer.Ordinal);

        foreach (var label in inputLabels)
        {
            if (existingSet.Contains(label) || !seenInBatch.Add(label))
            {
                result.Conflicts.Add(label);
                continue;
            }

            var bin = new StorageBin
            {
                Label = label,
                CreatedAt = DateTime.UtcNow,
                FreedAt = null
            };
            ctx.StorageBins.Add(bin);
            result.Created.Add(bin);
        }

        await ctx.SaveChangesAsync(ct);

        // UX X.29 (v0.1.16): Bulk-Anlage als ein Undo-Event - ein Klick auf
        // Rueckgaengig loescht alle in diesem Schwung angelegten Faecher
        // (sofern noch leer).
        if (result.Created.Count > 0)
        {
            var snapshot = new UndoSnapshotBinCreated
            {
                BinIds = result.Created.Select(b => b.Id).ToList()
            };
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = DateTime.UtcNow,
                Type = ScanType.BinCreated,
                ResultDescription = result.Created.Count == 1
                    ? $"Lagerfach '{result.Created[0].Label}' angelegt"
                    : $"{result.Created.Count} Lagerfaecher angelegt (Bulk)",
                WasUndone = false,
                UndoData = System.Text.Json.JsonSerializer.Serialize(snapshot)
            });
            await ctx.SaveChangesAsync(ct);
        }

        Log.Information("Bulk-Anlage: {Count} Faecher erstellt, {Conflicts} Konflikte",
            result.Created.Count, result.Conflicts.Count);
        return result;
    }

    // ---- Update ----

    public async Task<bool> RenameAsync(int id, string newLabel, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newLabel)) return false;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var bin = await ctx.StorageBins.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bin == null) return false;

        if (await ctx.StorageBins.AnyAsync(b => b.Label == newLabel && b.Id != id, ct))
            throw new InvalidOperationException($"Lagerfach '{newLabel}' existiert bereits.");

        var old = bin.Label;
        bin.Label = newLabel.Trim();
        await ctx.SaveChangesAsync(ct);
        Log.Information("Lagerfach umbenannt: '{Old}' -> '{New}'", old, bin.Label);
        return true;
    }

    public async Task<BinEmptyPreview?> GetEmptyPreviewAsync(int id, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var bin = await ctx.StorageBins.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bin == null) return null;
        var minifigs = await ctx.TrackedMinifigs.AsNoTracking()
            .Where(m => m.StorageBinId == id)
            .ToListAsync(ct);
        var floatingCount = await ctx.FloatingParts.AsNoTracking()
            .CountAsync(f => f.StorageBinId == id, ct);
        return new BinEmptyPreview(
            BinId: bin.Id,
            Label: bin.Label,
            WaitingMinifigsCount: minifigs.Count(m => m.Status == TrackedMinifigStatus.Waiting),
            CompleteMinifigsCount: minifigs.Count(m => m.Status == TrackedMinifigStatus.Complete),
            FloatingPartsCount: floatingCount);
    }

    public async Task<bool> EmptyAsync(int id, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var bin = await ctx.StorageBins
            .Include(b => b.TrackedMinifigs)
            .Include(b => b.FloatingParts)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bin == null) return false;

        var now = DateTime.UtcNow;
        var minifigsList = bin.TrackedMinifigs.ToList();
        var floatingsList = bin.FloatingParts.ToList();

        // UX X.29 Block C (v0.1.16): TrackedMinifigs werden aus dem Fach geloest,
        // aber NICHT geloescht. Jede Entkopplung schreibt einen Move-ScanEvent
        // mit UndoData (von old-bin zu null) - User kann via Verlauf-Tab eine
        // einzelne Figur zurueckverschieben.
        foreach (var minifig in minifigsList)
        {
            ct.ThrowIfCancellationRequested();
            var snap = new UndoSnapshotMove
            {
                MinifigId = minifig.Id,
                OldStorageBinId = id,
                NewStorageBinId = null
            };
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = now,
                Type = ScanType.Move,
                RecognizedId = minifig.BricklinkId ?? minifig.FigNum,
                ResultDescription = $"Fach geleert: Figur '{minifig.Name}' aus '{bin.Label}' geloest",
                WasUndone = false,
                UndoData = System.Text.Json.JsonSerializer.Serialize(snap)
            });
            minifig.StorageBinId = null;
        }

        // UX X.29 Block C: FloatingParts werden geloescht (siehe CLAUDE.md
        // "Beim Leeren werden FloatingParts geloescht"). Pro geloeschtem
        // FloatingPart ein Delete-ScanEvent mit UndoData - Strg+Z stellt
        // den Eintrag wieder her.
        foreach (var fp in floatingsList)
        {
            ct.ThrowIfCancellationRequested();
            var snap = new UndoSnapshotFloatingDelete
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
                ResultDescription = $"Fach geleert: Einzelteil '{fp.PartName}' (BL:{fp.PartNumber}/{fp.ColorId}) x{fp.Quantity} aus '{bin.Label}' geloescht",
                WasUndone = false,
                UndoData = System.Text.Json.JsonSerializer.Serialize(snap)
            });
        }
        ctx.FloatingParts.RemoveRange(floatingsList);

        // BinFreed-ScanEvent mit UndoData - Strg+Z koennte das Fach wieder als
        // belegt markieren (FreedAt=null), aendert aber NICHT die Item-
        // Zuordnungen rueckgaengig (das machen die einzelnen Move/Delete-Undos).
        var freedSnap = new UndoSnapshotBinFreed { BinId = bin.Id, PreviousFreedAt = now };
        ctx.ScanEvents.Add(new ScanEvent
        {
            Timestamp = now,
            Type = ScanType.BinFreed,
            ResultDescription = $"Lagerfach '{bin.Label}' geleert ({minifigsList.Count} Figuren geloest, {floatingsList.Count} FloatingParts geloescht)",
            WasUndone = false,
            UndoData = System.Text.Json.JsonSerializer.Serialize(freedSnap)
        });

        bin.FreedAt = now;
        // v0.1.23 S17: Beim Leeren wird Kind direkt auf Empty gesetzt
        // (keine Recalc-Query noetig - wir wissen dass der Bin leer ist).
        bin.Kind = StorageBinKind.Empty;
        await ctx.SaveChangesAsync(ct);
        Log.Information("Lagerfach '{Label}' geleert ({Mfgs} Figuren geloest, {Parts} FloatingParts geloescht)",
            bin.Label, minifigsList.Count, floatingsList.Count);
        return true;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var bin = await ctx.StorageBins
            .Include(b => b.TrackedMinifigs)
            .Include(b => b.FloatingParts)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bin == null) return false;

        // Wartende Figuren oder Floating-Parts blockieren das Loeschen.
        var waiting = bin.TrackedMinifigs.Where(m => m.Status == TrackedMinifigStatus.Waiting).ToList();
        if (waiting.Any() || bin.FloatingParts.Any())
        {
            throw new InvalidOperationException(
                $"Lagerfach '{bin.Label}' enthaelt noch wartende Figuren oder Teile. Erst leeren.");
        }

        // Nicht-wartende Figuren (DISMANTLED, COMPLETE, SOLD) werden ab-gekoppelt -
        // sie bleiben in der DB fuer Statistik, sind aber nicht mehr im Fach.
        var detachedCount = bin.TrackedMinifigs.Count;
        foreach (var m in bin.TrackedMinifigs.ToList())
        {
            m.StorageBinId = null;
        }

        ctx.StorageBins.Remove(bin);
        await ctx.SaveChangesAsync(ct);
        Log.Information("Lagerfach '{Label}' geloescht ({Detached} nicht-wartende Figuren ab-gekoppelt)",
            bin.Label, detachedCount);
        return true;
    }

    public async Task<List<StorageBin>> FindBinsThatWouldBeEmptyAsync(
        IEnumerable<int> minifigIdsToBeRemoved,
        IEnumerable<int>? floatingPartIdsToBeRemoved = null,
        CancellationToken ct = default)
    {
        var minifigIds = (minifigIdsToBeRemoved ?? Enumerable.Empty<int>()).ToHashSet();
        var floatingIds = (floatingPartIdsToBeRemoved ?? Enumerable.Empty<int>()).ToHashSet();
        if (minifigIds.Count == 0 && floatingIds.Count == 0)
            return new List<StorageBin>();

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Kandidaten: Faecher die mind. einen zu entfernenden Eintrag enthalten
        // UND noch nicht freigegeben sind. Sammeln aus beiden Quellen.
        var candidateBinIds = new HashSet<int>();

        if (minifigIds.Count > 0)
        {
            var fromMinifigs = await ctx.TrackedMinifigs.AsNoTracking()
                .Where(m => minifigIds.Contains(m.Id) && m.StorageBinId != null)
                .Select(m => m.StorageBinId!.Value)
                .Distinct()
                .ToListAsync(ct);
            foreach (var id in fromMinifigs) candidateBinIds.Add(id);
        }

        if (floatingIds.Count > 0)
        {
            var fromFloats = await ctx.FloatingParts.AsNoTracking()
                .Where(p => floatingIds.Contains(p.Id))
                .Select(p => p.StorageBinId)
                .Distinct()
                .ToListAsync(ct);
            foreach (var id in fromFloats) candidateBinIds.Add(id);
        }

        if (candidateBinIds.Count == 0) return new List<StorageBin>();

        var bins = await ctx.StorageBins.AsNoTracking()
            .Include(b => b.TrackedMinifigs)
            .Include(b => b.FloatingParts)
            .Where(b => b.FreedAt == null && candidateBinIds.Contains(b.Id))
            .ToListAsync(ct);

        // Bin gilt als "wird leer" wenn nach dem Cleanup:
        //   - alle aktuell drin liegenden Minifigs entfernt werden (in der ID-Liste)
        //   - alle aktuell drin liegenden FloatingParts entfernt werden (in der ID-Liste)
        return bins.Where(b =>
            b.TrackedMinifigs.All(m => minifigIds.Contains(m.Id))
            && b.FloatingParts.All(fp => floatingIds.Contains(fp.Id))
        ).OrderBy(b => b.Label).ToList();
    }

    public async Task<int> ReleaseBinsAsync(IEnumerable<int> binIds, CancellationToken ct = default)
    {
        var ids = binIds?.ToList() ?? new List<int>();
        if (ids.Count == 0) return 0;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var bins = await ctx.StorageBins
            .Where(b => ids.Contains(b.Id) && b.FreedAt == null)
            .ToListAsync(ct);
        if (bins.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var b in bins)
        {
            b.FreedAt = now;
            // UX X.29 (v0.1.16): pro Bin ein eigener BinFreed-ScanEvent.
            // Damit kann der User pro Fach einzeln zurueckziehen falls
            // er nur eines wieder reaktivieren will.
            var snapshot = new UndoSnapshotBinFreed
            {
                BinId = b.Id,
                PreviousFreedAt = now
            };
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = now,
                Type = ScanType.BinFreed,
                ResultDescription = $"Lagerfach '{b.Label}' freigegeben",
                WasUndone = false,
                UndoData = System.Text.Json.JsonSerializer.Serialize(snapshot)
            });
        }
        await ctx.SaveChangesAsync(ct);

        Log.Information("Lagerfaecher freigegeben: {Count} ({Labels})",
            bins.Count, string.Join(", ", bins.Select(b => b.Label)));
        return bins.Count;
    }

    // Bug A (UX X.28, 2026-05-08): CleanupStaleBinAssignmentsAsync ersatzlos entfernt.
    // Pre-X.6-Altlast - hat StorageBinId von Complete-Figuren beim App-Start
    // auf null gesetzt, widersprach UX-X.6-Konvention "Faecher bleiben belegt".

    public async Task<BinDetailData?> GetDetailAsync(int id, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var bin = await ctx.StorageBins.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bin == null) return null;

        var minifigs = await ctx.TrackedMinifigs.AsNoTracking()
            .Where(m => m.StorageBinId == id)
            .Include(m => m.RequiredParts)
            .OrderBy(m => m.Status)
            .ThenBy(m => m.Name)
            .ToListAsync(ct);

        var floats = await ctx.FloatingParts.AsNoTracking()
            .Where(p => p.StorageBinId == id)
            .OrderBy(p => p.PartName)
            .ToListAsync(ct);

        return new BinDetailData
        {
            Bin = bin,
            Minifigs = minifigs,
            FloatingParts = floats
        };
    }

    public async Task<BinOccupancy> GetOccupancyAsync(int id, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var minifigs = await ctx.TrackedMinifigs.AsNoTracking()
            .Where(m => m.StorageBinId == id && m.Status == TrackedMinifigStatus.Waiting)
            .ToListAsync(ct);
        var partCount = await ctx.FloatingParts.AsNoTracking()
            .Where(p => p.StorageBinId == id)
            .CountAsync(ct);

        return new BinOccupancy
        {
            MinifigCount = minifigs.Count,
            FloatingPartCount = partCount,
            Minifigs = minifigs
        };
    }

    // ========================================================================
    // v0.1.23: Bin-Typ-Spalte (StorageBin.Kind) ersetzt GetBinKindAsync.
    // RecalculateKindAsync pflegt die Spalte nach jeder Schreib-Aktion.
    // ========================================================================

    public async Task RecalculateKindAsync(int binId, CancellationToken ct = default)
    {
        // v0.1.23: liest den aktuellen Inhalt eines Bins und persistiert
        // den korrekten Kind. Idempotent - wenn der Wert sich nicht
        // aendert, wird trotzdem SaveChangesAsync gerufen (EF Core erkennt
        // intern dass nichts dirty ist, kein DB-Roundtrip noetig).
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var bin = await ctx.StorageBins
            .Include(b => b.TrackedMinifigs)
            .Include(b => b.FloatingParts)
            .FirstOrDefaultAsync(b => b.Id == binId, ct);
        if (bin == null) return;

        var waitingCount = bin.TrackedMinifigs.Count(m => m.Status == TrackedMinifigStatus.Waiting);
        var completeCount = bin.TrackedMinifigs.Count(m => m.Status == TrackedMinifigStatus.Complete);
        var floatingCount = bin.FloatingParts.Count;

        StorageBinKind newKind;
        if (waitingCount == 0 && completeCount == 0 && floatingCount == 0)
            newKind = StorageBinKind.Empty;
        else if (waitingCount > 0)
            newKind = StorageBinKind.Waiting;   // Reifungspfad: Wartend+Complete bleibt Waiting
        else if (floatingCount > 0)
            newKind = StorageBinKind.Floating;
        else
            newKind = StorageBinKind.Complete;

        if (bin.Kind != newKind)
        {
            bin.Kind = newKind;
            await ctx.SaveChangesAsync(ct);
        }
    }

    public async Task<List<BinMixWarning>> FindBestandMixBinsAsync(CancellationToken ct = default)
    {
        // v0.1.22-beta.1 Block G: scannt einmalig die DB nach Mix-Bins die
        // die neue Block-G-Sortier-Logik nicht mehr produziert:
        //   1) Wartend-Bin mit fremden FloatingParts
        //      (= FloatingParts die zu KEINER der enthaltenen wartenden
        //      Figuren passen).
        //   2) Floating-Bin mit wartender oder kompletter Figur
        //      (Klassifikator wuerde Anomalie loggen).
        // Kein Auto-Move - reine Detektion plus User-Hinweis.
        var result = new List<BinMixWarning>();
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Alle relevanten Bins einmal laden (typisch <100 Bins, akzeptabler
        // In-Memory-Aggregate).
        var bins = await ctx.StorageBins.AsNoTracking()
            .Include(b => b.TrackedMinifigs)
                .ThenInclude(m => m.RequiredParts)
            .Include(b => b.FloatingParts)
            .ToListAsync(ct);

        foreach (var bin in bins)
        {
            ct.ThrowIfCancellationRequested();
            var hasWaiting = bin.TrackedMinifigs.Any(m => m.Status == TrackedMinifigStatus.Waiting);
            var hasComplete = bin.TrackedMinifigs.Any(m => m.Status == TrackedMinifigStatus.Complete);
            var floats = bin.FloatingParts;

            // Fall 2: Floating + Minifig (egal welcher Status)
            if (floats.Count > 0 && (hasWaiting || hasComplete))
            {
                // Wartend-Bin mit fremden Floating-Parts: pruefen ob alle Floats
                // zu mindestens einer wartenden Figur passen (Reverse-Match-
                // Kandidaten). Sonst ist es Mix.
                if (hasWaiting && !hasComplete)
                {
                    // Sammele alle (PartNo, ColorId)-Paare die in den wartenden
                    // Figuren stehen.
                    var waitingPartKeys = bin.TrackedMinifigs
                        .Where(m => m.Status == TrackedMinifigStatus.Waiting)
                        .SelectMany(m => m.RequiredParts)
                        .Select(rp => (rp.PartNumber, rp.ColorId))
                        .ToHashSet();

                    var foreignFloats = floats
                        .Where(fp => !waitingPartKeys.Contains((fp.PartNumber, fp.ColorId)))
                        .ToList();
                    if (foreignFloats.Count > 0)
                    {
                        result.Add(new BinMixWarning(
                            bin.Id, bin.Label,
                            $"Wartend-Bin mit {foreignFloats.Count} fremden Einzelteilen " +
                            "(passen zu keiner der wartenden Figuren)."));
                    }
                }
                else
                {
                    // Komplett+Floating oder gemischt Wartend+Komplett+Floating
                    result.Add(new BinMixWarning(
                        bin.Id, bin.Label,
                        $"Floating + Minifig im selben Bin ({floats.Count} Einzelteile, " +
                        $"{bin.TrackedMinifigs.Count} Figuren)."));
                }
            }
        }

        if (result.Count > 0)
        {
            Log.Warning("Bestand-Mix-Bins gefunden: {Count} ({Bins})",
                result.Count, string.Join(", ", result.Select(r => r.Label)));
        }
        return result;
    }

    public async Task<List<StorageBin>> GetEligibleBinsAsync(
        BinTargetKind targetKind,
        string? partNo = null,
        int? colorId = null,
        int? excludeMinifigId = null,
        int? waitingLimit = null,
        int? completeLimit = null,
        CancellationToken ct = default)
    {
        // v0.1.23: Filter direkt ueber persistierte StorageBin.Kind-Spalte
        // (Index IX_StorageBins_Kind). Vorher (v0.1.22-beta.3): alle Bins
        // mit Include geladen + lokal klassifiziert. Jetzt: WHERE Kind IN ...
        // plus Reverse-Match-Pruefung nur fuer Floating-Target.
        //
        // v0.1.24-beta.1 (Konzept Befund B1): optionale Limit-Parameter
        // (waitingLimit / completeLimit). Wenn gesetzt: Bins mit Count
        // >= Limit werden ausgefiltert. Defensive Clamp auf >=1 - 0 oder
        // negative Werte wuerden alle Bins filtern und sind vermutlich
        // ein Bedienfehler des Aufrufers.
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        StorageBinKind[] allowedKinds = targetKind switch
        {
            BinTargetKind.WaitingMinifigTarget =>
                new[] { StorageBinKind.Empty, StorageBinKind.Waiting, StorageBinKind.Complete },
            BinTargetKind.CompleteMinifigTarget =>
                new[] { StorageBinKind.Empty, StorageBinKind.Waiting, StorageBinKind.Complete },
            BinTargetKind.FloatingTarget =>
                new[] { StorageBinKind.Empty, StorageBinKind.Floating, StorageBinKind.Waiting },
            _ => throw new ArgumentOutOfRangeException(nameof(targetKind))
        };

        // v0.1.24-beta.1: brauchen wir Minifig-Counts? Wenn waitingLimit oder
        // completeLimit gesetzt sind: TrackedMinifigs einbeziehen damit wir
        // die Counts in-memory ermitteln koennen. Sonst bleibt das Query so
        // schlank wie bisher.
        var needsCounts = waitingLimit.HasValue || completeLimit.HasValue;

        // FloatingTarget mit Waiting-Bypass braucht TrackedMinifigs + RequiredParts.
        // Andere Pfade nicht - dann ohne Include schneller.
        var query = ctx.StorageBins.AsNoTracking()
            .Where(b => allowedKinds.Contains(b.Kind))
            .Where(b => true)
            .OrderBy(b => b.Label)
            .AsQueryable();

        if (targetKind == BinTargetKind.FloatingTarget)
        {
            query = query
                .Include(b => b.TrackedMinifigs)
                    .ThenInclude(m => m.RequiredParts);
        }
        else if (needsCounts)
        {
            // Minifig-Targets brauchen die TrackedMinifigs-Liste nur, wenn die
            // Limits gesetzt sind. Ohne Limits bleibt das alte schnelle Query
            // erhalten (Backwards-Compat).
            query = query.Include(b => b.TrackedMinifigs);
        }

        var bins = await query.ToListAsync(ct);

        // Bei FloatingTarget mit Waiting-Bin: nur durchlassen wenn Reverse-Match
        // ein passendes wartendes Required-Part findet.
        if (targetKind == BinTargetKind.FloatingTarget)
        {
            var eligible = new List<StorageBin>(bins.Count);
            foreach (var bin in bins)
            {
                if (bin.Kind == StorageBinKind.Empty || bin.Kind == StorageBinKind.Floating)
                {
                    eligible.Add(bin);
                    continue;
                }
                // Waiting-Bin: pruefen ob (partNo, colorId) zu einer wartenden Figur passt.
                if (string.IsNullOrEmpty(partNo) || !colorId.HasValue) continue;

                var matchFound = bin.TrackedMinifigs.Any(m =>
                    m.Status == TrackedMinifigStatus.Waiting
                    && (!excludeMinifigId.HasValue || m.Id != excludeMinifigId.Value)
                    && m.RequiredParts.Any(rp =>
                        rp.PartNumber == partNo
                        && rp.ColorId == colorId.Value
                        // v0.1.24-beta.8 Phase 3: effektive Restmenge
                        // (physisch + BL-reserviert).
                        && (rp.QuantityCollected + rp.QuantityReservedFromBl) < rp.QuantityNeeded));
                if (matchFound) eligible.Add(bin);
            }
            return eligible;
        }

        // Minifig-Targets: alle bins mit erlaubtem Kind durchlassen.
        // v0.1.24-beta.1: Limit-Filter falls aktiv.
        if (!needsCounts) return bins;

        // Clamp Limits auf >=1 (0 oder negativ ist Bedienfehler).
        var wLimit = waitingLimit.HasValue ? Math.Max(1, waitingLimit.Value) : (int?)null;
        var cLimit = completeLimit.HasValue ? Math.Max(1, completeLimit.Value) : (int?)null;

        var filtered = new List<StorageBin>(bins.Count);
        foreach (var bin in bins)
        {
            // Counts ohne ausgeschlossene Minifig (analog excludeMinifigId-
            // Konvention an anderen Service-Stellen).
            var waiting = bin.TrackedMinifigs.Count(m =>
                m.Status == TrackedMinifigStatus.Waiting
                && (!excludeMinifigId.HasValue || m.Id != excludeMinifigId.Value));
            var complete = bin.TrackedMinifigs.Count(m =>
                m.Status == TrackedMinifigStatus.Complete
                && (!excludeMinifigId.HasValue || m.Id != excludeMinifigId.Value));

            if (wLimit.HasValue && waiting >= wLimit.Value) continue;
            if (cLimit.HasValue && complete >= cLimit.Value) continue;
            filtered.Add(bin);
        }
        return filtered;
    }

    /// <summary>
    /// v0.1.24-beta.1 Phase 2b (Konzept Befund B2 / OPEN-7): wie
    /// <see cref="GetEligibleBinsAsync"/>, aber zusaetzlich pro Bin die
    /// Belegungs-Counts (WaitingCount, CompleteCount, FloatingCount).
    ///
    /// Eigenstaendige Query mit Include fuer TrackedMinifigs + FloatingParts
    /// (anders als <see cref="GetEligibleBinsAsync"/>, das die Includes nur
    /// fuer FloatingTarget oder Limit-Filter macht). Aggregation passiert
    /// in-memory ueber die schon geladenen Listen — kein N+1.
    /// </summary>
    public async Task<List<StorageBinWithCounts>> GetEligibleBinsWithCountsAsync(
        BinTargetKind targetKind,
        string? partNo = null,
        int? colorId = null,
        int? excludeMinifigId = null,
        int? waitingLimit = null,
        int? completeLimit = null,
        CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        StorageBinKind[] allowedKinds = targetKind switch
        {
            BinTargetKind.WaitingMinifigTarget =>
                new[] { StorageBinKind.Empty, StorageBinKind.Waiting, StorageBinKind.Complete },
            BinTargetKind.CompleteMinifigTarget =>
                new[] { StorageBinKind.Empty, StorageBinKind.Waiting, StorageBinKind.Complete },
            BinTargetKind.FloatingTarget =>
                new[] { StorageBinKind.Empty, StorageBinKind.Floating, StorageBinKind.Waiting },
            _ => throw new ArgumentOutOfRangeException(nameof(targetKind))
        };

        // Counts brauchen TrackedMinifigs + FloatingParts pro Bin. Floating-
        // Target braucht zusaetzlich RequiredParts fuer den Reverse-Match-Filter.
        var query = ctx.StorageBins.AsNoTracking()
            .Where(b => allowedKinds.Contains(b.Kind))
            .Include(b => b.TrackedMinifigs)
            .Include(b => b.FloatingParts)
            .OrderBy(b => b.Label)
            .AsQueryable();

        if (targetKind == BinTargetKind.FloatingTarget)
        {
            query = query
                .Include(b => b.TrackedMinifigs)
                    .ThenInclude(m => m.RequiredParts);
        }

        var bins = await query.ToListAsync(ct);

        // Reverse-Match-Bypass nur für FloatingTarget mit Waiting-Bins
        // (identische Logik wie GetEligibleBinsAsync — Empty/Floating
        // durchlassen, Waiting nur wenn ein wartendes Required-Part zu
        // (partNo, colorId) passt).
        var eligible = new List<StorageBin>(bins.Count);
        if (targetKind == BinTargetKind.FloatingTarget)
        {
            foreach (var bin in bins)
            {
                if (bin.Kind == StorageBinKind.Empty || bin.Kind == StorageBinKind.Floating)
                {
                    eligible.Add(bin);
                    continue;
                }
                if (string.IsNullOrEmpty(partNo) || !colorId.HasValue) continue;

                var matchFound = bin.TrackedMinifigs.Any(m =>
                    m.Status == TrackedMinifigStatus.Waiting
                    && (!excludeMinifigId.HasValue || m.Id != excludeMinifigId.Value)
                    && m.RequiredParts.Any(rp =>
                        rp.PartNumber == partNo
                        && rp.ColorId == colorId.Value
                        // v0.1.24-beta.8 Phase 3: effektive Restmenge
                        // (physisch + BL-reserviert).
                        && (rp.QuantityCollected + rp.QuantityReservedFromBl) < rp.QuantityNeeded));
                if (matchFound) eligible.Add(bin);
            }
        }
        else
        {
            eligible.AddRange(bins);
        }

        // Clamp Limits auf >=1 (identisch zu GetEligibleBinsAsync).
        var wLimit = waitingLimit.HasValue ? Math.Max(1, waitingLimit.Value) : (int?)null;
        var cLimit = completeLimit.HasValue ? Math.Max(1, completeLimit.Value) : (int?)null;

        var result = new List<StorageBinWithCounts>(eligible.Count);
        foreach (var bin in eligible)
        {
            var waiting = bin.TrackedMinifigs.Count(m =>
                m.Status == TrackedMinifigStatus.Waiting
                && (!excludeMinifigId.HasValue || m.Id != excludeMinifigId.Value));
            var complete = bin.TrackedMinifigs.Count(m =>
                m.Status == TrackedMinifigStatus.Complete
                && (!excludeMinifigId.HasValue || m.Id != excludeMinifigId.Value));
            var floating = bin.FloatingParts.Count;

            if (wLimit.HasValue && waiting >= wLimit.Value) continue;
            if (cLimit.HasValue && complete >= cLimit.Value) continue;

            result.Add(new StorageBinWithCounts(bin, waiting, complete, floating));
        }
        return result;
    }
}
