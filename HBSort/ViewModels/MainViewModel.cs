using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private int _mainTabIndex = 0;

    public bool IsMainTabSorting   => MainTabIndex == 0;
    public bool IsMainTabInventory => MainTabIndex == 1;
    public bool IsMainTabHelp      => MainTabIndex == 2;

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
        IUpdateService updateService)
    {
        _settingsService = settingsService;
        ScanViewModel = scanViewModel;
        Inventory = inventory;
        BuildSuggestions = buildSuggestions;
        LiveStats = liveStats;
        WaitingDetail = waitingDetail;
        RecentScans = recentScans;
        Help = help;
        _brickognizeClient = brickognizeClient;
        // Wir brauchen die konkrete Implementierung wegen ActiveToasts -
        // das DI registriert beide Wege auf die selbe Singleton-Instanz.
        _notificationService = (NotificationService)notificationService;
        _imageCache = imageCache;
        _rateLimiter = rateLimiter;
        _updateService = updateService;

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
    /// Strg+Komma: Einstellungen oeffnen. Wir feuern ein Event, das das
    /// MainWindow auffaengt und den SettingsDialog oeffnet - das VM darf
    /// kein Window direkt instanziieren.
    /// </summary>
    public event EventHandler? OpenSettingsRequested;

    [RelayCommand]
    public void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

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
