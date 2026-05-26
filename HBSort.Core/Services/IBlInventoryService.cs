using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// v0.1.24-beta.6 Phase 1: Service-Schicht ueber den lokalen BL-Inventar-
/// Spiegel. Phase 1 = manueller Sync + Read; Phase 2 wuerde Matching gegen
/// FloatingPart/TrackedMinifig liefern.
/// </summary>
public interface IBlInventoryService
{
    /// <summary>
    /// Holt das komplette BL-Store-Inventar via <see cref="IBricklinkClient.GetInventoryAsync"/>,
    /// loescht den lokalen Snapshot und speichert die neuen Lots. Liefert
    /// die Anzahl der nun gespeicherten Eintraege zurueck. Idempotent -
    /// wiederholtes Aufrufen produziert denselben Stand.
    ///
    /// Wirft <see cref="BricklinkExceptions.BricklinkAuthException"/> wenn
    /// Tokens fehlen / falsch sind, und
    /// <see cref="BricklinkExceptions.BricklinkRateLimitException"/> wenn der
    /// eigene Hard-Threshold erreicht ist.
    /// </summary>
    Task<int> SyncInventoryAsync(CancellationToken ct = default);

    /// <summary>
    /// Liest den aktuellen lokalen Inventar-Stand aus userdata.db. Kein
    /// BL-Call - reiner DB-Read. Sortiert nach ItemType, ItemNo, ColorId
    /// fuer stabile Reihenfolge.
    /// </summary>
    Task<List<BlInventoryLot>> GetInventoryAsync(CancellationToken ct = default);

    /// <summary>
    /// v0.1.24-beta.7 Phase 2: feuert nach jedem erfolgreichen
    /// <see cref="SyncInventoryAsync"/>. Damit kann der Inventar-Tab seine
    /// Liste auto-refreshen wenn der Sync aus dem Settings-Tab heraus
    /// ausgeloest wurde - eine Sync-Code-Quelle, mehrere UI-Konsumenten.
    /// </summary>
    event EventHandler? InventoryChanged;
}
