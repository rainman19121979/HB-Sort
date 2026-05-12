using HBSort.Core.Models.Pricing;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Bugfix Phase-8 #3: PriceMath kapselt die User-Settings-Anwendung
/// (PriceColumn-Wahl + Korrektur-Prozent) damit die Logik in einem
/// einzigen Topf liegt und sowohl PriceCalculationService als auch das
/// WPF-MinifigPriceViewModel sie nutzen koennen, ohne auseinanderzulaufen.
/// </summary>
public class PriceMathTests
{
    // ===== PickValue =====

    [Fact]
    public void PickValue_min_returns_MinPrice()
    {
        var p = MakePrice(min: 1m, avg: 2m, qtyAvg: 3m, max: 4m);
        Assert.Equal(1m, PriceMath.PickValue(p, "min"));
    }

    [Fact]
    public void PickValue_avg_returns_AvgPrice()
    {
        var p = MakePrice(min: 1m, avg: 2m, qtyAvg: 3m, max: 4m);
        Assert.Equal(2m, PriceMath.PickValue(p, "avg"));
    }

    [Fact]
    public void PickValue_max_returns_MaxPrice()
    {
        var p = MakePrice(min: 1m, avg: 2m, qtyAvg: 3m, max: 4m);
        Assert.Equal(4m, PriceMath.PickValue(p, "max"));
    }

    [Fact]
    public void PickValue_qty_avg_returns_QtyAvgPrice_when_present()
    {
        var p = MakePrice(min: 1m, avg: 2m, qtyAvg: 3m, max: 4m);
        Assert.Equal(3m, PriceMath.PickValue(p, "qty_avg"));
    }

    [Fact]
    public void PickValue_qty_avg_falls_back_to_AvgPrice_when_QtyAvg_null()
    {
        // Tritt bei zu wenigen Listings auf (BL liefert dann nur AvgPrice).
        var p = MakePrice(min: 1m, avg: 2m, qtyAvg: null, max: 4m);
        Assert.Equal(2m, PriceMath.PickValue(p, "qty_avg"));
    }

    [Fact]
    public void PickValue_unknown_column_string_falls_back_to_qty_avg_then_avg()
    {
        // Default-Branch: alles unbekannte landet beim qty_avg-Pfad.
        var p = MakePrice(min: 1m, avg: 2m, qtyAvg: 3m, max: 4m);
        Assert.Equal(3m, PriceMath.PickValue(p, "wat"));
        Assert.Equal(3m, PriceMath.PickValue(p, null));
    }

    [Fact]
    public void PickValue_null_input_returns_null()
    {
        Assert.Null(PriceMath.PickValue(null, "avg"));
        Assert.Null(PriceMath.PickValue(null, "qty_avg"));
    }

    // ===== ApplyCorrection =====

    [Fact]
    public void ApplyCorrection_zero_percent_returns_raw_value()
    {
        Assert.Equal(10.00m, PriceMath.ApplyCorrection(10m, 0m));
    }

    [Fact]
    public void ApplyCorrection_minus_10_percent_yields_factor_0_90()
    {
        // 10 * 0.90 = 9.00
        Assert.Equal(9.00m, PriceMath.ApplyCorrection(10m, -10m));
    }

    [Fact]
    public void ApplyCorrection_plus_5_percent_yields_factor_1_05()
    {
        // 20 * 1.05 = 21.00
        Assert.Equal(21.00m, PriceMath.ApplyCorrection(20m, 5m));
    }

    [Fact]
    public void ApplyCorrection_negative_percent_works_for_parts_too()
    {
        // typischer User-Case: -15% Parts.
        Assert.Equal(8.50m, PriceMath.ApplyCorrection(10m, -15m));
    }

    [Fact]
    public void ApplyCorrection_null_input_returns_null()
    {
        Assert.Null(PriceMath.ApplyCorrection(null, -10m));
    }

    [Fact]
    public void ApplyCorrection_rounds_kaufmaennisch_to_4_decimals()
    {
        // 3.33335 * 0.90 = 3.000015 -> 3.0000 (AwayFromZero auf 4 Stellen)
        Assert.Equal(3.0000m, PriceMath.ApplyCorrection(3.33335m, -10m));
        // 1.00005 * 1.00 = 1.00005 -> 1.0001 (AwayFromZero, nicht Banker's Rounding)
        Assert.Equal(1.0001m, PriceMath.ApplyCorrection(1.00005m, 0m));
    }

    [Fact]
    public void ApplyCorrection_preserves_four_decimal_precision_for_small_values()
    {
        // BL liefert oft Preise wie 0.0234 EUR fuer Standard-Plates.
        // Mit -10% Korrektur: 0.0234 * 0.90 = 0.02106 -> 0.0211 (4 Stellen).
        // Vorher (2 Stellen): 0.0234 * 0.90 = 0.02106 -> 0.02 (Praezision weg).
        Assert.Equal(0.0211m, PriceMath.ApplyCorrection(0.0234m, -10m));
    }

    [Fact]
    public void ApplyCorrectionOrZero_returns_zero_for_null_input()
    {
        Assert.Equal(0m, PriceMath.ApplyCorrectionOrZero(0m, -10m));
    }

    [Fact]
    public void ApplyCorrectionOrZero_applies_factor_for_nonzero_input()
    {
        Assert.Equal(9.00m, PriceMath.ApplyCorrectionOrZero(10m, -10m));
    }

    // ===== Helpers =====

    private static PriceResult MakePrice(decimal? min, decimal? avg, decimal? qtyAvg, decimal? max)
        => new()
        {
            MinPrice = min,
            AvgPrice = avg,
            QtyAvgPrice = qtyAvg,
            MaxPrice = max,
            Currency = "EUR",
            FetchedAt = DateTime.UtcNow
        };
}
