using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// Phase 5: zentraler Service fuer Modus-B (Einzelteil-Scan).
///
/// Zustaendigkeiten:
///   1. Lookup eines Teils + Farbe -> Liste der wartenden Figuren die das Teil brauchen
///   2. Aktion "Teil zu Figur zuordnen" (QuantityCollected++)
///   3. Aktion "Teil als Floating-Part lagern"
///   4. Aktion "Neue Figur aus Supersets-Treffer sammeln" (anlegen + Trigger-Teil mitgeben)
///   5. Inverse-Aktion "Teil von Figur entfernen" (QuantityCollected=0)
/// </summary>
public interface IPartLookupService
{
    /// <summary>
    /// Liest Teil-Stammdaten (Name, Farbe, RGB) aus dem Cache und ermittelt
    /// alle wartenden Figuren die dieses Teil noch brauchen.
    /// </summary>
    Task<PartLookupResult> LookupPartAsync(string blPartNo, int blColorId, CancellationToken ct = default);

    /// <summary>
    /// Erhoeht <c>QuantityCollected</c> eines konkreten TrackedMinifigPart um 1.
    /// Cap-Logik symmetrisch zum Complete-Check (Effective-aware):
    /// <list type="bullet">
    ///   <item>Physisch bereits voll (<c>Coll &gt;= Need</c>) → ablehnen.</item>
    ///   <item>Effektiv voll (<c>Coll + Res &gt;= Need</c>) UND keine
    ///   Reservierung zum Aufloesen → ablehnen.</item>
    /// </list>
    /// Setzt Status=Complete + CompletedAt wenn alle Parts der Figur danach
    /// effektiv komplett sind. Schreibt einen <c>PartScan</c>-ScanEvent.
    ///
    /// <para><b>v0.1.24-beta.13 (V5):</b> wenn vor dem Inkrement
    /// <c>QuantityReservedFromBl &gt; 0</c> ist UND der bevorstehende Stand
    /// die effektive Bedarfsmenge uebersteigen wuerde
    /// (<c>(QuantityCollected+1) + QuantityReservedFromBl &gt; QuantityNeeded</c>),
    /// wird die juengste offene BL-Reservierung fuer dieses Part ueber
    /// <see cref="IBlInventoryService.ReleaseSingleReservationAsync"/>
    /// aufgeloest. <c>EffectiveCollected</c> bleibt dabei invariant — nur
    /// die Quelle wechselt von "im BL reserviert" zu "physisch gesammelt".
    /// Die Lot-Daten (Condition + Remarks + Figur-Bin) werden im
    /// <see cref="BlReservationReleaseInfo"/>-Result mitgeliefert, damit der
    /// Caller eine passende SortInstruction zeigen kann (U-Lot: Teil zurueck
    /// in BL-Shop; N-Lot: Tausch).</para>
    /// </summary>
    Task<AssignPartResult> AssignPartToMinifigAsync(int trackedMinifigPartId, CancellationToken ct = default);

    /// <summary>
    /// Inverse-Aktion: setzt QuantityCollected auf 0. Falls die Figur vorher
    /// Complete war, wird sie auf Waiting zurueckgesetzt (CompletedAt=null).
    /// </summary>
    /// <returns>true wenn der Eintrag existierte und veraendert wurde.</returns>
    Task<bool> UnassignPartFromMinifigAsync(int trackedMinifigPartId, CancellationToken ct = default);

    /// <summary>
    /// Legt das Teil als FloatingPart in das angegebene Fach. Wenn schon ein
    /// FloatingPart mit gleichem (BlPart, BlColor, Bin) existiert, wird Quantity addiert.
    ///
    /// UX X.33 Block N (v0.1.19-beta.7): <paramref name="brickognizeCategory"/>
    /// optional - wird beim Anlegen am FloatingPart gespeichert. Beim
    /// Stapel-Match wird die bestehende Category beibehalten (nicht
    /// ueberschrieben). Null/Empty = "Unbekannt"-Kategorie fuer die
    /// Default-Regel "max 1 PartId pro Kategorie pro Bin".
    /// </summary>
    Task<FloatingPart> AddPartToFloatingAsync(
        string blPartNo, int blColorId, string partName, string colorName,
        int quantity, int storageBinId,
        string? brickognizeCategory = null,
        CancellationToken ct = default);

