using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// Persistiert eine erkannte Minifigur (PendingMinifig) in userdata.db
/// und macht den Reverse-Match gegen vorhandene FloatingParts:
/// passende Floating-Parts werden in das Fach der Figur "umgebucht"
/// (FloatingPart geloescht, QuantityCollected des passenden Required-Parts erhoeht).
///
/// Phase 4 minimal: vor allem Speicherung + Reverse-Match.
/// Modus-B (Einzelteil-Scan) kommt in Phase 5.
/// </summary>
public interface IMinifigPersistenceService
{
    Task<PersistMinifigResult> PersistAndStoreAsync(
        PersistMinifigInput input,
        CancellationToken ct = default);

    /// <summary>
    /// Wird gefeuert wenn sich der TrackedMinifigs- oder FloatingParts-Bestand
    /// veraendert hat (Persistierung, Aufgeben, Verschieben). Listener wie die
    /// Wartende-Figuren-Liste koennen sich daran haengen.
    /// </summary>
    event EventHandler? DataChanged;

    /// <summary>Manuell DataChanged feuern (z.B. nach externen Updates wie Aufgeben).</summary>
    void RaiseDataChanged();

    /// <summary>
    /// "Aufgeben"-Workflow: setzt Figur auf DISMANTLED, loest sie vom Fach, und
    /// uebernimmt die als KEEP markierten Required-Parts als FloatingParts in das
    /// jeweils gewaehlte Ziel-Fach (mit OriginMinifigId=Figur). Verworfene Teile
    /// landen NICHT im Floating-Pool.
    /// </summary>
    Task<DismantleResult> DismantleAsync(int trackedMinifigId,
        IEnumerable<DismantlePartChoice> choices,
        CancellationToken ct = default);

    /// <summary>
    /// Loescht eine Figur komplett aus der DB. RequiredParts werden via Cascade
    /// mitgeloescht. FloatingParts mit OriginMinifigId=Figur bleiben bestehen -
    /// ihr OriginMinifigId wird auf null gesetzt (sie werden in der Lagerliste
    /// dann als "lose" angezeigt).
    /// </summary>
    Task<bool> DeleteAsync(int trackedMinifigId, CancellationToken ct = default);

    /// <summary>
    /// Beim App-Start: Alle Figuren mit Status=Dismantled werden komplett geloescht
    /// (alter Code-Pfad). Liefert Anzahl geloeschter Eintraege.
    /// </summary>
    Task<int> CleanupOldDismantledMinifigsAsync(CancellationToken ct = default);

    /// <summary>
    /// Beim App-Start: Pseudo-Figuren aus dem alten "Diese Figur anlegen"-Bug
    /// loeschen - Status=COMPLETE mit genau 1 RequiredPart. Diese sind durch den
    /// Single-Row-Supersets-Cache entstanden, bevor IsFromSupersets eingefuehrt wurde.
    /// </summary>
    Task<int> CleanupOnePartCompletesAsync(CancellationToken ct = default);

    /// <summary>
    /// Phase 6: pruefen ob eine wartende Figur nach manueller Quantity-Aenderung
    /// (z.B. Pending-Klick im Summary-Dialog) jetzt komplett ist. Wenn ja:
    /// Status=Complete, CompletedAt=jetzt, DailyStats.Completed +1.
    /// Liefert true wenn die Figur in diesem Aufruf erst komplettiert wurde.
    /// </summary>
    Task<bool> CheckAndMarkCompleteAsync(int trackedMinifigId, CancellationToken ct = default);

    /// <summary>
    /// Phase 6: eine als Complete markierte Figur wieder auf Waiting setzen
    /// (User-Korrektur, "Wieder oeffnen"-Button im Summary-Dialog).
    /// CompletedAt wird auf null gesetzt; DailyStats wird NICHT veraendert
    /// (der Tag, an dem die Figur komplett war, bleibt korrekt gezaehlt).
    /// </summary>
    Task<bool> ReopenAsync(int trackedMinifigId, CancellationToken ct = default);

    /// <summary>
    /// Phase 7: BSX-Export-Cleanup. Entfernt alle uebergebenen Figuren
    /// aus der DB (RequiredParts via Cascade). FloatingParts mit
    /// OriginMinifigId in der ID-Liste werden auf null gesetzt (sie
    /// bleiben als lose Teile bestehen). Schreibt einen ScanEvent als
    /// Audit-Trail ("BSX-Export: N Figur(en) entfernt"). Liefert die
    /// Anzahl tatsaechlich entfernter Figuren.
    /// </summary>
    Task<int> RemoveExportedMinifigsAsync(
        IEnumerable<int> minifigIds,
        CancellationToken ct = default);

