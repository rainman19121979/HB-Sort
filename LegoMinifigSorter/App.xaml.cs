using System.IO;
using System.Net.Http;
using System.Windows;
using LegoMinifigSorter.Core.Database;
using LegoMinifigSorter.Core.Services;
using LegoMinifigSorter.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace LegoMinifigSorter;

/// <summary>
/// App.xaml.cs ist der Einstiegspunkt der WPF-Anwendung.
/// Hier wird alles initialisiert: Logging, DI-Container, Datenbank, Fenster.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Der DI-Container (Dependency Injection).
    /// Alle Services werden hier registriert und können dann überall angefordert werden.
    /// Das macht den Code testbar, weil wir Services in Tests durch Mocks ersetzen können.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    // Pfad zum App-Daten-Ordner: %APPDATA%\LegoMinifigSorter\
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LegoMinifigSorter");

    /// <summary>
    /// Wird beim App-Start aufgerufen (statt StartupUri in XAML).
    /// Wir steuern den Start selbst, damit wir DI, Logging etc. einrichten können.
    /// </summary>
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        // 1. Ordnerstruktur sicherstellen
        EnsureDirectories();

        // 2. Logging konfigurieren (Serilog)
        SetupLogging();

        Log.Information("=== LEGO Minifig Sortierer startet ===");
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

            // 4.5 UI-Density anwenden BEVOR Fenster geladen werden, damit
            // alle DynamicResource-Lookups direkt den richtigen Wert haben.
            await ApplyStoredUiDensityAsync(settingsService);

            // 5. Datenbank erstellen/migrieren
            await InitializeDatabaseAsync();

            // 6. Backup der userdata.db beim Start anlegen
            BackupUserData();

            // 6.5 Cleanup: Altdaten – nicht-wartende Figuren von Faechern loesen,
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

            // 7. Catalog-Check: existiert catalog.db?
            // Falls nicht, Splash mit Erstinitialisierung anzeigen.
            if (!EnsureCatalogAsync())
            {
                // User hat den Import abgebrochen → App beenden
                Log.Information("App wird beendet (Catalog-Import nicht abgeschlossen)");
                Shutdown(0);
                return;
            }

            // 8. Hauptfenster anzeigen
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
    /// Logs werden 30 Tage aufbewahrt, dann automatisch gelöscht.
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
    /// Jeder Service wird über sein Interface registriert (z.B. ICameraService → CameraService),
    /// damit wir in Tests leicht Mock-Implementierungen einsetzen können.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // Services als Singleton registrieren (eine Instanz für die gesamte App-Laufzeit)
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ICameraService, CameraService>();

        // Catalog-Services (Phase 1.5)
        services.AddTransient<ICatalogImporter, CatalogImporter>();
        services.AddSingleton<ICatalogService, CatalogService>();

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
        // Phase 3: Verkettet Brickognize-Treffer mit Catalog-Stammdaten
        services.AddSingleton<IMinifigLookupService, MinifigLookupService>();

        // Phase R1: BrickLink-Store-API (OAuth1).
        // Token-Storage haengt am SettingsService; der Client-Wrapper laed lazy.
        services.AddSingleton<IBricklinkTokenStorage, BricklinkTokenStorage>();
        services.AddSingleton<IBricklinkClient, BricklinkClient>();

        // Phase R2: BL-Cache-Repository + BlCatalogService (Cache-First-Lookup).
        services.AddSingleton<IBlCacheRepository, BlCacheRepository>();
        // Phase R2.5: eigener Rate-Limiter mit Hard-Stop vor BLs 5000/24h.
        services.AddSingleton<IBricklinkRateLimiter, BricklinkRateLimiter>();
        services.AddSingleton<IBlCatalogService, BlCatalogService>();

        // NotificationService als Singleton, beide Registrierungen zeigen auf die selbe
        // Instanz – damit MainViewModel die ObservableCollection direkt anbinden kann.
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());

        // UI-Density-Service (Compact/Normal/Comfortable). Wechselt zur Laufzeit
        // das Density-ResourceDictionary in Application.Resources.
        services.AddSingleton<IUiDensityService, UiDensityService>();

        // EF Core DbContext für userdata.db.
        // Wir registrieren beide Wege: AddDbContext (fuer Migrationen) und
        // AddDbContextFactory (damit Services pro Operation einen frischen Context
        // bekommen koennen — wichtig in WPF, wo es keinen klaren Scope gibt).
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

        // ViewModels
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<ViewModels.ScanViewModel>();
        services.AddTransient<ViewModels.SettingsViewModel>();
        services.AddTransient<ViewModels.SplashViewModel>();
        services.AddTransient<ViewModels.BinManagerViewModel>();
        services.AddTransient<ViewModels.BinBulkCreateViewModel>();
        services.AddSingleton<ViewModels.WaitingMinifigsViewModel>();
        services.AddSingleton<ViewModels.InventoryListViewModel>();

        // Phase 4: Bin-Dialoge (transient – pro Aufruf eine neue Instanz).
        services.AddTransient<Views.BinCreateDialog>();
        services.AddTransient<Views.BinBulkCreateDialog>();

        // Fenster
        services.AddSingleton<MainWindow>();
        services.AddTransient<Views.SplashWindow>();
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
    /// Liest die gespeicherte UiDensity-Setting und wendet sie via
    /// UiDensityService an. Default ist Normal wenn nichts gespeichert ist.
    /// </summary>
    private static async Task ApplyStoredUiDensityAsync(ISettingsService settings)
    {
        var raw = settings.Current.UiDensity ?? "Normal";
        if (!Enum.TryParse<UiDensity>(raw, ignoreCase: true, out var density))
        {
            density = UiDensity.Normal;
        }

        var svc = Services.GetRequiredService<IUiDensityService>();
        await svc.ApplyAsync(density);
    }

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
                Log.Information("Keine BL-Tokens hinterlegt – BL-Test uebersprungen.");
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
    /// Prueft ob die catalog.db existiert. Wenn nicht, oeffnet die Splash-View
    /// mit dem Erstinitialisierungs-Import.
    /// Gibt true zurueck wenn alles OK ist (entweder existierte schon, oder Import war erfolgreich).
    /// Gibt false zurueck wenn der User den Import abgebrochen hat.
    /// </summary>
    private static bool EnsureCatalogAsync()
    {
        var catalogService = Services.GetRequiredService<ICatalogService>();
        if (catalogService.CatalogExists())
        {
            Log.Information("catalog.db existiert, Erstinitialisierung uebersprungen");
            return true;
        }

        Log.Information("catalog.db nicht vorhanden, starte Erstinitialisierung");

        // Splash-Fenster modal anzeigen. Es startet den Import im Loaded-Event.
        var splash = Services.GetRequiredService<Views.SplashWindow>();
        var result = splash.ShowDialog();

        return result == true;
    }

    /// <summary>
    /// Erstellt ein Backup der userdata.db beim App-Start.
    /// Falls die Datei beschädigt wird, haben wir wenigstens den Stand vom letzten Start.
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
    /// Stellt sicher, dass alle benötigten Unterordner im AppData-Ordner existieren.
    /// </summary>
    private static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataFolder);
        Directory.CreateDirectory(Path.Combine(AppDataFolder, "images"));
        Directory.CreateDirectory(Path.Combine(AppDataFolder, "scans"));
        Directory.CreateDirectory(Path.Combine(AppDataFolder, "logs"));
    }

    /// <summary>Liest die aktuelle App-Version aus der Assembly</summary>
    private static string GetVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.1.0";
    }

    /// <summary>
    /// Wird aufgerufen wenn die App beendet wird.
    /// Räumt auf: Kamera freigeben, Logging flushen.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("=== LEGO Minifig Sortierer beendet ===");

        // Kamera sauber freigeben
        if (Services != null)
        {
            var cameraService = Services.GetService<ICameraService>();
            cameraService?.Dispose();
        }

        // Serilog-Puffer leeren (damit der letzte Log-Eintrag noch geschrieben wird)
        Log.CloseAndFlush();

        base.OnExit(e);
    }
}