    /// <summary>
    /// "Diese Figur sammeln" aus dem Supersets-Dialog: legt eine neue
    /// TrackedMinifig (Status=Waiting) im gewaehlten Fach an, baut die
    /// RequiredParts aus dem BL-Cache (oder via 1 BL-Call wenn noetig),
    /// setzt das Trigger-Teil sofort auf collected und macht Reverse-Match
    /// gegen FloatingParts.
    /// </summary>
    Task<TrackedMinifig> CollectMinifigFromSupersetAsync(
        string blMinifigId, int storageBinId,
        string triggerPartNo, int triggerColorId, int triggerQuantity,
        CancellationToken ct = default);

    /// <summary>
    /// UX X.32 v0.1.19-beta.4 Block D: erweiterte Variante von
    /// <see cref="CollectMinifigFromSupersetAsync"/> mit User-Auswahl welche
    /// Reverse-Match-Teile konsumiert werden sollen. Statt automatisch ALLE
    /// matchenden FloatingParts zu nehmen, wird nur fuer die in
    /// <paramref name="consumePartsFromFloating"/> genannten (PartNo, ColorId)
    /// ein Reverse-Match versucht. Andere Required-Parts bleiben "fehlt /
    /// wartend".
    ///
    /// v0.1.24-beta.1 Phase 2a (Konzept 6.3 / Befund A3): optionaler Parameter
    /// <paramref name="manuellClaimedParts"/> markiert weitere Required-Parts
    /// direkt als gesammelt OHNE FloatingPart-Effekt - User hat das Teil
    /// physisch in der Hand, will es aber nicht aus dem Lager nehmen. Der
    /// Service erhoeht QuantityCollected (cap auf QuantityNeeded) und
    /// vermerkt die Anzahl im ScanEvent-ResultDescription. Bei null/empty
    /// laeuft der alte Pfad unveraendert (Backwards-Compat).
    ///
    /// Liefert ein <see cref="CollectMinifigResult"/> mit der angelegten
    /// Figur, dem Complete-Status und den konsumierten FloatingParts (fuer
    /// das Sammel-Popup im UI-Layer).
    /// </summary>
    Task<CollectMinifigResult> CollectMinifigFromSupersetWithSelectionAsync(
        string blMinifigId, int storageBinId,
        string triggerPartNo, int triggerColorId, int triggerQuantity,
        IReadOnlyCollection<(string PartNo, int ColorId)> consumePartsFromFloating,
        IReadOnlyCollection<(string PartNo, int ColorId)>? manuellClaimedParts = null,
        CancellationToken ct = default);

    /// <summary>
    /// Loescht einen FloatingPart-Eintrag komplett aus der DB (User-Aktion in
    /// der Lagerliste). Liefert true wenn der Eintrag existierte.
    /// </summary>
    Task<bool> DeleteFloatingPartAsync(int floatingPartId, CancellationToken ct = default);

    /// <summary>
    /// Findet alle Lagerfaecher in denen das Teil (BL-Part-No + BL-Color-Id) bereits
    /// als Einzelteil liegt. Sortiert absteigend nach Gesamt-Menge im Fach - das
    /// erste Element ist das "Smart-Default-Fach" fuer den DismantleWizard.
    /// </summary>
    Task<List<FloatingPartLocation>> FindFloatingLocationsAsync(
        string blPartNo, int blColorId, CancellationToken ct = default);

    /// <summary>
    /// v0.1.22-beta.1 Block C: Bulk-Variante von <see cref="FindFloatingLocationsAsync"/>.
    /// Liefert pro angefragtem (PartNo, ColorId)-Paar die Floating-Locations.
    /// Nicht in der Eingabe vorhandene Paare tauchen NICHT im Ergebnis auf -
    /// der Aufrufer kann ueber das Dict iterieren und fehlende Keys als
    /// "kein Floating vorhanden" interpretieren.
    /// Eine einzige DB-Query statt N+1 - relevant fuer LoadAsync-Pfade in
    /// CollectMinifigSelection und DismantleWizard.
    /// </summary>
    Task<Dictionary<(string PartNo, int ColorId), List<FloatingPartLocation>>>
        FindFloatingLocationsForManyAsync(
            IReadOnlyCollection<(string PartNo, int ColorId)> keys,
            CancellationToken ct = default);
}

