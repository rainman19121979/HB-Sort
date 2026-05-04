using HBSort.Core.Database;
using HBSort.Core.Models.Pricing;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Phase 8: Default-Implementierung des Preis-Aggregators.
///
/// Vorgehen:
///   1. Provider via Factory ermitteln. Nicht konfiguriert -> NoData.
///   2. TrackedMinifig + RequiredParts laden.
///   3. Parallel den Minifig-Preis und alle Part-Preise holen.
///   4. Korrekturen aus Settings auf BL-Preise aufschlagen.
///   5. SalesAdvice ableiten (CompleteWorthIt / PartsWorthIt / Equal).
/// </summary>
public class PriceCalculationService : IPriceCalculationService
{
    /// <summary>
    /// Schwelle in Prozent, ab der die Empfehlung NICHT mehr "Equal" ist.
    /// 10% Differenz = klare Empfehlung; darunter Gleichstand.
    /// </summary>
    private const decimal EqualThresholdPercent = 10m;

    private readonly IPriceProviderFactory _providerFactory;
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly ISettingsService _settings;

    public PriceCalculationService(
        IPriceProviderFactory providerFactory,
        IDbContextFactory<UserDataContext> ctxFactory,
        ISettingsService settings)
    {
        _providerFactory = providerFactory;
        _ctxFactory = ctxFactory;
        _settings = settings;
    }

    public async Task<SalesRecommendation> CalculateForMinifigAsync(
        int trackedMinifigId, CancellationToken ct = default)
    {
        var provider = _providerFactory.GetActiveProvider();
        var configured = await provider.IsConfiguredAsync(ct);
        if (!configured)
        {
            return new SalesRecommendation
            {
                ProviderInfo = provider.Name,
                Advice = SalesAdvice.NoData
            };
        }

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var m = await ctx.TrackedMinifigs.AsNoTracking()
            .Where(x => x.Id == trackedMinifigId)
            .Include(x => x.RequiredParts)
            .FirstOrDefaultAsync(ct);
        if (m == null)
        {
            return new SalesRecommendation
            {
                ProviderInfo = provider.Name,
                Advice = SalesAdvice.NoData
            };
        }

        var blMinifigId = m.BricklinkId ?? m.FigNum;
        var cfg = _settings.Current.Prices;

        // Parallel-Lookups: Minifig + alle Parts.
        var minifigTask = provider.GetMinifigPriceAsync(blMinifigId, ct);
        var partTasks = m.RequiredParts
            .Select(p => provider.GetPartPriceAsync(p.PartNumber, p.ColorId, ct))
            .ToList();

        await Task.WhenAll(new[] { minifigTask }.Concat(partTasks));

        // Nach WhenAll sind alle Tasks abgeschlossen – await liest direkt den
        // gecachten Wert (kein zweiter Schedule). Lesbarer als .Result.
        var minifigPrice = await minifigTask;
        var partPrices = new List<PriceResult?>(partTasks.Count);
        foreach (var t in partTasks) partPrices.Add(await t);

        // Spalte aus Settings ableiten (fuer beide Seiten gleich).
        // Bugfix Phase-8 #3: liegt jetzt in PriceMath (gemeinsam mit dem
        // WPF-VM) damit die beiden Pfade nicht auseinanderlaufen koennen.
        decimal? PickValue(PriceResult? p) => PriceMath.PickValue(p, cfg.PriceColumn);

        // Minifig: Roh + Korrektur.
        var minifigRaw = PickValue(minifigPrice);
        var correctedMinifig = PriceMath.ApplyCorrectionOrZero(
            minifigRaw ?? 0m, cfg.CorrectionMinifigPercent);

        // Parts: Summe ueber alle Required (qty * Preis), null-Werte zaehlen wir mit.
        decimal? partsRawSum = null;
        var partsWithPrice = 0;
        var partsMissing = 0;
        for (int i = 0; i < m.RequiredParts.Count; i++)
        {
            var p = m.RequiredParts[i];
            var v = PickValue(partPrices[i]);
            if (v.HasValue && v.Value > 0)
            {
                partsRawSum = (partsRawSum ?? 0m) + v.Value * p.QuantityNeeded;
                partsWithPrice++;
            }
            else
            {
                partsMissing++;
            }
        }
        var correctedParts = partsRawSum.HasValue
            ? PriceMath.ApplyCorrectionOrZero(partsRawSum.Value, cfg.CorrectionPartsPercent)
            : 0m;

        // Empfehlung. Nur wenn beide Seiten Daten haben.
        SalesAdvice advice;
        decimal diff = 0m;
        if (!minifigRaw.HasValue || !partsRawSum.HasValue)
        {
            advice = SalesAdvice.NoData;
        }
        else
        {
            diff = correctedMinifig - correctedParts;
            var basis = Math.Max(correctedMinifig, correctedParts);
            var diffPct = basis > 0 ? Math.Abs(diff) / basis * 100m : 0m;

            if (diffPct < EqualThresholdPercent) advice = SalesAdvice.Equal;
            else if (diff > 0)                   advice = SalesAdvice.CompleteWorthIt;
            else                                 advice = SalesAdvice.PartsWorthIt;
        }

        Log.Information(
            "PriceCalc: {BlId} -> minifig={Mfg}, parts={Parts}, advice={Advice}",
            blMinifigId, correctedMinifig, correctedParts, advice);

        return new SalesRecommendation
        {
            ProviderInfo = minifigPrice?.ProviderLabel ?? provider.Name,
            MinifigPrice = minifigPrice,
            CorrectedMinifigPrice = correctedMinifig,
            PartsRawSum = partsRawSum,
            CorrectedPartsSum = correctedParts,
            PartsWithPriceCount = partsWithPrice,
            PartsMissingPriceCount = partsMissing,
            Advice = advice,
            Difference = diff,
            Currency = minifigPrice?.Currency ?? cfg.Currency,
            CalculatedAt = DateTime.UtcNow
        };
    }

}
