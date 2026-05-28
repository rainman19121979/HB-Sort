using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Velopack;

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
        // UX-Iteration X.23: Velopack-Hook MUSS als allererstes laufen.
        // Wenn die App per Setup.exe installiert ist und gerade ein Update
        // oder Erst-Lauf passiert, beendet VelopackApp.Run() den Prozess
        // intern - der restliche App-Code laeuft dann gar nicht erst los.
        // Bei nicht-installierten Apps (Portable-ZIP, Debug-Builds) ist
        // das ein No-Op.
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception velopackEx)
        {
            // Defensiv: wir wollen NIE dass Velopack die App killt. Bei
            // jedem Fehler einfach loggen und weiter starten - die App
            // funktioniert auch ohne Update-Mechanismus.
            // Logging ist hier noch nicht initialisiert, daher Debug.WriteLine.
            Debug.WriteLine($"[Velopack] Init fehlgeschlagen, ueberspringe: {velopackEx}");
        }

        // UX-Iteration X.23: Splash-Window VOR allem anderen anzeigen, damit
        // der User sofort Feedback bekommt (Logo + ProgressRing). Der Splash
        // bleibt mind. 800ms sichtbar (Splash-Stopwatch) und wird nach dem
        // MainWindow.Show() unten wieder geschlossen.
        var splashStopwatch = Stopwatch.StartNew();
        var splash = new SplashWindow();
        splash.Show();

        // 0a. Pending-Restore-Check (UX X.29 / v0.1.16). Muss VOR allem
        // anderen laufen damit zur Laufzeit keine SQLite-Connection das
        // userdata.db / bl_cache.db File-Lock haelt. Direkter Restore zur
        // Laufzeit ist nicht moeglich - deshalb hat der User-Klick auf
        // "Wiederherstellen" die Files in %APPDATA%\HBSort\.pending-restore\
        // gelegt + den Prozess neu gestartet. Hier wird das jetzt
        // angewendet.
        TryApplyPendingRestoreSafe();

        // v0.1.22-beta.1 Block A: Splash-Status sichtbar machen. Pro Phase
        // ein kurzer Hinweis was gerade laeuft - statt eines einzigen "Bereitet
        // App vor"-Texts.
        splash.SetStatus("Pruefe alte Datenbestaende...");

        // 0b. Auto-Migration: alter Datenbestand (LegoMinifigSorter) -> neuer Pfad (HBSort).
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

            splash.SetStatus("Lade Einstellungen...");

            // 4. Settings laden
            var settingsService = Services.GetRequiredService<ISettingsService>();
            await settingsService.LoadAsync();

            // 4.6 Tooltips-Schalter anwenden (UX-Iteration X.9). Schreibt den
            // gespeicherten ShowTooltips-Wert in die Application-Resource,
            // bevor das erste Fenster gezeigt wird.
            await Services.GetRequiredService<ITooltipsService>().ApplyAsync();

            splash.SetStatus("Initialisiere Datenbank...");

            // 5. Datenbank erstellen/migrieren
            await InitializeDatabaseAsync();

            // 6. Backup der userdata.db beim Start anlegen
            BackupUserData();

            splash.SetStatus("Pruefe Datenbestand...");

            // 6.5 DataHeal (UX X.28): Bug-B-Folgen aus alten Versionen heilen.
            // Bins die belegt sind aber FreedAt!=null haben werden korrigiert.
            // Idempotent - mehrfaches Aufrufen ist safe.
            await DataHealAsync();

            // 6.51 BrickognizeCategory-Backfill (Pre-Tag-Fix v0.1.19-beta.7):
            // Bestand-FloatingParts (vor Migration A oder via Zerlege-Pfaden mit
            // null-Bug) bekommen ihre Kategorie via Heuristik nachgereicht.
            // Idempotent - macht nur Eintraege mit null/empty an.
            await BackfillBrickognizeCategoriesAsync();

            // 6.512 v0.1.22-beta.1 Fix 2026-05-12: Backfill fuer leere
            // TrackedMinifigPart.PartName-Eintraege. Bestand vor dem Fix
            // (Block-D-Anlegepfad) hat die Spalte leer - dadurch faellt die
            // DeriveCategoryFromPartName-Heuristik beim Zerlegen auf
            // "Unbekannt" und die Default-Regel wird wirkungslos.
            // Idempotent: bearbeitet nur Zeilen mit null/empty PartName.
            await BackfillTrackedMinifigPartNamesAsync();

            // 6.513 v0.1.23: Backfill der StorageBin.Kind-Spalte. Die EF-Core-
            // Migration AddStorageBinKind setzt die neue Spalte initial auf
            // Empty (Default 0). Bestand-Bins mit Inhalten werden hier
            // einmalig korrekt klassifiziert (Empty/Floating/Waiting/Complete).
            // Idempotent - mehrfaches Aufrufen ist safe; bei bereits korrekt
            // gesetzten Bins passiert nichts.
            await BackfillStorageBinKindAsync();

            // 6.52 BL-Preis-Cache-Anti-Vergiftung (UX X.34 v0.1.20):
            // Eintraege mit allen Preis-Feldern NULL und total_quantity=0
            // sind das Resultat eines API-Calls mit leeren Filter-Strings
            // (Bug vor dem null-Fix). Solche Eintraege blockieren spaetere
            // Lookups, weil sie als gueltiger Cache-Hit zurueckkommen.
            // Idempotent - mehrfaches Aufrufen ist safe.
            await ClearEmptyPriceCacheEntriesAsync();

            // 6.525 v0.1.22-beta.1 Block G: Bestand-Mix-Bins detektieren
            // (Wartend+Floating ohne Reverse-Match-Bezug, Floating+Complete).
            // Fire-and-forget mit Toast, kein Auto-Move - User entscheidet.
            _ = Task.Run(WarnAboutBestandMixBinsAsync);

            // 6.53 v0.1.20.1 Auto-Trigger Catalog-Reimport wenn bl_colors leer ist,
            // bl_items aber voll. Das ist der Zustand alter v0.1.20-Installs vor dem
            // colors.xml-Importer-Fix - der Reimport schreibt die Farben jetzt nach.
            // Fire-and-forget; ETag-Check im BlBulkImportService verhindert Doppel-
            // Download wenn ohnehin nichts neues da ist. Blockiert NICHT den Start.
            _ = Task.Run(TryReimportCatalogIfColorsMissingAsync);

            splash.SetStatus("Pruefe Stammdaten...");

            // 6.55 Auto-Backup (UX X.29): wenn aktiviert + Intervall faellig,
            // im Hintergrund ein Backup erzeugen + alte aufraeumen. Fire-and-
            // forget - blockiert NICHT den Startup. Task.Run-Wrapping wegen
            // sync SQLite-BackupDatabase + ZIP-Pack auf ThreadPool statt
            // UI-Thread (siehe Auto-BL-Import-Bug-Fix in MainViewModel).
            _ = Task.Run(TryAutoBackupAsync);

            // 6.6 Cleanup: alte DISMANTLED-Figuren komplett loeschen (T2-Migration).
            // Frueher hat "Aufgeben" Status=Dismantled gesetzt; jetzt wird die Figur
            // direkt geloescht. Wir raeumen Altbestaende beim Start auf.
            await CleanupOldDismantledMinifigsAsync();

            // 6.7 Cleanup: Pseudo-Figuren aus dem alten BL-Catalog-Collect-Bug loeschen
            // (Status=COMPLETE mit genau 1 RequiredPart, entstanden durch Single-Row-
            // Supersets-Cache vor Einfuehrung von IsFromSupersets).
            await CleanupOnePartCompletesAsync();

            // v0.1.22-beta.3 (zweiter Versuch) 2026-05-13: Camera-Init
            // synchron im Splash-Pfad statt fire-and-forget in Window_Loaded.
            // User-Wunsch: Hauptfenster erscheint vollstaendig startklar inkl.
            // Live-Bild. Vorher zeigte das MainWindow 20s eine leere Camera-
            // Area waehrend InitializeCameraAsync im Hintergrund lief.
            splash.SetStatus("Initialisiere Kamera...");
            try
            {
                var mainVm = Services.GetRequiredService<ViewModels.MainViewModel>();
                await mainVm.ScanViewModel.InitializeCameraAsync();
            }
            catch (Exception cameraEx)
            {
                // Camera-Init darf den App-Start NICHT verhindern. Fehler nur
                // loggen, App startet ohne Camera (User kann in Settings die
                // Kamera waehlen).
                Log.Warning(cameraEx, "Camera-Init beim Start fehlgeschlagen - " +
                    "App startet ohne Live-Bild. User kann in Settings die Kamera waehlen.");
            }

            splash.SetStatus("Bereite Hauptfenster vor...");

            // 7. Hauptfenster anzeigen
            var mainWindow = Services.GetRequiredService<MainWindow>();

            // UX-Iteration X.23: Splash mind. 800ms anzeigen damit der User
            // ihn ueberhaupt wahrnimmt - bei kalt-gestartetem System dauert
            // die DB-Init oben allein meist > 800ms, der Delay greift dann
            // gar nicht. Bei warmem Start (zweiter App-Start innerhalb von
            // Sekunden) ist die Init schneller, der Delay garantiert eine
            // saubere Splash-Anzeige.
            var elapsed = splashStopwatch.ElapsedMilliseconds;
            if (elapsed < 800)
            {
                await Task.Delay((int)(800 - elapsed));
            }

            // UX X.34 v0.1.20-beta.3 Bug-Fix: Reihenfolge geaendert von
            // (mainWindow.Show, splash.Close) auf (splash.Close, mainWindow.Show)
            // mit dazwischenliegendem Dispatcher-Flush. Vorher konnte der
            // Splash-Z-Order kurz nach Close() noch ueber dem FirstRunDialog
            // bleiben - mit AllowsTransparency+Topmost (jetzt False) war das
            // besonders haeufig. Mit Dispatcher.BeginInvoke + Background-
            // Priority wartet der Code bis WPF den Splash wirklich aus dem
            // Compositor entfernt hat.
            splash.Close();
            await System.Windows.Threading.Dispatcher.CurrentDispatcher
                .InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
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
            // Splash auch im Fehlerfall schliessen, sonst haengt er ueber dem
            // MessageBox-Error-Dialog.
            try { splash.Close(); } catch { /* Splash schon zu - egal */ }

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

        // v0.1.24-beta.6 Phase 1: BL-Store-Inventar-Spiegel (userdata.db).
        services.AddSingleton<IBlInventoryService, BlInventoryService>();

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

        // UX X.33 v0.1.19-beta.7 Block M: Brickognize-Category -> Bin Mapping.
        services.AddSingleton<ICategoryBinMappingService, CategoryBinMappingService>();

        // UX X.28: Daten-Heal beim App-Start (Bug-B-Folgen automatisch korrigieren).
        services.AddSingleton<IDataHealService, DataHealService>();

        // UX X.34 v0.1.20-beta.2: First-Run-Onboarding-Erkennung.
        services.AddSingleton<IFirstRunService, FirstRunService>();

        // UX X.29 (v0.1.16): Backup-System. Konstruktor braucht den AppData-
        // Pfad damit die Backups in %APPDATA%\HBSort\backups\ landen.
        services.AddSingleton<IBackupService>(_ => new BackupService(AppDataFolder));

        // UX X.29 (v0.1.16): Undo-System. Liest ScanEvent-UndoData und
        // macht Aktionen rueckgaengig. Singleton weil zustandslos.
        services.AddSingleton<IUndoService, UndoService>();

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

        // v0.1.24-beta.1 Phase 1.5: Presenter fuer Post-Save-Sortier-Modal.
        // Wurzel-Fix fuer Owner.DataContext-Cast in verschachtelten Dialogen
        // (DismantleWizard aus MinifigSummary). Constructor-Injection braucht
        // MainViewModel - daher direkt nach dessen Registrierung.
        services.AddSingleton<ISortInstructionPresenter, SortInstructionPresenter>();
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

        // UX X.29 (v0.1.16): Verlauf-Tab.
        services.AddSingleton<ViewModels.HistoryViewModel>();

        // v0.1.24-beta.7 Phase 2: BrickLink-Inventar-Tab. Singleton damit der
        // Lade-Zustand und das InventoryChanged-Subscription beim Tab-Wechsel
        // erhalten bleiben.
        services.AddSingleton<ViewModels.BlInventoryViewModel>();

        // UX-Iteration X.23: Velopack-Auto-Update via GitHub Releases.
        // Singleton weil der UpdateManager den letzten Check-Stand cached
        // (pendingUpdate). Aufgerufen vom MainViewModel beim App-Start
        // (Background-Check) und ueber den Update-Badge im Header.
        services.AddSingleton<IUpdateService, UpdateService>();

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
    /// UX X.29 (v0.1.16): wendet einen vorbereiteten Pending-Restore an,
    /// falls einer in %APPDATA%\HBSort\.pending-restore\ liegt.
    ///
    /// Wird ZU ANFANG von Application_Startup aufgerufen, BEVOR Logging,
    /// DI oder DB-Init laufen - sonst haelt eine offene SqliteConnection
    /// das File-Lock auf userdata.db / bl_cache.db und der Restore
    /// scheitert mit IOException.
    ///
    /// Logging kann hier nur Debug.WriteLine + MessageBox sein, weil
    /// Serilog noch nicht initialisiert ist.
    /// </summary>
    private static void TryApplyPendingRestoreSafe()
    {
        try
        {
            if (!Directory.Exists(AppDataFolder)) return;
            var applied = HBSort.Core.Services.BackupService.TryApplyPendingRestore(AppDataFolder);
            if (applied)
            {
                Debug.WriteLine("[Restore] Pending-Restore beim App-Start angewendet.");
            }
        }
        catch (Exception ex)
        {
            // Beim Pre-DI-Restore ist der Logger noch nicht initialisiert -
            // wir muessen den User direkt informieren via MessageBox.
            Debug.WriteLine($"[Restore] Anwendung des Pending-Restore fehlgeschlagen: {ex}");
            try
            {
                MessageBox.Show(
                    $"Beim Wiederherstellen eines Backups ist ein Fehler aufgetreten:\n\n" +
                    $"{ex.Message}\n\n" +
                    $"Die Backup-Dateien liegen in:\n{Path.Combine(AppDataFolder, ".pending-restore")}\n\n" +
                    "Bitte schliesse die App, kopiere die Dateien dort manuell in den AppData-Ordner " +
                    "und starte neu. Der Pending-Ordner wird beim naechsten Erfolg automatisch geloescht.",
                    "Restore-Fehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* MessageBox kann auch fehlschlagen wenn das Display nicht da ist */ }
        }
    }

    /// <summary>
    /// UX X.29 (v0.1.16): Auto-Backup beim App-Start. Wenn die Setting
    /// aktiviert ist und das letzte Backup laenger als das Intervall her
    /// ist, wird im Hintergrund ein neues Backup erzeugt + alte aufgeraeumt.
    /// Fire-and-forget - Fehler werden nur geloggt, App-Start blockiert nicht.
    /// </summary>
    private static async Task TryAutoBackupAsync()
    {
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            if (!settings.Current.AutoBackup) return;

            var lastBackup = settings.Current.LastBackup;
            var intervalDays = Math.Max(1, settings.Current.AutoBackupIntervalDays);
            if (lastBackup.HasValue
                && (DateTime.UtcNow - lastBackup.Value).TotalDays < intervalDays)
            {
                Log.Debug("Auto-Backup: Intervall noch nicht erreicht, ueberspringen");
                return;
            }

            var backup = Services.GetRequiredService<IBackupService>();
            var result = await backup.CreateBackupAsync();
            settings.Current.LastBackup = result.CreatedAt;
            await settings.SaveAsync();
            Log.Information("Auto-Backup erzeugt: {File} ({Size} Bytes)",
                result.FileName, result.SizeBytes);

            await backup.CleanupOldBackupsAsync(settings.Current.BackupKeepCount);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-Backup fehlgeschlagen");
        }
    }

    /// <summary>
    /// UX X.28 (2026-05-08): Heilt Bug-B-Folgen aus alten Versionen.
    /// Bins die belegt sind (mind. eine TrackedMinifig oder ein FloatingPart)
    /// aber FreedAt!=null haben, werden auf belegt zurueckgesetzt.
    /// Idempotent - mehrfaches Aufrufen ist safe.
    /// </summary>
    private static async Task DataHealAsync()
    {
        try
        {
            var heal = Services.GetRequiredService<IDataHealService>();
            var result = await heal.HealAsync();
            if (result.ResetFreedAtCount > 0)
            {
                Log.Information("DataHeal abgeschlossen: {Count} Bin-FreedAt-Inkonsistenzen korrigiert",
                    result.ResetFreedAtCount);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DataHeal geworfen");
        }
    }

    /// <summary>
    /// Pre-Tag-Fix v0.1.19-beta.7: Backfill-Migration fuer FloatingPart.
    /// BrickognizeCategory. Bestand vor Block-N-Migration A hat die Spalte
    /// auf null - Default-Regel "max 1 PartId pro Kategorie pro Bin" wuerde
    /// alle ungelabelten Eintraege als Pseudo-Kategorie "Unbekannt" werten,
    /// aber ein Backfill macht den Settings-Tab "Kategorien" und
    /// Konflikt-Diagnose praeziser.
    ///
    /// Idempotent: bearbeitet nur Zeilen mit null/empty Kategorie. Heuristik
    /// findet meist eine echte Kategorie ("Minifigure, Head ..." ->
    /// "Minifigure, Head"); Edge-Cases wie "Hips and Legs Plain" bleiben
    /// auf "Unbekannt".
    /// </summary>
    private static async Task BackfillBrickognizeCategoriesAsync()
    {
        try
        {
            using var scope = Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<UserDataContext>();
            var categoryMapping = scope.ServiceProvider.GetRequiredService<ICategoryBinMappingService>();

            // Track-Query damit wir die Spalte direkt aendern koennen.
            var withoutCategory = await ctx.FloatingParts
                .Where(fp => fp.BrickognizeCategory == null || fp.BrickognizeCategory == "")
                .ToListAsync();
            if (withoutCategory.Count == 0) return;

            int derivedCount = 0;
            int unknownCount = 0;
            foreach (var fp in withoutCategory)
            {
                var derived = categoryMapping.DeriveCategoryFromPartName(fp.PartName);
                fp.BrickognizeCategory = derived;
                if (derived == ICategoryBinMappingService.UnknownCategory) unknownCount++;
                else derivedCount++;
            }

            await ctx.SaveChangesAsync();
            Log.Information(
                "BrickognizeCategory-Backfill: {Total} FloatingParts bearbeitet ({Derived} per Heuristik, {Unknown} 'Unbekannt')",
                withoutCategory.Count, derivedCount, unknownCount);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BrickognizeCategory-Backfill geworfen");
        }
    }

    /// <summary>
    /// v0.1.22-beta.1 Fix 2026-05-12: Backfill-Migration fuer
    /// TrackedMinifigPart.PartName. Bestand vor dem Fix hat die Spalte
    /// leer (Block-D-Anlege-Pfad in PartLookupService), wodurch die
    /// DeriveCategoryFromPartName-Heuristik beim Zerlegen "Unbekannt"
    /// liefert und die Default-Regel "max 1 PartId pro Kategorie pro Bin"
    /// wirkungslos wird. Bulk-Lookup ueber IBlCacheRepository.GetItemNamesAsync.
    /// Idempotent: bearbeitet nur Zeilen mit leerem/null PartName.
    /// </summary>
    private static async Task BackfillTrackedMinifigPartNamesAsync()
    {
        try
        {
            using var scope = Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<UserDataContext>();
            var cache = scope.ServiceProvider.GetRequiredService<IBlCacheRepository>();

            // Track-Query damit wir die Spalte direkt aendern koennen.
            var withoutName = await ctx.TrackedMinifigParts
                .Where(p => p.PartName == null || p.PartName == "")
                .ToListAsync();
            if (withoutName.Count == 0) return;

            // Bulk-Lookup ueber alle distinct PartNumbers.
            var partNos = withoutName.Select(p => p.PartNumber).Distinct().ToList();
            var names = await cache.GetItemNamesAsync(partNos);

            int updated = 0;
            foreach (var p in withoutName)
            {
                if (names.TryGetValue(p.PartNumber, out var name)
                    && !string.IsNullOrEmpty(name))
                {
                    p.PartName = name;
                    updated++;
                }
            }

            await ctx.SaveChangesAsync();
            Log.Information(
                "TrackedMinifigPart.PartName-Backfill: {Updated}/{Total} " +
                "Eintraege befuellt (Rest hat keinen bl_items-Eintrag)",
                updated, withoutName.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "TrackedMinifigPart.PartName-Backfill geworfen - " +
                "unkritisch, naechster App-Start versucht es wieder");
        }
    }

    /// <summary>
    /// v0.1.23: Backfill der StorageBin.Kind-Spalte. Wird einmalig beim
    /// App-Start ausgefuehrt (idempotent, mehrfache Aufrufe sind safe).
    /// Klassifiziert alle Bins anhand ihrer aktuellen Inhalte:
    /// - Empty: keine Minifigs, keine FloatingParts
    /// - Floating: nur FloatingParts
    /// - Waiting: mindestens eine wartende Figur (Reifungspfad - mit oder
    ///   ohne komplette Figuren im selben Bin)
    /// - Complete: nur komplette Figuren
    ///
    /// Mix-Bins (Wartend+Floating, Floating+Complete) werden bewusst als
    /// Waiting bzw. Complete klassifiziert; eine separate Warnung dafuer
    /// laeuft ueber WarnAboutBestandMixBinsAsync.
    /// </summary>
    private static async Task BackfillStorageBinKindAsync()
    {
        try
        {
            using var scope = Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<UserDataContext>();

            var bins = await ctx.StorageBins
                .Include(b => b.TrackedMinifigs)
                .Include(b => b.FloatingParts)
                .ToListAsync();

            int updated = 0;
            foreach (var bin in bins)
            {
                var waitingCount = bin.TrackedMinifigs.Count(m => m.Status == TrackedMinifigStatus.Waiting);
                var completeCount = bin.TrackedMinifigs.Count(m => m.Status == TrackedMinifigStatus.Complete);
                var floatingCount = bin.FloatingParts.Count;

                StorageBinKind newKind;
                if (waitingCount == 0 && completeCount == 0 && floatingCount == 0)
                    newKind = StorageBinKind.Empty;
                else if (waitingCount > 0)
                    newKind = StorageBinKind.Waiting;        // Reifungs-zentrisch
                else if (floatingCount > 0)
                    newKind = StorageBinKind.Floating;
                else
                    newKind = StorageBinKind.Complete;

                if (bin.Kind != newKind)
                {
                    bin.Kind = newKind;
                    updated++;
                }
            }

            await ctx.SaveChangesAsync();
            Log.Information(
                "Bin-Typ-Backfill v0.1.23: {Updated}/{Total} Bins aktualisiert",
                updated, bins.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "Bin-Typ-Backfill v0.1.23 geworfen - " +
                "naechster App-Start versucht es wieder");
        }
    }

    /// <summary>
    /// UX X.34 v0.1.20: One-Shot-Cleanup beim App-Start. Loescht
    /// "vergiftete" Cache-Eintraege in bl_prices die durch den Empty-Filter-
    /// Bug entstanden sind (alle Preis-Felder null, total_quantity=0). Ohne
    /// Cleanup wuerde der Lookup diese null-Eintraege als gueltigen Cache-
    /// Hit zurueckgeben und die UI zeigt nie Preise.
    /// Idempotent - mehrfaches Aufrufen ist safe.
    /// </summary>
    private static async Task ClearEmptyPriceCacheEntriesAsync()
    {
        try
        {
            var repo = Services.GetRequiredService<IBlCacheRepository>();
            var cleared = await repo.ClearEmptyPricesAsync();
            if (cleared > 0)
            {
                Log.Information(
                    "BL-Preis-Cache Anti-Vergiftung: {Count} leere Eintraege beim Start aufgeraeumt",
                    cleared);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BL-Preis-Cache Anti-Vergiftung-Cleanup geworfen");
        }
    }

    /// <summary>
    /// v0.1.22-beta.1 Block G: Bestand-Mix-Bins beim App-Start detektieren.
    /// Fire-and-forget; bei Treffer Toast + WRN-Log. Kein Auto-Move - die
    /// User-Spec verlangt manuelle Aufloesung ueber die Lagerliste.
    /// </summary>
    private static async Task WarnAboutBestandMixBinsAsync()
    {
        try
        {
            var binService = Services.GetRequiredService<IStorageBinService>();
            var mixBins = await binService.FindBestandMixBinsAsync();
            if (mixBins.Count == 0) return;

            try
            {
                var notify = Services.GetRequiredService<INotificationService>();
                var labels = string.Join(", ", mixBins.Take(3).Select(m => m.Label));
                var suffix = mixBins.Count > 3 ? $" (+{mixBins.Count - 3} weitere)" : "";
                notify.ShowInfo(
                    $"Es wurden {mixBins.Count} Mix-Lagerfaecher gefunden: {labels}{suffix}. " +
                    "Pruefe die Lagerfaecher in den Einstellungen.");
            }
            catch { /* ignore - Hauptzweck ist das WRN-Log im Service */ }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Bestand-Mix-Bin-Scan geworfen - kein Impact auf Startup");
        }
    }

    /// <summary>
    /// v0.1.20.1: bl_colors-Migration fuer alte v0.1.20-Installs. Wenn der User
    /// schon BrickStore-Daten importiert hat (bl_items > 0) aber bl_colors leer
    /// ist, war das ein Import vor dem colors.xml-Parser-Fix. Wir stossen einen
    /// Reimport an - der ETag-Check spart den Download wenn die ZIP gleich
    /// geblieben ist, die colors.xml wird in jedem Fall neu importiert.
    /// Fire-and-forget; Fehler werden nur geloggt, App-Start laeuft weiter.
    /// </summary>
    private static async Task TryReimportCatalogIfColorsMissingAsync()
    {
        try
        {
            var repo = Services.GetRequiredService<IBlCacheRepository>();
            var stats = await repo.GetStatsAsync();
            var colors = await repo.GetAllColorsAsync();

            if (stats.ItemCount <= 0 || colors.Count > 0) return; // nichts zu tun

            Log.Information(
                "bl_colors leer aber bl_items voll ({ItemCount}) - Stammdaten-Reimport " +
                "wird im Hintergrund ausgeloest (v0.1.20.1-Migration).",
                stats.ItemCount);

            var importer = Services.GetRequiredService<IBlBulkImportService>();
            var settings = Services.GetRequiredService<ISettingsService>();
            var previousEtag = settings.Current.LastBlImportEtag;
            var previousHash = settings.Current.LastBlImportContentHash;

            var result = await importer.ImportFromGitHubAsync(
                previousEtag: previousEtag,
                previousContentHash: previousHash);

            // ETag/Hash zurueckschreiben falls der Server uns ein neues geliefert hat.
            if (!string.IsNullOrEmpty(result.NewEtag))
                settings.Current.LastBlImportEtag = result.NewEtag;
            if (!string.IsNullOrEmpty(result.NewContentHash))
                settings.Current.LastBlImportContentHash = result.NewContentHash;
            settings.Current.LastBlImport = DateTime.UtcNow;
            await settings.SaveAsync();

            // Erfolg-Toast (best-effort - falls Notification noch nicht da, ignorieren).
            try
            {
                var notify = Services.GetRequiredService<INotificationService>();
                notify.ShowInfo("Stammdaten aktualisiert: BL-Farben wurden ergaenzt.");
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "bl_colors-Migration (Reimport-Trigger) geworfen - " +
                "User kann den Catalog-Reimport manuell anstossen ueber Einstellungen.");
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
        Log.Information("=== HB-Sort beendet ===");

        // ServiceProvider disponieren - das ruft Dispose() auf allen Singleton-
        // Services die IDisposable implementieren in der korrekten Reihenfolge.
        // Insbesondere: BlCacheRepository + PersistentImageCache schliessen ihre
        // SQLite-Connections, CameraService stoppt den Capture-Thread.
        (Services as IDisposable)?.Dispose();

        // Serilog-Puffer leeren (damit der letzte Log-Eintrag noch geschrieben wird)
        Log.CloseAndFlush();

        base.OnExit(e);

        // UX X.26 (v0.1.13) Tasks-Geist-Fix:
        // User-Befund: nach App-Beenden bleibt manchmal eine HBSort.exe-Instanz
        // im Hintergrund haengen und blockiert die Webcam beim naechsten Start.
        // Wahrscheinliche Ursachen: OpenCvSharp4-VideoCapture-Thread (non-
        // Background), Velopack-async-Tasks, fire-and-forget HttpClient-Calls.
        //
        // Fallback: nach 2s Wartezeit Process.Kill als ultima ratio. Wir geben
        // dem normalen Shutdown-Pfad (oben) erstmal Zeit, dann zwingen wir die
        // Beendigung. Settings-Saves laufen ueber Window_Closing schon vorher
        // synchron durch - kein Datenverlust durch das Kill.
        Task.Run(async () =>
        {
            await Task.Delay(2000);
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            catch
            {
                // Wenn Kill scheitert (z.B. Berechtigungs-Fehler): nicht weiter
                // tragisch, der Prozess wird irgendwann von Windows aufgeraeumt.
            }
        });
    }
}
