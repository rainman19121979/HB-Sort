namespace HBSort.Services;

/// <summary>
/// UX-Iteration X.23: Auto-Update via Velopack + GitHub Releases.
///
/// Pruefen, herunterladen und installieren von neuen Versionen.
/// Velopack erkennt anhand des Installationspfads ob die App ueber den
/// Setup.exe-Installer installiert wurde - wenn nicht (Portable-ZIP-User),
/// liefert CheckForUpdatesAsync false und alles bleibt deaktiviert.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Prueft im Hintergrund ob eine neue Version auf GitHub Releases liegt.
    /// True wenn ein Update verfuegbar ist; AvailableVersion ist dann gesetzt.
    /// False bei Portable-Install, kein Internet, oder schon aktuelle Version.
    /// </summary>
    Task<bool> CheckForUpdatesAsync();

    /// <summary>Versions-String der gefundenen neuen Version (z.B. "0.2.0"), null wenn keine.</summary>
    string? AvailableVersion { get; }

    /// <summary>True wenn die App ueber den Velopack-Setup installiert wurde.</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Laedt das anstehende Update herunter, wendet es an und startet die App neu.
    /// Nur sinnvoll nach erfolgreichem CheckForUpdatesAsync mit Treffer.
    /// </summary>
    Task DownloadAndApplyAsync(IProgress<int>? progress = null);
}
