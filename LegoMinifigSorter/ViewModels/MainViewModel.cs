using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LegoMinifigSorter.Core.Models.Bricklink;
using LegoMinifigSorter.Core.Services;
using LegoMinifigSorter.Services;
using Serilog;

namespace LegoMinifigSorter.ViewModels;

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

    /// <summary>Wartende-Figuren-Liste fuer den R2,C2 Quadrant (Phase 4).</summary>
    public WaitingMinifigsViewModel WaitingMinifigs { get; }

    /// <summary>Lagerliste-Tab (Phase X).</summary>
    public InventoryListViewModel Inventory { get; }

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
        WaitingMinifigsViewModel waitingMinifigs,
        InventoryListViewModel inventory)
    {
        _settingsService = settingsService;
        ScanViewModel = scanViewModel;
        WaitingMinifigs = waitingMinifigs;
        Inventory = inventory;
        _brickognizeClient = brickognizeClient;
        // Wir brauchen die konkrete Implementierung wegen ActiveToasts –
        // das DI registriert beide Wege auf die selbe Singleton-Instanz.
        _notificationService = (NotificationService)notificationService;
        _imageCache = imageCache;
        _rateLimiter = rateLimiter;

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
    /// F1: oeffnet die Hilfe-Ansicht. Wird in Phase 7 implementiert.
    /// Aktuell Stub mit Info-Toast.
    /// </summary>
    [RelayCommand]
    public void OpenHelp()
    {
        _notificationService.ShowInfo("Hilfe (F1) kommt in Phase 7.");
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
