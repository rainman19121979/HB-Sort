using System.Collections.Concurrent;
using HBSort.Core.Helpers;
using HBSort.Core.Models;
using HBSort.Core.Models.Pricing;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Default-Implementierung des Stale-While-Revalidate-Cache-Services.
///
/// Wichtig zur Provider-Interaktion:
/// Der existierende BricklinkApiPriceProvider macht intern bereits einen
/// Cache-Read. Da wir den Provider hier ohne Bypass aufrufen, gibt es einen
/// "Doppel-Read" pro Live-Lookup (~1ms Overhead). Das ist bewusst akzeptiert
/// - kein Risiko, kein Refactor des Providers noetig (siehe UX#12-Plan).
///
/// Plan D (Bugfix): Wir cachen den Provider NICHT mehr im Konstruktor,
/// sondern fragen die Factory bei jedem Live-Aufruf neu. Damit greift ein
/// User-Wechsel des Providers im Settings-Dialog sofort, ohne App-Neustart.
/// TTL- und Currency-Settings werden ebenfalls bei jedem Aufruf live aus
/// _settings.Current.Prices gelesen.
/// </summary>
public class BlPriceCacheService : IBlPriceCacheService
{
    /// <summary>
    /// User-sichtbarer Hinweis-Text wenn der Provider in den Settings noch
    /// auf "None" steht (Default fuer Neuinstallationen).
    ///
    /// TODO 2026-05 (UX-Politur): klickbarer "Einstellungen oeffnen"-Button
    /// in der MinifigPriceView, der den Settings-Dialog auf den Preise-Tab
    /// vorgewaehlt oeffnet. Braucht einen INavigationService den Core-Services
    /// nicht direkt referenzieren sollten (UI-Konzern). Bewusst aufgeschoben
    /// auf eine eigene Iteration; aktuell ist der Hinweistext der Workaround.
    /// </summary>
    public const string ProviderNotConfiguredMessage =
        "Preise nicht verfuegbar - Provider noch nicht eingerichtet. " +
        "Oeffne Einstellungen → Preise und waehle 'BL-API'.";

    private readonly IBlCacheRepository _repo;
    private readonly IPriceProviderFactory _providerFactory;
    private readonly ISettingsService _settings;

    /// <summary>
    /// In-Flight-Schutz: Tasks die gerade gegen die API laufen. Wenn ein
    /// zweiter Aufruf fuer denselben Key reinkommt, wartet er auf den
    /// laufenden Task statt einen zweiten API-Call zu starten.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<PriceLookupOutcome>> _inFlight = new();

    public BlPriceCacheService(
        IBlCacheRepository repo,
        IPriceProviderFactory providerFactory,
        ISettingsService settings)
    {
        _repo = repo;
        _providerFactory = providerFactory;
        _settings = settings;
    }

    /// <summary>
    /// Prueft ob ein "echter" Provider hinterlegt ist (nicht "None"). Wird
    /// beim Live-Pfad geprueft; auf den Cache-Pfad wirkt das nicht - alte
    /// Cache-Eintraege bleiben gueltig auch wenn der Provider gerade auf
    /// "None" steht.
    /// </summary>
    private bool IsProviderConfigured()
    {
        var name = _settings.Current.Prices?.Provider;
        return !string.IsNullOrWhiteSpace(name) && name != "None";
    }

    public Task<PriceLookupOutcome> GetMinifigPriceAsync(
        string blMinifigId, CancellationToken ct = default)
        => GetPriceCoreAsync(
            itemType: "M",
            itemNo: blMinifigId,
            colorId: 0,
            ttlDays: _settings.Current.Prices.BlPriceCacheTtlMinifigDays,
            ct: ct);

    public Task<PriceLookupOutcome> GetPartPriceAsync(
        string blPartNo, int blColorId, CancellationToken ct = default)
        => GetPriceCoreAsync(
            itemType: "P",
            itemNo: blPartNo,
            colorId: blColorId,
            ttlDays: _settings.Current.Prices.BlPriceCacheTtlPartDays,
            ct: ct);

    /// <summary>
    /// Kern der Stale-While-Revalidate-Logik. Liefert immer einen Outcome,
    /// auch bei Fehler (Source=None mit ErrorMessage).
    /// </summary>
    private async Task<PriceLookupOutcome> GetPriceCoreAsync(
        string itemType, string itemNo, int colorId, int ttlDays, CancellationToken ct)
    {
        var cfg = _settings.Current.Prices;
        var newOrUsed = "U"; // wir tracken nur Used wie im PriceCalculationService
        var region = cfg.Region ?? string.Empty;
        var currency = string.IsNullOrWhiteSpace(cfg.Currency) ? "EUR" : cfg.Currency;
        var effectiveTtl = Math.Max(1, ttlDays);
        // UX X.34 v0.1.20: Cache-Lookups mit aktuellem VAT-Mode filtern.
        var vatCode = cfg.VatMode switch
        {
            VatMode.Y => "Y",
            VatMode.N => "N",
            VatMode.O => "O",
            _         => "Y"
        };

        // 1) Cache-Lookup mit Stale-Flag.
        CachedPriceLookup? cached = null;
        try
        {
            cached = await _repo.GetCachedPriceWithStaleFlagAsync(
                itemType, itemNo, colorId,
                cfg.GuideType, newOrUsed, region, currency,
                effectiveTtl, ct, vatCode);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Cache-Lookup geworfen ({Type}/{No}/{Color}) - faellt auf API-Pfad zurueck",
                itemType, itemNo, colorId);
        }

