using HBSort.Core.Models;
using HBSort.Core.Models.Pricing;
using HBSort.Core.Services;
using HBSort.ViewModels;

namespace HBSort.Tests;

/// <summary>
/// UX-Iteration X.10: Tests fuer das Auto/Manual-Verhalten der zwei
/// MinifigPriceView-Haelften + Cache-First-Pfad in beiden Modi.
/// </summary>
public class MinifigPriceViewModelTests
{
    private readonly StubCache _cache = new();
    private readonly StubSettings _settings = new();

    private static readonly IReadOnlyList<(string PartNo, int ColorId, int QuantityNeeded, string PartName)>
        TwoSubsets = new[]
        {
            ("3001", 11, 2, "Brick 2x4"),
            ("3024",  0, 1, "Plate 1x1")
        };

    // ===== Auto-Mode =====

    [Fact]
    public async Task AutoMode_complete_triggers_LoadComplete_in_constructor()
    {
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Auto;
        _settings.Current.Prices.AutoLoadPartsPrice    = PriceLoadMode.Manual;
        _cache.NextMinifigOutcome = MakeOutcome(10m, PriceLookupSource.Cache);

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        Assert.NotNull(vm.CompleteAutoLoadTask);
        await vm.CompleteAutoLoadTask!; // wartet auf den vom Konstruktor gestarteten Auto-Load

        Assert.True(vm.IsCompleteRequested);
        Assert.True(vm.CompleteHasPrice);
        Assert.False(vm.ShowCompleteIdleButton);  // Button weg, Inhalt sichtbar
        Assert.Equal(1, _cache.MinifigCalls);
        // Parts-Haelfte bleibt im Idle-Zustand.
        Assert.False(vm.IsPartsRequested);
        Assert.True(vm.ShowPartsIdleButton);
        Assert.Equal(0, _cache.PartCalls);
        Assert.Null(vm.PartsAutoLoadTask);
    }

    [Fact]
    public async Task AutoMode_parts_triggers_LoadParts_in_constructor()
    {
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _settings.Current.Prices.AutoLoadPartsPrice    = PriceLoadMode.Auto;
        _cache.NextPartOutcome = MakeOutcome(0.5m, PriceLookupSource.Cache);

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        Assert.NotNull(vm.PartsAutoLoadTask);
        await vm.PartsAutoLoadTask!;

        Assert.True(vm.IsPartsRequested);
        Assert.False(vm.IsCompleteRequested);
        // 2 Subsets -> 2 Provider-Calls.
        Assert.Equal(2, _cache.PartCalls);
        Assert.Equal(0, _cache.MinifigCalls);
        Assert.Null(vm.CompleteAutoLoadTask);
    }

    // ===== Manual-Mode =====

    [Fact]
    public void ManualMode_does_not_trigger_any_load_in_constructor()
    {
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _settings.Current.Prices.AutoLoadPartsPrice    = PriceLoadMode.Manual;

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);

