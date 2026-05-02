namespace HBSort.Core.Models;

/// <summary>
/// Eine Minifigur, die der User gescannt hat und deren Teile er sammelt.
/// Wird in userdata.db gespeichert und verfolgt den Fortschritt beim Zusammenbauen.
/// </summary>
public class TrackedMinifig
{
    public int Id { get; set; }

    /// <summary>Rebrickable-ID, z.B. "fig-001549"</summary>
    public string FigNum { get; set; } = string.Empty;

    /// <summary>BrickLink-ID, z.B. "sw0001" – für BrickStore-Export und Preis-Tool</summary>
    public string? BricklinkId { get; set; }

    /// <summary>Name der Figur, gecacht aus dem Katalog</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>URL zum Bild auf Rebrickable</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Pfad zum lokal gecachten Bild im images-Ordner</summary>
    public string? LocalImagePath { get; set; }

    /// <summary>Optionale Notizen vom User, z.B. "Helm fehlt original"</summary>
    public string? UserNotes { get; set; }

    /// <summary>Wann die Figur gescannt/angelegt wurde</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Wann die Figur als komplett markiert wurde (null = noch nicht)</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Aktueller Status der Figur im Workflow</summary>
    public TrackedMinifigStatus Status { get; set; } = TrackedMinifigStatus.Waiting;

    /// <summary>In welchem Lagerfach liegt diese Figur? (null = noch keins zugewiesen)</summary>
    public int? StorageBinId { get; set; }

    /// <summary>Navigation Property: das zugewiesene Lagerfach</summary>
    public StorageBin? StorageBin { get; set; }

    /// <summary>Liste aller benötigten Einzelteile mit Sammel-Fortschritt</summary>
    public List<TrackedMinifigPart> RequiredParts { get; set; } = [];
}

/// <summary>
/// Die möglichen Zustände einer verfolgten Minifigur.
/// WAITING = Teile werden gesammelt, COMPLETE = alle Teile da,
/// DISMANTLED = wieder zerlegt, SOLD = verkauft.
/// </summary>
public enum TrackedMinifigStatus
{
    Waiting,
    Complete,
    Dismantled,
    Sold
}
