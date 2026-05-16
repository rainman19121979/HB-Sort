using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Helpers;

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
        // UX X.32 (v0.1.19): auch QuantityCollected einzeln tracken - bei
        // Quantity > 1 aendert ein Wechsel 0->1 nicht IsCollected, aber
        // HasAnyCollected geht von false auf true und der "Direkt zerlegen"-
        // Button muss sichtbar werden.
        if (e.PropertyName == nameof(PendingPartViewModel.IsCollected)
         || e.PropertyName == nameof(PendingPartViewModel.QuantityCollected))
            RaiseCollectedDerived();
    }

    private void RaiseCollectedDerived()
    {
        OnPropertyChanged(nameof(CollectedCount));
        OnPropertyChanged(nameof(PartsHeaderLabel));
        OnPropertyChanged(nameof(PersistButtonText));
        OnPropertyChanged(nameof(PersistButtonTooltip));
        OnPropertyChanged(nameof(WillBeComplete));
        OnPropertyChanged(nameof(HasAnyCollected));
    }

    /// <summary>Anzahl Teile die der User vorab markiert hat ("habe ich bereits").</summary>
    public int CollectedCount => Parts.Count(p => p.IsCollected);

    /// <summary>
    /// UX X.32 Block B (v0.1.19): True wenn mindestens ein Teil eine Sammel-
    /// Menge >0 hat. Steuert die Sichtbarkeit des "Direkt zerlegen"-Buttons
    /// in der MinifigDetailView - nur sinnvoll wenn ueberhaupt etwas zu
    /// zerlegen waere.
    /// </summary>
    public bool HasAnyCollected => Parts.Count > 0 && Parts.Any(p => p.QuantityCollected > 0);

    /// <summary>Header-Label fuer die Teileliste.</summary>
    public string PartsHeaderLabel => CollectedCount > 0
        ? $"{NumParts} Teile insgesamt - {CollectedCount} bereits markiert"
        : $"{NumParts} Teile insgesamt";

    /// <summary>True wenn alle Teile manuell markiert sind -> Figur wird COMPLETE.</summary>
    public bool WillBeComplete => NumParts > 0 && CollectedCount == NumParts;

    /// <summary>
    /// UX X.32 Bug-Fix v0.1.19-beta.3: Button-Text einheitlich "Speichern"
    /// (egal ob Complete- oder Wartend-Modus) - der Tooltip erklaert was
    /// passiert. Vorher waren die Texte zu lang fuer den Footer und haben
    /// die Lagerfach-Combobox verdraengt.
    /// </summary>
    public string PersistButtonText => "Speichern";

    /// <summary>
    /// UX X.32 Bug-Fix v0.1.19-beta.3: Tooltip mit kontextabhaengigem Text -
    /// macht klar, ob die Figur als Wartend oder als Complete gespeichert
    /// wird (haengt davon ab, ob alle Teile markiert sind).
    /// </summary>
    public string PersistButtonTooltip
    {
        get
        {
            if (NumParts == 0)
                return "Figur in das gewaehlte Lagerfach legen.";
            if (WillBeComplete)
                return "Alle Teile markiert - Figur wird direkt als COMPLETE gespeichert.";
            if (CollectedCount > 0)
                return $"Figur als wartend speichern ({CollectedCount}/{NumParts} Teile markiert).";
            return "Figur als wartend in das gewaehlte Lagerfach legen.";
        }
    }

    // ===== Phase 4: Lagerfach-Auswahl + Notizen =====

    /// <summary>
    /// Liste der waehlbaren Lagerfaecher fuer die ComboBox.
    /// v0.1.24-beta.1 Phase 2b: <see cref="BinDisplayItem"/> mit Belegungs-
    /// Suffix ("Box 005 (2 wartend)") — Konsumenten greifen via
    /// <c>SelectedBin.Bin.Id</c> / <c>SelectedBin.Bin.Label</c> auf das Original.
    /// </summary>
    public ObservableCollection<BinDisplayItem> AvailableBins { get; } = new();

    /// <summary>Aktuell gewaehltes Lagerfach (Default: erstes freies).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPersist))]
    private BinDisplayItem? _selectedBin;

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block K: Warnung wenn alle Lagerfaecher belegt
    /// sind und der Service-Suggest keinen passenden Vorschlag mehr liefert.
    /// Null = kein Warnhinweis, sonst der anzuzeigende Text.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFreeBinWarning))]
    private string? _noFreeBinWarning;

    public bool HasNoFreeBinWarning => !string.IsNullOrEmpty(NoFreeBinWarning);

    /// <summary>Optionale User-Notiz (z.B. "Helm fehlt original").</summary>
    [ObservableProperty]
    private string _userNotes = string.Empty;

    /// <summary>True waehrend "In Fach legen" lauft (Button greyout).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPersist))]
    private bool _isPersisting;

    /// <summary>Speichern erlaubt? (Bin gewaehlt, kein Lookup, nicht gerade am Speichern)</summary>
    public bool CanPersist => SelectedBin != null && !IsLoading && !IsPersisting;

    /// <summary>
    /// v0.1.24-beta.1 Phase 2a-Polish: Liste der explizit per "Aus Fach"-
    /// Button uebernommenen FloatingParts. Wird von
    /// <c>ScanViewModel.TransferFloatingPartToPendingAsync</c> gefuellt
    /// nach jedem erfolgreichen Transfer.
    ///
    /// <para>
    /// Beim PersistPending wird daraus die Take-Sektion des Post-Save-
    /// Modals gebaut. Wenn die Liste leer ist (User hat keine "Aus Fach"-
    /// Buttons gedrueckt), zeigt das Modal nur die Put-Sektion.
    /// </para>
    ///
    /// <para>Engineering-Prinzip 1.4: explizit ueber implizit.</para>
    /// </summary>
    public List<ConsumedFromBin> ConsumedFromBins { get; } = new();

    /// <summary>
    /// Eintrag fuer <see cref="ConsumedFromBins"/>. Stack-Logik: bei
    /// wiederholtem "Aus Fach"-Klick fuer dieselbe (PartNo, ColorId,
    /// SourceBinLabel)-Kombination wird <see cref="Quantity"/> erhoeht
    /// statt einen neuen Eintrag anzulegen.
    /// </summary>
    public sealed class ConsumedFromBin
    {
        public string PartNo { get; init; } = string.Empty;
        public int ColorId { get; init; }
        public string PartName { get; init; } = string.Empty;
        public string ColorName { get; init; } = string.Empty;
        public int Quantity { get; set; } = 1;
        public string SourceBinLabel { get; init; } = string.Empty;
        public string? ImageUrl { get; init; }
    }
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

    /// <summary>RGB-Hex (ohne #) - fuer Farbquadrat in der UI.</summary>
    public string? ColorRgb { get; init; }

    public int Quantity { get; init; }

    /// <summary>BL "ExtraQty" - zusaetzliche Teile bei Sets, fuer Minifigs meist 0.</summary>
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
    /// Wieviele Exemplare dieser Sorte hat der User bereits gesammelt
    /// (manuell angehakt ODER per "Aus Fach uebernehmen"). 0..Quantity.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollected))]
    [NotifyPropertyChangedFor(nameof(IsTransferButtonVisible))]
    [NotifyPropertyChangedFor(nameof(QuantityProgressLabel))]
    private int _quantityCollected;

    /// <summary>
    /// "Komplett gesammelt"-Helper. Klick auf die Anhaken-Checkbox setzt
    /// QuantityCollected entweder auf Quantity (alles markiert) oder auf 0.
    /// </summary>
    public bool IsCollected
    {
        get => Quantity > 0 && QuantityCollected >= Quantity;
        set
        {
            QuantityCollected = value ? Quantity : 0;
            // QuantityCollected hat den Notify-Hook auf IsCollected drauf,
            // also kein extra OnPropertyChanged hier.
        }
    }

    /// <summary>
    /// UX X.4+: Existiert ein passender FloatingPart in irgendeinem Lagerfach?
    /// Wird beim Laden der Pending-Minifigur einmalig befuellt und nach jedem
    /// erfolgreichen Transfer neu evaluiert.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransferButtonVisible))]
    private bool _hasMatchingFloatingPart;

    /// <summary>Anzeige-Label "Box 003" fuer den Hinweis neben dem Transfer-Button.</summary>
    [ObservableProperty]
    private string? _matchingFloatingPartBinLabel;

    /// <summary>Wieviel Exemplare im Pool liegen (Anzeige-Hinweis, kein Limit).</summary>
    [ObservableProperty]
    private int _matchingFloatingPartQuantity;

    /// <summary>
    /// Visibility-Helper fuer den "Aus Fach uebernehmen"-Button.
    /// Sichtbar wenn:
    ///   - Pool hat noch Exemplare verfuegbar (HasMatchingFloatingPart) UND
    ///   - die Pending-Figur braucht noch (QuantityCollected &lt; Quantity).
    /// </summary>
    public bool IsTransferButtonVisible
        => HasMatchingFloatingPart && QuantityCollected < Quantity;

    /// <summary>"2 / 3"-Label neben Quantity wenn Teil-gesammelt.</summary>
    public string QuantityProgressLabel
        => QuantityCollected == 0
            ? string.Empty
            : $"({QuantityCollected}/{Quantity})";

    /// <summary>"BL-Part: 3024" - fuer die UI-Zeile.</summary>
    public string PartDisplayLabel => $"BL-Part: {BricklinkPartNo}";

    /// <summary>"Green (BL:36)" - fuer die UI-Zeile.</summary>
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
    ///
    /// UX X.29 Block G (v0.1.16): defaultCollected steuert die initiale
    /// Vorbelegung von QuantityCollected. False = nichts vorab abgehakt
    /// (Default), True = alles vorab abgehakt. Caller liest die Setting
    /// AppSettings.DefaultPartsCollected und reicht sie hier durch.
    /// </summary>
    public static PendingPartViewModel FromSubset(
        BlSubset subset, BlColor? color, string? itemName = null,
        bool defaultCollected = false)
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
            MatchId = subset.MatchId,
            QuantityCollected = defaultCollected ? subset.Quantity : 0
        };
    }
}
