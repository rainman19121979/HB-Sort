using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Pricing;
using HBSort.Core.Services;
using HBSort.Services;
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
    // v0.1.24-beta.10 Hotfix: Mindest-Schwellwert fuer Teilmatches aus
    // ScoreThresholdMin (0.0-1.0) lesen.
    private readonly ISettingsService _settings;
    // v0.1.24-beta.11: Bulk-Preise-Holen + Toast-Notify.
    private readonly IPriceProviderFactory _priceFactory;
    private readonly INotificationService _notify;

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
    [NotifyPropertyChangedFor(nameof(CanFetchPrices))]
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

    // ============================================================
    // v0.1.24-beta.11: Persistente Ignorier-Liste
    // ============================================================
    /// <summary>Anzahl persistent ignorierter Figuren (fuer Footer-Reset-Link).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIgnored))]
    [NotifyPropertyChangedFor(nameof(IgnoredCountLabel))]
    private int _ignoredCount;

    public bool HasIgnored => IgnoredCount > 0;
    public string IgnoredCountLabel => $"Ignorierte verwalten ({IgnoredCount})";

    // ============================================================
    // v0.1.24-beta.11: Preise-Holen-Feature
    // ============================================================
    /// <summary>True waehrend FetchAllPricesAsync laeuft (Button disabled, Spinner).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetchPrices))]
    private bool _isFetchingPrices;

    /// <summary>Button-Enable: nicht waehrend Refresh, nicht waehrend Fetch, mind. 1 Vorschlag.</summary>
    public bool CanFetchPrices => !IsLoading && !IsFetchingPrices && Suggestions.Count > 0;

    public BuildSuggestionsViewModel(
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCacheRepository blCache,
        IPartImageProvider imageProvider,
        IMinifigPersistenceService persistence,
        IBlInventoryService blInventory,
        ISettingsService settings,
        IPriceProviderFactory priceFactory,
        INotificationService notify)
    {
        _ctxFactory = ctxFactory;
        _blCache = blCache;
        _imageProvider = imageProvider;
        _persistence = persistence;
        _blInventory = blInventory;
        _settings = settings;
        _priceFactory = priceFactory;
        _notify = notify;

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

    // ============================================================
    // v0.1.24-beta.11: Ignorier-Commands (persistent)
    // ============================================================
    [RelayCommand]
    public async Task IgnoreSuggestionAsync(BuildSuggestionItem? item)
    {
        if (item == null) return;
        try
        {
            await using (var ctx = await _ctxFactory.CreateDbContextAsync())
            {
                var exists = await ctx.IgnoredBuildSuggestions
                    .AsNoTracking()
                    .AnyAsync(i => i.BricklinkId == item.BricklinkId);
                if (!exists)
                {
                    ctx.IgnoredBuildSuggestions.Add(new IgnoredBuildSuggestion
                    {
                        BricklinkId = item.BricklinkId,
                        IgnoredAt = DateTime.UtcNow
                    });
                    await ctx.SaveChangesAsync();
                }
            }
            // beta.11 Fix 1: voller Refresh statt Suggestions.Remove(item).
            // Damit rueckt der naechstbeste nicht-ignorierte Kandidat (z.B.
            // der vorher auf Position 21+ war und damit unter dem MaxSuggestions-
            // Cap lag) automatisch nach.
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BuildSuggestions: Ignorieren fehlgeschlagen ({Bl})", item.BricklinkId);
            _notify.ShowError($"Konnte '{item.Name}' nicht ignorieren: {ex.Message}");
        }
    }

    // ============================================================
    // v0.1.24-beta.11: Alle Preise holen (Bulk, gedrosselt)
    // ============================================================
    [RelayCommand]
    public async Task FetchAllPricesAsync()
    {
        // Snapshot der aktuellen Vorschlaege - waehrend des Holens kann ein
        // Refresh den Suggestions-Stream tauschen, dann wuerden wir auf
        // freigegebene Items schreiben.
        var snapshot = Suggestions.ToList();
        if (snapshot.Count == 0) return;

        var provider = _priceFactory.GetActiveProvider();
        if (!await provider.IsConfiguredAsync())
        {
            _notify.ShowWarning("Preis-Provider nicht konfiguriert (BL-Tokens fehlen oder Provider = None).");
            return;
        }

        IsFetchingPrices = true;
        var priceColumn = _settings.Current.Prices.PriceColumn;

        // Drosseln: max 3 parallel. Rate-Limit-Gate ist im Provider selbst,
        // hier limitieren wir nur die Netzwerk-Concurrency.
        using var sem = new SemaphoreSlim(3, 3);
        int fresh = 0, fromCache = 0, failed = 0;

        try
        {
            var tasks = snapshot.Select(async item =>
            {
                await sem.WaitAsync();
                try
                {
                    var result = await provider.GetMinifigPriceAsync(item.BricklinkId);
                    if (result == null || !result.HasAnyPrice)
                    {
                        await SetPriceFailedOnUiAsync(item);
                        Interlocked.Increment(ref failed);
                        return;
                    }
                    var pick = PriceMath.PickValue(result, priceColumn);
                    if (!pick.HasValue)
                    {
                        await SetPriceFailedOnUiAsync(item);
                        Interlocked.Increment(ref failed);
                        return;
                    }
                    await SetPriceOnUiAsync(item, pick.Value, result.Currency);
                    if (result.FromCache) Interlocked.Increment(ref fromCache);
                    else Interlocked.Increment(ref fresh);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "FetchAllPrices: Lookup fehlgeschlagen ({Bl})", item.BricklinkId);
                    await SetPriceFailedOnUiAsync(item);
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    sem.Release();
                }
            });
            await Task.WhenAll(tasks);

            _notify.ShowInfo($"Preise: {fresh} geholt, {fromCache} aus Cache, {failed} fehlgeschlagen.");
        }
        finally
        {
            IsFetchingPrices = false;
        }
    }

    /// <summary>UI-Thread-Wrapper - Item-Property-Set immer auf dem Dispatcher.</summary>
    private async Task SetPriceOnUiAsync(BuildSuggestionItem item, decimal value, string currency)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess())
        {
            await disp.InvokeAsync(() =>
            {
                item.PriceValue = value;
                item.PriceCurrency = currency;
                item.PriceLookupFailed = false;
            });
        }
        else
        {
            item.PriceValue = value;
            item.PriceCurrency = currency;
            item.PriceLookupFailed = false;
        }
    }

    private async Task SetPriceFailedOnUiAsync(BuildSuggestionItem item)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess())
        {
            await disp.InvokeAsync(() => item.PriceLookupFailed = true);
        }
        else
        {
            item.PriceLookupFailed = true;
        }
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

            // 5) v0.1.24-beta.11: drei Kategorien in einer Liste, mit
            //    persistenter Ignorier-Filterung + sauberer Modus-Trennung.
            //
            //    Modus 1 (Toggle AUS):  Kat.1 (HBSort 100%) + Kat.3 (Teilmatch
            //                            oberhalb Schwellwert), je nur Match-Badge.
            //    Modus 2 (Toggle AN):   Kat.1 + Kat.2 (Shop-completable, 100%
            //                            + Shop-Badge) + Kat.3 (Teilmatch, ggf.
            //                            mit "X Teil(e) aus Shop"-Badge).
            //
            //    Sortierung: Kat.1 (Name asc) -> Kat.2 (wenig Shop-Teile zuerst)
            //                -> Kat.3 (Match-% absteigend).
            HasAnyBlInventory = await _blInventory.HasAnyInventoryAsync();

            // Persistente Ignorier-Liste laden + sofort filtern.
            var ignoredSet = await ctx.IgnoredBuildSuggestions
                .AsNoTracking()
                .Select(i => i.BricklinkId)
                .ToListAsync();
            var ignoredHash = new HashSet<string>(ignoredSet, StringComparer.OrdinalIgnoreCase);
            IgnoredCount = ignoredHash.Count;

            suggestions = suggestions
                .Where(s => !ignoredHash.Contains(s.BricklinkId))
                .ToList();

            // Schwellwert: ScoreThresholdMin (0.0-1.0) auf Prozent (0-100) skalieren.
            // Default 0.5 → 50%. Items unter dem Schwellwert verschwinden komplett.
            var thresholdPercent = (int)(_settings.Current.ScoreThresholdMin * 100);

            // BL-Shop-Analyse fuer alle <100%-Kandidaten (canComplete-Flag +
            // helpCount). Nur ausfuehren wenn der Toggle aktiv ist - sonst
            // verschwendete DB-Queries.
            var shopHelp = new Dictionary<string, (int helpCount, bool canComplete)>(StringComparer.OrdinalIgnoreCase);
            if (IncludeBlInventory && HasAnyBlInventory)
            {
                shopHelp = await AnalyzeBlShopHelpAsync(suggestions, haveMap);
            }

            // Kat.2-IDs (Shop-completable). Nur in Modus 2 relevant.
            var cat2Ids = shopHelp
                .Where(kv => kv.Value.canComplete)
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Kat.1: HBSort 100% (nicht durch Shop-Pool ersetzt).
            var cat1 = suggestions
                .Where(s => s.MatchPercent >= 100 && !cat2Ids.Contains(s.BricklinkId))
                .OrderBy(s => s.Name)
                .ToList();

            // Kat.2: Shop-completable (nur in Modus 2). Wir bauen frische
            // Items mit IsBlShopAddition=true + BlShopPartCount=helpCount.
            var cat2 = new List<BuildSuggestionItem>();
            if (IncludeBlInventory && HasAnyBlInventory)
            {
                foreach (var c in suggestions.Where(s => s.MatchPercent < 100 && cat2Ids.Contains(s.BricklinkId)))
                {
                    var (helpCount, _) = shopHelp[c.BricklinkId];
                    cat2.Add(new BuildSuggestionItem
                    {
                        BricklinkId = c.BricklinkId,
                        Name = c.Name,
                        MatchPercent = 100,
                        TotalParts = c.TotalParts,
                        MissingPartsCount = 0,
                        TotalQtyNeeded = c.TotalQtyNeeded,
                        TotalQtyHave = c.TotalQtyNeeded,
                        IsBlShopAddition = true,
                        BlShopPartCount = helpCount
                    });
                }
                cat2 = cat2
                    .OrderBy(s => s.BlShopPartCount)
                    .ThenBy(s => s.Name)
                    .ToList();
            }

            // Kat.3: Teilmatches oberhalb Schwellwert, NICHT bereits in Kat.2.
            // In Modus 2 annotieren wir mit Shop-Help-Count (=> orange Badge wird
            // durch BL-Badge ersetzt; siehe BuildSuggestionItem.HasBlShopContribution).
            var cat3 = new List<BuildSuggestionItem>();
            foreach (var s in suggestions
                .Where(s => s.MatchPercent < 100
                         && s.MatchPercent >= thresholdPercent
                         && !cat2Ids.Contains(s.BricklinkId)))
            {
                int helpCount = 0;
                if (IncludeBlInventory && HasAnyBlInventory
                    && shopHelp.TryGetValue(s.BricklinkId, out var info))
                {
                    helpCount = info.helpCount;
                }
                cat3.Add(new BuildSuggestionItem
                {
                    BricklinkId = s.BricklinkId,
                    Name = s.Name,
                    MatchPercent = s.MatchPercent,
                    TotalParts = s.TotalParts,
                    MissingPartsCount = s.MissingPartsCount,
                    TotalQtyNeeded = s.TotalQtyNeeded,
                    TotalQtyHave = s.TotalQtyHave,
                    IsBlShopAddition = false,
                    BlShopPartCount = helpCount
                });
            }
            cat3 = cat3
                .OrderByDescending(s => s.MatchPercent)
                .ThenBy(s => s.MissingPartsCount)
                .ToList();

            // Top-N greift auf die Gesamt-Liste. Damit Kat.1/2 bevorzugt sichtbar
            // bleiben: zuerst konkatenieren, dann Take.
            var top = cat1.Concat(cat2).Concat(cat3).Take(MaxSuggestions).ToList();

            Suggestions.Clear();
            foreach (var s in top) Suggestions.Add(s);

            int shownCat1 = 0, shownCat2 = 0, shownCat3 = 0;
            foreach (var s in top)
            {
                if (s.IsBlShopAddition) shownCat2++;
                else if (s.MatchPercent >= 100) shownCat1++;
                else shownCat3++;
            }

            SummaryText = top.Count == 0
                ? (thresholdPercent > 0
                    ? $"Keine Bauvorschlaege ueber {thresholdPercent}% Match - Schwellwert in Einstellungen pruefen."
                    : "Keine Bauvorschlaege - keine deiner losen Teile passt zu einer ungetrackten Minifig.")
                : (IncludeBlInventory && HasAnyBlInventory
                    ? $"{top.Count} Vorschlaege ({shownCat1} aus HBSort, {shownCat2} mit BL-Shop, {shownCat3} Teilmatches)"
                    : $"{top.Count} Vorschlaege ({shownCat1} aus HBSort, {shownCat3} Teilmatches)");
            OnPropertyChanged(nameof(CanFetchPrices));

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
    /// v0.1.24-beta.11: einheitliche BL-Shop-Analyse fuer alle Kandidaten mit
    /// MatchPercent &lt; 100. Liefert pro BricklinkId zurueck wieviele Teile
    /// vom BL-Shop benoetigt waeren und ob das die Figur komplettieren wuerde.
    ///
    /// Vorher (beta.8 - beta.10) war das nur die "kann komplettieren"-Liste -
    /// fuer Kat.3 Teilmatches mit teilweiser Shop-Hilfe gab es keinen Wert.
    /// Jetzt unified: Caller entscheidet ob er nur canComplete=true (Kat.2)
    /// oder auch helpCount &gt; 0 (Kat.3-Annotation) nutzt.
    /// </summary>
    private async Task<Dictionary<string, (int helpCount, bool canComplete)>> AnalyzeBlShopHelpAsync(
        List<BuildSuggestionItem> allCandidates,
        Dictionary<(string PartNo, int ColorId), int> haveMap)
    {
        var result = new Dictionary<string, (int helpCount, bool canComplete)>(StringComparer.OrdinalIgnoreCase);
        var teilbare = allCandidates.Where(s => s.MatchPercent < 100).ToList();

        // Verfuegbarkeits-Map fuer BL-Inventar - eine Query pro (Part, Color)
        // gecached, damit wir bei vielen Kandidaten mit gleichen Parts nicht
        // N-mal die DB fragen.
        var blCache = new Dictionary<(string, int), int>();

        foreach (var c in teilbare)
        {
            var subsets = await _blCache.GetSubsetsAsync("M", c.BricklinkId);
            subsets = subsets.Where(s => !s.IsFromSupersets && s.ItemType == "P").ToList();
            if (subsets.Count == 0) continue;

            int blHelp = 0;
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
                blHelp += blTaken;
                if (blTaken < stillNeeded)
                {
                    canComplete = false;
                    // Hier KEIN break - wir wollen die volle helpCount-Summe
                    // auch wenn ein einzelnes Teil nicht reicht (fuer
                    // Kat.3-Annotation "X Teil(e) aus Shop").
                }
            }

            if (blHelp > 0)
                result[c.BricklinkId] = (blHelp, canComplete);
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
    /// Steuert die "100% (X/Y)" + BL-Badge-Kombo in der UI.
    /// </summary>
    public bool IsBlShopAddition { get; init; }

    /// <summary>
    /// Anzahl Teile-Stueck die aus dem BL-Shop genutzt werden koennten.
    /// Bei IsBlShopAddition: deckt zusammen mit HBSort die ganze Figur ab.
    /// Bei Kat.3 (beta.11): Teilbeitrag des Shops (kann fuer 100% nicht reichen).
    /// </summary>
    public int BlShopPartCount { get; init; }

    /// <summary>
    /// v0.1.24-beta.11: True wenn der BL-Shop irgendwie helfen wuerde -
    /// entweder komplettiert (Kat.2) oder partial (Kat.3-Annotation).
    /// Steuert die Sichtbarkeit des blauen "X Teil(e) aus Shop"-Badges.
    /// </summary>
    public bool HasBlShopContribution => BlShopPartCount > 0;

    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>"60% (3/5)" - der einzige Match-Badge in der Vorschlags-Zeile.</summary>
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

    /// <summary>"3 Teile aus Shop" / "1 Teil aus Shop" - korrekt pluralisiert.</summary>
    public string BlShopBadgeLabel =>
        BlShopPartCount == 1 ? "1 Teil aus Shop" : $"{BlShopPartCount} Teile aus Shop";

    // ============================================================
    // v0.1.24-beta.11: Preis-Anzeige (FetchAllPricesCommand)
    // ============================================================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrice))]
    [NotifyPropertyChangedFor(nameof(PriceLabel))]
    private decimal? _priceValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrice))]
    [NotifyPropertyChangedFor(nameof(PriceLabel))]
    private string? _priceCurrency;

    /// <summary>True wenn der Preis tatsaechlich geholt wurde und einen Wert hat.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrice))]
    [NotifyPropertyChangedFor(nameof(PriceLabel))]
    private bool _priceLookupFailed;

    public bool HasPrice => PriceValue.HasValue || PriceLookupFailed;

    /// <summary>"4,75 EUR" / "Preis n/v" je nach Lookup-Ergebnis.</summary>
    public string PriceLabel
    {
        get
        {
            if (PriceLookupFailed) return "Preis n/v";
            if (PriceValue.HasValue)
            {
                var cur = string.IsNullOrWhiteSpace(PriceCurrency) ? "EUR" : PriceCurrency;
                return $"{PriceValue.Value.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} {cur}";
            }
            return string.Empty;
        }
    }

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
