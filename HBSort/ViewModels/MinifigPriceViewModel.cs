using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Models.Pricing;
using HBSort.Core.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// UX#12: ViewModel fuer die Preis-Anzeige in der oberen rechten Box des
/// Sortier-Tabs. Wird vom ScanViewModel angelegt sobald eine Pending-Minifig
/// existiert (gerade gescannt, noch nicht persistiert).
///
/// Layout:
///   LINKS  - "Komplette Figur": Min/Avg/Max + Anzahl Listings + Datum
///   RECHTS - "Einzelteile aufsummiert": Liste pro Subset + Summe
///   UNTEN  - optional: Empfehlung "Komplett vs Einzelteile"
///
/// Lade-Strategie:
///   1) Alle Lookups parallel (Task.WhenAll) gegen IBlPriceCacheService.
///   2) Stale-While-Revalidate ist im Service gekapselt - das VM bekommt
///      direkt den schnellen (ggf. stale) Wert.
///   3) IsLoading-Flags fuer Komplett- und Parts-Bereich getrennt.
///
/// Refresh-Button: loescht den Cache fuer diese Figur + alle Subsets und
/// startet den Load erneut.
/// </summary>
public partial class MinifigPriceViewModel : ObservableObject
{
    /// <summary>
    /// Schwelle in Prozent ab der eine "klare Empfehlung" gegeben wird.
    /// Unter 10% wird "Equal" angezeigt (kein Vor-/Nachteil).
    /// </summary>
    private const decimal RecommendationThresholdPercent = 10m;

    private readonly IBlPriceCacheService _cache;
    private readonly string _blMinifigId;
    private readonly IReadOnlyList<(string PartNo, int ColorId, int QuantityNeeded, string PartName)> _subsets;

    public string BlMinifigId => _blMinifigId;

    // ===== Komplett-Figur (linke Haelfte) =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompletePriceLabel))]
    [NotifyPropertyChangedFor(nameof(CompleteMinMaxLabel))]
    [NotifyPropertyChangedFor(nameof(CompleteListingsLabel))]
    [NotifyPropertyChangedFor(nameof(CompleteFetchedAtLabel))]
    [NotifyPropertyChangedFor(nameof(CompleteHasPrice))]
    [NotifyPropertyChangedFor(nameof(IsCompleteStale))]
    private PriceLookupOutcome? _completeOutcome;

    [ObservableProperty] private bool _isCompleteLoading;

    public bool CompleteHasPrice => CompleteOutcome?.HasPrice == true;
    public bool IsCompleteStale => CompleteOutcome?.Source == PriceLookupSource.Stale;

    public string CompletePriceLabel
        => FormatMoney(CompleteOutcome?.Price?.AvgPrice
                       ?? CompleteOutcome?.Price?.QtyAvgPrice);

