namespace LegoMinifigSorter.Core.Models.Bricklink;

/// <summary>
/// Ein Eintrag aus der bl_subsets-Tabelle. Beschreibt "Parent enthaelt Item in Color".
/// Parent ist typisch eine Minifigur ('M') oder ein Set ('S'), Item ein Teil ('P').
/// MatchId gruppiert alternative Teile (BL-"Match-Group").
/// </summary>
public record BlSubset
{
    public string ParentType { get; init; } = string.Empty;   // 'M' oder 'S'
    public string ParentNo { get; init; } = string.Empty;
    public string ItemType { get; init; } = "P";              // 'P' im Normalfall
    public string ItemNo { get; init; } = string.Empty;
    public int ColorId { get; init; }
    public int Quantity { get; init; }
    public int ExtraQuantity { get; init; }
    public bool IsAlternate { get; init; }
    public bool IsCounterpart { get; init; }

    /// <summary>BL-Match-Group-ID. 0 = nicht in einer Gruppe.</summary>
    public int MatchId { get; init; }

    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// True wenn der Eintrag aus einer GetSupersets-Antwort stammt – also nur ein
    /// Single-Row-Eintrag pro Parent ist und KEINE vollstaendige Teileliste der
    /// Minifig darstellt. Cache-Konsumenten muessen pruefen ob mind. 1 Eintrag
    /// "echt" (IsFromSupersets=false) ist, bevor sie den Cache als komplett
    /// betrachten.
    /// </summary>
    public bool IsFromSupersets { get; init; }
}
