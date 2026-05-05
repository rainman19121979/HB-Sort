using System.IO;
using System.Net.Http;
using System.Windows;
using HBSort.Core.Database;
using HBSort.Core.Services;
using HBSort.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort;

/// <summary>
/// App.xaml.cs ist der Einstiegspunkt der WPF-Anwendung.
/// Hier wird alles initialisiert: Logging, DI-Container, Datenbank, Fenster.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Der DI-Container (Dependency Injection).
    /// Alle Services werden hier registriert und koennen dann ueberall angefordert werden.
    /// Das macht den Code testbar, weil wir Services in Tests durch Mocks ersetzen koennen.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    // Pfad zum App-Daten-Ordner: %APPDATA%\HBSort\
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HBSort");

    // Alter AppData-Pfad (vor dem Renaming auf HBSort) - fuer einmalige
    // Auto-Migration des Datenbestands beim ersten Start nach dem Rename.
    private static readonly string LegacyAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LegoMinifigSorter");

    /// <summary>
    /// Wird beim App-Start aufgerufen (statt StartupUri in XAML).
    /// Wir steuern den Start selbst, damit wir DI, Logging etc. einrichten koennen.
    /// </summary>
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        // 0. Auto-Migration: alter Datenbestand (LegoMinifigSorter) -> neuer Pfad (HBSort).
        // Muss VOR EnsureDirectories laufen, weil wir den neuen Ordner sonst leer anlegen.
        await MigrateLegacyAppDataIfNeededAsync();

        // 1. Ordnerstruktur sicherstellen
        EnsureDirectories();

        // 2. Logging konfigurieren (Serilog)
        SetupLogging();

        Log.Information("=== HB-Sort startet ===");
        Log.Information("Version: {Version}", GetVersion());
        Log.Information("AppData-Ordner: {Path}", AppDataFolder);

        try
        {
            // 3. DI-Container aufbauen
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            Services = serviceCollection.BuildServiceProvider();

            // 4. Settings laden
            var settingsService = Services.GetRequiredService<ISettingsService>();
            await settingsService.LoadAsync();

            // 4.6 Tooltips-Schalter anwenden (UX-Iteration X.9). Schreibt den
            // gespeicherten ShowTooltips-Wert in die Application-Resource,
            // bevor das erste Fenster gezeigt wird.
            await Services.GetRequiredService<ITooltipsService>().ApplyAsync();

            // 5. Datenbank erstellen/migrieren
            await InitializeDatabaseAsync();

            // 6. Backup der userdata.db beim Start anlegen
            BackupUserData();

            // 6.5 Cleanup: Altdaten - nicht-wartende Figuren von Faechern loesen,
            // damit BinManager-Zaehler und DeleteAsync konsistent bleiben.
            await CleanupStaleBinAssignmentsAsync();

            // 6.6 Cleanup: alte DISMANTLED-Figuren komplett loeschen (T2-Migration).
            // Frueher hat "Aufgeben" Status=Dismantled gesetzt; jetzt wird die Figur
            // direkt geloescht. Wir raeumen Altbestaende beim Start auf.
            await CleanupOldDismantledMinifigsAsync();

            // 6.7 Cleanup: Pseudo-Figuren aus dem alten BL-Catalog-Collect-Bug loeschen
            // (Status=COMPLETE mit genau 1 RequiredPart, entstanden durch Single-Row-
            // Supersets-Cache vor Einfuehrung von IsFromSupersets).
            await CleanupOnePartCompletesAsync();

            // 7. Hauptfenster anzeigen
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            // 9. Phase R1: BL-API-Verbindung im Hintergrund pruefen wenn Tokens vorhanden.
            // Fire-and-forget: blockiert NICHT den Startup. Wenn keine Tokens da sind,
            // ueberspringen wir den Test komplett (User kann in Phase R3 nichts lookuppen,
            // aber die App startet normal).
            _ = CheckBricklinkConnectionInBackground();

            // 10. Phase R2.5: Rate-Limiter-Wartung (alte Eintraege pruning).
            _ = PruneRateLimiterLogAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Schwerwiegender Fehler beim App-Start");
            MessageBox.Show(
                $"Die Anwendung konnte nicht gestartet werden:\n\n{ex.Message}",
                "Fehler beim Start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Richtet Serilog ein: Konsole + tagesweise rotierende Logdateien.
    /// Logs werden 30 Tage aufbewahrt, dann automatisch geloescht.
    /// </summary>
    private static void SetupLogging()
    {
        var logPath = Path.Combine(AppDataFolder, "logs", "app-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            // rollingInterval: Jeden Tag eine neue Datei (app-2026-04-30.log)
            // retainedFileCountLimit: Maximal 30 Dateien behalten
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    /// <summary>
    /// Registriert alle Services im DI-Container.
    /// Jeder Service wird ueber sein Interface registriert (z.B. ICameraService → CameraService),
    /// damit wir in Tests leicht Mock-Implementierungen einsetzen koennen.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // Services als Singleton registrieren (eine Instanz fuer die gesamte App-Laufzeit)
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ICameraService, CameraService>();

        // Phase 2: Brickognize + ID-Resolver + Notifications.
        // Wir registrieren einen statischen HttpClient (kein AddHttpClient-Factory,
        // weil wir nur einen einzigen Endpoint haben und einen langen Timeout brauchen).
        services.AddSingleton<HttpClient>(_ =>
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri("https://api.brickognize.com"),
                Timeout = TimeSpan.FromSeconds(60) // siehe BRICKOGNIZE_API.md
            };
            return client;
        });
        services.AddSingleton<IBrickognizeClient, BrickognizeClient>();
        services.AddSingleton<IExternalIdResolver, ExternalIdResolver>();
        // PersistentImageCache: SQLite-Sidecar-DB + LRU-Eviction unter %APPDATA%/images/
        services.AddSingleton<IPersistentImageCache, PersistentImageCache>();
        // PartImageProvider: holt das beste Bild (BL-First, Brickognize-Fallback) ueber den Cache
        services.AddSingleton<IPartImageProvider, BricklinkImageProvider>();

        // Phase R1: BrickLink-Store-API (OAuth1).
        // Token-Storage haengt am SettingsService; der Client-Wrapper laed lazy.
        services.AddSingleton<IBricklinkTokenStorage, BricklinkTokenStorage>();
        services.AddSingleton<IBricklinkClient, BricklinkClient>();

        // Phase R2: BL-Cache-Repository + BlCatalogService (Cache-First-Lookup).
        services.AddSingleton<IBlCacheRepository, BlCacheRepository>();
        // Phase 5.5: BrickStore-Bulk-Import (GitHub-Download + lokaler Folder).
        services.AddSingleton<IBlBulkImportService, BlBulkImportService>();
        // Phase R2.5: eigener Rate-Limiter mit Hard-Stop vor BLs 5000/24h.
        services.AddSingleton<IBricklinkRateLimiter, BricklinkRateLimiter>();
        services.AddSingleton<IBlCatalogService, BlCatalogService>();

        // NotificationService als Singleton, beide Registrierungen zeigen auf die selbe
        // Instanz - damit MainViewModel die ObservableCollection direkt anbinden kann.
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());

        // DialogService (UX-Iteration X.4): einheitliche ContentDialogs statt MessageBox.
        services.AddSingleton<IDialogService, DialogService>();

        // UX-Iteration X.11 (2026-05-04): Darstellungsdichte komplett entfernt.
        // Frueher gab es hier IUiDensityService mit Compact/Normal/Comfortable -
        // jetzt sind die Werte fest (Compact-Profil) als XAML-Literale eingebettet.

        // UX-Iteration X.9: globaler Tooltips-Schalter (Default: an).
        services.AddSingleton<ITooltipsService, TooltipsService>();

        // EF Core DbContext fuer userdata.db.
        // Wir registrieren beide Wege: AddDbContext (fuer Migrationen) und
        // AddDbContextFactory (damit Services pro Operation einen frischen Context
        // bekommen koennen - wichtig in WPF, wo es keinen klaren Scope gibt).
        var dbPath = Path.Combine(AppDataFolder, "userdata.db");
        services.AddDbContext<UserDataContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));
        services.AddDbContextFactory<UserDataContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"), ServiceLifetime.Singleton);

        // Phase 4: Lagerfach-Verwaltung + Minifig-Persistenz mit Reverse-Match
        services.AddSingleton<IStorageBinService, StorageBinService>();
        services.AddSingleton<IMinifigPersistenceService, MinifigPersistenceService>();

        // Phase 5: PartLookup (Modus B)
        services.AddSingleton<IPartLookupService, PartLookupService>();

        // UX-Iteration X.4+: FloatingPart->Pending-Minifig-Transfer
        // (Button "Aus Fach uebernehmen" in der MinifigDetailView).
        services.AddSingleton<IFloatingPartTransferService, FloatingPartTransferService>();

        // Phase 7: BSX-Export
        services.AddSingleton<IBsxExportService, BsxExportService>();

        // BL-Wanted-List-Export (UX-Iteration X.4): generiert Wanted-List-XMLs
        // aus den fehlenden Teilen wartender Figuren.
        services.AddSingleton<IWantedListExportService, WantedListExportService>();

        // Phase 8: Preise. Beide Provider als Singleton damit der ProviderFactory
        // bei jedem Settings-Wechsel einfach den passenden zurueckgibt - die
        // VMs muessen nichts neu instanziieren.
        services.AddSingleton<DummyPriceProvider>();
        services.AddSingleton<BricklinkApiPriceProvider>();
        services.AddSingleton<IPriceProviderFactory, PriceProviderFactory>();
        services.AddSingleton<IPriceCalculationService, PriceCalculationService>();

        // UX#12: Stale-While-Revalidate-Wrapper ueber den Provider, mit
        // In-Flight-Schutz. Wird vom MinifigPriceViewModel fuer die Live-
        // Anzeige in der oberen rechten Box des Sortier-Tabs benutzt.
        services.AddSingleton<IBlPriceCacheService, BlPriceCacheService>();

        // ViewModels
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<ViewModels.ScanViewModel>();
        services.AddTransient<ViewModels.SettingsViewModel>();
        services.AddTransient<ViewModels.BinManagerViewModel>();
        services.AddTransient<ViewModels.BinBulkCreateViewModel>();
        services.AddSingleton<ViewModels.InventoryListViewModel>();

        // Variables Feld unten rechts - Singletons damit die VMs auch dann live
        // bleiben wenn der User auf einen anderen Tab wechselt und zurueck.
        services.AddSingleton<ViewModels.BuildSuggestionsViewModel>();
        services.AddSingleton<ViewModels.LiveStatsViewModel>();
        services.AddSingleton<ViewModels.WaitingDetailViewModel>();
        services.AddSingleton<ViewModels.RecentScansViewModel>();

        // UX-Iteration X.9: Hilfe-Tab.
        // ContentService liefert die Markdown-Dateien aus den eingebetteten
        // Resources; Singleton, weil index.json einmal beim Start gelesen wird.
        services.AddSingleton<IHelpContentService, HelpContentService>();
        services.AddSingleton<ViewModels.HelpViewModel>();

        // Phase 4: Bin-Dialoge (transient - pro Aufruf eine neue Instanz).
        services.AddTransient<Views.BinCreateDialog>();
        services.AddTransient<Views.BinBulkCreateDialog>();

        // Phase 7: BSX-Export-Dialog
        services.AddTransient<Views.BsxExportDialog>();

        // UX-Iteration X.4: Wanted-List-Export-Dialog (transient - pro Klick eine
        // neue Instanz, damit Status/Inputs frisch sind).
        services.AddTransient<Views.WantedListExportDialog>();

        // Fenster
        services.AddSingleton<MainWindow>();
    }

    /// <summary>
    /// Erstellt die Datenbank-Tabellen falls sie noch nicht existieren.
    /// EF Core "Migrate" wendet alle ausstehenden Migrationen an.
    /// </summary>
    private static async Task InitializeDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserDataContext>();
        await context.Database.MigrateAsync();
        Log.Information("Datenbank userdata.db bereit");
    }

    /// <summary>
    /// Einmaliger Cleanup beim Start: Figuren die im Fach liegen, aber nicht
    /// mehr WAITING sind, werden ab-gekoppelt (StorageBinId=null). Verhindert
    /// Inkonsistenzen zwischen Anzeige (zaehlt nur belegte) und DeleteAsync
    /// (blockt bei jeder Figur-Zuordnung).
    /// </summary>
    private static async Task CleanupStaleBinAssignmentsAsync()
    {
        try
        {
            var binService = Services.GetRequiredService<IStorageBinService>();
            var detached = await binService.CleanupStaleBinAssignmentsAsync();
            if (detached > 0)
            {
                Log.Information("Startup-Cleanup: {Count} Figuren aus Faechern geloest", detached);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup-Cleanup geworfen");
        }
    }

    /// <summary>
    /// Beim Start: alle Figuren mit Status=Dismantled werden komplett geloescht.
    /// "Aufgeben" -> "Zerlegen" loescht die Figur jetzt direkt; alte Datenbestaende
    /// werden hier aufgeraeumt.
    /// </summary>
    private static async Task CleanupOldDismantledMinifigsAsync()
    {
        try
        {
            var persistence = Services.GetRequiredService<IMinifigPersistenceService>();
            await persistence.CleanupOldDismantledMinifigsAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup-Cleanup DISMANTLED geworfen");
        }
    }

    /// <summary>
    /// Beim Start: Pseudo-Figuren mit Status=COMPLETE und genau 1 RequiredPart loeschen.
    /// Diese sind durch den alten BL-Catalog-Collect-Bug entstanden, bevor die
    /// IsFromSupersets-Markierung in bl_subsets eingefuehrt wurde.
    /// </summary>
    private static async Task CleanupOnePartCompletesAsync()
    {
        try
        {
            var persistence = Services.GetRequiredService<IMinifigPersistenceService>();
            await persistence.CleanupOnePartCompletesAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup-Cleanup OnePart-COMPLETE geworfen");
        }
    }

    /// <summary>
    /// Prueft im Hintergrund ob die BL-API erreichbar ist. Wenn keine Tokens
    /// gespeichert sind: still beenden, kein Toast (User kann ja noch Tokens
    /// eingeben). Wenn Tokens vorhanden aber Test schlaegt fehl: Toast.
    /// </summary>
    /// <summary>
    /// Loescht api_call_log-Eintraege aelter als 7 Tage (sind weder fuer 24h-Window
    /// noch fuer "Heute" relevant). Ein einmaliger Aufruf beim App-Start reicht.
    /// </summary>
    private static async Task PruneRateLimiterLogAsync()
    {
        try
        {
            var limiter = Services.GetRequiredService<IBricklinkRateLimiter>();
            await limiter.PruneOldEntriesAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BL Rate-Limiter Prune geworfen");
        }
    }

    private static async Task CheckBricklinkConnectionInBackground()
    {
        try
        {
            var blClient = Services.GetRequiredService<IBricklinkClient>();
            if (!await blClient.IsConfiguredAsync())
            {
                Log.Information("Keine BL-Tokens hinterlegt - BL-Test uebersprungen.");
                return;
            }

            var test = await blClient.TestConnectionAsync();
            if (!test.Success)
            {
                var notif = Services.GetService<Services.INotificationService>();
                notif?.ShowError($"BL-API-Verbindung fehlgeschlagen: {test.ErrorMessage}");
                Log.Warning("BL TestConnection beim Start fehlgeschlagen: {Msg}", test.ErrorMessage);
                return;
            }

            Log.Information("BL TestConnection beim Start erfolgreich ({Ms} ms)", test.ResponseTimeMs);

            // Phase R2: bl_colors initial befuellen wenn leer (~250 Eintraege, einmalig).
            await EnsureColorListCachedAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BL-Hintergrund-Test geworfen");
        }
    }

    /// <summary>
    /// Holt einmalig die komplette BL-Farbliste, falls bl_colors leer ist.
    /// Nach Initial-Befuellung lebt die Liste lebenslang im Cache (Colors aendern sich
    /// extrem selten).
    /// </summary>
    private static async Task EnsureColorListCachedAsync()
    {
        try
        {
            var repo = Services.GetRequiredService<IBlCacheRepository>();
            var preExisting = (await repo.GetAllColorsAsync()).Count;

            if (preExisting > 0)
            {
                Log.Debug("BL-Color-Liste bereits gecacht ({Count} Farben)", preExisting);
                return;
            }

            // Cache war leer: BlCatalogService.GetAllColorsAsync triggert die Erstbefuellung
            var catalog = Services.GetRequiredService<IBlCatalogService>();
            var fetched = await catalog.GetAllColorsAsync();
            if (fetched.Count > 0)
            {
                var notif = Services.GetService<Services.INotificationService>();
                notif?.ShowSuccess($"BL-Farben importiert ({fetched.Count} Eintraege)");
                Log.Information("BL-Color-Initialimport: {Count} Eintraege", fetched.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Konnte BL-Color-Liste nicht initial laden");
        }
    }

    /// <summary>
    /// Erstellt ein Backup der userdata.db beim App-Start.
    /// Falls die Datei beschaedigt wird, haben wir wenigstens den Stand vom letzten Start.
    /// </summary>
    private static void BackupUserData()
    {
        var dbPath = Path.Combine(AppDataFolder, "userdata.db");
        var backupPath = Path.Combine(AppDataFolder, "userdata.db.bak");

        if (File.Exists(dbPath))
        {
            try
            {
                File.Copy(dbPath, backupPath, overwrite: true);
                Log.Information("userdata.db Backup erstellt");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Konnte kein Backup von userdata.db erstellen");
            }
        }
    }

    /// <summary>
    /// Stellt sicher, dass alle benoetigten Unterordner im AppData-Ordner existieren.
    /// </summary>
    private static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataFolder);
        Directory.CreateDirectory(Path.Combine(AppDataFolder, "images"));
        Directory.CreateDirectory(Path.Combine(AppDataFolder, "scans"));
        Directory.CreateDirectory(Path.Combine(AppDataFolder, "logs"));
    }

    /// <summary>
    /// Auto-Migration: wenn der alte AppData-Ordner (LegoMinifigSorter) existiert
    /// und im neuen Ordner (HBSort) noch keine userdata.db liegt, wird der
    /// komplette Datenbestand kopiert. Einmalig beim ersten Start nach dem Rename.
    /// </summary>
    private static async Task MigrateLegacyAppDataIfNeededAsync()
    {
        try
        {
            if (!Directory.Exists(LegacyAppDataFolder)) return;

            // Wenn im neuen Ordner schon userdata.db existiert: schon migriert
            // ODER frische Installation - nicht ueberschreiben.
            var newDbExists = File.Exists(Path.Combine(AppDataFolder, "userdata.db"));
            if (newDbExists) return;

            CopyDirectory(LegacyAppDataFolder, AppDataFolder);

            // Marker im alten Ordner setzen, damit klar ist dass migriert wurde.
            // Audit M-8 (2026-05-04): WriteAllTextAsync statt sync. Laeuft hier
            // einmalig vor dem App-Start, also kein UI-Block-Risiko - aber
            // konsistent mit dem Rest der File-IO-Konvention.
            // User-sichtbare Notiz, daher lokale Zeit (nicht UTC).
            await File.WriteAllTextAsync(
                Path.Combine(LegacyAppDataFolder, "MIGRATED_TO_HBSort.txt"),
                $"Daten wurden nach {AppDataFolder} migriert am " +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}.\n" +
                "Dieser Ordner kann nach Verifikation manuell geloescht werden.");
        }
        catch (Exception ex)
        {
            // Logging ist hier noch nicht initialisiert -> Trace.
            System.Diagnostics.Trace.WriteLine(
                $"AppData-Migration LegoMinifigSorter -> HBSort fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>Kopiert ein Verzeichnis rekursiv inkl. Dateien + Unterordner.</summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            try { File.Copy(file, destFile, overwrite: false); }
            catch (IOException) { /* Datei existiert schon - ueberspringen */ }
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }

    /// <summary>Liest die aktuelle App-Version aus der Assembly</summary>
    private static string GetVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.1.0";
    }

    /// <summary>
    /// Wird aufgerufen wenn die App beendet wird.
    /// Raeumt auf: alle DI-Singletons freigeben, Logging flushen.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("[SPLITTER] OnExit called");
        Log.Information("=== HB-Sort beendet ===");

        // ServiceProvider disponieren - das ruft Dispose() auf allen Singleton-
        // Services die IDisposable implementieren in der korrekten Reihenfolge.
        // Insbesondere: BlCacheRepository + PersistentImageCache schliessen ihre
        // SQLite-Connections, CameraService stoppt den Capture-Thread.
        (Services as IDisposable)?.Dispose();

        // Serilog-Puffer leeren (damit der letzte Log-Eintrag noch geschrieben wird)
        Log.CloseAndFlush();

        base.OnExit(e);
    }
}
