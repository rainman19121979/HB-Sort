using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// Client fuer die Brickognize-API.
/// Vier Endpoints (siehe BRICKOGNIZE_API.md):
///   - /health/                     - Service-Verfuegbarkeit
///   - /predict/                    - generisch (Fallback)
///   - /predict/parts/?predict_color=true - Teile + Farberkennung
///   - /predict/figs/               - Minifiguren
///   - /predict/sets/               - Sets (in diesem Projekt nicht genutzt)
/// </summary>
public interface IBrickognizeClient
{
    /// <summary>
    /// Pingt den Health-Endpoint mit kurzem Timeout (5 Sek).
    /// Wirft KEINE Exception bei Fehler - gibt stattdessen ein Result-Objekt zurueck.
    /// </summary>
    Task<BrickognizeHealth> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Generischer Endpoint /predict/ - Fallback wenn unklar ob Teil oder Figur.</summary>
    Task<BrickognizePrediction> PredictGenericAsync(byte[] jpegBytes, CancellationToken cancellationToken = default);

    /// <summary>Endpoint /predict/parts/?predict_color=true - Teile + Farberkennung.</summary>
    Task<BrickognizePrediction> PredictPartAsync(byte[] jpegBytes, CancellationToken cancellationToken = default);

    /// <summary>Endpoint /predict/figs/ - Minifiguren.</summary>
    Task<BrickognizePrediction> PredictMinifigAsync(byte[] jpegBytes, CancellationToken cancellationToken = default);
}

/// <summary>Ergebnis des Health-Checks.</summary>
public class BrickognizeHealth
{
    /// <summary>Status: Online (gruen), Slow (gelb), Offline (rot).</summary>
    public BrickognizeStatus Status { get; set; }

    /// <summary>Antwortzeit in Millisekunden, oder null wenn nicht erreichbar.</summary>
    public long? ResponseTimeMs { get; set; }

    /// <summary>Fehlertext bei Status=Offline.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Zeitpunkt des Checks (UTC, Audit M-3).</summary>
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Brickognize-Verfuegbarkeitsstatus.</summary>
public enum BrickognizeStatus
{
    Unknown,
    Online,
    Slow,
    Offline
}
