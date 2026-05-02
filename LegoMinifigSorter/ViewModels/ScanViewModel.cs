using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LegoMinifigSorter.Core.Models;
using LegoMinifigSorter.Core.Models.Exceptions;
using LegoMinifigSorter.Core.Services;
using LegoMinifigSorter.Services;
using Serilog;

namespace LegoMinifigSorter.ViewModels;

/// <summary>
/// ViewModel für die Scan-Ansicht (Hauptansicht der App).
///
/// Phase 2: Brickognize-Integration.
///   - Modus-Auswahl (Figur / Teil / Auto)
///   - Schnappschuss + API-Aufruf an passenden Endpoint
///   - Top-3-Karten mit Score-Auswertung und Auto-Akzept-Hervorhebung
///   - Status-Anzeige (Online / Slow / Offline)
/// </summary>
public partial class ScanViewModel : ObservableObject
{
    private readonly ICameraService _cameraService;
    private readonly ISettingsService _settingsService;
    private readonly IBrickognizeClient _brickognizeClient;
    private readonly IExternalIdResolver _idResolver;
    private readonly INotificationService _notifications;
    private readonly ICatalogService _catalogService;     // bleibt fuer Modus-B-Farben (Phase 5), nicht mehr fuer Minifig-Lookup
    private readonly IPartImageProvider _imageProvider;
    private readonly IPersistentImageCache _persistentCache;
    private readonly IBlCatalogService _blCatalog;
    private readonly IStorageBinService _binService;
    private readonly IMinifigPersistenceService _persistenceService;
    private readonly IPartLookupService _partLookup;

    private DateTime _lastScanTime = DateTime.MinValue;
    private bool _isFrozen = false;

    /// <summary>
    /// Cancellation-Token fuer den aktuell laufenden BL-Lookup. Beim Klick auf
    /// eine andere Karte (oder beim naechsten Scan) wird der vorige Lookup gecancelt.
    /// </summary>
    private CancellationTokenSource? _lookupCts;

    /// <summary>Index der aktuell ausgewaehlten Top-3-Karte (-1 = keine).</summary>
    [ObservableProperty]
    private int _selectedCardIndex = -1;

    /// <summary>Das aktuell angezeigte Bild (Live-Kamera oder eingefrorener Schnappschuss)</summary>
    [ObservableProperty]
    private BitmapImage? _currentFrame;

    [ObservableProperty]
    private bool _isCameraRunning;

    [ObservableProperty]
    private string _scanStatusText = "Kamera nicht gestartet";

    /// <summary>Kann gerade gescannt werden? (Cooldown abgelaufen + Kamera aktiv + Brickognize online)</summary>
    [ObservableProperty]
    private bool _canScan;

    [ObservableProperty]
    private List<string> _availableCameras = [];

    [ObservableProperty]
    private int _selectedCameraIndex;

    // --- Modus-Auswahl ---

    /// <summary>Gewaehlter Scan-Modus. Bestimmt welcher Brickognize-Endpoint genutzt wird.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModeMinifig))]
    [NotifyPropertyChangedFor(nameof(IsModePart))]
    [NotifyPropertyChangedFor(nameof(IsModeAuto))]
    [NotifyPropertyChangedFor(nameof(ModeLabel))]
    private ScanMode _scanMode = ScanMode.Auto;

    public bool IsModeMinifig => ScanMode == ScanMode.Minifig;
    public bool IsModePart    => ScanMode == ScanMode.Part;
    public bool IsModeAuto    => ScanMode == ScanMode.Auto;
    public string ModeLabel => ScanMode switch
    {
        ScanMode.Minifig => "Modus: Minifigur",
        ScanMode.Part    => "Modus: Einzelteil",
        _                => "Modus: Automatisch"
    };

    // --- Brickognize-Status ---

    [ObservableProperty]
    private BrickognizeStatus _brickognizeStatus = BrickognizeStatus.Unknown;

    [ObservableProperty]
    private string _brickognizeStatusText = "Brickognize: Status unbekannt";

    // --- Top-3-Ergebnis-Karten ---

    [ObservableProperty]
    private ObservableCollection<ScanResultCard> _resultCards = new();

    /// <summary>True, sobald nach einem Scan Karten angezeigt werden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowTopCards))]
    private bool _hasResults;

    /// <summary>
    /// Top-3-Karten werden ausgeblendet sobald die MinifigDetailView ODER PartLookupView
    /// gezeigt wird – beide Detail-Views machen die Top-Treffer-Info redundant.
    /// </summary>
    public bool ShouldShowTopCards => HasResults && !HasPendingMinifig && !HasPendingPart;

    /// <summary>Score-Status-Text fuer den Hinweis ueber den Karten ("Auto-akzeptiert", "Bitte auswaehlen", "Nicht erkannt").</summary>
    [ObservableProperty]
    private string _resultHeadlineText = string.Empty;

    /// <summary>True, wenn der Such-Dialog-Button angezeigt werden soll (Score &lt; min).</summary>
    [ObservableProperty]
    private bool _showSearchFallback;

    // --- Farb-Erkennung (nur fuer Teile) ---

    /// <summary>Top-Farben aus der Brickognize-Antwort (sortiert nach Score).</summary>
    [ObservableProperty]
    private ObservableCollection<ColorMatch> _recognizedColors = new();

    /// <summary>True wenn die letzte Antwort Farben enthielt (sonst Sektion ausgeblendet).</summary>
    [ObservableProperty]
    private bool _hasColors;

    // --- Phase 3: Minifig-Detail (Pending) ---

    /// <summary>
    /// Aktuell erkannte Minifigur mit Catalog-Daten (in-memory, NICHT persistent).
    /// Wird in Phase 3 nur angezeigt; Speicherung in userdata.db kommt mit Phase 4.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingMinifig))]
    [NotifyPropertyChangedFor(nameof(ShouldShowTopCards))]
    private PendingMinifigViewModel? _pendingMinifig;

    public bool HasPendingMinifig => PendingMinifig != null;

    /// <summary>Hinweis-Text bei Minifig-Erkennung ohne Catalog-Treffer.</summary>
    [ObservableProperty]
    private string _minifigStatusText = string.Empty;

    // --- Phase 5: Modus B (Einzelteil-Scan) ---

