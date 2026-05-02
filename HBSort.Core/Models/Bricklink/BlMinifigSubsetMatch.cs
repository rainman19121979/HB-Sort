namespace HBSort.Core.Models.Bricklink;

/// <summary>
/// Reverse-Lookup-Treffer aus bl_subsets + bl_items: eine Minifig die ein
/// bestimmtes Teil in einer bestimmten Farbe enthaelt. Ergaenzt um Item-Daten
/// (Name/Image) sofern in bl_items gecached.
/// </summary>
public record BlMinifigSubsetMatch(
    string MinifigBlId,
    string? MinifigName,
    string? MinifigImageUrl,
    int QuantityInMinifig);
