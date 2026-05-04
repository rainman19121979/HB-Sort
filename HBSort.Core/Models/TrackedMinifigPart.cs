namespace HBSort.Core.Models;

/// <summary>
/// Ein einzelnes Teil, das fuer eine bestimmte Minifigur benoetigt wird.
/// Trackt wie viele Exemplare gebraucht werden und wie viele schon gesammelt sind.
/// </summary>
public class TrackedMinifigPart
{
    public int Id { get; set; }

    /// <summary>Fremdschluessel zur zugehoerigen Minifigur</summary>
    public int TrackedMinifigId { get; set; }

    /// <summary>Navigation Property zur Minifigur</summary>
    public TrackedMinifig TrackedMinifig { get; set; } = null!;

    /// <summary>Rebrickable-Teilenummer, z.B. "3001"</summary>
    public string PartNumber { get; set; } = string.Empty;

    /// <summary>BrickLink-Farb-ID (Brickognize liefert die direkt).</summary>
    public int ColorId { get; set; }

    /// <summary>Farbname gecacht, z.B. "Black" - damit wir nicht immer den Katalog fragen muessen</summary>
    public string ColorName { get; set; } = string.Empty;

    /// <summary>Teilename gecacht, z.B. "Brick 2 x 4"</summary>
    public string PartName { get; set; } = string.Empty;

    /// <summary>Wie viele Exemplare dieses Teils werden gebraucht</summary>
    public int QuantityNeeded { get; set; }

    /// <summary>Wie viele Exemplare wurden bereits gesammelt</summary>
    public int QuantityCollected { get; set; }
}
