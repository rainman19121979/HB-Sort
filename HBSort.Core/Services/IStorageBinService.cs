using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// Lager-Fach-Verwaltung. Schreibt nach userdata.db (EF Core).
/// </summary>
public interface IStorageBinService
{
    Task<List<StorageBin>> GetAllAsync(CancellationToken ct = default);
    Task<StorageBin?> GetByLabelAsync(string label, CancellationToken ct = default);
    Task<StorageBin?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>Faecher die aktuell keine wartende Figur und keinen FloatingPart enthalten.</summary>
    Task<List<StorageBin>> GetFreeAsync(CancellationToken ct = default);

    /// <summary>Faecher die mindestens eine wartende Figur ODER FloatingParts enthalten.</summary>
    Task<List<StorageBin>> GetOccupiedAsync(CancellationToken ct = default);

    /// <summary>Erstes freies Fach (sortiert nach Label) oder null wenn keins frei.</summary>
    Task<StorageBin?> GetNextFreeAsync(CancellationToken ct = default);

    Task<StorageBin> CreateSingleAsync(string label, string? notes = null, CancellationToken ct = default);

    /// <summary>
    /// Bulk-Anlage. Konflikte (Label existiert bereits) werden NICHT angelegt
    /// und in <see cref="BulkCreateResult.Conflicts"/> zurueckgegeben.
    /// </summary>
    Task<BulkCreateResult> CreateBulkAsync(IEnumerable<string> labels, CancellationToken ct = default);

    Task<bool> RenameAsync(int id, string newLabel, CancellationToken ct = default);

    /// <summary>
    /// Setzt FreedAt=now, loest TrackedMinifigs vom Fach (StorageBinId=null) und
    /// loescht alle FloatingParts in diesem Fach. Liefert false wenn das Fach
    /// nicht existiert.
    /// </summary>
    Task<bool> EmptyAsync(int id, CancellationToken ct = default);

    /// <summary>Loescht das Fach. Schlaegt fehl wenn noch Figuren oder FloatingParts darin sind.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Aktuelle Belegung (Anzahl + Liste der wartenden Figuren).</summary>
    Task<BinOccupancy> GetOccupancyAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Detaillierter Inhalt eines Faches: Bin selbst + alle TrackedMinifigs (egal welcher Status,
    /// solange dem Fach zugeordnet) + alle FloatingParts gruppiert nach (BlPartNo, ColorId).
    /// Wird vom BinDetailDialog genutzt.
    /// </summary>
    Task<BinDetailData?> GetDetailAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Phase 7: Vorab-Berechnung fuer den BSX-Export-Cleanup. Liefert die Faecher
    /// die NACH dem Loeschen der uebergebenen Minifig-IDs UND/ODER FloatingPart-IDs
    /// leer waeren (FreedAt=null, keine wartenden Figuren ausser den uebergebenen,
    /// keine FloatingParts ausser den uebergebenen).
    /// Setzt FreedAt NICHT - der Aufrufer entscheidet ob die Faecher freigegeben werden.
    /// floatingPartIdsToBeRemoved kann null oder leer sein - dann wird nur Minifig-
    /// Removal beruecksichtigt (Backwards-Compat zur urspruenglichen Phase-7-Variante).
    /// </summary>
    Task<List<StorageBin>> FindBinsThatWouldBeEmptyAsync(
        IEnumerable<int> minifigIdsToBeRemoved,
        IEnumerable<int>? floatingPartIdsToBeRemoved = null,
        CancellationToken ct = default);

    /// <summary>
    /// Phase 7: setzt FreedAt=now fuer die uebergebenen Faecher.
    /// Pruefen ob die Faecher wirklich leer sind macht der Aufrufer.
    /// Liefert die Anzahl freigegebener Faecher.
    /// </summary>
    Task<int> ReleaseBinsAsync(IEnumerable<int> binIds, CancellationToken ct = default);
}

/// <summary>Daten-Struktur fuer den BinDetailDialog.</summary>
public class BinDetailData
{
    public StorageBin Bin { get; init; } = null!;
    public List<TrackedMinifig> Minifigs { get; init; } = new();
    public List<FloatingPart> FloatingParts { get; init; } = new();
}

/// <summary>Ergebnis von CreateBulkAsync.</summary>
public class BulkCreateResult
{
    public List<StorageBin> Created { get; init; } = new();
    public List<string> Conflicts { get; init; } = new();
}

/// <summary>Belegungs-Snapshot eines Faches.</summary>
public class BinOccupancy
{
    public int MinifigCount { get; init; }
    public int FloatingPartCount { get; init; }
    public List<TrackedMinifig> Minifigs { get; init; } = new();
}
