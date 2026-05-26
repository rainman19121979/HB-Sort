using System.Collections.ObjectModel;
using System.Threading;
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
/// "Was kann ich bauen?" - reverse match aus dem Floating-Pool.
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
public partial class BuildSuggestionsViewModel : ObservableObject, IDisposable
{
    private const int MaxSuggestions = 20;

    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IBlCacheRepository _blCache;
    private readonly IPartImageProvider _imageProvider;
    // v0.1.24-beta.8 Phase 3: BL-Inventar-Zugriff fuer die optionale
    // "Mit BL-Shop vervollstaendigbar"-Sektion.
    private readonly IBlInventoryService _blInventory;

    // Audit K-2: Wir merken uns die DataChanged-Subscription (als Field), damit
    // wir sie in Dispose() sauber abmelden koennen. Ohne das Unsubscribe wuerde
    // der Event-Handler die VM-Instanz am Leben halten - bei Singleton-Lifetime
    // ist das in Production kein Problem (lebt sowieso bis App-Ende), aber:
    // 1) Bei Tests die das VM-Setup wiederholen sammeln sich Handler an,
    // 2) Future-Refactor (z.B. Tab-Lazy-Init) wird damit zur echten Leak-Quelle.
    private readonly IMinifigPersistenceService _persistence;
    private readonly EventHandler _onDataChanged;

    // v0.1.23-beta.2 Fix C: laufende Image-Loads bei Refresh abbrechen.
    private CancellationTokenSource? _imageLoadCts;

