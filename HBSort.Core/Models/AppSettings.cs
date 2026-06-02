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
    /// UX X.29 Block G (v0.1.16): Default-Auswahl beim Scannen einer Figur.
    /// false = nichts vorab abgehakt (Default - "ich klicke an was ich habe")
    /// true  = alles vorab abgehakt ("ich klicke ab was fehlt")
    /// User kann pro Figur immer noch manuell anpassen.
    /// </summary>
    public bool DefaultPartsCollected { get; set; } = false;

    /// <summary>
    /// UX X.31 (v0.1.18): Maximale Anzahl Complete-Figuren die sich ein Lagerfach
    /// teilen duerfen. Wenn dieses Limit erreicht ist, schlaegt
    /// <see cref="IStorageBinService.SuggestBinForCompleteMinifigAsync"/> ein neues
    /// freies Fach vor statt weiter ins gleiche Fach zu stapeln.
    /// Default 5. Min 1, Max 999 (UI-seitig validiert).
    /// </summary>
    public int MaxCompleteFiguresPerBin { get; set; } = 5;

    /// <summary>
    /// UX X.32 v0.1.19-beta.4: Maximale Anzahl wartender Figuren pro Lagerfach.
    /// Default 3. Bei 1 bekommt jede wartende Figur ein eigenes Fach
    /// (UX-X.6-konform / strikte Trennung). Bei &gt;1 koennen mehrere wartende
    /// Figuren ein Fach teilen - sinnvoll wenn man mehrere Figuren parallel
    /// sammelt und Platz sparen will. Min 1, Max 999 (UI-seitig validiert).
    /// </summary>
    public int MaxWaitingFiguresPerBin { get; set; } = 3;

    // UX X.33 v0.1.19-beta.7 Block M: MaxFloatingPartTypesPerBin entfernt -
    // wurde durch das CategoryToBinMapping (siehe unten) abgeloest. Mapping
    // ist deterministisch, deshalb braucht es keine Mix-Limit-Heuristik mehr.
    // Alte settings.json mit "maxFloatingPartTypesPerBin"-Eintrag wird von
    // System.Text.Json ignoriert.

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block M: User-pflegbares Mapping einer
    /// Brickognize-Kategorie (z.B. "Minifigure, Head") auf ein bestimmtes
    /// Lagerfach (BinId). Beim Einzelteil-Scan wird die von Brickognize
    /// gelieferte Kategorie hier nachgeschlagen - bei Treffer ist das Bin
    /// das Default. Bei keinem Treffer faellt der Pfad auf den Suggest-
    /// Service zurueck.
    ///
    /// Der Key ist der unveraenderte Brickognize-String (z.B.
    /// "Minifigure, Head" oder "Minifigure, Torso Assembly, Decor.").
    /// Settings-UI sammelt bisher gesehene Strings via
    /// <see cref="SeenBrickognizeCategories"/> und bietet sie als Liste an.
    /// </summary>
    public Dictionary<string, int> CategoryToBinMapping { get; set; } = new();

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block M: Liste der Brickognize-Kategorien,
    /// die im Lauf der Zeit beim Scannen gesehen wurden. Wird beim Settings-
    /// Dialog als Vorlage angeboten (User mappt jeden Eintrag auf ein Bin).
    /// Wird beim Scannen automatisch gepflegt - kein manuelles Eintragen.
    /// </summary>
    public List<string> SeenBrickognizeCategories { get; set; } = new();

    /// <summary>
    /// UX-Iteration X.9: Tooltips global an/ausschalten. Der TooltipsService
    /// schreibt diesen Wert in eine Application-Resource, die als
    /// DynamicResource an ToolTipService.IsEnabled der Wurzel-Windows gebunden
    /// ist - dadurch wirkt das Toggle live ohne Neustart und ohne dass jeder
    /// einzelne Tooltip-Konsument davon wissen muss. Default: an.
    /// </summary>
    public bool ShowTooltips { get; set; } = true;

    /// <summary>
    /// UX X.34 v0.1.20-beta.2: First-Run-Onboarding-Dialog beim App-Start
    /// zeigen wenn das Setup nicht komplett ist (BL-Catalog fehlt und/oder
    /// keine Lagerfaecher angelegt). Default true. Wird durch User-Interaktion
    /// im Dialog ("Loslegen"-Button nach erfolgreichem Setup) auf false
    /// gesetzt - sobald Setup komplett ist, wird der Check beim Start ohnehin
    /// 'Complete' liefern und der Dialog erscheint nicht mehr.
    /// </summary>
    public bool ShowFirstRunDialog { get; set; } = true;

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

    // UX X.33 v0.1.19-beta.7: UiDensity (DEPRECATED seit UX X.11) entfernt -
    // alte settings.json mit "uiDensity"-Eintrag wird von System.Text.Json
    // ignoriert.
    // UX X.33 v0.1.19-beta.7: PriceToolUrl (Phase-8-Stub, nie produktiv
    // genutzt) entfernt - das Feld wurde nirgends im UI gebunden und nicht
    // zurueckgeschrieben. Alte settings.json wird beim Laden ignoriert.

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
    /// "Erfolgreich" hier = Daten wurden tatsaechlich aktualisiert. Bei
    /// HTTP-304/Hash-unveraendert wird stattdessen <see cref="LastBlImportCheck"/>
    /// gesetzt. Null = noch nie. UTC.
    /// </summary>
    public DateTime? LastBlImport { get; set; }

    /// <summary>
    /// UX X.29 (v0.1.16): Zeitstempel der letzten Pruefung (auch wenn der
    /// Inhalt unveraendert war und der Import uebersprungen wurde). Wird bei
    /// jedem Auto-Import-Lauf gesetzt - User sieht in Settings dass die
    /// Pruefung regelmaessig laeuft, auch wenn LastBlImport laenger her ist.
    /// </summary>
    public DateTime? LastBlImportCheck { get; set; }

    /// <summary>
    /// UX X.29 (v0.1.16): ETag aus dem letzten erfolgreichen ZIP-Download.
    /// Wird beim naechsten Import als If-None-Match-Header geschickt -
    /// GitHub liefert dann HTTP 304 zurueck wenn das ZIP unveraendert ist.
    /// </summary>
    public string? LastBlImportEtag { get; set; }

    /// <summary>
    /// UX X.29 (v0.1.16): SHA256-Hash des ZIP-Inhalts beim letzten erfolgreichen
    /// Import. Wird verglichen wenn der Server keinen ETag liefert oder der
    /// Etag-Cache verloren geht. Bei gleichem Hash wird die Verarbeitung
    /// uebersprungen.
    /// </summary>
    public string? LastBlImportContentHash { get; set; }

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

    /// <summary>
    /// Gemerktes Spalten-Layout (Breiten + Sortierung) der DataGrid im
    /// Tab "Temporaeres Inventar" (InventoryListView). Wird beim Schliessen
    /// des Views / App-Ende geschrieben und beim Oeffnen wieder angewendet.
    /// Default = leeres Layout (greift dann auf die XAML-Defaults zurueck).
    /// </summary>
    public InventoryGridLayout InventoryGridLayout { get; set; } = new();
}

