using OpenCvSharp;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Implementierung des Kamera-Service mit OpenCvSharp4.
/// Läuft in einem Hintergrund-Thread und feuert FrameReceived-Events
/// für jeden neuen Frame, den die Kamera liefert.
/// </summary>
public class CameraService : ICameraService
{
    // Das VideoCapture-Objekt von OpenCV – unsere Verbindung zur Kamera
    private VideoCapture? _capture;

    // Hintergrund-Thread der kontinuierlich Frames liest
    private Thread? _captureThread;

    // Signal zum Stoppen des Threads
    private CancellationTokenSource? _cancellationTokenSource;

    // Der zuletzt gelesene Frame (für Snapshot-Funktion)
    // "volatile" bedeutet: verschiedene Threads sehen immer den aktuellsten Wert
    private volatile byte[]? _lastFrame;

    /// <summary>Wird für jeden neuen Frame aufgerufen (als JPEG-Bytes)</summary>
    public event Action<byte[]>? FrameReceived;

    public bool IsRunning => _capture != null && _capture.IsOpened();

    public List<string> GetAvailableCameras()
    {
        var cameras = new List<string>();

        // OpenCV hat leider keine eingebaute Methode um Kamera-Namen zu lesen.
        // Wir probieren einfach die Indizes 0-9 durch und schauen welche sich öffnen lassen.
        // Annahme: Mehr als 10 USB-Kameras hat niemand angeschlossen.
        for (int i = 0; i < 10; i++)
        {
            using var testCapture = new VideoCapture(i);
            if (testCapture.IsOpened())
            {
                // Da wir den echten Kamera-Namen nicht auslesen können,
                // vergeben wir einen generischen Namen mit dem Index
                cameras.Add($"Kamera {i}");
                testCapture.Release();
            }
            else
            {
                // Sobald eine Kamera nicht antwortet, brechen wir ab
                // (die Indizes sind meistens fortlaufend)
                break;
            }
        }

        Log.Information("{Count} Kamera(s) gefunden", cameras.Count);
        return cameras;
    }

    public Task StartAsync(int cameraIndex)
    {
        // Falls schon eine Kamera läuft, erst stoppen
        Stop();

        Log.Information("Starte Kamera mit Index {Index}", cameraIndex);

        _capture = new VideoCapture(cameraIndex);

        if (!_capture.IsOpened())
        {
            Log.Error("Kamera {Index} konnte nicht geöffnet werden", cameraIndex);
            _capture.Dispose();
            _capture = null;
            return Task.CompletedTask;
        }

        // Auflösung setzen (Standard: 640x480 ist meistens gut genug für Brickognize)
        _capture.Set(VideoCaptureProperties.FrameWidth, 640);
        _capture.Set(VideoCaptureProperties.FrameHeight, 480);

        // Hintergrund-Thread starten, der kontinuierlich Frames liest
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        _captureThread = new Thread(() => CaptureLoop(token))
        {
            // Als Background-Thread markiert: wird automatisch beendet wenn die App schließt
            IsBackground = true,
            Name = "CameraCapture"
        };
        _captureThread.Start();

        Log.Information("Kamera {Index} gestartet", cameraIndex);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Endlosschleife die im Hintergrund läuft und Frames von der Kamera liest.
    /// Jeder Frame wird als JPEG kodiert und über das FrameReceived-Event verteilt.
    /// </summary>
    private void CaptureLoop(CancellationToken token)
    {
        // Mat ist das OpenCV-Format für ein Bild im Speicher ("Matrix")
        using var frame = new Mat();

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_capture == null || !_capture.IsOpened())
                    break;

                // Einen Frame von der Kamera lesen
                bool success = _capture.Read(frame);

                if (!success || frame.Empty())
                {
                    // Kein Frame bekommen – kurz warten und nochmal versuchen
                    Thread.Sleep(10);
                    continue;
                }

                // Frame als JPEG-Bytes kodieren (für Anzeige in WPF und API-Calls)
                var jpegBytes = frame.ImEncode(".jpg");

                // Letzten Frame merken (für Snapshot-Funktion)
                _lastFrame = jpegBytes;

                // Alle Subscriber benachrichtigen (z.B. das UI für Live-Anzeige)
                FrameReceived?.Invoke(jpegBytes);

                // ~30 FPS: ca. 33ms zwischen Frames
                Thread.Sleep(33);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fehler beim Lesen des Kamera-Frames");
                Thread.Sleep(100);
            }
        }
    }

    public void Stop()
    {
        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();
            _captureThread?.Join(timeout: TimeSpan.FromSeconds(2));
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            _captureThread = null;
        }

        if (_capture != null)
        {
            _capture.Release();
            _capture.Dispose();
            _capture = null;
        }

        _lastFrame = null;
        Log.Information("Kamera gestoppt");
    }

    public byte[]? CaptureSnapshot()
    {
        // Einfach den zuletzt gelesenen Frame zurückgeben
        return _lastFrame;
    }

    public void Dispose()
    {
        Stop();
    }
}
