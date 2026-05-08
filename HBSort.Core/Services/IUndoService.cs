using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// UX X.29 (v0.1.16): Undo-Service. Liest die ScanEvent-Tabelle nach
/// rueckgaengigmachbaren Aktionen und macht sie auf Anforderung wieder
/// rueckgaengig. UndoData (JSON) im ScanEvent traegt den Snapshot, der
/// pro <see cref="ScanType"/> ein eigenes Format hat (siehe UndoSnapshot*-
/// Records in HBSort.Core.Services).
/// </summary>
public interface IUndoService
{
    /// <summary>
    /// Liefert die letzten N rueckgaengigmachbaren Aktionen (sortiert
    /// neueste zuerst). WasUndone-Eintraege sind ausgeschlossen.
    /// </summary>
    Task<List<UndoableAction>> GetUndoableActionsAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Liefert die jeweils neueste rueckgaengigmachbare Aktion (fuer Strg+Z).
    /// Null wenn die Historie leer ist oder nichts undobar ist.
    /// </summary>
    Task<UndoableAction?> GetLastUndoableAsync(CancellationToken ct = default);

    /// <summary>
    /// Macht eine Aktion rueckgaengig. Markiert das ScanEvent als WasUndone+
    /// UndoneAt und schreibt einen Audit-ScanEvent vom Typ UndoApplied.
    /// </summary>
    Task<UndoResult> UndoAsync(int scanEventId, CancellationToken ct = default);

    /// <summary>
    /// Vollstaendige Verlauf-Liste (auch nicht-undobare Aktionen + bereits
    /// undonete Eintraege). Fuer den Verlauf-Tab.
    /// </summary>
    Task<List<HistoryEntry>> GetHistoryAsync(int limit = 200, CancellationToken ct = default);
}

/// <summary>Eine rueckgaengigmachbare Aktion in der Verlaufs-Liste.</summary>
public record UndoableAction(
    int Id,
    DateTime Timestamp,
    ScanType Type,
    string Description);

/// <summary>Ergebnis eines Undo-Versuchs.</summary>
public record UndoResult(bool Success, string? ErrorMessage)
{
    public static UndoResult Ok() => new(true, null);
    public static UndoResult Fail(string msg) => new(false, msg);
}

/// <summary>
/// Eintrag im Verlauf-Tab. CanUndo=false fuer Typen ohne UndoData
/// oder bereits undonete Aktionen.
/// </summary>
public record HistoryEntry(
    int Id,
    DateTime Timestamp,
    ScanType Type,
    string Description,
    bool CanUndo,
    bool WasUndone,
    DateTime? UndoneAt);
