using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Helpers;
using HBSort.Core.Models;
using HBSort.Core.Models.Exceptions;
using HBSort.Core.Services;
using HBSort.Helpers;
using HBSort.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel fuer die Scan-Ansicht (Hauptansicht der App).
///
/// Phase 2: Brickognize-Integration.
///   - Modus-Auswahl (Figur / Teil / Auto)
///   - Schnappschuss + API-Aufruf an passenden Endpoint
///   - Top-3-Karten mit Score-Auswertung und Auto-Akzept-Hervorhebung
///   - Status-Anzeige (Online / Slow / Offline)
/// </summary>
public partial class ScanViewModel : ObservableObject, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly ISettingsService _settingsService;
    private readonly IBrickognizeClient _brickognizeClient;
    private readonly IExternalIdResolver _idResolver;
    private readonly INotificationService _notifications;
    private readonly IBlCacheRepository _blCacheRepo;     // bl_colors fuer Farb-Swatches
    private readonly IPartImageProvider _imageProvider;
    private readonly IPersistentImageCache _persistentCache;
    private readonly IBlCatalogService _blCatalog;
    private readonly IStorageBinService _binService;
    private readonly IMinifigPersistenceService _persistenceService;
    private readonly IPartLookupService _partLookup;
    private readonly IDialogService _dialogs;
    private readonly IFloatingPartTransferService _floatingTransfer;
    private readonly IBlPriceCacheService _priceCache;

    private DateTime _lastScanTime = DateTime.MinValue;
    private bool _isFrozen = false;

    /// <summary>
    /// Cancellation-Token fuer den aktuell laufenden BL-Lookup. Beim Klick auf
    /// eine andere Karte (oder beim naechsten Scan) wird der vorige Lookup gecancelt.
    /// </summary>
    private CancellationTokenSource? _lookupCts;

    /// <summary>
    /// v0.1.23-beta.2 Fix C: CTS fuer den Preload-Part-Image-Hintergrund-Lauf.
    /// Vor jedem neuen Minifig-Scan wird der laufende Lauf abgebrochen, damit
    /// kein alter Loop noch via Dispatcher den UI-Thread bedient nachdem der
    /// User schon eine andere Figur fokussiert hat.
    /// </summary>
    private CancellationTokenSource? _preloadPartImagesCts;

    /// <summary>
    /// v0.1.23-beta.2 Fix C: CTS fuer den BL-Catalog-Match-Image-Loader im
    /// Part-Lookup-Modus. Wird bei jedem Scan / Re-Lookup neu gestartet,
    /// vorher gecancelt - das ist der wichtigste Hotpath fuer das Save-
    /// Reaktivitaets-Problem aus Befund Performance v0.1.23-beta.1.
    /// </summary>
    private CancellationTokenSource? _blCatalogImagesCts;

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
    /// gezeigt wird - beide Detail-Views machen die Top-Treffer-Info redundant.
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

    /// <summary>
    /// UX#12: Preis-VM fuer die obere rechte Box. Wird beim Pending-Aufbau
    /// erzeugt und beim Verwerfen/Speichern auf null gesetzt. Visibility in
    /// der View ueber das Vorhandensein gebunden.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMinifigPriceInfo))]
    private MinifigPriceViewModel? _priceInfo;

    public bool HasMinifigPriceInfo => PriceInfo != null;

    /// <summary>
    /// Wenn die Pending-Minifig auf null wechselt (Verwerfen/Persistieren),
    /// raeumen wir auch die Preis-Anzeige weg. Der "Aufbau" findet beim
    /// PendingMinifig-Setzen explizit statt - hier nur das Cleanup.
    /// </summary>
    partial void OnPendingMinifigChanged(PendingMinifigViewModel? oldValue, PendingMinifigViewModel? newValue)
    {
        // UX X.31 Block B Bug-Fix v2 (v0.1.18): Live-Bin-Re-Suggest beim Toggeln
        // der Teile-Haekchen. PendingMinifigViewModel feuert bereits WillBeComplete
        // PropertyChanged bei jeder Teil-Aenderung (siehe RaiseCollectedDerived);
        // wir hoeren hier zu und wechseln den Bin-Vorschlag zwischen Waiting- und
        // Complete-Pfad.
        if (oldValue != null) oldValue.PropertyChanged -= OnPendingMinifigPropertyChanged;
        if (newValue != null) newValue.PropertyChanged += OnPendingMinifigPropertyChanged;

        if (newValue == null) PriceInfo = null;
    }

    /// <summary>
    /// UX X.31 Block B Bug-Fix v2 (v0.1.18): hoert auf WillBeComplete-Wechsel
    /// am Pending und stupst den Bin-Vorschlag an. Subscriber wird in
    /// OnPendingMinifigChanged gemanaged.
    /// </summary>
    private async void OnPendingMinifigPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PendingMinifigViewModel.WillBeComplete)) return;
        if (sender is not PendingMinifigViewModel pending) return;
        await ReSuggestBinForPendingAsync(pending);
    }

    /// <summary>
    /// Re-evaluiert den Bin-Vorschlag basierend auf pending.WillBeComplete und
    /// wechselt das SelectedBin im Dropdown wenn ein besserer Vorschlag
    /// existiert. UX X.31 Block B Bug-Fix v2 (v0.1.18) - "pragmatischster
    /// Ansatz": immer wechseln. Manuelles Wechseln durch den User triggert
    /// diese Logik nicht (nur WillBeComplete-Wechsel tut das).
    /// </summary>
    private async Task ReSuggestBinForPendingAsync(PendingMinifigViewModel pending)
    {
        try
        {
            StorageBin? newBest;
            if (pending.WillBeComplete)
            {
                var maxComplete = _settingsService.Current.MaxCompleteFiguresPerBin;
                newBest = await _binService.SuggestBinForCompleteMinifigAsync(maxComplete);
                Log.Information("Re-Suggest (Complete-Pfad, Limit={Limit}): {Bin}",
                    maxComplete, newBest?.Label ?? "(keiner)");
            }
            else
            {
                // UX X.32 v0.1.19-beta.4: Limit aus Settings reicht den
                // "Stapel-mit-bestehenden-Wartenden"-Pfad an den Service.
                var maxWaiting = _settingsService.Current.MaxWaitingFiguresPerBin;
                newBest = await _binService.SuggestBinForWaitingMinifigAsync(maxWaiting);
                Log.Information("Re-Suggest (Waiting-Pfad, MaxWaiting={Max}): {Bin}",
                    maxWaiting, newBest?.Label ?? "(keiner)");
            }

            if (newBest == null) return;

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                // v0.1.24-beta.1 Phase 2b: BinDisplayItem statt rohes StorageBin —
                // .Id (Convenience an BinDisplayItem) bleibt unveraendert.
                var newBin = pending.AvailableBins.FirstOrDefault(b => b.Id == newBest.Id);
                if (newBin != null && newBin.Id != pending.SelectedBin?.Id)
                {
                    pending.SelectedBin = newBin;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Re-Suggest-Bin-Logik fehlgeschlagen");
        }
    }

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

    // ====================================================================
    // UX X.31 (v0.1.18) Block B: Bin-Anweisungs-Overlay
    //
    // Nach erfolgreichem Persist (Minifig oder Floating-Part) wird ein
    // mittiges, prominentes Overlay angezeigt: "Lege diese Figur in
    // Box 005" + Item-Bild. Schliesst per Enter / Leertaste / Klick /
    // 8s-Timeout. Damit weiss der User nach der Tastatur-Aktion
    // (Enter-Hotkey) eindeutig wo das Item physisch hin soll.
    //
    // Implementation in BinInstructionViewModel ausgelagert, damit das
    // Verhalten ohne ScanViewModel-Mock-Stack testbar ist. Hier nur die
    // Timer-Verwaltung (DispatcherTimer braucht WPF-Dispatcher).
    // ====================================================================

    // v0.1.22-beta.1 Block F: konsolidiert. Eine VM mit Mode-Switch
    // (Single + Group) statt zwei separate Klassen + zwei separate
    // ScanViewModel-Properties. Konsumenten nutzen ShowBinInstruction (Single,
    // mit Auto-Dismiss-Timer) oder ShowBinInstructionGroup (Group, ohne
    // Auto-Dismiss).
    public BinInstructionViewModel BinInstruction { get; } = new();

    /// <summary>
    /// Convenience-Property fuer XAML-DataTrigger-Bindings die Visibility
    /// am Overlay steuern (Mode-Switch passiert intern in der VM).
    /// </summary>
    public bool IsBinInstructionVisible => BinInstruction.IsVisible;

    /// <summary>
    /// Zeigt das Sammel-Popup mit mehreren "Lege X in Y"-Anweisungen
    /// (Group-Mode). UX X.32 v0.1.19-beta.5: <paramref name="headerText"/>
    /// kann gesetzt werden um den Default ("Lege folgende Teile...") zu
    /// ueberschreiben - z.B. fuer den BuildSuggestion-Pfad ("Nimm folgende
    /// Teile...").
    /// </summary>
    public void ShowBinInstructionGroup(
        IEnumerable<BinInstructionItem> items,
        string? headerText = null)
    {
        BinInstruction.ShowGroup(items, headerText);
        OnPropertyChanged(nameof(IsBinInstructionVisible));
    }

    /// <summary>
    /// v0.1.24-beta.1 (Konzept 4.3 Rev. 3): zeigt das einheitliche Post-Save-
    /// Modal mit Take/Put/Plus-Sektionen. Wrapper um
    /// <see cref="BinInstructionViewModel.ShowSortInstruction"/>. Wird von
    /// allen 6 Sortier-Triggers nach erfolgreichem Persist gerufen (Bulk-
    /// Move, DismantleWizard, Single-Move, StoreFloating,
    /// Reverse-Match-Konsum, Wizard-Stufe-2-Save).
    ///
    /// Der DispatcherPriority.Render-Wrapper lebt im VM (Performance-Pattern
    /// aus Konzept 4.3.8.1) — Aufrufer brauchen sich darum nicht zu kuemmern.
    /// </summary>
    public void ShowSortInstruction(SortInstruction instruction)
    {
        BinInstruction.ShowSortInstruction(instruction);
        OnPropertyChanged(nameof(IsBinInstructionVisible));
    }

    private System.Windows.Threading.DispatcherTimer? _binInstructionTimer;

    /// <summary>
    /// Zeigt das Anweisungs-Overlay im Single-Mode mit Bin-Label + Item-
    /// Bild. Wird vom PersistPendingAsync-Pfad sowie vom Einzelteil-Lager-
    /// Pfad (PartLookupView.StoreFloating_Click) aufgerufen. Beendet sich
    /// nach 8 Sekunden automatisch.
    /// </summary>
    public void ShowBinInstruction(string binLabel, string? imageUrl)
    {
        BinInstruction.ShowSingle(binLabel, imageUrl);
        OnPropertyChanged(nameof(IsBinInstructionVisible));

        // Bestehenden Timer aus einem vorherigen Aufruf abbrechen.
        // DispatcherTimer braucht einen WPF-Dispatcher; defensive Try-Catch
        // damit Tests/Headless-Pfade nicht stolpern.
        try
        {
            _binInstructionTimer?.Stop();
            _binInstructionTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8)
            };
            _binInstructionTimer.Tick += (_, _) =>
            {
                _binInstructionTimer?.Stop();
                DismissBinInstruction();
            };
            _binInstructionTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Auto-Dismiss-Timer konnte nicht gestartet werden");
        }
    }

    /// <summary>
    /// Schliesst das Anweisungs-Overlay (manuell durch Enter / Leertaste /
    /// Klick oder durch Timer).
    /// </summary>
    [RelayCommand]
    public void DismissBinInstruction()
    {
        try { _binInstructionTimer?.Stop(); } catch { /* Timer ohne Dispatcher */ }
        _binInstructionTimer = null;
        BinInstruction.Dismiss();
        OnPropertyChanged(nameof(IsBinInstructionVisible));
    }

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block M Erweiterung: Pending-Part lagern.
    /// Zentraler Pfad fuer "Lagern"-Button-Klick + Enter-Hotkey im
    /// MainWindow. Liefert true wenn das Lagern erfolgreich durchgelaufen
    /// ist (Toast + Anweisungs-Popup wurden gezeigt, PendingPart wurde
    /// auf null gesetzt). false bei Validierungs-Fehler oder Exception.
    /// </summary>
    public async Task<bool> StoreFloatingFromPendingPartAsync()
    {
        var pending = PendingPart;
        if (pending == null) return false;
        if (pending.SelectedFloatingBin == null)
        {
            _notifications.ShowWarning("Bitte ein Lagerfach auswaehlen.");
            return false;
        }
        if (pending.FloatingQuantity <= 0)
        {
            _notifications.ShowWarning("Anzahl muss > 0 sein.");
            return false;
        }

        pending.IsBusy = true;
        try
        {
            await _partLookup.AddPartToFloatingAsync(
                pending.BlPartNo, pending.BlColorId,
                pending.PartName, pending.ColorName,
                pending.FloatingQuantity, pending.SelectedFloatingBin.Bin.Id,
                pending.BrickognizeCategory);

            // UX X.33 Block N (v0.1.19-beta.7 Refactor): Auto-Mapping
            // entfernt. Settings-Tab "Kategorien" befuellt sich nicht mehr
            // automatisch beim Lagern - User pflegt Mapping nur manuell.
            // Default-Regel ("max 1 PartId pro Kategorie pro Bin") greift
            // wenn kein Mapping da ist.

            _notifications.ShowSuccess(
                $"{pending.FloatingQuantity}x '{pending.PartName}' in {pending.SelectedFloatingBin.Bin.Label} eingelagert.",
                pending.ImageUrl);

            // v0.1.24-beta.1 (Trigger 6): Modal-Daten BEVOR PendingPart auf
            // null gesetzt wird — sonst sind die Felder weg.
            var binLabel = pending.SelectedFloatingBin.Bin.Label;
            var partImage = pending.ImageUrl;
            var partName = pending.PartName;
            var blPartNo = pending.BlPartNo;
            var colorName = pending.ColorName;
            var qty = pending.FloatingQuantity;

            PendingPart = null;

            // v0.1.24-beta.1 (Aufgabe K, Praxis-Befund 14.05.2026):
            // Brickognize-Vorschlaege zuruecksetzen nach erfolgreichem Save,
            // damit der naechste Scan nicht mit veralteten Daten startet.
            ResetBrickognizeSuggestionsAfterSave();

            // v0.1.24-beta.1 (Konzept 4.3 Trigger 6): Post-Save-Modal mit
            // nur Put-Sektion (User hat Teil in der Hand, keine Take-
            // Section). Konzept E7.
            if (!string.IsNullOrWhiteSpace(binLabel))
            {
                var instruction = new SortInstruction
                {
                    HeaderText = "Operation erfolgreich"
                };
                instruction.Put.Add(new SortSection
                {
                    BinLabel = binLabel,
                    Items = new List<SortItemLine>
                    {
                        new()
                        {
                            Label = partName,
                            Detail = $"{blPartNo} - {colorName}",
                            QuantityText = $"{qty}x",
                            ImageUrl = partImage
                        }
                    }
                });
                ShowSortInstruction(instruction);
            }
            return true;
        }
        catch (HBSort.Core.Services.InvalidBinKindException strict)
        {
            // v0.1.24-beta.1 (Konzept E10): Strict-Mode-Verletzung beim
            // Save (z.B. Ziel-Bin in der Zwischenzeit zu Complete-Bin
            // mutiert). Sortier-Modal erscheint NICHT, statt dessen
            // Error-Dialog. PendingPart bleibt offen, User kann neuen Bin
            // waehlen.
            Log.Warning(strict, "StoreFloating: Strict-Mode-Verletzung");
            await _dialogs.ShowErrorAsync("Lagern nicht moeglich", strict.Message);
            return false;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException race)
        {
            Log.Warning(race, "StoreFloating: DB-Konflikt beim Speichern");
            await _dialogs.ShowErrorAsync(
                "Konflikt beim Speichern",
                "Die Daten haben sich in der Zwischenzeit geaendert. Bitte Auswahl erneut treffen.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "StoreFloating fehlgeschlagen");
            _notifications.ShowError($"Fehler beim Lagern: {ex.Message}");
            return false;
        }
        finally
        {
            // pending kann bei Erfolg null sein - Defensive-Check.
            if (PendingPart != null) PendingPart.IsBusy = false;
        }
    }

    /// <summary>
    /// v0.1.24-beta.1 (Aufgabe K, Praxis-Befund 14.05.2026): Brickognize-
    /// Vorschlaege-State zuruecksetzen nach einem erfolgreichen Save-Pfad.
    /// Sonst zeigen ResultCards (Top-3-Karten) und Score-Status veraltete
    /// Daten des vorigen Scans.
    ///
    /// Zentraler Helper damit Trigger 1 (Reverse-Match-Konsum, Phase 2)
    /// denselben Reset-Pfad nutzen kann ohne Duplikation.
    /// </summary>
    public void ResetBrickognizeSuggestionsAfterSave()
    {
        try
        {
            ResultCards.Clear();
            HasResults = false;
            ResultHeadlineText = string.Empty;
            ShowSearchFallback = false;
            RecognizedColors.Clear();
            HasColors = false;
            SelectedCardIndex = -1;
            // PendingMinifig / PendingPart bewusst NICHT hier resetten —
            // die Verkabelung im Aufrufer (z.B. StoreFloating) hat das schon
            // gemacht oder soll es noch tun. Andere Aufrufer (z.B. Wizard-
            // Stufe-2-Save in Phase 2) duerfen den Brickognize-State zuerst
            // sehen — das hier ist das nachgeschaltete Cleanup.
        }
        catch (Exception ex)
        {
            // Defensive — der Reset ist Komfort, kein Hard-Requirement.
            Log.Warning(ex, "ResetBrickognizeSuggestionsAfterSave fehlgeschlagen");
        }
    }

    private readonly ICategoryBinMappingService _categoryMapping;

    public ScanViewModel(
        ICameraService cameraService,
        ISettingsService settingsService,
        IBrickognizeClient brickognizeClient,
        IExternalIdResolver idResolver,
        INotificationService notifications,
        IBlCacheRepository blCacheRepo,
        IPartImageProvider imageProvider,
        IPersistentImageCache persistentCache,
        IBlCatalogService blCatalog,
        IStorageBinService binService,
        IMinifigPersistenceService persistenceService,
        IPartLookupService partLookup,
        IDialogService dialogs,
        IFloatingPartTransferService floatingTransfer,
        IBlPriceCacheService priceCache,
        ICategoryBinMappingService categoryMapping)
    {
        _cameraService = cameraService;
        _settingsService = settingsService;
        _brickognizeClient = brickognizeClient;
        _idResolver = idResolver;
        _notifications = notifications;
        _blCacheRepo = blCacheRepo;
        _imageProvider = imageProvider;
        _persistentCache = persistentCache;
        _blCatalog = blCatalog;
        _binService = binService;
        _persistenceService = persistenceService;
        _partLookup = partLookup;
        _dialogs = dialogs;
        _floatingTransfer = floatingTransfer;
        _priceCache = priceCache;
        _categoryMapping = categoryMapping;

        _cameraService.FrameReceived += OnFrameReceived;
    }

    [RelayCommand]
    public async Task InitializeCameraAsync()
    {
        // v0.1.22-beta.4 2026-05-13: Lazy-Enumeration. Beim App-Start
        // KEINE GetAvailableCameras()-Schleife (probiert 10 Indizes,
        // jeder Probe-Konstruktor 1-3s = 17-20s UI-Block bei einer
        // Kamera). Stattdessen: gespeicherten Index direkt oeffnen.
        // Volle Enumeration nur lazy im Settings-Tab "Erkennung" wenn
        // der User dort die Kamera-Combobox oeffnet.
        var savedIndex = _settingsService.Current.SelectedCameraIndex;
        if (savedIndex < 0) savedIndex = 0;
        SelectedCameraIndex = savedIndex;

        try
        {
            await StartCameraAsync(savedIndex);
        }
        catch (Exception ex)
        {
            // Kein Re-Throw - App soll trotzdem starten. StartCameraAsync
            // setzt IsCameraRunning intern auf false bei Fehler; falls
            // doch eine Exception durchkommt: defensiv behandeln.
            Log.Warning(ex,
                "Camera-Init mit gespeichertem Index {Idx} fehlgeschlagen - " +
                "User muss in Settings eine andere Kamera waehlen", savedIndex);
            IsCameraRunning = false;
            ScanStatusText = "Kamera konnte nicht gestartet werden!";
            try
            {
                _notifications.ShowError(
                    "Kamera konnte nicht gestartet werden. Bitte in den " +
                    "Einstellungen unter 'Erkennung' eine Kamera auswaehlen.");
            }
            catch { /* Notifications nicht verfuegbar */ }
        }
    }

    public async Task StartCameraAsync(int index)
    {
        ScanStatusText = "Kamera wird gestartet...";
        await _cameraService.StartAsync(index);

        IsCameraRunning = _cameraService.IsRunning;
        UpdateCanScan();

        ScanStatusText = IsCameraRunning
            ? "Leertaste druecken zum Scannen"
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
            BrickognizeStatus.Offline => $"Brickognize: Offline - {health.ErrorMessage}",
            _                          => "Brickognize: Status unbekannt"
        };

        UpdateCanScan();

        if (health.Status == BrickognizeStatus.Offline)
        {
            _notifications.ShowError("Brickognize nicht erreichbar - Scannen deaktiviert.");
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
        // bei TextBox-Fokus weiter - deshalb pruefen wir das hier explizit.
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
        ScanStatusText = "Bild eingefroren - sende an Brickognize...";

        // Bild im scans-Ordner archivieren
        await SaveScanImageAsync(snapshot);

        // Kein paralleles Freeze-Delay mehr - die Kamera geht direkt nach der
        // API-Antwort wieder live (UX-1 FIX 4). Doppelscans werden weiterhin
        // durch _isFrozen + ScanCooldownMs verhindert.

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
            // Vorher (BUG): nur wenn der Top-Treffer ein Teil war - dann blieben
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
        ScanStatusText = "Leertaste druecken zum Scannen";
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
            ResultHeadlineText = "Nichts erkannt - bitte erneut versuchen oder manuelle Suche.";
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
            // Zusaetzliche Regel aus CLAUDE.md: ab Score < ShowSelection IMMER Auswahl-UI -
            // d.h. selbst wenn der Top-Score >= Auto liegt, aber ShowSelection groesser ist
            // (sollte normal nicht sein, da Auto > ShowSelection als Default), trotzdem
            // hervorheben - aber Auswahl bleibt sichtbar.
            if (i == 0 && item.Score >= thresholdAuto)
            {
                // Audit W-11: kein direktes Brush-Setting mehr - der DataTrigger
                // im XAML faerbt die Karte basierend auf IsTopHit gruen.
                card.IsTopHit = true;
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
            ResultHeadlineText = $"Top-Treffer mit {topScore:P0} - auto-akzeptiert.";
            ShowSearchFallback = false;
        }
        else
        {
            ResultHeadlineText = $"Bitte auswaehlen (Top-Score {topScore:P0}).";
            ShowSearchFallback = false;
        }

        HasResults = true;

        // Toast nur fuer "Nicht sicher" / "Bitte auswaehlen" - Auto-Akzept-Feedback
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
    /// (vor der Farb-Identifikation) - die Top-Card wird spaeter via
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
    /// Brickognize liefert direkt BL-Color-IDs - wir reichen sie ohne Mapping
    /// an den ImageProvider durch.
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
            if (top.BricklinkId.HasValue) recognizedColorId = top.BricklinkId.Value;
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
            ResultHeadlineText = $"Top-Treffer mit {topScore:P0} - auto-akzeptiert.";
        }
        else
        {
            ResultHeadlineText = $"Treffer {index + 1} mit {card.Score:P0} - manuell ausgewaehlt.";
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
            _notifications.ShowWarning("Keine BL-Referenz - Lookup nicht moeglich.");
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

            // Tasks sind nach WhenAll garantiert abgeschlossen - await ist hier
            // ein billiges Auslesen des bereits berechneten Resultats.
            var details = await detailsTask;
            var subsets = await partsTask;
            var colors = await colorsTask;
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

            // UX X.29 Block G (v0.1.16): Default-Auswahl beim Scannen aus Settings.
            var defaultCollected = _settingsService.Current.DefaultPartsCollected;
            foreach (var subset in subsets)
            {
                colorLookup.TryGetValue(subset.ColorId, out var color);
                var itemName = await GetCachedItemNameOrEmptyAsync(subset.ItemType, subset.ItemNo);
                pending.Parts.Add(PendingPartViewModel.FromSubset(subset, color, itemName, defaultCollected));
            }

            // Wenn der Token zwischenzeitlich gecancelt wurde -> nicht ueberschreiben
            if (ct.IsCancellationRequested) return;

            PendingMinifig = pending;

            // UX#12: Preis-VM fuer die obere rechte Box anlegen + Load starten.
            // Sub-Tupel mit (PartNo, ColorId, QuantityNeeded, PartName) aus den
            // BL-Subsets bauen - das VM nutzt das fuer die parallel-Lookups.
            var priceSubsets = pending.Parts
                .Select(p => (
                    PartNo: p.BricklinkPartNo,
                    ColorId: p.BricklinkColorId,
                    QuantityNeeded: p.Quantity,
                    PartName: p.PartName))
                .ToList();
            // UX-Iteration X.10: das VM startet im Konstruktor ggf. den
            // Auto-Load (je nach Settings.Prices.AutoLoadCompletePrice und
            // .AutoLoadPartsPrice). Im Manual-Modus passiert nichts bis der
            // User auf "Preis laden" klickt - dann triggert die View den
            // entsprechenden Command direkt am VM.
            PriceInfo = new MinifigPriceViewModel(_priceCache, _settingsService, bricklinkId, priceSubsets);

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
            LoadMinifigHeaderImageAsync(bricklinkId).FireAndForget("Scan-MinifigHeaderImage");
            if (_settingsService.Current.ImageCache.PreloadOnMinifigScan)
            {
                // v0.1.23-beta.2 Fix C: vorigen Preload-Lauf canceln.
                _preloadPartImagesCts?.Cancel();
                _preloadPartImagesCts?.Dispose();
                _preloadPartImagesCts = new CancellationTokenSource();
                PreloadPartImagesAsync(pending, _preloadPartImagesCts.Token)
                    .FireAndForget("Scan-PreloadPartImages");
            }

            // Phase 4: Lagerfach-Liste fuer die ComboBox laden + Default setzen.
            LoadAvailableBinsForPendingAsync(pending).FireAndForget("Scan-LoadAvailableBins");

            // UX X.4+: pro Teil pruefen ob ein passender FloatingPart existiert
            // -> Button "Aus Fach uebernehmen" einblenden.
            RefreshFloatingMatchesForPendingAsync(pending).FireAndForget("Scan-RefreshFloatingMatches");
        }
        catch (OperationCanceledException)
        {
            // Vom User durch neuen Klick / neuen Scan abgebrochen - kein Toast.
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
            _notifications.ShowWarning("Keine BL-Part-No - Teile-Lookup nicht moeglich.");
            return;
        }

        // Color: BL-ID aus RecognizedColors[0] (Mapping ist schon erfolgt).
        // Fallback 0 = "no color" wenn Brickognize keine Farbe erkannt hat.
        int blColorId = 0;
        if (RecognizedColors.Count > 0 && RecognizedColors[0].BricklinkId.HasValue)
            blColorId = RecognizedColors[0].BricklinkId!.Value;

        // UX X.33 v0.1.19-beta.7 Block M: Brickognize-Category an die VM
        // durchreichen damit der CategoryBinMappingService das Mapping
        // pro Scan zur Hand hat.
        var pending = new PartLookupViewModel(blPartNo, blColorId, card.RawItem?.Category);
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
        LoadPartImageAsync(pending).FireAndForget("PartScan-LoadImage");

        // Lagerfaecher fuer Floating-Combo laden
        LoadAvailableBinsForPendingPartAsync(pending).FireAndForget("PartScan-LoadAvailableBins");

        // Known-Colors fuer Korrektur-Dropdown laden
        LoadKnownColorsForPendingPartAsync(pending).FireAndForget("PartScan-LoadKnownColors");

        // BL-Catalog-Treffer-Bilder im Hintergrund nachladen
        // v0.1.23-beta.2 Fix C: vorigen Lauf canceln + neue CTS.
        _blCatalogImagesCts?.Cancel();
        _blCatalogImagesCts?.Dispose();
        _blCatalogImagesCts = new CancellationTokenSource();
        LoadBlCatalogImagesAsync(pending, _blCatalogImagesCts.Token)
            .FireAndForget("PartScan-LoadBlCatalogImages");
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

        LoadPartImageAsync(pending).FireAndForget("PartReLookup-LoadImage");

        // v0.1.23-beta.2 Fix C: vorigen Lauf canceln + neue CTS.
        _blCatalogImagesCts?.Cancel();
        _blCatalogImagesCts?.Dispose();
        _blCatalogImagesCts = new CancellationTokenSource();
        LoadBlCatalogImagesAsync(pending, _blCatalogImagesCts.Token)
            .FireAndForget("PartReLookup-LoadBlCatalogImages");
    }

    /// <summary>
    /// Best-effort: fuer jeden BL-Catalog-Treffer in der PartLookupView das
    /// Minifig-Bild via PartImageProvider laden falls noch nicht da.
    /// Aenderungen werden auf den UI-Thread gepostet (ImageUrl ist ObservableProperty).
    /// </summary>
    private async Task LoadBlCatalogImagesAsync(PartLookupViewModel pending, CancellationToken ct = default)
    {
        foreach (var match in pending.BlCatalogMatches.ToList())
        {
            if (ct.IsCancellationRequested) break;
            if (!string.IsNullOrEmpty(match.ImageUrl)) continue;
            try
            {
                var url = await _imageProvider.GetImageFileByBlAsync("M", match.BlMinifigId, null);
                if (ct.IsCancellationRequested) break;
                if (!string.IsNullOrEmpty(url))
                {
                    // v0.1.23-beta.2 Fix C: InvokeAsync statt Invoke - vermeidet
                    // synchrone Blockierung des UI-Threads nach Save-Operation.
                    var disp = System.Windows.Application.Current?.Dispatcher;
                    if (disp != null)
                        await disp.InvokeAsync(() => match.ImageUrl = url);
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
            // v0.1.23-beta.1-Fix 2026-05-14 (Praxis-Test-Befund): typ-gefiltert
            // + Reverse-Match-Bypass. Vorher GetAllAsync() - dann erschienen auch
            // Complete-Bins in der "Als Einzelteil lagern"-Combobox; Strict-Mode
            // warf zwar beim Klick, der User konnte aber das falsche Ziel
            // ueberhaupt erst auswaehlen. partNo/colorId werden durchgereicht
            // damit Wartend-Bins mit passendem Reverse-Match-Required-Part
            // weiterhin zulaessig sind.
            // v0.1.24-beta.1 Phase 2b: WithCounts-Variante fuer Combobox-Suffix.
            // FloatingCount ist hier besonders relevant ("(5 Einzelteile)" zeigt
            // dass das Bin schon Stapel hat).
            var allBins = await _binService.GetEligibleBinsWithCountsAsync(
                BinTargetKind.FloatingTarget,
                partNo: pending.BlPartNo,
                colorId: pending.BlColorId);

            // UX X.33 v0.1.19-beta.7 Block N: alle drei Stufen (Stapel ->
            // User-Mapping -> Default-Regel "max 1 PartId pro Kategorie pro
            // Bin") sind im neuen Service-Aufruf gebuendelt. Reihenfolge
            // ist im Service fest verdrahtet, hier nur EIN Aufruf.
            //
            // Brickognize-Kategorie zur Seen-Liste hinzufuegen damit das
            // Settings-UI sie zeigt (organisch wachsende Liste). KEIN
            // Auto-Mapping mehr - User pflegt Settings manuell.
            if (!string.IsNullOrWhiteSpace(pending.BrickognizeCategory))
            {
                await _categoryMapping.RememberSeenCategoryAsync(pending.BrickognizeCategory);
            }

            // UX-X.7-Hint: nur Anzeige ("Liegt schon in Box X (5x)") -
            // der Stapel-Match selbst wird im Service-Aufruf erneut
            // berechnet, das ist preiswert und vermeidet Doppel-Logik.
            var stackHint = await _floatingTransfer.FindBestStorageBinSuggestionAsync(
                pending.BlPartNo, pending.BlColorId);

            var suggested = await _binService.SuggestBinForFloatingPartAsync(
                pending.BlPartNo, pending.BlColorId,
                pending.BrickognizeCategory ?? string.Empty,
                userMapping: _settingsService.Current.CategoryToBinMapping);

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                pending.AvailableBins.Clear();
                foreach (var b in allBins)
                    pending.AvailableBins.Add(new BinDisplayItem(b.Bin, BinDisplayFormatter.FormatBinDisplayText(b)));

                // Reference-Equality-Lookup damit die ComboBox den Eintrag
                // in ItemsSource findet (Id ist Convenience-Property auf BinDisplayItem).
                var defaultBin = suggested != null
                    ? pending.AvailableBins.FirstOrDefault(b => b.Id == suggested.Id)
                    : null;

                pending.SelectedFloatingBin = defaultBin;

                // UX-X.7-Hint nur dann zeigen, wenn der Service auch das
                // Stapel-Bin als Default genommen hat (sonst waere der Hint
                // irrefuehrend - "schon dort" zeigt auf ein anderes Bin als
                // SelectedFloatingBin).
                if (stackHint != null && defaultBin?.Id == stackHint.BinId)
                {
                    pending.HasMatchingFloatingBin = true;
                    pending.MatchingFloatingBinLabel = stackHint.BinLabel;
                    pending.MatchingFloatingBinQuantity = stackHint.QuantityInThisBin;
                    pending.MatchingFloatingBinCount = stackHint.TotalMatchingBinsCount;
                }
                else
                {
                    pending.HasMatchingFloatingBin = false;
                    pending.MatchingFloatingBinLabel = null;
                    pending.MatchingFloatingBinQuantity = 0;
                    pending.MatchingFloatingBinCount = 0;
                }

                // UX X.33 Block K: bei null vom Service Banner zeigen,
                // kein Silent-Fallback.
                if (defaultBin == null)
                {
                    pending.NoFreeBinWarning = pending.AvailableBins.Count == 0
                        ? "Es gibt noch keine Lagerfaecher. Lege zuerst eines an."
                        : "Kein passendes Fach frei (Default-Regel: max 1 Teil-Typ pro Kategorie pro Bin). Bitte neues Fach anlegen oder im Settings-Tab 'Kategorien' ein Mapping setzen.";
                }
                else
                {
                    pending.NoFreeBinWarning = null;
                }

                Log.Information(
                    "PartLookup-Bins: {Count} Faecher, Cat='{Cat}', Selected.Id={Sel}",
                    pending.AvailableBins.Count,
                    pending.BrickognizeCategory ?? "(empty)",
                    pending.SelectedFloatingBin?.Id);
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
                Log.Debug("KnownColors leer - Fallback auf {Count} alle BL-Farben", known.Count);
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
            // v0.1.23-beta.1-Fix 2026-05-14 (Praxis-Test-Befund): typ-gefiltert
            // statt GetAllAsync(). Vorher landeten auch Floating-Bins in der
            // Combobox - der Strict-Mode warf zwar beim Speichern, aber der
            // User konnte das falsche Ziel ueberhaupt erst auswaehlen.
            // Whitelist fuer Waiting/Complete-Target ist identisch
            // ({Empty, Waiting, Complete}), darum braucht ReSuggestBinForPendingAsync
            // die Liste nicht neu zu laden wenn WillBeComplete sich aendert.
            var targetKind = pending.WillBeComplete
                ? BinTargetKind.CompleteMinifigTarget
                : BinTargetKind.WaitingMinifigTarget;
            // v0.1.24-beta.1 Phase 2b: WithCounts-Variante fuer Combobox-Suffix
            // ("Box 005 (2 wartend, 3 fertig)" / "(5 Einzelteile)" / leer).
            var allBins = await _binService.GetEligibleBinsWithCountsAsync(targetKind);
            // UX X.31 Block B Bug-Fix v2 (v0.1.18): Initial-Suggest abhaengig
            // vom Pending-Status. "Alles vorab abgehakt" + Reverse-Match heisst:
            // Pending kann beim Erscheinen schon Complete sein - dann Complete-
            // Pfad nutzen statt Waiting. Live-Updates ueber ReSuggestBinForPendingAsync.
            StorageBin? firstFree;
            if (pending.WillBeComplete)
            {
                var maxComplete = _settingsService.Current.MaxCompleteFiguresPerBin;
                firstFree = await _binService.SuggestBinForCompleteMinifigAsync(maxComplete);
            }
            else
            {
                // UX X.32 v0.1.19-beta.4: Limit fuer wartende Figuren aus Settings.
                var maxWaiting = _settingsService.Current.MaxWaitingFiguresPerBin;
                firstFree = await _binService.SuggestBinForWaitingMinifigAsync(maxWaiting);
            }

            // ComboBox auf UI-Thread befuellen
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                pending.AvailableBins.Clear();
                foreach (var b in allBins)
                    pending.AvailableBins.Add(new BinDisplayItem(b.Bin, BinDisplayFormatter.FormatBinDisplayText(b)));

                // UX X.33 v0.1.19-beta.7 Block K: bei null vom Service NICHT
                // mehr stillschweigend auf irgendein Fach fallback - User
                // soll Warnung sehen und ein neues Fach anlegen oder leeren.
                if (firstFree != null)
                {
                    // WICHTIG: Item aus AvailableBins holen (gleiche Id) damit
                    // die ComboBox den Eintrag findet (Reference-Equality).
                    pending.SelectedBin = pending.AvailableBins.FirstOrDefault(b => b.Id == firstFree.Id);
                    pending.NoFreeBinWarning = null;
                }
                else
                {
                    pending.SelectedBin = null;
                    pending.NoFreeBinWarning = allBins.Count == 0
                        ? "Es gibt noch keine Lagerfaecher. Lege zuerst eines an."
                        : "Alle Lagerfaecher sind belegt. Bitte ein neues Fach anlegen oder ein bestehendes leeren.";
                }
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

        // UX X.32 v0.1.19-beta.4 (User-Befund): Bestaetigungs-Dialog
        // "Lagerfach belegt - mehrere Figuren erlaubt?" entfernt. Mehrere
        // wartende/komplette Figuren pro Fach sind ueber Settings
        // (MaxWaitingFiguresPerBin / MaxCompleteFiguresPerBin) explizit
        // erlaubt - das ist bewusst eingestellt, nicht warnungswuerdig.

        pending.IsPersisting = true;
        try
        {
            // Required-Parts aus den Pending-Parts extrahieren
            //
            // v0.1.24-beta.1 Phase 2a-Polish: ConsumeFloatingParts=false.
            // Der Pool-Konsum passiert hier ausschliesslich ueber explizite
            // "Aus Fach"-Klicks (TransferFloatingPartToPendingAsync), die
            // QuantityCollected schon hochgezaehlt UND PendingMinifig.
            // ConsumedFromBins gefuellt haben. Der Service soll NICHT
            // zusaetzlich nach passenden FloatingParts suchen.
            // (BuildSuggestionDetailDialog laesst den Default true.)
            var input = new PersistMinifigInput
            {
                BricklinkId = pending.BricklinkId,
                Name = pending.Name,
                ImageUrl = pending.ImageUrl,
                LocalImagePath = pending.ImageUrl, // BL-Provider liefert Pfad als URL-Form
                UserNotes = pending.UserNotes,
                StorageBinId = pending.SelectedBin.Bin.Id,
                ConsumeFloatingParts = false,
                RequiredParts = pending.Parts.Select(p => new PersistMinifigPart
                {
                    BricklinkPartNo = p.BricklinkPartNo,
                    BricklinkColorId = p.BricklinkColorId,
                    PartName = p.PartName,
                    ColorName = p.ColorName,
                    QuantityNeeded = p.Quantity,
                    // Manuelle Markierung im Pending: "Habe ich" -> alle Teile dieser
                    // Sorte als gesammelt. QuantityCollected kommt aus dem Pending-VM
                    // (Anhaken-Checkbox = Quantity ODER "Aus Fach uebernehmen" = 1..Quantity).
                    QuantityCollected = p.QuantityCollected
                }).ToList()
            };

            var result = await _persistenceService.PersistAndStoreAsync(input);

            // UX X.20 Teil 5: Toast mit Item-Bild. Bei Auto-Komplettierung
            // kombinierter Hinweis ("komplett in Box X").
            //
            // v0.1.24-beta.1 Phase 2a-Polish: Pool-Uebernahmen werden aus
            // PendingMinifig.ConsumedFromBins gezaehlt (explizite "Aus Fach"-
            // Klicks), nicht aus result.ReverseMatchedFloating (das ist 0
            // weil ConsumeFloatingParts=false).
            var toastImage = pending.ImageUrl;
            var binLabelForInstruction = pending.SelectedBin?.Bin.Label ?? string.Empty;
            var totalConsumed = pending.ConsumedFromBins.Sum(c => c.Quantity);
            if (result.IsFullyComplete)
            {
                _notifications.ShowSuccess(
                    $"Figur '{pending.Name}' komplett in {pending.SelectedBin?.Bin.Label ?? "(kein Fach)"}!",
                    toastImage);
            }
            else if (totalConsumed > 0)
            {
                _notifications.ShowSuccess(
                    $"'{pending.Name}' in {pending.SelectedBin?.Bin.Label} eingelagert. " +
                    $"{totalConsumed} Teil(e) wurden aus Lagerfaechern uebernommen.",
                    toastImage);
            }
            else
            {
                _notifications.ShowSuccess(
                    $"'{pending.Name}' in {pending.SelectedBin?.Bin.Label} eingelagert.",
                    toastImage);
            }

            // v0.1.24-beta.1 Phase 1.5 (Trigger 1): einheitliches Post-Save-
            // Modal mit Take/Put-Sektionen. Loest die bisherigen Single-/
            // Group-Aufrufe (ShowBinInstruction / ShowBinInstructionGroup)
            // ab und nutzt den gleichen Modal-Pfad wie die anderen 5
            // Sortier-Triggers. Konzept 4.3.7.
            //
            // - Take-Sektionen: pro Quell-Bin der konsumierten FloatingParts
            //   eine Section (kann leer sein wenn keine FloatingParts
            //   konsumiert wurden - dann nur Put).
            // - Put-Sektion: die Figur kommt ins Ziel-Bin.
            // - HeaderText: prominent bei Auto-Komplettierung.
            //
            // Die alten ShowBinInstruction/ShowBinInstructionGroup-Methoden
            // bleiben erhalten - sie werden noch von BuildSuggestionDetail-
            // Dialog, PartLookupView und MinifigDetailView gerufen. Cleanup
            // dieser Pfade ist v0.1.25-Material.
            if (!string.IsNullOrWhiteSpace(binLabelForInstruction))
            {
                var instruction = new SortInstruction
                {
                    HeaderText = result.IsFullyComplete
                        ? $"Figur '{pending.Name}' komplett angelegt"
                        : "Operation erfolgreich"
                };

                // v0.1.24-beta.1 Phase 2a-Polish: Take-Sektionen aus der
                // ConsumedFromBins-Tracking-Liste (gefuellt von expliziten
                // "Aus Fach"-Klicks via TransferFloatingPartToPendingAsync).
                // Das ersetzt die alte Quelle result.ConsumedFloatingParts:
                // erstens kommen jetzt Bilder mit (Befund B aus Praxis-Test
                // 2026-05-16), zweitens wird nicht mehr automatisch
                // konsumiert (Befund A).
                foreach (var byBin in pending.ConsumedFromBins
                    .GroupBy(c => c.SourceBinLabel))
                {
                    var section = new SortSection { BinLabel = byBin.Key };
                    foreach (var c in byBin)
                    {
                        section.Items.Add(new SortItemLine
                        {
                            Label = c.PartName,
                            Detail = $"{c.PartNo} - {c.ColorName}",
                            QuantityText = $"{c.Quantity}x",
                            ImageUrl = c.ImageUrl
                        });
                    }
                    instruction.Take.Add(section);
                }

                // Put-Sektion: die Figur kommt ins Ziel-Bin.
                instruction.Put.Add(new SortSection
                {
                    BinLabel = binLabelForInstruction,
                    Items = new List<SortItemLine>
                    {
                        new()
                        {
                            Label = $"Figur '{pending.Name}'",
                            Detail = pending.BricklinkId ?? string.Empty,
                            QuantityText = "1x",
                            ImageUrl = toastImage
                        }
                    }
                });

                ShowSortInstruction(instruction);
            }

            // Pending ausblenden, Top-3 wiederherstellen
            PendingMinifig = null;
            MinifigStatusText = string.Empty;
        }
        catch (InvalidBinKindException strict)
        {
            // v0.1.23 Strict-Mode: Ziel-Bin akzeptiert die Figur nicht. User-
            // freundliche Meldung statt unter Generic-Fehler vergraben.
            Log.Warning(strict, "PersistPending: Strict-Mode-Verletzung");
            _notifications.ShowError(strict.Message);
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
    /// SemaphoreSlim(5) als Throttle. Best-effort - einzelne Fehler werden nur geloggt.
    /// v0.1.23-beta.2 Fix C: per CancellationToken abbrechbar.
    /// </summary>
    private async Task PreloadPartImagesAsync(PendingMinifigViewModel pending, CancellationToken ct = default)
    {
        using var throttle = new SemaphoreSlim(initialCount: 5);
        var tasks = pending.Parts.Select(async part =>
        {
            if (ct.IsCancellationRequested) return;
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ct.IsCancellationRequested) return;
                // Direkt mit BL-IDs - kein Brickognize-Wrapper noetig.
                var url = await _imageProvider.GetImageFileByBlAsync("P", part.BricklinkPartNo, part.BricklinkColorId);
                if (ct.IsCancellationRequested) return;

                // v0.1.23-beta.2 Fix C: InvokeAsync statt Invoke.
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp != null)
                    await disp.InvokeAsync(() => part.ImageUrl = url);
            }
            catch (OperationCanceledException)
            {
                // Erwartet beim Cancel - nicht loggen.
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Vorab-Cache fuer Teil {Part}/{Color} fehlgeschlagen",
                    part.BricklinkPartNo, part.BricklinkColorId);
            }
            finally
            {
                try { throttle.Release(); } catch { /* Semaphore disposed */ }
            }
        });

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* erwartet */ }
        Log.Information("Vorab-Cache abgeschlossen ({Count} Teile)", pending.Parts.Count);
    }

    /// <summary>
    /// UX-Iteration X.4+: pro Teil in der Pending-Minifig pruefen, ob ein
    /// passender FloatingPart in irgendeinem Lagerfach existiert. Befuellt
    /// HasMatchingFloatingPart + MatchingFloatingPartBinLabel + Quantity.
    /// Wird beim ersten Laden der Pending-Figur aufgerufen + nach jedem
    /// erfolgreichen Transfer.
    /// </summary>
    private async Task RefreshFloatingMatchesForPendingAsync(PendingMinifigViewModel pending)
    {
        foreach (var part in pending.Parts.ToList())
        {
            try
            {
                var match = await _floatingTransfer.FindFirstMatchAsync(
                    part.BricklinkPartNo, part.BricklinkColorId);

                // Update auf dem UI-Thread (ObservableObject-Properties)
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (match != null)
                    {
                        part.HasMatchingFloatingPart = true;
                        part.MatchingFloatingPartBinLabel = match.StorageBinLabel;
                        part.MatchingFloatingPartQuantity = match.QuantityAvailable;
                    }
                    else
                    {
                        part.HasMatchingFloatingPart = false;
                        part.MatchingFloatingPartBinLabel = null;
                        part.MatchingFloatingPartQuantity = 0;
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "FloatingMatch-Lookup fuer {Part}/{Color} fehlgeschlagen",
                    part.BricklinkPartNo, part.BricklinkColorId);
            }
        }
    }

    /// <summary>
    /// UX-Iteration X.4+: User klickt "Aus Fach uebernehmen" an einem Pending-Part.
    /// Reduziert den FloatingPart in der DB sofort um 1, erhoeht
    /// QuantityCollected des Pending-Parts und feuert einen Toast.
    ///
    /// Wichtig: Die Buchung passiert SOFORT, nicht erst beim "In Fach legen"
    /// - das Teil ist physisch beim User. Wenn der User die Pending-Figur
    /// danach verwirft, bleibt der FloatingPart trotzdem reduziert (Spec).
    /// </summary>
    public async Task TransferFloatingPartToPendingAsync(PendingPartViewModel part)
    {
        if (part.QuantityCollected >= part.Quantity) return; // schon komplett

        try
        {
            var pendingDesc = PendingMinifig != null
                ? $"{PendingMinifig.Name} ({PendingMinifig.BricklinkId}) - Pending"
                : $"Pending ({part.BricklinkPartNo})";

            var result = await _floatingTransfer.TransferOneAsync(
                part.BricklinkPartNo, part.BricklinkColorId, pendingDesc);

            if (!result.Success)
            {
                _notifications.ShowWarning(
                    result.ErrorMessage ?? "Teil nicht mehr im Fach verfuegbar.");
                // UI-Refresh trotzdem - vielleicht ist das Match-Flag jetzt aus.
                if (PendingMinifig != null)
                    await RefreshFloatingMatchesForPendingAsync(PendingMinifig);
                return;
            }

            // Im Pending-VM hochzaehlen.
            part.QuantityCollected = Math.Min(part.QuantityCollected + 1, part.Quantity);

            // v0.1.24-beta.1 Phase 2a-Polish: Tracking-Liste fuer das Post-
            // Save-Modal befuellen. Wenn dasselbe (PartNo, ColorId, Bin)
            // schon einmal uebernommen wurde, Quantity++ (Stack-Logik).
            if (PendingMinifig != null)
            {
                var existing = PendingMinifig.ConsumedFromBins
                    .FirstOrDefault(c => c.PartNo == part.BricklinkPartNo
                                      && c.ColorId == part.BricklinkColorId
                                      && c.SourceBinLabel == result.SourceBinLabel);
                if (existing != null)
                {
                    existing.Quantity++;
                }
                else
                {
                    PendingMinifig.ConsumedFromBins.Add(new PendingMinifigViewModel.ConsumedFromBin
                    {
                        PartNo = part.BricklinkPartNo,
                        ColorId = part.BricklinkColorId,
                        PartName = part.PartName,
                        ColorName = part.ColorName,
                        Quantity = 1,
                        SourceBinLabel = result.SourceBinLabel ?? "?",
                        ImageUrl = part.ImageUrl
                    });
                }
            }

            var toastMsg = result.BinFreedAfterTransfer
                ? $"Teil aus '{result.SourceBinLabel}' uebernommen (Fach jetzt frei)."
                : $"Teil aus '{result.SourceBinLabel}' uebernommen.";
            _notifications.ShowSuccess(toastMsg);

            // Match-Flag fuer alle Pending-Parts neu evaluieren (das soeben
            // entnommene Teil koennte jetzt gar nicht mehr im Pool sein, oder
            // andere Teile haben sich nicht geaendert - egal, ein voller
            // Refresh ist die einfachere und korrektere Variante).
            if (PendingMinifig != null)
                await RefreshFloatingMatchesForPendingAsync(PendingMinifig);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FloatingPart-Transfer fehlgeschlagen ({Part}/{Color})",
                part.BricklinkPartNo, part.BricklinkColorId);
            _notifications.ShowError($"Fehler: {ex.Message}");
        }
    }

    /// <summary>
    /// Wertet die `colors`-Liste der Brickognize-Antwort aus und schlaegt zu jedem
    /// Eintrag die RGB-Farbe in bl_colors nach (fuer das Swatch in der UI).
    /// Brickognize liefert in der "id"-Spalte BL-Color-IDs direkt (validiert:
    /// id=5 = Red = BL-ID Red), daher kein Name-Lookup oder RB->BL-Konvertierung.
    /// </summary>
    private async Task BuildColorMatchesAsync(BrickognizePrediction prediction)
    {
        RecognizedColors.Clear();

        if (prediction.Colors == null || prediction.Colors.Count == 0)
        {
            HasColors = false;
            return;
        }

        // bl_colors einmalig laden + in Dictionary fuer O(1)-Lookup. ~250 Eintraege,
        // also vernachlaessigbar; fuer UI-Live-Anzeige sowieso uneerheblich.
        Dictionary<int, Core.Models.Bricklink.BlColor> blColorMap;
        try
        {
            var allBlColors = await _blCacheRepo.GetAllColorsAsync();
            blColorMap = allBlColors.ToDictionary(c => c.ColorId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BuildColorMatches: bl_colors Lookup fehlgeschlagen");
            blColorMap = new Dictionary<int, Core.Models.Bricklink.BlColor>();
        }

        // Top-3 Farben anzeigen (mehr ist normalerweise nicht im Bild).
        foreach (var raw in prediction.Colors.OrderByDescending(c => c.Score).Take(3))
        {
            // Brickognize-id direkt als BL-Color-ID interpretieren.
            int? blColorId = null;
            Core.Models.Bricklink.BlColor? blColor = null;
            if (int.TryParse(raw.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
            {
                blColorId = parsedId;
                blColorMap.TryGetValue(parsedId, out blColor);
            }

            RecognizedColors.Add(ColorMatch.FromBlColor(blColorId, raw.Name, raw.Score, blColor));
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
            "HBSort", "scans");

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

    // ========================================================================
    // v0.1.20-beta.5: IDisposable
    // ScanViewModel ist Singleton im DI. Beim App-Shutdown ruft
    // (Services as IDisposable)?.Dispose() in App.OnExit den hier hinterlegten
    // Dispose-Pfad auf, der die Event-Subscriptions wieder loest. Ohne das
    // bleiben der CameraService-FrameReceived-Handler und der PendingMinifig-
    // PropertyChanged-Handler als GC-Anker stehen.
    // ========================================================================

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // CameraService.FrameReceived abbestellen - sonst haengt der
        // OnFrameReceived-Handler am Camera-Singleton und haelt das ViewModel
        // (und damit den ganzen UI-Graphen) am Leben.
        try { _cameraService.FrameReceived -= OnFrameReceived; }
        catch (Exception ex) { Log.Debug(ex, "FrameReceived-Unsubscribe geworfen"); }

        // Dynamisch abonnierte PendingMinifig-PropertyChanged abbestellen
        // (siehe OnPendingMinifigChanged - dort wird sub/unsub bei jedem Set
        // gemanaged, hier defensiv fuer den allerletzten Zustand).
        try
        {
            if (PendingMinifig != null)
                PendingMinifig.PropertyChanged -= OnPendingMinifigPropertyChanged;
        }
        catch (Exception ex) { Log.Debug(ex, "PendingMinifig-Unsubscribe geworfen"); }

        // BinInstruction-Auto-Dismiss-Timer stoppen falls noch aktiv.
        try
        {
            _binInstructionTimer?.Stop();
            _binInstructionTimer = null;
        }
        catch (Exception ex) { Log.Debug(ex, "BinInstruction-Timer-Stop geworfen"); }

        // Laufenden BL-Lookup canceln und Token disposen.
        try { _lookupCts?.Cancel(); } catch { /* ignore */ }
        try { _lookupCts?.Dispose(); } catch { /* ignore */ }

        // v0.1.23-beta.2 Fix C: Image-Loader-CTS sauber beenden.
        try { _preloadPartImagesCts?.Cancel(); } catch { /* ignore */ }
        try { _preloadPartImagesCts?.Dispose(); } catch { /* ignore */ }
        try { _blCatalogImagesCts?.Cancel(); } catch { /* ignore */ }
        try { _blCatalogImagesCts?.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>Scan-Modus: bestimmt welcher Brickognize-Endpoint angesprochen wird.</summary>
public enum ScanMode
{
    /// <summary>Generischer /predict/ - wenn unklar.</summary>
    Auto,
    /// <summary>/predict/figs/ - Minifiguren.</summary>
    Minifig,
    /// <summary>/predict/parts/?predict_color=true - Teile + Farbe.</summary>
    Part
}
