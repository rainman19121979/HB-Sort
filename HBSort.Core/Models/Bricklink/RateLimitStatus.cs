namespace HBSort.Core.Models.Bricklink;

/// <summary>
/// Snapshot des Rate-Limit-Counters. Wird vom IBricklinkRateLimiter
/// vor jedem API-Call gepruef und zur Anzeige im Status-Balken/Settings genutzt.
///
/// CallsLast24h ist das wirksame "Rolling-24h-Window" – BL misst dasselbe.
/// CallsToday und CallsThisHour sind nur fuer die UI-Anzeige (gibt User mehr Gefuehl).
/// </summary>
public record RateLimitStatus(
    int CallsLast24h,
    int CallsToday,
    int CallsThisHour,
    int SoftThreshold,
    int HardThreshold,
    int BlRealLimit,
    RateLimitState State,
    DateTime? OldestCallIn24h);

/// <summary>
/// Status-Stufen, abhaengig von CallsLast24h vs. Schwellwerten:
///   * Ok       : calls &lt; soft
///   * Warning  : soft &lt;= calls &lt; hard
///   * Critical : hard &lt;= calls &lt; 95% des BL-Limits (sollte praktisch nie auftreten,
///                wenn die Schwellen sinnvoll gesetzt sind – ist der "Wir-haben-doch-noch-was-uebersehen"-Bereich)
///   * Blocked  : calls &gt;= hard – keine API-Calls mehr, nur Cache.
/// </summary>
public enum RateLimitState
{
    Ok,
    Warning,
    Critical,
    Blocked
}
