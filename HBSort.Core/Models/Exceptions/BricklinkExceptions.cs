namespace HBSort.Core.Models.Exceptions;

/// <summary>
/// Wird geworfen wenn keine BL-Tokens konfiguriert sind oder der Token-Storage
/// die Tokens nicht entschluesseln kann (z.B. settings.json wurde von einem
/// anderen Windows-User uebernommen, DPAPI-Decrypt scheitert).
/// </summary>
public class BricklinkAuthException : Exception
{
    public BricklinkAuthException(string message) : base(message) { }
    public BricklinkAuthException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Wird geworfen wenn die BL-API mit HTTP 429 antwortet (Rate-Limit erreicht).
/// Der Aufrufer soll auf den lokalen Cache zurueckfallen.
/// </summary>
public class BricklinkRateLimitException : Exception
{
    public BricklinkRateLimitException(string message) : base(message) { }
}

/// <summary>
/// Wird geworfen wenn unser EIGENER Rate-Limiter den Call blockt (hardThreshold erreicht).
/// Anders als <see cref="BricklinkRateLimitException"/> ist hier kein API-Call
/// passiert – der wurde vorher abgewuergt. Der Aufrufer soll Cache-Daten zurueckgeben.
/// </summary>
public class RateLimitBlockedException : Exception
{
    public RateLimitBlockedException(string message) : base(message) { }
}
