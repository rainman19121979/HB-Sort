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
    /// ein <see cref="BlInventorySyncResult"/> mit Zahl der Lots,
    /// erhaltenen/gecappten/verlorenen Reservierungen und Sync-Zeitpunkt.
    /// Idempotent - wiederholtes Aufrufen produziert denselben Stand.
    ///
    /// Wirft <see cref="BricklinkExceptions.BricklinkAuthException"/> wenn
    /// Tokens fehlen / falsch sind, und
    /// <see cref="BricklinkExceptions.BricklinkRateLimitException"/> wenn der
    /// eigene Hard-Threshold erreicht ist.
    ///
    /// <para>v0.1.24-beta.13: Rueckgabetyp von <c>int</c> auf
    /// <see cref="BlInventorySyncResult"/> erweitert, damit Aufrufer (z.B.
    /// MassUpdateExport-Dialog) sehen koennen ob/wie viele Reservierungen
    /// wegen geaenderter BL-Mengen gecappt wurden (V6-Cap, Audit H4).</para>
    /// </summary>
    Task<BlInventorySyncResult> SyncInventoryAsync(CancellationToken ct = default);

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
    /// v0.1.25 (BUILD-1): liefert die distinct <c>(PartNo, ColorId)</c>-Paare
    /// aller Lots mit verfuegbarer Menge (<c>Quantity - ReservedQuantity &gt; 0</c>).
    /// Wird vom Baubar-Tab genutzt um den Reverse-Lookup-Pool ueber den
    /// FloatingPool hinaus um BL-Inventar zu erweitern, damit Figuren erscheinen
    /// die zu 100% aus dem BL-Shop baubar waeren (auch wenn der User kein
    /// einziges Teil davon lose hat).
    ///
    /// <para>Nur Teile-Lots (<c>ItemType == "P"</c>) mit gesetzter ColorId
    /// kommen in den Pool: der Reverse-Lookup laeuft ueber Subset-Teile, und
    /// farblose Lots (<c>ColorId == null</c>, also Minifigs/Sets) ergeben in
    /// einem teile-basierten Match keinen sinnvollen Treffer. Sie werden daher
    /// uebersprungen — konsistent dazu, dass <see cref="FindLotsForPartAsync"/>
    /// fuer einen farblosen Lookup ausdruecklich <c>colorId == null</c>
    /// braucht und Subsets immer eine konkrete ColorId tragen.</para>
    /// </summary>
    Task<List<(string PartNo, int ColorId)>> GetAvailablePartTuplesAsync(CancellationToken ct = default);

    /// <summary>
    /// BUILD-3 (N+1-Hang Baubar-Tab): Bulk-Variante von
    /// <see cref="FindLotsForPartAsync"/> fuer die verfuegbaren Mengen vieler
    /// Teile in EINER DB-Query. Liefert pro angefragtem (PartNo, ColorId)-Paar
    /// die Summe der verfuegbaren Mengen (<c>Quantity - ReservedQuantity</c>,
    /// nur Lots mit Available &gt; 0) ueber alle passenden Teile-Lots
    /// (<c>ItemType == "P"</c>).
    ///
    /// <para>Ergebnis-Identitaet zu <see cref="FindLotsForPartAsync"/>:
    /// <c>AnalyzeBlShopHelpAsync</c> summierte bisher
    /// <c>lots.Sum(l =&gt; l.Quantity - l.ReservedQuantity)</c> ueber das
    /// Ergebnis von FindLotsForPartAsync (das bereits Available&lt;=0 ausfiltert).
    /// Diese Methode liefert exakt denselben Summen-Wert. Paare ohne verfuegbares
    /// Lot tauchen NICHT im Result auf (Caller behandelt fehlend wie 0 - identisch
    /// zur leeren Lot-Liste von FindLotsForPartAsync).</para>
    /// </summary>
    Task<Dictionary<(string PartNo, int ColorId), int>> GetAvailableQuantitiesAsync(
        IEnumerable<(string PartNo, int ColorId)> parts, CancellationToken ct = default);

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
    /// v0.1.24-beta.11: konsolidierte Reservierungs-Aktion fuer einen
    /// einzelnen TrackedMinifigPart. Atomar:
    /// <list type="number">
    ///   <item><see cref="ReserveAsync"/>(lotId, 1) - BL-Lot reservieren.</item>
    ///   <item><c>TrackedMinifigPart.QuantityReservedFromBl += 1</c>.</item>
    ///   <item>ScanEvent (Type=BlInventoryReservation) mit UndoData
    ///   (<see cref="UndoSnapshotBlReservation"/>) fuer Audit/Release.</item>
    /// </list>
    /// Bei DB-Fehler nach erfolgreichem Lot-Reserve wird via
    /// <see cref="ReleaseAsync"/> kompensiert (kein Geist-Lock).
    ///
    /// <para>Liefert <c>true</c> wenn die ganze Aktion durchlief; <c>false</c>
    /// wenn das Lot nicht reservierbar war oder das Teil nicht existiert.</para>
    /// </summary>
    Task<bool> ReserveForPartAsync(int trackedMinifigPartId, int lotId, CancellationToken ct = default);

    /// <summary>
    /// v0.1.24-beta.11: liefert pro reservierten Stueck eines Lots eine
    /// Detail-Zeile fuer das BL-Inventar-Tab Detail-Panel. Eine Zeile pro
    /// ScanEvent (Type=BlInventoryReservation, !WasUndone) mit LotId-Match;
    /// zusaetzlich pro Lot-Drift-Stueck (Lot.ReservedQuantity &gt; Anzahl
    /// matching ScanEvents) eine "Verwaiste Reservierung"-Zeile ohne
    /// ScanEvent-Bezug.
    ///
    /// <para>Sortierung: ScanEvent-Zeilen nach Timestamp absteigend (juengste
    /// zuerst), Drift-Zeilen am Ende.</para>
    /// </summary>
    Task<List<BlReservationDetail>> GetReservationsForLotAsync(int lotId, CancellationToken ct = default);

    /// <summary>
    /// v0.1.24-beta.13: Liefert den juengsten offenen BL-Reservierungs-
    /// ScanEvent fuer ein TrackedMinifigPart (LIFO). Wird vom Reverse-Match-
    /// Pfad in <c>AssignPartToMinifigAsync</c> genutzt um ein "physisches
    /// Teil ersetzt Reservierung"-Verhalten umzusetzen: vor dem
    /// QuantityCollected-Inkrement wird ueber den hier gelieferten ScanEvent
    /// eine Reservierung freigegeben (siehe
    /// <see cref="ReleaseSingleReservationAsync"/>).
    ///
    /// <para>Liefert (null, 0) wenn keine offene Reservierung fuer das Part
    /// existiert.</para>
    /// </summary>
    Task<(int? ScanEventId, int LotId)> FindLatestReservationScanEventAsync(
        int trackedMinifigPartId, CancellationToken ct = default);

    /// <summary>
    /// v0.1.24-beta.11: hebt eine einzelne Reservierung auf - entweder
    /// via ScanEvent (alle Buchungsschritte: Lot-Release, Event als undone
    /// markieren, Audit-Release-Event, ggf. TrackedMinifigPart.QuantityReservedFromBl
    /// dekrementieren und Figur-Status zurueck auf Waiting wenn sie dadurch
    /// nicht mehr komplett ist) oder als reine Drift-Bereinigung
    /// (<paramref name="scanEventId"/>=null: nur Lot.ReservedQuantity -1 +
    /// Audit-Event).
    ///
    /// <para>Liefert true bei Erfolg, false wenn das Lot oder der ScanEvent
    /// nicht (mehr) existiert / passt. Feuert <see cref="InventoryChanged"/>
    /// bei Erfolg.</para>
    /// </summary>
    Task<bool> ReleaseSingleReservationAsync(int? scanEventId, int lotId, CancellationToken ct = default);

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

    // ====================================================================
    // v0.1.24-beta.12 Phase 4: Mass-Update-Export
    // ====================================================================

    /// <summary>
    /// Generiert das BL-Mass-Update-XML fuer alle aktuell reservierten Mengen
    /// und schreibt einen Snapshot in <c>PendingExports</c>:
    /// <list type="bullet">
    ///   <item><c>ReservedQuantity &gt;= Quantity</c> → <c>&lt;ITEM&gt;&lt;LOTID&gt;x&lt;/LOTID&gt;&lt;DELETE/&gt;&lt;/ITEM&gt;</c>,
    ///   PendingExport.ShouldDelete=true</item>
    ///   <item>sonst → <c>&lt;ITEM&gt;&lt;LOTID&gt;x&lt;/LOTID&gt;&lt;QTY&gt;-{Reserved}&lt;/QTY&gt;&lt;/ITEM&gt;</c>,
    ///   PendingExport.ExpectedQuantity=Quantity-Reserved</item>
    /// </list>
    ///
    /// <para>Re-Generate: alte PendingExports werden komplett geloescht und
    /// durch den aktuellen Stand ersetzt. Liefert den fertigen XML-String
    /// zurueck (ggf. leeres Inventory wenn keine Reservierungen offen sind).</para>
    /// </summary>
    Task<MassUpdateExportResult> GenerateMassUpdateXmlAsync(CancellationToken ct = default);

    /// <summary>
    /// Verifiziert den letzten Mass-Update-Export gegen das frische BL-Inventar.
    /// Pro PendingExport:
    /// <list type="bullet">
    ///   <item>ShouldDelete=true + Lot fehlt im frischen Inventar → Erfolg</item>
    ///   <item>ShouldDelete=false + Ist-Menge == ExpectedQuantity → Erfolg</item>
    ///   <item>sonst → Fehler (Eintrag bleibt)</item>
    /// </list>
    ///
    /// <para>Pro erfolgreichem Eintrag werden die zugehoerigen BL-Reservierungen
    /// (offene <c>BlInventoryReservation</c>-ScanEvents zum LotId) in
    /// <c>QuantityCollected</c> umgewandelt: <c>QuantityCollected += 1</c>,
    /// <c>QuantityReservedFromBl -= 1</c>, ScanEvent als WasUndone markieren,
    /// neuer <c>BlReservationConvertedToCollected</c>-Audit-Event. Das Lot
    /// selbst bekommt <c>ReservedQuantity = 0</c>.</para>
    ///
    /// <para>Wenn ALLE PendingExports erfolgreich sind, wird abschliessend
    /// <see cref="SyncInventoryAsync"/> automatisch ausgefuehrt, damit
    /// Mengen + ggf. weggefallene Lots vom BL-Server frisch eingelesen werden.</para>
    /// </summary>
    Task<MassUpdateVerifyResult> VerifyExportAsync(CancellationToken ct = default);

    /// <summary>
    /// Liefert die Anzahl aktuell offener Mass-Update-Eintraege. UI nutzt das
    /// fuer den "Verifizieren"-Button (disabled wenn 0).
    /// </summary>
    Task<int> GetPendingExportCountAsync(CancellationToken ct = default);
}

