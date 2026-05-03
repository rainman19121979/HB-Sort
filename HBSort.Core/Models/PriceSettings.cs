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
    // DEPRECATED ab Phase 8 - wird vom Stale-While-Revalidate-Pfad
    // (IBlPriceCacheService) nicht mehr genutzt. Stattdessen die zwei
    // dedizierten TTL-Felder unten verwenden. Kann in einer spaeteren
    // Iteration entfernt werden.
    public int CacheDays { get; set; } = 7;

    /// <summary>
    /// Phase 8 / UX#12: TTL fuer Komplett-Figur-Cache-Eintraege in Tagen.
    /// Default 90 (= 3 Monate) - BL-Preise aendern sich langsam.
    /// Bei abgelaufener TTL liefert der Cache-Service Stale-Werte sofort
    /// und triggert im Hintergrund eine Revalidation.
    /// </summary>
    public int BlPriceCacheTtlMinifigDays { get; set; } = 90;

    /// <summary>
    /// Phase 8 / UX#12: TTL fuer Einzelteil-Cache-Eintraege in Tagen.
    /// Default 90. Getrennt vom Minifig-TTL weil Teile-Preise potenziell
    /// volatiler sein koennen (Sets-Releases, Saison-Effekte).
    /// </summary>
    public int BlPriceCacheTtlPartDays { get; set; } = 90;

    /// <summary>Beim Oeffnen einer kompletten Figur Preise sofort laden (sonst Button).</summary>
    public bool AutoLoadOnComplete { get; set; } = true;
}
