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

    public async Task<List<StorageBin>> GetFreeAsync(CancellationToken ct = default)
    {
        // "Frei" heisst hier: keine wartende Figur (Status=WAITING) UND keine FloatingParts.
        // Status spielt mit - eine COMPLETE-Figur "blockiert" das Fach nicht mehr.
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        return await ctx.StorageBins
            .AsNoTracking()
            .Where(b => !b.TrackedMinifigs.Any(m => m.Status == TrackedMinifigStatus.Waiting)
                     && !b.FloatingParts.Any())
            .OrderBy(b => b.Label)
            .ToListAsync(ct);
    }

    public async Task<List<StorageBin>> GetOccupiedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        return await ctx.StorageBins
            .AsNoTracking()
            .Where(b => b.TrackedMinifigs.Any(m => m.Status == TrackedMinifigStatus.Waiting)
                     || b.FloatingParts.Any())
            .OrderBy(b => b.Label)
            .ToListAsync(ct);
    }

    public async Task<StorageBin?> GetNextFreeAsync(CancellationToken ct = default)
    {
        var free = await GetFreeAsync(ct);
        return free.FirstOrDefault();
    }

    // ====================================================================
    // UX X.31 (v0.1.18): Konsistente Bin-Vorschlaege pro Item-Typ.
    // Strenge Auslegung der UX-X.6-Konvention: Complete-Figuren BLOCKIEREN
    // ein Fach. Die alten GetFreeAsync/GetNextFreeAsync ignorieren das aus
    // Backwards-Compat-Gruenden. Diese drei Suggest-Methoden sind der
    // korrekte Default-Vorschlag fuer den Sortier-Workflow.
    // ====================================================================

    public async Task<StorageBin?> SuggestBinForWaitingMinifigAsync(
        int maxWaitingLimit = 1,
        CancellationToken ct = default)
    {
        // Defensive Clamping - UI sollte 1..999 garantieren, aber wir
        // verlassen uns nicht drauf. Werte unter 1 verhalten sich wie 1.
        if (maxWaitingLimit < 1) maxWaitingLimit = 1;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // 1) Bei Limit > 1: Bin mit anderen wartenden (unter Limit) bevorzugen.
        //    Bedingungen: keine Complete-Figuren, keine FloatingParts (sonst
        //    Mix-Fach), bestehende wartende UND Anzahl &lt; Limit.
        //    Sortierung: voller Bin zuerst (Stapel waechst).
        if (maxWaitingLimit > 1)
        {
            var stackCandidate = await ctx.StorageBins
                .AsNoTracking()
                .Where(b => !b.TrackedMinifigs.Any(m => m.Status == TrackedMinifigStatus.Complete)
                         && !b.FloatingParts.Any())
                .Select(b => new
                {
                    Bin = b,
                    WaitingCount = b.TrackedMinifigs.Count(m => m.Status == TrackedMinifigStatus.Waiting)
                })
                .Where(x => x.WaitingCount > 0 && x.WaitingCount < maxWaitingLimit)
                .OrderByDescending(x => x.WaitingCount)
                .ThenBy(x => x.Bin.Label)
                .FirstOrDefaultAsync(ct);
            if (stackCandidate != null) return stackCandidate.Bin;
        }

        // 2) Fallback: wirklich freies Fach (keine Minifig irgendeines Status,
        //    keine FloatingParts). UX-X.6-konform.
        return await ctx.StorageBins
            .AsNoTracking()
            .Where(b => !b.TrackedMinifigs.Any()
                     && !b.FloatingParts.Any())
            .OrderBy(b => b.Label)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<StorageBin?> SuggestBinForCompleteMinifigAsync(int maxCompleteLimit, CancellationToken ct = default)
    {
        // Defensive Clamping - UI sollte 1..999 garantieren, aber wir
        // verlassen uns nicht drauf.
        if (maxCompleteLimit < 1) maxCompleteLimit = 1;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // 1) Bevorzugt: Bin mit anderen Complete-Figuren (unter Limit) und
        //    keinen Waiting-Figuren und keinen FloatingParts. Sortierung:
        //    meiste Complete-Figuren zuerst (wir wollen den vorhandenen
        //    Stapel wachsen lassen, nicht parallele anlegen).
        var candidates = await ctx.StorageBins
            .AsNoTracking()
            .Where(b => !b.TrackedMinifigs.Any(m => m.Status == TrackedMinifigStatus.Waiting)
                     && !b.FloatingParts.Any())
            .Select(b => new
            {
                Bin = b,
                CompleteCount = b.TrackedMinifigs.Count(m => m.Status == TrackedMinifigStatus.Complete)
            })
            .Where(x => x.CompleteCount > 0 && x.CompleteCount < maxCompleteLimit)
            .OrderByDescending(x => x.CompleteCount)
            .ThenBy(x => x.Bin.Label)
            .FirstOrDefaultAsync(ct);

        if (candidates != null) return candidates.Bin;

        // 2) Fallback: wirklich freies Fach (analog SuggestBinForWaitingMinifig).
        return await ctx.StorageBins
            .AsNoTracking()
            .Where(b => !b.TrackedMinifigs.Any() && !b.FloatingParts.Any())
            .OrderBy(b => b.Label)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<StorageBin?> SuggestBinForFloatingPartAsync(
        string blPartNo, int blColorId,
        int? excludeMinifigId = null,
        int maxCategoriesPerBin = 1,
        string? partName = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blPartNo))
            throw new ArgumentException("BL-Part-No darf nicht leer sein.", nameof(blPartNo));

        // Defensive Clamping.
        if (maxCategoriesPerBin < 1) maxCategoriesPerBin = 1;

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // 1) Bevorzugt: Bin in dem das gleiche Teil (PartNo + ColorId) schon liegt.
        //    FIFO nach AddedAt, damit konsistente Wahl bei mehreren Treffern.
        //    Bin muss noch existieren (FK-Pfad, falls Bin via DELETE wegging).
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

        var hasExclude = excludeMinifigId.HasValue;

        // 2) UX X.32 v0.1.19-beta.4 (User-Befund): Kategorie-Stapel-Pfad
        //    laeuft IMMER (auch bei Limit=1) - Phase A der Helper-Methode
        //    sucht ein Bin wo die gleiche Kategorie schon drin ist
        //    (z.B. zweites Bein-Teil zum bestehenden Bein-Bin). Phase B
        //    (Mix-Fach mit verschiedenen Kategorien) greift nur bei
        //    Limit > 1. Kategorie kommt aus PartName-Praefix (BL-Naming
        //    "Minifig Head, ..."), Fallback PartNo-Praefix.
        var categoryStackBin = await TryFindCategoryStackBinAsync(
            ctx, blPartNo, blColorId, partName, excludeMinifigId, maxCategoriesPerBin, ct);
        if (categoryStackBin != null) return categoryStackBin;

        // 3) Wirklich freies Fach: keine Minifigs (ausser excluded), keine FloatingParts.
        var trulyFree = await ctx.StorageBins
            .AsNoTracking()
            .Where(b =>
                !b.TrackedMinifigs.Any(m => !hasExclude || m.Id != excludeMinifigId!.Value)
                && !b.FloatingParts.Any())
            .OrderBy(b => b.Label)
            .FirstOrDefaultAsync(ct);
        if (trulyFree != null) return trulyFree;

        // 4) Erweitert: Fach OHNE Complete- und Waiting-Figuren (FloatingParts
        //    erlaubt - Mix-Fach OK wenn keine wirklich freien mehr da sind).
        var noMinifigOnly = await ctx.StorageBins
            .AsNoTracking()
            .Where(b =>
                !b.TrackedMinifigs.Any(m => !hasExclude || m.Id != excludeMinifigId!.Value))
            .OrderBy(b => b.Label)
            .FirstOrDefaultAsync(ct);
        if (noMinifigOnly != null) return noMinifigOnly;

        // 5) Letzter Fallback: irgendein Fach, sortiert nach am wenigsten
        //    belegt (Complete-Count zuerst, dann FloatingParts-Count, dann Label).
        //    User sieht "kein perfektes Fach, hier die beste Wahl" statt eines
        //    kommentarlosen null. Excluded Minifig zaehlt nicht.
        return await ctx.StorageBins
            .AsNoTracking()
            .OrderBy(b => b.TrackedMinifigs.Count(m =>
                m.Status == TrackedMinifigStatus.Complete
                && (!hasExclude || m.Id != excludeMinifigId!.Value)))
            .ThenBy(b => b.FloatingParts.Count)
            .ThenBy(b => b.Label)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// UX X.32 v0.1.19-beta.4 (User-Befund): zwei Phasen.
    ///
    /// Phase A (gilt IMMER, auch bei Limit=1): Bin wo die NEUE Kategorie
    /// schon vorhanden ist - dort STAPELT die neue Kategorie-Instanz mit
    /// (z.B. zweites Bein-Teil zum bestehenden Bein-Bin). Step 1
    /// (gleicher PartNo+ColorId-Stapel) deckt das nur fuer identische
    /// Teile ab; verschiedene Bein-Varianten haben aber unterschiedliche
    /// PartNos.
    ///
    /// Phase B (nur bei Limit > 1): Bin wo die neue Kategorie noch NICHT
    /// vorhanden ist UND Set-Count unter Limit - der User hat im Setting
    /// erlaubt mehrere Kategorien zu mischen.
    ///
    /// Kategorie-Bestimmung:
    /// 1. <see cref="IBlCacheRepository.GetCategoryIdsForPartsAsync"/>
    ///    (bl_items.category_id - meist nur fuer 'full'-Eintraege gesetzt)
    /// 2. Fallback: <see cref="BlPartCategoryHeuristic.GetPseudoCategory"/>
    ///    (numerischer Praefix der PartNo + Aliase fuer Bein/Arm/Hand-
    ///    Varianten).
    /// </summary>
    private async Task<StorageBin?> TryFindCategoryStackBinAsync(
        UserDataContext ctx,
        string blPartNo,
        int blColorId,
        string? partName,
        int? excludeMinifigId,
        int maxCategoriesPerBin,
        CancellationToken ct)
    {
        var hasExclude = excludeMinifigId.HasValue;

        // Kandidaten-Bins: haben FloatingParts UND keine Minifigs (ausser excluded).
        var candidates = await ctx.StorageBins
            .AsNoTracking()
            .Include(b => b.FloatingParts)
            .Where(b =>
                b.FloatingParts.Any()
                && !b.TrackedMinifigs.Any(m => !hasExclude || m.Id != excludeMinifigId!.Value))
            .ToListAsync(ct);
        if (candidates.Count == 0) return null;

        // Kategorie der NEUEN PartNo bestimmen - PartName-Praefix bevorzugt
        // (BL-Naming "Minifig Head, ..."), Fallback PartNo-Praefix. ColorId
        // wird in den Schluessel einbezogen damit verschiedene Farben des
        // gleichen Teils nicht zusammengewuerfelt werden.
        var newCategory = BlPartCategoryHeuristic.Resolve(blPartNo, blColorId, partName);

        // Pro Kandidat: Set seiner Kategorien (PartName + PartNo + ColorId
        // aus dem FloatingPart-Object - kein Cache-Roundtrip noetig).
        var ranked = candidates
            .Select(b => new
            {
                Bin = b,
                Categories = b.FloatingParts
                    .Select(fp => BlPartCategoryHeuristic.Resolve(
                        fp.PartNumber, fp.ColorId, fp.PartName))
                    .ToHashSet()
            })
            .ToList();

        Log.Information(
            "BinSuggest CategoryStack: NewPart={Part}/{Color} Name='{Name}' -> Key='{Key}'. Kandidaten: {Cnt}",
            blPartNo, blColorId, partName ?? "(null)", newCategory, ranked.Count);
        foreach (var r in ranked)
        {
            Log.Information("  - Bin '{Label}': Cats=[{Cats}]",
                r.Bin.Label, string.Join(",", r.Categories));
        }

        // UX X.32 v0.1.19-beta.4 (User-Befund 2x Helm): strikte Trennung
        // pro Kategorie. Step 1 (gleicher PartNo+ColorId-Stapel) hat schon
        // davor zugeschlagen - nur identische Teile stapeln dort.
        // Hier: Bin wo newCategory NOCH NICHT drin ist + Set-Count unter
        // Limit. Bei Limit=1 heisst das: Bin muss leer sein (count < 1).
        // Bei Limit > 1 wird Mischen erlaubt.
        var fitBin = ranked
            .Where(x => !x.Categories.Contains(newCategory)
                     && x.Categories.Count < maxCategoriesPerBin)
            .OrderByDescending(x => x.Categories.Count)
            .ThenBy(x => x.Bin.Label)
            .FirstOrDefault();
        if (fitBin != null)
        {
            Log.Information("BinSuggest: Kategorie-Filter trifft, Bin='{Label}'", fitBin.Bin.Label);
            return fitBin.Bin;
        }

        Log.Information("BinSuggest: kein Kategorie-Bin passt - Fallback auf Stufe 3-5");
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
            SoldMinifigsCount: minifigs.Count(m => m.Status == TrackedMinifigStatus.Sold),
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
    // Pre-X.6-Altlast - hat StorageBinId von Complete/Sold-Figuren beim App-Start
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
}
