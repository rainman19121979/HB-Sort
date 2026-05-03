using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Database;
using HBSort.Core.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// PROMPT 11: "Was kann ich bauen?" - reverse match aus dem Floating-Pool.
/// Zeigt komplette/teil-komplette Minifig-Vorschlaege basierend auf den
/// losen Teilen die der User gerade hat.
///
/// Ablauf in RefreshAsync:
///   1. Floating-Pool laden, gruppiert nach (PartNo, BL-ColorId).
///   2. Bereits getrackte Minifig-IDs sammeln (die schlagen wir nicht vor).
///   3. Im BL-Cache: alle Minifigs finden, die mind. eines dieser Teile enthalten.
///   4. Pro Kandidat: Match-Prozent berechnen (Quantity-aware).
///   5. Sortieren nach Match-%, Top 20.
/// </summary>
public partial class BuildSuggestionsViewModel : ObservableObject
{
    private const int MaxSuggestions = 20;

    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IBlCacheRepository _blCache;
    private readonly IPartImageProvider _imageProvider;

    public ObservableCollection<BuildSuggestionItem> Suggestions { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    /// <summary>Mindest-Match in Prozent (Slider 10..100, Default 50).</summary>
    [ObservableProperty]
    private int _minMatchPercent = 50;

    public BuildSuggestionsViewModel(
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCacheRepository blCache,
        IPartImageProvider imageProvider,
        IMinifigPersistenceService persistence)
    {
        _ctxFactory = ctxFactory;
        _blCache = blCache;
        _imageProvider = imageProvider;

        persistence.DataChanged += (_, _) =>
        {
            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.BeginInvoke(() => _ = RefreshAsync());
            else
                _ = RefreshAsync();
        };

        _ = RefreshAsync();
    }

    partial void OnMinMatchPercentChanged(int value) => _ = RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();

            // 1) Floating-Pool gruppiert. ColorId-Quelle: BricklinkColorId bevorzugt,
            //    sonst ColorId (LegacyPart).
            var rawFloats = await ctx.FloatingParts.AsNoTracking().ToListAsync();
            var floats = rawFloats
                .GroupBy(fp => new { fp.PartNumber, ColorId = fp.BricklinkColorId ?? fp.ColorId })
                .Select(g => new
                {
                    PartNo = g.Key.PartNumber,
                    ColorId = g.Key.ColorId,
                    TotalQty = g.Sum(x => x.Quantity)
                })
                .ToList();

            if (floats.Count == 0)
            {
                Suggestions.Clear();
                SummaryText = "Keine losen Teile vorhanden.";
                return;
            }

            // 2) Bereits getrackte Minifig-IDs (egal welcher Status) ausschliessen -
            //    fuer wartende ist das logisch, fuer komplette will man nicht
            //    nochmal denselben Vorschlag bekommen.
            var trackedIds = await ctx.TrackedMinifigs.AsNoTracking()
                .Select(m => m.BricklinkId ?? m.FigNum)
                .ToListAsync();
            var trackedSet = new HashSet<string>(trackedIds, StringComparer.OrdinalIgnoreCase);

            // 3) Reverse-Lookup im BL-Cache.
            var partTuples = floats.Select(f => (f.PartNo, f.ColorId)).ToList();
            var minifigIds = await _blCache.FindMinifigsContainingPartsAsync(partTuples);

            // 4) Quick-Lookup-Dict fuer have-Mengen.
            var haveMap = floats.ToDictionary(
                f => (f.PartNo, f.ColorId),
                f => f.TotalQty);

            var suggestions = new List<BuildSuggestionItem>();
            foreach (var blMinifigId in minifigIds)
            {
                if (trackedSet.Contains(blMinifigId)) continue;

                var subsets = await _blCache.GetSubsetsAsync("M", blMinifigId);
                // Pseudo-Eintraege filtern (Repository-Query macht das schon, aber
                // GetSubsetsAsync liest die Subsets direkt - hier nochmal sicherheitshalber).
                subsets = subsets.Where(s => !s.IsFromSupersets && s.ItemType == "P").ToList();
                if (subsets.Count == 0) continue;

                var totalNeeded = subsets.Sum(s => s.Quantity);
                if (totalNeeded == 0) continue;

                var totalHave = 0;
                var missingPartCount = 0;
                foreach (var s in subsets)
                {
                    haveMap.TryGetValue((s.ItemNo, s.ColorId), out var have);
                    var taken = Math.Min(have, s.Quantity);
                    totalHave += taken;
                    if (taken < s.Quantity) missingPartCount++;
                }

                var matchPercent = (int)(100.0 * totalHave / totalNeeded);
                if (matchPercent < MinMatchPercent) continue;

                var item = await _blCache.GetItemAsync("M", blMinifigId);
                if (item == null) continue;

                suggestions.Add(new BuildSuggestionItem
                {
                    BricklinkId = blMinifigId,
                    Name = item.Name,
                    MatchPercent = matchPercent,
                    TotalParts = subsets.Count,
                    MissingPartsCount = missingPartCount,
                    TotalQtyNeeded = totalNeeded,
                    TotalQtyHave = totalHave
                });
            }

            // 5) Sortieren + Top N.
            var top = suggestions
                .OrderByDescending(s => s.MatchPercent)
                .ThenBy(s => s.MissingPartsCount)
                .Take(MaxSuggestions)
                .ToList();

            Suggestions.Clear();
            foreach (var s in top) Suggestions.Add(s);

            SummaryText = top.Count == 0
                ? $"Keine Vorschlaege ueber {MinMatchPercent}%."
                : $"{top.Count} Vorschlaege (>= {MinMatchPercent}%)";

            _ = LoadImagesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BuildSuggestions Refresh fehlgeschlagen");
            SummaryText = $"Fehler: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadImagesAsync()
    {
        foreach (var s in Suggestions.ToList())
        {
            if (!string.IsNullOrEmpty(s.ImageUrl)) continue;
            try
            {
                var url = await _imageProvider.GetImageFileByBlAsync("M", s.BricklinkId, null);
                if (!string.IsNullOrEmpty(url))
                    Application.Current?.Dispatcher.Invoke(() => s.ImageUrl = url);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "BuildSuggestions: Bild fuer {Bl} nicht ladbar", s.BricklinkId);
            }
        }
    }
}

/// <summary>Eine Bauvorschlags-Zeile.</summary>
public partial class BuildSuggestionItem : ObservableObject
{
    public string BricklinkId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int MatchPercent { get; init; }
    public int TotalParts { get; init; }
    public int MissingPartsCount { get; init; }
    public int TotalQtyNeeded { get; init; }
    public int TotalQtyHave { get; init; }

    [ObservableProperty]
    private string? _imageUrl;

    public string MatchLabel => $"{MatchPercent}% ({TotalQtyHave}/{TotalQtyNeeded})";
    public string MissingLabel => MissingPartsCount == 0
        ? "Komplett!"
        : $"Es fehlen {MissingPartsCount} Teile-Sorten";

    public Brush MatchBrush
    {
        get
        {
            Brush b = MatchPercent >= 100
                ? FreezeBrush(Color.FromRgb(46, 125, 50))      // Vollgruen
                : MatchPercent >= 75
                    ? FreezeBrush(Color.FromRgb(76, 175, 80))  // Hellgruen
                    : MatchPercent >= 50
                        ? FreezeBrush(Color.FromRgb(255, 167, 38)) // Orange
                        : Brushes.Gray;
            return b;
        }
    }

    private static Brush FreezeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
