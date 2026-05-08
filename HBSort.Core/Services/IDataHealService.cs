namespace HBSort.Core.Services;

/// <summary>
/// UX X.28 (2026-05-08): heilt Bug-B-Folgen aus alten Versionen.
/// Wird bei jedem App-Start aufgerufen, ist idempotent.
/// </summary>
public interface IDataHealService
{
    Task<DataHealResult> HealAsync(CancellationToken ct = default);
}

/// <summary>
/// Ergebnis eines Heal-Laufs.
/// RestoredBinAssignments: Bug-A-Folgen koennen NICHT automatisch geheilt werden
/// (urspruengliche Fach-Zuordnung verloren) - nur Log-Hinweis. Wert ist immer 0.
/// ResetFreedAtCount: Bins die belegt waren aber FreedAt!=null hatten - wurden korrigiert.
/// </summary>
public record DataHealResult(int RestoredBinAssignments, int ResetFreedAtCount);
