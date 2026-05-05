using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// "Letzte Scans"-Tab. Zeigt die juengsten 50 ScanEvents chronologisch
/// absteigend. Refresht automatisch bei DataChanged.
/// </summary>
public partial class RecentScansViewModel : ObservableObject, IDisposable
{
    private const int MaxItems = 50;

    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    // UX X.20 Teil 2: Item-Bilder laden wir lazy ueber den PartImageProvider.
    // Cache-Hits sind gratis; Misses werden einmalig in den lokalen Bildordner
    // geladen.
    private readonly IPartImageProvider? _imageProvider;

    // Audit K-2: Subscription als Field, damit Dispose() abmelden kann.
    private readonly IMinifigPersistenceService _persistence;
    private readonly EventHandler _onDataChanged;

    public ObservableCollection<ScanEventDisplay> Items { get; } = new();

    public RecentScansViewModel(
        IDbContextFactory<UserDataContext> ctxFactory,
        IMinifigPersistenceService persistence,
        IPartImageProvider? imageProvider = null)
    {
        _ctxFactory = ctxFactory;
        _persistence = persistence;
        _imageProvider = imageProvider;
        _onDataChanged = (_, _) =>
        {
            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.BeginInvoke(() => _ = RefreshAsync());
            else
                _ = RefreshAsync();
        };
        _persistence.DataChanged += _onDataChanged;
        _ = RefreshAsync();
    }

    /// <summary>Audit K-2: Unsubscribe beim ServiceProvider-Dispose.</summary>
    public void Dispose()
    {
        _persistence.DataChanged -= _onDataChanged;
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            var raw = await ctx.ScanEvents.AsNoTracking()
                .OrderByDescending(e => e.Timestamp)
                .Take(MaxItems)
                .ToListAsync();

            Items.Clear();
            foreach (var e in raw)
            {
                var local = e.Timestamp.ToLocalTime();
                Items.Add(new ScanEventDisplay
                {
                    Timestamp = local,
                    TimeLabel = local.ToString("HH:mm"),
                    DateLabel = local.ToString("dd.MM."),
                    Description = e.ResultDescription,
                    TypeLabel = e.Type == ScanType.MinifigScan ? "Figur" : "Teil",
                    ConfidenceLabel = e.Confidence.HasValue
                        ? $"{(e.Confidence.Value * 100):F0}%"
                        : string.Empty,
                    BlType = e.Type == ScanType.MinifigScan ? "M" : "P",
                    RecognizedId = e.RecognizedId
                });
            }

            // UX X.20 Teil 2: Bilder im Hintergrund laden (best-effort).
            // Wir blockieren den Refresh nicht; die Bilder ploppen ein wenn
            // der Cache antwortet.
            if (_imageProvider != null)
                _ = LoadImagesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RecentScans Refresh fehlgeschlagen");
        }
    }

    /// <summary>
    /// Holt pro Eintrag das BL-Bild via IPartImageProvider und setzt
    /// ScanEventDisplay.ImageUrl. Bei Fehlern oder fehlendem RecognizedId
    /// bleibt das Bild leer - der Slot im Template ist dann unsichtbar.
    /// </summary>
    private async Task LoadImagesAsync()
    {
        if (_imageProvider == null) return;
        // Snapshot der aktuellen Items - falls ein Refresh kommt, sind die
        // alten Items aus der UI raus, das Hintergrund-Setzen tut nichts.
        var snapshot = Items.ToList();
        foreach (var item in snapshot)
        {
            if (string.IsNullOrWhiteSpace(item.RecognizedId)) continue;
            try
            {
                // Color-ID kennen wir hier nicht (steht nicht im ScanEvent),
                // also rufen wir mit colorId=null auf - der Provider liefert
                // dann das ungefaerbte Standard-Bild.
                var url = await _imageProvider.GetImageFileByBlAsync(
                    item.BlType, item.RecognizedId, null);
                if (!string.IsNullOrEmpty(url))
                {
                    Application.Current?.Dispatcher.Invoke(() => item.ImageUrl = url);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "RecentScans: Bild-Lookup fehlgeschlagen ({Type}/{Id})",
                    item.BlType, item.RecognizedId);
            }
        }
    }
}

/// <summary>
/// Anzeige-Datenklasse fuer eine Zeile in der Letzte-Scans-Liste.
/// UX X.20 Teil 2: ImageUrl wird async vom VM nachgesetzt - daher
/// ObservableObject-Basis, damit das XAML-Binding den Wechsel mitbekommt.
/// </summary>
public partial class ScanEventDisplay : ObservableObject
{
    public DateTime Timestamp { get; init; }
    public string TimeLabel { get; init; } = string.Empty;
    public string DateLabel { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string TypeLabel { get; init; } = string.Empty;
    public string ConfidenceLabel { get; init; } = string.Empty;

    /// <summary>"M" oder "P" - fuer den PartImageProvider-Lookup.</summary>
    public string BlType { get; init; } = "P";

    /// <summary>BL-Item-No aus dem ScanEvent. Kann null sein bei manuellen Events.</summary>
    public string? RecognizedId { get; init; }

    /// <summary>Lokal gecachter Bildpfad. Wird vom VM async befuellt.</summary>
    [ObservableProperty]
    private string? _imageUrl;
}
