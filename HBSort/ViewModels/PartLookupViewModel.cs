using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;

namespace HBSort.ViewModels;

/// <summary>
/// "Pending" Teil: ist erkannt + Color-Vermutung + Lookup gegen wartende Figuren.
/// Wird in der PartLookupView gerendert. Persistierung passiert beim Klick auf
/// "Zuordnen" (assign zur Figur), "Lagern" (Floating-Part) oder "Diese Figur sammeln".
/// </summary>
public partial class PartLookupViewModel : ObservableObject
{
    /// <summary>BL-Part-No, z.B. "3626pb0810".</summary>
    public string BlPartNo { get; }

    /// <summary>Aktuelle BL-Color-Id (kann via Korrektur-Dropdown geaendert werden).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorDisplayLabel))]
    private int _blColorId;

    [ObservableProperty]
    private string _partName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorDisplayLabel))]
    private string _colorName = string.Empty;

    /// <summary>RGB-Hex (ohne #) – fuer Swatch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SwatchBrush))]
    private string? _colorRgb;

    /// <summary>Bild des Teils (URL/Pfad). Wird via PartImageProvider asynchron befuellt.</summary>
    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>Anzahl der gescannten Teile (Default 1, vom User editierbar).</summary>
    [ObservableProperty]
    private int _quantity = 1;

    /// <summary>Treffer in wartenden Figuren.</summary>
    public ObservableCollection<WaitingMinifigMatchViewModel> WaitingMatches { get; } = new();

    /// <summary>
    /// Treffer im BL-Catalog-Cache (Minifigs deren Subsets schon mal gecached
    /// wurden) – die der User aber noch NICHT als wartende Figur hat. "Diese
    /// Figur sammeln"-Aktion legt die Figur an + setzt das Trigger-Teil collected.
    /// </summary>
    public ObservableCollection<BlCatalogMatchViewModel> BlCatalogMatches { get; } = new();

    public bool HasBlCatalogMatches => BlCatalogMatches.Count > 0;

    /// <summary>Verfuegbare Farben fuer das Korrektur-Dropdown (nur known colors).</summary>
    public ObservableCollection<BlColor> AvailableColors { get; } = new();

    /// <summary>True waehrend GetKnownColors laeuft.</summary>
    [ObservableProperty]
    private bool _isLoadingColors;

    /// <summary>Aktuell ausgewaehlte Farbe im Dropdown (Two-Way).</summary>
    [ObservableProperty]
    private BlColor? _selectedColor;

    /// <summary>Lagerfaecher fuer Floating-Part-Lager-Combo.</summary>
    public ObservableCollection<StorageBin> AvailableBins { get; } = new();

    [ObservableProperty]
    private StorageBin? _selectedFloatingBin;

    /// <summary>Anzahl Teile fuer Floating-Lagerung.</summary>
    [ObservableProperty]
    private int _floatingQuantity = 1;

    // ====================================================================
    // UX-Iteration X.7: Smart-Storage-Suggestion fuer "Als Einzelteil lagern"
    // ====================================================================

    /// <summary>
    /// True wenn das Teil bereits in mind. einem Lagerfach mit gleicher
    /// Farbe vorhanden ist. Steuert Visibility des Hint-TextBlocks unter
    /// dem "Als Einzelteil lagern"-Dropdown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchingFloatingBinHintText))]
    private bool _hasMatchingFloatingBin;

    /// <summary>Bin-Label des besten Vorschlags ("Box 003").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchingFloatingBinHintText))]
    private string? _matchingFloatingBinLabel;

    /// <summary>Wieviele Teile bereits im vorgeschlagenen Fach liegen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchingFloatingBinHintText))]
    private int _matchingFloatingBinQuantity;

    /// <summary>
    /// Anzahl der Faecher mit Treffern (1..n). Bei &gt;1 wird der Hint-Text
    /// um "und N weiteren Faechern" erweitert.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchingFloatingBinHintText))]
    private int _matchingFloatingBinCount;

    /// <summary>
    /// Anzeige-Text "📦 Liegt schon in Box XXX (3x)" oder
    /// "📦 Liegt schon in Box XXX (3x) und 2 weiteren Faechern".
    /// Leer wenn kein Match.
    /// </summary>
    public string MatchingFloatingBinHintText
    {
        get
        {
            if (!HasMatchingFloatingBin || string.IsNullOrEmpty(MatchingFloatingBinLabel))
                return string.Empty;

            var basis = $"📦 Liegt schon in {MatchingFloatingBinLabel} ({MatchingFloatingBinQuantity}x)";
            if (MatchingFloatingBinCount <= 1) return basis;

            var extra = MatchingFloatingBinCount - 1;
            return extra == 1
                ? basis + " und 1 weiterem Fach"
                : basis + $" und {extra} weiteren Faechern";
        }
    }

    /// <summary>True waehrend irgendeine Aktion laeuft (greyout aller Buttons).</summary>
    [ObservableProperty]
    private bool _isBusy;

    public bool HasWaitingMatches => WaitingMatches.Count > 0;

    public string ColorDisplayLabel
        => $"{ColorName} (BL:{BlColorId.ToString(CultureInfo.InvariantCulture)})";

    public Brush SwatchBrush => ParseRgbBrush(ColorRgb);

    public PartLookupViewModel(string blPartNo, int blColorId)
    {
        BlPartNo = blPartNo;
        _blColorId = blColorId;
    }

    /// <summary>Befuellt die Felder aus einem PartLookupResult.</summary>
    public void ApplyLookupResult(PartLookupResult r)
    {
        PartName = r.PartName;
        ColorName = r.ColorName;
        ColorRgb = r.ColorRgb;
        BlColorId = r.BlColorId;

        WaitingMatches.Clear();
        foreach (var m in r.WaitingMatches)
            WaitingMatches.Add(new WaitingMinifigMatchViewModel(m));
        OnPropertyChanged(nameof(HasWaitingMatches));

        BlCatalogMatches.Clear();
        foreach (var m in r.BlCatalogMatches)
            BlCatalogMatches.Add(new BlCatalogMatchViewModel(m));
        OnPropertyChanged(nameof(HasBlCatalogMatches));
    }

    private static Brush ParseRgbBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Brushes.Gray;
        var clean = hex.TrimStart('#');
        if (clean.Length != 6) return Brushes.Gray;
        try
        {
            var r = byte.Parse(clean.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var g = byte.Parse(clean.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var b = byte.Parse(clean.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
        catch { return Brushes.Gray; }
    }
}

/// <summary>
/// Eine Karte in der WaitingMatches-Liste der PartLookupView.
/// Wraps das Core-Match-Record mit zusaetzlichen UI-Eigenschaften (Bild-URL,
/// kompaktes Anzeige-Label).
/// </summary>
public partial class WaitingMinifigMatchViewModel : ObservableObject
{
    public int TrackedMinifigPartId { get; }
    public int TrackedMinifigId { get; }
    public string BlMinifigId { get; }
    public string MinifigName { get; }
    public string? StorageBinLabel { get; }
    public int StorageBinId { get; }
    public int QuantityNeeded { get; }
    public int QuantityCollected { get; }
    public bool IsAlternate { get; }

    [ObservableProperty]
    private string? _imageUrl;

    public string ProgressLabel => $"braucht {QuantityNeeded - QuantityCollected}x  -  schon {QuantityCollected}/{QuantityNeeded}";
    public string BinLabel => string.IsNullOrEmpty(StorageBinLabel) ? "(kein Fach)" : $"({StorageBinLabel})";

    public WaitingMinifigMatchViewModel(WaitingMinifigMatch m)
    {
        TrackedMinifigPartId = m.TrackedMinifigPartId;
        TrackedMinifigId = m.TrackedMinifigId;
        BlMinifigId = m.BlMinifigId;
        MinifigName = m.MinifigName;
        StorageBinLabel = m.StorageBinLabel;
        StorageBinId = m.StorageBinId;
        QuantityNeeded = m.QuantityNeeded;
        QuantityCollected = m.QuantityCollected;
        IsAlternate = m.IsAlternate;
        _imageUrl = m.MinifigImageUrl;
    }
}

/// <summary>
/// Eine BL-Catalog-Treffer-Karte in der PartLookupView. Wird angezeigt wenn
/// das gescannte Teil in einer Minifig vorkommt, die der User aber noch nicht
/// als wartende Figur angelegt hat. "Diese Figur anlegen"-Button triggert
/// CollectMinifigFromSupersetAsync.
/// </summary>
public partial class BlCatalogMatchViewModel : ObservableObject
{
    public string BlMinifigId { get; }
    public string MinifigName { get; }
    public int QuantityInMinifig { get; }

    [ObservableProperty]
    private string? _imageUrl;

    public string QuantityLabel => $"x{QuantityInMinifig}";

    public BlCatalogMatchViewModel(BlCatalogMatch m)
    {
        BlMinifigId = m.BlMinifigId;
        // Fallback auf BL-ID wenn der bl_items-Cache den Namen noch nicht hat
        // (z.B. Eintrag kommt aus einem GetSupersets-Treffer).
        MinifigName = string.IsNullOrEmpty(m.MinifigName) ? m.BlMinifigId : m.MinifigName;
        QuantityInMinifig = m.QuantityInMinifig;
        _imageUrl = m.MinifigImageUrl;
    }
}
