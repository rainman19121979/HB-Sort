using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel fuer den BinDetailDialog (Phase 4 Erweiterung).
/// Liefert die Inhalts-Listen (Figuren + FloatingParts) eines Faches
/// und besorgt Bilder + Color-Swatches im Hintergrund.
/// </summary>
public partial class BinDetailViewModel : ObservableObject
{
    private readonly IStorageBinService _binService;
    private readonly IBlCatalogService _catalog;
    private readonly IPartImageProvider _imageProvider;

    public int BinId { get; }

    [ObservableProperty] private string _binLabel = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _createdAtText = string.Empty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<TrackedMinifigSummaryViewModel> Minifigs { get; } = new();
    public ObservableCollection<FloatingPartSummaryViewModel> FloatingParts { get; } = new();

    public BinDetailViewModel(
        int binId,
        IStorageBinService binService,
        IBlCatalogService catalog,
        IPartImageProvider imageProvider)
    {
        BinId = binId;
        _binService = binService;
        _catalog = catalog;
        _imageProvider = imageProvider;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var data = await _binService.GetDetailAsync(BinId);
            if (data == null) return;

            BinLabel = data.Bin.Label;
            StatusText = (data.Minifigs.Count == 0 && data.FloatingParts.Count == 0)
                ? "Frei"
                : $"Belegt ({data.Minifigs.Count} Figur(en), {data.FloatingParts.Count} Einzelteil(e))";
            CreatedAtText = data.Bin.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

            Minifigs.Clear();
            foreach (var m in data.Minifigs)
                Minifigs.Add(new TrackedMinifigSummaryViewModel(m));

            // FloatingParts gruppieren nach (PartNumber, ColorId) – falls in mehreren
            // Eintraegen das gleiche Teil/Farbe vorkommt, summieren wir die Quantity.
            var allColors = await _catalog.GetAllColorsAsync();
            var colorDict = allColors.ToDictionary(c => c.ColorId);

            FloatingParts.Clear();
            var grouped = data.FloatingParts
                .GroupBy(p => (p.PartNumber, p.ColorId))
                .Select(g => new
                {
                    Part = g.First(),
                    TotalQty = g.Sum(p => p.Quantity)
                })
                .OrderBy(x => x.Part.PartName)
                .ThenBy(x => x.Part.ColorName);
            foreach (var g in grouped)
            {
                colorDict.TryGetValue(g.Part.ColorId, out var col);
                FloatingParts.Add(new FloatingPartSummaryViewModel(g.Part, g.TotalQty, col?.Rgb));
            }

            IsEmpty = Minifigs.Count == 0 && FloatingParts.Count == 0;
            SummaryText = IsEmpty
                ? "(keine Inhalte)"
                : $"{Minifigs.Count} Figur(en), {FloatingParts.Count} Teile-Eintraege";

            // Bilder im Hintergrund laden (best-effort)
            _ = LoadImagesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BinDetail Load fehlgeschlagen");
            SummaryText = $"Fehler: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadImagesAsync()
    {
        // Minifig-Header-Bilder
        foreach (var m in Minifigs.ToList())
        {
            try
            {
                var url = await _imageProvider.GetImageFileByBlAsync("M", m.BricklinkId, null);
                if (!string.IsNullOrEmpty(url))
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => m.ImageUrl = url);
                }
            }
            catch (Exception ex)
            {
                // Best-effort: ohne Bild ist die Liste immer noch nutzbar.
                Log.Debug(ex, "Bin-Detail: Minifig-Bild konnte nicht geladen werden ({BlId})", m.BricklinkId);
            }
        }
        // FloatingPart-Bilder mit BL-Color
        foreach (var fp in FloatingParts.ToList())
        {
            try
            {
                var url = await _imageProvider.GetImageFileByBlAsync("P", fp.BlPartNo, fp.ColorId);
                if (!string.IsNullOrEmpty(url))
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => fp.ImageUrl = url);
                }
            }
            catch (Exception ex)
            {
                // Best-effort: ohne Bild ist die Liste immer noch nutzbar.
                Log.Debug(ex, "Bin-Detail: Part-Bild konnte nicht geladen werden ({Part}/c={Color})",
                    fp.BlPartNo, fp.ColorId);
            }
        }
    }
}

/// <summary>Eine Figur in der Inhalts-Liste eines Faches.</summary>
public partial class TrackedMinifigSummaryViewModel : ObservableObject
{
    public int Id { get; }
    public string Name { get; }
    public string BricklinkId { get; }
    public string StatusLabel { get; }
    public int TotalParts { get; }
    public int CompletedParts { get; }
    public string ProgressLabel => $"{CompletedParts}/{TotalParts} Teile";
    public string DisplayLine => $"Status: {StatusLabel} - {ProgressLabel}";

    [ObservableProperty]
    private string? _imageUrl;

    public TrackedMinifigSummaryViewModel(TrackedMinifig m)
    {
        Id = m.Id;
        Name = m.Name;
        BricklinkId = m.BricklinkId ?? m.FigNum;
        StatusLabel = m.Status switch
        {
            TrackedMinifigStatus.Waiting => "WAITING",
            TrackedMinifigStatus.Complete => "COMPLETE",
            TrackedMinifigStatus.Dismantled => "DISMANTLED",
            TrackedMinifigStatus.Sold => "SOLD",
            _ => m.Status.ToString().ToUpperInvariant()
        };
        TotalParts = m.RequiredParts.Count;
        CompletedParts = m.RequiredParts.Count(p => p.QuantityCollected >= p.QuantityNeeded);
        _imageUrl = m.LocalImagePath ?? m.ImageUrl;
    }
}

/// <summary>Eine Floating-Part-Zeile in der Inhalts-Liste eines Faches.</summary>
public partial class FloatingPartSummaryViewModel : ObservableObject
{
    public string PartName { get; }
    public string BlPartNo { get; }
    public string ColorName { get; }
    public int ColorId { get; }
    public Brush ColorSwatch { get; }
    public int Quantity { get; }
    public string DisplayLine => $"{PartName} ({BlPartNo}) - {ColorName}";
    public string QuantityLabel => $"x {Quantity}";

    [ObservableProperty]
    private string? _imageUrl;

    public FloatingPartSummaryViewModel(FloatingPart p, int totalQty, string? rgbHex)
    {
        PartName = p.PartName;
        BlPartNo = p.PartNumber;
        ColorName = p.ColorName;
        ColorId = p.ColorId;
        ColorSwatch = ParseRgbBrush(rgbHex);
        Quantity = totalQty;
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