    public string CompleteMinMaxLabel
    {
        get
        {
            var p = CompleteOutcome?.Price;
            if (p == null) return string.Empty;
            return $"Min {FormatMoney(p.MinPrice)} • Max {FormatMoney(p.MaxPrice)}";
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

    [ObservableProperty] private bool _isPartsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PartsTotalLabel))]
    private decimal _partsTotalSum;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PartsMissingLabel))]
    private int _partsMissingCount;

    public string PartsTotalLabel => $"Summe: {FormatMoney(PartsTotalSum)}";

    public string PartsMissingLabel => PartsMissingCount == 0
        ? string.Empty
        : $"{PartsMissingCount} Teile ohne Preis";

    // ===== Empfehlung =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecommendation))]
    private string? _recommendationText;

    public bool HasRecommendation => !string.IsNullOrEmpty(RecommendationText);

    /// <summary>
    /// Echter Fehler (rotes Banner): API nicht erreichbar, Token abgelaufen,
    /// Exception im Provider. Plan B (Bugfix): wird aus dem Outcome
    /// hochgehoben falls Source=None mit Notice=Error.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Konfigurations-Hinweis (oranges Banner): Provider="None", BL-Token
    /// fehlt. Plan B+C (Bugfix): andere Optik als der rote Fehler-Banner -
    /// das ist kein Drama, der User muss nur noch zur Settings.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigurationHint))]
    private string? _configurationHint;

    public bool HasConfigurationHint => !string.IsNullOrEmpty(ConfigurationHint);

    public MinifigPriceViewModel(
        IBlPriceCacheService cache,
        string blMinifigId,
        IReadOnlyList<(string PartNo, int ColorId, int QuantityNeeded, string PartName)> subsets)
    {
        _cache = cache;
        _blMinifigId = blMinifigId;
        _subsets = subsets;

        // Pre-Fill der PartRows damit die UI sofort einen Skeleton zeigt
        // und nicht zwischendurch einen leeren Zustand.
        foreach (var s in subsets)
        {
            PartRows.Add(new PartPriceRowViewModel
            {
                PartNo = s.PartNo,
                ColorId = s.ColorId,
                PartName = s.PartName,
                QuantityNeeded = s.QuantityNeeded,
                IsLoading = true
            });
        }
    }

    /// <summary>
    /// Startet das parallele Laden von Komplett-Figur + allen Subset-Teilen.
    /// Stale-While-Revalidate steckt im Cache-Service - das VM sieht das nicht.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        ErrorMessage = null;
        ConfigurationHint = null;
        IsCompleteLoading = true;
        IsPartsLoading = true;

        // Komplett-Figur (linke Haelfte) und alle Parts (rechte Haelfte)
        // gleichzeitig laden - kein Warten aufeinander.
        var completeTask = LoadCompleteAsync(ct);
        var partsTask = LoadPartsAsync(ct);
        await Task.WhenAll(completeTask, partsTask);

        // Plan B: Outcome-Probleme an die UI durchreichen, damit der User
        // weiss WARUM nichts kommt. Wir gucken auf den Komplett-Outcome -
        // er ist der Hauptanker; wenn der "Provider not configured" hat,
        // gilt das fuer alle Parts genauso.
        PromoteOutcomeNoticeToBanner(CompleteOutcome);

        UpdateRecommendation();
    }

    /// <summary>
    /// Hebt eine Notice aus dem Outcome auf das passende View-Property.
    /// - NotConfigured -&gt; ConfigurationHint (orange)
    /// - Error         -&gt; ErrorMessage      (rot)
    /// - None          -&gt; nichts tun
    /// Plan B (Bugfix): bisher blieb der Outcome-ErrorMessage stumm,
    /// dadurch sah der User nur eine leere Box.
    /// </summary>
    private void PromoteOutcomeNoticeToBanner(PriceLookupOutcome? outcome)
    {
        if (outcome == null) return;
        if (outcome.HasPrice) return; // Preis vorhanden -&gt; kein Banner.

        if (outcome.IsConfigurationHint)
        {
            ConfigurationHint = outcome.ErrorMessage;
        }
        else if (outcome.HasError)
        {
            ErrorMessage = outcome.ErrorMessage;
        }
    }

    private async Task LoadCompleteAsync(CancellationToken ct)
    {
        try
        {
            CompleteOutcome = await _cache.GetMinifigPriceAsync(_blMinifigId, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MinifigPriceVM: Komplett-Lookup geworfen");
            CompleteOutcome = new PriceLookupOutcome(
                null, PriceLookupSource.None, null, ex.Message);
        }
        finally
        {
            IsCompleteLoading = false;
        }
    }

    private async Task LoadPartsAsync(CancellationToken ct)
    {
        try
        {
            // Pro Teile-Zeile parallel den Preis ueber den Cache holen.
            // Wir setzen das Ergebnis direkt in die jeweilige Zeile statt
            // erst alle zu sammeln - so sieht die UI Werte progressiv.
            var rowTasks = PartRows.Select(async row =>
            {
                try
                {
                    var outcome = await _cache.GetPartPriceAsync(row.PartNo, row.ColorId, ct);
                    row.Outcome = outcome;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "MinifigPriceVM: Part-Lookup geworfen ({Part}/{Color})",
                        row.PartNo, row.ColorId);
                    row.Outcome = new PriceLookupOutcome(
                        null, PriceLookupSource.None, null, ex.Message);
                }
                finally
                {
                    row.IsLoading = false;
                }
            }).ToArray();

            await Task.WhenAll(rowTasks);

            RecalculatePartsTotal();
        }
        finally
        {
            IsPartsLoading = false;
        }
    }

    private void RecalculatePartsTotal()
    {
        decimal total = 0m;
        int missing = 0;
        foreach (var row in PartRows)
        {
            var unit = row.UnitPrice;
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

    /// <summary>
    /// Empfehlung "Komplett verkaufen lohnt sich mehr (+X,XX €)" /
    /// "Einzelteile verkaufen lohnt sich mehr (+X,XX €)" / "etwa gleich".
    /// Nur wenn beide Seiten Daten haben.
    /// </summary>
    private void UpdateRecommendation()
    {
        var completeAvg = CompleteOutcome?.Price?.AvgPrice
                          ?? CompleteOutcome?.Price?.QtyAvgPrice;
        var partsTotal = PartsTotalSum;

        if (!completeAvg.HasValue || partsTotal <= 0m)
        {
            RecommendationText = null;
            return;
        }

        var diff = completeAvg.Value - partsTotal;
        var basis = Math.Max(completeAvg.Value, partsTotal);
        var diffPct = basis > 0 ? Math.Abs(diff) / basis * 100m : 0m;

        if (diffPct < RecommendationThresholdPercent)
        {
            RecommendationText = "Komplett und Einzelteile etwa gleich wert.";
        }
        else if (diff > 0)
        {
            RecommendationText = $"Komplett verkaufen lohnt sich mehr (+{FormatMoney(diff)})";
        }
        else
        {
            RecommendationText = $"Einzelteile verkaufen lohnt sich mehr (+{FormatMoney(-diff)})";
        }
    }

    /// <summary>
    /// Pro-Eintrag-Refresh ueber den ↻-Button: loescht den Cache fuer diese
    /// Figur + alle Subset-Teile und laed neu. In-Flight-Schutz im Service
    /// verhindert dass paralleles Klicken Doppel-API-Calls ausloest.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        // Wenn gerade noch ein Load laeuft, ignorieren wir den Klick - der
        // In-Flight-Schutz im Cache-Service wuerde sowieso nichts doppelt
        // machen, aber so bleibt das UI konsistent.
        if (IsCompleteLoading || IsPartsLoading) return;

        try
        {
            var subsetSpecs = _subsets.Select(s => (s.PartNo, s.ColorId)).ToList();
            await _cache.DeleteForMinifigAsync(_blMinifigId, subsetSpecs);

            // PartRows resetten auf Loading-Zustand.
            foreach (var row in PartRows)
            {
                row.Outcome = null;
                row.IsLoading = true;
            }
            CompleteOutcome = null;
            PartsTotalSum = 0m;
            PartsMissingCount = 0;
            RecommendationText = null;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MinifigPriceVM: Refresh fehlgeschlagen");
            ErrorMessage = $"Refresh fehlgeschlagen: {ex.Message}";
        }
    }

    /// <summary>"12,34 €" / "—" wenn null.</summary>
    private static string FormatMoney(decimal? value)
    {
        if (!value.HasValue || value.Value <= 0) return "—";
        return value.Value.ToString("N2", CultureInfo.GetCultureInfo("de-DE")) + " €";
    }

    /// <summary>"Daten vom 03.05.2026" / leer wenn null.</summary>
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
    [NotifyPropertyChangedFor(nameof(UnitPrice))]
    [NotifyPropertyChangedFor(nameof(UnitPriceLabel))]
    [NotifyPropertyChangedFor(nameof(SubtotalLabel))]
    [NotifyPropertyChangedFor(nameof(HasPrice))]
    private PriceLookupOutcome? _outcome;

    public decimal? UnitPrice
        => Outcome?.Price?.AvgPrice ?? Outcome?.Price?.QtyAvgPrice;

    public bool HasPrice => UnitPrice.HasValue && UnitPrice.Value > 0;

    public string UnitPriceLabel
    {
        get
        {
            var u = UnitPrice;
            if (!u.HasValue || u.Value <= 0) return "—";
            return u.Value.ToString("N2", CultureInfo.GetCultureInfo("de-DE")) + " €";
        }
    }

    public string SubtotalLabel
    {
        get
        {
            var u = UnitPrice;
            if (!u.HasValue || u.Value <= 0) return "—";
            var sub = u.Value * QuantityNeeded;
            return sub.ToString("N2", CultureInfo.GetCultureInfo("de-DE")) + " €";
        }
    }

    public string DisplayName => $"{QuantityNeeded}× {PartName}";
}
