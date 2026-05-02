using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;

namespace HBSort.ViewModels;

/// <summary>
/// "Pending" Minifigur: ist erkannt + im BL-Catalog gefunden, wird der UI angezeigt,
/// aber NOCH NICHT in userdata.db persistiert. Persistierung passiert in Phase 4
/// nach Lagerfach-Auswahl + Bestaetigung.
///
/// Phase R3: Daten kommen aus dem BlCatalogService (BL-API + bl_cache.db).
/// Felder werden hier eingefroren (Name, ColorName, BL-IDs), damit eine spaetere
/// Catalog-Aktualisierung diese Werte beim spaeteren Speichern nicht ueberschreibt.
/// </summary>
public partial class PendingMinifigViewModel : ObservableObject
{
    /// <summary>BrickLink-Minifig-ID, z.B. "arc007".</summary>
    public string BricklinkId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public int? YearReleased { get; init; }

    /// <summary>Anzahl Teile laut BL-Subsets-Liste.</summary>
    public int NumParts { get; init; }

    /// <summary>URL bzw. lokaler Pfad zum Vorschau-Bild der Minifigur.</summary>
    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>Status-Hinweis fuer den Header (z.B. "Aus Cache" / "Frisch geladen").</summary>
    [ObservableProperty]
    private string _sourceLabel = string.Empty;

    /// <summary>True waehrend ein BL-Lookup laeuft (zeigt Loading-Overlay in der Detail-View).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPersist))]
    private bool _isLoading;

    /// <summary>Die Teileliste der Figur.</summary>
    public ObservableCollection<PendingPartViewModel> Parts { get; } = new();

    public PendingMinifigViewModel()
    {
        // Wenn der User eine Checkbox toggelt, aendern sich CollectedCount/Header/Button-Text.
        // Wir hoeren auf PropertyChanged JEDES Teils und auch auf Add/Remove der Liste.
        Parts.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (PendingPartViewModel p in e.NewItems) p.PropertyChanged += OnPartChanged;
            if (e.OldItems != null)
                foreach (PendingPartViewModel p in e.OldItems) p.PropertyChanged -= OnPartChanged;
            RaiseCollectedDerived();
        };
    }

    private void OnPartChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PendingPartViewModel.IsCollected))
            RaiseCollectedDerived();
    }

    private void RaiseCollectedDerived()
    {
        OnPropertyChanged(nameof(CollectedCount));
        OnPropertyChanged(nameof(PartsHeaderLabel));
        OnPropertyChanged(nameof(PersistButtonText));
        OnPropertyChanged(nameof(WillBeComplete));
    }

    /// <summary>Anzahl Teile die der User vorab markiert hat ("habe ich bereits").</summary>
    public int CollectedCount => Parts.Count(p => p.IsCollected);

    /// <summary>Header-Label fuer die Teileliste.</summary>
    public string PartsHeaderLabel => CollectedCount > 0
        ? $"{NumParts} Teile insgesamt – {CollectedCount} bereits markiert"
        : $"{NumParts} Teile insgesamt";

    /// <summary>True wenn alle Teile manuell markiert sind -> Figur wird COMPLETE.</summary>
    public bool WillBeComplete => NumParts > 0 && CollectedCount == NumParts;

    /// <summary>Dynamischer Button-Text fuer "In Fach legen".</summary>
    public string PersistButtonText
    {
        get
        {
            if (NumParts == 0) return "In Fach legen";
            if (WillBeComplete) return "Direkt als COMPLETE speichern";
            if (CollectedCount > 0) return $"In Fach legen ({CollectedCount}/{NumParts} markiert)";
            return "In Fach legen";
        }
    }

    // ===== Phase 4: Lagerfach-Auswahl + Notizen =====

    /// <summary>Liste der waehlbaren Lagerfaecher fuer die ComboBox.</summary>
    public ObservableCollection<StorageBin> AvailableBins { get; } = new();

    /// <summary>Aktuell gewaehltes Lagerfach (Default: erstes freies).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPersist))]
    private StorageBin? _selectedBin;

    /// <summary>Optionale User-Notiz (z.B. "Helm fehlt original").</summary>
    [ObservableProperty]
    private string _userNotes = string.Empty;

    /// <summary>True waehrend "In Fach legen" lauft (Button greyout).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPersist))]
    private bool _isPersisting;

    /// <summary>Speichern erlaubt? (Bin gewaehlt, kein Lookup, nicht gerade am Speichern)</summary>
    public bool CanPersist => SelectedBin != null && !IsLoading && !IsPersisting;
}

