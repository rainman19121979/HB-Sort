using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Exceptions;
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
    private readonly IUpdateService _updateService;
    private readonly IBackupService _backupService;
    // v0.1.24-beta.6 Phase 1: BL-Store-Inventar-Sync.
    private readonly IBlInventoryService _blInventory;
    private readonly INotificationService _notifications;

    /// <summary>Tab "Lagerfaecher" - eigenes ViewModel mit Liste + Commands.</summary>
    public BinManagerViewModel BinManager { get; }

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block M: User-pflegbares Mapping einer
    /// Brickognize-Kategorie auf ein Lagerfach. Wird im Tab "Kategorien"
    /// als ListView angezeigt - bisher gesehene Kategorien aus dem
    /// Setting werden hier mit einem Bin-Dropdown angereichert.
    /// </summary>
    public ObservableCollection<CategoryMappingRow> CategoryMappings { get; } = new();

    /// <summary>
    /// Bin-Liste fuers Combobox-Binding pro Mapping-Zeile. Wird beim Laden
    /// einmal befuellt: erstes Element ist ein Sentinel-Bin
    /// (<see cref="CategoryMappingNoneSentinel"/>) der die "(kein Mapping)"-
    /// Auswahl darstellt. Save-Pfad behandelt den Sentinel speziell und
    /// entfernt das Mapping-Dict-Entry.
    /// </summary>
    public ObservableCollection<StorageBin> CategoryMappingAvailableBins { get; } = new();

    /// <summary>
    /// UX X.33 Block N (v0.1.19-beta.7) Mini-Fix: Sentinel-Bin der die
    /// "(kein Mapping)"-Auswahl in der Kategorien-Tab-ComboBox repraesentiert.
    /// Id=0 (existiert nicht in DB), Label = "(kein Mapping)". WPF-ComboBox
    /// haendelt null-Items unsauber - Sentinel-Object ist robuster.
    /// </summary>
    public static readonly StorageBin CategoryMappingNoneSentinel = new()
    {
        Id = 0,
        Label = "(kein Mapping)"
    };

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

    // UX X.33 v0.1.19-beta.7: _priceToolUrl entfernt - die Property war
    // nirgends im UI gebunden und das zugehoerige AppSettings.PriceToolUrl
    // ist mit dieser Iteration ebenfalls weggefallen.

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

    // --- UX-Iteration X.23: Updates-Tab ---

    /// <summary>Toggle "Beim App-Start nach Updates suchen". Persistiert in
    /// AppSettings.AutoCheckForUpdates.</summary>
    [ObservableProperty]
    private bool _autoCheckForUpdates;

    /// <summary>True wenn die App per Setup.exe installiert ist (= Auto-Update
    /// nutzbar). Steuert die Enable-Property aller Update-Controls.</summary>
    [ObservableProperty]
    private bool _isUpdateInstalled;

    /// <summary>Status-Text "Per Setup.exe installiert" / "Portable-ZIP-Build".</summary>
    [ObservableProperty]
    private string _updateInstallStateText = "...";

    /// <summary>Aktuelle App-Version aus dem Assembly.</summary>
    [ObservableProperty]
    private string _currentVersionText = "-";

    /// <summary>Zeitstempel der letzten Update-Pruefung (oder "noch nie").</summary>
    [ObservableProperty]
    private string _lastUpdateCheckText = "noch nie";

    /// <summary>Zwischenzustand-Text nach manuellem Check ("Pruefe..." / "Keine
    /// neue Version" / "Update {Version} verfuegbar").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateCheckStatus))]
    private string _updateCheckStatusText = string.Empty;

    public bool HasUpdateCheckStatus => !string.IsNullOrEmpty(UpdateCheckStatusText);

    // --- UX X.28 (v0.1.15): Auto-BL-Import ---

    /// <summary>Toggle "Automatisch im Hintergrund aktualisieren".</summary>
    [ObservableProperty]
    private bool _autoBlImport;

    /// <summary>Intervall in Tagen (UI: Dropdown 7/14/30/90).</summary>
    [ObservableProperty]
    private int _autoBlImportIntervalDays;

    /// <summary>Anzeige-Text "Letzte Aktualisierung: 12.04.2026 14:23" oder "noch nie".</summary>
    [ObservableProperty]
    private string _lastBlImportText = "noch nie";

    /// <summary>
    /// UX X.29 (v0.1.16): Anzeige-Text "Letzte Pruefung: ..." - wird bei jedem
    /// Auto-Import-Lauf gesetzt (auch wenn die Daten unveraendert waren).
    /// </summary>
    [ObservableProperty]
    private string _lastBlImportCheckText = "noch nie";

    /// <summary>UX X.29: True wenn die letzte Pruefung neuer ist als die letzte
    /// echte Aktualisierung - dann wird der Pruefung-Text in der UI angezeigt.</summary>
    [ObservableProperty]
    private bool _showLastBlImportCheck;

    /// <summary>Verfuegbare Intervall-Optionen fuer das Dropdown.</summary>
    public List<int> AutoBlImportIntervalOptions { get; } = new() { 7, 14, 30, 90 };

    /// <summary>Refresht die LastBlImportText-Anzeige aus den aktuellen Settings.</summary>
    public Task RefreshLastBlImportTextAsync()
    {
        var s = _settingsService.Current;
        var last = s.LastBlImport;
        LastBlImportText = last is null
            ? "noch nie"
            : last.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        // UX X.29: Pruefung-Anzeige nur wenn sie neuer ist als die letzte
        // echte Aktualisierung (sonst redundant - User sieht eh den
        // Aktualisierungs-Zeitstempel).
        var check = s.LastBlImportCheck;
        if (check is null)
        {
            LastBlImportCheckText = "noch nie";
            ShowLastBlImportCheck = false;
        }
        else
        {
            LastBlImportCheckText = check.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
            ShowLastBlImportCheck = last is null
                || check.Value > last.Value.AddSeconds(5); // 5s Toleranz fuer den Selben-Lauf-Fall
        }
        return Task.CompletedTask;
    }

    // --- UX X.29 Block G (v0.1.16): Default-Auswahl beim Scannen ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultPartsNothingCollected))]
    [NotifyPropertyChangedFor(nameof(DefaultPartsAllCollected))]
    private bool _defaultPartsCollected;

    /// <summary>RadioButton-Helper fuer "Nichts vorab abgehakt".</summary>
    public bool DefaultPartsNothingCollected
    {
        get => !DefaultPartsCollected;
        set { if (value) DefaultPartsCollected = false; }
    }

    /// <summary>RadioButton-Helper fuer "Alles vorab abgehakt".</summary>
    public bool DefaultPartsAllCollected
    {
        get => DefaultPartsCollected;
        set { if (value) DefaultPartsCollected = true; }
    }

    // --- UX X.31 (v0.1.18): Maximum Complete-Figuren pro Lagerfach ---
    [ObservableProperty]
    private int _maxCompleteFiguresPerBin;

    // --- UX X.32 v0.1.19-beta.4: Maximum wartende Figuren pro Lagerfach ---
    [ObservableProperty]
    private int _maxWaitingFiguresPerBin;

    // UX X.33 v0.1.19-beta.7 Block M: _maxFloatingPartTypesPerBin entfernt -
    // wurde durch das CategoryToBinMapping abgeloest.

    // --- UX X.29 (v0.1.16): Backup-Tab ---

    [ObservableProperty]
    private bool _autoBackup;

    [ObservableProperty]
    private int _autoBackupIntervalDays;

    [ObservableProperty]
    private int _backupKeepCount;

    [ObservableProperty]
    private string _lastBackupDisplay = "noch nie";

    /// <summary>Liste der vorhandenen Backups, neueste zuerst (fuer DataGrid).</summary>
    public ObservableCollection<BackupRowViewModel> Backups { get; } = new();

    [ObservableProperty]
    private string _backupsSummary = string.Empty;

    // --- v0.1.24-beta.6 Phase 1: BL-Store-Inventar-Sync ---

    /// <summary>True solange ein Sync laeuft. Button wird per CanExecute disabled,
    /// der Status-Text zeigt "Synchronisiere..." an.</summary>
    [ObservableProperty]
    private bool _isSyncingInventory;

    /// <summary>Letzter Sync-Status. Default leer; nach Sync entweder
    /// Erfolgsmeldung ("N Lots synchronisiert, ...") oder Fehlermeldung.</summary>
    [ObservableProperty]
    private string _blInventoryStatusText = string.Empty;

    // --- UX X.29 Block F (v0.1.16): Hotkey-Tab ---

    /// <summary>
    /// Statische Liste aller festen Tastatur-Shortcuts. Zentrale
    /// Quelle der Wahrheit - synchron halten mit MainWindow.xaml InputBindings
    /// + MainWindow_PreviewKeyDown + 09-hotkeys.md.
    /// </summary>
    public List<HotkeyEntry> HotkeyEntries { get; } = new()
    {
        new("Leertaste",     "Scan ausloesen (im Sortier-Tab)"),
        new("Q",             "Mehrfach-Scan-Dialog (im Sortier-Tab)"),
        new("F1",            "Hilfe-Tab oeffnen"),
        new("Strg+Z",        "Letzte Aktion rueckgaengig (Verlauf-Tab)"),
        new("Strg+S",        "Sortieren-Tab"),
        new("Strg+L",        "Lagerliste-Tab"),
        new("Strg+H",        "Verlauf-Tab"),
        new("Strg+,",        "Einstellungen oeffnen"),
        new("Strg+B",        "Backup jetzt erstellen"),
        new("Strg+Q",        "App beenden"),
        new("Doppelklick",   "Detail-Dialog oeffnen (Lagerliste)"),
        new("Entf",          "Markierte Items loeschen (Lagerliste)"),
        new("Esc",           "Dialog/Modal-Overlay schliessen"),
        new("Enter",         "Primaeren Button ausloesen (OK / Speichern)")
    };

    /// <summary>UX X.26 (v0.1.13): True wenn ein Update verfuegbar ist.
    /// Steuert die Sichtbarkeit der "Jetzt updaten"-Box im Updates-Tab.</summary>
    [ObservableProperty]
    private bool _hasUpdateAvailable;

    /// <summary>UX X.26 (v0.1.13): Versions-String der gefundenen neuen Version.</summary>
    [ObservableProperty]
    private string _availableUpdateVersion = string.Empty;

    /// <summary>Brush-Resource-Key fuer den Status-Text. Default Info-Blau,
    /// bei Treffer Success-Gruen, bei Fehler Error-Rot.</summary>
    [ObservableProperty]
    private System.Windows.Media.Brush _updateCheckStatusBrush =
        System.Windows.Media.Brushes.Gray;

    private readonly IStorageBinService _binService;

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
        ITooltipsService tooltips,
        IUpdateService updateService,
        IBackupService backupService,
        IStorageBinService binService,
        IBlInventoryService blInventory,
        INotificationService notifications)
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
        _updateService = updateService;
        _backupService = backupService;
        _binService = binService;
        _blInventory = blInventory;
        _notifications = notifications;
        BinManager = binManager;

        // v0.1.22-beta.1 Block B: ctor-Profiling. Block C entscheidet ob die
        // sieben fire-and-forget-Async-Tasks lazy auf Tab-Wechsel verschoben
        // werden (z.B. Backups-Liste erst wenn der Backup-Tab geklickt wird).
        var swCtor = Stopwatch.StartNew();

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

        // UX-Iteration X.23: Update-Tab Initial-Daten.
        InitializeUpdateState();

        // UX X.28 (v0.1.15): Auto-BL-Import-Anzeige initialisieren.
        _ = RefreshLastBlImportTextAsync();

        // UX X.29 (v0.1.16): Backup-Liste laden + Letztes-Backup-Anzeige.
        _ = RefreshBackupsAsync();

        // UX#12: Preis-Cache-Eintraege initial laden.
        _ = RefreshPriceCacheCountAsync();

        swCtor.Stop();
        Log.Information(
            "[PROFILE] SettingsViewModel ctor (sync) {Ms}ms (7+ fire-and-forget " +
            "Refresh-Tasks laufen weiter im Hintergrund)",
            swCtor.ElapsedMilliseconds);
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
        SoundEnabled = s.SoundEnabled;
        DefaultPartsCollected = s.DefaultPartsCollected;
        MaxCompleteFiguresPerBin = s.MaxCompleteFiguresPerBin;
        MaxWaitingFiguresPerBin = s.MaxWaitingFiguresPerBin;
        // UX X.33 v0.1.19-beta.7 Block M: MaxFloatingPartTypesPerBin entfernt -
        // wurde durch das CategoryToBinMapping abgeloest.
        ShowTooltips = s.ShowTooltips;
        AutoCheckForUpdates = s.AutoCheckForUpdates;
        AutoBlImport = s.AutoBlImport;
        AutoBlImportIntervalDays = AutoBlImportIntervalOptions.Contains(s.AutoBlImportIntervalDays)
            ? s.AutoBlImportIntervalDays
            : 30;

        // UX X.29: Backup-Settings
        AutoBackup = s.AutoBackup;
        // Erlaubte Werte fuer Intervall: 1/7/30. Andere Werte fallen auf 1.
        AutoBackupIntervalDays = (s.AutoBackupIntervalDays == 7 || s.AutoBackupIntervalDays == 30)
            ? s.AutoBackupIntervalDays : 1;
        BackupKeepCount = s.BackupKeepCount > 0 ? s.BackupKeepCount : 7;
        PreferBricklinkImages = s.ImageCache.PreferBricklinkImages;
        PreloadOnMinifigScan = s.ImageCache.PreloadOnMinifigScan;
        ImageCacheLimitMb = s.ImageCache.LimitMb;
        BsxExportFolder = s.BsxExportFolder ?? string.Empty;

        // Phase 8: Preise-Tab
        PriceProvider                  = string.IsNullOrWhiteSpace(s.Prices.Provider) ? "None" : s.Prices.Provider;
        PriceGuideType                 = s.Prices.GuideType;
        PricePriceColumn               = s.Prices.PriceColumn;
        PriceRegion                    = s.Prices.Region ?? string.Empty;
        PriceCountryCode               = s.Prices.CountryCode ?? string.Empty;
        // UX X.34 v0.1.20: Filter-Mode aus Settings-Stand ableiten. SettingsService
        // hat schon normalisiert (CountryCode wins wenn beide gesetzt waren), also
        // ist max. ein Wert gefuellt.
        PriceFilterMode                = !string.IsNullOrWhiteSpace(s.Prices.CountryCode)
            ? PriceFilterMode.Country
            : !string.IsNullOrWhiteSpace(s.Prices.Region)
                ? PriceFilterMode.Region
                : PriceFilterMode.Global;
        PriceCurrency                  = s.Prices.Currency;
        PriceCorrectionMinifigPercent  = s.Prices.CorrectionMinifigPercent;
        PriceCorrectionPartsPercent    = s.Prices.CorrectionPartsPercent;
        // Audit W-8: PriceCacheDays-Feld wurde entfernt - die TTL kommt jetzt
        // aus den zwei dedizierten Minifig/Part-Feldern.
        PriceCacheTtlMinifigDays       = s.Prices.BlPriceCacheTtlMinifigDays;
        PriceCacheTtlPartDays          = s.Prices.BlPriceCacheTtlPartDays;
        PriceAutoLoadCompletePrice     = s.Prices.AutoLoadCompletePrice;
        PriceAutoLoadPartsPrice        = s.Prices.AutoLoadPartsPrice;
        // UX X.34 v0.1.20: VAT-Modus laden.
        PriceVatMode                   = s.Prices.VatMode;

        // v0.1.22-beta.4 2026-05-13: KEINE synchrone Enumeration mehr in
        // LoadFromSettings. Die GetAvailableCameras()-Schleife dauert
        // 17-20s und wurde frueher beim Settings-Open (=> auch beim
        // SettingsViewModel-Konstruktor) gerufen. Jetzt: Enumeration nur
        // lazy via EnumerateCamerasAsync() wenn der Settings-Dialog
        // wirklich angezeigt wird (Loaded-Hook in SettingsWindow.xaml.cs).
        //
        // Die Combobox zeigt initial einen einzigen Platzhalter-Eintrag
        // ("Kamera {savedIndex}"), damit der SelectedIndex-Binding-Pfad
        // nicht ins Leere zeigt. Sobald EnumerateCamerasAsync durch ist,
        // wird AvailableCameras ersetzt.
        if (AvailableCameras.Count == 0)
        {
            var placeholder = SelectedCameraIndex >= 0
                ? $"Kamera {SelectedCameraIndex}"
                : "Kamera 0";
            AvailableCameras = new List<string> { placeholder };
        }
    }

    /// <summary>
    /// v0.1.22-beta.4 2026-05-13: Lazy-Enumeration der angeschlossenen
    /// Kameras. Wird vom SettingsWindow beim Loaded-Hook aufgerufen.
    /// Re-Entrancy-Schutz via IsEnumeratingCameras-Flag. Die Probe-
    /// Schleife (10x OpenCV-VideoCapture) laeuft auf einem Thread-Pool-
    /// Worker, der UI-Thread bleibt frei fuer Spinner-Updates.
    /// </summary>
    [ObservableProperty]
    private bool _isEnumeratingCameras;

    public async Task EnumerateCamerasAsync()
    {
        if (IsEnumeratingCameras) return;
        IsEnumeratingCameras = true;
        try
        {
            var cameras = await Task.Run(() => _cameraService.GetAvailableCameras());
            // SelectedCameraIndex bleibt - Combobox aktualisiert sich automatisch
            // sobald AvailableCameras gesetzt wird (PropertyChanged auf
            // _availableCameras via [ObservableProperty]).
            AvailableCameras = cameras;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kamera-Enumeration fehlgeschlagen");
        }
        finally
        {
            IsEnumeratingCameras = false;
        }
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
        s.SoundEnabled = SoundEnabled;
        s.DefaultPartsCollected = DefaultPartsCollected;
        // UX X.31: clamp auf [1..999] beim Speichern, damit ein leeres
        // Eingabefeld oder ein versehentlicher 0/Negativ-Wert die App nicht
        // ins Endlos-Fallback laufen laesst.
        s.MaxCompleteFiguresPerBin = Math.Clamp(MaxCompleteFiguresPerBin <= 0 ? 5 : MaxCompleteFiguresPerBin, 1, 999);
        s.MaxWaitingFiguresPerBin  = Math.Clamp(MaxWaitingFiguresPerBin  <= 0 ? 3 : MaxWaitingFiguresPerBin, 1, 999);
        // UX X.33 v0.1.19-beta.7 Block M: MaxFloatingPartTypesPerBin entfernt.
        s.ShowTooltips = ShowTooltips;
        s.AutoCheckForUpdates = AutoCheckForUpdates;
        s.AutoBlImport = AutoBlImport;
        s.AutoBlImportIntervalDays = AutoBlImportIntervalDays;

        // UX X.29: Backup-Settings
        s.AutoBackup = AutoBackup;
        s.AutoBackupIntervalDays = AutoBackupIntervalDays > 0 ? AutoBackupIntervalDays : 1;
        s.BackupKeepCount = Math.Max(1, BackupKeepCount);
        s.ImageCache.PreferBricklinkImages = PreferBricklinkImages;
        s.ImageCache.PreloadOnMinifigScan = PreloadOnMinifigScan;
        s.ImageCache.LimitMb = ImageCacheLimitMb;
        s.BsxExportFolder = string.IsNullOrWhiteSpace(BsxExportFolder) ? null : BsxExportFolder.Trim();

        // Phase 8: Preise
        s.Prices.Provider                  = PriceProvider;
        s.Prices.GuideType                 = PriceGuideType;
        s.Prices.PriceColumn               = PricePriceColumn;
        // UX X.34 v0.1.20: Filter-Mode-Either-Or beim Save erzwingen.
        // Egal was im UI getippt wurde - nur das Feld des aktiven Modus
        // wandert ins POCO, der andere wird auf "" gesetzt.
        switch (PriceFilterMode)
        {
            case PriceFilterMode.Country:
                s.Prices.CountryCode = (PriceCountryCode ?? string.Empty).Trim();
                s.Prices.Region      = string.Empty;
                break;
            case PriceFilterMode.Region:
                s.Prices.Region      = (PriceRegion ?? string.Empty).Trim();
                s.Prices.CountryCode = string.Empty;
                break;
            default: // Global
                s.Prices.Region      = string.Empty;
                s.Prices.CountryCode = string.Empty;
                break;
        }
        s.Prices.Currency                  = string.IsNullOrWhiteSpace(PriceCurrency) ? "EUR" : PriceCurrency;
        s.Prices.CorrectionMinifigPercent  = PriceCorrectionMinifigPercent;
        s.Prices.CorrectionPartsPercent    = PriceCorrectionPartsPercent;
        // Audit W-8: kein CacheDays-Schreiben mehr - Property entfernt.
        s.Prices.BlPriceCacheTtlMinifigDays = Math.Max(1, PriceCacheTtlMinifigDays);
        s.Prices.BlPriceCacheTtlPartDays    = Math.Max(1, PriceCacheTtlPartDays);
        s.Prices.AutoLoadCompletePrice     = PriceAutoLoadCompletePrice;
        s.Prices.AutoLoadPartsPrice        = PriceAutoLoadPartsPrice;
        // UX X.34 v0.1.20: VAT-Modus speichern.
        s.Prices.VatMode                   = PriceVatMode;

        // UX X.33 v0.1.19-beta.7 Block N Mini-Fix: Mapping-Rows zurueck ins
        // Dict. Sentinel-Eintrag "(kein Mapping)" hat Id=0 - Save-Pfad
        // schreibt nur echte Bins (Id > 0); Sentinel-Auswahl entfernt den
        // Eintrag (clear() macht das schon implizit, der foreach-Add
        // ueberspringt den Sentinel).
        s.CategoryToBinMapping.Clear();
        foreach (var row in CategoryMappings)
        {
            if (row.SelectedBin != null && row.SelectedBin.Id > 0)
                s.CategoryToBinMapping[row.Category] = row.SelectedBin.Id;
        }

        await _settingsService.SaveAsync();
        Log.Information("Einstellungen gespeichert");
    }

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block M: Befuellt CategoryMappings + verfuegbare
    /// Bins fuer den Tab. Wird beim Settings-Dialog-Open einmalig gerufen
    /// (nach LoadFromSettings) und bei Bin-Aenderungen erneut. Behandelt
    /// fehlende Bins (geloeschte IDs) durch Fallback auf null.
    /// </summary>
    public async Task ReloadCategoryMappingsAsync()
    {
        var bins = await _binService.GetAllAsync();

        // UX X.33 Block N Mini-Fix: Sentinel-Eintrag "(kein Mapping)" als
        // erstes Combobox-Item. Damit kann der User ein bestehendes Mapping
        // entfernen, indem er den Sentinel waehlt. Save-Pfad erkennt den
        // Sentinel an Id=0 und entfernt den Dict-Entry.
        CategoryMappingAvailableBins.Clear();
        CategoryMappingAvailableBins.Add(CategoryMappingNoneSentinel);
        foreach (var b in bins) CategoryMappingAvailableBins.Add(b);

        var s = _settingsService.Current;
        CategoryMappings.Clear();
        foreach (var category in s.SeenBrickognizeCategories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            // Bei fehlendem Mapping (oder Mapping auf geloeschtes Bin) wird
            // der Sentinel als SelectedBin gesetzt - sonst zeigt die ComboBox
            // ein leeres Feld und der User koennte den Sentinel nicht aktiv
            // waehlen weil er identisch zum Default-Zustand waere.
            StorageBin selected = CategoryMappingNoneSentinel;
            if (s.CategoryToBinMapping.TryGetValue(category, out var binId))
            {
                var match = bins.FirstOrDefault(b => b.Id == binId);
                if (match != null) selected = match;
            }
            CategoryMappings.Add(new CategoryMappingRow(category, selected));
        }
    }

    // ========================================================================
    // UX-Iteration X.23: Updates-Tab
    // ========================================================================

    /// <summary>
    /// Initialisiert die Update-Tab-Anzeigen aus IUpdateService + Settings.
    /// Liest die Install-Variante (Setup.exe vs Portable-ZIP) und den letzten
    /// Check-Zeitstempel; lost keinen aktiven Update-Check aus.
    /// </summary>
    private void InitializeUpdateState()
    {
        IsUpdateInstalled = _updateService.IsInstalled;
        UpdateInstallStateText = IsUpdateInstalled
            ? "Per Setup.exe installiert (Auto-Update verfuegbar)"
            : "Portable-ZIP-Build oder Debug (kein Auto-Update)";

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        CurrentVersionText = version is null ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";

        var last = _settingsService.Current.LastUpdateCheck;
        LastUpdateCheckText = last is null
            ? "noch nie"
            : last.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        // UX X.26 (v0.1.13): wenn der MainViewModel-Background-Check beim
        // App-Start schon ein Update gefunden hat, ist UpdateService.
        // AvailableVersion gesetzt - dann Box gleich beim ersten Settings-
        // Oeffnen sichtbar machen, ohne dass der User nochmal manuell
        // pruefen muesste.
        if (_updateService.AvailableVersion is { } pendingVersion)
        {
            AvailableUpdateVersion = pendingVersion;
            HasUpdateAvailable = true;
        }
    }

    /// <summary>
    /// Manuelles "Jetzt nach Updates suchen". Aktualisiert Status-Text +
    /// LastUpdateCheck in Settings (sofort persistiert) und setzt
    /// HasUpdateAvailable/AvailableUpdateVersion fuer die "Jetzt updaten"-Box.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateCheckStatusText = "Pruefe...";
        UpdateCheckStatusBrush = System.Windows.Media.Brushes.Gray;

        try
        {
            var hasUpdate = await _updateService.CheckForUpdatesAsync();
            _settingsService.Current.LastUpdateCheck = DateTime.UtcNow;
            await _settingsService.SaveAsync();
            LastUpdateCheckText = DateTime.Now.ToString("dd.MM.yyyy HH:mm");

            if (hasUpdate && _updateService.AvailableVersion is { } v)
            {
                AvailableUpdateVersion = v;
                HasUpdateAvailable = true;
                UpdateCheckStatusText = $"Update {v} verfuegbar - klick unten 'Jetzt auf v{v} updaten'.";
                UpdateCheckStatusBrush = System.Windows.Media.Brushes.Green;
            }
            else
            {
                HasUpdateAvailable = false;
                AvailableUpdateVersion = string.Empty;
                UpdateCheckStatusText = "Du nutzt die aktuelle Version.";
                UpdateCheckStatusBrush = System.Windows.Media.Brushes.Gray;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CheckForUpdatesAsync fehlgeschlagen (UI-Pfad)");
            UpdateCheckStatusText = "Pruefung fehlgeschlagen - kein Internet?";
            UpdateCheckStatusBrush = System.Windows.Media.Brushes.OrangeRed;
        }
    }

    /// <summary>
    /// UX X.26 (v0.1.13): "Jetzt updaten"-Button im Settings-Tab. Triggert
    /// Velopack-Download + Restart. Velopack beendet die App intern und
    /// startet sie neu - der Code nach diesem Aufruf laeuft nur wenn was
    /// schief gegangen ist.
    /// </summary>
    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (!HasUpdateAvailable) return;

        try
        {
            await _updateService.DownloadAndApplyAsync();
            // Wenn wir hier landen: Velopack hat den Restart NICHT angestossen.
            HasUpdateAvailable = false;
            AvailableUpdateVersion = string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update-Apply fehlgeschlagen (Settings-Pfad)");
            UpdateCheckStatusText = "Update fehlgeschlagen - bitte spaeter erneut versuchen.";
            UpdateCheckStatusBrush = System.Windows.Media.Brushes.OrangeRed;
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
    /// v0.1.24-beta.6 Phase 1: holt das komplette BL-Store-Inventar (1 API-Call)
    /// und speichert es als Snapshot in userdata.db (alte Lots werden vorher
    /// geloescht). Erfolg/Fehler via Toast - der Tab-Status-Text bleibt nach
    /// Sync stehen damit der User die Zahl sieht ohne den Tab zu wechseln.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSyncInventory))]
    public async Task SyncInventoryAsync()
    {
        IsSyncingInventory = true;
        SyncInventoryCommand.NotifyCanExecuteChanged();
        BlInventoryStatusText = "Synchronisiere...";
        try
        {
            var count = await _blInventory.SyncInventoryAsync();
            var msg = $"{count} Lots aus dem BrickLink-Store synchronisiert.";
            BlInventoryStatusText = $"{msg} (UTC {DateTime.UtcNow:HH:mm:ss})";
            _notifications.ShowSuccess(msg);
        }
        catch (BricklinkAuthException auth)
        {
            Log.Warning(auth, "BL-Inventar-Sync: Auth fehlt/falsch");
            BlInventoryStatusText = $"Auth-Fehler: {auth.Message}";
            _notifications.ShowError(
                "BrickLink-Tokens fehlen oder sind ungueltig. Bitte oben pruefen.");
        }
        catch (BricklinkRateLimitException rl)
        {
            Log.Warning(rl, "BL-Inventar-Sync: Rate-Limit");
            BlInventoryStatusText = $"Rate-Limit: {rl.Message}";
            _notifications.ShowError(rl.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BL-Inventar-Sync fehlgeschlagen");
            BlInventoryStatusText = $"Fehler: {ex.Message}";
            _notifications.ShowError($"Inventar-Sync fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            IsSyncingInventory = false;
            SyncInventoryCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanSyncInventory() => !IsSyncingInventory;

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

    // UX X.34 v0.1.20: Region/CountryCode haben jetzt Default leer (nicht
    // mehr "europe"/"DE") - der neue Filter-Mode steuert ueber Either-Or
    // welcher der beiden Werte ueberhaupt gefuellt sein darf.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterModeRegion))]
    [NotifyPropertyChangedFor(nameof(IsFilterModeCountry))]
    [NotifyPropertyChangedFor(nameof(IsFilterModeGlobal))]
    private string _priceRegion = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterModeRegion))]
    [NotifyPropertyChangedFor(nameof(IsFilterModeCountry))]
    [NotifyPropertyChangedFor(nameof(IsFilterModeGlobal))]
    private string _priceCountryCode = string.Empty;

    [ObservableProperty] private string _priceCurrency = "EUR";

    /// <summary>
    /// UX X.34 v0.1.20: Filter-Mode Either-Or. Wert wird beim LoadFromSettings
    /// aus dem (CountryCode/Region)-Stand abgeleitet, beim SaveSettings ins
    /// PriceSettings-POCO durchgereicht (anderer Wert wird auf "" gezwungen).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterModeRegion))]
    [NotifyPropertyChangedFor(nameof(IsFilterModeCountry))]
    [NotifyPropertyChangedFor(nameof(IsFilterModeGlobal))]
    private PriceFilterMode _priceFilterMode = PriceFilterMode.Global;

    public bool IsFilterModeGlobal  => PriceFilterMode == PriceFilterMode.Global;
    public bool IsFilterModeRegion  => PriceFilterMode == PriceFilterMode.Region;
    public bool IsFilterModeCountry => PriceFilterMode == PriceFilterMode.Country;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinifigPreviewLabel))]
    private decimal _priceCorrectionMinifigPercent = -10m;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PartsPreviewLabel))]
    private decimal _priceCorrectionPartsPercent = -15m;

    // Audit W-8 (2026-05-04): _priceCacheDays-Field entfernt; siehe
    // _priceCacheTtlMinifigDays / _priceCacheTtlPartDays unten.
    // UX X.33 v0.1.19-beta.7: _priceAutoLoadOnComplete entfernt - hat
    // den DEPRECATED PriceSettings.AutoLoadOnComplete bedient, der ist
    // mit dieser Iteration ebenfalls weggefallen.

    // UX#12: getrennte TTLs fuer Stale-While-Revalidate.
    [ObservableProperty] private int _priceCacheTtlMinifigDays = 90;
    [ObservableProperty] private int _priceCacheTtlPartDays = 90;

    // UX-Iteration X.10: pro Bereich Auto vs Manuell.
    // UX X.34 v0.1.20: bei Wechsel von einem der beiden Werte wird der
    // ShowAutoLoadApiWarning-Banner sichtbar/unsichtbar - PropertyChanged
    // muss den Computed-Helper benachrichtigen.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAutoLoadApiWarning))]
    private PriceLoadMode _priceAutoLoadCompletePrice = PriceLoadMode.Manual;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAutoLoadApiWarning))]
    private PriceLoadMode _priceAutoLoadPartsPrice    = PriceLoadMode.Manual;

    /// <summary>
    /// UX X.34 v0.1.20: zeigt einen orangenen API-Limit-Warnhinweis im
    /// Settings-Tab "BrickLink" → Sektion "Preise" wenn mindestens einer der
    /// beiden AutoLoad-Werte auf Auto steht. Hintergrund: Auto-Mode loest pro
    /// angezeigter Figur API-Aufrufe aus - Komplett-Preis = 1 Call/Figur,
    /// Einzelteile-Preis = 1 Call PRO TEIL-TYP. Bei vielen Figuren oder
    /// niedriger BL-Limit-Reserve ist Manual sparsamer.
    /// </summary>
    public bool ShowAutoLoadApiWarning =>
        PriceAutoLoadCompletePrice == PriceLoadMode.Auto ||
        PriceAutoLoadPartsPrice == PriceLoadMode.Auto;

    // UX X.34 v0.1.20: VAT-Modus fuer BL-Preis-Lookups. Default Y (Brutto)
    // matcht die BL-Webseite-Preise. Vorher kein Parameter -> Library-Default
    // Netto -> gemischte Aggregate. Cache-Wechsel passiert lazy beim ersten
    // Lookup nach Mode-Aenderung (Cache-Miss -> frischer API-Call).
    [ObservableProperty] private VatMode _priceVatMode = VatMode.Y;

    /// <summary>UI-Bindings fuer das VAT-Mode-Dropdown.</summary>
    public IReadOnlyList<VatModeOption> VatModeOptions { get; } = new[]
    {
        new VatModeOption(VatMode.Y, "Brutto (Y) - matcht Website (Default)"),
        new VatModeOption(VatMode.N, "Netto (N) - VAT abgezogen"),
        new VatModeOption(VatMode.O, "Norwegen (O) - 25% VAT")
    };

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

    // ====================================================================
    // UX X.29 (v0.1.16): Backup-System
    // ====================================================================

    /// <summary>Aktualisiert die Backup-Liste + Letztes-Backup-Anzeige.</summary>
    public async Task RefreshBackupsAsync()
    {
        try
        {
            var last = _settingsService.Current.LastBackup;
            LastBackupDisplay = last is null
                ? "noch nie"
                : last.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

            var list = await _backupService.ListBackupsAsync();
            Backups.Clear();
            long totalSize = 0;
            foreach (var b in list)
            {
                Backups.Add(new BackupRowViewModel(b));
                totalSize += b.SizeBytes;
            }
            BackupsSummary = list.Count == 0
                ? "Keine Backups vorhanden."
                : $"{list.Count} Backup(s), gesamt {FormatSize(totalSize)}";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RefreshBackupsAsync fehlgeschlagen");
            BackupsSummary = "Fehler beim Laden der Backup-Liste.";
        }
    }

    /// <summary>Erzeugt ein Backup auf User-Klick. Liefert Statusmeldung fuer die UI.</summary>
    public async Task<string> CreateBackupNowAsync()
    {
        try
        {
            var result = await _backupService.CreateBackupAsync();
            _settingsService.Current.LastBackup = result.CreatedAt;
            await _settingsService.SaveAsync();
            await _backupService.CleanupOldBackupsAsync(_settingsService.Current.BackupKeepCount);
            await RefreshBackupsAsync();
            return $"Backup erstellt: {result.FileName} ({FormatSize(result.SizeBytes)})";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CreateBackupNow fehlgeschlagen");
            return "Backup fehlgeschlagen: " + ex.Message;
        }
    }

    /// <summary>
    /// Bereitet einen Restore vor. Wirft die Backup-Dateien in den
    /// Pending-Ordner und meldet zurueck dass der App-Neustart noetig ist.
    /// Tatsaechlicher File-Replace passiert beim naechsten App-Start
    /// (siehe App.xaml.cs::TryApplyPendingRestoreSafe).
    /// </summary>
    public async Task<bool> RestoreBackupAsync(string fileName)
    {
        try
        {
            var ok = await _backupService.RestoreBackupAsync(fileName);
            await RefreshBackupsAsync();
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RestoreBackup fehlgeschlagen");
            return false;
        }
    }

    /// <summary>Loescht ein einzelnes Backup.</summary>
    public async Task<bool> DeleteBackupAsync(string fileName)
    {
        try
        {
            var ok = await _backupService.DeleteBackupAsync(fileName);
            await RefreshBackupsAsync();
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DeleteBackup fehlgeschlagen");
            return false;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("F2") + " MB";
    }

}

