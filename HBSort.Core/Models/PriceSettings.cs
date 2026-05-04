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

    // Audit W-8 (2026-05-04): Frueher gab es hier ein einzelnes CacheDays-
    // Feld (Default 7). Mit Phase 8 wurden zwei dedizierte TTL-Felder
    // eingefuehrt (s.u.); im Audit X.16 wurde der BricklinkApiPriceProvider
    // auf die neuen Felder umgestellt und das alte CacheDays entfernt.
    // System.Text.Json ignoriert unbekannte Properties beim Deserialize -
    // alte settings.json mit "CacheDays": 7 wird also ohne Crash geladen,
    // der Wert verfaellt einfach.

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

    /// <summary>
    /// Beim Oeffnen einer kompletten Figur Preise sofort laden (sonst Button).
    /// </summary>
    /// <remarks>
    /// DEPRECATED ab UX-Iteration X.10 (2026-05-04): wird durch
    /// <see cref="AutoLoadCompletePrice"/> ersetzt. Feld bleibt aus
    /// Backwards-Compat-Gruenden in der settings.json erhalten, wird aber
    /// nirgends mehr gelesen. Kann in einer spaeteren Iteration entfernt
    /// werden.
    /// </remarks>
    public bool AutoLoadOnComplete { get; set; } = true;

    /// <summary>
    /// UX-Iteration X.10 (2026-05-04): wann der Komplett-Figur-Preis im
    /// Sortier-Tab (MinifigPriceView) UND im MinifigSummaryDialog geladen
    /// werden soll. Auto = sofort beim Erscheinen, Manual = erst auf Klick
    /// (spart API-Calls). Default: Manual.
    /// </summary>
    public PriceLoadMode AutoLoadCompletePrice { get; set; } = PriceLoadMode.Manual;

    /// <summary>
    /// UX-Iteration X.10: dito fuer die Einzelteile-Summe. Default: Manual.
    /// Unabhaengig von <see cref="AutoLoadCompletePrice"/> - der User kann
    /// z.B. den Komplett-Preis auto laden lassen und die Teile nur auf Klick.
    /// </summary>
    public PriceLoadMode AutoLoadPartsPrice { get; set; } = PriceLoadMode.Manual;
}

/// <summary>
/// UX-Iteration X.10: wann ein Preis-Bereich in der MinifigPriceView geladen
/// werden soll. Auto = automatisch beim Erscheinen einer Pending-Minifig,
/// Manual = erst auf User-Klick auf einen "Preis laden"-Button.
/// </summary>
public enum PriceLoadMode
{
    /// <summary>Erst auf Klick laden. Spart API-Calls (Default).</summary>
    Manual,
    /// <summary>Sofort laden sobald der Bereich angezeigt wird.</summary>
    Auto
}
