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
    /// mitgeloescht. FloatingParts mit OriginMinifigId=Figur bleiben bestehen –
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
    /// loeschen — Status=COMPLETE mit genau 1 RequiredPart. Diese sind durch den
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
}

/// <summary>Eine Part-Wahl im DismantleWizard.</summary>
public class DismantlePartChoice
{
    public int TrackedMinifigPartId { get; init; }
    public bool IsKept { get; init; }
    public int? TargetBinId { get; init; }
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

    /// <summary>Brickognize-Konfidenz – fuer ScanEvent.</summary>
    public double? Confidence { get; init; }

    /// <summary>Pfad zum Scan-Bild (im scans-Ordner) – fuer ScanEvent.</summary>
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
}