/// <summary>
/// v0.1.24-beta.13: Result von <see cref="IBlInventoryService.SyncInventoryAsync"/>.
/// Vorher war der Rueckgabetyp <c>int</c> (nur LotCount); fuer den
/// MassUpdateExport-Dialog wird zusaetzlich gebraucht, ob beim Snapshot-
/// Replace Reservierungen wegen geaenderter BL-Mengen gecappt wurden
/// (V6-Cap, Audit H4) — der User soll sehen, dass HBSort etwas angepasst
/// hat, bevor er das Mass-Update-XML hochlaedt.
/// </summary>
public sealed class BlInventorySyncResult
{
    /// <summary>Anzahl der Lots nach dem Sync (frisch aus der BL-Antwort).</summary>
    public int LotCount { get; init; }

    /// <summary>
    /// Lots fuer die eine vorherige Reservierung &gt; 0 wiederhergestellt
    /// werden konnte (in der neuen BL-Antwort noch enthalten).
    /// </summary>
    public int RestoredReservations { get; init; }

    /// <summary>
    /// Lots fuer die die Reservierung wegen gesunkener BL-Quantity nach
    /// unten gecappt wurde. Wenn &gt; 0 hat HBSort die Buchhaltung
    /// nachgezogen — sichtbar als Hinweis im MassUpdateExport-Dialog.
    /// </summary>
    public int CappedReservations { get; init; }

