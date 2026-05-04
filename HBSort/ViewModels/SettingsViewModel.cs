using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel fuer den Einstellungen-Dialog.
/// Zeigt die aktuellen Settings an und laesst den User sie aendern.
/// Aenderungen werden erst beim Klick auf "Speichern" uebernommen.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ICameraService _cameraService;
    private readonly IPersistentImageCache _imageCache;
    private readonly IBricklinkTokenStorage _bricklinkTokenStorage;
    private readonly IBricklinkClient _bricklinkClient;
    private readonly IBlCatalogService _blCatalogService;
    private readonly IBricklinkRateLimiter _rateLimiter;
    private readonly HttpClient _http;
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IDialogService _dialogs;
    private readonly IBlPriceCacheService _priceCache;
    private readonly ITooltipsService _tooltips;

    /// <summary>Tab "Lagerfaecher" - eigenes ViewModel mit Liste + Commands.</summary>
    public BinManagerViewModel BinManager { get; }

    // --- Kamera ---

    /// <summary>Liste der verfuegbaren Kameras</summary>
    [ObservableProperty]
    private List<string> _availableCameras = [];

    /// <summary>Aktuell ausgewaehlte Kamera</summary>
    [ObservableProperty]
    private int _selectedCameraIndex;

    // --- Schwellwerte ---

    [ObservableProperty]
    private double _scoreThresholdAuto;

    [ObservableProperty]
    private double _scoreThresholdMin;

    [ObservableProperty]
    private double _scoreThresholdShowSelection;

    // --- Timing ---

    [ObservableProperty]
    private int _scanCooldownMs;

    [ObservableProperty]
    private int _freezeFrameMs;

    // --- Sonstiges ---

    [ObservableProperty]
    private bool _soundEnabled;

    /// <summary>UX-Iteration X.9: globaler Tooltips-Schalter.</summary>
    [ObservableProperty]
    private bool _showTooltips;

    /// <summary>
    /// Live-Reaktion auf den Toggle: TooltipsService updated die
    /// Application-Resource sofort - der User sieht die Aenderung beim
    /// naechsten Hover, ohne Neustart und ohne "Speichern". Persistierung
    /// in settings.json passiert erst beim Save-Command.
    /// </summary>
    partial void OnShowTooltipsChanged(bool value)
    {
        // _tooltips ist null wenn Property aus dem ObservableProperty-Init
        // _vor_ dem Konstruktor-Body lebendig wird (Default-Wert false).
        // Eigentlich nicht der Fall hier, aber defensive Pruefung schadet nichts.
        _tooltips?.SetEnabled(value);
    }

    /// <summary>BrickLink-Bilder bevorzugen statt Brickognize-Graustufen-Renderings.</summary>
    [ObservableProperty]
    private bool _preferBricklinkImages;

    /// <summary>Vorab-Cache der Teile-Bilder beim Minifig-Scan.</summary>
    [ObservableProperty]
    private bool _preloadOnMinifigScan;

    /// <summary>
    /// Cache-Limit in MB. 0 = unbegrenzt.
    /// Wird per Radio-Button (100/1024/5120/0) ODER per Custom-Eingabe gesetzt.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLimit100Mb))]
    [NotifyPropertyChangedFor(nameof(IsLimit1Gb))]
    [NotifyPropertyChangedFor(nameof(IsLimit5Gb))]
    [NotifyPropertyChangedFor(nameof(IsLimitUnlimited))]
    private int _imageCacheLimitMb;

    public bool IsLimit100Mb       => ImageCacheLimitMb == 100;
    public bool IsLimit1Gb         => ImageCacheLimitMb == 1024;
    public bool IsLimit5Gb         => ImageCacheLimitMb == 5120;
    public bool IsLimitUnlimited   => ImageCacheLimitMb == 0;

    /// <summary>Eingabe-Wert fuer Custom-Limit-Textbox (separat vom aktiven Limit).</summary>
    [ObservableProperty]
    private string _customLimitInput = string.Empty;

    // --- Cache-Stats fuer die Anzeige ---

    [ObservableProperty]
    private int _cacheFileCount;

    [ObservableProperty]
    private string _cacheUsageText = string.Empty;

    [ObservableProperty]
    private string _cacheLimitText = string.Empty;

    [ObservableProperty]
    private string _priceToolUrl = string.Empty;

    // === Phase R1: BrickLink-API ===

    [ObservableProperty]
    private string _bricklinkConsumerKey = string.Empty;

    [ObservableProperty]
    private string _bricklinkConsumerSecret = string.Empty;

    [ObservableProperty]
    private string _bricklinkTokenValue = string.Empty;

    [ObservableProperty]
    private string _bricklinkTokenSecret = string.Empty;

    /// <summary>Status-Text fuer den BL-Tab (zeigt z.B. "Tokens gespeichert ✓" oder Fehler).</summary>
    [ObservableProperty]
    private string _bricklinkStatusText = "Noch keine Tokens hinterlegt.";

    /// <summary>Test-Resultat-Text (wird nach Klick auf "Verbindung testen" gesetzt).</summary>
    [ObservableProperty]
    private string _bricklinkTestResultText = string.Empty;

    /// <summary>Anzeige der externen IP (fuer den IP-Whitelist-Hinweis).</summary>
    [ObservableProperty]
    private string _bricklinkExternalIp = string.Empty;

    // --- BL-Cache-Statistik (Phase R2) ---

    [ObservableProperty]
    private string _blCacheStatsText = "Cache-Statistik wird geladen...";

    // --- Phase 5.5: BrickStore-Bulk-Import ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotImporting))]
    private bool _isImporting;

    public bool IsNotImporting => !IsImporting;

    [ObservableProperty] private double _importProgress;
    [ObservableProperty] private string _importStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportResult))]
    private string _importResultText = string.Empty;

    [ObservableProperty] private string _localImportFolder = string.Empty;
    [ObservableProperty] private int _blCacheItemsCount;
    [ObservableProperty] private int _blCacheSubsetsCount;
    [ObservableProperty] private string _blCacheSizeLabel = string.Empty;

    public bool HasImportResult => !string.IsNullOrEmpty(ImportResultText);

    /// <summary>Laed Stats fuer den BrickStore-Import-Tab (Items/Subsets/Groesse).</summary>
    public async Task RefreshBrickStoreStatsAsync()
    {
        try
        {
            var stats = await _blCatalogService.GetCacheStatsAsync();
            BlCacheItemsCount = stats.ItemCount;
            BlCacheSubsetsCount = stats.SubsetCount;
            BlCacheSizeLabel = $"{stats.DbFileSizeBytes / 1024.0 / 1024.0:F1} MB";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RefreshBrickStoreStats geworfen");
        }
    }

    // --- BL-Rate-Limit (Phase R2.5) ---

    [ObservableProperty]
    private int _bricklinkSoftThreshold;

    [ObservableProperty]
    private int _bricklinkHardThreshold;

    /// <summary>BL-Konstanten zur Anzeige (5000 fix).</summary>
    public int BricklinkRealLimit => Core.Models.BricklinkConstants.BlRealLimit;
    public int BricklinkMaxAllowedHard => Core.Models.BricklinkConstants.MaxAllowedHardThreshold;

    [ObservableProperty]
    private string _rateLimitCalls24h = "-";

    [ObservableProperty]
    private string _rateLimitCallsToday = "-";

    [ObservableProperty]
    private string _rateLimitCallsThisHour = "-";

    [ObservableProperty]
    private string _rateLimitStateText = "-";

    [ObservableProperty]
    private string _rateLimitOldestCallText = "-";

    [ObservableProperty]
    private string _rateLimitSaveMessage = string.Empty;

    public SettingsViewModel(
        ISettingsService settingsService,
        ICameraService cameraService,
        IPersistentImageCache imageCache,
        IBricklinkTokenStorage bricklinkTokenStorage,
        IBricklinkClient bricklinkClient,
        IBlCatalogService blCatalogService,
        IBricklinkRateLimiter rateLimiter,
        HttpClient http,
        IDbContextFactory<UserDataContext> ctxFactory,
        BinManagerViewModel binManager,
        IDialogService dialogs,
        IBlPriceCacheService priceCache,
        ITooltipsService tooltips)
    {
        _settingsService = settingsService;
        _cameraService = cameraService;
        _imageCache = imageCache;
        _bricklinkTokenStorage = bricklinkTokenStorage;
        _bricklinkClient = bricklinkClient;
        _blCatalogService = blCatalogService;
        _rateLimiter = rateLimiter;
        _http = http;
        _ctxFactory = ctxFactory;
        _dialogs = dialogs;
        _tooltips = tooltips;
        _priceCache = priceCache;
        BinManager = binManager;

        // Vorhandene BL-Tokens beim Oeffnen der Settings laden, damit der User
        // sie sehen / aendern kann ohne neu eingeben zu muessen.
        _ = LoadBricklinkTokensAsync();
        _ = RefreshBlCacheStatsAsync();
        _ = RefreshBrickStoreStatsAsync();

        // Rate-Limit-Schwellen aus Settings + aktuellen Counter
        BricklinkSoftThreshold = settingsService.Current.Bricklink.SoftThreshold;
        BricklinkHardThreshold = settingsService.Current.Bricklink.HardThreshold;
        _ = RefreshRateLimitStatusAsync();

        // Aktuelle Werte aus den Settings laden
        LoadFromSettings();

        // Cache-Stats initial befuellen
        RefreshCacheStats();

        // UX#12: Preis-Cache-Eintraege initial laden.
        _ = RefreshPriceCacheCountAsync();
    }

    /// <summary>UX#12: Eintragsanzahl aus dem Preis-Cache holen und in der UI anzeigen.</summary>
    public async Task RefreshPriceCacheCountAsync()
    {
        try
        {
            PriceCacheEntryCount = await _priceCache.GetEntryCountAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Preis-Cache-Count-Refresh geworfen");
        }
    }

    /// <summary>UX#12: kompletten Preis-Cache leeren (mit Bestaetigung).</summary>
    [RelayCommand]
    public async Task ClearPriceCacheAsync()
    {
        var ok = await _dialogs.ShowQuestionAsync(
            "Preis-Cache leeren",
            "Wirklich alle gespeicherten BL-Preise loeschen? Naechste Lookups gehen " +
            "live in die BL-API (kann Rate-Limit kosten).");
        if (!ok) return;

        var deleted = await _priceCache.ClearAllAsync();
        await RefreshPriceCacheCountAsync();
        Log.Information("Preis-Cache geleert: {Count} Eintraege entfernt", deleted);
    }

    private void LoadFromSettings()
    {
        var s = _settingsService.Current;

        SelectedCameraIndex = s.SelectedCameraIndex;
        ScoreThresholdAuto = s.ScoreThresholdAuto;
        ScoreThresholdMin = s.ScoreThresholdMin;
        ScoreThresholdShowSelection = s.ScoreThresholdShowSelection;
        ScanCooldownMs = s.ScanCooldownMs;
        FreezeFrameMs = s.FreezeFrameMs;
        SoundEnabled = s.SoundEnabled;
        ShowTooltips = s.ShowTooltips;
        PreferBricklinkImages = s.ImageCache.PreferBricklinkImages;
        PreloadOnMinifigScan = s.ImageCache.PreloadOnMinifigScan;
        ImageCacheLimitMb = s.ImageCache.LimitMb;
        PriceToolUrl = s.PriceToolUrl;
        BsxExportFolder = s.BsxExportFolder ?? string.Empty;

        // Phase 8: Preise-Tab
        PriceProvider                  = string.IsNullOrWhiteSpace(s.Prices.Provider) ? "None" : s.Prices.Provider;
        PriceGuideType                 = s.Prices.GuideType;
        PricePriceColumn               = s.Prices.PriceColumn;
        PriceRegion                    = s.Prices.Region;
        PriceCountryCode               = s.Prices.CountryCode;
        PriceCurrency                  = s.Prices.Currency;
        PriceCorrectionMinifigPercent  = s.Prices.CorrectionMinifigPercent;
        PriceCorrectionPartsPercent    = s.Prices.CorrectionPartsPercent;
        // Audit W-8: PriceCacheDays-Feld wurde entfernt - die TTL kommt jetzt
        // aus den zwei dedizierten Minifig/Part-Feldern.
        PriceCacheTtlMinifigDays       = s.Prices.BlPriceCacheTtlMinifigDays;
        PriceCacheTtlPartDays          = s.Prices.BlPriceCacheTtlPartDays;
        PriceAutoLoadOnComplete        = s.Prices.AutoLoadOnComplete;
        PriceAutoLoadCompletePrice     = s.Prices.AutoLoadCompletePrice;
        PriceAutoLoadPartsPrice        = s.Prices.AutoLoadPartsPrice;

        // Kameras auflisten
        AvailableCameras = _cameraService.GetAvailableCameras();
    }

    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        var s = _settingsService.Current;

        s.SelectedCameraIndex = SelectedCameraIndex;
        s.ScoreThresholdAuto = ScoreThresholdAuto;
        s.ScoreThresholdMin = ScoreThresholdMin;
        s.ScoreThresholdShowSelection = ScoreThresholdShowSelection;
        s.ScanCooldownMs = ScanCooldownMs;
        s.FreezeFrameMs = FreezeFrameMs;
        s.SoundEnabled = SoundEnabled;
        s.ShowTooltips = ShowTooltips;
        s.ImageCache.PreferBricklinkImages = PreferBricklinkImages;
        s.ImageCache.PreloadOnMinifigScan = PreloadOnMinifigScan;
        s.ImageCache.LimitMb = ImageCacheLimitMb;
        s.BsxExportFolder = string.IsNullOrWhiteSpace(BsxExportFolder) ? null : BsxExportFolder.Trim();

        // Phase 8: Preise
        s.Prices.Provider                  = PriceProvider;
        s.Prices.GuideType                 = PriceGuideType;
        s.Prices.PriceColumn               = PricePriceColumn;
        s.Prices.Region                    = PriceRegion ?? string.Empty;
        s.Prices.CountryCode               = PriceCountryCode ?? string.Empty;
        s.Prices.Currency                  = string.IsNullOrWhiteSpace(PriceCurrency) ? "EUR" : PriceCurrency;
        s.Prices.CorrectionMinifigPercent  = PriceCorrectionMinifigPercent;
        s.Prices.CorrectionPartsPercent    = PriceCorrectionPartsPercent;
        // Audit W-8: kein CacheDays-Schreiben mehr - Property entfernt.
        s.Prices.BlPriceCacheTtlMinifigDays = Math.Max(1, PriceCacheTtlMinifigDays);
        s.Prices.BlPriceCacheTtlPartDays    = Math.Max(1, PriceCacheTtlPartDays);
        s.Prices.AutoLoadOnComplete        = PriceAutoLoadOnComplete; // DEPRECATED-Schreiben weiter, fuer Backwards-Compat
        s.Prices.AutoLoadCompletePrice     = PriceAutoLoadCompletePrice;
        s.Prices.AutoLoadPartsPrice        = PriceAutoLoadPartsPrice;

        await _settingsService.SaveAsync();
        Log.Information("Einstellungen gespeichert");
    }

    // ========================================================================
    // Bild-Cache (Phase 2.5)
    // ========================================================================

    /// <summary>Liest Stats vom PersistentImageCache und aktualisiert die UI.</summary>
    public void RefreshCacheStats()
    {
        var stats = _imageCache.GetStats();
        CacheFileCount = stats.FileCount;
        CacheUsageText = $"{stats.FileCount} Bilder, {FormatMb(stats.TotalSizeBytes)}";
        CacheLimitText = stats.LimitBytes <= 0
            ? "unbegrenzt"
            : FormatMb(stats.LimitBytes);
    }

    /// <summary>Setzt das Cache-Limit auf einen festen Wert (Radio-Button-Aktion).</summary>
    [RelayCommand]
    public void SetCacheLimit(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) && mb >= 0)
        {
            ImageCacheLimitMb = mb;
            RefreshCacheStats();
        }
    }

    /// <summary>Uebernimmt den Wert aus dem Custom-Eingabefeld.</summary>
    [RelayCommand]
    public async Task ApplyCustomLimit()
    {
        if (int.TryParse(CustomLimitInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) && mb > 0)
        {
            ImageCacheLimitMb = mb;
            RefreshCacheStats();
        }
        else
        {
            await _dialogs.ShowInfoAsync("Ungueltige Eingabe",
                "Bitte eine positive Ganzzahl in Megabyte eingeben.");
        }
    }

    /// <summary>Loescht den kompletten Bild-Cache (mit Bestaetigungs-Dialog).</summary>
    [RelayCommand]
    public async Task ClearCacheAsync()
    {
        var stats = _imageCache.GetStats();
        var msg = $"{stats.FileCount} Bilder und {FormatMb(stats.TotalSizeBytes)} werden geloescht. Fortfahren?";
        // Destruktive Aktion -> "Ja" / "Nein"
        if (!await _dialogs.ShowQuestionAsync("Cache leeren", msg)) return;

        var deleted = await _imageCache.ClearAsync();
        Log.Information("Bild-Cache manuell geleert: {Count} Dateien", deleted);
        RefreshCacheStats();
    }

    // ========================================================================
    // BrickLink-Tokens (Phase R1)
    // ========================================================================

    /// <summary>Laed die gespeicherten BL-Tokens in die UI-Felder.</summary>
    private async Task LoadBricklinkTokensAsync()
    {
        try
        {
            if (_bricklinkTokenStorage.HasTokens())
            {
                var tokens = await _bricklinkTokenStorage.LoadAsync();
                if (tokens != null)
                {
                    BricklinkConsumerKey = tokens.ConsumerKey;
                    BricklinkConsumerSecret = tokens.ConsumerSecret;
                    BricklinkTokenValue = tokens.TokenValue;
                    BricklinkTokenSecret = tokens.TokenSecret;
                    BricklinkStatusText = "Tokens geladen ✓";
                }
                else
                {
                    BricklinkStatusText = "Tokens-Eintrag vorhanden, aber leer.";
                }
            }
            else
            {
                BricklinkStatusText = "Noch keine Tokens hinterlegt.";
            }
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // settings.json wurde von anderem Windows-User uebernommen
            Log.Warning(ex, "BL-Tokens konnten nicht entschluesselt werden");
            BricklinkStatusText = "Tokens nicht entschluesselbar - bitte neu eingeben.";
        }
    }

    /// <summary>Speichert die aktuellen UI-Tokens via TokenStorage (DPAPI).</summary>
    [RelayCommand]
    public async Task SaveBricklinkTokensAsync()
    {
        var tokens = new BricklinkTokens
        {
            ConsumerKey = BricklinkConsumerKey?.Trim() ?? string.Empty,
            ConsumerSecret = BricklinkConsumerSecret?.Trim() ?? string.Empty,
            TokenValue = BricklinkTokenValue?.Trim() ?? string.Empty,
            TokenSecret = BricklinkTokenSecret?.Trim() ?? string.Empty
        };

        if (!tokens.IsComplete)
        {
            BricklinkStatusText = "Bitte alle vier Felder ausfuellen.";
            return;
        }

        try
        {
            await _bricklinkTokenStorage.SaveAsync(tokens);
            BricklinkStatusText = "Tokens gespeichert ✓";
            BricklinkTestResultText = string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BL-Tokens speichern fehlgeschlagen");
            BricklinkStatusText = $"Fehler beim Speichern: {ex.Message}";
        }
    }

    /// <summary>Loescht die gespeicherten Tokens nach Bestaetigungs-Dialog.</summary>
    [RelayCommand]
    public async Task ClearBricklinkTokensAsync()
    {
        // Destruktive Aktion -> "Ja" / "Nein"
        if (!await _dialogs.ShowQuestionAsync("Tokens loeschen",
            "BrickLink-Tokens wirklich loeschen? Du musst sie spaeter neu eingeben."))
            return;

        await _bricklinkTokenStorage.ClearAsync();
        BricklinkConsumerKey = string.Empty;
        BricklinkConsumerSecret = string.Empty;
        BricklinkTokenValue = string.Empty;
        BricklinkTokenSecret = string.Empty;
        BricklinkStatusText = "Tokens geloescht.";
        BricklinkTestResultText = string.Empty;
    }

    /// <summary>Test-Call gegen die BL-API. Setzt BricklinkTestResultText.</summary>
    [RelayCommand]
    public async Task TestBricklinkConnectionAsync()
    {
        BricklinkTestResultText = "Teste...";
        try
        {
            var result = await _bricklinkClient.TestConnectionAsync();
            if (result.Success)
            {
                BricklinkTestResultText = $"OK - '{result.ItemName}' erkannt ({result.ResponseTimeMs} ms)";
            }
            else
            {
                BricklinkTestResultText = $"Fehler: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BL TestConnection geworfen");
            BricklinkTestResultText = $"Fehler: {ex.Message}";
        }
    }

    /// <summary>
    /// Laedt die Cache-Stats aus dem BlCatalogService und formatiert sie fuer die UI.
    /// </summary>
    public async Task RefreshBlCacheStatsAsync()
    {
        try
        {
            var stats = await _blCatalogService.GetCacheStatsAsync();
            var sizeMb = stats.DbFileSizeBytes / 1024.0 / 1024.0;
            var oldestText = stats.OldestFetchedAt.HasValue
                ? $"vor {(int)(DateTime.UtcNow - stats.OldestFetchedAt.Value).TotalDays} Tagen"
                : "(keine Eintraege)";

            BlCacheStatsText =
                $"Items: {stats.ItemCount:N0} | Subsets: {stats.SubsetCount:N0} | " +
                $"Colors: {stats.ColorCount:N0} | DB: {sizeMb:0.#} MB | aelteste: {oldestText}";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BL-Cache-Stats laden fehlgeschlagen");
            BlCacheStatsText = "Stats nicht verfuegbar.";
        }
    }

    /// <summary>Loescht stale Eintraege im BL-Cache (>cacheStaleDays Tage).</summary>
    [RelayCommand]
    public async Task ClearStaleBlCacheAsync()
    {
        try
        {
            var deleted = await _blCatalogService.ClearStaleAsync();
            await RefreshBlCacheStatsAsync();
            BricklinkTestResultText = $"{deleted} stale Eintraege geloescht.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BL-Cache stale Cleanup fehlgeschlagen");
            BricklinkTestResultText = $"Fehler: {ex.Message}";
        }
    }

    /// <summary>Liest aktuellen Rate-Limit-Status und befuellt die UI-Properties.</summary>
    public async Task RefreshRateLimitStatusAsync()
    {
        try
        {
            var s = await _rateLimiter.GetStatusAsync();
            RateLimitCalls24h = s.CallsLast24h.ToString();
            RateLimitCallsToday = s.CallsToday.ToString();
            RateLimitCallsThisHour = s.CallsThisHour.ToString();
            RateLimitStateText = s.State.ToString();

            if (s.OldestCallIn24h.HasValue)
            {
                var ageHrs = (DateTime.UtcNow - s.OldestCallIn24h.Value).TotalHours;
                var resetInHrs = Math.Max(0, 24 - ageHrs);
                RateLimitOldestCallText =
                    $"vor {ageHrs:0.#}h (resettet in ca. {resetInHrs:0.#}h)";
            }
            else
            {
                RateLimitOldestCallText = "(keine Calls im Window)";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RefreshRateLimitStatus geworfen");
        }
    }

    /// <summary>
    /// Speichert die geaenderten Soft/Hard-Thresholds nach Validierung.
    /// Validation: soft &gt; 0, soft &lt; hard, hard &lt;= MaxAllowedHard (4900).
    /// </summary>
    [RelayCommand]
    public async Task SaveRateLimitThresholdsAsync()
    {
        if (BricklinkSoftThreshold <= 0)
        {
            RateLimitSaveMessage = "Soft-Threshold muss > 0 sein.";
            return;
        }
        if (BricklinkHardThreshold <= BricklinkSoftThreshold)
        {
            RateLimitSaveMessage = "Hard-Threshold muss > Soft-Threshold sein.";
            return;
        }
        if (BricklinkHardThreshold > BricklinkMaxAllowedHard)
        {
            RateLimitSaveMessage = $"Hard-Threshold darf maximal {BricklinkMaxAllowedHard} sein " +
                                   $"(Sicherheitsmarge zum BL-Limit {BricklinkRealLimit}).";
            return;
        }

        _settingsService.Current.Bricklink.SoftThreshold = BricklinkSoftThreshold;
        _settingsService.Current.Bricklink.HardThreshold = BricklinkHardThreshold;
        await _settingsService.SaveAsync();
        RateLimitSaveMessage = "Schwellwerte gespeichert.";
        await RefreshRateLimitStatusAsync();
    }

    /// <summary>
    /// Debug-Button: ruft BlCatalogService.GetMinifigPartsAsync("arc007") auf,
    /// um die R2-Cache-Mechanik (uebergreifend Subsets+Items) live zu sehen.
    /// Wird in R4 vermutlich wieder entfernt.
    /// </summary>
    [RelayCommand]
    public async Task DebugFetchArc007Async()
    {
        BricklinkTestResultText = "Hole arc007-Teileliste...";
        try
        {
            var subsets = await _blCatalogService.GetMinifigPartsAsync("arc007");
            BricklinkTestResultText = $"arc007: {subsets.Count} Subset-Eintraege gecacht.";
            await RefreshBlCacheStatsAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Debug-Fetch arc007 fehlgeschlagen");
            BricklinkTestResultText = $"Fehler: {ex.Message}";
        }
    }

    /// <summary>Loescht den kompletten BL-Cache (mit Bestaetigung).</summary>
    [RelayCommand]
    public async Task ClearBlCacheAsync()
    {
        var msg = "BL-Cache komplett leeren? Items, Subsets und Colors werden geloescht. " +
                  "Alle zukuenftigen Lookups holen neu von BL.";
        // Destruktive Aktion -> "Ja" / "Nein"
        if (!await _dialogs.ShowQuestionAsync("Cache leeren", msg)) return;

        try
        {
            await _blCatalogService.ClearCacheAsync();
            await RefreshBlCacheStatsAsync();
            BricklinkTestResultText = "BL-Cache geleert.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BL-Cache leeren fehlgeschlagen");
            BricklinkTestResultText = $"Fehler: {ex.Message}";
        }
    }

    /// <summary>Holt die externe IP von api.ipify.org und zeigt sie im UI an.</summary>
    [RelayCommand]
    public async Task ShowExternalIpAsync()
    {
        try
        {
            // ipify.org ist ein bekannter freier Echo-IP-Service (HTTPS, kein Auth).
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ip = await _http.GetStringAsync("https://api.ipify.org", cts.Token);
            BricklinkExternalIp = ip.Trim();
        }
        catch (Exception ex)
        {
            BricklinkExternalIp = $"Fehler: {ex.Message}";
        }
    }

    /// <summary>Formatiert Bytes als "12.4 MB" oder "1.5 GB" je nach Groesse.</summary>
    private static string FormatMb(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        double mb = bytes / 1024.0 / 1024.0;
        if (mb >= 1024) return $"{mb / 1024:0.#} GB";
        return $"{mb:0.#} MB";
    }

    // ========================================================================
    // Phase 6: Statistik-Tab
    // ========================================================================

    [ObservableProperty] private bool _statsRangeToday = true;
    [ObservableProperty] private bool _statsRange7Days;
    [ObservableProperty] private bool _statsRange30Days;
    [ObservableProperty] private bool _statsRangeAllTime;

    [ObservableProperty] private int _statsScanCount;
    [ObservableProperty] private int _statsCompletedCount;
    [ObservableProperty] private int _statsDismantledCount;

    [ObservableProperty] private int _currentWaitingCount;
    [ObservableProperty] private int _currentCompleteCount;
    [ObservableProperty] private int _currentFloatingCount;
    [ObservableProperty] private int _currentBinsUsedCount;

    /// <summary>Phase 7: Default-Ordner fuer den BSX-Export.</summary>
    [ObservableProperty] private string _bsxExportFolder = string.Empty;

    /// <summary>
    /// Default-Pfad fuer BSX-Exporte (Documents\HBSort-Export\).
    /// Wird im Export-Tab als Hinweis angezeigt und vom "Zuruecksetzen"-Button
    /// genutzt. Statisch, weil der Pfad nur vom System-Profil abhaengt.
    /// </summary>
    public static string DefaultBsxExportFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "HBSort-Export");

    /// <summary>
    /// Speichert den BSX-Export-Ordner SOFORT in settings.json (ohne den
    /// "Speichern"-Button zu druecken). Der Export-Tab nutzt das, damit
    /// der User die Aenderung direkt in den BsxExportDialog sieht.
    ///
    /// Annahme: NULL/leer = "auf Default zuruecksetzen" (loescht den Eintrag
    /// in settings.json, BsxExportDialog faellt dann auf Documents/HBSort-Export
    /// zurueck).
    /// </summary>
    public async Task SaveBsxExportFolderImmediatelyAsync(string? folder)
    {
        var trimmed = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        // VM-Property sofort aktualisieren, damit die UI-TextBox refreshed wird.
        BsxExportFolder = trimmed ?? string.Empty;

        // In die zentrale Settings-Instanz schreiben und persistieren.
        // Der BsxExportDialog liest beim Oeffnen direkt aus _settings.Current.BsxExportFolder
        // - dadurch greift die Aenderung sofort beim naechsten Export.
        _settingsService.Current.BsxExportFolder = trimmed;
        await _settingsService.SaveAsync();
        Log.Information("BSX-Export-Ordner geaendert auf: {Folder}", trimmed ?? "(Default)");
    }

    // ====================================================================
    // Phase 8: Preise-Tab
    // ====================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPriceProviderNone))]
    [NotifyPropertyChangedFor(nameof(IsPriceProviderBricklink))]
    [NotifyPropertyChangedFor(nameof(MinifigPreviewLabel))]
    [NotifyPropertyChangedFor(nameof(PartsPreviewLabel))]
    private string _priceProvider = "None";

    public bool IsPriceProviderNone       => PriceProvider == "None";
    public bool IsPriceProviderBricklink  => PriceProvider == "BricklinkApi";

    [ObservableProperty] private string _priceGuideType = "sold";       // "sold" | "stock"
    [ObservableProperty] private string _pricePriceColumn = "qty_avg";  // "min"|"avg"|"qty_avg"|"max"
    [ObservableProperty] private string _priceRegion = "europe";
    [ObservableProperty] private string _priceCountryCode = "DE";
    [ObservableProperty] private string _priceCurrency = "EUR";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinifigPreviewLabel))]
    private decimal _priceCorrectionMinifigPercent = -10m;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PartsPreviewLabel))]
    private decimal _priceCorrectionPartsPercent = -15m;

    // Audit W-8 (2026-05-04): _priceCacheDays-Field entfernt; siehe
    // _priceCacheTtlMinifigDays / _priceCacheTtlPartDays unten.
    [ObservableProperty] private bool _priceAutoLoadOnComplete = true; // DEPRECATED, siehe AutoLoadCompletePrice

    // UX#12: getrennte TTLs fuer Stale-While-Revalidate.
    [ObservableProperty] private int _priceCacheTtlMinifigDays = 90;
    [ObservableProperty] private int _priceCacheTtlPartDays = 90;

    // UX-Iteration X.10: pro Bereich Auto vs Manuell.
    [ObservableProperty] private PriceLoadMode _priceAutoLoadCompletePrice = PriceLoadMode.Manual;
    [ObservableProperty] private PriceLoadMode _priceAutoLoadPartsPrice    = PriceLoadMode.Manual;

    /// <summary>
    /// UX-Iteration X.10: Optionen fuer die zwei "Preise laden"-Dropdowns.
    /// Statisch - reicht fuer einen Enum mit zwei Werten.
    /// </summary>
    public IReadOnlyList<LoadModeOption> PriceLoadModeOptions { get; } = new[]
    {
        new LoadModeOption(PriceLoadMode.Manual, "Manuell"),
        new LoadModeOption(PriceLoadMode.Auto,   "Auto")
    };

    public sealed record LoadModeOption(PriceLoadMode Value, string Label);

    /// <summary>Anzahl Eintraege im Preis-Cache (Settings-Anzeige).</summary>
    [ObservableProperty] private int _priceCacheEntryCount;

    /// <summary>"X Eintraege" als Anzeige-Text.</summary>
    public string PriceCacheCountText
        => PriceCacheEntryCount == 0
            ? "Cache ist leer."
            : $"Aktuell {PriceCacheEntryCount} Eintraege im Preis-Cache";

    partial void OnPriceCacheEntryCountChanged(int value)
        => OnPropertyChanged(nameof(PriceCacheCountText));

    /// <summary>Live-Vorschau "BL Avg 10.00 EUR -> mein VK 9.00 EUR" fuer Minifig.</summary>
    public string MinifigPreviewLabel
    {
        get
        {
            const decimal demoBl = 10.00m;
            var corrected = Math.Round(demoBl * (1m + PriceCorrectionMinifigPercent / 100m), 2);
            return $"Beispiel Minifig: BL {demoBl:F2} {PriceCurrency} -> mein VK {corrected:F2} {PriceCurrency}";
        }
    }

    /// <summary>Live-Vorschau fuer Einzelteile-Summe.</summary>
    public string PartsPreviewLabel
    {
        get
        {
            const decimal demoBl = 6.00m;
            var corrected = Math.Round(demoBl * (1m + PriceCorrectionPartsPercent / 100m), 2);
            return $"Beispiel Teile-Summe: BL {demoBl:F2} {PriceCurrency} -> mein VK {corrected:F2} {PriceCurrency}";
        }
    }

    /// <summary>Wird beim Tippen in PriceCurrency aufgerufen, damit Vorschau-Labels triggern.</summary>
    partial void OnPriceCurrencyChanged(string value)
    {
        OnPropertyChanged(nameof(MinifigPreviewLabel));
        OnPropertyChanged(nameof(PartsPreviewLabel));
    }

    // Bei Wechsel des aktiven Radio-Buttons direkt nachladen.
    partial void OnStatsRangeTodayChanged(bool value)   { if (value) _ = LoadStatsAsync(); }
    partial void OnStatsRange7DaysChanged(bool value)   { if (value) _ = LoadStatsAsync(); }
    partial void OnStatsRange30DaysChanged(bool value)  { if (value) _ = LoadStatsAsync(); }
    partial void OnStatsRangeAllTimeChanged(bool value) { if (value) _ = LoadStatsAsync(); }

    /// <summary>
    /// Aggregiert die DailyStats fuer den gewaehlten Zeitraum + ermittelt den
    /// aktuellen Bestand (Wartende, Komplette, Floating, belegte Faecher).
    /// </summary>
    public async Task LoadStatsAsync()
    {
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();

            // Audit M-2: DailyStats.Date ist in UTC - die Statistik-Filter
            // muessen ebenfalls UTC nutzen, sonst sind die Counts off-by-one.
            var todayUtc = DateTime.UtcNow.Date;
            DateTime? since = StatsRangeToday   ? todayUtc
                            : StatsRange7Days   ? todayUtc.AddDays(-6)   // inkl. heute
                            : StatsRange30Days  ? todayUtc.AddDays(-29)  // inkl. heute
                            : (DateTime?)null;                            // AllTime

            var query = ctx.DailyStats.AsNoTracking().AsQueryable();
            if (since.HasValue) query = query.Where(s => s.Date >= since.Value);

            var rows = await query.ToListAsync();
            StatsScanCount       = rows.Sum(s => s.ScanCount);
            StatsCompletedCount  = rows.Sum(s => s.MinifigsCompletedCount);
            StatsDismantledCount = rows.Sum(s => s.MinifigsDismantledCount);

            // Aktueller Bestand
            CurrentWaitingCount = await ctx.TrackedMinifigs.AsNoTracking()
                .CountAsync(m => m.Status == TrackedMinifigStatus.Waiting);
            CurrentCompleteCount = await ctx.TrackedMinifigs.AsNoTracking()
                .CountAsync(m => m.Status == TrackedMinifigStatus.Complete);
            CurrentFloatingCount = await ctx.FloatingParts.AsNoTracking()
                .SumAsync(f => (int?)f.Quantity) ?? 0;
            CurrentBinsUsedCount = await ctx.StorageBins.AsNoTracking()
                .CountAsync(b => b.FreedAt == null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoadStatsAsync fehlgeschlagen");
        }
    }

}
