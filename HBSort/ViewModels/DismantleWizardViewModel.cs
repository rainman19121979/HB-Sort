using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// Wizard-VM fuer das Aufgeben einer Figur. Zeigt pro Required-Part eine
/// Checkbox + Ziel-Fach-Auswahl. Default: alle Teile ankreuzen, Ziel-Fach
/// = aktuelles Fach der Figur (oder erstes freies).
/// </summary>
public partial class DismantleWizardViewModel : ObservableObject
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IStorageBinService _binService;
    private readonly IMinifigPersistenceService _persistence;
    private readonly IPartImageProvider? _imageProvider;
    private readonly IBlCatalogService? _catalog;
    private readonly IPartLookupService? _partLookup;

    public int TrackedMinifigId { get; }

    [ObservableProperty] private string _minifigName = string.Empty;
    [ObservableProperty] private string _bricklinkId = string.Empty;
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<DismantlePartItemViewModel> Parts { get; } = new();
    public ObservableCollection<StorageBin> AvailableBins { get; } = new();

    /// <summary>Default-Bin fuer "Auf alle anwenden".</summary>
    [ObservableProperty]
    private StorageBin? _defaultBin;

    public DismantleWizardViewModel(
        int trackedMinifigId,
        IDbContextFactory<UserDataContext> ctxFactory,
        IStorageBinService binService,
        IMinifigPersistenceService persistence,
        IPartImageProvider? imageProvider = null,
        IBlCatalogService? catalog = null,
        IPartLookupService? partLookup = null)
    {
        TrackedMinifigId = trackedMinifigId;
        _ctxFactory = ctxFactory;
        _binService = binService;
        _persistence = persistence;
        _imageProvider = imageProvider;
        _catalog = catalog;
        _partLookup = partLookup;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            // Figur + Required-Parts + aktuelles Fach holen
            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            var m = await ctx.TrackedMinifigs.AsNoTracking()
                .Include(x => x.RequiredParts)
                .Include(x => x.StorageBin)
                .FirstOrDefaultAsync(x => x.Id == TrackedMinifigId);
            if (m == null) return;

            MinifigName = m.Name;
            BricklinkId = m.BricklinkId ?? m.FigNum;

            // Lagerfaecher fuer Combobox laden
            var bins = await _binService.GetAllAsync();
            AvailableBins.Clear();
            foreach (var b in bins) AvailableBins.Add(b);

            // Default-Bin: aktuelles Fach der Figur, sonst erstes freies
            StorageBin? targetBin = m.StorageBinId.HasValue
                ? AvailableBins.FirstOrDefault(b => b.Id == m.StorageBinId.Value)
                : null;
            if (targetBin == null)
            {
                var firstFree = await _binService.GetNextFreeAsync();
                if (firstFree != null)
                    targetBin = AvailableBins.FirstOrDefault(b => b.Id == firstFree.Id);
            }
            DefaultBin = targetBin ?? AvailableBins.FirstOrDefault();

            Parts.Clear();
            foreach (var p in m.RequiredParts.OrderBy(p => p.PartName))
            {
                var partVm = new DismantlePartItemViewModel(p)
                {
                    // Smart-Default: nur tatsaechlich gesammelte Teile vor-aktivieren.
                    // Nicht-gesammelte Teile sind im Wizard deaktiviert (User kann
                    // bei Bedarf manuell ankreuzen, dann werden QuantityNeeded uebernommen).
                    IsKept = p.QuantityCollected > 0,
                    TargetBin = DefaultBin
                };

                // Smart-Sammeln: ist das gleiche Teil schon irgendwo als Einzelteil
                // gelagert? Dann das Fach mit der hoechsten Menge vor-auswaehlen
                // damit Bestaende beim Speichern zusammengefuehrt werden.
                if (_partLookup != null)
                {
                    try
                    {
                        var blColorId = p.BricklinkColorId ?? p.ColorId;
                        Log.Information("SmartHint-Lookup: PartNo={PartNo}, ColorId={ColorId}, " +
                                        "BricklinkColorId={BlColorId}, blColorId-effective={Effective}",
                            p.PartNumber, p.ColorId, p.BricklinkColorId, blColorId);

                        var locations = await _partLookup.FindFloatingLocationsAsync(
                            p.PartNumber, blColorId);

                        Log.Information("SmartHint-Lookup result: {Count} Locations gefunden", locations.Count);
                        foreach (var loc in locations)
                        {
                            Log.Information("  -> Bin {Id} '{Label}': {Qty} Stueck",
                                loc.StorageBinId, loc.StorageBinLabel, loc.TotalQuantity);
                        }

                        if (locations.Count > 0)
                        {
                            var best = locations[0]; // sortiert nach Menge absteigend
                            var bin = AvailableBins.FirstOrDefault(b => b.Id == best.StorageBinId);
                            if (bin != null)
                            {
                                partVm.TargetBin = bin;
                                partVm.SmartHint = $"+{best.TotalQuantity} schon dort";
                            }
                            else
                            {
                                Log.Warning("SmartHint: Bin Id={BinId} nicht in AvailableBins gefunden",
                                    best.StorageBinId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "SmartHint-Lookup fuer {Part}/{Color} fehlgeschlagen",
                            p.PartNumber, p.ColorId);
                    }
                }

                Parts.Add(partVm);
            }

            // Bilder + Color-Swatches im Hintergrund (best-effort).
            if (_imageProvider != null && _catalog != null)
            {
                _ = LoadPartImagesAndSwatchesAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DismantleWizard Load fehlgeschlagen");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadPartImagesAndSwatchesAsync()
    {
        try
        {
            var allColors = _catalog == null ? new List<Core.Models.Bricklink.BlColor>()
                : await _catalog.GetAllColorsAsync();
            var colorMap = allColors.ToDictionary(c => c.ColorId);

            foreach (var p in Parts.ToList())
            {
                if (colorMap.TryGetValue(p.BlColorId, out var col) && col.Rgb != null)
                {
                    var brush = ParseRgbBrush(col.Rgb);
                    Application.Current?.Dispatcher.Invoke(() => p.SwatchBrush = brush);
                }

                if (_imageProvider != null)
                {
                    try
                    {
                        var url = await _imageProvider.GetImageFileByBlAsync("P", p.BlPartNo, p.BlColorId);
                        if (!string.IsNullOrEmpty(url))
                            Application.Current?.Dispatcher.Invoke(() => p.ImageUrl = url);
                    }
                    catch { /* best-effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Wizard LoadPartImagesAndSwatches geworfen");
        }
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

    /// <summary>Setzt alle Checkboxen auf true.</summary>
    public void SelectAll()
    {
        foreach (var p in Parts) p.IsKept = true;
    }

    /// <summary>Setzt alle Checkboxen auf false.</summary>
    public void DeselectAll()
    {
        foreach (var p in Parts) p.IsKept = false;
    }

    /// <summary>Wendet das DefaultBin auf alle Teile an (auch deaktivierte).</summary>
    public void ApplyDefaultBin()
    {
        if (DefaultBin == null) return;
        // Bin-Objekt aus AvailableBins (Reference-Equality fuer ComboBox)
        var bin = AvailableBins.FirstOrDefault(b => b.Id == DefaultBin.Id);
        if (bin == null) return;
        foreach (var p in Parts) p.TargetBin = bin;
    }

    /// <summary>Fuehrt den Aufgeben-Vorgang aus und gibt das Service-Resultat zurueck.</summary>
    public async Task<DismantleResult> ConfirmAsync()
    {
        var choices = Parts.Select(p => new DismantlePartChoice
        {
            TrackedMinifigPartId = p.Id,
            IsKept = p.IsKept,
            TargetBinId = p.IsKept ? p.TargetBin?.Id : null
        }).ToList();
        return await _persistence.DismantleAsync(TrackedMinifigId, choices);
    }
}

/// <summary>Eine Zeile im Wizard: ein Required-Part + Auswahl + Ziel-Fach.</summary>
public partial class DismantlePartItemViewModel : ObservableObject
{
    public int Id { get; }
    public string PartName { get; }
    public string BlPartNo { get; }
    public string ColorName { get; }
    public int BlColorId { get; }
    public int QuantityNeeded { get; }
    public int QuantityCollected { get; }

    /// <summary>True wenn QuantityCollected > 0 (Teil ist tatsaechlich da).</summary>
    public bool WasCollected => QuantityCollected > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private bool _isKept;

    [ObservableProperty]
    private StorageBin? _targetBin;

    /// <summary>BL-Bild des Teils – wird vom Parent-VM async befuellt.</summary>
    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>Color-Swatch – wird vom Parent-VM async aus bl_colors befuellt.</summary>
    [ObservableProperty]
    private Brush _swatchBrush = Brushes.Gray;

    /// <summary>
    /// Smart-Hint neben der Bin-ComboBox. Beispiel: "+3 schon dort" wenn das Teil
    /// im vorgewaehlten Fach bereits als Einzelteil liegt. Null = kein Hinweis.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSmartHint))]
    private string? _smartHint;

    public bool HasSmartHint => !string.IsNullOrEmpty(SmartHint);

    public string StatusLabel => IsKept ? "(uebernommen)" : "(verworfen)";
    public string EffectiveQtyLabel => WasCollected
        ? $"x{QuantityCollected} (gesammelt)"
        : $"x{QuantityNeeded} (NICHT gesammelt)";
    public string DisplayLine => $"{PartName} ({BlPartNo}) – {ColorName}, {EffectiveQtyLabel}";

    public DismantlePartItemViewModel(TrackedMinifigPart p)
    {
        Id = p.Id;
        PartName = p.PartName;
        BlPartNo = p.PartNumber;
        ColorName = p.ColorName;
        BlColorId = p.BricklinkColorId ?? p.ColorId;
        QuantityNeeded = p.QuantityNeeded;
        QuantityCollected = p.QuantityCollected;
    }
}
