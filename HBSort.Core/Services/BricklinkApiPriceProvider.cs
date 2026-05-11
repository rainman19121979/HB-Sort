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
/// VAT-Parameter via PriceSettings.VatMode (UX X.34 v0.1.20):
/// Default Y = Brutto-Preise (matcht BL-Webseite). Vorher war kein
/// vat-Parameter gesetzt - BL-Default ist Netto, was gemischte Aggregate
/// erzeugt hat (Privat-Verkaeufer brutto, gewerblich netto). Verkaufs-
/// empfehlungen waren irrefuehrend. Cache-Lookups gehen seit X.34 mit
/// vat_mode-Filter durch (alte Bestand-Eintraege als 'N' markiert).
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
        var currency = string.IsNullOrWhiteSpace(cfg.Currency) ? "EUR" : cfg.Currency;
        // UX X.34 v0.1.20: VAT-Mode aus Settings, gemappt auf BricklinkSharp.VatOption.
        var vatCode = MapVatModeToCode(cfg.VatMode);
        var vatOption = MapVatModeToOption(cfg.VatMode);

        // UX X.34 v0.1.20 Either-Or-Fix: BL-API erwartet entweder Land
        // ODER Region, nicht beide gleichzeitig. Vorher haben wir beide
        // geschickt - bei Sold + DE filterte BL nur auf DE-Verkaeufer-Sales,
        // was bei seltenen Items 0 Treffer ergab. Logik:
        //   - CountryCode gesetzt -> wins, Region wird auf "" gezwungen
        //   - CountryCode leer + Region gesetzt -> Region greift
        //   - Beide leer -> global (kein Filter)
        // Cache-Key nutzt die effektiven Werte damit Lookup + Upsert konsistent
        // sind und bei Filter-Mode-Wechsel ein frischer API-Call ausgeloest wird.
        var hasCountry = !string.IsNullOrWhiteSpace(cfg.CountryCode);
        var effectiveCountry = hasCountry ? cfg.CountryCode!.Trim() : string.Empty;
        var effectiveRegion  = hasCountry ? string.Empty : (cfg.Region ?? string.Empty).Trim();
        // Audit W-8 (2026-05-04): pro Item-Typ die dedizierte TTL nutzen.
        // Frueher hatte hier ein einzelnes cfg.CacheDays-Feld gestanden; das
        // ist im Stale-While-Revalidate-Pfad (BlPriceCacheService) schon laenger
        // durch zwei TTL-Felder ersetzt - der Provider hat sie aber bisher
        // ignoriert. Jetzt konsistent: M -> Minifig-TTL, P -> Part-TTL.
        var staleDays = Math.Max(1, itemType == "M"
            ? cfg.BlPriceCacheTtlMinifigDays
            : cfg.BlPriceCacheTtlPartDays);

        // 1) Cache-Hit?
        try
        {
            var cached = await _cache.GetCachedPriceAsync(
                itemType, itemNo, colorId, cfg.GuideType, newOrUsed,
                effectiveRegion, currency, staleDays, ct, vatCode);
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
            return await TryStaleFallbackAsync(itemType, itemNo, colorId, cfg, newOrUsed, effectiveRegion, currency, vatCode, ct);
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
            // UX X.34 v0.1.20: vat-Parameter explizit gesetzt (Default Y = Brutto).
            // Either-Or: effectiveCountry und effectiveRegion sind nie beide gesetzt
            // (siehe Berechnung oben). Damit erhaelt BL-API einen einzelnen Filter.
            //
            // BUG-FIX v0.1.20: leere Strings explizit auf null normalisieren.
            // BricklinkSharp 1.9.0 sendet leere Strings als URL-Parameter
            // ("&country_code=") - BL-API behandelt das nicht wie "weglassen".
            // BL-API-Doku: "If you don't specify both country_code and region,
            // this method retrieves the price information regardless of the
            // store's location" - "don't specify" heisst NICHT mitsenden.
            // BricklinkSharp.IBricklinkClient hat string? mit Default null,
            // also reicht null hier um den Parameter im URL wegzulassen.
            var apiCountry = string.IsNullOrWhiteSpace(effectiveCountry) ? null : effectiveCountry;
            var apiRegion  = string.IsNullOrWhiteSpace(effectiveRegion)  ? null : effectiveRegion;
            Log.Information("BL GetPriceGuide: type={Type} no={No} color={Color} guide={Guide} country={Country} region={Region} currency={Currency} vat={Vat}",
                itemType, itemNo, colorId, cfg.GuideType,
                apiCountry ?? "(null)", apiRegion ?? "(null)", currency, vatCode);
            var pg = await client.GetPriceGuideAsync(
                blType, itemNo,
                colorId: colorId,
                priceGuideType: guideType,
                condition: condition,
                countryCode: apiCountry,
                region: apiRegion,
                currencyCode: currency,
                vat: vatOption);

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
            // BUG-FIX v0.1.20: leere API-Resultate NICHT cachen (Cache-
            // Vergiftung verhindern). Wenn alle Preis-Felder null sind UND
            // total_quantity=0, hat BL kein Resultat geliefert (z.B. weil
            // der Filter alle Verkaufstransaktionen ausgeschlossen hat).
            // Das in den Cache zu schreiben wuerde dazu fuehren dass spaetere
            // Lookups das null-Resultat zurueckgeben statt den API erneut zu
            // fragen. Stattdessen: Cache-Eintrag NICHT anlegen, der naechste
            // Lookup loest erneut einen API-Call aus (gut bei kuenftiger
            // Datenverfuegbarkeit oder geaendertem Filter).
            var isEmptyResult = result.MinPrice is null
                && result.AvgPrice is null
                && result.QtyAvgPrice is null
                && result.MaxPrice is null
                && result.TotalQuantity == 0;
            if (isEmptyResult)
            {
                Log.Information(
                    "BL GetPriceGuide({Type},{No},c={Color}) -> leeres Resultat (alle Preise null) - Cache wird NICHT geschrieben (Anti-Vergiftung)",
                    itemType, itemNo, colorId);
            }
            else
            {
                try
                {
                    await _cache.UpsertPriceAsync(
                        itemType, itemNo, colorId, cfg.GuideType, newOrUsed,
                        effectiveRegion, currency, result, ct, vatCode);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Preis-Cache-Write geworfen");
                }
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
            return await TryStaleFallbackAsync(itemType, itemNo, colorId, cfg, newOrUsed, effectiveRegion, currency, vatCode, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            statusCode = (ex.Message ?? "").Contains("404") ? 404
                       : (ex.Message ?? "").Contains("429") ? 429
                       : 500;
            Log.Warning(ex, "Preis-Lookup fehlgeschlagen ({Type}/{No}/{Color})", itemType, itemNo, colorId);
            return await TryStaleFallbackAsync(itemType, itemNo, colorId, cfg, newOrUsed, effectiveRegion, currency, vatCode, ct);
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
        string vatCode,
        CancellationToken ct)
    {
        try
        {
            var stale = await _cache.GetCachedPriceAsync(
                itemType, itemNo, colorId, cfg.GuideType, newOrUsed,
                region, currency, staleDays: 0, ct, vatCode);
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
        // UX X.34 v0.1.20: VAT-Mode im Label, damit User sofort sieht ob
        // Brutto- oder Netto-Preise angezeigt werden.
        var vatText = cfg.VatMode switch
        {
            VatMode.Y => "Brutto",
            VatMode.N => "Netto",
            VatMode.O => "NO",
            _         => "Brutto"
        };
        return $"BL {guideText}-{colText}, Used, {cfg.Currency}-{regText}, {vatText}";
    }

    /// <summary>
    /// UX X.34 v0.1.20: HBSort-VatMode -> BricklinkSharp.VatOption.
    /// Y/Brutto -> Include, N/Netto -> Exclude, O/Norwegen -> IncludeAsNorway.
    /// </summary>
    private static BricklinkSharp.Client.VatOption MapVatModeToOption(VatMode mode) => mode switch
    {
        VatMode.Y => BricklinkSharp.Client.VatOption.Include,
        VatMode.N => BricklinkSharp.Client.VatOption.Exclude,
        VatMode.O => BricklinkSharp.Client.VatOption.IncludeAsNorway,
        _         => BricklinkSharp.Client.VatOption.Include
    };

    /// <summary>
    /// UX X.34 v0.1.20: HBSort-VatMode -> BL-API-String-Code (fuer Cache-Key).
    /// Identisch zur BL-Spec: Y/N/O.
    /// </summary>
    private static string MapVatModeToCode(VatMode mode) => mode switch
    {
        VatMode.Y => "Y",
        VatMode.N => "N",
        VatMode.O => "O",
        _         => "Y"
    };

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
