using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Models;
using HBSort.Core.Models.Pricing;
using HBSort.Core.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// UX#12 + X.10: ViewModel fuer die Preis-Anzeige in der oberen rechten Box
/// des Sortier-Tabs. Wird vom ScanViewModel angelegt sobald eine
/// Pending-Minifig existiert (gerade gescannt, noch nicht persistiert).
///
/// Layout:
///   LINKS  - "Komplette Figur": Avg + Min/Max + Anzahl Listings + Datum
///   RECHTS - "Einzelteile aufsummiert": Liste pro Subset + Summe
///   UNTEN  - optional: Empfehlung "Komplett vs Einzelteile"
///
/// Lade-Strategie (UX X.10): jede Haelfte hat ihren eigenen Auto/Manual-
/// Modus aus den Settings (PriceSettings.AutoLoadCompletePrice und
/// .AutoLoadPartsPrice). Default beider Modi ist Manual - der User klickt
/// aktiv "Preis laden". Bei Auto-Modus startet der Load direkt im
/// Konstruktor.
///
/// Cache-First, dann Provider (Stale-While-Revalidate liegt im
/// IBlPriceCacheService):
///   1) Cache-Hit + frisch -> sofort verwenden, keine API-Call.
///   2) Cache-Hit + stale  -> Stale-Wert sofort anzeigen, im Hintergrund
///                            Live-Refresh.
///   3) Cache-Miss         -> Provider live aufrufen, Ergebnis cachen.
/// Beide Modi (Auto + Manual) gehen durch genau diesen Pfad. Der einzige
/// Unterschied ist WANN er ausgeloest wird (sofort vs auf Klick).
///
/// Force-Refresh (↻-Icon pro Haelfte):
///   - Loescht den passenden Cache-Eintrag fuer GuideType + Region +
///     Currency aus den Settings.
///   - Triggert dann einen normalen Load (der jetzt zwangslaeufig
///     Cache-Miss ist und Live geht).
/// </summary>
public partial class MinifigPriceViewModel : ObservableObject
{
    /// <summary>
    /// Schwelle in Prozent ab der eine "klare Empfehlung" gegeben wird.
    /// Unter 10% wird "Equal" angezeigt (kein Vor-/Nachteil).
    /// </summary>
    private const decimal RecommendationThresholdPercent = 10m;

    private readonly IBlPriceCacheService _cache;
    private readonly ISettingsService _settings;
    private readonly string _blMinifigId;
    private readonly IReadOnlyList<(string PartNo, int ColorId, int QuantityNeeded, string PartName)> _subsets;

    public string BlMinifigId => _blMinifigId;

    /// <summary>
    /// Wenn Auto-Modus aktiv ist, traegt das hier den vom Konstruktor
    /// gestarteten Hintergrund-Task. Tests koennen darauf awaiten;
    /// Produktion braucht es nicht (UI updated sich ueber Bindings).
    /// Null wenn Manual-Modus.
    /// </summary>
    public Task? CompleteAutoLoadTask { get; private set; }
    public Task? PartsAutoLoadTask { get; private set; }

    // ===== Komplett-Figur (linke Haelfte) =====

