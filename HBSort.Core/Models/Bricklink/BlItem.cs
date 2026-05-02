namespace HBSort.Core.Models.Bricklink;

/// <summary>
/// Ein Eintrag aus der bl_items-Tabelle. ItemType + ItemNo bilden den
/// zusammengesetzten Primary-Key.
/// data_completeness unterscheidet zwischen "voll geholt via GetItem"
/// und "nebenbei aus GetSubsets-Antwort befuellt".
/// </summary>
public record BlItem
{
    public string ItemType { get; init; } = string.Empty;   // 'M', 'P', 'S'
    public string ItemNo { get; init; } = string.Empty;     // 'arc007', '3024', '75300-1'
    public string Name { get; init; } = string.Empty;
    public int? YearReleased { get; init; }
    public string? ImageUrl { get; init; }
    public double? Weight { get; init; }
    public double? DimX { get; init; }
    public double? DimY { get; init; }
    public double? DimZ { get; init; }
    public int? CategoryId { get; init; }

    /// <summary>Volle API-Antwort als JSON (oder leer wenn aus Subsets befuellt).</summary>
    public string? JsonFull { get; init; }

    /// <summary>Full = via GetItem; Subset = aus GetSubsets-Eintrag mit-gecached.</summary>
    public DataCompleteness DataCompleteness { get; init; }

    /// <summary>ISO-8601-Zeitstempel des Cache-Eintrags (UTC).</summary>
    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>Vollstaendigkeit eines Cache-Eintrags.</summary>
public enum DataCompleteness
{
    /// <summary>Aus GetSubsets mit-gecached, nur Basis-Felder verlaesslich (Name, ggf. Image).</summary>
    Subset,
    /// <summary>Via GetItem geholt, alle Felder gesetzt.</summary>
    Full
}
