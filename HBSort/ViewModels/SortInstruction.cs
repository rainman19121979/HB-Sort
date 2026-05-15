namespace HBSort.ViewModels;

/// <summary>
/// v0.1.24-beta.1: Universal-DTO fuer alle Sortier-Anweisungen.
/// Wird im Post-Save-Modal (BinInstructionOverlay erweiterter Group-Mode)
/// verwendet. Pattern aus Konzept 4.3.6 (Rev. 3): Modal-only-Regel fuer
/// alle 6 aktiven Sortier-Triggers (Bulk-Move, DismantleWizard,
/// Single-Move, StoreFloating, Reverse-Match-Konsum + Wizard-Stufe-2-Save).
///
/// Struktur:
///   Take (List)   - aus welchen Bins genommen wird, pro Bin eine Sektion.
///                   Leer wenn keine Quell-Bewegung noetig (StoreFloating —
///                   User hat Teil in der Hand).
///   Put (List)    - in welche Bins gelegt wird, pro Bin eine Sektion.
///                   Mindestens ein Eintrag.
///   PlusHint      - optionaler Zusatz-Hinweis (z.B. "Plus: lege wartende
///                   Figur 'X' in Bin Y" fuer Reverse-Match-Konsum mit
///                   neuer Figur).
///   HeaderText    - Default "Operation erfolgreich". Aufrufer kann
///                   ueberschreiben (z.B. "Figur 'X' zerlegt").
/// </summary>
public sealed class SortInstruction
{
    /// <summary>
    /// Quell-Bins und Items die daraus genommen werden. Mehrere Eintraege
    /// moeglich (Bulk-Move aus mehreren Bins, Reverse-Match-Konsum).
    /// Leer wenn keine Quell-Bewegung (StoreFloating).
    /// </summary>
    public List<SortSection> Take { get; set; } = new();

    /// <summary>
    /// Ziel-Bins und Items die dorthin gelegt werden. Mehrere Eintraege
    /// moeglich (DismantleWizard verteilt Required-Parts auf mehrere
    /// Ziel-Bins). Mindestens ein Eintrag.
    /// </summary>
    public List<SortSection> Put { get; set; } = new();

    /// <summary>
    /// Optionaler Zusatz-Hinweis. Null wenn nicht relevant.
    /// </summary>
    public string? PlusHint { get; set; }

    /// <summary>
    /// Header-Text. Default "Operation erfolgreich" fuer alle Trigger-
    /// Pfade. Aufrufer kann optional ueberschreiben.
    /// </summary>
    public string HeaderText { get; set; } = "Operation erfolgreich";
}

/// <summary>
/// Eine Sektion (eine Quell- oder Ziel-Bin-Gruppe) im SortInstruction-DTO.
/// Wird im Overlay als GroupBox mit Header "Nimm aus {BinLabel}:" bzw.
/// "Lege in {BinLabel}:" gerendert.
/// </summary>
public sealed class SortSection
{
    public string BinLabel { get; set; } = string.Empty;
    public List<SortItemLine> Items { get; set; } = new();
}

/// <summary>
/// Eine Zeile in einer SortSection — beschreibt EIN Item das bewegt wird.
/// </summary>
public sealed class SortItemLine
{
    /// <summary>Lesbare Bezeichnung, z.B. "Hair, Long with Curls".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Detail-Zeile, z.B. "973pb1234 - Black" oder "BL-ID arc007".</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// Anzeige-Text fuer die Menge. Default "1x". Kann auch "3x" oder
    /// "+1 (vorher 3x &#8594; nachher 4x)" enthalten (Stapel-Wachstum).
    /// </summary>
    public string QuantityText { get; set; } = "1x";

    /// <summary>Optionaler BL-/Cache-Bild-Pfad fuer Vorschau.</summary>
    public string? ImageUrl { get; set; }
}
