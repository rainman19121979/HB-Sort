using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
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
/// ViewModel fuer den MinifigSummaryDialog (Wartende-Liste-Klick).
/// Liest die Figur frisch aus der DB inkl. RequiredParts und berechnet die
/// Anzeige-Felder. Stellt zudem die "Verschieben"-Liste (alle Faecher) bereit.
///
/// UX X.20 Teil 7: ein einheitlicher Dialog fuer wartende UND komplette
/// Figuren. Der Verkaufsempfehlungs-Block wurde entfernt - die Live-Preise
/// leben in der MinifigPriceView im Sortier-Tab (Spalte 3 unten).
/// </summary>
public partial class MinifigSummaryViewModel : ObservableObject
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IStorageBinService _binService;
    private readonly IPartImageProvider? _imageProvider;
    private readonly IBlCatalogService? _catalog;

    public int MinifigId { get; }
    public string Name { get; private set; } = string.Empty;
    public string BricklinkId { get; private set; } = string.Empty;
    public string? ImageUrl { get; private set; }
    public string BinLabel { get; private set; } = string.Empty;
    public int? CurrentBinId { get; private set; }

    /// <summary>Status der Figur (fuer Loeschen-Confirmation und UI-Logik).</summary>
    public TrackedMinifigStatus Status { get; private set; } = TrackedMinifigStatus.Waiting;

    /// <summary>Phase 6: Visibility-Helper fuer Buttons im Summary-Dialog.</summary>
    public bool IsWaiting  => Status == TrackedMinifigStatus.Waiting;
    public bool IsComplete => Status == TrackedMinifigStatus.Complete;

    public string? Notes { get; private set; }
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public string NotesDisplay => string.IsNullOrWhiteSpace(Notes) ? string.Empty : $"📝 {Notes}";

    public int TotalParts { get; private set; }
    public int CompletedParts { get; private set; }
    public double ProgressFraction => TotalParts == 0 ? 0 : (double)CompletedParts / TotalParts;
    public string ProgressLabel => $"{CompletedParts} von {TotalParts} komplett";

    /// <summary>Teile mit Status fuer die Anzeige.</summary>
    public ObservableCollection<SummaryPartViewModel> Parts { get; } = new();

    /// <summary>Verfuegbare Faecher fuer "Verschieben" (ohne aktuelles Fach).</summary>
    public ObservableCollection<StorageBin> AvailableBins { get; } = new();

    [ObservableProperty]
    private StorageBin? _moveTarget;

    public MinifigSummaryViewModel(
        int minifigId,
        IDbContextFactory<UserDataContext> ctxFactory,
        IStorageBinService binService,
        IPartImageProvider? imageProvider = null,
        IBlCatalogService? catalog = null)
    {
        MinifigId = minifigId;
        _ctxFactory = ctxFactory;
        _binService = binService;
        _imageProvider = imageProvider;
        _catalog = catalog;
    }

    /// <summary>Laed die Figur aus der DB inkl. Parts + alle anderen Faecher als Move-Targets.</summary>
    public async Task LoadAsync()
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var m = await ctx.TrackedMinifigs.AsNoTracking()
            .Where(x => x.Id == MinifigId)
            .Include(x => x.RequiredParts)
            .Include(x => x.StorageBin)
            .FirstOrDefaultAsync();
        if (m == null) return;

        Name = m.Name;
        BricklinkId = m.BricklinkId ?? m.FigNum;
        ImageUrl = m.LocalImagePath ?? m.ImageUrl;
        BinLabel = m.StorageBin?.Label ?? "(kein Fach)";
        CurrentBinId = m.StorageBinId;
        Status = m.Status;
        Notes = m.UserNotes;
        TotalParts = m.RequiredParts.Count;
        CompletedParts = m.RequiredParts.Count(p => p.QuantityCollected >= p.QuantityNeeded);

        Parts.Clear();
        foreach (var p in m.RequiredParts.OrderBy(x => x.PartName))
        {
            Parts.Add(new SummaryPartViewModel(p));
        }

        // Bilder + Color-Swatches asynchron im Hintergrund laden (best-effort).
        if (_imageProvider != null && _catalog != null)
        {
            _ = LoadPartImagesAndSwatchesAsync();
        }

        // Fallback: wenn LocalImagePath leer ist (alte Figuren oder beim
        // BL-Catalog-Collect nicht gespeichert), Bild nachladen + persistieren.
        if (string.IsNullOrEmpty(m.LocalImagePath) && _imageProvider != null)
        {
            _ = LoadAndPersistMinifigImageAsync(m.Id, m.BricklinkId ?? m.FigNum);
        }

        // Move-Targets: alle TYP-PASSENDEN Faecher AUSSER dem aktuellen.
        // v0.1.22-beta.3 (2026-05-13): vorher GetAllAsync, was auch Floating-
        // Bins als Move-Ziel angeboten hat - inkonsistent zum Volle-Faecher-
        // Banner. Jetzt typ-gefiltert je nach Status der Figur.
        var targetKind = Status == TrackedMinifigStatus.Complete
            ? BinTargetKind.CompleteMinifigTarget
            : BinTargetKind.WaitingMinifigTarget;
        AvailableBins.Clear();
        var bins = await _binService.GetEligibleBinsAsync(
            targetKind, excludeMinifigId: m.Id);
        foreach (var b in bins.Where(b => b.Id != CurrentBinId))
            AvailableBins.Add(b);

        // Trigger property notifications fuer die berechneten Properties
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(BricklinkId));
        OnPropertyChanged(nameof(ImageUrl));
        OnPropertyChanged(nameof(BinLabel));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(NotesDisplay));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(TotalParts));
        OnPropertyChanged(nameof(CompletedParts));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(ProgressLabel));
    }

    /// <summary>
    /// Best-effort Hintergrund-Loading: pro Teil Bild + Color-RGB.
    /// Aenderungen werden auf den UI-Thread gepostet.
    /// </summary>
    private async Task LoadPartImagesAndSwatchesAsync()
    {
        try
        {
            var allColors = _catalog == null ? new List<Core.Models.Bricklink.BlColor>()
                : await _catalog.GetAllColorsAsync();
            var colorMap = allColors.ToDictionary(c => c.ColorId);

            foreach (var p in Parts.ToList())
            {
                // Color-Swatch
                if (colorMap.TryGetValue(p.ColorId, out var color) && color.Rgb != null)
                {
                    var brush = ParseRgbBrush(color.Rgb);
                    Application.Current?.Dispatcher.Invoke(() => p.SwatchBrush = brush);
                }

                // Image
                if (_imageProvider != null)
                {
                    try
                    {
                        var url = await _imageProvider.GetImageFileByBlAsync("P", p.PartNumber, p.ColorId);
                        if (!string.IsNullOrEmpty(url))
                            Application.Current?.Dispatcher.Invoke(() => p.ImageUrl = url);
                    }
                    catch (Exception imgEx)
                    {
                        Log.Debug(imgEx, "Konnte Part-Bild nicht laden ({Part}/{Color})",
                            p.PartNumber, p.ColorId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoadPartImagesAndSwatches geworfen");
        }
    }

    /// <summary>
    /// Best-effort: Header-Bild via PartImageProvider laden, in DB als
    /// LocalImagePath persistieren und das UI aktualisieren. Wird aufgerufen
    /// wenn LocalImagePath beim Laden noch leer war.
    /// </summary>
    private async Task LoadAndPersistMinifigImageAsync(int minifigId, string blId)
    {
        try
        {
            if (_imageProvider == null) return;
            var url = await _imageProvider.GetImageFileByBlAsync("M", blId, null);
            if (string.IsNullOrEmpty(url)) return;

            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            var dbm = await ctx.TrackedMinifigs.FirstOrDefaultAsync(x => x.Id == minifigId);
            if (dbm == null) return;
            dbm.LocalImagePath = url;
            await ctx.SaveChangesAsync();

            // UI aktualisieren - ImageUrl ist ein normales Property mit
            // OnPropertyChanged-Trigger via Dialog-Binding.
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ImageUrl = url;
                OnPropertyChanged(nameof(ImageUrl));
            });
            Log.Debug("MinifigSummary: LocalImagePath fuer {Bl} nachgeladen", blId);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "MinifigSummary: Bild fuer {Bl} nicht nachladbar", blId);
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

    /// <summary>Verschiebt die Figur in ein anderes Fach.</summary>
    public async Task<bool> MoveToAsync(int newBinId)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var m = await ctx.TrackedMinifigs.FirstOrDefaultAsync(x => x.Id == MinifigId);
        if (m == null) return false;

        // Bug B Fix (UX X.28): wenn das Ziel-Fach als "frei" markiert war,
        // jetzt wieder als belegt markieren.
        var bin = await ctx.StorageBins.FirstOrDefaultAsync(b => b.Id == newBinId);
        if (bin == null)
            throw new InvalidOperationException($"Lagerfach {newBinId} existiert nicht.");
        if (bin.FreedAt != null)
        {
            Log.Information("Fach '{Label}' war als frei markiert (seit {FreedAt}) - wird durch verschobene Figur wieder belegt",
                bin.Label, bin.FreedAt);
            bin.FreedAt = null;
        }

        // UX X.29 (v0.1.16): Move-Snapshot fuer Undo erfassen + ScanEvent schreiben.
        var oldBinId = m.StorageBinId;
        var moveSnapshot = new HBSort.Core.Services.UndoSnapshotMove
        {
            MinifigId = m.Id,
            OldStorageBinId = oldBinId,
            NewStorageBinId = newBinId
        };
        ctx.ScanEvents.Add(new HBSort.Core.Models.ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = HBSort.Core.Models.ScanType.Move,
            RecognizedId = m.BricklinkId ?? m.FigNum,
            ResultDescription = $"Figur '{m.Name}' verschoben in Fach '{bin.Label}'",
            WasUndone = false,
            UndoData = System.Text.Json.JsonSerializer.Serialize(moveSnapshot)
        });

        m.StorageBinId = newBinId;
        await ctx.SaveChangesAsync();
        Log.Information("Minifigur '{Name}' (Id={Id}) in Fach {Bin} verschoben",
            m.Name, m.Id, newBinId);
        return true;
    }
}

/// <summary>Eine Teile-Zeile im Summary-Dialog.</summary>
public partial class SummaryPartViewModel : ObservableObject
{
    public int Id { get; }
    public string PartName { get; }
    public string PartNumber { get; }
    public int ColorId { get; }
    public string ColorName { get; }
    public int QuantityNeeded { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(QuantityLabel))]
    private int _quantityCollected;

    /// <summary>BL-Bild des Teils (URL/Pfad) - wird vom Parent-VM async befuellt.</summary>
    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>Color-Swatch - wird vom Parent-VM async aus bl_colors befuellt.</summary>
    [ObservableProperty]
    private Brush _swatchBrush = Brushes.Gray;

    public bool IsCompleted => QuantityCollected >= QuantityNeeded;
    public string QuantityLabel => $"{QuantityCollected}/{QuantityNeeded}";

    public SummaryPartViewModel(TrackedMinifigPart p)
    {
        Id = p.Id;
        PartName = p.PartName;
        PartNumber = p.PartNumber;
        ColorId = p.ColorId;
        ColorName = p.ColorName;
        QuantityNeeded = p.QuantityNeeded;
        _quantityCollected = p.QuantityCollected;
    }
}
