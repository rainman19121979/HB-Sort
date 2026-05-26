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
    /// Wird auch nach <see cref="ReserveAsync"/>/<see cref="ReleaseAsync"/>
    /// gefeuert damit der Inventar-Tab die ReservedQuantity-Spalte updated.
    /// </summary>
    event EventHandler? InventoryChanged;

    // ====================================================================
    // v0.1.24-beta.8 Phase 3: Komplettieren-Integration
    // ====================================================================

    /// <summary>
    /// Schneller Existence-Check fuer die UI: gibt es ueberhaupt
    /// BL-Inventar-Lots in der DB? Wird im Sortier-Tab + MinifigSummary
    /// + BuildSuggestions benutzt um BL-Hinweise auszublenden wenn der User
    /// noch nie synchronisiert hat.
    /// </summary>
    Task<bool> HasAnyInventoryAsync(CancellationToken ct = default);

    /// <summary>
    /// Sucht alle Lots die zu (PartNo, ColorId) passen. Sortierung:
    /// Neu vor Gebraucht, dann nach <c>Available = Quantity - ReservedQuantity</c>
    /// desc (best verfuegbares Lot zuerst). Lots ohne Verfuegbarkeit
    /// (Available &lt;= 0) werden NICHT zurueckgegeben.
    ///
    /// <para><paramref name="colorId"/>=null trifft auch farblose Lots (Minifig/Set).</para>
    /// </summary>
    Task<List<BlInventoryLot>> FindLotsForPartAsync(
        string blPartNo, int? colorId, CancellationToken ct = default);

    /// <summary>
    /// Reserviert <paramref name="qty"/> Einheiten eines Lots: erhoeht
    /// <see cref="BlInventoryLot.ReservedQuantity"/>. Liefert true bei
    /// Erfolg, false wenn das Lot nicht (mehr) existiert oder nicht
    /// genug verfuegbar ist. Atomar via SaveChanges. Feuert
    /// <see cref="InventoryChanged"/> bei Erfolg.
    /// </summary>
    Task<bool> ReserveAsync(int lotId, int qty = 1, CancellationToken ct = default);

    /// <summary>
    /// Hebt eine Reservierung wieder auf (verringert
    /// <see cref="BlInventoryLot.ReservedQuantity"/>). Genutzt vom Undo-
    /// Pfad. Liefert true bei Erfolg, false wenn das Lot nicht existiert
    /// oder die Reservierung schon 0 ist. Feuert
    /// <see cref="InventoryChanged"/> bei Erfolg.
    /// </summary>
    Task<bool> ReleaseAsync(int lotId, int qty = 1, CancellationToken ct = default);

    /// <summary>
    /// v0.1.24-beta.10 V1 (Audit-Befund H1): gibt ALLE noch offenen BL-
    /// Reservierungen frei, die zu den angegebenen TrackedMinifigs gehoeren.
    /// Wird vom <see cref="IMinifigPersistenceService"/> VOR jedem Loesch-/
    /// Zerlegungs-/Cleanup-Pfad aufgerufen, damit keine Geist-Reservierungen
    /// im BL-Lot-Spiegel zurueckbleiben.
    ///
    /// <para>Algorithmus (analog <c>MinifigSummaryViewModel.ReleaseAllBlReservationsAsync</c>):</para>
    /// <list type="number">
    ///   <item>RequiredParts der Minifigs mit <c>QuantityReservedFromBl &gt; 0</c> sammeln.</item>
    ///   <item>ScanEvents (Type=BlInventoryReservation, !WasUndone) materialisieren
    ///   und in-memory ueber <see cref="UndoSnapshotBlReservation"/>-JSON gegen die Part-Ids matchen.</item>
    ///   <item>LIFO: pro Match das Lot via <see cref="ReleaseAsync"/>-Semantik
    ///   freigeben (ReservedQuantity-- + neuer Release-ScanEvent + altes Event als undone markieren).</item>
    ///   <item><c>QuantityReservedFromBl</c> auf 0 setzen fuer die betroffenen Parts.
    ///   Cascade-Delete im Aufrufer-Context entfernt die Parts gleich danach -
    ///   das Setzen ist defensiv fuer standalone-Aufrufe.</item>
    /// </list>
    /// Liefert die Anzahl tatsaechlich freigegebener Reservierungen.
    /// Feuert <see cref="InventoryChanged"/> wenn mindestens eine Reservierung
    /// freigegeben wurde.
    /// </summary>
    Task<int> ReleaseAllForMinifigsAsync(IEnumerable<int> trackedMinifigIds, CancellationToken ct = default);
}
