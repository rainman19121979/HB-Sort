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
}

/// <summary>
/// Typ des ScanEvents:
///   MinifigScan / PartScan        - klassische Brickognize-Scans
///   FloatingPartTransfer          - User uebernimmt ein Teil aus einem
///                                   Lagerfach in eine wartende oder gerade
///                                   gescannte Pending-Minifigur (UX X.4+).
///   FloatingPartExported          - FloatingPart wurde im BSX-Export
///                                   beruecksichtigt und beim Cleanup aus
///                                   der DB entfernt (Phase 7-Erweiterung).
///                                   ResultDescription enthaelt die Quell-
///                                   Bin-Info als Audit-Trail.
/// HasConversion&lt;string&gt;() in UserDataContext - neue Werte sind ohne
/// DB-Migration moeglich (TEXT-Spalte).
/// </summary>
public enum ScanType
{
    MinifigScan,
    PartScan,
    FloatingPartTransfer,
    FloatingPartExported
}
