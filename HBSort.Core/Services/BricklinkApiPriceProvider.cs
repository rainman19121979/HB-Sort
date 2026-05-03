using System.Diagnostics;
using BricklinkSharp.Client;
using HBSort.Core.Models;
using HBSort.Core.Models.Exceptions;
using HBSort.Core.Models.Pricing;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Phase 8: Preis-Provider via BricklinkSharp.GetPriceGuideAsync.
///
/// Ablauf pro Lookup:
///   1. Cache-Hit (nicht stale)? -> direkt zurueck (kein API-Call).
///   2. Rate-Limit-Gate via IBricklinkRateLimiter.
///   3. BL-API-Call, mappen ins PriceResult, in den Cache schreiben.
///   4. API-Fehler? -> stale Cache-Eintrag zurueck wenn vorhanden, sonst null.
///
/// VAT-Parameter wurde absichtlich weggelassen - der Default der Library
/// passt fuer die EU-Prices-in-EUR-Konvention. Falls noetig, koennen wir
/// das spaeter ueber PriceSettings ergaenzen.
/// </summary>
public class BricklinkApiPriceProvider : IPriceProvider
{
    private readonly IBricklinkTokenStorage _tokenStorage;
    private readonly IBlCacheRepository _cache;
    private readonly IBricklinkRateLimiter _rateLimiter;
    private readonly ISettingsService _settings;
    private readonly object _configLock = new();

    public string Name => "BrickLink";

    public BricklinkApiPriceProvider(
        IBricklinkTokenStorage tokenStorage,
        IBlCacheRepository cache,
        IBricklinkRateLimiter rateLimiter,
        ISettingsService settings)
    {
        _tokenStorage = tokenStorage;
        _cache = cache;
        _rateLimiter = rateLimiter;
        _settings = settings;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        if (!_tokenStorage.HasTokens()) return false;
        try
        {
            var tokens = await _tokenStorage.LoadAsync();
            return tokens != null && tokens.IsComplete;
        }
        catch
        {
            return false;
        }
    }

    public Task<PriceResult?> GetMinifigPriceAsync(string blMinifigId, CancellationToken ct = default)
        => GetPriceAsync("M", blMinifigId, 0, ct);

    public Task<PriceResult?> GetPartPriceAsync(string blPartNo, int blColorId, CancellationToken ct = default)
        => GetPriceAsync("P", blPartNo, blColorId, ct);

