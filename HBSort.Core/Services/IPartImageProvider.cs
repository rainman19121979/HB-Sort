using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// Liefert das beste verfuegbare Bild fuer ein Brickognize-Item.
///
/// Hybrid-Strategie:
///   1. BrickLink-Bilder bevorzugen (echte, farbige Teil-Fotos)
///   2. Bei 404/Timeout: Fallback auf Brickognize-Bild
///
/// Implementierungen sollen das Ergebnis ueber den IPersistentImageCache
/// auf Disk halten und einen lokalen Pfad zurueckgeben.
/// </summary>
public interface IPartImageProvider
{
    /// <summary>
    /// Liefert den lokalen Pfad zur besten verfuegbaren Bilddatei (Brickognize-Pfad).
    /// Erste Stufe: BL-URL aus item.external_sites + (optional) BL-Color-ID direkt.
    /// Fallback: Brickognize-Image.
    /// </summary>
    /// <param name="bricklinkColorId">BL-Color-ID direkt (Brickognize liefert seit
    /// PROMPT 6 BL-IDs in der "id"-Spalte); null = generic / color=0.</param>
    Task<string> GetImageFileAsync(BrickognizeItem item, int? bricklinkColorId, CancellationToken ct = default);

    /// <summary>
    /// Phase R3+: Direkt mit BL-IDs bedienen, ohne Brickognize-Item-Wrapper.
    /// Wird von der MinifigDetailView genutzt, wo wir die BL-IDs schon aus
    /// dem BlCatalogService haben (kein Brickognize-Match noetig).
    /// </summary>
    /// <param name="bricklinkType">"M", "P" oder "S".</param>
    /// <param name="bricklinkId">BL-Item-No, z.B. "3024" oder "arc007".</param>
    /// <param name="bricklinkColorId">BL-Color-ID; null = generic / color=0.</param>
    Task<string> GetImageFileByBlAsync(string bricklinkType, string bricklinkId, int? bricklinkColorId, CancellationToken ct = default);
}
