using HBSort.Core.Models.Bricklink;

namespace HBSort.Core.Services;

/// <summary>
/// Eigener Rate-Limiter fuer die BL-Store-API. BL erlaubt 5000 Calls/24h
/// (Rolling-Window). Wir tracken jeden Call in api_call_log und blocken
/// vorzeitig (HardThreshold), damit wir nie ins echte Limit laufen.
/// </summary>
public interface IBricklinkRateLimiter
{
    /// <summary>Aktueller Status-Snapshot (Counter + State).</summary>
    Task<RateLimitStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// True wenn der naechste Call gemacht werden DARF.
    /// Wird VOR dem API-Call aufgerufen.
    /// </summary>
    Task<bool> CanMakeCallAsync(CancellationToken ct = default);

    /// <summary>
    /// Logged einen ausgefuehrten Call. Wird NACH dem API-Call aufgerufen.
    /// Ausloest <see cref="StatusChanged"/> sobald der State eine neue Stufe erreicht.
    /// </summary>
    Task LogCallAsync(string method, string? itemType, string? itemNo,
        int responseTimeMs, int statusCode, bool success, CancellationToken ct = default);

    /// <summary>Wartung beim App-Start: aelter als 7 Tage loeschen.</summary>
    Task PruneOldEntriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Wird ausgeloest sobald der Status sich aendert (Ok->Warning, Warning->Blocked, etc).
    /// Wird auch bei jedem Call gefeuert (auch wenn State gleich bleibt) - das UI
    /// kann darueber den Counter live aktualisieren.
    /// </summary>
    event EventHandler<RateLimitStatus>? StatusChanged;
}
