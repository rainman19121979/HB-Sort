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
/// ViewModel für das Hauptfenster.
///
/// Phase 2: Erweiterung um Brickognize-Health-Check und Toast-Container.
/// Der Toast-Container (NotificationService.ActiveToasts) wird hier ueber
/// die Property ActiveToasts an das XAML weitergereicht.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IBrickognizeClient _brickognizeClient;
    private readonly NotificationService _notificationService;
    private readonly IPersistentImageCache _imageCache;
    private readonly IBricklinkRateLimiter _rateLimiter;

    // 60s-Timer fuer Status-Refresh damit die Anzeige aktuell bleibt auch ohne neue Calls
    // (Eintraege fallen aus dem rolling 24h-Window).
    private readonly DispatcherTimer _rateLimitTimer = new()
    {
        Interval = TimeSpan.FromSeconds(60)
    };

    /// <summary>Letzter beobachteter State – fuer Toast-Trigger bei Wechseln.</summary>
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
        HelpViewModel help)
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
        // Wir brauchen die konkrete Implementierung wegen ActiveToasts –
        // das DI registriert beide Wege auf die selbe Singleton-Instanz.
        _notificationService = (NotificationService)notificationService;
        _imageCache = imageCache;
        _rateLimiter = rateLimiter;

        // Letzten Tab-Index (variables Feld unten rechts) aus den Settings laden.
        // Default 0 (= Lagerfaecher). Direkt auf das Backing-Field, damit der
        // OnXxxChanged-Hook nicht direkt beim Laden ein Save triggert.
        var savedTabIndex = settingsService.Current.BottomRightTabIndex ?? 0;
        if (savedTabIndex < 0 || savedTabIndex > 4) savedTabIndex = 0;
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
}
