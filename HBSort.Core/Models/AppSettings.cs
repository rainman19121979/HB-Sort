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
    /// UX-Iteration X.9: Tooltips global an/ausschalten. Der TooltipsService
    /// schreibt diesen Wert in eine Application-Resource, die als
    /// DynamicResource an ToolTipService.IsEnabled der Wurzel-Windows gebunden
    /// ist - dadurch wirkt das Toggle live ohne Neustart und ohne dass jeder
    /// einzelne Tooltip-Konsument davon wissen muss. Default: an.
    /// </summary>
    public bool ShowTooltips { get; set; } = true;

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

    /// <summary>
    /// Phase 7: Default-Ordner fuer den BSX-Export. Null = Documents/HBSort-Export/.
    /// Wird beim ersten Export auf den vom User gewaehlten Ordner gesetzt und
    /// danach als Default fuer kuenftige Exporte vorgeschlagen.
    /// </summary>
    public string? BsxExportFolder { get; set; }

    /// <summary>
    /// Default-Ordner fuer den BL-Wanted-List-Export. Null = wird auf
    /// BsxExportFolder zurueckgefallen (oder dessen Default, falls auch null).
    /// Eigenes Feld weil "Mein Inventar exportieren" und "Was brauche ich noch
    /// kaufen" konzeptionell verschiedene Workflows sind.
    /// </summary>
    public string? WantedListExportFolder { get; set; }

    /// <summary>
    /// Zuletzt gewaehlter Tab im variablen Feld unten rechts (R2,C2)
    /// im Sortier-Tab. 0=Lagerfaecher, 1=Was kann ich bauen?, 2=Live-Stats,
    /// 3=Wartende-Detail, 4=Letzte Scans. Null = noch nie gesetzt -> Default 0.
    /// </summary>
    public int? BottomRightTabIndex { get; set; }

    /// <summary>
    /// Phase 8: Preis-Lookup-Konfiguration (Provider, Korrekturen, Cache).
    /// Default: Provider=None -> keine Preise sichtbar.
    /// </summary>
    public PriceSettings Prices { get; set; } = new();
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

    /// <summary>Anteil der ERSTEN Spalte (Webcam) am 3-Spalten-Layout (0..1). Default 0.333.</summary>
    public double SplitterColumnRatio { get; set; } = 1.0 / 3.0;

    /// <summary>
    /// Anteil der ZWEITEN Spalte (MinifigDetail/PartLookup) am 3-Spalten-Layout (0..1).
    /// Default 0.333. Die dritte Spalte ergibt sich automatisch als
    /// (1 - SplitterColumnRatio - SplitterColumnRatio2).
    /// (UX-Iteration X.4: Sortier-Tab ist von 2 auf 3 gleich breite Spalten umgebaut.)
    /// </summary>
    public double SplitterColumnRatio2 { get; set; } = 1.0 / 3.0;

    /// <summary>Anteil der oberen Zeile am 2x2-Layout (0..1). Default 0.6.</summary>
    public double SplitterRowRatio { get; set; } = 0.6;
}
