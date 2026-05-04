using System.Text.Json.Serialization;

namespace HBSort.Core.Models;

/// <summary>
/// Die vier OAuth1-Tokens fuer die BrickLink-Store-API.
/// Wird per DPAPI verschluesselt und Base64-encoded in settings.json
/// unter `bricklink.tokensEncrypted` gespeichert.
/// </summary>
public class BricklinkTokens
{
    /// <summary>App-spezifisch, aus dem BL-Consumer-Profile.</summary>
    [JsonPropertyName("consumerKey")]
    public string ConsumerKey { get; set; } = string.Empty;

    /// <summary>App-spezifisch, aus dem BL-Consumer-Profile.</summary>
    [JsonPropertyName("consumerSecret")]
    public string ConsumerSecret { get; set; } = string.Empty;

    /// <summary>User-spezifisch, aus dem BL-Consumer-Profile.</summary>
    [JsonPropertyName("tokenValue")]
    public string TokenValue { get; set; } = string.Empty;

    /// <summary>User-spezifisch, aus dem BL-Consumer-Profile.</summary>
    [JsonPropertyName("tokenSecret")]
    public string TokenSecret { get; set; } = string.Empty;

    /// <summary>True wenn alle vier Felder befuellt sind.</summary>
    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ConsumerKey) &&
        !string.IsNullOrWhiteSpace(ConsumerSecret) &&
        !string.IsNullOrWhiteSpace(TokenValue) &&
        !string.IsNullOrWhiteSpace(TokenSecret);
}

/// <summary>
/// BrickLink-spezifische Settings (in AppSettings.Bricklink).
/// Token-Inhalt selbst lebt in <see cref="TokensEncrypted"/> als
/// DPAPI-verschluesseltes Blob (Base64).
/// </summary>
public class BricklinkSettings
{
    /// <summary>
    /// DPAPI-verschluesselte BricklinkTokens-JSON, Base64-encoded.
    /// Leer wenn keine Tokens konfiguriert sind.
    /// </summary>
    public string TokensEncrypted { get; set; } = string.Empty;

    /// <summary>
    /// Soft-Threshold: bei diesem Stand (Calls in den letzten 24h) wird ein
    /// gelber Warn-Toast angezeigt. Default 1000.
    /// </summary>
    public int SoftThreshold { get; set; } = 1000;

    /// <summary>
    /// Hard-Threshold: ab diesem Stand werden KEINE neuen API-Calls mehr gemacht
    /// (Cache-Fallback). 90% des BL-Limits = Sicherheitsmarge. Default 4500.
    /// </summary>
    public int HardThreshold { get; set; } = 4500;

    /// <summary>
    /// Veralteter Eintrag fuer Backward-Compat mit settings.json aus Phase R1.
    /// Der Rate-Limiter nutzt jetzt SoftThreshold/HardThreshold; dieses Feld
    /// bleibt nur damit alte settings.json-Dateien noch lesbar sind.
    /// </summary>
    public int RateLimitWarningThreshold { get; set; } = 4500;

    /// <summary>
    /// Cache-Lebensdauer in Tagen. Aelter -> Stale-While-Revalidate beim
    /// naechsten Lookup. Default 90.
    /// </summary>
    public int CacheStaleDays { get; set; } = 90;
}

/// <summary>
/// Konstanten zur BL-API. BL_REAL_LIMIT ist NICHT in den Settings editierbar -
/// das ist BLs fixes Tageskontingent.
/// </summary>
public static class BricklinkConstants
{
    /// <summary>BLs offizielles Tageslimit (Rolling 24h).</summary>
    public const int BlRealLimit = 5000;

    /// <summary>Sicherheits-Maximum fuer den HardThreshold (5000 - 100).</summary>
    public const int MaxAllowedHardThreshold = 4900;
}