    /// <summary>
    /// Phase 7-Erweiterung: BSX-Export-Cleanup fuer Einzelteile. Entfernt die
    /// uebergebenen FloatingParts komplett aus der DB. Pro entferntem Teil wird
    /// ein ScanEvent vom Typ <see cref="HBSort.Core.Models.ScanType.FloatingPartExported"/>
    /// geschrieben - im ResultDescription stehen Quell-Bin-Label, Part-No,
    /// Color-Id und Quantity, damit man auch nach spaeterer Bin-Freigabe noch
    /// nachvollziehen kann wo das Teil herkam.
    /// Liefert die Anzahl tatsaechlich entfernter Eintraege.
    /// </summary>
    Task<int> RemoveExportedFloatingPartsAsync(
        IEnumerable<int> floatingPartIds,
        CancellationToken ct = default);

    /// <summary>
    /// UX X.29 Block C (v0.1.16): Bulk-Loeschen aus der Lagerliste.
    /// Loescht die uebergebenen Minifigs UND FloatingParts in einer
    /// atomaren Transaction. Pro geloeschtes Item wird ein ScanEvent
    /// vom Typ Delete + UndoData geschrieben (analog Block-B-Mechanik) -
    /// Strg+Z kann Undo machen, Item fuer Item ueber den Verlauf-Tab.
    ///
    /// Liefert (deletedMinifigs, deletedFloatings).
    /// Fehler wirft - kein Halbzustand.
    /// </summary>
    Task<(int Minifigs, int FloatingParts)> DeleteSelectionAsync(
        IEnumerable<int> minifigIds,
        IEnumerable<int> floatingPartIds,
        CancellationToken ct = default);

    /// <summary>
    /// UX X.29 Block C (v0.1.16): Bulk-Verschieben aus der Lagerliste.
    /// Setzt StorageBinId fuer die uebergebenen Minifigs und FloatingParts
    /// auf <paramref name="targetBinId"/>. Falls der Ziel-Bin als frei
    /// markiert war (FreedAt!=null), wird er reaktiviert.
    ///
    /// Pro verschobenes Item ein ScanEvent vom Typ Move + UndoData -
    /// Strg+Z stellt die alten StorageBinIds wieder her.
    ///
    /// Liefert (movedMinifigs, movedFloatings).
    /// Fehler wirft - kein Halbzustand. Wirft auch wenn der targetBin
    /// nicht existiert.
    /// </summary>
    Task<(int Minifigs, int FloatingParts)> MoveSelectionAsync(
        IEnumerable<int> minifigIds,
        IEnumerable<int> floatingPartIds,
        int targetBinId,
        CancellationToken ct = default);
}

/// <summary>
/// Eine Part-Wahl im DismantleWizard.
///
/// IsKept=false: Teil wird verworfen (kein FloatingPart, keine Zuordnung).
/// IsKept=true:  GENAU EINE der folgenden Properties muss gesetzt sein:
///   - TargetBinId             -> Teil wird als FloatingPart in das Fach gelegt
///   - AssignToTrackedMinifigPartId -> Teil wird einer wartenden Figur direkt
///                                     zugeordnet (UX X.25)
/// Validierung in DismantleAsync wirft InvalidOperationException wenn beide
/// gesetzt sind oder beide null bei IsKept=true.
/// </summary>
public class DismantlePartChoice
{
    public int TrackedMinifigPartId { get; init; }
    public bool IsKept { get; init; }
    public int? TargetBinId { get; init; }

    /// <summary>
    /// UX X.25: ID eines wartenden TrackedMinifigPart das diesem Teil-Slot
    /// zugeordnet werden soll. Wenn gesetzt, wird statt FloatingPart anlegen
    /// die wartende Figur inkrementiert (analog AssignPartToMinifigAsync).
    /// </summary>
    public int? AssignToTrackedMinifigPartId { get; init; }
}

/// <summary>Ergebnis von DismantleAsync.</summary>
public class DismantleResult
{
    /// <summary>True wenn der Vorgang erfolgreich war (Figur existierte).</summary>
    public bool Success { get; init; }

    /// <summary>Anzahl FloatingPart-Eintraege die angelegt/aktualisiert wurden.</summary>
    public int CreatedFloatingParts { get; init; }

