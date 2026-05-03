using HBSort.Core.Models.Pricing;

namespace HBSort.Core.Services;

/// <summary>
/// Phase 8: Default-Provider fuer Provider="None". Liefert immer null,
/// markiert sich als nicht konfiguriert. Die UI-Layer prueft IsConfiguredAsync
/// und versteckt den Verkaufsempfehlung-Block.
/// </summary>
public class DummyPriceProvider : IPriceProvider
{
    public string Name => "Keine Preise";

    public Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<PriceResult?> GetMinifigPriceAsync(string blMinifigId, CancellationToken ct = default)
        => Task.FromResult<PriceResult?>(null);

    public Task<PriceResult?> GetPartPriceAsync(string blPartNo, int blColorId, CancellationToken ct = default)
        => Task.FromResult<PriceResult?>(null);
}
