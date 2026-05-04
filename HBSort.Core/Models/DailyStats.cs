namespace HBSort.Core.Models;

/// <summary>
/// Tagesstatistik: wie viele Scans, wie viele Figuren komplettiert/zerlegt.
/// Ein Eintrag pro Tag, das Datum ist der Primaerschluessel.
/// </summary>
public class DailyStats
{
    /// <summary>Datum (nur Tag, ohne Uhrzeit) - dient als Primaerschluessel</summary>
    public DateTime Date { get; set; }

    /// <summary>Anzahl der Scans an diesem Tag</summary>
    public int ScanCount { get; set; }

    /// <summary>Wie viele Figuren wurden an diesem Tag komplettiert</summary>
    public int MinifigsCompletedCount { get; set; }

    /// <summary>Wie viele Figuren wurden an diesem Tag zerlegt</summary>
    public int MinifigsDismantledCount { get; set; }
}
