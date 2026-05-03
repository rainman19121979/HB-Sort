using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// Tab "Lagerliste" – jetzt als BrickStore-Style DataGrid.
/// Eine flache Liste aller Items (Minifiguren + Einzelteile) mit
/// Status-Icon-Spalte, Bild, Color-Swatch, Lagerfach, Aktionen.
/// </summary>
public partial class InventoryListViewModel : ObservableObject
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IBlCatalogService _catalog;
    private readonly IPartImageProvider _imageProvider;

    /// <summary>Alle Items (flach, nach RowNumber sortiert).</summary>
    public ObservableCollection<InventoryRowItem> Items { get; } = new();

    public ICollectionView ItemsView { get; }

    public ObservableCollection<StorageBin> AvailableBins { get; } = new();

    /// <summary>
    /// Pseudo-Bin der "alle Faecher" repraesentiert. Wird als erster Eintrag in
    /// AvailableBins eingefuegt damit der User per ComboBox-Auswahl wieder auf
    /// "alle" zurueckschalten kann (ohne den Reset-Button).
    /// Id=-1 ist der Sentinel-Wert; im Filter wird er mit IsAllBinsSentinel erkannt.
    /// </summary>
    private static readonly StorageBin AllBinsSentinel = new()
    {
        Id = -1,
        Label = "(alle Faecher)"
    };

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showComplete = true;

    [ObservableProperty]
    private bool _showWaiting = true;

    [ObservableProperty]
    private bool _showFloating = true;

    [ObservableProperty]
    private StorageBin? _filterByBin;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    // Phase 7 + UX X.6: Multi-Select fuer BSX-Export. Komplette Figuren UND
    // Einzelteile sind auswaehlbar. Counter zeigt Summe beider.
    [ObservableProperty]
    private int _selectedExportableCount;

    public bool HasSelectedExportables => SelectedExportableCount > 0;
    partial void OnSelectedExportableCountChanged(int value)
        => OnPropertyChanged(nameof(HasSelectedExportables));

    /// <summary>
    /// True wenn mindestens eine komplette Figur ODER ein Einzelteil in der
    /// aktuellen DataGrid-Sicht ist (steuert Sichtbarkeit der Action-Bar).
    /// </summary>
    public bool HasAnyExportable =>
        Items.Any(r =>
            (r.Status == StatusKind.Complete && r.UnderlyingMinifigId.HasValue)
            || (r.Status == StatusKind.Floating && r.UnderlyingFloatingId.HasValue));

    /// <summary>Aktuell selektierte komplette Figuren.</summary>
    public IEnumerable<InventoryRowItem> SelectedCompletes =>
        Items.Where(r => r.IsSelected
                      && r.Status == StatusKind.Complete
                      && r.UnderlyingMinifigId.HasValue);

    /// <summary>Aktuell selektierte Einzelteile (FloatingParts).</summary>
    public IEnumerable<InventoryRowItem> SelectedFloatings =>
        Items.Where(r => r.IsSelected
                      && r.Status == StatusKind.Floating
                      && r.UnderlyingFloatingId.HasValue);

    [RelayCommand]
    public void SelectAllExportable()
    {
        // Selektiert alle aktuell SICHTBAREN kompletten Figuren UND Einzelteile
        // (respektiert die Filter). Wartende werden ignoriert.
        foreach (var r in ItemsView.Cast<object>().Cast<InventoryRowItem>().ToList())
        {
            if (r.Status == StatusKind.Complete && r.UnderlyingMinifigId.HasValue)
                r.IsSelected = true;
            else if (r.Status == StatusKind.Floating && r.UnderlyingFloatingId.HasValue)
                r.IsSelected = true;
        }
        RecalculateSelection();
    }

    [RelayCommand]
    public void DeselectAllExportable()
    {
        foreach (var r in Items.ToList())
            r.IsSelected = false;
        RecalculateSelection();
    }

    /// <summary>
    /// Wird vom Code-Behind nach jedem CheckBox-Click gerufen, um den globalen
    /// Counter (SelectedExportableCount) zu refreshen.
    /// </summary>
    public void RecalculateSelection()
    {
        SelectedExportableCount = SelectedCompletes.Count() + SelectedFloatings.Count();
    }

    public InventoryListViewModel(
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCatalogService catalog,
        IPartImageProvider imageProvider,
        IMinifigPersistenceService persistence)
    {
        _ctxFactory = ctxFactory;
        _catalog = catalog;
        _imageProvider = imageProvider;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = RowFilter;

        // Auto-Refresh
        persistence.DataChanged += (_, _) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(() => _ = LoadAsync());
            else
                _ = LoadAsync();
        };
    }

    partial void OnSearchTextChanged(string value) => RefreshView();
    partial void OnShowCompleteChanged(bool value) => RefreshView();
    partial void OnShowWaitingChanged(bool value) => RefreshView();
    partial void OnShowFloatingChanged(bool value) => RefreshView();
    partial void OnFilterByBinChanged(StorageBin? value) => RefreshView();

    private void RefreshView()
    {
        ItemsView.Refresh();
        UpdateSummary();
    }

    private bool RowFilter(object obj)
    {
        if (obj is not InventoryRowItem r) return false;

        // Status-Filter
        var statusVisible = r.Status switch
        {
            StatusKind.Complete => ShowComplete,
            StatusKind.Waiting => ShowWaiting,
            StatusKind.Floating => ShowFloating,
            _ => true
        };
        if (!statusVisible) return false;

        // Bin-Filter ueberspringen wenn Sentinel "(alle Faecher)" ausgewaehlt ist.
        if (FilterByBin != null && FilterByBin.Id != -1 && r.StorageBinId != FilterByBin.Id)
            return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            if (!r.ItemId.Contains(s, StringComparison.OrdinalIgnoreCase)
                && !r.Description.Contains(s, StringComparison.OrdinalIgnoreCase)
                && !r.ColorName.Contains(s, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private void UpdateSummary()
    {
        var visible = ItemsView.Cast<object>().Cast<InventoryRowItem>().ToList();
        var c = visible.Count(r => r.Status == StatusKind.Complete);
        var w = visible.Count(r => r.Status == StatusKind.Waiting);
        var f = visible.Count(r => r.Status == StatusKind.Floating);
        var fQty = visible.Where(r => r.Status == StatusKind.Floating).Sum(r => r.Quantity);
        SummaryText = $"{c} Komplett, {w} Wartend, {f} Einzelteil-Eintraege ({fQty} Stueck)";
    }

    public void ClearFilters()
    {
        SearchText = string.Empty;
        // Statt null: erstes Combo-Item (= Sentinel "(alle Faecher)").
        FilterByBin = AvailableBins.FirstOrDefault();
        ShowComplete = true;
        ShowWaiting = true;
        ShowFloating = true;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();

            // Faecher (Filter-Combo). Sentinel "(alle Faecher)" als erster Eintrag,
            // damit der User per Combo-Auswahl wieder auf "alle" zurueckschalten kann.
            var bins = await ctx.StorageBins.AsNoTracking()
                .OrderBy(b => b.Label).ToListAsync();
            AvailableBins.Clear();
            AvailableBins.Add(AllBinsSentinel);
            foreach (var b in bins) AvailableBins.Add(b);

            // Default-Auswahl: Sentinel (= "alle Faecher"), nur beim Initial-Load
            // (nicht ueberschreiben wenn der User schon einen Filter gesetzt hat).
            if (FilterByBin == null) FilterByBin = AllBinsSentinel;

            // Alle Figuren (Waiting + Complete; Dismantled gibt's dank Cleanup nicht mehr).
            var allMinifigs = await ctx.TrackedMinifigs.AsNoTracking()
                .Where(m => m.Status == TrackedMinifigStatus.Waiting
                         || m.Status == TrackedMinifigStatus.Complete
                         || m.Status == TrackedMinifigStatus.Sold)
                .Include(m => m.RequiredParts)
                .Include(m => m.StorageBin)
                .OrderBy(m => m.Status)
                .ThenBy(m => m.Name)
                .ToListAsync();

            var floats = await ctx.FloatingParts.AsNoTracking()
                .Include(p => p.StorageBin)
                .OrderBy(p => p.PartName).ToListAsync();

            var allColors = await _catalog.GetAllColorsAsync();
            var colorMap = allColors.ToDictionary(c => c.ColorId);

            Items.Clear();
            int rowNum = 1;
            foreach (var m in allMinifigs)
                Items.Add(InventoryRowItem.FromMinifig(m, rowNum++));
            foreach (var fp in floats)
            {
                colorMap.TryGetValue(fp.ColorId, out var col);
                Items.Add(InventoryRowItem.FromFloating(fp, col?.Rgb, rowNum++));
            }

            UpdateSummary();

            // Phase 7 + UX X.6: nach Refresh ist die Selektion immer leer
            // (neue Items sind standardmaessig unselektiert). Anzeige der
            // Action-Bar updaten.
            SelectedExportableCount = 0;
            OnPropertyChanged(nameof(HasAnyExportable));

            _ = LoadImagesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Inventory LoadAsync fehlgeschlagen");
            SummaryText = $"Fehler: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadImagesAsync()
    {
        foreach (var r in Items.ToList())
        {
            if (!string.IsNullOrEmpty(r.ImageUrl)) continue;
            try
            {
                var url = r.Type == InventoryItemType.Minifig
                    ? await _imageProvider.GetImageFileByBlAsync("M", r.ItemId, null)
                    : await _imageProvider.GetImageFileByBlAsync("P", r.ItemId, r.ColorId);
                if (!string.IsNullOrEmpty(url))
                    Application.Current?.Dispatcher.Invoke(() => r.ImageUrl = url);
            }
            catch { /* best-effort */ }
        }
    }
}

/// <summary>Eine Zeile in der DataGrid-Lagerliste.</summary>
public partial class InventoryRowItem : ObservableObject
{
    public int RowNumber { get; init; }
    public InventoryItemType Type { get; init; }
    public StatusKind Status { get; init; }

    /// <summary>Status-Anzeige-Text (z.B. "Komplett", "Wartend", "Einzelteil").</summary>
    public string StatusLabel { get; init; } = string.Empty;

    /// <summary>Status-Icon (Unicode/ASCII).</summary>
    public string StatusIcon { get; init; } = string.Empty;

    public Brush StatusBrush { get; init; } = Brushes.Gray;

    /// <summary>BL-ID (Minifig-No oder Part-No).</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>Anzeige-Beschreibung (Name).</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>BrickStore-Condition (default G = Used).</summary>
    public string Condition { get; init; } = "G";

    /// <summary>Color-Swatch (null bei Minifigs).</summary>
    public Brush? ColorSwatch { get; init; }

    public string ColorName { get; init; } = string.Empty;
    public int ColorId { get; init; }
    public int Quantity { get; init; }
    public string StorageBinLabel { get; init; } = string.Empty;
    public int? StorageBinId { get; init; }

    /// <summary>Kategorie (z.B. "Minifig", "Part") – Phase 7-Vorbereitung.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Item-Typ-Label fuer DataGrid ("Minifig" / "Einzelteil").</summary>
    public string ItemTypeLabel { get; init; } = string.Empty;

    public int? UnderlyingMinifigId { get; init; }
    public int? UnderlyingFloatingId { get; init; }

    /// <summary>Fortschritt fuer wartende Figuren ("3/7"); leer fuer andere.</summary>
    public string ProgressLabel { get; init; } = string.Empty;

    /// <summary>
    /// UX-Iteration X.9: ausfuehrlicher Tooltip-Text zum Status-Badge.
    /// Wird im DataGrid an die Status-Zelle gehaengt - der User kriegt
    /// dadurch eine klare Erlaeuterung, was die jeweilige Markierung
    /// bedeutet, ohne in die Hilfe wechseln zu muessen.
    /// </summary>
    public string StatusTooltip => Status switch
    {
        StatusKind.Complete => "Alle Teile vorhanden. Du kannst die Figur via BSX exportieren.",
        StatusKind.Waiting  => "Mindestens ein Teil fehlt noch. Sammle weiter.",
        StatusKind.Floating => "Loses Einzelteil im Pool. Wird beim Reverse-Match automatisch aufgegriffen.",
        StatusKind.Sold     => "Bereits verkauft.",
        _ => string.Empty
    };

    /// <summary>
    /// UX-Iteration X.9: Tooltip fuer die Fortschritts-Spalte ("3/7" -&gt;
    /// "3 von 7 Teilen vorhanden"). Leer wenn ProgressLabel leer ist.
    /// </summary>
    public string ProgressTooltip
    {
        get
        {
            if (string.IsNullOrEmpty(ProgressLabel)) return string.Empty;
            var parts = ProgressLabel.Split('/');
            if (parts.Length == 2)
            {
                return $"{parts[0]} von {parts[1]} Teilen vorhanden.";
            }
            return ProgressLabel;
        }
    }

    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>
    /// Phase 7: Multi-Select-Flag fuer den BSX-Export. Im DataGrid wird die
    /// Checkbox ueber einen Visibility-Trigger nur fuer komplette Figuren
    /// angezeigt; andere Zeilen ignorieren das Property.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    public static InventoryRowItem FromMinifig(TrackedMinifig m, int rowNum)
    {
        var status = m.Status switch
        {
            TrackedMinifigStatus.Complete => StatusKind.Complete,
            TrackedMinifigStatus.Sold     => StatusKind.Sold,
            _                              => StatusKind.Waiting
        };
        var (icon, label, brush) = StatusVisuals(status);

        var totalParts = m.RequiredParts.Count;
        var collectedParts = m.RequiredParts.Count(p => p.QuantityCollected >= p.QuantityNeeded);
        var progress = totalParts == 0 ? string.Empty : $"{collectedParts}/{totalParts}";

        return new InventoryRowItem
        {
            RowNumber = rowNum,
            Type = InventoryItemType.Minifig,
            Status = status,
            StatusIcon = icon,
            StatusLabel = label,
            StatusBrush = brush,
            ItemId = m.BricklinkId ?? m.FigNum,
            Description = m.Name,
            ColorSwatch = null,
            ColorName = "(N/A)",
            ColorId = 0,
            Quantity = 1,
            StorageBinLabel = m.StorageBin?.Label ?? "—",
            StorageBinId = m.StorageBinId,
            Category = "Minifig",
            ItemTypeLabel = "Minifig",
            UnderlyingMinifigId = m.Id,
            ProgressLabel = progress,
            ImageUrl = m.LocalImagePath ?? m.ImageUrl
        };
    }

    public static InventoryRowItem FromFloating(FloatingPart fp, string? rgbHex, int rowNum)
    {
        var (icon, label, brush) = StatusVisuals(StatusKind.Floating);
        return new InventoryRowItem
        {
            RowNumber = rowNum,
            Type = InventoryItemType.FloatingPart,
            Status = StatusKind.Floating,
            StatusIcon = icon,
            StatusLabel = label,
            StatusBrush = brush,
            ItemId = fp.PartNumber,
            Description = fp.PartName,
            ColorSwatch = ParseRgbBrush(rgbHex),
            ColorName = fp.ColorName,
            ColorId = fp.ColorId,
            Quantity = fp.Quantity,
            StorageBinLabel = fp.StorageBin?.Label ?? "—",
            StorageBinId = fp.StorageBinId,
            Category = "Part",
            ItemTypeLabel = "Einzelteil",
            UnderlyingFloatingId = fp.Id
        };
    }

    private static (string icon, string label, Brush brush) StatusVisuals(StatusKind kind) => kind switch
    {
        StatusKind.Complete => ("OK",  "Komplett",   FreezeBrush(Color.FromRgb(76, 175, 80))),
        StatusKind.Waiting  => ("...", "Wartend",    FreezeBrush(Color.FromRgb(255, 167, 38))),
        StatusKind.Floating => ("[T]", "Einzelteil", FreezeBrush(Color.FromRgb(33, 150, 243))),
        StatusKind.Sold     => ("$",   "Verkauft",   FreezeBrush(Color.FromRgb(156, 39, 176))),
        _                   => ("?",   "?",           Brushes.Gray)
    };

    private static Brush FreezeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
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

public enum InventoryItemType { Minifig, FloatingPart }
public enum StatusKind { Complete, Waiting, Floating, Sold }