    /// <summary>
    /// True sobald LoadComplete getriggert wurde (Auto oder Klick). Steuert
    /// die Sichtbarkeit des "Preis laden"-Buttons (sichtbar nur wenn false).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCompleteIdleButton))]
    [NotifyPropertyChangedFor(nameof(ShowCompleteLoadedContent))]
    [NotifyPropertyChangedFor(nameof(ShowCompleteRefreshIcon))]
    private bool _isCompleteRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCompleteLoadedContent))]
    [NotifyPropertyChangedFor(nameof(ShowCompleteRefreshIcon))]
    private bool _isCompleteLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleteStale))]
    [NotifyPropertyChangedFor(nameof(CompleteListingsLabel))]
    [NotifyPropertyChangedFor(nameof(CompleteFetchedAtLabel))]
    [NotifyPropertyChangedFor(nameof(CompleteHasError))]
    [NotifyPropertyChangedFor(nameof(CompleteHasConfigurationHint))]
    [NotifyPropertyChangedFor(nameof(CompleteHintMessage))]
    [NotifyPropertyChangedFor(nameof(ShowCompleteIssueBanner))]
    [NotifyPropertyChangedFor(nameof(ShowCompleteLoadedContent))]
    [NotifyPropertyChangedFor(nameof(ShowCompleteRefreshIcon))]
    [NotifyPropertyChangedFor(nameof(HasRecommendation))]
    [NotifyPropertyChangedFor(nameof(RecommendationText))]
    private PriceLookupOutcome? _completeOutcome;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompletePriceLabel))]
    [NotifyPropertyChangedFor(nameof(CompletePriceTooltip))]
    [NotifyPropertyChangedFor(nameof(CompleteHasPrice))]
    [NotifyPropertyChangedFor(nameof(ShowCompleteLoadedContent))]
    [NotifyPropertyChangedFor(nameof(ShowCompleteRefreshIcon))]
    [NotifyPropertyChangedFor(nameof(HasRecommendation))]
    [NotifyPropertyChangedFor(nameof(RecommendationText))]
    private decimal? _completeRawPrice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompletePriceLabel))]
    [NotifyPropertyChangedFor(nameof(CompletePriceTooltip))]
    [NotifyPropertyChangedFor(nameof(CompleteHasPrice))]
    [NotifyPropertyChangedFor(nameof(ShowCompleteLoadedContent))]
    [NotifyPropertyChangedFor(nameof(ShowCompleteRefreshIcon))]
    [NotifyPropertyChangedFor(nameof(HasRecommendation))]
    [NotifyPropertyChangedFor(nameof(RecommendationText))]
    private decimal? _completeCorrectedPrice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompleteMinMaxLabel))]
    private decimal? _completeRawMin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompleteMinMaxLabel))]
    private decimal? _completeRawMax;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompleteMinMaxLabel))]
    private decimal? _completeCorrectedMin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompleteMinMaxLabel))]
    private decimal? _completeCorrectedMax;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMinifigCorrectionHint))]
    private string? _minifigCorrectionHint;

    public bool HasMinifigCorrectionHint => !string.IsNullOrEmpty(MinifigCorrectionHint);

    public bool CompleteHasPrice => CompleteCorrectedPrice.HasValue && CompleteCorrectedPrice.Value > 0;
    public bool IsCompleteStale => CompleteOutcome?.Source == PriceLookupSource.Stale;

    public bool CompleteHasError =>
        CompleteOutcome != null && CompleteOutcome.HasError && !CompleteHasPrice;

    public bool CompleteHasConfigurationHint =>
        CompleteOutcome != null && CompleteOutcome.IsConfigurationHint && !CompleteHasPrice;

    /// <summary>Gemeinsamer Hinweis-Text fuer Error- ODER Configuration-Banner.</summary>
    public string? CompleteHintMessage => CompleteOutcome?.ErrorMessage;

    public bool ShowCompleteIdleButton => !IsCompleteRequested;
    public bool ShowCompleteLoadedContent =>
        IsCompleteRequested && !IsCompleteLoading && CompleteHasPrice;
    public bool ShowCompleteIssueBanner =>
        IsCompleteRequested && !IsCompleteLoading
        && (CompleteHasError || CompleteHasConfigurationHint);
    public bool ShowCompleteRefreshIcon =>
        IsCompleteRequested && !IsCompleteLoading && CompleteHasPrice;

    public string CompletePriceLabel => FormatMoney(CompleteCorrectedPrice);

    public string? CompletePriceTooltip
    {
        get
        {
            if (!CompleteRawPrice.HasValue || !CompleteCorrectedPrice.HasValue) return null;
            if (CompleteRawPrice.Value == CompleteCorrectedPrice.Value) return null;
            return $"Roh: {FormatMoney(CompleteRawPrice)} • Korrigiert: {FormatMoney(CompleteCorrectedPrice)}";
        }
    }

    public string CompleteMinMaxLabel
    {
        get
        {
            if (!CompleteCorrectedMin.HasValue && !CompleteCorrectedMax.HasValue) return string.Empty;
            return $"Min {FormatMoney(CompleteCorrectedMin)} • Max {FormatMoney(CompleteCorrectedMax)}";
        }
    }

    public string CompleteListingsLabel
    {
        get
        {
            var p = CompleteOutcome?.Price;
            if (p == null) return string.Empty;
            return $"{p.UnitQuantity} Listings";
        }
    }

    public string CompleteFetchedAtLabel => FormatFetchedAt(CompleteOutcome?.FetchedAt);

    // ===== Einzelteile (rechte Haelfte) =====

    public ObservableCollection<PartPriceRowViewModel> PartRows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPartsIdleButton))]
    [NotifyPropertyChangedFor(nameof(ShowPartsLoadedContent))]
    [NotifyPropertyChangedFor(nameof(ShowPartsRefreshIcon))]
    private bool _isPartsRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPartsLoadedContent))]
    [NotifyPropertyChangedFor(nameof(ShowPartsRefreshIcon))]
    [NotifyPropertyChangedFor(nameof(PartsLoadingLabel))]
    private bool _isPartsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PartsTotalLabel))]
    [NotifyPropertyChangedFor(nameof(PartsHasAnyPrice))]
    [NotifyPropertyChangedFor(nameof(ShowPartsLoadedContent))]
    [NotifyPropertyChangedFor(nameof(ShowPartsRefreshIcon))]
    [NotifyPropertyChangedFor(nameof(HasRecommendation))]
    [NotifyPropertyChangedFor(nameof(RecommendationText))]
    private decimal _partsTotalSum;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PartsMissingLabel))]
    private int _partsMissingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartsCorrectionHint))]
    private string? _partsCorrectionHint;

    /// <summary>
    /// Echter Top-Level-Fehler im Parts-Pfad (nicht pro Zeile - das laeuft
    /// per row.Outcome.HasError). Tritt nur in seltenen Faellen auf
    /// (z.B. Cancellation oder unerwartete Exception in LoadPartsAsync).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PartsHasError))]
    [NotifyPropertyChangedFor(nameof(ShowPartsIssueBanner))]
    [NotifyPropertyChangedFor(nameof(ShowPartsLoadedContent))]
    private string? _partsErrorMessage;

    /// <summary>
    /// Wenn der Provider nicht konfiguriert ist, kommt diese Meldung von
    /// jeder Row gleich. Wir heben den Hinweis hier hoch, damit die Liste
    /// kein "X Teile ohne Preis" zeigt sondern ein klareres Banner.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PartsHasConfigurationHint))]
    [NotifyPropertyChangedFor(nameof(ShowPartsIssueBanner))]
    [NotifyPropertyChangedFor(nameof(ShowPartsLoadedContent))]
    private string? _partsConfigurationHint;

    public bool HasPartsCorrectionHint => !string.IsNullOrEmpty(PartsCorrectionHint);

    public bool PartsHasAnyPrice => PartsTotalSum > 0m;
    public bool PartsHasError => !string.IsNullOrEmpty(PartsErrorMessage);
    public bool PartsHasConfigurationHint => !string.IsNullOrEmpty(PartsConfigurationHint);

    public bool ShowPartsIdleButton => !IsPartsRequested;
    public bool ShowPartsLoadedContent =>
        IsPartsRequested && !IsPartsLoading
        && !PartsHasError && !PartsHasConfigurationHint;
    public bool ShowPartsIssueBanner =>
        IsPartsRequested && !IsPartsLoading
        && (PartsHasError || PartsHasConfigurationHint);
    public bool ShowPartsRefreshIcon =>
        IsPartsRequested && !IsPartsLoading && PartsHasAnyPrice;

    public string PartsTotalLabel => $"Summe: {FormatMoney(PartsTotalSum)}";

    public string PartsMissingLabel => PartsMissingCount == 0
        ? string.Empty
        : $"{PartsMissingCount} Teile ohne Preis";

    public string PartsLoadingLabel
        => $"Lade {_subsets.Count} Teile...";

    /// <summary>Gemeinsame Hinweis-Message (Error oder Config) fuer den Parts-Banner.</summary>
    public string? PartsHintMessage => PartsConfigurationHint ?? PartsErrorMessage;

    // ===== Empfehlung =====

    /// <summary>
    /// Empfehlung erscheint nur wenn BEIDE Bereiche erfolgreich geladen sind.
    /// Solange einer noch idle / loading / error ist: keine Empfehlung.
    /// </summary>
    public bool HasRecommendation
        => CompleteHasPrice && PartsHasAnyPrice && !string.IsNullOrEmpty(RecommendationText);

    public string? RecommendationText
    {
        get
        {
            if (!CompleteCorrectedPrice.HasValue || PartsTotalSum <= 0m) return null;
            var diff = CompleteCorrectedPrice.Value - PartsTotalSum;
            var basis = Math.Max(CompleteCorrectedPrice.Value, PartsTotalSum);
            var diffPct = basis > 0 ? Math.Abs(diff) / basis * 100m : 0m;

            if (diffPct < RecommendationThresholdPercent)
                return "Komplett und Einzelteile etwa gleich wert.";
            return diff > 0
                ? $"Komplett verkaufen lohnt sich mehr (+{FormatMoney(diff)})"
                : $"Einzelteile verkaufen lohnt sich mehr (+{FormatMoney(-diff)})";
        }
    }

    public MinifigPriceViewModel(
        IBlPriceCacheService cache,
        ISettingsService settings,
        string blMinifigId,
        IReadOnlyList<(string PartNo, int ColorId, int QuantityNeeded, string PartName)> subsets)
    {
        _cache = cache;
        _settings = settings;
        _blMinifigId = blMinifigId;
        _subsets = subsets;

        // Pre-Fill der PartRows damit die UI sofort einen Skeleton zeigt
        // sobald Auto-Mode den Load triggert oder der User klickt.
        foreach (var s in subsets)
        {
            PartRows.Add(new PartPriceRowViewModel
            {
                PartNo = s.PartNo,
                ColorId = s.ColorId,
                PartName = s.PartName,
                QuantityNeeded = s.QuantityNeeded
            });
        }

        // Korrektur-Hinweise vorab setzen (unabhaengig vom Lade-Modus).
        var cfg = _settings.Current.Prices;
        MinifigCorrectionHint = BuildCorrectionHint(cfg.CorrectionMinifigPercent);
        PartsCorrectionHint   = BuildCorrectionHint(cfg.CorrectionPartsPercent);

        // UX-Iteration X.10: Auto-Modes aus den Settings honorieren.
        // Manuell wird einfach gar nichts getan - der Idle-Button bleibt
        // sichtbar bis der User klickt. Tasks werden behalten damit Tests
        // sauber awaiten koennen (Produktions-Code braucht das nicht).
        if (cfg.AutoLoadCompletePrice == PriceLoadMode.Auto)
            CompleteAutoLoadTask = LoadCompleteCoreAsync();
        if (cfg.AutoLoadPartsPrice == PriceLoadMode.Auto)
            PartsAutoLoadTask = LoadPartsCoreAsync();
    }

    // ===== Public Commands (Klick-Handler) =====

    /// <summary>
    /// Manuelles Laden der Komplett-Figur. Geht durch den ganz normalen
    /// Cache-First-Pfad (Stale-While-Revalidate). KEIN Force-Refresh -
    /// wenn der Cache einen frischen Eintrag hat, wird gar keine API-Call
    /// gemacht.
    /// </summary>
    [RelayCommand]
    public Task LoadCompleteAsync(CancellationToken ct = default)
        => LoadCompleteCoreAsync(ct);

    /// <summary>Manuelles Laden der Einzelteile-Liste. Cache-First, dann Provider.</summary>
    [RelayCommand]
    public Task LoadPartsAsync(CancellationToken ct = default)
        => LoadPartsCoreAsync(ct);

    /// <summary>
    /// Force-Refresh der Komplett-Figur: loescht den Cache-Eintrag fuer die
    /// aktuelle GuideType+Region+Currency-Kombination und triggert einen
    /// Live-Call. Nur sichtbar wenn schon Daten geladen sind (kein blinder
    /// Refresh im leeren Zustand).
    /// </summary>
    [RelayCommand]
    public async Task RefreshCompleteAsync()
    {
        if (IsCompleteLoading) return;
        try
        {
            await _cache.DeleteMinifigPriceAsync(_blMinifigId);
            ResetCompleteState();
            await LoadCompleteCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MinifigPriceVM: RefreshComplete fehlgeschlagen");
            CompleteOutcome = new PriceLookupOutcome(
                null, PriceLookupSource.None, null, $"Refresh fehlgeschlagen: {ex.Message}",
                PriceLookupNotice.Error);
        }
    }

    /// <summary>Force-Refresh der Einzelteile-Liste.</summary>
    [RelayCommand]
    public async Task RefreshPartsAsync()
    {
        if (IsPartsLoading) return;
        try
        {
            var subsetSpecs = _subsets.Select(s => (s.PartNo, s.ColorId)).ToList();
            await _cache.DeletePartPricesAsync(subsetSpecs);
            ResetPartsState();
            await LoadPartsCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MinifigPriceVM: RefreshParts fehlgeschlagen");
            PartsErrorMessage = $"Refresh fehlgeschlagen: {ex.Message}";
        }
    }

    // ===== Internals =====

    private async Task LoadCompleteCoreAsync(CancellationToken ct = default)
    {
        IsCompleteRequested = true;
        IsCompleteLoading = true;
        try
        {
            // Cache-First, dann Provider - der IBlPriceCacheService kapselt
            // Stale-While-Revalidate. Der Auto/Manual-Modus aendert nur
            // WANN dieser Aufruf stattfindet, NICHT was er macht.
            CompleteOutcome = await _cache.GetMinifigPriceAsync(_blMinifigId, ct);
            ApplyCompleteOutcomeToView(CompleteOutcome);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MinifigPriceVM: Komplett-Lookup geworfen");
            CompleteOutcome = new PriceLookupOutcome(
                null, PriceLookupSource.None, null, ex.Message,
                PriceLookupNotice.Error);
            ApplyCompleteOutcomeToView(CompleteOutcome);
        }
        finally
        {
            IsCompleteLoading = false;
        }
    }

    private void ApplyCompleteOutcomeToView(PriceLookupOutcome? outcome)
    {
        var cfg = _settings.Current.Prices;
        var p = outcome?.Price;

        var rawAvg = PriceMath.PickValue(p, cfg.PriceColumn);
        CompleteRawPrice = rawAvg;
        CompleteCorrectedPrice = PriceMath.ApplyCorrection(rawAvg, cfg.CorrectionMinifigPercent);

        CompleteRawMin = p?.MinPrice;
        CompleteRawMax = p?.MaxPrice;
        CompleteCorrectedMin = PriceMath.ApplyCorrection(p?.MinPrice, cfg.CorrectionMinifigPercent);
        CompleteCorrectedMax = PriceMath.ApplyCorrection(p?.MaxPrice, cfg.CorrectionMinifigPercent);
    }

    private async Task LoadPartsCoreAsync(CancellationToken ct = default)
    {
        IsPartsRequested = true;
        IsPartsLoading = true;
        PartsErrorMessage = null;
        PartsConfigurationHint = null;
        try
        {
            var cfg = _settings.Current.Prices;
            // Pro Teile-Zeile parallel den Preis ueber den Cache holen.
            // Cache-First-Pfad pro Row - identisch zur Komplett-Haelfte.
            var rowTasks = PartRows.Select(async row =>
            {
                row.IsLoading = true;
                try
                {
                    var outcome = await _cache.GetPartPriceAsync(row.PartNo, row.ColorId, ct);
                    row.Outcome = outcome;
                    ApplyPartOutcomeToRow(row, outcome, cfg);

                    // Ein Configuration-Hint von einer Row ist universell -
                    // alle Rows wuerden denselben sehen. Wir ziehen den nach
                    // oben damit die UI ein einziges grosses Banner zeigt
                    // statt N redundante "—".
                    if (outcome != null && outcome.IsConfigurationHint
                        && PartsConfigurationHint == null)
                    {
                        PartsConfigurationHint = outcome.ErrorMessage;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "MinifigPriceVM: Part-Lookup geworfen ({Part}/{Color})",
                        row.PartNo, row.ColorId);
                    row.Outcome = new PriceLookupOutcome(
                        null, PriceLookupSource.None, null, ex.Message,
                        PriceLookupNotice.Error);
                    ApplyPartOutcomeToRow(row, row.Outcome, cfg);
                }
                finally
                {
                    row.IsLoading = false;
                }
            }).ToArray();

            await Task.WhenAll(rowTasks);

            RecalculatePartsTotal();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MinifigPriceVM: LoadParts geworfen");
            PartsErrorMessage = ex.Message;
        }
        finally
        {
            IsPartsLoading = false;
        }
    }

    private static void ApplyPartOutcomeToRow(
        PartPriceRowViewModel row, PriceLookupOutcome? outcome, PriceSettings cfg)
    {
        var raw = PriceMath.PickValue(outcome?.Price, cfg.PriceColumn);
        row.UnitPriceRaw = raw;
        row.UnitPriceCorrected = PriceMath.ApplyCorrection(raw, cfg.CorrectionPartsPercent);
    }

    private void RecalculatePartsTotal()
    {
        decimal total = 0m;
        int missing = 0;
        foreach (var row in PartRows)
        {
            var unit = row.UnitPriceCorrected;
            if (unit.HasValue && unit.Value > 0)
            {
                total += unit.Value * row.QuantityNeeded;
            }
            else
            {
                missing++;
            }
        }
        PartsTotalSum = Math.Round(total, 2, MidpointRounding.AwayFromZero);
        PartsMissingCount = missing;
    }

    private void ResetCompleteState()
    {
        CompleteOutcome = null;
        CompleteRawPrice = null;
        CompleteCorrectedPrice = null;
        CompleteRawMin = null;
        CompleteRawMax = null;
        CompleteCorrectedMin = null;
        CompleteCorrectedMax = null;
    }

    private void ResetPartsState()
    {
        foreach (var row in PartRows)
        {
            row.Outcome = null;
            row.UnitPriceRaw = null;
            row.UnitPriceCorrected = null;
        }
        PartsTotalSum = 0m;
        PartsMissingCount = 0;
        PartsErrorMessage = null;
        PartsConfigurationHint = null;
    }

    // ===== Helpers =====

    private static string BuildCorrectionHint(decimal correctionPercent)
    {
        if (correctionPercent == 0m) return string.Empty;
        var sign = correctionPercent > 0 ? "+" : string.Empty;
        return $"Inkl. {sign}{correctionPercent:0.#}% Korrektur";
    }

    private static string FormatMoney(decimal? value)
    {
        if (!value.HasValue || value.Value <= 0) return "—";
        return value.Value.ToString("N2", CultureInfo.GetCultureInfo("de-DE")) + " €";
    }

    private static string FormatFetchedAt(DateTime? utc)
        => utc.HasValue
            ? "Daten vom " + utc.Value.ToLocalTime().ToString("dd.MM.yyyy")
            : string.Empty;
}