    private async Task<PriceResult?> GetPriceAsync(string itemType, string itemNo, int colorId, CancellationToken ct)
    {
        var cfg = _settings.Current.Prices;
        var newOrUsed = "U"; // CLAUDE.md: Default-Condition ist Used (Sammler-Workflow)
        var region = cfg.Region ?? string.Empty;
        var currency = string.IsNullOrWhiteSpace(cfg.Currency) ? "EUR" : cfg.Currency;
        var staleDays = Math.Max(1, cfg.CacheDays);

        // 1) Cache-Hit?
        try
        {
            var cached = await _cache.GetCachedPriceAsync(
                itemType, itemNo, colorId, cfg.GuideType, newOrUsed,
                region, currency, staleDays, ct);
            if (cached != null)
            {
                return cached with { ProviderLabel = BuildProviderLabel(cfg) };
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Preis-Cache-Lookup geworfen ({Type}/{No}/{Color})", itemType, itemNo, colorId);
        }

        // 2) Rate-Limit-Gate. Wenn blockiert -> stale Cache-Eintrag versuchen, sonst null.
        if (!await _rateLimiter.CanMakeCallAsync(ct))
        {
            Log.Debug("Preis-Lookup geblockt durch Rate-Limit ({Type}/{No}/{Color})", itemType, itemNo, colorId);
            return await TryStaleFallbackAsync(itemType, itemNo, colorId, cfg, newOrUsed, region, currency, ct);
        }

        // 3) BL-API-Call.
        var sw = Stopwatch.StartNew();
        int statusCode = 200;
        bool success = false;
        BricklinkSharp.Client.IBricklinkClient? client = null;
        try
        {
            client = await CreateClientAsync();
            var blType = ParseItemType(itemType);
            var guideType = cfg.GuideType.Equals("stock", StringComparison.OrdinalIgnoreCase)
                ? PriceGuideType.Stock
                : PriceGuideType.Sold;
            var condition = newOrUsed == "N" ? Condition.New : Condition.Used;

            // Region/Country/Currency koennen alle leer sein -> Library nimmt Defaults.
            var pg = await client.GetPriceGuideAsync(
                blType, itemNo,
                colorId: colorId,
                priceGuideType: guideType,
                condition: condition,
                countryCode: cfg.CountryCode ?? string.Empty,
                region: region,
                currencyCode: currency);

            sw.Stop();
            success = true;

            var result = new PriceResult
            {
                ProviderLabel = BuildProviderLabel(cfg),
                MinPrice = NonZero(pg.MinPrice),
                AvgPrice = NonZero(pg.AveragePrice),
                QtyAvgPrice = NonZero(pg.QuantityAveragePrice),
                MaxPrice = NonZero(pg.MaxPrice),
                UnitQuantity = pg.UnitQuantity,
                TotalQuantity = pg.TotalQuantity,
                Currency = string.IsNullOrWhiteSpace(pg.CurrencyCode) ? currency : pg.CurrencyCode,
                FetchedAt = DateTime.UtcNow
            };

            // 4) In Cache schreiben (best-effort).
            try
            {
                await _cache.UpsertPriceAsync(
                    itemType, itemNo, colorId, cfg.GuideType, newOrUsed,
                    region, currency, result, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Preis-Cache-Write geworfen");
            }

            Log.Information("BL GetPriceGuide({Type},{No},c={Color}) -> avg={Avg}, qty_avg={Qty} ({Ms}ms)",
                itemType, itemNo, colorId, result.AvgPrice, result.QtyAvgPrice, sw.ElapsedMilliseconds);
            return result;
        }
        catch (BricklinkAuthException ex)
        {
            sw.Stop();
            statusCode = 401;
            Log.Warning(ex, "Preis-Lookup: Auth-Fehler");
            return await TryStaleFallbackAsync(itemType, itemNo, colorId, cfg, newOrUsed, region, currency, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            statusCode = (ex.Message ?? "").Contains("404") ? 404
                       : (ex.Message ?? "").Contains("429") ? 429
                       : 500;
            Log.Warning(ex, "Preis-Lookup fehlgeschlagen ({Type}/{No}/{Color})", itemType, itemNo, colorId);
            return await TryStaleFallbackAsync(itemType, itemNo, colorId, cfg, newOrUsed, region, currency, ct);
        }
        finally
        {
            client?.Dispose();
            try
            {
                await _rateLimiter.LogCallAsync("GetPriceGuide", itemType, itemNo,
                    (int)sw.ElapsedMilliseconds, statusCode, success, ct);
            }
            catch (Exception logEx)
            {
                Log.Warning(logEx, "RateLimiter LogCall geworfen");
            }
        }
    }

    /// <summary>
    /// Stale-Fallback: bei API-Fehler oder Rate-Limit-Block den Cache komplett
    /// ohne Stale-Pruefung (staleDays=0) abfragen, damit der User wenigstens
    /// alte Werte sieht.
    /// </summary>
    private async Task<PriceResult?> TryStaleFallbackAsync(
        string itemType, string itemNo, int colorId,
        PriceSettings cfg, string newOrUsed, string region, string currency,
        CancellationToken ct)
    {
        try
        {
            var stale = await _cache.GetCachedPriceAsync(
                itemType, itemNo, colorId, cfg.GuideType, newOrUsed,
                region, currency, staleDays: 0, ct);
            if (stale != null)
            {
                Log.Information("Preis-Lookup: stale Cache-Wert geliefert ({Type}/{No}/{Color}, fetched {Days}d ago)",
                    itemType, itemNo, colorId, (DateTime.UtcNow - stale.FetchedAt).TotalDays);
                return stale with { ProviderLabel = BuildProviderLabel(cfg) + " (Cache)" };
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private string BuildProviderLabel(PriceSettings cfg)
    {
        var guideText = cfg.GuideType.Equals("stock", StringComparison.OrdinalIgnoreCase) ? "Stock" : "Sold";
        var colText = cfg.PriceColumn switch
        {
            "min" => "Min",
            "avg" => "Avg",
            "max" => "Max",
            _     => "Qty-Avg"
        };
        var regText = string.IsNullOrWhiteSpace(cfg.CountryCode) ? cfg.Region : cfg.CountryCode;
        if (string.IsNullOrWhiteSpace(regText)) regText = "global";
        return $"BL {guideText}-{colText}, Used, {cfg.Currency}-{regText}";
    }

    private async Task<BricklinkSharp.Client.IBricklinkClient> CreateClientAsync()
    {
        if (!_tokenStorage.HasTokens())
            throw new BricklinkAuthException("Keine BL-Tokens hinterlegt.");

        var tokens = await _tokenStorage.LoadAsync()
            ?? throw new BricklinkAuthException("BL-Tokens konnten nicht geladen werden.");
        if (!tokens.IsComplete)
            throw new BricklinkAuthException("BL-Tokens unvollstaendig.");

        lock (_configLock)
        {
            BricklinkClientConfiguration.Instance.TokenValue = tokens.TokenValue;
            BricklinkClientConfiguration.Instance.TokenSecret = tokens.TokenSecret;
            BricklinkClientConfiguration.Instance.ConsumerKey = tokens.ConsumerKey;
            BricklinkClientConfiguration.Instance.ConsumerSecret = tokens.ConsumerSecret;
        }
        return BricklinkClientFactory.Build();
    }

    private static ItemType ParseItemType(string s) => s.ToUpperInvariant() switch
    {
        "M" => ItemType.Minifig,
        "P" => ItemType.Part,
        "S" => ItemType.Set,
        _ => throw new ArgumentException($"Unbekannter ItemType '{s}'")
    };

    /// <summary>BricklinkSharp liefert decimal; 0 oder negativ -> null (kein Preis vorhanden).</summary>
    private static decimal? NonZero(decimal v) => v > 0m ? v : null;
}