    /// <summary>
    /// v0.1.24-beta.8 Phase 3 (Fix 5): eine einheitliche Liste.
    /// - Toggle aus: nur Figuren die komplett aus HBSort-FloatingParts
    ///   baubar sind (MatchPercent=100, kein BL).
    /// - Toggle an: zusaetzlich Figuren die mit HBSort+BL=100% baubar sind
    ///   (HasBlShopAddition=true, Badge "X Teile aus Shop").
    /// Sortierung: HBSort-only zuerst, BL-erweiterte danach.
    /// </summary>
    public ObservableCollection<BuildSuggestionItem> Suggestions { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    /// <summary>
    /// v0.1.24-beta.8 Phase 3: Checkbox oben in der View. Default aus -
    /// Verhalten ohne BL-Inventar bleibt unveraendert. Bei Aenderung wird
    /// die Liste neu berechnet.
    /// </summary>
    [ObservableProperty]
    private bool _includeBlInventory;

    /// <summary>True wenn der User BL-Inventar synchronisiert hat (steuert Checkbox-Sichtbarkeit).</summary>
    [ObservableProperty]
    private bool _hasAnyBlInventory;

    partial void OnIncludeBlInventoryChanged(bool value) => _ = RefreshAsync();

    public BuildSuggestionsViewModel(
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCacheRepository blCache,
        IPartImageProvider imageProvider,
        IMinifigPersistenceService persistence,
        IBlInventoryService blInventory)
    {
        _ctxFactory = ctxFactory;
        _blCache = blCache;
        _imageProvider = imageProvider;
        _persistence = persistence;
        _blInventory = blInventory;

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

    /// <summary>
    /// Audit K-2: meldet sich vom DataChanged-Event ab. Wird vom DI-Container
    /// automatisch beim ServiceProvider-Dispose (App.xaml.cs::OnExit) aufgerufen.
    /// </summary>
    public void Dispose()
    {
        _persistence.DataChanged -= _onDataChanged;
        _imageLoadCts?.Cancel();
        _imageLoadCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();

            // 1) Floating-Pool gruppiert nach (PartNumber, BL-ColorId).
            var rawFloats = await ctx.FloatingParts.AsNoTracking().ToListAsync();
            var floats = rawFloats
                .GroupBy(fp => new { fp.PartNumber, fp.ColorId })
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
                // Kein Mindest-Schwellenwert mehr - alle baubaren Vorschlaege
                // werden gezeigt, sortiert nach Match-% absteigend.

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

            // 5) v0.1.24-beta.8 Phase 3 (Fix 5): einheitliche Liste statt
            //    zwei Sektionen. Toggle-off zeigt nur HBSort-100%, Toggle-on
            //    ergaenzt um Figuren die mit BL-Shop auf 100% kommen.
            HasAnyBlInventory = await _blInventory.HasAnyInventoryAsync();
            var hbsortComplete = suggestions
                .Where(s => s.MatchPercent >= 100)
                .OrderBy(s => s.Name)
                .Take(MaxSuggestions)
                .ToList();

            var blCompletable = new List<BuildSuggestionItem>();
            if (IncludeBlInventory && HasAnyBlInventory)
            {
                blCompletable = await BuildBlCompletableAsync(suggestions, haveMap);
            }

            Suggestions.Clear();
            // Erst HBSort-100% (alphabetisch), dann BL-Erweiterungen
            // (nach Aufwand: wenig BL-Teile zuerst).
            foreach (var s in hbsortComplete) Suggestions.Add(s);
            foreach (var s in blCompletable.OrderBy(s => s.BlShopPartCount).ThenBy(s => s.Name))
                Suggestions.Add(s);

            SummaryText = Suggestions.Count == 0
                ? "Keine Bauvorschlaege - keine deiner losen Teile reicht fuer eine ungetrackte Minifig."
                : (IncludeBlInventory && HasAnyBlInventory
                    ? $"{hbsortComplete.Count} aus HBSort + {blCompletable.Count} mit BL-Shop-Ergaenzung"
                    : $"{hbsortComplete.Count} Vorschlaege (komplett aus HBSort baubar)");

            // v0.1.23-beta.2 Fix C: vorigen Image-Load abbrechen + neue CTS.
            _imageLoadCts?.Cancel();
            _imageLoadCts?.Dispose();
            _imageLoadCts = new CancellationTokenSource();
            _ = LoadImagesAsync(_imageLoadCts.Token);
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

    private async Task LoadImagesAsync(CancellationToken ct = default)
    {
        foreach (var s in Suggestions.ToList())
        {
            if (ct.IsCancellationRequested) break;
            if (!string.IsNullOrEmpty(s.ImageUrl)) continue;
            try
            {
                var url = await _imageProvider.GetImageFileByBlAsync("M", s.BricklinkId, null);
                if (ct.IsCancellationRequested) break;
                if (!string.IsNullOrEmpty(url))
                {
                    var disp = Application.Current?.Dispatcher;
                    if (disp != null)
                        await disp.InvokeAsync(() => s.ImageUrl = url);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "BuildSuggestions: Bild fuer {Bl} nicht ladbar", s.BricklinkId);
            }
        }
    }

    /// <summary>
    /// v0.1.24-beta.8 Phase 3 (Fix 5): aus den nicht-HBSort-100%-Kandidaten
    /// die heraussuchen, die mit BL-Shop-Ergaenzung auf 100% kommen. Diese
    /// werden als zusaetzliche BuildSuggestionItems (mit
    /// <see cref="BuildSuggestionItem.IsBlShopAddition"/>=true) in die eine
    /// gemeinsame Liste eingefuegt.
    /// </summary>
    private async Task<List<BuildSuggestionItem>> BuildBlCompletableAsync(
        List<BuildSuggestionItem> allCandidates,
        Dictionary<(string PartNo, int ColorId), int> haveMap)
    {
        const int maxBlAdditions = 20;
        var teilbare = allCandidates.Where(s => s.MatchPercent < 100).ToList();
        var result = new List<BuildSuggestionItem>();

        // Verfuegbarkeits-Map fuer BL-Inventar - eine Query pro (Part, Color)
        // gecached, damit wir bei vielen Kandidaten mit gleichen Parts nicht
        // N-mal die DB fragen.
        var blCache = new Dictionary<(string, int), int>();

        foreach (var c in teilbare)
        {
            if (result.Count >= maxBlAdditions) break;

            var subsets = await _blCache.GetSubsetsAsync("M", c.BricklinkId);
            subsets = subsets.Where(s => !s.IsFromSupersets && s.ItemType == "P").ToList();
            if (subsets.Count == 0) continue;

            int blShopCount = 0;
            bool canComplete = true;

            foreach (var s in subsets)
            {
                var key = (s.ItemNo, s.ColorId);
                haveMap.TryGetValue(key, out var hbHave);
                var hbTaken = Math.Min(hbHave, s.Quantity);
                var stillNeeded = s.Quantity - hbTaken;
                if (stillNeeded <= 0) continue;

                if (!blCache.TryGetValue(key, out var blAvail))
                {
                    var lots = await _blInventory.FindLotsForPartAsync(s.ItemNo, s.ColorId);
                    blAvail = lots.Sum(l => l.Quantity - l.ReservedQuantity);
                    blCache[key] = blAvail;
                }

                var blTaken = Math.Min(stillNeeded, blAvail);
                if (blTaken < stillNeeded)
                {
                    canComplete = false;
                    break;
                }
                blShopCount += blTaken;
            }

            if (!canComplete || blShopCount == 0) continue;

            // Effektiv-100%-Vorschlag mit BL-Badge. Reuse von c (gleiches
            // ImageUrl-Loading) waere riskant - eigenes Item, Match-% auf
            // 100, MissingLabel auf passenden Hinweis.
            result.Add(new BuildSuggestionItem
            {
                BricklinkId = c.BricklinkId,
                Name = c.Name,
                MatchPercent = 100,
                TotalParts = c.TotalParts,
                MissingPartsCount = 0,
                TotalQtyNeeded = c.TotalQtyNeeded,
                TotalQtyHave = c.TotalQtyNeeded, // alles gedeckt (HBSort + BL)
                IsBlShopAddition = true,
                BlShopPartCount = blShopCount
            });
        }

        return result;
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

    /// <summary>
    /// v0.1.24-beta.8 Phase 3 (Fix 5): true wenn diese Figur nur mit
    /// BL-Shop-Ergaenzung auf 100% kommt (nicht allein aus HBSort).
    /// Steuert den BL-Badge in der UI.
    /// </summary>
    public bool IsBlShopAddition { get; init; }

    /// <summary>Anzahl Teile die aus dem BL-Shop kommen muessten (Badge-Text).</summary>
    public int BlShopPartCount { get; init; }

    [ObservableProperty]
    private string? _imageUrl;

    public string MatchLabel => $"{MatchPercent}% ({TotalQtyHave}/{TotalQtyNeeded})";

    public string MissingLabel
    {
        get
        {
            if (IsBlShopAddition)
                return $"Komplett mit {BlShopPartCount} Teilen aus BL-Shop";
            return MissingPartsCount == 0
                ? "Komplett!"
                : $"Es fehlen {MissingPartsCount} Teile-Sorten";
        }
    }

    public string BlShopBadgeLabel => $"{BlShopPartCount} Teile aus Shop";

    public Brush MatchBrush
    {
        get
        {
            // BL-Erweiterungen bekommen das BL-Blau (visuelle Trennung).
            if (IsBlShopAddition)
                return FreezeBrush(Color.FromRgb(2, 119, 189)); // #0277BD
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