        Assert.False(vm.IsCompleteRequested);
        Assert.False(vm.IsPartsRequested);
        Assert.True(vm.ShowCompleteIdleButton);
        Assert.True(vm.ShowPartsIdleButton);
        Assert.Equal(0, _cache.MinifigCalls);
        Assert.Equal(0, _cache.PartCalls);
    }

    [Fact]
    public async Task ManualMode_click_LoadComplete_triggers_lookup()
    {
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _cache.NextMinifigOutcome = MakeOutcome(20m, PriceLookupSource.Live);

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        Assert.Equal(0, _cache.MinifigCalls);

        await vm.LoadCompleteCommand.ExecuteAsync(null);

        Assert.Equal(1, _cache.MinifigCalls);
        Assert.True(vm.CompleteHasPrice);
        Assert.False(vm.ShowCompleteIdleButton);
    }

    // ===== Cache-Pfad: ein Klick im Manual-Mode auf einen Cache-Hit
    //       darf KEINEN API-Call ausloesen. Wir simulieren das so:
    //       der StubCache liefert beim Lookup ein Source=Cache-Outcome zurueck,
    //       OHNE einen Provider-Call zu machen. Der Stub spielt damit die
    //       Rolle des realen IBlPriceCacheService (der den Provider gar nicht
    //       erst anfasst wenn der Cache frisch ist).

    [Fact]
    public async Task ManualMode_click_with_cache_hit_does_not_call_API()
    {
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _cache.NextMinifigOutcome = MakeOutcome(10m, PriceLookupSource.Cache);
        _cache.SimulateUnderlyingApiCall = false; // Cache-Hit, kein Provider

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        await vm.LoadCompleteCommand.ExecuteAsync(null);

        // Genau ein CACHE-Service-Call (LoadComplete), 0 simulierte API-Calls.
        Assert.Equal(1, _cache.MinifigCalls);
        Assert.Equal(0, _cache.SimulatedApiCalls);
        Assert.Equal(PriceLookupSource.Cache, vm.CompleteOutcome!.Source);
    }

    [Fact]
    public async Task AutoMode_with_cache_hit_does_not_call_API()
    {
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Auto;
        _cache.NextMinifigOutcome = MakeOutcome(10m, PriceLookupSource.Cache);
        _cache.SimulateUnderlyingApiCall = false;

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        await vm.CompleteAutoLoadTask!;

        Assert.Equal(0, _cache.SimulatedApiCalls);
    }

    [Fact]
    public async Task Cache_miss_in_either_mode_causes_API_call()
    {
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _cache.NextMinifigOutcome = MakeOutcome(10m, PriceLookupSource.Live);
        _cache.SimulateUnderlyingApiCall = true;

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        await vm.LoadCompleteCommand.ExecuteAsync(null);

        Assert.Equal(1, _cache.SimulatedApiCalls);
    }

    // ===== Refresh ↻ =====

    [Fact]
    public async Task RefreshComplete_deletes_cache_then_reloads()
    {
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _cache.NextMinifigOutcome = MakeOutcome(10m, PriceLookupSource.Cache);

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        await vm.LoadCompleteCommand.ExecuteAsync(null);
        Assert.Equal(0, _cache.MinifigDeletes);

        // Force-Refresh.
        _cache.NextMinifigOutcome = MakeOutcome(15m, PriceLookupSource.Live);
        await vm.RefreshCompleteCommand.ExecuteAsync(null);

        Assert.Equal(1, _cache.MinifigDeletes);
        Assert.Equal(2, _cache.MinifigCalls);
        Assert.Equal(15m, vm.CompleteRawPrice);
    }

    [Fact]
    public async Task RefreshParts_deletes_subsets_only_not_minifig()
    {
        _settings.Current.Prices.AutoLoadPartsPrice = PriceLoadMode.Manual;
        _cache.NextPartOutcome = MakeOutcome(0.5m, PriceLookupSource.Cache);

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        await vm.LoadPartsCommand.ExecuteAsync(null);
        Assert.Equal(0, _cache.PartDeletes);

        await vm.RefreshPartsCommand.ExecuteAsync(null);

        Assert.Equal(1, _cache.PartDeletes);  // genau ein DeletePartPricesAsync-Call (mit allen Subsets)
        Assert.Equal(0, _cache.MinifigDeletes); // Komplett-Cache nicht angefasst
    }

    // ===== UX X.15: Gruene Markierung der besseren Variante =====

    [Fact]
    public async Task Winner_complete_higher_marks_complete_winning()
    {
        // Komplett 20€, Einzelteile 2x2€ + 1x2€ = 6€ -> Komplett gewinnt.
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _settings.Current.Prices.AutoLoadPartsPrice    = PriceLoadMode.Manual;
        _settings.Current.Prices.CorrectionMinifigPercent = 0m;
        _settings.Current.Prices.CorrectionPartsPercent   = 0m;
        _cache.NextMinifigOutcome = MakeOutcome(20m, PriceLookupSource.Live);
        _cache.NextPartOutcome    = MakeOutcome(2m,  PriceLookupSource.Live);

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        await vm.LoadCompleteCommand.ExecuteAsync(null);
        await vm.LoadPartsCommand.ExecuteAsync(null);

        Assert.True(vm.IsCompleteWinning);
        Assert.False(vm.IsPartsWinning);
    }

    [Fact]
    public async Task Winner_parts_higher_marks_parts_winning()
    {
        // Komplett 5€, Einzelteile 2x10€ + 1x10€ = 30€ -> Einzelteile gewinnen.
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _settings.Current.Prices.AutoLoadPartsPrice    = PriceLoadMode.Manual;
        _settings.Current.Prices.CorrectionMinifigPercent = 0m;
        _settings.Current.Prices.CorrectionPartsPercent   = 0m;
        _cache.NextMinifigOutcome = MakeOutcome(5m,  PriceLookupSource.Live);
        _cache.NextPartOutcome    = MakeOutcome(10m, PriceLookupSource.Live);

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        await vm.LoadCompleteCommand.ExecuteAsync(null);
        await vm.LoadPartsCommand.ExecuteAsync(null);

        Assert.False(vm.IsCompleteWinning);
        Assert.True(vm.IsPartsWinning);
    }

    [Fact]
    public async Task Winner_equal_values_complete_wins_by_default()
    {
        // Komplett 6€ = Einzelteile 2x2€ + 1x2€ = 6€ -> bei Gleichstand
        // gewinnt die Komplett-Figur (Default-Fall).
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _settings.Current.Prices.AutoLoadPartsPrice    = PriceLoadMode.Manual;
        _settings.Current.Prices.CorrectionMinifigPercent = 0m;
        _settings.Current.Prices.CorrectionPartsPercent   = 0m;
        _cache.NextMinifigOutcome = MakeOutcome(6m, PriceLookupSource.Live);
        _cache.NextPartOutcome    = MakeOutcome(2m, PriceLookupSource.Live);

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        await vm.LoadCompleteCommand.ExecuteAsync(null);
        await vm.LoadPartsCommand.ExecuteAsync(null);

        Assert.True(vm.IsCompleteWinning);
        Assert.False(vm.IsPartsWinning);
    }

    [Fact]
    public void Winner_neither_when_nothing_loaded()
    {
        // Keine Haelfte geladen -> keine Markierung.
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _settings.Current.Prices.AutoLoadPartsPrice    = PriceLoadMode.Manual;

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);

        Assert.False(vm.IsCompleteWinning);
        Assert.False(vm.IsPartsWinning);
    }

    [Fact]
    public async Task Winner_neither_when_only_one_half_loaded()
    {
        // Nur Komplett geladen -> noch kein Vergleich moeglich.
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _settings.Current.Prices.AutoLoadPartsPrice    = PriceLoadMode.Manual;
        _cache.NextMinifigOutcome = MakeOutcome(20m, PriceLookupSource.Live);

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        await vm.LoadCompleteCommand.ExecuteAsync(null);

        Assert.True(vm.CompleteHasPrice);
        Assert.False(vm.PartsHasAnyPrice);
        Assert.False(vm.IsCompleteWinning);
        Assert.False(vm.IsPartsWinning);
    }

    [Fact]
    public async Task Winner_neither_when_complete_errored()
    {
        // Komplett-Fehler, Einzelteile geladen -> keine der beiden gewinnt
        // (Fehler-Status macht die "Variante" nicht vergleichbar).
        _settings.Current.Prices.AutoLoadCompletePrice = PriceLoadMode.Manual;
        _settings.Current.Prices.AutoLoadPartsPrice    = PriceLoadMode.Manual;
        _cache.NextMinifigOutcome = new PriceLookupOutcome(
            null, PriceLookupSource.None, null, "boom", PriceLookupNotice.Error);
        _cache.NextPartOutcome = MakeOutcome(2m, PriceLookupSource.Live);

        var vm = new MinifigPriceViewModel(_cache, _settings, "arc007", TwoSubsets);
        await vm.LoadCompleteCommand.ExecuteAsync(null);
        await vm.LoadPartsCommand.ExecuteAsync(null);

        Assert.False(vm.CompleteHasPrice);
        Assert.False(vm.IsCompleteWinning);
        Assert.False(vm.IsPartsWinning);
    }

    // ===== Helpers + Stubs =====

    private static PriceLookupOutcome MakeOutcome(decimal avg, PriceLookupSource source)
        => new(
            new PriceResult
            {
                AvgPrice = avg,
                QtyAvgPrice = avg,
                MinPrice = avg * 0.8m,
                MaxPrice = avg * 1.2m,
                Currency = "EUR",
                FetchedAt = DateTime.UtcNow,
                UnitQuantity = 5
            },
            source,
            DateTime.UtcNow,
            null,
            PriceLookupNotice.None);

    /// <summary>
    /// Stub fuer IBlPriceCacheService. Zaehlt Aufrufe pro Methode und kann
    /// einen "kein Provider-Call gemacht"-Fall (Cache-Hit) simulieren.
    /// </summary>
    private sealed class StubCache : IBlPriceCacheService
    {
        public PriceLookupOutcome? NextMinifigOutcome { get; set; }
        public PriceLookupOutcome? NextPartOutcome { get; set; }

        /// <summary>
        /// Wenn true, zaehlen wir bei jedem Lookup einen "API-Call" mit -
        /// simuliert die Cache-Miss-Situation. Bei false: Cache-Hit, kein
        /// Provider-Call.
        /// </summary>
        public bool SimulateUnderlyingApiCall { get; set; }

        public int MinifigCalls;
        public int PartCalls;
        public int SimulatedApiCalls;
        public int MinifigDeletes;
        public int PartDeletes;
        public int FullDeletes;

        public Task<PriceLookupOutcome> GetMinifigPriceAsync(string blMinifigId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref MinifigCalls);
            if (SimulateUnderlyingApiCall) Interlocked.Increment(ref SimulatedApiCalls);
            return Task.FromResult(NextMinifigOutcome ?? new PriceLookupOutcome(
                null, PriceLookupSource.None, null, "no stub", PriceLookupNotice.Error));
        }

        public Task<PriceLookupOutcome> GetPartPriceAsync(string blPartNo, int blColorId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PartCalls);
            if (SimulateUnderlyingApiCall) Interlocked.Increment(ref SimulatedApiCalls);
            return Task.FromResult(NextPartOutcome ?? new PriceLookupOutcome(
                null, PriceLookupSource.None, null, "no stub", PriceLookupNotice.Error));
        }

        public Task DeleteMinifigPriceAsync(string blMinifigId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref MinifigDeletes);
            return Task.CompletedTask;
        }

        public Task DeletePartPricesAsync(IReadOnlyList<(string PartNo, int ColorId)> subsetParts, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PartDeletes);
            return Task.CompletedTask;
        }

        public Task DeleteForMinifigAsync(
            string blMinifigId,
            IReadOnlyList<(string PartNo, int ColorId)> subsetParts,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref FullDeletes);
            return Task.CompletedTask;
        }

        public Task<int> GetEntryCountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ClearAllAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; } = new()
        {
            Prices = new PriceSettings
            {
                Provider = "BricklinkApi",
                Currency = "EUR",
                Region = "europe",
                GuideType = "sold",
                AutoLoadCompletePrice = PriceLoadMode.Manual,
                AutoLoadPartsPrice    = PriceLoadMode.Manual,
                CorrectionMinifigPercent = 0m,
                CorrectionPartsPercent = 0m
            }
        };
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }
}
