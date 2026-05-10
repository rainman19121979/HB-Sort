using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// UX X.32 v0.1.19-beta.4 Block D: Auswahl-Dialog vor "Diese Figur anlegen".
/// Zeigt alle Required-Parts der BL-Minifig mit Vor-Markierung:
///   - Trigger-Teil: immer markiert + nicht abwaehlbar
///   - Reverse-Match-Treffer (Teil schon im Floating-Pool): vor-markiert
///   - Sonst: nicht markiert
/// User kann pro Teil entscheiden ob es beim Anlegen aus dem Pool genommen
/// werden soll oder dort liegen bleibt.
/// </summary>
public partial class CollectMinifigSelectionViewModel : ObservableObject
{
    private readonly IBlCatalogService _catalog;
    private readonly IPartLookupService _partLookup;
    private readonly IPartImageProvider _imageProvider;
    private readonly IStorageBinService? _binService;

    [ObservableProperty]
    private string _minifigName = string.Empty;

    [ObservableProperty]
    private string _bricklinkId = string.Empty;

    [ObservableProperty]
    private string? _imageUrl;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<CollectPartItem> Parts { get; } = new();

    /// <summary>
    /// UX X.32 v0.1.19-beta.4 (User-Befund): Lagerfach-Auswahl im Dialog.
    /// Vorher kam der Bin vom Caller (= Floating-Bin des gerade gescannten
    /// Teils) - das war fuer die wartende Figur falsch. Jetzt waehlt der
    /// User explizit ein Fach im Dialog (Default = SuggestBinForWaiting).
    /// </summary>
    public ObservableCollection<StorageBin> AvailableBins { get; } = new();

    [ObservableProperty]
    private StorageBin? _selectedBin;

    /// <summary>Wird vom Aufrufer (View) ausgewertet nach OK-Click.</summary>
    public IReadOnlyList<(string PartNo, int ColorId)> SelectedPartsForReverseMatch =>
        Parts.Where(p => p.IsKept && (p.IsCanBeReverseMatched || p.IsTrigger))
             .Select(p => (p.PartNumber, p.ColorId))
             .ToList();

    public CollectMinifigSelectionViewModel(
        IBlCatalogService catalog,
        IPartLookupService partLookup,
        IPartImageProvider imageProvider,
        IStorageBinService? binService = null)
    {
        _catalog = catalog;
        _partLookup = partLookup;
        _imageProvider = imageProvider;
        _binService = binService;
    }

    /// <summary>
    /// Laedt die Required-Parts der Minifig + bestimmt pro Teil, ob es per
    /// Reverse-Match aus dem Floating-Pool gezogen werden kann.
    /// Trigger-Teil (gerade gescannt) wird immer als "kept" markiert.
    /// </summary>
    public async Task LoadAsync(
        string blMinifigId,
        string triggerPartNo, int triggerColorId,
        int maxWaitingLimit = 1,
        CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            BricklinkId = blMinifigId;

            // UX X.32 v0.1.19-beta.4 (User-Befund): Lagerfach-Liste laden
            // + Default = wartende-Figur-Vorschlag aus dem StorageBinService.
            // Vorher hat der Caller das Floating-Bin des gerade gescannten
            // Teils uebergeben - das war fuer die NEUE wartende Figur falsch.
            if (_binService != null)
            {
                var allBins = await _binService.GetAllAsync(ct);
                AvailableBins.Clear();
                foreach (var b in allBins) AvailableBins.Add(b);

                var suggested = await _binService.SuggestBinForWaitingMinifigAsync(
                    maxWaitingLimit, ct);
                // UX X.33 v0.1.19-beta.7 Block K: kein FirstOrDefault-Fallback
                // mehr - User soll bewusst ein Fach in der Combobox waehlen
                // wenn der Service keinen Vorschlag mehr liefert.
                SelectedBin = suggested != null
                    ? AvailableBins.FirstOrDefault(b => b.Id == suggested.Id)
                    : null;
            }

            var item = await _catalog.GetMinifigDetailsAsync(blMinifigId, ct);
            MinifigName = item?.Name ?? blMinifigId;

            // Bild laden (best-effort).
            try
            {
                ImageUrl = await _imageProvider.GetImageFileByBlAsync("M", blMinifigId, null);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "CollectMinifigSelection: Bild fuer {Bl} nicht ladbar", blMinifigId);
            }

            // Subsets + Color-Map.
            var subsets = await _catalog.GetMinifigPartsAsync(blMinifigId, ct);
            var allColors = await _catalog.GetAllColorsAsync(ct);
            var colorMap = allColors.ToDictionary(c => c.ColorId);