    /// <summary>
    /// Aktuell erkanntes Teil mit Lookup-Result (in-memory, NICHT persistent).
    /// Mutually exclusive mit PendingMinifig: nie beide gleichzeitig gesetzt.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingPart))]
    [NotifyPropertyChangedFor(nameof(ShouldShowTopCards))]
    private PartLookupViewModel? _pendingPart;

    public bool HasPendingPart => PendingPart != null;

    public ScanViewModel(
        ICameraService cameraService,
        ISettingsService settingsService,
        IBrickognizeClient brickognizeClient,
        IExternalIdResolver idResolver,
        INotificationService notifications,
        ICatalogService catalogService,
        IPartImageProvider imageProvider,
        IPersistentImageCache persistentCache,
        IBlCatalogService blCatalog,
        IStorageBinService binService,
        IMinifigPersistenceService persistenceService,
        IPartLookupService partLookup)
    {
        _cameraService = cameraService;
        _settingsService = settingsService;
        _brickognizeClient = brickognizeClient;
        _idResolver = idResolver;
        _notifications = notifications;
        _catalogService = catalogService;
        _imageProvider = imageProvider;
        _persistentCache = persistentCache;
        _blCatalog = blCatalog;
        _binService = binService;
        _persistenceService = persistenceService;
        _partLookup = partLookup;

        _cameraService.FrameReceived += OnFrameReceived;
    }

    [RelayCommand]
    public async Task InitializeCameraAsync()
    {
        AvailableCameras = _cameraService.GetAvailableCameras();

        if (AvailableCameras.Count == 0)
        {
            ScanStatusText = "Keine Kamera gefunden! Bitte USB-Kamera anschließen.";
            Log.Warning("Keine Kamera gefunden");
            return;
        }

        var savedIndex = _settingsService.Current.SelectedCameraIndex;
        SelectedCameraIndex = savedIndex < AvailableCameras.Count ? savedIndex : 0;

        await StartCameraAsync(SelectedCameraIndex);
    }

    public async Task StartCameraAsync(int index)
    {
        ScanStatusText = "Kamera wird gestartet...";
        await _cameraService.StartAsync(index);

        IsCameraRunning = _cameraService.IsRunning;
        UpdateCanScan();

        ScanStatusText = IsCameraRunning
            ? "Leertaste drücken zum Scannen"
            : "Kamera konnte nicht gestartet werden!";

        if (IsCameraRunning) Log.Information("Kamera {Index} aktiv", index);
    }

    /// <summary>
    /// Setzt den Brickognize-Status und aktualisiert ScanStatusText/CanScan entsprechend.
    /// Wird vom MainViewModel nach dem Health-Check aufgerufen.
    /// </summary>
    public void UpdateBrickognizeStatus(BrickognizeHealth health)
    {
        BrickognizeStatus = health.Status;

        BrickognizeStatusText = health.Status switch
        {
            BrickognizeStatus.Online  => $"Brickognize: Online ({health.ResponseTimeMs} ms)",
            BrickognizeStatus.Slow    => $"Brickognize: Langsam ({health.ResponseTimeMs} ms)",
            BrickognizeStatus.Offline => $"Brickognize: Offline – {health.ErrorMessage}",
            _                          => "Brickognize: Status unbekannt"
        };

        UpdateCanScan();

        if (health.Status == BrickognizeStatus.Offline)
        {
            _notifications.ShowError("Brickognize nicht erreichbar – Scannen deaktiviert.");
        }
    }

    /// <summary>
    /// Sammelplatz fuer die CanScan-Logik:
    /// - Kamera laeuft
    /// - Brickognize ist Online ODER Slow (Offline blockiert Scan)
    /// - Nicht gerade ein Scan in Arbeit
    /// </summary>
    private void UpdateCanScan()
    {
        CanScan = IsCameraRunning
                  && !_isFrozen
                  && BrickognizeStatus is BrickognizeStatus.Online or BrickognizeStatus.Slow or BrickognizeStatus.Unknown;
    }

    /// <summary>
    /// Q-Taste: oeffnet den Mengen-Eingabe-Dialog fuer den letzten Scan
    /// ("Wieviele dieser Teile?"). Wird in Phase 5 (Modus B) implementiert.
    /// Aktuell Stub mit Toast.
    /// </summary>
    [RelayCommand]
    public void OpenQuantityDialog()
    {
        if (Keyboard.FocusedElement is TextBoxBase) return;
        _notifications.ShowInfo("Mengen-Dialog (Q) kommt in Phase 5.");
    }

    /// <summary>
    /// Strg+Z: macht den letzten Scan-Effekt rueckgaengig. Wird in Phase 5
    /// implementiert (Undo-System). Aktuell Stub mit Toast.
    /// </summary>
    [RelayCommand]
    public void UndoLastScan()
    {
        if (Keyboard.FocusedElement is TextBoxBase) return;
        _notifications.ShowInfo("Undo (Strg+Z) kommt in Phase 5.");
    }

    /// <summary>Schaltet den Scan-Modus um.</summary>
    [RelayCommand]
    public void SetMode(string modeName)
    {
        ScanMode = modeName.ToLowerInvariant() switch
        {
            "minifig" => ScanMode.Minifig,
            "part"    => ScanMode.Part,
            _         => ScanMode.Auto
        };
        Log.Information("Scan-Modus gewechselt: {Mode}", ScanMode);
    }

    [RelayCommand]
    public async Task PerformScanAsync()
    {
        // Sicherheits-Check fuer das KeyBinding "Leertaste = Scan":
        // Wenn der User gerade in einem Eingabe-Control tippt (z.B. Notiz-TextBox
        // in Phase 3+), soll das Drucken der Leertaste KEINEN Scan ausloesen,
        // sondern als Leerzeichen im Text landen. WPF leitet KeyBindings auch
        // bei TextBox-Fokus weiter – deshalb pruefen wir das hier explizit.
        // Wenn TextBox Fokus hat: still beenden, damit die TextBox-Default-Verarbeitung
        // (Leerzeichen einfuegen) durchlaeuft.
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            Log.Debug("Scan ueber Leertaste unterdrueckt (TextBox-Fokus)");
            return;
        }