/// <summary>
/// UX X.33 v0.1.19-beta.7 Block M: Eine Zeile im Kategorien-Tab. Kapselt
/// einen Brickognize-Kategorie-String (z.B. "Minifigure, Head") plus das
/// gemappte Bin (oder null = "kein Mapping"). UI-bindbar.
/// </summary>
public partial class CategoryMappingRow : ObservableObject
{
    public string Category { get; }

    [ObservableProperty]
    private StorageBin? _selectedBin;

    public CategoryMappingRow(string category, StorageBin? selectedBin)
    {
        Category = category;
        _selectedBin = selectedBin;
    }
}

/// <summary>
/// UX X.29 Block F (v0.1.16): eine Zeile im Hotkeys-Tab.
/// </summary>
public record HotkeyEntry(string Key, string Action);

/// <summary>
/// UX X.29 (v0.1.16): eine Zeile in der Backup-DataGrid.
/// </summary>
public class BackupRowViewModel
{
    public string FileName { get; }
    public long SizeBytes { get; }
    public DateTime CreatedAt { get; }
    public string CreatedAtDisplay { get; }
    public string SizeDisplay { get; }

    public BackupRowViewModel(BackupInfo info)
    {
        FileName = info.FileName;
        SizeBytes = info.SizeBytes;
        CreatedAt = info.CreatedAt;
        CreatedAtDisplay = info.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
        SizeDisplay = info.SizeBytes < 1024 * 1024
            ? (info.SizeBytes / 1024.0).ToString("F1") + " KB"
            : (info.SizeBytes / (1024.0 * 1024.0)).ToString("F2") + " MB";
    }
}

/// <summary>
/// UX X.34 v0.1.20: UI-Wrapper fuer das VAT-Mode-Dropdown im
/// "BrickLink"-Tab → Sektion "Preise". Enum + Display-Label in einem
/// kleinen Record damit das ComboBox-Binding sauber bleibt.
/// </summary>
public record VatModeOption(VatMode Mode, string Label)
{
    public override string ToString() => Label;
}
