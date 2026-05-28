namespace HBSort.Core.Models;

/// <summary>
/// v0.1.24-beta.11: Persistent ignorierter Bauvorschlag. Wenn der User im
/// Bauvorschlags-Tab das X klickt, landet die BricklinkId hier - der Vorschlag
/// taucht in spaeteren RefreshAsync-Laeufen nicht mehr auf. Nicht reversibel
/// per Figur, nur Reset aller ignorierten Eintraege (DELETE FROM-Pfad).
/// </summary>
public class IgnoredBuildSuggestion
{
    public int Id { get; set; }
    public string BricklinkId { get; set; } = string.Empty;
    public DateTime IgnoredAt { get; set; }
}
