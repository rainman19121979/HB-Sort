namespace HBSort.Core.Models;

/// <summary>
/// Alle konfigurierbaren Einstellungen der App.
/// Wird als JSON in %APPDATA%\HBSort\settings.json gespeichert.
/// </summary>
public class AppSettings
{
    /// <summary>Index der ausgewaehlten USB-Kamera (0 = erste Kamera)</summary>
    public int SelectedCameraIndex { get; set; } = 0;

    /// <summary>Gespeicherter Fensterzustand (Position, Groesse, Maximiert)</summary>
    public WindowState WindowState { get; set; } = new();

    /// <summary>
    /// Ab diesem Score wird ein Scan-Ergebnis automatisch akzeptiert (ohne User-Bestaetigung)
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
    /// UI-Darstellungsdichte (DEPRECATED ab UX-Iteration X.11, 2026-05-04).
    /// Frueher: "Compact" / "Normal" / "Comfortable". Jetzt sind die
    /// Compact-Werte fest in den Views verdrahtet. Feld bleibt aus
    /// Backwards-Compat in der settings.json - wird nirgends mehr
    /// gelesen. Kann in einer spaeteren Iteration entfernt werden.
    /// </summary>
    public string UiDensity { get; set; } = "Compact";

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

    /// <summary>
    /// UX-Iteration X.23: Auto-Update via Velopack + GitHub Releases.
    /// Wenn true: beim App-Start einmal pruefen ob ein neuer Release
    /// verfuegbar ist; bei Treffer wird das Update-Badge im Header
    /// angezeigt. Manuelles "Jetzt pruefen" funktioniert immer
    /// unabhaengig von dem Wert. (LastUpdateCheck-Zeitstempel wird
    /// weiterhin in der bestehenden LastUpdateCheck-Property oben
    /// gespeichert.)
    /// </summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    // ====================================================================
    // UX X.28 (v0.1.15): Auto-BL-Import
    // ====================================================================

    /// <summary>
    /// Wenn true: BrickStore-Inventory wird im Hintergrund automatisch alle
    /// AutoBlImportIntervalDays Tage aktualisiert. Default false - User muss
    /// explizit aktivieren in Settings -> BrickLink-Daten.
    /// </summary>
    public bool AutoBlImport { get; set; } = false;

    /// <summary>
    /// Intervall in Tagen zwischen automatischen BL-Imports. Default 30.
    /// UI bietet Auswahl 7/14/30/90 Tage.
    /// </summary>
    public int AutoBlImportIntervalDays { get; set; } = 30;

    /// <summary>
    /// Zeitstempel des letzten erfolgreichen BL-Imports (manuell oder Auto).
    /// Null = noch nie. UTC.
    /// </summary>
    public DateTime? LastBlImport { get; set; }

    // ====================================================================
    // UX X.29 (v0.1.16): Backup-System (Block A)
    // ====================================================================

    /// <summary>
    /// Wenn true: beim App-Start wird ein automatisches Backup erzeugt, sofern
    /// das letzte Backup laenger als AutoBackupIntervalDays her ist. Default
    /// true - Sicherheitsnetz fuer neue Installationen.
    /// </summary>
    public bool AutoBackup { get; set; } = true;

    /// <summary>
    /// Intervall in Tagen zwischen automatischen Backups. Default 1 = taeglich.
    /// UI bietet Dropdown 1/7/30 Tage (Taeglich/Woechentlich/Monatlich).
    /// </summary>
    public int AutoBackupIntervalDays { get; set; } = 1;

    /// <summary>
    /// Wieviele Backups maximal aufgehoben werden - aelteste werden beim
    /// Cleanup geloescht. Default 7.
    /// </summary>
    public int BackupKeepCount { get; set; } = 7;

    /// <summary>
    /// Zeitstempel des letzten erfolgreichen Backups (manuell oder Auto).
    /// Null = noch nie. UTC.
    /// </summary>
    public DateTime? LastBackup { get; set; }
}

/// <summary>
/// Speichert Position und Groesse des Hauptfensters,
/// damit beim naechsten Start alles so ist wie beim letzten Mal.
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
    /// <remarks>
    /// DEPRECATED ab UX X.19 Teil 3b: das alte 2x2-Layout hatte EIN
    /// gemeinsames oben/unten-Verhaeltnis. Jetzt hat jede der drei Spalten
    /// ihr eigenes (siehe Column1HorizontalSplitterRatio etc.). Wird beim
    /// Laden ignoriert; alte settings.json laedt trotzdem sauber, weil
    /// System.Text.Json unbekannte oder ueberzaehlige Properties uebergeht.
    /// </remarks>
    public double SplitterRowRatio { get; set; } = 0.6;

    /// <summary>
    /// UX X.19 Teil 3b: Anteil der oberen Box (Webcam) in Spalte 1.
    /// Wert 0..1. Default 0.65 entspricht dem bisherigen 65/35-Layout.
    /// </summary>
    public double Column1HorizontalSplitterRatio { get; set; } = 0.65;

    /// <summary>
    /// UX X.19 Teil 3b: Anteil der oberen Box (MinifigDetail/PartLookup)
    /// in Spalte 2. Default 0.65.
    /// </summary>
    public double Column2HorizontalSplitterRatio { get; set; } = 0.65;

    /// <summary>
    /// UX X.19 Teil 3b: Anteil der oberen Box (BuildSuggestions) in Spalte 3.
    /// Default 0.65.
    /// </summary>
    public double Column3HorizontalSplitterRatio { get; set; } = 0.65;
}
