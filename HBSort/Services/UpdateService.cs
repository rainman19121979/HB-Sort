using Serilog;
using Velopack;
using Velopack.Sources;

namespace HBSort.Services;

/// <summary>
/// Velopack-basierte Implementierung von IUpdateService.
///
/// Zwei Lebensweisen der App:
/// 1. Per Setup.exe installiert -> UpdateManager.IsInstalled = true,
///    Update-Pfad ist aktiv.
/// 2. Per Portable-ZIP entpackt -> IsInstalled = false, alle Update-
///    Calls liefern null/false. Die UI versteckt das Update-Badge.
///
/// GitHub-Source: das Repo veroeffentlicht die Velopack-Artefakte
/// (Setup.exe, *-full.nupkg, RELEASES) als Release-Assets. Velopack
/// liest die RELEASES-Datei aus dem latest Release und entscheidet
/// anhand der dort gelisteten Version + den Delta-Eintraegen ob ein
/// Update sinnvoll ist.
/// </summary>
public class UpdateService : IUpdateService
{
    private readonly UpdateManager _updateManager;
    private UpdateInfo? _pendingUpdate;

    public UpdateService()
    {
        // Public-Repo, kein Token noetig. preReleases=false weil wir nur
        // stable Releases als Update-Quelle wollen - Pre-Releases (wenn
        // wir mal welche haben) bleiben dann eine bewusste manuelle Wahl.
        var source = new GithubSource(
            "https://github.com/rainman19121979/HB-Sort",
            accessToken: null,
            prerelease: false);

        _updateManager = new UpdateManager(source);
    }

    public bool IsInstalled => _updateManager.IsInstalled;

    public string? AvailableVersion => _pendingUpdate?.TargetFullRelease.Version.ToString();

    public async Task<bool> CheckForUpdatesAsync()
    {
        if (!_updateManager.IsInstalled)
        {
            Log.Debug("UpdateService: App ist nicht ueber Setup.exe installiert -> kein Update-Check");
            return false;
        }

        try
        {
            _pendingUpdate = await _updateManager.CheckForUpdatesAsync();
            if (_pendingUpdate is null)
            {
                Log.Information("UpdateService: keine neue Version gefunden");
                return false;
            }

            Log.Information("UpdateService: Update verfuegbar -> {Version}",
                _pendingUpdate.TargetFullRelease.Version);
            // Hypothese B (Threading): in welchem Thread laufen wir hier?
            // Wenn UI-Thread (1) -> Continuation-Capture funktioniert; Caller
            // setzt Property nach await safe auf UI-Thread.
            // Wenn anderer Thread -> Velopack hat den Sync-Context verloren.
            Log.Information("[DIAG-UI] UpdateService.CheckForUpdatesAsync return - ManagedThreadId={ThreadId} IsBackground={IsBg} CheckAccess={CheckAccess}",
                System.Threading.Thread.CurrentThread.ManagedThreadId,
                System.Threading.Thread.CurrentThread.IsBackground,
                System.Windows.Application.Current?.Dispatcher.CheckAccess() ?? false);
            return true;
        }
        catch (Exception ex)
        {
            // Netzwerk-Fehler, GitHub-Rate-Limit, RELEASES-Datei kaputt etc.
            // Update-Check ist nice-to-have - wir loggen und liefern false.
            Log.Warning(ex, "UpdateService: CheckForUpdatesAsync fehlgeschlagen");
            return false;
        }
    }

    public async Task DownloadAndApplyAsync(IProgress<int>? progress = null)
    {
        if (_pendingUpdate is null)
        {
            Log.Warning("UpdateService: DownloadAndApplyAsync ohne pending Update");
            return;
        }

        try
        {
            // Velopack 0.0.1298 nimmt Action<int> als Progress-Callback (0..100),
            // unsere Public-API ist IProgress<int> - kleiner Adapter, damit
            // Caller komfortable IProgress.Report nutzen koennen.
            Action<int>? progressCallback = progress is null ? null : progress.Report;

            await _updateManager.DownloadUpdatesAsync(_pendingUpdate, progressCallback);

            // ApplyUpdatesAndRestart beendet die laufende App, schwenkt
            // den current-Symlink auf die neue Version und startet die
            // App neu. Aufrufer-Code nach diesem Punkt laeuft nicht mehr.
            _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UpdateService: DownloadAndApplyAsync fehlgeschlagen");
            throw;
        }
    }
}
