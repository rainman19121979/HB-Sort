namespace HBSort.Core.Models.Bricklink;

/// <summary>
/// Eine einzelne Komponente eines "Combined Part" (assembliertes Teil mit
/// cXX-Suffix). Beispiel: ein montierter Torso besteht physisch aus dem bedruckten
/// Torso-Rumpf (= Grund-Teil), den Armen und den Haenden.
///
/// Die Daten kommen REIN aus dem lokalen bl_cache.db (bl_subsets + bl_items +
/// bl_colors) - kein BL-API-Call. Befuellt von
/// <see cref="Services.IBlCatalogService.GetPartComponentsAsync"/>.
/// Reine Anzeige-Info (KISS) - keine Status-/Reserve-/Inventar-Logik.
/// </summary>
public record PartComponent
{
    /// <summary>BL-Item-No der Komponente, z.B. "983" (Hand) oder "973pb01" (bare Torso).</summary>
    public string ItemNo { get; init; } = string.Empty;

    /// <summary>Klartext-Name aus bl_items. Fallback = <see cref="ItemNo"/> wenn nicht gecached.</summary>
    public string ItemName { get; init; } = string.Empty;

    /// <summary>BL-Color-Id der Komponente. 0 = "Not Applicable" (typisch beim Grund-Teil).</summary>
    public int ColorId { get; init; }

    /// <summary>Farb-Name aus bl_colors. Leer wenn ColorId nicht aufgeloest werden konnte.</summary>
    public string ColorName { get; init; } = string.Empty;

    /// <summary>RGB-Hex (ohne #) aus bl_colors - fuer den Farb-Swatch in der UI.</summary>
    public string? ColorRgb { get; init; }

    /// <summary>Wieviele Stueck dieser Komponente im Combined Part stecken.</summary>
    public int Quantity { get; init; }

    /// <summary>
    /// True wenn diese Zeile das Grund-Teil (bare item) selbst ist - also der
    /// bedruckte Torso-Rumpf, an dem Arme/Haende haengen. Heuristik:
    /// die ItemNo ist ein Praefix der complete-ID (Self-Reference). In der UI wird
    /// das Grund-Teil als "Grund-Teil" gekennzeichnet (siehe Konzept Sektion 6.2).
    /// </summary>
    public bool IsBaseItem { get; init; }
}