    /// <summary>
    /// Reservierungen die komplett verloren gingen, weil das Lot nicht
    /// mehr in der BL-Antwort vorkam (ausverkauft / manuell geloescht).
    /// </summary>
    public int LostReservations { get; init; }

    /// <summary>UTC-Zeitpunkt des Syncs (= LastSyncedAt aller Lots).</summary>
    public DateTime SyncedAt { get; init; }
}

/// <summary>
/// v0.1.24-beta.12: Result von <see cref="IBlInventoryService.GenerateMassUpdateXmlAsync"/>.
/// </summary>
public sealed class MassUpdateExportResult
{
    public string Xml { get; init; } = string.Empty;
    public int TotalLots { get; init; }
    public int DeletedLots { get; init; }
    public int ReducedLots { get; init; }
}

/// <summary>
/// v0.1.24-beta.12: Result von <see cref="IBlInventoryService.VerifyExportAsync"/>.
/// </summary>
public sealed class MassUpdateVerifyResult
{
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public int RemainingCount { get; init; }
    public bool AllVerified => FailedCount == 0 && RemainingCount == 0;
    /// <summary>Detail-Liste pro PendingExport (fuer Toast/Log).</summary>
    public List<MassUpdateVerifyDetail> Details { get; init; } = new();
}

public sealed class MassUpdateVerifyDetail
{
    public int LotId { get; init; }
    public bool Success { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// v0.1.24-beta.11: Eine Detail-Zeile fuer "Reservierungen" im Lot-Detail-
/// Panel. Eine Zeile = 1 Stueck reservierte Menge. Bei Standard-Reservierungen
/// existiert ein ScanEvent + (meist) eine lebende Figur; bei Altlasten
/// (Geist-Reservierungen ohne lebende Figur oder reine Lot-Drift) ist
/// <see cref="IsOrphan"/> true.
/// </summary>
public sealed class BlReservationDetail
{
    /// <summary>ScanEvent-Id der ursprünglichen Reservierung; null bei reiner Lot-Drift ohne ScanEvent-Bezug.</summary>
    public int? ScanEventId { get; init; }

    public int LotId { get; init; }

    /// <summary>Immer 1 - eine Detail-Zeile pro reserviertem Stueck.</summary>
    public int Quantity { get; init; } = 1;

    /// <summary>Lebende Figur die die Reservierung haelt; null bei Orphan.</summary>
    public int? TrackedMinifigId { get; init; }

    /// <summary>Anzeige-Name der Figur (leer bei Orphan).</summary>
    public string FigureName { get; init; } = string.Empty;

    /// <summary>BL-ID der Figur fuer Anzeige + Bild-Lookup; null bei Orphan.</summary>
    public string? FigureBricklinkId { get; init; }

    /// <summary>True wenn keine lebende Figur dazu existiert (zerlegt/geloescht oder reine Lot-Drift).</summary>
    public bool IsOrphan { get; init; }

    /// <summary>Zeitpunkt der ursprünglichen Reservierung; null bei reiner Drift.</summary>
    public DateTime? ReservedAt { get; init; }
}
