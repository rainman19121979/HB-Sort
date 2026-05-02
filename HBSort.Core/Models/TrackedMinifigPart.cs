namespace HBSort.Core.Models;

/// <summary>
/// Ein einzelnes Teil, das für eine bestimmte Minifigur benötigt wird.
/// Trackt wie viele Exemplare gebraucht werden und wie viele schon gesammelt sind.
/// </summary>
public class TrackedMinifigPart
{
    public int Id { get; set; }

    /// <summary>Fremdschlüssel zur zugehörigen Minifigur</summary>
    public int TrackedMinifigId { get; set; }

    /// <summary>Navigation Property zur Minifigur</summary>
    public TrackedMinifig TrackedMinifig { get; set; } = null!;

    /// <summary>Rebrickable-Teilenummer, z.B. "3001"</summary>
    public string PartNumber { get; set; } = string.Empty;

    /// <summary>Farb-ID aus dem Rebrickable-Katalog</summary>
    public int ColorId { get; set; }

    /// <summary>
    /// BrickLink-Farb-ID (gemappt aus ColorId via ColorMapping). Optional weil
    /// nicht jede Rebrickable-Farbe eine BrickLink-Entsprechung hat (z.B.
    /// Modulex-Farben). Wird beim Speichern gleich befuellt, damit der
    /// BSX-Export in Phase 7 keine Konvertierungsarbeit mehr braucht.
    /// </summary>
    public int? BricklinkColorId { get; set; }

    /// <summary>Farbname gecacht, z.B. "Black" – damit wir nicht immer den Katalog fragen müssen</summary>
    public string ColorName { get; set; } = string.Empty;

    /// <summary>Teilename gecacht, z.B. "Brick 2 x 4"</summary>
    public string PartName { get; set; } = string.Empty;

    /// <summary>Wie viele Exemplare dieses Teils werden gebraucht</summary>
    public int QuantityNeeded { get; set; }

    /// <summary>Wie viele Exemplare wurden bereits gesammelt</summary>
    public int QuantityCollected { get; set; }
}