/// <summary>
/// Ein Teil aus der Minifig-Teileliste (Pending = noch nicht gespeichert).
/// Phase R3: alle IDs sind BrickLink-IDs (R4 macht den Rebrickable-Cleanup).
/// </summary>
public partial class PendingPartViewModel : ObservableObject
{
    /// <summary>BrickLink-Part-Nummer, z.B. "3024".</summary>
    public string BricklinkPartNo { get; init; } = string.Empty;

    /// <summary>BrickLink-Color-ID.</summary>
    public int BricklinkColorId { get; init; }

    public string PartName { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;

    /// <summary>RGB-Hex (ohne #) – fuer Farbquadrat in der UI.</summary>
    public string? ColorRgb { get; init; }

    public int Quantity { get; init; }

    /// <summary>BL "ExtraQty" – zusaetzliche Teile bei Sets, fuer Minifigs meist 0.</summary>
    public int ExtraQuantity { get; init; }

    /// <summary>True wenn das Teil eine Match-Group-Alternative ist.</summary>
    public bool IsAlternate { get; init; }

    /// <summary>Match-Group-ID (BL). 0 = nicht in einer Gruppe.</summary>
    public int MatchId { get; init; }

    /// <summary>
    /// Teilebild via PartImageProvider. Wird asynchron befuellt
    /// (zuerst leer, dann farbiges BL-Bild).
    /// </summary>
    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>
    /// Phase-3-Anzeige: ob das Teil bereits "abgehakt" ist (gesammelt).
    /// In Phase R3 nur read-only, in Phase 5 wird das aktivierbar via Reverse-Match
    /// und Modus-B-Workflow.
    /// </summary>
    [ObservableProperty]
    private bool _isCollected;

    /// <summary>"BL-Part: 3024" – fuer die UI-Zeile.</summary>
    public string PartDisplayLabel => $"BL-Part: {BricklinkPartNo}";

    /// <summary>"Green (BL:36)" – fuer die UI-Zeile.</summary>
    public string ColorDisplayLabel
        => $"{ColorName} (BL:{BricklinkColorId.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>Quantity-Label "x 1" oder "x 3".</summary>
    public string QuantityLabel => $"x {Quantity}";

    /// <summary>"(Alt-Teil aus Match-Group 5)" oder leer.</summary>
    public string AlternateLabel => IsAlternate
        ? $"(Alt-Teil aus Match-Group {MatchId})"
        : string.Empty;

    /// <summary>SolidColorBrush fuer das Farb-Swatch (parsed RGB-Hex).</summary>
    public Brush SwatchBrush => ParseRgbBrush(ColorRgb);

    /// <summary>RGB-Hex (z.B. "FCFCFC") -> SolidColorBrush, mit grauem Fallback.</summary>
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

    /// <summary>
    /// Factory: aus einem BlSubset-Eintrag + BL-Color-Lookup baut sich das ViewModel.
    /// </summary>
    public static PendingPartViewModel FromSubset(BlSubset subset, BlColor? color, string? itemName = null)
    {
        return new PendingPartViewModel
        {
            BricklinkPartNo = subset.ItemNo,
            BricklinkColorId = subset.ColorId,
            PartName = itemName ?? string.Empty,
            ColorName = color?.Name ?? $"Color {subset.ColorId}",
            ColorRgb = color?.Rgb,
            Quantity = subset.Quantity,
            ExtraQuantity = subset.ExtraQuantity,
            IsAlternate = subset.IsAlternate,
            MatchId = subset.MatchId
        };
    }
}
