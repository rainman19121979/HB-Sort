namespace HBSort.Core.Models;

/// <summary>
/// Phase 8: Konfiguration fuer das Preis-Lookup-System.
/// Wird unter AppSettings.Prices in der settings.json persistiert.
/// </summary>
public class PriceSettings
{
    /// <summary>
    /// Aktiver Preis-Provider:
    ///   "None"         - keine Preisanzeige (Default)
    ///   "BricklinkApi" - GetPriceGuide via BricklinkSharp (benoetigt BL-Tokens)
    /// Spaeter ggf. "HbPriceTool" wenn das eigene Tool angebunden ist.
    /// </summary>
    public string Provider { get; set; } = "None";

    /// <summary>"sold" = Sold-Listings (was wirklich verkauft wurde), "stock" = Aktuelle Angebote.</summary>
    public string GuideType { get; set; } = "sold";

    /// <summary>"min" | "avg" | "qty_avg" | "max" - welche Spalte angezeigt/zugrunde gelegt wird.</summary>
    public string PriceColumn { get; set; } = "qty_avg";

    /// <summary>Region-Filter (BL: "europe", "north_america", "asia"). Leer = global.</summary>
    public string Region { get; set; } = "europe";

    /// <summary>ISO-Country-Code (z.B. "DE"). Leer = kein Filter.</summary>
    public string CountryCode { get; set; } = "DE";

    /// <summary>Waehrung (z.B. "EUR", "USD").</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// Korrekturfaktor in Prozent fuer Komplett-Figur. -10 = 10% Abschlag.
    /// </summary>
    public decimal CorrectionMinifigPercent { get; set; } = -10m;

    /// <summary>
    /// Korrekturfaktor in Prozent fuer Einzelteile-Summe. -15 = 15% Abschlag.
    /// </summary>
    public decimal CorrectionPartsPercent { get; set; } = -15m;

    /// <summary>Cache-Lebensdauer in Tagen. Aeltere Eintraege werden neu geholt.</summary>
    public int CacheDays { get; set; } = 7;

    /// <summary>Beim Oeffnen einer kompletten Figur Preise sofort laden (sonst Button).</summary>
    public bool AutoLoadOnComplete { get; set; } = true;
}