            Parts.Clear();
            foreach (var s in subsets)
            {
                ct.ThrowIfCancellationRequested();
                colorMap.TryGetValue(s.ColorId, out var color);

                var part = new CollectPartItem
                {
                    PartNumber = s.ItemNo,
                    ColorId = s.ColorId,
                    PartName = string.IsNullOrWhiteSpace(s.ItemNo) ? "(unbekannt)" : s.ItemNo,
                    ColorName = color?.Name ?? $"Color {s.ColorId}",
                    QuantityNeeded = s.Quantity,
                    IsTrigger = (s.ItemNo.Equals(triggerPartNo, StringComparison.OrdinalIgnoreCase)
                              && s.ColorId == triggerColorId)
                };

                if (part.IsTrigger)
                {
                    // Trigger-Teil ist immer markiert + nicht abwaehlbar.
                    part.IsKept = true;
                }
                else
                {
                    // Reverse-Match: existiert FloatingPart mit gleicher Kombi?
                    try
                    {
                        var locations = await _partLookup.FindFloatingLocationsAsync(
                            s.ItemNo, s.ColorId, ct);
                        if (locations.Count > 0)
                        {
                            part.IsCanBeReverseMatched = true;
                            part.FoundInBinLabel = locations[0].StorageBinLabel;
                            part.IsKept = true; // Vor-Markierung
                        }
                        else
                        {
                            part.IsKept = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex,
                            "CollectMinifigSelection: FindFloatingLocations fuer {Part}/{Color} fehlgeschlagen",
                            s.ItemNo, s.ColorId);
                    }
                }

                Parts.Add(part);

                // Item-Bild best-effort (laeuft asynchron im Hintergrund pro Teil).
                _ = LoadPartImageAsync(part);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Bild pro Teil im Hintergrund nachladen.</summary>
    private async Task LoadPartImageAsync(CollectPartItem part)
    {
        try
        {
            var url = await _imageProvider.GetImageFileByBlAsync("P", part.PartNumber, part.ColorId);
            if (!string.IsNullOrEmpty(url))
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    part.ImageUrl = url;
                });
            }
        }
        catch
        {
            // Bild ist optional - kein User-Hinweis bei Fehler.
        }
    }
}

/// <summary>
/// UX X.32 v0.1.19-beta.4 Block D: Eine Zeile im Auswahl-Dialog.
/// </summary>
public partial class CollectPartItem : ObservableObject
{
    public string PartNumber { get; init; } = string.Empty;
    public int ColorId { get; init; }
    public string PartName { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public int QuantityNeeded { get; init; }

    /// <summary>True wenn das Teil das gerade gescannte Trigger-Teil ist.</summary>
    public bool IsTrigger { get; init; }

    /// <summary>
    /// True wenn dieses Teil bereits als FloatingPart im Pool liegt und
    /// per Reverse-Match aus einem Bin gezogen werden kann.
    /// </summary>
    public bool IsCanBeReverseMatched { get; set; }

    /// <summary>Falls IsCanBeReverseMatched=true: Label des Quell-Bins.</summary>
    public string? FoundInBinLabel { get; set; }

    /// <summary>Soll dieses Teil beim Anlegen markiert/konsumiert werden?</summary>
    [ObservableProperty]
    private bool _isKept;

    /// <summary>Bild-URL pro Teil - asynchron befuellt.</summary>
    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>
    /// Der Trigger-Teil-Haken ist immer aktiv und nicht abwaehlbar -
    /// wir binden Eingabe-Sperre via "ist es der Trigger".
    /// </summary>
    public bool IsCheckBoxEnabled => !IsTrigger;

    /// <summary>UI-Status-Label pro Teil (Farbe via StatusBrush).</summary>
    public string StatusText => IsTrigger
        ? "gerade gescannt"
        : IsCanBeReverseMatched
            ? $"im Lager: {FoundInBinLabel}"
            : "fehlt - bleibt wartend";

    /// <summary>Akzent-Farbe je nach Status.</summary>
    public Brush StatusBrush => IsTrigger
        ? Brushes.Goldenrod
        : IsCanBeReverseMatched
            ? Brushes.Green
            : Brushes.Gray;

    /// <summary>"3001 - Black, x2" - kompakte Anzeige in der Zeile.</summary>
    public string DisplayLabel =>
        QuantityNeeded > 1
            ? $"{PartNumber} - {ColorName}, x{QuantityNeeded}"
            : $"{PartNumber} - {ColorName}";
}