/// <summary>Eine Zeile in der Einzelteil-Liste der oberen rechten Box.</summary>
public partial class PartPriceRowViewModel : ObservableObject
{
    public string PartNo { get; init; } = string.Empty;
    public int ColorId { get; init; }
    public string PartName { get; init; } = string.Empty;
    public int QuantityNeeded { get; init; }

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    private PriceLookupOutcome? _outcome;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitPriceLabel))]
    [NotifyPropertyChangedFor(nameof(SubtotalLabel))]
    [NotifyPropertyChangedFor(nameof(UnitPriceTooltip))]
    [NotifyPropertyChangedFor(nameof(HasPrice))]
    private decimal? _unitPriceRaw;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitPrice))]
    [NotifyPropertyChangedFor(nameof(UnitPriceLabel))]
    [NotifyPropertyChangedFor(nameof(SubtotalLabel))]
    [NotifyPropertyChangedFor(nameof(UnitPriceTooltip))]
    [NotifyPropertyChangedFor(nameof(HasPrice))]
    private decimal? _unitPriceCorrected;

    public decimal? UnitPrice => UnitPriceCorrected;

    public bool HasPrice => UnitPriceCorrected.HasValue && UnitPriceCorrected.Value > 0;

    public string UnitPriceLabel
    {
        get
        {
            var u = UnitPriceCorrected;
            if (!u.HasValue || u.Value <= 0) return "—";
            return Format(u.Value);
        }
    }

    public string SubtotalLabel
    {
        get
        {
            var u = UnitPriceCorrected;
            if (!u.HasValue || u.Value <= 0) return "—";
            var sub = u.Value * QuantityNeeded;
            return Format(sub);
        }
    }

    public string? UnitPriceTooltip
    {
        get
        {
            if (!UnitPriceRaw.HasValue || !UnitPriceCorrected.HasValue) return null;
            if (UnitPriceRaw.Value == UnitPriceCorrected.Value) return null;
            return $"Roh: {Format(UnitPriceRaw.Value)} • Korrigiert: {Format(UnitPriceCorrected.Value)}";
        }
    }

    public string DisplayName => $"{QuantityNeeded}× {PartName}";

    private static string Format(decimal value)
        => value.ToString("N2", CultureInfo.GetCultureInfo("de-DE")) + " €";
}
