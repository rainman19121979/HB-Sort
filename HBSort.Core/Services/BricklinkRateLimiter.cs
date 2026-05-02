using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Default-Implementierung des Rate-Limiters. Schreibt jeden Call ins
/// api_call_log (Sidecar-Tabelle in bl_cache.db) und liest fuer die State-
/// Berechnung das rollende 24h-Window aus.
///
/// Aktualisierung des States via Event – bewusst KEIN polling-loop. Das UI
/// hoert auf StatusChanged und kann zusaetzlich alle 60s pollen, damit der
/// Counter ohne neue Calls aktualisiert wird (alte Calls fallen aus dem Window).
/// </summary>
public class BricklinkRateLimiter : IBricklinkRateLimiter
{
    private static readonly TimeSpan Window24h = TimeSpan.FromHours(24);
    private static readonly TimeSpan Window1h = TimeSpan.FromHours(1);

    private readonly IBlCacheRepository _repo;
    private readonly ISettingsService _settings;

    /// <summary>Letzter publizierter State – fuer Wechsel-Detektion (Toast-Trigger).</summary>
    private RateLimitState _lastPublishedState = RateLimitState.Ok;

    public event EventHandler<RateLimitStatus>? StatusChanged;

    public BricklinkRateLimiter(IBlCacheRepository repo, ISettingsService settings)
    {
        _repo = repo;
        _settings = settings;
    }

    public async Task<RateLimitStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var bl = _settings.Current.Bricklink;
        var calls24h = await _repo.GetCallCountInWindowAsync(Window24h, ct);
        var calls1h = await _repo.GetCallCountInWindowAsync(Window1h, ct);

        // CallsToday = seit 00:00 lokale Zeit
        var todayLocalStart = DateTime.Now.Date; // Kind=Local
        var callsToday = await _repo.GetCallCountSinceAsync(todayLocalStart, ct);

        var oldest = await _repo.GetOldestCallInWindowAsync(Window24h, ct);

        var state = ComputeState(calls24h, bl.SoftThreshold, bl.HardThreshold);

        return new RateLimitStatus(
            CallsLast24h: calls24h,
            CallsToday: callsToday,
            CallsThisHour: calls1h,
            SoftThreshold: bl.SoftThreshold,
            HardThreshold: bl.HardThreshold,
            BlRealLimit: BricklinkConstants.BlRealLimit,
            State: state,
            OldestCallIn24h: oldest);
    }

    public async Task<bool> CanMakeCallAsync(CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct);
        return status.State != RateLimitState.Blocked;
    }

    public async Task LogCallAsync(string method, string? itemType, string? itemNo,
        int responseTimeMs, int statusCode, bool success, CancellationToken ct = default)
    {
        await _repo.LogApiCallAsync(method, itemType, itemNo, responseTimeMs, statusCode, success, ct);

        // Status nach dem Logging holen und Event feuern
        var status = await GetStatusAsync(ct);

        // State-Wechsel separat loggen (fuer Forensik, falls Toasts uebersehen werden)
        if (status.State != _lastPublishedState)
        {
            Log.Information("BL Rate-Limit-State: {Old} -> {New} ({Calls}/24h)",
                _lastPublishedState, status.State, status.CallsLast24h);
            _lastPublishedState = status.State;
        }

        StatusChanged?.Invoke(this, status);
    }

    public async Task PruneOldEntriesAsync(CancellationToken ct = default)
    {
        // Eintraege aelter als 7 Tage sind weder fuer 24h-Window noch fuer Heute relevant.
        await _repo.PruneApiCallLogAsync(7, ct);
    }

    /// <summary>
    /// Reine Funktion: Berechnet State aus Counter + Schwellen.
    /// Critical = ab 95% des BL-Limits (sehr ungewoehnlich, weil HardThreshold
    /// uns vorher schon blockt – aber falls jemand softThreshold &gt; 4750 setzt,
    /// kann es passieren).
    /// </summary>
    public static RateLimitState ComputeState(int callsLast24h, int softThreshold, int hardThreshold)
    {
        if (callsLast24h >= hardThreshold) return RateLimitState.Blocked;

        // Critical: zwischen Hard und 95% des BL-Limits
        var criticalFloor = (int)(BricklinkConstants.BlRealLimit * 0.95);
        if (callsLast24h >= criticalFloor) return RateLimitState.Critical;

        if (callsLast24h >= softThreshold) return RateLimitState.Warning;

        return RateLimitState.Ok;
    }
}
