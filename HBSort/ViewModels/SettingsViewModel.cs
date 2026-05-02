using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.Services;
using Microsoft.Win32;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel für den Einstellungen-Dialog.
/// Zeigt die aktuellen Settings an und lässt den User sie ändern.
/// Änderungen werden erst beim Klick auf "Speichern" übernommen.
///
/// Phase 1.5: Erweitert um den Tab "Katalog" (Anzeige Metadaten + Update).
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ICameraService _cameraService;
    private readonly ICatalogService _catalogService;
    private readonly ICatalogImporter _catalogImporter;
    private readonly IPersistentImageCache _imageCache;
    private readonly IBricklinkTokenStorage _bricklinkTokenStorage;
    private readonly IBricklinkClient _bricklinkClient;
    private readonly IBlCatalogService _blCatalogService;
    private readonly IBricklinkRateLimiter _rateLimiter;
    private readonly IUiDensityService _uiDensity;
    private readonly HttpClient _http;

    /// <summary>Tab "Lagerfaecher" – eigenes ViewModel mit Liste + Commands.</summary>
    public BinManagerViewModel BinManager { get; }

    // --- Kamera ---

    /// <summary>Liste der verfügbaren Kameras</summary>
    [ObservableProperty]
    private List<string> _availableCameras = [];

    /// <summary>Aktuell ausgewählte Kamera</summary>
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

    // --- UI-Darstellungsdichte ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDensityCompact))]
    [NotifyPropertyChangedFor(nameof(IsDensityNormal))]
    [NotifyPropertyChangedFor(nameof(IsDensityComfortable))]
    private UiDensity _selectedDensity;

    public bool IsDensityCompact     => SelectedDensity == UiDensity.Compact;
    public bool IsDensityNormal      => SelectedDensity == UiDensity.Normal;
    public bool IsDensityComfortable => SelectedDensity == UiDensity.Comfortable;

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

    // --- Katalog (Phase 1.5) ---

    /// <summary>Datum des letzten Imports (formatiert).</summary>
    [ObservableProperty]
    private string _catalogImportedAt = "(unbekannt)";

    [ObservableProperty]
    private int _catalogMinifigCount;

    [ObservableProperty]
    private int _catalogPartCount;

    [ObservableProperty]
    private int _catalogColorCount;

    [ObservableProperty]
    private int _catalogSetCount;

    /// <summary>Liste der zuletzt importierten ZIPs (Pfade, max. 7).</summary>
    [ObservableProperty]
    private ObservableCollection<string> _recentCatalogZips = [];

    [ObservableProperty]
    private bool _hasNoRecentZips = true;

    // Update-Workflow Statusfelder
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCatalogUpdateIdle))]
    private bool _isCatalogUpdateRunning;

    [ObservableProperty]
    private double _catalogUpdateProgress;

    [ObservableProperty]
    private string _catalogUpdateStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCatalogUpdateMessage))]
    private string _catalogUpdateMessage = string.Empty;

    public bool IsCatalogUpdateIdle => !IsCatalogUpdateRunning;
    public bool HasCatalogUpdateMessage => !string.IsNullOrEmpty(CatalogUpdateMessage);

    public SettingsViewModel(
        ISettingsService settingsService,
        ICameraService cameraService,
        ICatalogService catalogService,
        ICatalogImporter catalogImporter,
        IPersistentImageCache imageCache,
        IBricklinkTokenStorage bricklinkTokenStorage,
        IBricklinkClient bricklinkClient,
        IBlCatalogService blCatalogService,
        IBricklinkRateLimiter rateLimiter,
        IUiDensityService uiDensity,
        HttpClient http,
        BinManagerViewModel binManager)
    {
        _settingsService = settingsService;
        _cameraService = cameraService;
        _catalogService = catalogService;
        _catalogImporter = catalogImporter;
        _imageCache = imageCache;
        _bricklinkTokenStorage = bricklinkTokenStorage;
        _bricklinkClient = bricklinkClient;
        _blCatalogService = blCatalogService;
        _rateLimiter = rateLimiter;
        _uiDensity = uiDensity;
        _http = http;
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

        // Aktuelle UI-Density vom Service uebernehmen
        SelectedDensity = uiDensity.Current;

        // Aktuelle Werte aus den Settings laden
        LoadFromSettings();

        // Catalog-Metadaten asynchron laden (UI nicht blockieren)
        _ = LoadCatalogMetadataAsync();

        // Cache-Stats initial befuellen
        RefreshCacheStats();
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
        PreferBricklinkImages = s.ImageCache.PreferBricklinkImages;
        PreloadOnMinifigScan = s.ImageCache.PreloadOnMinifigScan;
        ImageCacheLimitMb = s.ImageCache.LimitMb;
        PriceToolUrl = s.PriceToolUrl;

        // Recent-Liste in eine ObservableCollection kopieren
        RecentCatalogZips = new ObservableCollection<string>(s.RecentCatalogZips);
        HasNoRecentZips = RecentCatalogZips.Count == 0;

        // Kameras auflisten
        AvailableCameras = _cameraService.GetAvailableCameras();
    }

    /// <summary>
    /// Liest die Catalog-Metadaten aus der DB und aktualisiert die Anzeige im Tab.
    /// </summary>
    private async Task LoadCatalogMetadataAsync()
    {
        var meta = await _catalogService.GetMetadataAsync();
        if (meta == null)
        {
            CatalogImportedAt = "(nicht verfuegbar)";
            return;
        }

        // Datum schoener formatieren
        if (DateTime.TryParse(meta.ImportedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt))
        {
            CatalogImportedAt = dt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        }
        else
        {
            CatalogImportedAt = meta.ImportedAt;
        }

        CatalogMinifigCount = meta.MinifigCount;
        CatalogPartCount    = meta.PartCount;
        CatalogColorCount   = meta.ColorCount;
        CatalogSetCount     = meta.SetCount;
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
        s.ImageCache.PreferBricklinkImages = PreferBricklinkImages;
        s.ImageCache.PreloadOnMinifigScan = PreloadOnMinifigScan;
        s.ImageCache.LimitMb = ImageCacheLimitMb;

        // Recent-Liste zurueckschreiben
        s.RecentCatalogZips = new List<string>(RecentCatalogZips);

        await _settingsService.SaveAsync();
        Log.Information("Einstellungen gespeichert");
    }

    /// <summary>
    /// Workflow: User klickt "ZIPs auswaehlen und importieren..."
    /// 1. File-Picker fuer Multi-Select
    /// 2. Backup der bestehenden catalog.db als catalog.db.bak
    /// 3. Import in temporaere catalog.db.new
    /// 4. Bei Erfolg: alte DB ersetzen, Recent-Liste aktualisieren
    /// 5. Bei Fehler: alte DB bleibt unangetastet, Fehlermeldung anzeigen
    /// </summary>
    [RelayCommand]
    public async Task PickAndImportCatalogAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Rebrickable-Catalog-ZIPs auswaehlen",
            Filter = "ZIP-Dateien (*.zip)|*.zip",
            Multiselect = true
        };

        if (dlg.ShowDialog() != true) return;

        var zipPaths = dlg.FileNames.ToList();
        if (zipPaths.Count == 0) return;

        Log.Information("Catalog-Update gestartet mit {Count} ZIPs", zipPaths.Count);

        IsCatalogUpdateRunning = true;
        CatalogUpdateMessage = string.Empty;
        CatalogUpdateProgress = 0;
        CatalogUpdateStatus = "Wird gestartet...";

        // Pfade definieren
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HBSort");
        var dbPath  = Path.Combine(appData, "catalog.db");
        var bakPath = Path.Combine(appData, "catalog.db.bak");
        var newPath = Path.Combine(appData, "catalog.db.new");

        // Progress an UI weiterreichen
        var progress = new Progress<CatalogImportProgress>(p =>
        {
            CatalogUpdateStatus = p.Message;
            if (p.TotalSteps > 0)
            {
                CatalogUpdateProgress = Math.Min(100, (double)p.CurrentStep / p.TotalSteps * 100.0);
            }
        });

        try
        {
            // Backup der bestehenden DB anlegen (falls vorhanden)
            if (File.Exists(dbPath))
            {
                File.Copy(dbPath, bakPath, overwrite: true);
                Log.Information("Backup catalog.db.bak erstellt");
            }

            // Import in catalog.db.new
            if (File.Exists(newPath)) File.Delete(newPath);

            await _catalogImporter.ImportFromZipsAsync(zipPaths, newPath, progress);

            // Bei Erfolg: alte DB ersetzen
            if (File.Exists(dbPath)) File.Delete(dbPath);
            File.Move(newPath, dbPath);
            Log.Information("catalog.db erfolgreich aktualisiert");

            // Recent-Liste pflegen (Front-Insert, max 7, doppelte rauswerfen)
            UpdateRecentList(zipPaths);

            // Settings sofort speichern (ohne dass der User auf Speichern klicken muss –
            // sonst geht die Recent-Liste verloren wenn er Abbrechen klickt)
            _settingsService.Current.RecentCatalogZips = new List<string>(RecentCatalogZips);
            await _settingsService.SaveAsync();

            // Metadaten neu laden
            await LoadCatalogMetadataAsync();

            CatalogUpdateMessage = "Katalog erfolgreich aktualisiert.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Catalog-Update fehlgeschlagen");
            CatalogUpdateMessage = $"Fehler: {ex.Message}\nDie alte catalog.db wurde nicht veraendert.";

            // Aufraeumen: catalog.db.new loeschen falls noch vorhanden
            try { if (File.Exists(newPath)) File.Delete(newPath); } catch { /* best effort */ }
        }
        finally
        {
            IsCatalogUpdateRunning = false;
        }
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
    public void ApplyCustomLimit()
    {
        if (int.TryParse(CustomLimitInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) && mb > 0)
        {
            ImageCacheLimitMb = mb;
            RefreshCacheStats();
        }
        else
        {
            MessageBox.Show("Bitte eine positive Ganzzahl in Megabyte eingeben.",
                "Ungueltige Eingabe", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Loescht den kompletten Bild-Cache (mit Bestaetigungs-Dialog).</summary>
    [RelayCommand]
    public async Task ClearCacheAsync()
    {
        var stats = _imageCache.GetStats();
        var msg = $"{stats.FileCount} Bilder und {FormatMb(stats.TotalSizeBytes)} werden geloescht. Fortfahren?";
        var result = MessageBox.Show(msg, "Cache leeren", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

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
            BricklinkStatusText = "Tokens nicht entschluesselbar – bitte neu eingeben.";
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
        var result = MessageBox.Show(
            "BrickLink-Tokens wirklich loeschen? Du musst sie spaeter neu eingeben.",
            "Tokens loeschen",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

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
                BricklinkTestResultText = $"OK – '{result.ItemName}' erkannt ({result.ResponseTimeMs} ms)";
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

    /// <summary>
    /// Wechselt die UI-Density sofort und persistiert sie. Wird von den Radio-Buttons
    /// im "Darstellungsdichte"-Bereich aufgerufen.
    /// </summary>
    [RelayCommand]
    public async Task ApplyUiDensityAsync(string density)
    {
        if (Enum.TryParse<UiDensity>(density, ignoreCase: true, out var d))
        {
            SelectedDensity = d;
            await _uiDensity.ApplyAsync(d);
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
        var result = MessageBox.Show(msg, "Cache leeren", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

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

    /// <summary>
    /// Aktualisiert die Recent-Liste der zuletzt importierten ZIPs.
    /// Neue Pfade werden vorne eingefuegt, doppelte rausgefiltert,
    /// die Liste auf max. 7 Eintraege begrenzt.
    /// </summary>
    private void UpdateRecentList(IEnumerable<string> newPaths)
    {
        // Wir packen alle Pfade zusammen in eine Datei – jeder Update-Lauf produziert
        // typischerweise mehrere Dateien, aber wir merken uns sie alle (so kann der
        // User mit einem Klick einen alten Stand reproduzieren).
        var combined = string.Join(" ; ", newPaths.Select(Path.GetFileName));
        var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm}: {combined}";

        // Alte Eintraege mit gleichem Inhalt rauswerfen
        var existing = RecentCatalogZips.Where(e => e != entry).Take(6).ToList();

        RecentCatalogZips = new ObservableCollection<string>(new[] { entry }.Concat(existing));
        HasNoRecentZips = RecentCatalogZips.Count == 0;
    }
}
