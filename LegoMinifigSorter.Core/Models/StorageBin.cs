namespace LegoMinifigSorter.Core.Models;

/// <summary>
/// Ein physisches Lagerfach (Box, Schale etc.) in dem Teile und Figuren zwischengelagert werden.
/// Fächer werden nie gelöscht, nur als "frei" markiert, damit die Nummerierung stabil bleibt.
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
    /// Ein Fach ist erst wieder verfügbar, wenn der User explizit "Fach freigeben" klickt.
    /// </summary>
    public DateTime? FreedAt { get; set; }

    /// <summary>Optionale Notizen zum Fach (max. 500 Zeichen, vom User pflegbar).</summary>
    public string? Notes { get; set; }

    /// <summary>Alle Minifiguren die aktuell in diesem Fach liegen</summary>
    public List<TrackedMinifig> TrackedMinifigs { get; set; } = [];

    /// <summary>Einzelteile ohne zugeordnete Figur ("Floating Parts") in diesem Fach</summary>
    public List<FloatingPart> FloatingParts { get; set; } = [];
}