/// <summary>
/// v0.1.24-beta.13 (V5): Ergebnis von <see cref="IPartLookupService.AssignPartToMinifigAsync"/>.
/// </summary>
/// <param name="Assigned">True wenn QuantityCollected um 1 erhoeht wurde. False wenn das Teil bereits effektiv komplett war (Effective &gt;= Need und keine Reservierung zum Aufloesen).</param>
/// <param name="MinifigCompleted">True wenn die Figur durch diese Aktion auf Status=Complete uebergegangen ist.</param>
/// <param name="Released">Details zur aufgeloesten BL-Reservierung (Lot-Condition, Lot-Remarks, Figur-Bin) falls eine aufgeloest wurde. Null wenn normaler Konsum ohne Reservierungs-Interaktion.</param>
public record AssignPartResult(
    bool Assigned,
    bool MinifigCompleted,
    BlReservationReleaseInfo? Released);

/// <summary>
/// v0.1.24-beta.13 (V5): Detail-Infos zur aufgeloesten BL-Reservierung.
/// Vom Caller benutzt um eine passende SortInstruction zu bauen:
/// <list type="bullet">
///   <item><b>U-Lot (Gebraucht)</b>: gescanntes Teil zurueck in BL-Shop-Fach
///   (<see cref="LotRemarks"/>) — die Figur behaelt das BL-Teil das beim
///   Reservieren mental schon entnommen wurde.</item>
///   <item><b>N-Lot (Neu)</b>: Tausch — das neue Teil aus
///   <see cref="MinifigBinLabel"/> zurueck in BL-Shop-Fach
///   (<see cref="LotRemarks"/>), das gescannte (gebrauchte) Teil in das
///   Figur-Fach.</item>
/// </list>
/// </summary>
public record BlReservationReleaseInfo(
    int LotId,
    string LotCondition,        // "N" oder "U"
    string? LotRemarks,         // BL-Shop-Lagerplatz; null/leer = "(kein Lagerplatz)"
    string? MinifigBinLabel,    // Bin-Label der Figur (fuer N-Tausch)
    string PartName,
    string ColorName);

/// <summary>Ein Fach das ein bestimmtes Teil bereits als Einzelteil enthaelt.</summary>
public record FloatingPartLocation(
    int StorageBinId,
    string StorageBinLabel,
    int TotalQuantity);

/// <summary>
/// UX X.32 v0.1.19-beta.4 Block D: Ergebnis eines
/// <see cref="IPartLookupService.CollectMinifigFromSupersetWithSelectionAsync"/>-
/// Aufrufs. Liefert die angelegte Figur PLUS die Detail-Info ueber
/// konsumierte FloatingParts (fuer das Sammel-Popup im UI).
/// </summary>
public class CollectMinifigResult
{
    public TrackedMinifig SavedMinifig { get; init; } = null!;
    public bool IsFullyComplete { get; init; }
    public List<ConsumedFloatingPartInfo> ConsumedFloatingParts { get; init; } = new();
}

/// <summary>Ergebnis eines LookupPartAsync-Aufrufs.</summary>
public record PartLookupResult(
    string BlPartNo,
    int BlColorId,
    string PartName,
    string ColorName,
    string? ColorRgb,
    List<WaitingMinifigMatch> WaitingMatches,
    List<BlCatalogMatch> BlCatalogMatches);

/// <summary>
/// Eine Minifig aus dem BL-Catalog-Cache die das angefragte Teil enthaelt
/// (aber NICHT in den wartenden Figuren des Users vorkommt).
/// </summary>
public record BlCatalogMatch(
    string BlMinifigId,
    string? MinifigName,
    string? MinifigImageUrl,
    int QuantityInMinifig);

/// <summary>
/// Eine Figur die das angefragte Teil physisch noch braucht
/// (<c>QuantityCollected &lt; QuantityNeeded</c>). Kann sowohl
/// Status=Waiting als auch Status=Complete (komplett via offener
/// BL-Reservierung) sein — die UI zeigt das ueber das Badge-Feld.
/// </summary>
public record WaitingMinifigMatch(
    int TrackedMinifigPartId,
    int TrackedMinifigId,
    string BlMinifigId,
    string MinifigName,
    string? MinifigImageUrl,
    string? StorageBinLabel,
    int StorageBinId,
    int QuantityNeeded,
    int QuantityCollected,
    bool IsAlternate,
    // v0.1.24-beta.13 (V5 Fortsetzung): UI-Anzeige unterscheidet
    // "wirklich fehlend" vs "Reservierung wird aufgeloest". Defaults
    // null/false damit bestehende Test-Konstruktor-Aufrufe ohne
    // Reservierungs-Kontext nicht angefasst werden muessen.
    int QuantityReservedFromBl = 0,
    bool IsMinifigComplete = false);