/// <summary>
/// Persistiertes Spalten-Layout fuer die Lagerliste-DataGrid.
///
/// Bewusst KEINE DB-Tabelle (gehoert in settings.json, nicht in userdata.db):
/// es ist reine UI-Praeferenz, keine Nutzdaten - daher keine EF-Migration.
///
/// Identifikation der Spalten ueber den Header-Text (stabiler String wie
/// "Beschreibung" oder "Farbe"). Wenn sich das Spalten-Layout im Code aendert
/// und eine gespeicherte Spalte nicht mehr existiert, wird sie beim Anwenden
/// einfach ignoriert (kein Crash, siehe InventoryListView-Code-Behind).
/// </summary>
public class InventoryGridLayout
{
    /// <summary>
    /// Gespeicherte Breiten pro Spalte. Key = Header-Text, Value = Breite in
    /// Pixeln. Star-Spalten (z.B. "Beschreibung", Width="*") werden NICHT
    /// gespeichert (siehe Code-Behind) - sie sollen weiter mitwachsen.
    /// </summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = new();

    /// <summary>
    /// Header-Text der aktuell sortierten Spalte. Null/leer = keine aktive
    /// Sortierung gemerkt (DataGrid bleibt unsortiert wie im XAML-Default).
    /// </summary>
    public string? SortColumnHeader { get; set; }

    /// <summary>
    /// Richtung der gemerkten Sortierung. true = aufsteigend (Ascending),
    /// false = absteigend (Descending). Nur relevant wenn
    /// <see cref="SortColumnHeader"/> gesetzt ist.
    /// </summary>
    public bool SortAscending { get; set; } = true;
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

    // UX X.33 v0.1.19-beta.7: SplitterRowRatio (DEPRECATED seit UX X.19
    // Teil 3b - altes 2x2-Layout) entfernt. Pro-Spalten-Verhaeltnisse
    // leben in Column[1-3]HorizontalSplitterRatio. Alte settings.json
    // mit "splitterRowRatio"-Eintrag wird von System.Text.Json ignoriert.

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
