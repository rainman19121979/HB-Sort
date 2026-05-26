using System.ComponentModel.DataAnnotations.Schema;

namespace HBSort.Core.Models;

/// <summary>
/// Ein einzelnes Teil, das fuer eine bestimmte Minifigur benoetigt wird.
/// Trackt wie viele Exemplare gebraucht werden und wie viele schon gesammelt sind.
///
/// <para>
/// <b>v0.1.24-beta.8 Phase 3</b>: zweispaltige Beschaffungs-Buchhaltung.
/// </para>
/// <list type="bullet">
///   <item><c>QuantityCollected</c>: <b>physisch</b> in HBSort vorhanden -
///   per Scan eingelagert oder ueber Reverse-Match aus dem Floating-Pool
///   konsumiert. Das physische Maß.</item>
///   <item><c>QuantityReservedFromBl</c>: aus dem BrickLink-Store
///   reserviert (liegt physisch noch im Shop). Wird via
///   <see cref="HBSort.Core.Services.IBlInventoryService.ReserveAsync"/>
///   inkrementiert.</item>
/// </list>
/// <para>
/// Komplett-Check + Wartet-noch-auf-Teile-Logik nutzen die
/// <see cref="EffectiveCollected"/>-Summe; Reverse-Match-Konsum + Cap-
/// Berechnungen bleiben auf <see cref="QuantityCollected"/> (physisches Maß).
/// Phase 4 Export-Pfad muss vor dem BSX die Unterscheidung treffen
/// (verkaufbar nur was wirklich da ist - Reservierungen muessen vorher
/// physisch aus dem BL-Shop entnommen werden).
/// </para>
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

    /// <summary>
    /// Wie viele Exemplare wurden bereits PHYSISCH in HBSort gesammelt
    /// (per Scan eingelagert oder ueber Reverse-Match aus dem Floating-
    /// Pool konsumiert). Reverse-Match-Cap-Berechnungen
    /// (Math.Min(have, QuantityNeeded - QuantityCollected)) nutzen
    /// ausschliesslich dieses Feld.
    /// </summary>
    public int QuantityCollected { get; set; }

    /// <summary>
    /// v0.1.24-beta.8 Phase 3: Wie viele Exemplare wurden im BrickLink-
    /// Store reserviert (liegen physisch noch dort). Default 0. Wird
    /// inkrementiert via
    /// <see cref="HBSort.Core.Services.IBlInventoryService.ReserveAsync"/>
    /// und dekrementiert via Release. Beim Phase-4-Export gelten Teile
    /// mit <c>QuantityReservedFromBl &gt; 0</c> NICHT als physisch verkaufsbereit.
    /// </summary>
    public int QuantityReservedFromBl { get; set; }

    /// <summary>
    /// Effektiv-beschafft-Summe (physisch + BL-reserviert). Nutzt z.B. die
    /// Komplett-Markierung der Figur sowie die "Welche Teile fehlen noch?"-
    /// Logik im UI / Matching.
    /// </summary>
    [NotMapped]
    public int EffectiveCollected => QuantityCollected + QuantityReservedFromBl;

    /// <summary>True wenn EffectiveCollected den Bedarf deckt.</summary>
    [NotMapped]
    public bool IsEffectivelyComplete => EffectiveCollected >= QuantityNeeded;

    /// <summary>Noch zu beschaffende Restmenge (ueber alle Quellen). Min 0.</summary>
    [NotMapped]
    public int EffectivelyMissing => Math.Max(0, QuantityNeeded - EffectiveCollected);
}
