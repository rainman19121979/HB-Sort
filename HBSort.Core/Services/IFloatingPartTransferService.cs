namespace HBSort.Core.Services;

/// <summary>
/// UX-Iteration X.4+: "Teil aus Lagerfach in Pending-Minifig uebernehmen".
///
/// Dieser Service kapselt das Reduzieren eines FloatingPart-Eintrags um eine
/// Einheit (oder Loeschen wenn dadurch Quantity=0), die optionale Bin-Freigabe
/// und das Schreiben eines ScanEvents fuer den Audit-Trail.
///
/// Wichtig: Der Service ist UI-Zustand-agnostisch. Die "Pending-Minifig" der
/// gerade gescannten Figur lebt im ViewModel und wird dort hochgezaehlt -
/// nicht hier. Der Service garantiert nur dass der FloatingPart-Pool korrekt
/// reduziert wird.
/// </summary>
public interface IFloatingPartTransferService
{
    /// <summary>
    /// Liefert Info zum ersten passenden FloatingPart fuer die UI:
    /// "Existiert ein Teil in irgendeinem Fach?" + Anzeigename des Fachs.
    /// Schnell - kein Side-Effect.
    /// </summary>
    Task<FloatingPartMatch?> FindFirstMatchAsync(
        string blPartNo, int blColorId, CancellationToken ct = default);

    /// <summary>
    /// Reduziert den ersten passenden FloatingPart um 1 (loescht wenn dadurch
    /// Quantity=0), gibt ggf. das nun leere Fach frei und schreibt einen
    /// ScanEvent.
    ///
    /// Liefert ein TransferResult mit Source-Bin-Label (fuer Toast-Meldung)
    /// und Erfolg/Fehler. Kein Throw bei "kein Match" - der Aufrufer soll den
    /// Fall (z.B. Race-Condition) freundlich anzeigen koennen.
    /// </summary>
    /// <param name="targetMinifigDescription">
    /// Anzeigename der Ziel-Figur fuer den ScanEvent (z.B. "Arctic Forscher
    /// (arc007) - Pending"). Kann frei gewaehlt werden, dient nur dem
    /// Audit-Trail.
    /// </param>
    Task<FloatingPartTransferResult> TransferOneAsync(
        string blPartNo, int blColorId,
        string targetMinifigDescription,
        CancellationToken ct = default);
}

/// <summary>Match-Info fuer die UI - "wo liegt das Teil und wieviel ist da?".</summary>
public record FloatingPartMatch(
    int FloatingPartId,
    int StorageBinId,
    string StorageBinLabel,
    int QuantityAvailable);

/// <summary>Ergebnis eines Transfer-Vorgangs.</summary>
public record FloatingPartTransferResult(
    bool Success,
    string? SourceBinLabel,
    bool BinFreedAfterTransfer,
    string? ErrorMessage);
