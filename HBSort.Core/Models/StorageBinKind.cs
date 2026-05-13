namespace HBSort.Core.Models;

/// <summary>
/// v0.1.23: persistierter Bin-Typ. Ersetzt die berechnete BinKind-Logik
/// aus v0.1.22 (GetBinKindAsync). Schreib-Aktionen pflegen die Spalte
/// ueber RecalculateKindAsync. Lese-Pfade filtern direkt via WHERE Kind.
///
/// Reifungspfad: Wartend+Komplett im selben Bin bleibt Waiting.
///
/// Strict-Mode (v0.1.23): der Service wirft InvalidBinKindException wenn
/// eine Schreib-Aktion einen typ-inkompatiblen Ziel-Bin betrifft (z.B.
/// FloatingPart in einen Complete-Bin). Reverse-Match auf wartende Figuren
/// im Bin ist ein expliziter Bypass.
/// </summary>
public enum StorageBinKind
{
    /// <summary>Keine TrackedMinifigs, keine FloatingParts.</summary>
    Empty = 0,
    /// <summary>Nur FloatingParts, keine Minifigs.</summary>
    Floating = 1,
    /// <summary>Mindestens eine wartende Figur (plus optional komplette).</summary>
    Waiting = 2,
    /// <summary>Nur komplette Figuren, keine wartenden, keine FloatingParts.</summary>
    Complete = 3
}