    /// <summary>Gesamt-Anzahl Einzelteile die in den Pool gewandert sind (Summe der Mengen).</summary>
    public int TotalPartsTransferred { get; init; }

    /// <summary>UX X.25: Anzahl Teile die direkt einer wartenden Figur zugeordnet wurden.</summary>
    public int AssignedToWaitingCount { get; init; }

    /// <summary>
    /// UX X.25: Namen der wartenden Figuren die durch diese Aktion komplett geworden sind.
    /// Vom UI-Layer fuer Toast-Notifications genutzt ("Figur X ist jetzt komplett!").
    /// </summary>
    public List<string> CompletedMinifigNames { get; init; } = new();

    // Legacy-Felder (nicht mehr aktiv befuellt, fuer aeltere Aufrufer kompatibel)
    public int KeptCount { get; init; }
    public int DiscardedCount { get; init; }
    public int TotalKeptQuantity { get; init; }
}

/// <summary>Daten, die das ViewModel uebergibt.</summary>
public class PersistMinifigInput
{
    /// <summary>BrickLink-ID der erkannten Figur (z.B. "arc007").</summary>
    public string BricklinkId { get; init; } = string.Empty;

    /// <summary>Anzeigename aus BL.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Bild-URL (BL).</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Lokal gecachter Bildpfad (vom PartImageProvider).</summary>
    public string? LocalImagePath { get; init; }

    /// <summary>User-Notiz.</summary>
    public string? UserNotes { get; init; }

    /// <summary>ID des Lagerfaches (StorageBin), in dem die Figur landet.</summary>
    public int StorageBinId { get; init; }

    /// <summary>Brickognize-Konfidenz - fuer ScanEvent.</summary>
    public double? Confidence { get; init; }

    /// <summary>Pfad zum Scan-Bild (im scans-Ordner) - fuer ScanEvent.</summary>
    public string? ScanImagePath { get; init; }

    /// <summary>Required-Parts der Figur. Werden 1:1 in TrackedMinifigPart kopiert.</summary>
    public List<PersistMinifigPart> RequiredParts { get; init; } = new();
}

/// <summary>Ein Required-Part beim Persistieren.</summary>
public class PersistMinifigPart
{
    public string BricklinkPartNo { get; init; } = string.Empty;
    public int BricklinkColorId { get; init; }
    public string PartName { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public int QuantityNeeded { get; init; }

    /// <summary>
    /// Wieviele Teile dieser Sorte beim Speichern bereits als gesammelt
    /// gelten (manuelle Markierung in der Pending-View). Wird vom Reverse-
    /// Match weiter aufgefuellt; final cap auf QuantityNeeded.
    /// </summary>
    public int QuantityCollected { get; init; }
}

/// <summary>Ergebnis von PersistAndStoreAsync.</summary>
public class PersistMinifigResult
{
    /// <summary>Die persistierte Figur (mit Id != 0 nach SaveChanges).</summary>
    public TrackedMinifig SavedMinifig { get; init; } = null!;

    /// <summary>Anzahl Floating-Parts die per Reverse-Match konsumiert wurden.</summary>
    public int ReverseMatchedFloating { get; init; }

    /// <summary>Anzahl Required-Parts die durch Reverse-Match komplett gefuellt sind.</summary>
    public int CompletedRequiredParts { get; init; }

    /// <summary>True wenn nach Reverse-Match alle Required-Parts komplett sind.</summary>
    public bool IsFullyComplete { get; init; }

    /// <summary>
    /// UX X.32 Block C (v0.1.19): Pro konsumiertem FloatingPart eine Info-
    /// Zeile fuers Sammel-Popup. Wird nur befuellt wenn Reverse-Match
    /// tatsaechlich Teile genommen hat. Bin-Label ist das Quell-Fach
    /// (wo der FloatingPart vorher lag).
    /// </summary>
    public List<ConsumedFloatingPartInfo> ConsumedFloatingParts { get; init; } = new();
}

/// <summary>
/// UX X.32 Block C (v0.1.19): Info-Block ueber einen einzelnen Reverse-
/// Match-Konsum (vom UI-Layer als BinInstructionItem visualisiert).
/// </summary>
public class ConsumedFloatingPartInfo
{
    public string BlPartNo { get; init; } = string.Empty;
    public int BlColorId { get; init; }
    public string PartName { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public int Quantity { get; init; }

    /// <summary>Label des Quell-Fachs (wo der FloatingPart konsumiert wurde).</summary>
    public string SourceBinLabel { get; init; } = string.Empty;
}
