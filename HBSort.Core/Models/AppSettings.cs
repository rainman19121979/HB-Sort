namespace HBSort.Core.Models;

/// <summary>
/// Alle konfigurierbaren Einstellungen der App.
/// Wird als JSON in %APPDATA%\HBSort\settings.json gespeichert.
/// </summary>
public class AppSettings
{
    /// <summary>Index der ausgewählten USB-Kamera (0 = erste Kamera)</summary>
    public int SelectedCameraIndex { get; set; } = 0;

    /// <summary>Gespeicherter Fensterzustand (Position, Größe, Maximiert)</summary>
    public WindowState WindowState { get; set; } = new();

    /// <summary>
    /// Ab diesem Score wird ein Scan-Ergebnis automatisch akzeptiert (ohne User-Bestätigung)
    /// und ein BL-Lookup wird sofort ausgeloest. Empfohlen: 0.95 (sehr sicher), damit nur
    /// klare Treffer ohne Nachfrage gehen. Bei niedrigeren Scores klickt der User die
    /// passende Karte aus den Top-3 manuell an, was den BL-Call dann gezielt ausloest.
    /// </summary>
    public double ScoreThresholdAuto { get; set; } = 0.95;

    /// <summary>
    /// Minimaler Score, unter dem das Ergebnis als "nicht erkannt" gilt.
    /// Standard: 0.5 = 50% Konfidenz
    /// </summary>
    public double ScoreThresholdMin { get; set; } = 0.5;

    /// <summary>
    /// Ab diesem Score wird immer die Auswahl-UI gezeigt (auch bei nur 1 Treffer).
    /// Standard: 0.7 = 70% Konfidenz
    /// </summary>
    public double ScoreThresholdShowSelection { get; set; } = 0.7;

    /// <summary>Pause zwischen zwei Scans in Millisekunden (verhindert Doppelscans)</summary>
    public int ScanCooldownMs { get; set; } = 1000;

    /// <summary>Wie lange das Bild nach dem Scan eingefroren wird (in Millisekunden)</summary>
    public int FreezeFrameMs { get; set; } = 1000;

    /// <summary>Sound-Effekte an/aus (Default: aus)</summary>
    public bool SoundEnabled { get; set; } = false;

    /// <summary>
    /// Bild-Cache-Einstellungen (Limit, BL-Bevorzugung, Vorab-Cache).
    /// Subobjekt damit cache-relevante Felder gruppiert in der settings.json stehen.
    /// </summary>
    public ImageCacheSettings ImageCache { get; set; } = new();

    /// <summary>
    /// BrickLink-API-Konfiguration (Tokens + Rate-Limit-Schwelle + Cache-Lifespan).
    /// Tokens selbst sind DPAPI-verschluesselt (siehe BricklinkSettings.TokensEncrypted).
    /// </summary>
    public BricklinkSettings Bricklink { get; set; } = new();

    /// <summary>Wie oft Minifigur-Bilder aus dem Cache refreshed werden (in Tagen)</summary>
    public int ImageCacheRefreshDays { get; set; } = 30;

    /// <summary>
    /// UI-Darstellungsdichte: "Compact" / "Normal" / "Comfortable".
    /// Wird beim App-Start vom UiDensityService geladen und die entsprechenden
    /// Resource-Dictionaries angewendet. Default: Normal.
    /// String statt Enum damit settings.json menschlich lesbar bleibt.
    /// </summary>
    public string UiDensity { get; set; } = "Normal";

    /// <summary>URL zum BrickLink-Preis-Tool (Phase 8+, leer = nicht konfiguriert)</summary>
    public string PriceToolUrl { get; set; } = string.Empty;

    /// <summary>Zeitpunkt des letzten Update-Checks (max. 1x pro Tag)</summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>Liste der zuletzt importierten Katalog-ZIP-Pfade (max. 7)</summary>
    public List<string> RecentCatalogZips { get; set; } = [];
}

/// <summary>
/// Speichert Position und Größe des Hauptfensters,
/// damit beim nächsten Start alles so ist wie beim letzten Mal.
/// </summary>
public class WindowState
{
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 800;
    public double X { get; set; } = 0;
    public double Y { get; set; } = 0;
    public bool IsMaximized { get; set; } = true;

    /// <summary>Anteil der linken Spalte am 2x2-Layout (0..1). Default 0.5.</summary>
    public double SplitterColumnRatio { get; set; } = 0.5;

    /// <summary>Anteil der oberen Zeile am 2x2-Layout (0..1). Default 0.6.</summary>
    public double SplitterRowRatio { get; set; } = 0.6;
}
