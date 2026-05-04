using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using HBSort.Core.Models;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Implementierung des Brickognize-Clients mit HttpClient.
///
/// Architektur-Hinweis: HttpClient sollte nur einmal pro App-Laufzeit instanziiert
/// werden (Best Practice von Microsoft). Wir bekommen ihn via Konstruktor injected
/// damit das DI-Container das uebernehmen kann (AddHttpClient&lt;IBrickognizeClient&gt;).
/// </summary>
public class BrickognizeClient : IBrickognizeClient
{
    /// <summary>Pfad zum Debug-Log fuer rohe JSON-Responses (siehe Phase 2 Anforderung).</summary>
    private static readonly string DebugLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HBSort", "logs", "brickognize-debug.log");

    /// <summary>Schwellwert: Antworten ueber dieser Zeit gelten als "Slow" (gelb).</summary>
    private const int SlowResponseThresholdMs = 3000;

    /// <summary>Health-Check-Timeout (kurz, soll nicht den App-Start blockieren).</summary>
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>JSON-Optionen: case-insensitive matching fuer Robustheit.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;

    public BrickognizeClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        // Base-Address und Default-Timeout (60 Sek laut BRICKOGNIZE_API.md) werden
        // vom DI-Setup gesetzt.
    }

    public async Task<BrickognizeHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        // Eigene Cancellation mit kurzem Timeout, damit der App-Start nicht haengt
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(HealthCheckTimeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _httpClient.GetAsync("/health/", cts.Token);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Brickognize Health-Check: HTTP {Code}", (int)response.StatusCode);
                return new BrickognizeHealth
                {
                    Status = BrickognizeStatus.Offline,
                    ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}"
                };
            }

            var status = stopwatch.ElapsedMilliseconds > SlowResponseThresholdMs
                ? BrickognizeStatus.Slow
                : BrickognizeStatus.Online;

            Log.Information("Brickognize Health-Check OK: {Status} ({Ms} ms)",
                status, stopwatch.ElapsedMilliseconds);

            return new BrickognizeHealth
            {
                Status = status,
                ResponseTimeMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Warning(ex, "Brickognize Health-Check fehlgeschlagen");
            return new BrickognizeHealth
            {
                Status = BrickognizeStatus.Offline,
                ErrorMessage = ex.Message
            };
        }
    }

    public Task<BrickognizePrediction> PredictGenericAsync(byte[] jpegBytes, CancellationToken cancellationToken = default)
        => PostMultipartAsync("/predict/", jpegBytes, "generic", cancellationToken);

    public Task<BrickognizePrediction> PredictPartAsync(byte[] jpegBytes, CancellationToken cancellationToken = default)
        => PostMultipartAsync("/predict/parts/?predict_color=true", jpegBytes, "parts", cancellationToken);

    public Task<BrickognizePrediction> PredictMinifigAsync(byte[] jpegBytes, CancellationToken cancellationToken = default)
        => PostMultipartAsync("/predict/figs/", jpegBytes, "figs", cancellationToken);

    /// <summary>
    /// Gemeinsame Multipart-POST-Logik: bauet das query_image-Formular,
    /// schickt es an den Endpoint, deserialisiert die Response und
    /// schreibt die rohe JSON ins Debug-Log (fuer Phase-2-Validierung).
    /// </summary>
    private async Task<BrickognizePrediction> PostMultipartAsync(
        string endpoint,
        byte[] jpegBytes,
        string endpointTag,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Multipart-Form aufbauen.
        // ByteArrayContent kopiert NICHT, das ist effizient.
        using var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(jpegBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        // Filename ist egal, aber muss da sein. Mit UTC-Zeitstempel fuer
        // konsistente Debug-Logs (Audit M-3, 2026-05-04).
        content.Add(imageContent, "query_image", $"scan_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg");

        Log.Debug("Brickognize POST {Endpoint} ({Size} Byte)", endpoint, jpegBytes.Length);

        using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

        // Debug-Log schreiben BEVOR wir den Status-Check machen, damit wir
        // auch fehlerhafte Antworten zur Auswertung haben.
        await WriteDebugLogAsync(endpointTag, endpoint, response.StatusCode, stopwatch.ElapsedMilliseconds, rawJson);

        response.EnsureSuccessStatusCode();

        var result = JsonSerializer.Deserialize<BrickognizePrediction>(rawJson, JsonOptions);
        if (result == null)
        {
            throw new InvalidOperationException("Brickognize lieferte eine leere Antwort.");
        }

        Log.Information("Brickognize {Endpoint}: {ItemCount} Treffer, top-Score={TopScore:0.00}, {Ms} ms",
            endpointTag,
            result.Items.Count,
            result.Items.FirstOrDefault()?.Score ?? 0,
            stopwatch.ElapsedMilliseconds);

        return result;
    }

    /// <summary>
    /// Schreibt eine eingehende Response (komplette JSON) in
    /// %APPDATA%\HBSort\logs\brickognize-debug.log.
    /// Dient zur Phase-2-Validierung der URL-Patterns in BRICKOGNIZE_API.md.
    /// </summary>
    private static async Task WriteDebugLogAsync(
        string endpointTag,
        string endpoint,
        System.Net.HttpStatusCode statusCode,
        long elapsedMs,
        string rawJson)
    {
        try
        {
            // Ordner sicherstellen
            var dir = Path.GetDirectoryName(DebugLogPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // JSON wenn moeglich pretty-printen
            string prettyJson = rawJson;
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                prettyJson = JsonSerializer.Serialize(doc.RootElement, JsonOptions);
            }
            catch { /* ungueltiges JSON: roh loggen */ }

            var entry = $"""
                ====================================================================
                Timestamp:    {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC
                Endpoint-Tag: {endpointTag}
                URL:          {endpoint}
                HTTP-Status:  {(int)statusCode} {statusCode}
                Dauer:        {elapsedMs} ms
                --- Response-JSON ---
                {prettyJson}

                """;

            await File.AppendAllTextAsync(DebugLogPath, entry);
        }
        catch (Exception ex)
        {
            // Debug-Log-Fehler darf den Hauptablauf NICHT killen
            Log.Warning(ex, "Konnte brickognize-debug.log nicht schreiben");
        }
    }
}
