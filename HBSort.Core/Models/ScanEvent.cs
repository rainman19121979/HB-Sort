namespace HBSort.Core.Models;

/// <summary>
/// Protokolliert jeden Scan-Vorgang fuer die Undo-Funktion und Statistik.
/// </summary>
public class ScanEvent
{
    public int Id { get; set; }

    /// <summary>Zeitpunkt des Scans</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>War es ein Figur-Scan oder ein Einzelteil-Scan?</summary>
    public ScanType Type { get; set; }

    /// <summary>ID des erkannten Objekts (Rebrickable-ID oder Teilenummer)</summary>
    public string? RecognizedId { get; set; }

    /// <summary>Brickognize-Konfidenz (0.0 bis 1.0)</summary>
    public double? Confidence { get; set; }

    /// <summary>Pfad zum gespeicherten Scan-Bild im scans-Ordner</summary>
    public string? ImagePath { get; set; }

    /// <summary>Beschreibung des Ergebnisses fuer die Anzeige</summary>
    public string ResultDescription { get; set; } = string.Empty;

    /// <summary>Wurde dieser Scan per Undo rueckgaengig gemacht?</summary>
    public bool WasUndone { get; set; }

    /// <summary>
    /// UX X.29 (v0.1.16): Zeitpunkt zu dem die Aktion rueckgaengig gemacht
    /// wurde. Null = noch nicht. Doppel-Undo wird ueber WasUndone+UndoneAt
    /// blockiert.
    /// </summary>
    public DateTime? UndoneAt { get; set; }

    /// <summary>
    /// UX X.29 (v0.1.16): JSON-serialisierter Snapshot, der das Undo
    /// ermoeglicht. Pro <see cref="ScanType"/> ein eigenes Format - siehe
    /// HBSort.Core.Services.UndoSnapshot* Records. Null bei Aktionen die
    /// nicht rueckgaengig gemacht werden koennen (z.B. Import,
    /// FloatingPartExported).
    /// </summary>
    public string? UndoData { get; set; }
}

/// <summary>
/// Typ des ScanEvents.
///
/// HasConversion&lt;string&gt;() in UserDataContext - neue Werte sind ohne
/// DB-Migration moeglich (TEXT-Spalte). Trotzdem hier alle Werte in einer
/// stabilen Reihenfolge halten, damit alte ScanEvents bei Re-Read korrekt
/// gemappt werden.
///
///   MinifigScan / PartScan        - klassische Brickognize-Scans
///   FloatingPartTransfer          - User uebernimmt ein Teil aus einem
///                                   Lagerfach in eine wartende oder gerade
///                                   gescannte Pending-Minifigur (UX X.4+).
///   FloatingPartExported          - FloatingPart wurde im BSX-Export
///                                   beruecksichtigt und beim Cleanup aus
///                                   der DB entfernt (Phase 7-Erweiterung).
///                                   ResultDescription enthaelt die Quell-
///                                   Bin-Info als Audit-Trail.
///
/// UX X.29 (v0.1.16): Undo-faehige Aktions-Typen. Werden vom UndoService
/// verarbeitet, UndoData enthaelt einen typ-spezifischen JSON-Snapshot.
///   Delete            - Minifig oder FloatingPart geloescht
///   Move              - Minifig in anderen Bin verschoben
///   Complete          - Teil/Figur als komplett markiert (manuell)
///   BinFreed          - Bin wurde freigegeben (FreedAt gesetzt)
///   BinCreated        - Ein oder mehrere Bins wurden angelegt
///   Import            - BL-Daten-Import (NICHT undobar - Backup nutzen)
///   UndoApplied       - Audit: ein anderer ScanEvent wurde rueckgaengig
///                       gemacht. Selbst nicht erneut undobar.
///
/// v0.1.24-beta.8 Phase 3: BL-Inventar-Reservierungen.
///   BlInventoryReservation - User hat ein Lot im BL-Shop fuer eine wartende
///                            Figur reserviert. RecognizedId = LotId,
///                            ResultDescription = "Reserviert: {PartName}
///                            ({Condition}) fuer {MinifigName}".
///   BlInventoryRelease     - Reservierung wieder aufgehoben (Undo).
/// </summary>
public enum ScanType
{
    MinifigScan,
    PartScan,
    FloatingPartTransfer,
    FloatingPartExported,
    Delete,
    Move,
    Complete,
    BinFreed,
    BinCreated,
    Import,
    UndoApplied,
    BlInventoryReservation,
    BlInventoryRelease
}
