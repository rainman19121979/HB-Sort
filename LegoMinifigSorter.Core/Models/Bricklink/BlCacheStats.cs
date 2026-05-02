namespace LegoMinifigSorter.Core.Models.Bricklink;

/// <summary>
/// Statistik-Snapshot fuer die bl_cache.db. Wird im Settings-Tab "BrickLink-API"
/// angezeigt und beim Cache-Leeren genutzt.
/// </summary>
public record BlCacheStats(
    int ItemCount,
    int SubsetCount,
    int ColorCount,
    long DbFileSizeBytes,
    DateTime? OldestFetchedAt);