        // Cooldown
        var cooldownMs = _settingsService.Current.ScanCooldownMs;
        var timeSinceLastScan = DateTime.Now - _lastScanTime;
        if (timeSinceLastScan.TotalMilliseconds < cooldownMs)
        {
            Log.Debug("Scan abgelehnt: Cooldown aktiv ({Remaining}ms)",
                cooldownMs - timeSinceLastScan.TotalMilliseconds);
            return;
        }

        if (!CanScan || _isFrozen) return;

        if (BrickognizeStatus == BrickognizeStatus.Offline)
        {
            _notifications.ShowError("Brickognize ist offline. Scannen deaktiviert.");
            return;
        }

        _lastScanTime = DateTime.Now;
        _isFrozen = true;
        UpdateCanScan();

        var snapshot = _cameraService.CaptureSnapshot();
        if (snapshot == null)
        {
            Log.Warning("Kein Frame fuer Schnappschuss verfuegbar");
            _isFrozen = false;
            UpdateCanScan();
            return;
        }

        Log.Information("Schnappschuss aufgenommen ({Size} Bytes), Modus={Mode}",
            snapshot.Length, ScanMode);
        ScanStatusText = "Bild eingefroren – sende an Brickognize...";

        // Bild im scans-Ordner archivieren
        await SaveScanImageAsync(snapshot);

        // Freeze-Anzeige (1 Sek) parallel zur API-Anfrage
        var freezeTask = Task.Delay(_settingsService.Current.FreezeFrameMs);

        BrickognizePrediction? prediction = null;
        try
        {
            prediction = ScanMode switch
            {
                ScanMode.Minifig => await _brickognizeClient.PredictMinifigAsync(snapshot),
                ScanMode.Part    => await _brickognizeClient.PredictPartAsync(snapshot),
                _                => await _brickognizeClient.PredictGenericAsync(snapshot)
            };

            // Auto-Modus Follow-up: Brickognize liefert Farben NUR am
            // /predict/parts/-Endpoint. Wenn IRGENDEINES der Top-Items ein Teil
            // ist (auch wenn der Top-Treffer eine Minifigur ist und Teile nur
            // auf Rang 2/3 stehen), machen wir einen zweiten Call um die Farbe
            // zusaetzlich zu erfassen. Der Call ist schnell (~50ms) und
            // wird im selben brickognize-debug.log mitgeschrieben.
            // Vorher (BUG): nur wenn der Top-Treffer ein Teil war – dann blieben
            // die Teile auf Rang 2/3 ohne Farbe und wurden in BL color=0
            // (oft schwarz) angezeigt.
            if (ScanMode == ScanMode.Auto
                && prediction.Items.Any(i => i.Type.Equals("part", StringComparison.OrdinalIgnoreCase))
                && (prediction.Colors == null || prediction.Colors.Count == 0))
            {
                try
                {
                    var partsResult = await _brickognizeClient.PredictPartAsync(snapshot);
                    if (partsResult.Colors is { Count: > 0 })
                    {
                        prediction.Colors = partsResult.Colors;
                        Log.Information("Auto-Mode Follow-up: {Count} Farben hinzugefuegt (mind. ein Teil in Top-3)",
                            partsResult.Colors.Count);
                    }
                }
                catch (Exception ex)
                {
                    // Follow-up-Fehler darf den Hauptfluss nicht killen
                    Log.Warning(ex, "Auto-Mode Follow-up fuer Farberkennung fehlgeschlagen");
                }
            }
        }
        catch (TaskCanceledException ex)
        {
            Log.Warning(ex, "Brickognize-Aufruf Timeout");
            _notifications.ShowError("Brickognize-Anfrage hat zu lange gedauert.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Brickognize-Aufruf fehlgeschlagen");
            _notifications.ShowError($"Brickognize-Fehler: {ex.Message}");
        }

        await freezeTask;

        if (prediction != null)
        {
            // Bei jedem Scan: vorigen BL-Lookup canceln + Detail-Views ausblenden.
            CancelPendingLookup();
            PendingMinifig = null;
            PendingPart = null;
            MinifigStatusText = string.Empty;

            BuildResultCards(prediction);
            await ResolveCardImagesAsync(prediction, knownColorId: null);
            await BuildColorMatchesAsync(prediction);

            // Nach dem Farb-Build: ALLE Karten neu aufloesen, damit auch
            // Karten 2 und 3 mit der erkannten Farbe gerendert werden.
            await RefreshAllCardsImagesAsync(prediction);

            // Hybrid-Lookup-Strategie:
            // - Wenn Top-Score >= AutoThreshold: SelectCard(0) automatisch
            //   triggert den BL-Lookup fuer den Top-Treffer.
            // - Sonst: keine Auto-Selection; User muss klicken um BL-Call auszuloesen.
            var topScore = prediction.Items.FirstOrDefault()?.Score ?? 0;
            var autoThreshold = _settingsService.Current.ScoreThresholdAuto;
            if (topScore >= autoThreshold && prediction.Items.Count > 0)
            {
                await SelectCardAsync(0);
            }
        }

