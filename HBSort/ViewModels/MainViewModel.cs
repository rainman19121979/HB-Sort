using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Helpers;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using HBSort.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel fuer das Hauptfenster.
///
/// Phase 2: Erweiterung um Brickognize-Health-Check und Toast-Container.
/// Der Toast-Container (NotificationService.ActiveToasts) wird hier ueber
/// die Property ActiveToasts an das XAML weitergereicht.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    private readonly ISettingsService _settingsService;
    private readonly IBrickognizeClient _brickognizeClient;
    private readonly NotificationService _notificationService;
    private readonly IPersistentImageCache _imageCache;
    private readonly IBricklinkRateLimiter _rateLimiter;
    private readonly IUpdateService _updateService;
    private readonly IBlBulkImportService _blBulkImport;
    private readonly IUndoService? _undoService;
    private readonly IBackupService? _backupService;

    // 60s-Timer fuer Status-Refresh damit die Anzeige aktuell bleibt auch ohne neue Calls
    // (Eintraege fallen aus dem rolling 24h-Window).
    private readonly DispatcherTimer _rateLimitTimer = new()
    {
        Interval = TimeSpan.FromSeconds(60)
    };

    /// <summary>Letzter beobachteter State - fuer Toast-Trigger bei Wechseln.</summary>
    private RateLimitState _lastRateLimitState = RateLimitState.Ok;

    public ScanViewModel ScanViewModel { get; }

    /// <summary>Lagerliste-Tab (Phase X).</summary>
    public InventoryListViewModel Inventory { get; }

    /// <summary>UX X.29 (v0.1.16): Verlauf-Tab.</summary>
    public HistoryViewModel History { get; }

    /// <summary>v0.1.24-beta.7 Phase 2: BrickLink-Inventar-Tab.</summary>
    public BlInventoryViewModel BlInventory { get; }

    /// <summary>
    /// Hilfe-Tab (UX-Iteration X.9): integrierte Doku als dritter
    /// Haupt-Tab. Wird vom DI als Singleton bereitgestellt damit die
    /// Kapitel-Auswahl beim Tab-Wechsel erhalten bleibt.
    /// </summary>
    public HelpViewModel Help { get; }

    // Variables Feld unten rechts (R2,C2) ist ein TabControl mit 5 Ansichten.
    // Jede VM ist ein Singleton (siehe DI), refresht sich selbst per
    // DataChanged-Event - der TabControl muss kein OnSelectedTab-Refresh triggern.
    public BuildSuggestionsViewModel BuildSuggestions { get; }
    public LiveStatsViewModel LiveStats { get; }
    public WaitingDetailViewModel WaitingDetail { get; }
    public RecentScansViewModel RecentScans { get; }

    /// <summary>
    /// Aktuell gewaehlter Tab-Index im Bottom-Right-TabControl (0..4).
    /// Wird beim Start aus AppSettings.BottomRightTabIndex geladen und bei
    /// jeder User-Auswahl persistiert.
    /// </summary>
    [ObservableProperty]
    private int _bottomRightSelectedTabIndex;

    /// <summary>
    /// Aktuell gewaehlter Haupt-Tab-Index: 0=Sortieren, 1=Lagerliste, 2=Hilfe
    /// (UX-Iteration X.4 + X.9). Wird vom modernisierten Header (RadioButton-
    /// Pivot) gesteuert. Nicht persistiert - beim App-Start beginnen wir immer
    /// auf "Sortieren".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainTabSorting))]
    [NotifyPropertyChangedFor(nameof(IsMainTabInventory))]
    [NotifyPropertyChangedFor(nameof(IsMainTabHelp))]
    [NotifyPropertyChangedFor(nameof(IsMainTabHistory))]
    [NotifyPropertyChangedFor(nameof(IsMainTabBlInventory))]
    private int _mainTabIndex = 0;

    public bool IsMainTabSorting     => MainTabIndex == 0;
    public bool IsMainTabInventory   => MainTabIndex == 1;
    public bool IsMainTabHelp        => MainTabIndex == 2;
    public bool IsMainTabHistory     => MainTabIndex == 3;
    // v0.1.24-beta.7 Phase 2: BrickLink-Inventar-Tab (Index 4, hinter Verlauf).
    public bool IsMainTabBlInventory => MainTabIndex == 4;

    /// <summary>Toast-Liste fuer das XAML-Binding (ItemsControl).</summary>
    public ObservableCollection<ToastItem> ActiveToasts => _notificationService.ActiveToasts;

    [ObservableProperty]
    private string _statusText = "Bereit";

    [ObservableProperty]
    private string _versionText = "v0.1.0";

    /// <summary>
    /// Cache-Status fuer den Status-Balken: "Cache: 247 Bilder, 12.4 MB / 1024 MB".
    /// Wird live aktualisiert ueber das StatsChanged-Event vom PersistentImageCache.
    /// </summary>
    [ObservableProperty]
    private string _cacheStatusText = "Cache: -";

    // --- Phase R2.5: BL-Rate-Limit-Anzeige ---

    [ObservableProperty]
    private string _bricklinkRateLimitText = "BL: -/-";

    [ObservableProperty]
    private string _bricklinkRateLimitTooltip = "(BL-Counter wird beim ersten API-Call aktiv)";

    [ObservableProperty]
    private RateLimitState _bricklinkRateLimitState = RateLimitState.Ok;

    // --- UX-Iteration X.23: Update-Badge im Header ---

    /// <summary>True wenn ein Update verfuegbar ist - steuert die Sichtbarkeit
    /// des Update-Badges im Header.</summary>
    [ObservableProperty]
    private bool _hasUpdateAvailable;

    /// <summary>Versions-String der gefundenen neuen Version (z.B. "0.2.0").</summary>
    [ObservableProperty]
    private string _availableUpdateVersion = string.Empty;

    /// <summary>Wahr waehrend ein Update gerade heruntergeladen wird; deaktiviert
    /// das Badge gegen Doppelklicks.</summary>
    [ObservableProperty]
    private bool _isUpdateDownloading;


    public MainViewModel(
        ISettingsService settingsService,
        ScanViewModel scanViewModel,
        IBrickognizeClient brickognizeClient,
        INotificationService notificationService,
        IPersistentImageCache imageCache,
        IBricklinkRateLimiter rateLimiter,
        InventoryListViewModel inventory,
        BuildSuggestionsViewModel buildSuggestions,
        LiveStatsViewModel liveStats,
        WaitingDetailViewModel waitingDetail,
        RecentScansViewModel recentScans,
        HelpViewModel help,
        HistoryViewModel history,
        IUpdateService updateService,
        IBlBulkImportService blBulkImport,
        IUndoService undoService,
        IBackupService backupService,
        BlInventoryViewModel blInventory)
    {
        _settingsService = settingsService;
        ScanViewModel = scanViewModel;
        Inventory = inventory;
        BuildSuggestions = buildSuggestions;
        LiveStats = liveStats;
        WaitingDetail = waitingDetail;
        RecentScans = recentScans;
        Help = help;
        History = history;
        BlInventory = blInventory;
        _undoService = undoService;
        _backupService = backupService;
        _brickognizeClient = brickognizeClient;
        // Wir brauchen die konkrete Implementierung wegen ActiveToasts -
        // das DI registriert beide Wege auf die selbe Singleton-Instanz.
        _notificationService = (NotificationService)notificationService;
        _imageCache = imageCache;
        _rateLimiter = rateLimiter;
        _updateService = updateService;
        _blBulkImport = blBulkImport;

        // Letzten Tab-Index (variables Feld unten rechts) aus den Settings laden.
        // Default 0 (Live-Stats). Direkt auf das Backing-Field, damit der
        // OnXxxChanged-Hook nicht direkt beim Laden ein Save triggert.
        //
        // Die Tab-Reihe hat heute drei Tabs (UX X.18):
        //   0 = Live-Stats, 1 = Wartende-Detail, 2 = Letzte Scans.
        //
        // Migration aelterer settings.json:
        //   UX X.15 hatte 5 Tabs (0=Lagerfaecher, 1=Was kann ich bauen?, ...)
        //   UX X.16 hatte 4 Tabs (0=Was kann ich bauen?, 1=Live-Stats, ...)
        //   UX X.18 hat  3 Tabs (0=Live-Stats, ...)
        // Wir clampen alte Werte einfach auf den heutigen Bereich [0..2];
        // damit landen "Was kann ich bauen?"-Werte auf Live-Stats - der User
        // verliert nichts inhaltlich, weil die Build-Suggestions jetzt
        // dauerhaft in Spalte 3 oben sichtbar sind.
        var savedTabIndex = settingsService.Current.BottomRightTabIndex ?? 0;
        if (savedTabIndex < 0 || savedTabIndex > 2) savedTabIndex = 0;
        _bottomRightSelectedTabIndex = savedTabIndex;

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
        {
            VersionText = $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        // Image-Cache-Stats: initial laden + auf Aenderungen lauschen.
        UpdateCacheStatusText();
        _imageCache.StatsChanged += OnCacheStatsChanged;

        // BL-Rate-Limit: initial laden + auf jeden API-Call lauschen + alle 60s pollen.
        _ = RefreshRateLimitStatusAsync();
        _rateLimiter.StatusChanged += OnRateLimitChanged;
        _rateLimitTimer.Tick += (_, _) => _ = RefreshRateLimitStatusAsync();
        _rateLimitTimer.Start();

        // UX-Iteration X.23: Update-Check beim App-Start (nur wenn der User
        // ihn aktiviert hat und die App per Setup.exe installiert ist).
        // Fire-and-forget weil die App nicht auf Netzwerk-Antworten warten soll.
        // UX X.26 (v0.1.13): das Header-Update-Badge ist entfernt; HasUpdate-
        // Available steuert jetzt nur noch den "Jetzt updaten"-Button im
        // Settings-Tab.
        if (_settingsService.Current.AutoCheckForUpdates && _updateService.IsInstalled)
        {
            _ = CheckForUpdatesInBackgroundAsync();
        }

        // UX X.28 (v0.1.15): Auto-BL-Import beim Start, falls aktiviert und
        // Intervall ueberschritten. Default ist AutoBlImport=false - User muss
        // explizit aktivieren in Settings -> BrickLink-Daten.
        //
        // UX X.29 (v0.1.16) Bug-Fix: Task.Run-Wrapping ist Pflicht. Der
        // BlBulkImportService entpackt das ZIP synchron (ZipFile.OpenRead +
        // entry.ExtractToFile haben keine async-Variante) und macht
        // CPU-intensive DB-Inserts. Ohne Task.Run laufen diese Block-Phasen
        // auf dem UI-Thread (weil der erste Aufruf vom Konstruktor unter
        // dem UI-SynchronizationContext startet) - resultiert in 10-20s
        // Freeze direkt nach App-Start.
        if (_settingsService.Current.AutoBlImport
            && ShouldRunAutoBlImport(_settingsService.Current))
        {
            Task.Run(async () => await RunAutoBlImportAsync())
                .FireAndForget("AutoBlImport-Startup");
        }
    }

    /// <summary>
    /// UX-Iteration X.23: stiller Update-Check im Hintergrund. Setzt bei
    /// Treffer HasUpdateAvailable + AvailableUpdateVersion. Bei Fehler/
    /// kein Treffer: kein Badge, kein Toast - nur Log.
    /// </summary>
    private async Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            var hasUpdate = await _updateService.CheckForUpdatesAsync();
            _settingsService.Current.LastUpdateCheck = DateTime.UtcNow;
            _ = _settingsService.SaveAsync();

            if (hasUpdate && _updateService.AvailableVersion is { } v)
            {
                AvailableUpdateVersion = v;
                HasUpdateAvailable = true;
                Log.Information("Update verfuegbar: {Version}", v);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Background-Update-Check fehlgeschlagen");
        }
    }

    /// <summary>
    /// UX X.28 (v0.1.15): prueft ob ein Auto-BL-Import faellig ist (faellig =
    /// noch nie ausgefuehrt ODER laenger her als IntervalDays). Public fuer
    /// Tests, ist aber sonst privat in der Logik.
    /// </summary>
    public static bool ShouldRunAutoBlImport(AppSettings s)
    {
        if (!s.AutoBlImport) return false;
        if (s.LastBlImport is null) return true; // noch nie -> sofort
        var daysSince = (DateTime.UtcNow - s.LastBlImport.Value).TotalDays;
        return daysSince >= s.AutoBlImportIntervalDays;
    }

    /// <summary>
    /// UX X.28 (v0.1.15): fuehrt den Auto-BL-Import durch. Toast-Feedback
    /// vorher + nachher, nicht-blockierend (Fire-and-Forget aus dem Konstruktor).
    /// Bei Erfolg: LastBlImport wird auf UtcNow gesetzt + persistiert.
    /// </summary>
    private async Task RunAutoBlImportAsync()
    {
        Log.Information("Auto-BL-Import wird gestartet (Intervall {Days} Tage)",
            _settingsService.Current.AutoBlImportIntervalDays);

        var settings = _settingsService.Current;
        var previousEtag = settings.LastBlImportEtag;
        var previousHash = settings.LastBlImportContentHash;

        try
        {
            // UX X.29 (v0.1.16): ETag + Hash an den Service weitergeben.
            // Service liefert bei 304/gleichem Hash sofort Skipped=true zurueck.
            // Toast-Anzeige nur wenn der Import tatsaechlich Daten zieht -
            // ein "wird gepruefen"-Toast bei jedem Start waere zu invasiv.
            var result = await _blBulkImport.ImportFromGitHubAsync(
                previousEtag: previousEtag,
                previousContentHash: previousHash,
                progress: null,
                ct: default);

            // LastBlImportCheck wird IMMER gesetzt (auch bei Skipped) - User
            // sieht in Settings dass die Pruefung regelmaessig laeuft.
            settings.LastBlImportCheck = DateTime.UtcNow;

            if (result.Skipped)
            {
                Log.Information("Auto-BL-Import uebersprungen: {Reason}", result.SkipReason);
                // Nur ETag/Hash aktualisieren falls neue Werte mitgekommen sind.
                if (!string.IsNullOrEmpty(result.NewEtag))
                    settings.LastBlImportEtag = result.NewEtag;
                if (!string.IsNullOrEmpty(result.NewContentHash))
                    settings.LastBlImportContentHash = result.NewContentHash;
                await _settingsService.SaveAsync();
                // Kein Toast - waere bei jedem App-Start zu nervig.
                return;
            }

            _notificationService.ShowInfo("BrickLink-Daten werden im Hintergrund aktualisiert...");

            settings.LastBlImport = DateTime.UtcNow;
            settings.LastBlImportEtag = result.NewEtag;
            settings.LastBlImportContentHash = result.NewContentHash;
            await _settingsService.SaveAsync();

            _notificationService.ShowSuccess("BrickLink-Daten aktualisiert.");
            Log.Information("Auto-BL-Import erfolgreich abgeschlossen ({Items} Items, {Inv} Subsets)",
                result.ItemsImported, result.InventoriesImported);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-BL-Import fehlgeschlagen");
            _notificationService.ShowError(
                "Auto-Import fehlgeschlagen - bitte manuell in Einstellungen -> BrickLink.");
        }
    }

    /// <summary>
    /// UX-Iteration X.23: Klick auf das Update-Badge. Stoesst den Download
    /// und Restart an. Velopack beendet die App intern und startet sie neu -
    /// nach diesem Aufruf laeuft kein Code mehr.
    /// </summary>
    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (!HasUpdateAvailable || IsUpdateDownloading) return;

        IsUpdateDownloading = true;
        try
        {
            await _updateService.DownloadAndApplyAsync();
            // Wenn wir hier landen, hat Velopack den Restart NICHT angestossen
            // (z.B. weil zwischen Check und Apply was schief ging). Badge zuruecksetzen.
            HasUpdateAvailable = false;
            AvailableUpdateVersion = string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update-Apply fehlgeschlagen");
            _notificationService.ShowError("Update fehlgeschlagen - bitte spaeter erneut versuchen.");
        }
        finally
        {
            IsUpdateDownloading = false;
        }
    }

    /// <summary>
    /// F1: wechselt zum Hilfe-Tab. Seit UX-Iteration X.9 ist die Hilfe ein
    /// eigener Haupt-Tab statt nur ein Toast.
    /// </summary>
    [RelayCommand]
    public void OpenHelp()
    {
        MainTabIndex = 2;
    }

    // UX X.20 Teil 6: zusaetzliche globale Shortcuts.
    // Tab-Wechsel - werden von KeyBindings im MainWindow.xaml getriggert
    // (Strg+S / Strg+L / Strg+H).

    /// <summary>Strg+S: Sortieren-Tab.</summary>
    [RelayCommand] public void SwitchToSortingTab()   => MainTabIndex = 0;

    /// <summary>Strg+L: Lagerliste-Tab.</summary>
    [RelayCommand] public void SwitchToInventoryTab() => MainTabIndex = 1;

    /// <summary>Strg+H: Hilfe-Tab (gleiche Aktion wie OpenHelp/F1, aber
    /// eigener Command-Name fuer den Strg+H-Shortcut).</summary>
    [RelayCommand] public void SwitchToHelpTab()      => MainTabIndex = 2;

    /// <summary>
    /// UX X.29 Block F (v0.1.16): Strg+B - sofortiges manuelles Backup ueber
    /// den globalen Hotkey. Toast bei Erfolg/Fehler. Settings-Tab "Backups"
    /// hat einen eigenen Button mit Status-Anzeige; dieser hier ist die
    /// Quick-Aktion ohne Tab-Wechsel.
    /// </summary>
    [RelayCommand]
    public async Task CreateBackupNowAsync()
    {
        if (_backupService == null) return;
        try
        {
            var result = await _backupService.CreateBackupAsync();
            _settingsService.Current.LastBackup = result.CreatedAt;
            await _settingsService.SaveAsync();
            await _backupService.CleanupOldBackupsAsync(_settingsService.Current.BackupKeepCount);
            _notificationService.ShowSuccess(
                $"Backup erstellt: {result.FileName} ({FormatBytes(result.SizeBytes)})");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Backup via Strg+B fehlgeschlagen");
            _notificationService.ShowError("Backup fehlgeschlagen: " + ex.Message);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("F2") + " MB";
    }

    /// <summary>UX X.29 (v0.1.16): Verlauf-Tab.</summary>
    [RelayCommand]
    public void SwitchToHistoryTab()
    {
        MainTabIndex = 3;
        // Beim Tab-Wechsel die Liste frisch laden (analog Inventory).
        if (History is HistoryViewModel hvm)
            _ = hvm.RefreshAsync();
    }

    /// <summary>
    /// UX X.29 (v0.1.16): Strg+Z - macht die letzte rueckgaengigmachbare Aktion
    /// rueckgaengig. Bei leerer Historie: Toast-Hinweis.
    /// </summary>
    [RelayCommand]
    public async Task UndoLastAsync()
    {
        if (_undoService == null) return;
        try
        {
            var last = await _undoService.GetLastUndoableAsync();
            if (last == null)
            {
                _notificationService.ShowInfo("Keine rueckgaengigmachbare Aktion vorhanden.");
                return;
            }

            var result = await _undoService.UndoAsync(last.Id);
            if (result.Success)
            {
                _notificationService.ShowSuccess($"Rueckgaengig: {last.Description}");
                // Verlauf-Liste live aktualisieren falls sichtbar.
                if (History is HistoryViewModel hvm) _ = hvm.RefreshAsync();
            }
            else
            {
                _notificationService.ShowError(
                    "Rueckgaengig fehlgeschlagen: " + (result.ErrorMessage ?? "unbekannter Fehler"));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Undo (Strg+Z) fehlgeschlagen");
            _notificationService.ShowError("Rueckgaengig fehlgeschlagen.");
        }
    }

    /// <summary>
    /// Strg+Komma: Einstellungen oeffnen. Wir feuern ein Event, das das
    /// MainWindow auffaengt und den SettingsDialog oeffnet - das VM darf
    /// kein Window direkt instanziieren.
    ///
    /// v0.1.22-beta.3 (2026-05-13): EventArgs erweitert um InitialTab.
    /// Aufrufer (z.B. Volle-Faecher-Banner-Button) koennen so direkt auf
    /// den Lagerfaecher-Tab springen statt auf dem Default zu landen.
    /// </summary>
    public event EventHandler<OpenSettingsEventArgs>? OpenSettingsRequested;

    [RelayCommand]
    public void OpenSettings() => OpenSettingsRequested?.Invoke(this, OpenSettingsEventArgs.Default);

    /// <summary>v0.1.22-beta.3: Settings mit vorausgewaehltem Tab oeffnen.</summary>
    public void OpenSettingsOnTab(HBSort.Views.SettingsTab tab)
        => OpenSettingsRequested?.Invoke(this, new OpenSettingsEventArgs { InitialTab = tab });

    /// <summary>
    /// UX-Iteration X.21 Teil 3: Strg+Q = App beenden. Wir feuern ein Event,
    /// das das MainWindow auffaengt und den ExitApplicationAsync-Pfad ausfuehrt.
    /// Analog zu OpenSettings: das VM darf das Window nicht direkt schliessen.
    /// </summary>
    public event EventHandler? ExitAppRequested;

    [RelayCommand]
    public void ExitApp() => ExitAppRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Liest den aktuellen RateLimitStatus und aktualisiert die UI-Properties.
    /// Wird bei jedem API-Call (via Event) und alle 60s (via Timer) aufgerufen.
    /// </summary>
    public async Task RefreshRateLimitStatusAsync()
    {
        try
        {
            var status = await _rateLimiter.GetStatusAsync();
            ApplyRateLimitStatus(status);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BL Rate-Limit Refresh geworfen");
        }
    }

    private void OnRateLimitChanged(object? sender, RateLimitStatus status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyRateLimitStatus(status));
        }
        else
        {
            ApplyRateLimitStatus(status);
        }
    }

    /// <summary>
    /// Setzt die Anzeige-Properties + triggert Toasts bei State-Wechsel.
    /// Toasts kommen NUR beim Wechsel (Ok->Warning, Warning->Blocked etc.) damit
    /// nicht jeder einzelne Call einen neuen Toast erzeugt.
    /// </summary>
    private void ApplyRateLimitStatus(RateLimitStatus status)
    {
        BricklinkRateLimitState = status.State;

        // Anzeigetext mit Symbol je nach State
        var symbol = status.State switch
        {
            RateLimitState.Warning  => " ⚠",
            RateLimitState.Critical => " ⚠",
            RateLimitState.Blocked  => " ⛔",
            _                        => string.Empty
        };
        var threshold = status.State == RateLimitState.Blocked
            ? status.HardThreshold
            : status.SoftThreshold;
        BricklinkRateLimitText = $"BL: {status.CallsLast24h}/{threshold} (24h){symbol}";

        // Tooltip mit allen Details
        var oldestText = status.OldestCallIn24h.HasValue
            ? status.OldestCallIn24h.Value.ToLocalTime().ToString("dd.MM. HH:mm")
            : "(keiner)";
        BricklinkRateLimitTooltip =
            $"Letzte 24h: {status.CallsLast24h} / {status.SoftThreshold} (Soft) / {status.HardThreshold} (Hard)\n" +
            $"Heute: {status.CallsToday}\n" +
            $"Letzte Stunde: {status.CallsThisHour}\n" +
            $"Aelteste Call im Window: {oldestText}\n" +
            $"BL-Limit (fix): {status.BlRealLimit}";

        // Toast bei State-Wechsel (Aufstieg in eine schlechtere Stufe)
        if (status.State != _lastRateLimitState && IsHigherSeverity(status.State, _lastRateLimitState))
        {
            switch (status.State)
            {
                case RateLimitState.Warning:
                    _notificationService.ShowWarning(
                        $"BL-API: Soft-Limit erreicht ({status.CallsLast24h} / {status.SoftThreshold} Calls in 24h)");
                    break;
                case RateLimitState.Critical:
                    _notificationService.ShowWarning(
                        $"BL-API: Critical ({status.CallsLast24h} Calls in 24h, BL-Limit {status.BlRealLimit})");
                    break;
                case RateLimitState.Blocked:
                    var hours = status.OldestCallIn24h.HasValue
                        ? Math.Max(0, 24 - (DateTime.UtcNow - status.OldestCallIn24h.Value).TotalHours)
                        : 24;
                    _notificationService.ShowError(
                        $"BL-API: Hard-Limit erreicht. Nur noch Cache-Lookups bis Reset (~{hours:0}h).");
                    break;
            }
        }
        _lastRateLimitState = status.State;
    }

    /// <summary>True wenn newState eine schlimmere Stufe ist als oldState.</summary>
    private static bool IsHigherSeverity(RateLimitState newState, RateLimitState oldState)
        => (int)newState > (int)oldState;

    private void OnCacheStatsChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(UpdateCacheStatusText);
        }
        else
        {
            UpdateCacheStatusText();
        }
    }

    private void UpdateCacheStatusText()
    {
        var stats = _imageCache.GetStats();
        var used = FormatMb(stats.TotalSizeBytes);
        var limit = stats.LimitBytes <= 0 ? "unbegrenzt" : FormatMb(stats.LimitBytes);
        CacheStatusText = $"Cache: {stats.FileCount} Bilder, {used} / {limit}";
    }

    private static string FormatMb(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        double mb = bytes / 1024.0 / 1024.0;
        if (mb >= 1024) return $"{mb / 1024:0.#} GB";
        return $"{mb:0.#} MB";
    }

    /// <summary>
    /// Bei Tab-Wechsel den neuen Index in AppSettings.BottomRightTabIndex
    /// persistieren. Fire-and-forget: Settings-Save ist async, aber der User
    /// wartet nicht darauf.
    /// </summary>
    partial void OnBottomRightSelectedTabIndexChanged(int value)
    {
        try
        {
            _settingsService.Current.BottomRightTabIndex = value;
            _ = _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Konnte BottomRightTabIndex nicht persistieren");
        }
    }

    /// <summary>
    /// Pingt /health/ einmal beim App-Start und aktualisiert die Status-Anzeige.
    /// Wird vom MainWindow.Loaded fire-and-forget aufgerufen.
    /// </summary>
    public async Task RunBrickognizeHealthCheckAsync()
    {
        try
        {
            var health = await _brickognizeClient.CheckHealthAsync();
            ScanViewModel.UpdateBrickognizeStatus(health);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Brickognize-Health-Check warf eine Exception");
            ScanViewModel.UpdateBrickognizeStatus(new BrickognizeHealth
            {
                Status = BrickognizeStatus.Offline,
                ErrorMessage = ex.Message
            });
        }
    }

    /// <summary>
    /// UX X.28 (v0.1.15): IDisposable damit der ServiceProvider beim OnExit
    /// den DispatcherTimer stoppt und Event-Subscriptions abmeldet. Vorher
    /// liefen die bis zum Process.Kill weiter.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _rateLimitTimer.Stop(); } catch { /* defensiv */ }
        try { _rateLimiter.StatusChanged -= OnRateLimitChanged; } catch { /* defensiv */ }
        try { _imageCache.StatsChanged -= OnCacheStatsChanged; } catch { /* defensiv */ }

        Log.Information("MainViewModel disposed");
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// v0.1.22-beta.3 (2026-05-13): EventArgs fuer OpenSettingsRequested
/// damit Banner-Buttons & Co. einen Tab-Hint mitliefern koennen.
/// </summary>
public class OpenSettingsEventArgs : EventArgs
{
    public HBSort.Views.SettingsTab InitialTab { get; init; } = HBSort.Views.SettingsTab.Default;
    public static readonly OpenSettingsEventArgs Default = new();
}