        // 2) Hit + frisch -> direkt zurueck.
        if (cached != null && !cached.IsStale)
        {
            return new PriceLookupOutcome(
                Price: cached.Price,
                Source: PriceLookupSource.Cache,
                FetchedAt: cached.Price.FetchedAt,
                ErrorMessage: null);
        }

        // 3) Hit + stale -> Stale-Wert sofort liefern; im Hintergrund revalidieren
        //    (fire-and-forget, eigener Task ohne ct, damit ein vorzeitiger Cancel
        //    am UI das Update nicht killt).
        if (cached != null && cached.IsStale)
        {
            Task.Run(() => RevalidateInBackgroundAsync(
                itemType, itemNo, colorId, cfg.GuideType, newOrUsed, region, currency))
                .FireAndForget($"BlPrice-Revalidate {itemType}/{itemNo}/{colorId}");

            return new PriceLookupOutcome(
                Price: cached.Price,
                Source: PriceLookupSource.Stale,
                FetchedAt: cached.Price.FetchedAt,
                ErrorMessage: null);
        }

        // 4) Plan C: Wenn der Provider gar nicht eingerichtet ist, brauchen wir
        //    weder Live-Call noch In-Flight-Guard. Wichtig: dieser Check MUSS
        //    vor GetLiveWithInFlightGuardAsync stattfinden - die GetOrAdd-
        //    Factory wuerde sonst einen synchron-fertigen Task im
        //    In-Flight-Dict hinterlassen (das finally-TryRemove laeuft VOR
        //    dem Add), was den naechsten Aufruf den alten NotConfigured-
        //    Wert erneut sehen lassen wuerde.
        if (!IsProviderConfigured())
        {
            return new PriceLookupOutcome(
                Price: null,
                Source: PriceLookupSource.None,
                FetchedAt: null,
                ErrorMessage: ProviderNotConfiguredMessage,
                Notice: PriceLookupNotice.NotConfigured);
        }

