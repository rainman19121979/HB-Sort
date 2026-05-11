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

    /// <summary>
    /// Region-Filter (BL: "europe", "north_america", "asia"). Leer = global.
    /// UX X.34 v0.1.20: Default jetzt leer (vorher "europe"). Either-Or
    /// mit <see cref="CountryCode"/> - wenn beide gesetzt sind, gewinnt
    /// CountryCode im Provider und Region wird ignoriert. Settings-Migration
    /// im SettingsService normalisiert alte "beide gesetzt"-Konfiguration.
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// ISO-Country-Code (z.B. "DE"). Leer = kein Land-Filter.
    /// UX X.34 v0.1.20: Default jetzt leer (vorher "DE"). Siehe Region.
    /// </summary>
    public string CountryCode { get; set; } = string.Empty;

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

    // UX X.33 v0.1.19-beta.7: AutoLoadOnComplete (DEPRECATED seit UX X.10,
    // ersetzt durch AutoLoadCompletePrice) entfernt. Wurde nicht mehr im
    // UI gebunden und nicht mehr von Konsumenten gelesen. Alte settings.json
    // mit "autoLoadOnComplete"-Eintrag wird von System.Text.Json ignoriert.

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

    /// <summary>
    /// UX X.34 v0.1.20: VAT-Behandlung beim BL-Preis-Lookup.
    ///
    /// <list type="bullet">
    /// <item><b>Y</b> (Default) = Brutto - matcht die Preise auf der BrickLink-
    /// Webseite. BL gibt VAT-Verkaeufer-Preise inkl. VAT aus, Privat-
    /// Verkaeufer-Preise unveraendert. Konsistente Aggregate fuer
    /// Markt-Vergleich.</item>
    /// <item><b>N</b> = Netto - VAT-Verkaeufer-Preise ohne VAT, Privat-Preise
    /// unveraendert. Gemischte Aggregate (Vorsicht). Nur sinnvoll fuer
    /// eigene VAT-Verkaeufer-Erloesabschaetzung.</item>
    /// <item><b>O</b> = Norwegen-Spezial (25% VAT, sehr enges Anwendungsfeld).</item>
    /// </list>
    ///
    /// Default Y weil 95% der User Brutto/Website-Preise wollen. Vorher
    /// (vor v0.1.20) war kein vat-Parameter gesetzt - BL-Default ist
    /// Netto, was gemischte Aggregate erzeugt hat (Privat brutto, gewerblich
    /// netto). Verkaufsempfehlungen waren irrefuehrend.
    /// </summary>
    public VatMode VatMode { get; set; } = VatMode.Y;
}

/// <summary>
/// UX X.34 v0.1.20: VAT-Behandlung bei BL-Preis-Lookups.
/// Mappt 1:1 auf BricklinkSharp.Client.VatOption (Include/Exclude/IncludeAsNorway),
/// aber bewusst eigenes Enum damit PriceSettings unabhaengig vom Library-
/// Detail bleibt. Mapping liegt im BricklinkApiPriceProvider.
/// </summary>
public enum VatMode
{
    /// <summary>Brutto - VAT eingerechnet, matcht Website-Preise (Default).</summary>
    Y,
    /// <summary>Netto - VAT abgezogen, gemischte Aggregate. Nur fuer VAT-Verkaeufer-Erloese.</summary>
    N,
    /// <summary>Norwegen 25% VAT - Spezial-Fall.</summary>
    O
}

/// <summary>
/// UX X.34 v0.1.20: Either-Or-Filter fuer BL-Preis-Lookups. Reine UI-
/// Konvention - das POCO speichert nur Region und CountryCode (jeweils
/// String, leer = nicht gesetzt). Der Filter-Modus wird beim Settings-Load
/// aus dem Stand der zwei Felder abgeleitet, beim Save wird je nach Modus
/// einer der beiden Werte auf "" gezwungen.
/// </summary>
public enum PriceFilterMode
{
    /// <summary>Kein geografischer Filter - groesste Datenbasis (Default).</summary>
    Global,
    /// <summary>Filter auf eine Region (europe / north_america / asia).</summary>
    Region,
    /// <summary>Filter auf ein spezifisches Land (DE / US / GB / ...).</summary>
    Country
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
