namespace HBSort.Core.Models;

/// <summary>
/// Ein physisches Lagerfach (Box, Schale etc.) in dem Teile und Figuren zwischengelagert werden.
/// Faecher werden nie geloescht, nur als "frei" markiert, damit die Nummerierung stabil bleibt.
/// </summary>
public class StorageBin
{
    public int Id { get; set; }

    /// <summary>Benutzerdefinierter Name, z.B. "Box 3" oder "Schale rot"</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Wann dieses Fach angelegt wurde</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Wann das Fach zuletzt freigegeben wurde. null = derzeit belegt.
    /// Ein Fach ist erst wieder verfuegbar, wenn der User explizit "Fach freigeben" klickt.
    /// </summary>
    public DateTime? FreedAt { get; set; }

    /// <summary>Optionale Notizen zum Fach (max. 500 Zeichen, vom User pflegbar).</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// v0.1.23: persistierter Bin-Typ. Wird vom Service nach jeder Schreib-
    /// Aktion via RecalculateKindAsync aktualisiert. Default Empty fuer
    /// neu angelegte Bins. Migration AddStorageBinKind plus Backfill-
    /// Methode beim App-Start setzen den Wert bei bestehenden DB-Eintraegen.
    /// </summary>
    public StorageBinKind Kind { get; set; } = StorageBinKind.Empty;

    /// <summary>Alle Minifiguren die aktuell in diesem Fach liegen</summary>
    public List<TrackedMinifig> TrackedMinifigs { get; set; } = [];

    /// <summary>Einzelteile ohne zugeordnete Figur ("Floating Parts") in diesem Fach</summary>
    public List<FloatingPart> FloatingParts { get; set; } = [];
}
