namespace HBSort.Core.Services;

/// <summary>
/// Verwaltet den Zugriff auf USB-Webcams via OpenCvSharp4.
/// Stellt Live-Frames bereit und kann Schnappschuesse aufnehmen.
///
/// OpenCvSharp4 ist ein C#-Wrapper fuer OpenCV (Open Computer Vision),
/// eine der bekanntesten Bibliotheken fuer Bildverarbeitung und Kamera-Zugriff.
/// </summary>
public interface ICameraService : IDisposable
{
    /// <summary>Wird ausgeloest wenn ein neuer Frame von der Kamera kommt</summary>
    event Action<byte[]>? FrameReceived;

    /// <summary>Ist gerade eine Kamera aktiv und liefert Bilder?</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Findet alle angeschlossenen USB-Kameras und gibt ihre Namen zurueck.
    /// Der Index in der Liste entspricht dem Kamera-Index fuer OpenCamera().
    /// </summary>
    List<string> GetAvailableCameras();

    /// <summary>Oeffnet die Kamera mit dem angegebenen Index und startet den Frame-Stream</summary>
    Task StartAsync(int cameraIndex);

    /// <summary>Stoppt den Frame-Stream und gibt die Kamera frei</summary>
    void Stop();

    /// <summary>
    /// Nimmt den aktuellen Frame als Schnappschuss und gibt ihn als JPEG-Bytes zurueck.
    /// Gibt null zurueck wenn keine Kamera aktiv ist.
    /// </summary>
    byte[]? CaptureSnapshot();
}
