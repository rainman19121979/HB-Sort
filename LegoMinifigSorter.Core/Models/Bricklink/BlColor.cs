namespace LegoMinifigSorter.Core.Models.Bricklink;

/// <summary>
/// Ein Eintrag aus der bl_colors-Tabelle.
/// Wird einmalig beim ersten App-Start mit gueltigen BL-Tokens befuellt.
/// </summary>
public record BlColor
{
    public int ColorId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Rgb { get; init; }

    /// <summary>BL-"Type" wie "Solid", "Transparent", "Chrome", "Glitter", ...</summary>
    public string? Type { get; init; }

    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
}