        _isFrozen = false;
        UpdateCanScan();
        ScanStatusText = "Leertaste drücken zum Scannen";
    }

    /// <summary>
    /// Werte die Brickognize-Antwort aus und baue die Top-3-Karten auf.
    /// Logik gemaess CLAUDE.md / BRICKOGNIZE_API.md:
    ///   Score >= scoreThresholdAuto              -> Top-Treffer hervorgehoben (Auto-Akzept)
    ///   scoreThresholdMin .. scoreThresholdShowSelection-1 -> Auswahl-UI ohne Hervorhebung
    ///   Score &lt; scoreThresholdMin              -> Hinweis "nicht erkannt" + Such-Fallback
    /// Auch bei nur 1 Treffer wird die Auswahl-UI gezeigt wenn der Score &lt; ShowSelection.
    /// </summary>
    private void BuildResultCards(BrickognizePrediction prediction)
    {
        ResultCards.Clear();
        SelectedCardIndex = -1;
        // Alte Farb-Anzeige zuruecksetzen (BuildColorMatchesAsync setzt sie ggf. neu)
        RecognizedColors.Clear();
        HasColors = false;

        var s = _settingsService.Current;
        var thresholdAuto      = s.ScoreThresholdAuto;
        var thresholdShowSel   = s.ScoreThresholdShowSelection;
        var thresholdMin       = s.ScoreThresholdMin;

        if (prediction.Items.Count == 0)
        {
            ResultHeadlineText = "Nichts erkannt – bitte erneut versuchen oder manuelle Suche.";
            HasResults = true;
            ShowSearchFallback = true;
            return;
        }

        var top3 = prediction.Items.Take(3).ToList();
        var topScore = top3[0].Score;

        for (int i = 0; i < top3.Count; i++)
        {
            var item = top3[i];
            var type = ParseType(item.Type);
            var ids  = _idResolver.Resolve(item.ExternalSites, type);

            var card = new ScanResultCard
            {
                Rank = i + 1,
                Name = item.Name,
                ScoreText = (item.Score).ToString("P0", CultureInfo.CurrentCulture),
                Score = item.Score,
                TypeLabel = TypeToGermanLabel(type),
                ImageUrl = item.ImgUrl,
                Ids = ids,
                RawItem = item
            };

            // Hervorhebung NUR fuer den Top-Treffer und NUR wenn Score >= scoreThresholdAuto.
            // Zusaetzliche Regel aus CLAUDE.md: ab Score < ShowSelection IMMER Auswahl-UI –
            // d.h. selbst wenn der Top-Score >= Auto liegt, aber ShowSelection groesser ist
            // (sollte normal nicht sein, da Auto > ShowSelection als Default), trotzdem
            // hervorheben – aber Auswahl bleibt sichtbar.
            if (i == 0 && item.Score >= thresholdAuto)
            {
                card.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
                card.BorderThickness = 3;
                card.HighlightLabel = "Top-Treffer (auto-akzeptiert)";
            }

            ResultCards.Add(card);
        }

        // Headline-Text setzen
        if (topScore < thresholdMin)
        {
            ResultHeadlineText = $"Nicht sicher erkannt (Top-Score {topScore:P0}).";
            ShowSearchFallback = true;
        }
        else if (topScore >= thresholdAuto && topScore >= thresholdShowSel)
        {
            ResultHeadlineText = $"Top-Treffer mit {topScore:P0} – auto-akzeptiert.";
            ShowSearchFallback = false;
        }
        else
        {
            ResultHeadlineText = $"Bitte auswaehlen (Top-Score {topScore:P0}).";
            ShowSearchFallback = false;
        }

        HasResults = true;

        // Toast nur fuer "Nicht sicher" / "Bitte auswaehlen" – Auto-Akzept-Feedback
        // wird vom SelectCardAsync-Flow gesteuert (Detail-Lookup-Toast).
        if (topScore < thresholdMin)
        {
            _notifications.ShowWarning("Nicht sicher erkannt.");
        }
        else if (topScore < thresholdAuto)
        {
            _notifications.ShowInfo("Bitte aus den Top-3-Treffern auswaehlen.");
        }
    }

    /// <summary>
    /// Loest fuer jede Top-Card die Bild-URL via PartImageProvider auf.
    /// BrickLink-First, Fallback auf Brickognize-Render. Wird ohne Farbe aufgerufen
    /// (vor der Farb-Identifikation) – die Top-Card wird spaeter via
    /// RefreshTopCardImageAsync aktualisiert sobald die Farbe bekannt ist.
    /// </summary>
    private async Task ResolveCardImagesAsync(BrickognizePrediction prediction, int? knownColorId)
    {
        for (int i = 0; i < ResultCards.Count && i < prediction.Items.Count; i++)
        {
            var item = prediction.Items[i];
            try
            {
                var url = await _imageProvider.GetImageFileAsync(item, knownColorId);
                ResultCards[i].ImageUrl = url;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Konnte Bild fuer {Item} nicht aufloesen", item.Id);
                ResultCards[i].ImageUrl = item.ImgUrl;
            }
        }
    }

    /// <summary>
    /// Aktualisiert das Bild der Top-Card mit der gerade erkannten Farbe.
    /// Brickognize liefert in `colors[]` immer die "globale" Farbe – wir nehmen die mit
    /// dem hoechsten Score und uebersetzen ueber Catalog-Lookup auf eine Rebrickable-ID,
    /// die der ImageProvider per ColorMapping auf die BL-Color-ID umrechnet.
    /// </summary>
    private async Task RefreshAllCardsImagesAsync(BrickognizePrediction prediction)
    {
        if (ResultCards.Count == 0 || prediction.Items.Count == 0) return;

        // Beste erkannte Farbe (falls vorhanden) - gilt nur fuer Teile,
        // nicht fuer Minifigs/Sets (die werden ohne Farbe gerendert).
        int? recognizedColorId = null;
        if (RecognizedColors.Count > 0)
        {
            var top = RecognizedColors[0];
            if (top.CatalogId >= 0) recognizedColorId = top.CatalogId;
        }

        for (int i = 0; i < ResultCards.Count && i < prediction.Items.Count; i++)
        {
            var item = prediction.Items[i];
            var card = ResultCards[i];

            // Pro Karte je nach Typ entscheiden welche Farbe relevant ist:
            //   - Minifig/Set: keine Farbe (immer ohne)
            //   - Teil + Farbe erkannt: erkannte Farbe verwenden
            //   - Teil + keine Farbe: ohne Farbe
            int? colorForThisCard = item.Type.Equals("part", StringComparison.OrdinalIgnoreCase)
                ? recognizedColorId
                : null;

            try
            {
                var url = await _imageProvider.GetImageFileAsync(item, colorForThisCard);
                card.ImageUrl = url;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Konnte Bild fuer Karte {Rank} ({Item}) nicht aktualisieren", i + 1, item.Id);
            }
        }
    }

    /// <summary>
    /// Bricht einen ggf. laufenden BL-Lookup ab (z.B. wenn der User schnell
    /// auf eine andere Karte klickt oder der naechste Scan kommt).
    /// </summary>
    private void CancelPendingLookup()
    {
        try { _lookupCts?.Cancel(); } catch { /* ignore */ }
        _lookupCts?.Dispose();
        _lookupCts = null;
    }

    /// <summary>
    /// User klickt eine Top-3-Karte (oder Auto-Akzept fuer Karte 0).
    /// Setzt SelectedCardIndex, IsSelected-Flags + triggert je nach Type den BL-Lookup.
    /// </summary>
    [RelayCommand]
    public async Task SelectCardAsync(int index)
    {
        if (index < 0 || index >= ResultCards.Count) return;

        var prevIndex = SelectedCardIndex;
        SelectedCardIndex = index;
        UpdateSelectedFlags();

        var card = ResultCards[index];

        // Headline aktualisieren
        var s = _settingsService.Current;
        var topScore = ResultCards.FirstOrDefault()?.Score ?? 0;
        if (index == 0 && topScore >= s.ScoreThresholdAuto)
        {
            ResultHeadlineText = $"Top-Treffer mit {topScore:P0} – auto-akzeptiert.";
        }
        else
        {
            ResultHeadlineText = $"Treffer {index + 1} mit {card.Score:P0} – manuell ausgewaehlt.";
            // Toast bei manuellem Wechsel (nicht bei Auto-Akzept und nicht bei
            // unveraendertem Index)
            if (prevIndex >= 0 && prevIndex != index)
            {
                var blInfo = string.IsNullOrEmpty(card.Ids.BricklinkId)
                    ? "(keine BL-ID)"
                    : $"BL: {card.Ids.BricklinkId}";
                _notifications.ShowInfo($"Wechsel zu '{card.Name}' ({blInfo})");
            }
        }

        // Type-spezifische Logik
        var cardType = card.RawItem?.Type?.ToLowerInvariant();
        switch (cardType)
        {
            case "fig":
                PendingPart = null;
                await LookupMinifigForCardAsync(card);
                break;
            case "part":
                PendingMinifig = null;
                MinifigStatusText = string.Empty;
                await LookupPartForCardAsync(card);
                break;
            case "set":
            case "sticker":
                _notifications.ShowInfo($"{TypeToGermanLabel(ParseType(card.RawItem!.Type))}-Workflow nicht unterstuetzt.");
                PendingMinifig = null;
                PendingPart = null;
                MinifigStatusText = string.Empty;
                break;
            default:
                PendingMinifig = null;
                PendingPart = null;
                MinifigStatusText = string.Empty;
                break;
        }
    }

    /// <summary>
    /// Setzt IsSelected auf jeder Card abhaengig von SelectedCardIndex.
    /// </summary>
    private void UpdateSelectedFlags()
    {
        for (int i = 0; i < ResultCards.Count; i++)
        {
            ResultCards[i].IsSelected = (i == SelectedCardIndex);
        }
    }

    /// <summary>
    /// BL-Lookup fuer die geklickte Minifig-Card. Holt Details + Subsets parallel
    /// ueber den BlCatalogService (Cache-First). Nutzt _lookupCts damit ein vorheriger
    /// Lookup beim schnellen Click-Through abgebrochen wird.
    /// </summary>
    private async Task LookupMinifigForCardAsync(ScanResultCard card)
    {
        // BL-ID muss vorhanden sein
        var bricklinkId = card.Ids.BricklinkId ?? card.RawItem?.Id;
        if (string.IsNullOrEmpty(bricklinkId))
        {
            _notifications.ShowWarning("Keine BL-Referenz – Lookup nicht moeglich.");
            PendingMinifig = null;
            MinifigStatusText = "Keine BL-Referenz fuer diese Karte.";
            return;
        }

        // Vorigen Lookup canceln + neuen Token vorbereiten
        CancelPendingLookup();
        _lookupCts = new CancellationTokenSource();
        var ct = _lookupCts.Token;

        // Pending-Anzeige sofort sichtbar machen mit IsLoading
        var pendingShell = new PendingMinifigViewModel
        {
            BricklinkId = bricklinkId,
            Name = card.Name,
            IsLoading = true
        };
        PendingMinifig = pendingShell;
        MinifigStatusText = $"BL-Catalog wird geladen ({bricklinkId})...";

        try
        {
            var detailsTask = _blCatalog.GetMinifigDetailsAsync(bricklinkId, ct);
            var partsTask = _blCatalog.GetMinifigPartsAsync(bricklinkId, ct);
            var colorsTask = _blCatalog.GetAllColorsAsync(ct);
            await Task.WhenAll(detailsTask, partsTask, colorsTask);

            ct.ThrowIfCancellationRequested();

            var details = detailsTask.Result;
            var subsets = partsTask.Result;
            var colors = colorsTask.Result;
            var colorLookup = colors.ToDictionary(c => c.ColorId);

            if (details == null)
            {
                MinifigStatusText = $"Minifigur '{bricklinkId}' wurde in BL nicht gefunden.";
                _notifications.ShowWarning(MinifigStatusText);
                PendingMinifig = null;
                return;
            }

            var pending = new PendingMinifigViewModel
            {
                BricklinkId = bricklinkId,
                Name = details.Name,
                YearReleased = details.YearReleased,
                NumParts = subsets.Count
            };

            foreach (var subset in subsets)
            {
                colorLookup.TryGetValue(subset.ColorId, out var color);
                var itemName = await GetCachedItemNameOrEmptyAsync(subset.ItemType, subset.ItemNo);
                pending.Parts.Add(PendingPartViewModel.FromSubset(subset, color, itemName));
            }

            // Wenn der Token zwischenzeitlich gecancelt wurde -> nicht ueberschreiben
            if (ct.IsCancellationRequested) return;

            PendingMinifig = pending;

            if (subsets.Count == 0)
            {
                MinifigStatusText = $"Fuer '{bricklinkId}' sind keine Teile in BL gelistet.";
                _notifications.ShowWarning(MinifigStatusText);
            }
            else
            {
                MinifigStatusText = string.Empty;
                _notifications.ShowSuccess($"Minifigur: {details.Name} ({subsets.Count} Teile)");
            }

            // Header-Bild + Vorab-Cache im Hintergrund (best-effort)
            _ = LoadMinifigHeaderImageAsync(bricklinkId);
            if (_settingsService.Current.ImageCache.PreloadOnMinifigScan)
            {
                _ = PreloadPartImagesAsync(pending);
            }

            // Phase 4: Lagerfach-Liste fuer die ComboBox laden + Default setzen.
            _ = LoadAvailableBinsForPendingAsync(pending);
        }
        catch (OperationCanceledException)
        {
            // Vom User durch neuen Klick / neuen Scan abgebrochen – kein Toast.
            Log.Debug("BL-Lookup fuer {Id} abgebrochen", bricklinkId);
        }
        catch (BricklinkAuthException ex)
        {
            Log.Warning(ex, "BL-Auth-Fehler beim Minifig-Lookup");
            MinifigStatusText = "BrickLink-Tokens fehlen oder sind ungueltig. Settings -> BrickLink-API pruefen.";
            _notifications.ShowError(MinifigStatusText);
            PendingMinifig = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Minifig-Lookup fehlgeschlagen");
            MinifigStatusText = $"Fehler beim BL-Catalog-Lookup: {ex.Message}";
            _notifications.ShowError(MinifigStatusText);
            PendingMinifig = null;
        }
        finally
        {
            // Loading-Flag ausschalten falls die Pending-VM noch existiert
            if (PendingMinifig != null) PendingMinifig.IsLoading = false;
        }
    }

    /// <summary>
    /// Modus B: Lookup eines erkannten Teils. Nutzt die im RecognizedColors[0]
    /// stehende Farbe (vom letzten Brickognize-Call), oder 0 wenn keine erkannt
    /// wurde. Befuellt PendingPart und triggert Bild + KnownColors + Bin-Liste
    /// im Hintergrund.
    /// </summary>
    private async Task LookupPartForCardAsync(ScanResultCard card)
    {
        var blPartNo = card.Ids.BricklinkId ?? card.RawItem?.Id;
        if (string.IsNullOrEmpty(blPartNo))
        {
            _notifications.ShowWarning("Keine BL-Part-No – Teile-Lookup nicht moeglich.");
            return;
        }

        // Color: BL-ID aus RecognizedColors[0] (Mapping ist schon erfolgt).
        // Fallback 0 = "no color" wenn Brickognize keine Farbe erkannt hat.
        int blColorId = 0;
        if (RecognizedColors.Count > 0 && RecognizedColors[0].BricklinkId.HasValue)
            blColorId = RecognizedColors[0].BricklinkId!.Value;

        var pending = new PartLookupViewModel(blPartNo, blColorId);
        PendingPart = pending;

        // Initial-Lookup
        try
        {
            var result = await _partLookup.LookupPartAsync(blPartNo, blColorId);
            pending.ApplyLookupResult(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Part-Lookup fehlgeschlagen ({Part}/{Color})", blPartNo, blColorId);
            _notifications.ShowError($"Part-Lookup fehlgeschlagen: {ex.Message}");
        }

        // Bild im Hintergrund (best-effort, nutzt BL-Color)
        _ = LoadPartImageAsync(pending);

        // Lagerfaecher fuer Floating-Combo laden
        _ = LoadAvailableBinsForPendingPartAsync(pending);

        // Known-Colors fuer Korrektur-Dropdown laden
        _ = LoadKnownColorsForPendingPartAsync(pending);

        // BL-Catalog-Treffer-Bilder im Hintergrund nachladen
        _ = LoadBlCatalogImagesAsync(pending);
    }

    /// <summary>Re-Lookup nach Farb-Korrektur ueber das Dropdown.</summary>
    public async Task RefreshPartLookupForColorAsync(int newBlColorId)
    {
        if (PendingPart == null) return;
        var pending = PendingPart;

        try
        {
            var result = await _partLookup.LookupPartAsync(pending.BlPartNo, newBlColorId);
            pending.ApplyLookupResult(result);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Re-Lookup nach Farb-Korrektur fehlgeschlagen");
        }

        _ = LoadPartImageAsync(pending);
        _ = LoadBlCatalogImagesAsync(pending);
    }

    /// <summary>
    /// Best-effort: fuer jeden BL-Catalog-Treffer in der PartLookupView das
    /// Minifig-Bild via PartImageProvider laden falls noch nicht da.
    /// Aenderungen werden auf den UI-Thread gepostet (ImageUrl ist ObservableProperty).
    /// </summary>
    private async Task LoadBlCatalogImagesAsync(PartLookupViewModel pending)
    {
        foreach (var match in pending.BlCatalogMatches.ToList())
        {
            if (!string.IsNullOrEmpty(match.ImageUrl)) continue;
            try
            {
                var url = await _imageProvider.GetImageFileByBlAsync("M", match.BlMinifigId, null);
                if (!string.IsNullOrEmpty(url))
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => match.ImageUrl = url);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "BL-Catalog-Bild fuer {Bl} nicht ladbar", match.BlMinifigId);
            }
        }
    }

    private async Task LoadPartImageAsync(PartLookupViewModel pending)
    {
        try
        {
            var url = await _imageProvider.GetImageFileByBlAsync(
                "P", pending.BlPartNo, pending.BlColorId == 0 ? null : pending.BlColorId);
            pending.ImageUrl = url;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Konnte Part-Bild nicht laden ({Part}/{Color})", pending.BlPartNo, pending.BlColorId);
        }
    }

    private async Task LoadAvailableBinsForPendingPartAsync(PartLookupViewModel pending)
    {
        try
        {
            var allBins = await _binService.GetAllAsync();
            var firstFree = await _binService.GetNextFreeAsync();
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                pending.AvailableBins.Clear();
                foreach (var b in allBins) pending.AvailableBins.Add(b);

                // WICHTIG: Bin-Objekt aus AvailableBins holen (gleiche Id),
                // nicht das separate firstFree-Objekt verwenden, sonst findet
                // die ComboBox den Eintrag nicht in ItemsSource (Reference-Equality).
                pending.SelectedFloatingBin = firstFree != null
                    ? pending.AvailableBins.FirstOrDefault(b => b.Id == firstFree.Id)
                      ?? pending.AvailableBins.FirstOrDefault()
                    : pending.AvailableBins.FirstOrDefault();

                Log.Information("PartLookup-Bins: {Count} Faecher, FirstFree.Id={Free}, Selected.Id={Sel}",
                    pending.AvailableBins.Count, firstFree?.Id, pending.SelectedFloatingBin?.Id);
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Konnte Lagerfaecher fuer Pending-Part nicht laden");
        }
    }

    private async Task LoadKnownColorsForPendingPartAsync(PartLookupViewModel pending)
    {
        pending.IsLoadingColors = true;
        try
        {
            var known = await _blCatalog.GetKnownColorsAsync(pending.BlPartNo);
            // Fallback: wenn leer (Edge-Case ohne BL-Tokens / Hard-Stop), alle BL-Farben anbieten
            if (known.Count == 0)
            {
                known = await _blCatalog.GetAllColorsAsync();
                Log.Debug("KnownColors leer – Fallback auf {Count} alle BL-Farben", known.Count);
            }

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                pending.AvailableColors.Clear();
                foreach (var c in known.OrderBy(c => c.Name))
                    pending.AvailableColors.Add(c);
                // Aktuelle Farbe selektieren
                pending.SelectedColor = pending.AvailableColors.FirstOrDefault(c => c.ColorId == pending.BlColorId)
                                       ?? pending.AvailableColors.FirstOrDefault();
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Konnte KnownColors nicht laden");
        }
        finally
        {
            pending.IsLoadingColors = false;
        }
    }

    /// <summary>Holt den Item-Namen aus dem bl_items-Cache (z.B. "Plate 1 x 1"). Leer wenn unbekannt.</summary>
    private async Task<string> GetCachedItemNameOrEmptyAsync(string itemType, string itemNo)
    {
        try
        {
            var item = await _blCatalog.GetPartDetailsAsync(itemNo);
            return item?.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Laed das Minifig-Header-Bild ueber den IPartImageProvider mit BL-Direct-API.
    /// </summary>
    private async Task LoadMinifigHeaderImageAsync(string bricklinkId)
    {
        try
        {
            var url = await _imageProvider.GetImageFileByBlAsync("M", bricklinkId, bricklinkColorId: null);
            if (PendingMinifig != null) PendingMinifig.ImageUrl = url;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Konnte Minifig-Header-Bild nicht laden ({Id})", bricklinkId);
        }
    }

    /// <summary>
    /// Laed die verfuegbaren Lagerfaecher in die PendingMinifig-VM und waehlt
    /// das erste freie als Default. Falls keins frei: erstes Fach generell.
    /// Wird direkt nach dem BL-Lookup aufgerufen.
    /// </summary>
    private async Task LoadAvailableBinsForPendingAsync(PendingMinifigViewModel pending)
    {
        try
        {
            var allBins = await _binService.GetAllAsync();
            var firstFree = await _binService.GetNextFreeAsync();

            // ComboBox auf UI-Thread befuellen
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                pending.AvailableBins.Clear();
                foreach (var bin in allBins)
                    pending.AvailableBins.Add(bin);

                // WICHTIG: Bin-Objekt aus AvailableBins holen (gleiche Id),
                // nicht das separate firstFree-Objekt verwenden, sonst findet
                // die ComboBox den Eintrag nicht in ItemsSource (Reference-Equality).
                pending.SelectedBin = firstFree != null
                    ? pending.AvailableBins.FirstOrDefault(b => b.Id == firstFree.Id)
                      ?? pending.AvailableBins.FirstOrDefault()
                    : pending.AvailableBins.FirstOrDefault();
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Konnte Lagerfaecher fuer Pending-Minifig nicht laden");
        }
    }

    /// <summary>
    /// Persistiert die aktuell angezeigte Pending-Minifig in die DB:
    /// 1. Speichert Figur + RequiredParts mit dem gewaehlten Fach
    /// 2. Reverse-Match: passende FloatingParts werden konsumiert
    /// 3. Bei kompletter Figur: Status=Complete, Toast
    /// 4. Pending wird ausgeblendet
    /// </summary>
    [RelayCommand]
    public async Task PersistPendingAsync()
    {
        var pending = PendingMinifig;
        if (pending == null) return;
        if (pending.SelectedBin == null)
        {
            _notifications.ShowWarning("Bitte ein Lagerfach auswaehlen.");
            return;
        }

        // Bin-Belegung pruefen + Bestaetigung wenn nicht frei
        try
        {
            var occ = await _binService.GetOccupancyAsync(pending.SelectedBin.Id);
            if (occ.MinifigCount > 0 || occ.FloatingPartCount > 0)
            {
                var msg = $"Fach '{pending.SelectedBin.Label}' ist bereits belegt: " +
                          $"{occ.MinifigCount} Figur(en), {occ.FloatingPartCount} Teil(e).\n\n" +
                          "Mehrere Figuren in einem Fach sind erlaubt – willst du fortfahren?";
                var result = System.Windows.MessageBox.Show(msg, "Lagerfach belegt",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);
                if (result != System.Windows.MessageBoxResult.Yes) return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Bin-Occupancy-Check fehlgeschlagen, fahre trotzdem fort");
        }

        pending.IsPersisting = true;
        try
        {
            // Required-Parts aus den Pending-Parts extrahieren
            var input = new PersistMinifigInput
            {
                BricklinkId = pending.BricklinkId,
                Name = pending.Name,
                ImageUrl = pending.ImageUrl,
                LocalImagePath = pending.ImageUrl, // BL-Provider liefert Pfad als URL-Form
                UserNotes = pending.UserNotes,
                StorageBinId = pending.SelectedBin.Id,
                RequiredParts = pending.Parts.Select(p => new PersistMinifigPart
                {
                    BricklinkPartNo = p.BricklinkPartNo,
                    BricklinkColorId = p.BricklinkColorId,
                    PartName = p.PartName,
                    ColorName = p.ColorName,
                    QuantityNeeded = p.Quantity,
                    // Manuelle Markierung im Pending: "Habe ich" -> alle Teile dieser
                    // Sorte als gesammelt. Reverse-Match laeuft danach (skipt volle Eintraege).
                    QuantityCollected = p.IsCollected ? p.Quantity : 0
                }).ToList()
            };

            var result = await _persistenceService.PersistAndStoreAsync(input);

            // Toast je nach Ergebnis
            if (result.IsFullyComplete)
            {
                _notifications.ShowSuccess(
                    $"Figur '{pending.Name}' direkt komplett (alle Teile bereits im Pool).");
            }
            else if (result.ReverseMatchedFloating > 0)
            {
                _notifications.ShowSuccess(
                    $"Figur '{pending.Name}' im Fach '{pending.SelectedBin.Label}' angelegt. " +
                    $"{result.ReverseMatchedFloating} passende Teil(e) aus dem Pool uebernommen.");
            }
            else
            {
                _notifications.ShowSuccess(
                    $"Figur '{pending.Name}' im Fach '{pending.SelectedBin.Label}' angelegt.");
            }

            // Pending ausblenden, Top-3 wiederherstellen
            PendingMinifig = null;
            MinifigStatusText = string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PersistPending fehlgeschlagen");
            _notifications.ShowError($"Fehler beim Speichern: {ex.Message}");
        }
        finally
        {
            if (PendingMinifig != null) PendingMinifig.IsPersisting = false;
        }
    }

    /// <summary>
    /// Laed die Teile-Bilder einer erkannten Minifigur im Hintergrund vor.
    /// SemaphoreSlim(5) als Throttle. Best-effort – einzelne Fehler werden nur geloggt.
    /// </summary>
    private async Task PreloadPartImagesAsync(PendingMinifigViewModel pending)
    {
        using var throttle = new System.Threading.SemaphoreSlim(initialCount: 5);
        var tasks = pending.Parts.Select(async part =>
        {
            await throttle.WaitAsync();
            try
            {
                // Direkt mit BL-IDs – kein Brickognize-Wrapper noetig.
                var url = await _imageProvider.GetImageFileByBlAsync("P", part.BricklinkPartNo, part.BricklinkColorId);

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    part.ImageUrl = url;
                });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Vorab-Cache fuer Teil {Part}/{Color} fehlgeschlagen",
                    part.BricklinkPartNo, part.BricklinkColorId);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
        Log.Information("Vorab-Cache abgeschlossen ({Count} Teile)", pending.Parts.Count);
    }

    /// <summary>
    /// Wertet die `colors`-Liste der Brickognize-Antwort aus und schlaegt zu jedem
    /// Eintrag die RGB-Farbe in der catalog.db nach (fuer das Swatch in der UI).
    /// </summary>
    private async Task BuildColorMatchesAsync(BrickognizePrediction prediction)
    {
        RecognizedColors.Clear();

        if (prediction.Colors == null || prediction.Colors.Count == 0)
        {
            HasColors = false;
            return;
        }

        // Top-3 Farben anzeigen (mehr ist normalerweise nicht im Bild).
        //
        // WICHTIG (validiert 2026-04-30): Brickognize liefert in der "id"-Spalte
        // NICHT die Rebrickable-Color-ID, sondern wahrscheinlich BrickLink-IDs
        // (BL 3 = Yellow != Rebrickable 3 = Dark Turquoise). Wir matchen daher
        // ueber den NAMEN gegen unsere catalog.db. Brickognize-Name "Yellow" ->
        // Rebrickable-Eintrag mit name="Yellow" -> korrekte ID + RGB.
        foreach (var raw in prediction.Colors.OrderByDescending(c => c.Score).Take(3))
        {
            // Catalog-Suche per Name (case-insensitive). Bei Treffer kriegen wir
            // RGB + die echte Rebrickable-ID (fuer spaetere Matching-Logik).
            var catalogColor = await _catalogService.GetColorByNameAsync(raw.Name);

            // Fallback: kein Treffer im Catalog -> Brickognize-Name behalten,
            // graues Swatch, ID = -1 (markiert "unbekannte Farbe").
            var resolvedId = catalogColor?.Id ?? -1;

            RecognizedColors.Add(ColorMatch.FromCatalogAndScore(resolvedId, raw.Name, raw.Score, catalogColor));
        }

        HasColors = RecognizedColors.Count > 0;
    }

    private static BrickognizeItemType ParseType(string raw) => raw.ToLowerInvariant() switch
    {
        "part"    => BrickognizeItemType.Part,
        "fig"     => BrickognizeItemType.Minifig,
        "set"     => BrickognizeItemType.Set,
        "sticker" => BrickognizeItemType.Sticker,
        _         => BrickognizeItemType.Unknown
    };

    private static string TypeToGermanLabel(BrickognizeItemType type) => type switch
    {
        BrickognizeItemType.Minifig => "Minifigur",
        BrickognizeItemType.Part    => "Teil",
        BrickognizeItemType.Set     => "Set",
        BrickognizeItemType.Sticker => "Sticker",
        _                            => "Unbekannt"
    };

    [RelayCommand]
    public async Task SwitchCameraAsync()
    {
        if (SelectedCameraIndex >= 0 && SelectedCameraIndex < AvailableCameras.Count)
        {
            _settingsService.Current.SelectedCameraIndex = SelectedCameraIndex;
            await _settingsService.SaveAsync();
            await StartCameraAsync(SelectedCameraIndex);
        }
    }

    private void OnFrameReceived(byte[] jpegBytes)
    {
        if (_isFrozen) return;

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            CurrentFrame = ConvertToBitmapImage(jpegBytes);
        });
    }

    private static BitmapImage ConvertToBitmapImage(byte[] jpegBytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(jpegBytes);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static async Task SaveScanImageAsync(byte[] imageBytes)
    {
        var scansFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LegoMinifigSorter", "scans");

        var fileName = $"scan_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
        var filePath = Path.Combine(scansFolder, fileName);

        await File.WriteAllBytesAsync(filePath, imageBytes);

        var files = Directory.GetFiles(scansFolder, "scan_*.jpg")
            .OrderByDescending(f => f)
            .Skip(100)
            .ToList();

        foreach (var oldFile in files)
        {
            try { File.Delete(oldFile); }
            catch (Exception ex) { Log.Warning(ex, "Konnte altes Scan-Bild nicht loeschen: {Path}", oldFile); }
        }
    }

    public void StopCamera()
    {
        _cameraService.Stop();
        IsCameraRunning = false;
        UpdateCanScan();
        CurrentFrame = null;
        ScanStatusText = "Kamera gestoppt";
    }
}

/// <summary>Scan-Modus: bestimmt welcher Brickognize-Endpoint angesprochen wird.</summary>
public enum ScanMode
{
    /// <summary>Generischer /predict/ – wenn unklar.</summary>
    Auto,
    /// <summary>/predict/figs/ – Minifiguren.</summary>
    Minifig,
    /// <summary>/predict/parts/?predict_color=true – Teile + Farbe.</summary>
    Part
}