        // 5) Miss + Provider konfiguriert -> Provider live aufrufen, Cache
        //    schreiben (Provider macht das selbst), mit In-Flight-Schutz
        //    damit parallele Aufrufe nur 1x in die API gehen.
        return await GetLiveWithInFlightGuardAsync(itemType, itemNo, colorId, ct);
    }

    /// <summary>
    /// Live-Lookup gegen den Provider, mit In-Flight-Guard via ConcurrentDictionary.
    /// Wenn fuer denselben Key bereits ein Task laeuft, warten wir darauf statt einen
    /// zweiten zu starten.
    /// </summary>
    private Task<PriceLookupOutcome> GetLiveWithInFlightGuardAsync(
        string itemType, string itemNo, int colorId, CancellationToken ct)
    {
        var key = BuildKey(itemType, itemNo, colorId);

        // GetOrAdd ist atomic: derselbe Key liefert denselben Task.
        return _inFlight.GetOrAdd(key, _ => RunLiveAsync(itemType, itemNo, colorId, key));
    }

    private async Task<PriceLookupOutcome> RunLiveAsync(
        string itemType, string itemNo, int colorId, string key)
    {
        try
        {
            // Plan D: Provider live aus der Factory, kein gecachter _provider mehr.
            // Damit greift ein Settings-Wechsel sofort. Der NotConfigured-Check
            // ist bewusst eine Ebene hoeher (GetPriceCoreAsync), siehe Kommentar
            // dort warum.
            var provider = _providerFactory.GetActiveProvider();

            // Provider hat seinen eigenen internen Cache-Lookup - wir akzeptieren
            // den Doppel-Read (~1ms). Bei Cache-Miss (was hier der Fall ist) macht
            // der Provider den BL-API-Call und schreibt das Ergebnis selbst in
            // den Cache.
            var price = itemType == "M"
                ? await provider.GetMinifigPriceAsync(itemNo)
                : await provider.GetPartPriceAsync(itemNo, colorId);

            if (price != null)
            {
                return new PriceLookupOutcome(
                    Price: price,
                    Source: PriceLookupSource.Live,
                    FetchedAt: price.FetchedAt,
                    ErrorMessage: null);
            }

            return new PriceLookupOutcome(
                Price: null,
                Source: PriceLookupSource.None,
                FetchedAt: null,
                ErrorMessage: "Kein Preis verfuegbar (Provider lieferte null).",
                Notice: PriceLookupNotice.Error);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Live-Lookup fehlgeschlagen ({Type}/{No}/{Color})",
                itemType, itemNo, colorId);
            return new PriceLookupOutcome(
                Price: null,
                Source: PriceLookupSource.None,
                FetchedAt: null,
                ErrorMessage: $"Preis-Lookup fehlgeschlagen: {ex.Message}",
                Notice: PriceLookupNotice.Error);
        }
        finally
        {
            // In-Flight-Slot freigeben - bei naechstem Aufruf wird neu geladen.
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Hintergrund-Revalidation fuer einen stale-Eintrag. Nutzt denselben
    /// In-Flight-Guard - falls derselbe Key gerade live abgerufen wird,
    /// laueft kein zweiter Call.
    /// </summary>
    private async Task RevalidateInBackgroundAsync(
        string itemType, string itemNo, int colorId,
        string guideType, string newOrUsed, string region, string currency)
    {
        try
        {
            var outcome = await GetLiveWithInFlightGuardAsync(itemType, itemNo, colorId,
                CancellationToken.None);
            Log.Debug(
                "Hintergrund-Revalidation {Type}/{No}/{Color} -> {Source}",
                itemType, itemNo, colorId, outcome.Source);
            // Cache-Write hat der Provider intern schon erledigt.
            // UI bekommt den frischen Wert beim naechsten Refresh-Trigger.
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Hintergrund-Revalidation geworfen ({Type}/{No}/{Color})",
                itemType, itemNo, colorId);
        }
    }

    public async Task DeleteForMinifigAsync(
        string blMinifigId,
        IReadOnlyList<(string PartNo, int ColorId)> subsetParts,
        CancellationToken ct = default)
    {
        // UX-Iteration X.10: jetzt eine Convenience ueber die zwei
        // dedizierten Methoden - so bleibt die Loesch-Logik in einem Topf.
        await DeleteMinifigPriceAsync(blMinifigId, ct);
        await DeletePartPricesAsync(subsetParts, ct);
    }

    public async Task DeleteMinifigPriceAsync(string blMinifigId, CancellationToken ct = default)
    {
        var cfg = _settings.Current.Prices;
        var region = cfg.Region ?? string.Empty;
        var currency = string.IsNullOrWhiteSpace(cfg.Currency) ? "EUR" : cfg.Currency;
        // UX X.34 v0.1.20: Refresh loescht den aktuellen VAT-Mode-Eintrag.
        var vatCode = MapVat(cfg.VatMode);

        await _repo.DeletePriceAsync("M", blMinifigId, 0,
            cfg.GuideType, "U", region, currency, ct, vatCode);

        Log.Information("Komplett-Figur-Cache geloescht: {Mfg} ({Guide}, vat={Vat})",
            blMinifigId, cfg.GuideType, vatCode);
    }

    public async Task DeletePartPricesAsync(
        IReadOnlyList<(string PartNo, int ColorId)> subsetParts,
        CancellationToken ct = default)
    {
        var cfg = _settings.Current.Prices;
        var region = cfg.Region ?? string.Empty;
        var currency = string.IsNullOrWhiteSpace(cfg.Currency) ? "EUR" : cfg.Currency;
        var vatCode = MapVat(cfg.VatMode);

        var distinct = subsetParts.Distinct().ToList();
        foreach (var (partNo, colorId) in distinct)
        {
            ct.ThrowIfCancellationRequested();
            await _repo.DeletePriceAsync("P", partNo, colorId,
                cfg.GuideType, "U", region, currency, ct, vatCode);
        }

        Log.Information("Einzelteil-Cache geloescht: {Count} Eintraege ({Guide}, vat={Vat})",
            distinct.Count, cfg.GuideType, vatCode);
    }

    public Task<int> GetEntryCountAsync(CancellationToken ct = default)
        => _repo.GetPriceCacheCountAsync(ct);

    public Task<int> ClearAllAsync(CancellationToken ct = default)
        => _repo.ClearAllPricesAsync(ct);

    /// <summary>UX X.34 v0.1.20: HBSort-VatMode -> BL-API-String-Code (Y/N/O).</summary>
    private static string MapVat(VatMode mode) => mode switch
    {
        VatMode.Y => "Y",
        VatMode.N => "N",
        VatMode.O => "O",
        _         => "Y"
    };

    /// <summary>
    /// Cache-Key fuer das In-Flight-Dictionary. Currency/Region/Guide etc.
    /// sind in der Aufruf-Signatur fix (kommen aus den Settings) - daher
    /// reichen ItemType + ItemNo + ColorId als Key.
    /// </summary>
    private static string BuildKey(string itemType, string itemNo, int colorId)
        => $"{itemType}|{itemNo}|{colorId}";
}
