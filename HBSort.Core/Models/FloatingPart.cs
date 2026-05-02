namespace HBSort.Core.Models;

/// <summary>
/// Ein Einzelteil das noch keiner Minifigur zugeordnet ist und "frei" in einem Lagerfach liegt.
/// Bleibt unbegrenzt im Pool, bis es einer Figur zugewiesen oder manuell entfernt wird.
/// </summary>
public class FloatingPart
{
    public int Id { get; set; }

    /// <summary>Rebrickable-Teilenummer</summary>
    public string PartNumber { get; set; } = string.Empty;

    /// <summary>Farb-ID aus dem Rebrickable-Katalog</summary>
    public int ColorId { get; set; }

    /// <summary>
    /// BrickLink-Farb-ID (gemappt aus ColorId via ColorMapping). Optional, weil
    /// nicht jede Rebrickable-Farbe eine BrickLink-Entsprechung hat. Wird beim
    /// Speichern gleich befuellt, damit der BSX-Export in Phase 7 sie direkt
    /// uebernehmen kann.
    /// </summary>
    public int? BricklinkColorId { get; set; }

    /// <summary>Farbname gecacht</summary>
    public string ColorName { get; set; } = string.Empty;

    /// <summary>Teilename gecacht</summary>
    public string PartName { get; set; } = string.Empty;

    /// <summary>Anzahl der Exemplare dieses Teils in dieser Farbe</summary>
    public int Quantity { get; set; }

    /// <summary>In welchem Lagerfach liegt dieses Teil</summary>
    public int StorageBinId { get; set; }

    /// <summary>Navigation Property zum Lagerfach</summary>
    public StorageBin StorageBin { get; set; } = null!;

    /// <summary>Wann das Teil hinzugefügt wurde</summary>
    public DateTime AddedAt { get; set; }

    /// <summary>
    /// Optional: aus welcher Figur stammt dieses Teil? Wird vom DismantleWizard
    /// gesetzt wenn der User eine Figur "aufgibt" und Teile in den Floating-Pool
    /// uebernimmt. Erlaubt es, Teile in der Lagerliste als "aus zerlegter Figur X"
    /// zu kennzeichnen. Bei Loeschung der Figur wird der FK auf NULL gesetzt
    /// (OnDelete=SetNull) – das Teil bleibt also als "verwaister Origin"-Eintrag bestehen.
    /// </summary>
    public int? OriginMinifigId { get; set; }

    /// <summary>Navigation Property zur Origin-Figur (optional).</summary>
    public TrackedMinifig? OriginMinifig { get; set; }
}
