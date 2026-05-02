namespace LegoMinifigSorter.Core.Models;

/// <summary>
/// Protokolliert jeden Scan-Vorgang für die Undo-Funktion und Statistik.
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

    /// <summary>Beschreibung des Ergebnisses für die Anzeige</summary>
    public string ResultDescription { get; set; } = string.Empty;

    /// <summary>Wurde dieser Scan per Undo rückgängig gemacht?</summary>
    public bool WasUndone { get; set; }
}

/// <summary>
/// Typ des Scans: Minifigur oder Einzelteil.
/// </summary>
public enum ScanType
{
    MinifigScan,
    PartScan
}
