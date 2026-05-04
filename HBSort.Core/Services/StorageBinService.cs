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

    public StorageBinService(IDbContextFactory<UserDataContext> ctxFactory)
    {
        _ctxFactory = ctxFactory;
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

    public async Task<bool> EmptyAsync(int id, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var bin = await ctx.StorageBins
            .Include(b => b.TrackedMinifigs)
            .Include(b => b.FloatingParts)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bin == null) return false;

        // TrackedMinifigs: aus dem Fach loesen, aber NICHT loeschen (sie bleiben in der DB)
        foreach (var minifig in bin.TrackedMinifigs.ToList())
        {
            minifig.StorageBinId = null;
        }

        // FloatingParts: vollstaendig loeschen (siehe CLAUDE.md "Beim Leeren ...
        // werden FloatingParts geloescht")
        ctx.FloatingParts.RemoveRange(bin.FloatingParts);

        bin.FreedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
        Log.Information("Lagerfach '{Label}' geleert ({Mfgs} Figuren geloest, {Parts} FloatingParts geloescht)",
            bin.Label, bin.TrackedMinifigs.Count, bin.FloatingParts.Count);
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
        foreach (var b in bins) b.FreedAt = now;
        await ctx.SaveChangesAsync(ct);

        Log.Information("Lagerfaecher freigegeben: {Count} ({Labels})",
            bins.Count, string.Join(", ", bins.Select(b => b.Label)));
        return bins.Count;
    }

    public async Task<int> CleanupStaleBinAssignmentsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var stale = await ctx.TrackedMinifigs
            .Where(m => m.StorageBinId != null
                     && m.Status != TrackedMinifigStatus.Waiting)
            .ToListAsync(ct);
        foreach (var m in stale)
            m.StorageBinId = null;
        if (stale.Count > 0)
        {
            await ctx.SaveChangesAsync(ct);
            Log.Information("Cleanup: {Count} nicht-wartende Figuren aus Faechern ab-gekoppelt", stale.Count);
        }
        return stale.Count;
    }

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
