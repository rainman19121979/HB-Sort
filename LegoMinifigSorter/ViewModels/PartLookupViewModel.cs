using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LegoMinifigSorter.Core.Models;
using LegoMinifigSorter.Core.Models.Bricklink;
using LegoMinifigSorter.Core.Services;

namespace LegoMinifigSorter.ViewModels;

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
