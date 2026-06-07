# HB-Sort

Windows-Desktop-App zum Sortieren von Klemmbaustein-Minifiguren und
Einzelteilen. HB-Sort erkennt Teile per USB-Webcam, ordnet sie wartenden
Figuren zu, verwaltet Lagerfaecher und unterstuetzt den Verkauf via
BrickStore- und BrickLink-Export.

> **Disclaimer:** LEGO® ist eine Marke der LEGO-Gruppe, die HB-Sort
> weder sponsert noch unterstuetzt oder anerkennt. HB-Sort ist ein
> unabhaengiges, kostenloses Werkzeug ohne offizielle Verbindung zur
> LEGO-Gruppe.

![HB-Sort beim Sortieren: Webcam-Erkennung, Pending-Teile mit Auto-Markierung, Reverse-Match-Vorschlaege und BL-Preise](docs/screenshots/01-sortieren-erkennung.png)

## Features

- **Webcam-Erkennung** ueber Brickognize-API: Minifiguren und Einzelteile
  inklusive Farbe.
- **Reverse-Match**: gescannte Einzelteile finden automatisch passende
  wartende Figuren; bei vollstaendigem Match wird die Figur als komplett
  markiert.
- **Lagerfaecher-Verwaltung** mit Smart-Storage-Suggestions: gleiches
  Teil + gleiche Farbe wird automatisch ins bestehende Fach gestapelt.
- **"Was kann ich bauen?"-Live-Vorschau** auf Basis deiner gelagerten
  Einzelteile.
- **BrickLink-Preise** (optional, eigener BL-Consumer-Account noetig)
  mit Cache + Stale-While-Revalidate, getrennten TTLs fuer Minifigs/Teile,
  Korrekturfaktoren fuer Verkaufsgebuehren.
- **BSX-Export** fuer BrickStore: komplette Figuren und Einzelteile in
  einer Datei, mit Lagerfach-Information im internen `<Remarks>`-Feld
  und User-Notizen im oeffentlichen `<Comments>`-Feld.
- **Wanted-List-Export** fuer BrickLink (XML pro Figur oder eine grosse
  Datei mit allen fehlenden Teilen).
- **Volle Tastatur-Bedienung** (Leertaste = scannen, Strg+S/L/H =
  Tab-Wechsel, F1 = Hilfe, etc.).
- **Integrierte Hilfe** (F1) mit Markdown-Inhalten.
- **Theming** folgt dem Windows-System-Theme (hell/dunkel).
- **Auto-Update** ueber GitHub Releases (Settings → Updates) - nur bei
  Setup-Installation, Portable-ZIP zeigt den Tab deaktiviert.
- **Auto-BL-Import** (ab v0.1.15, optional) - aktualisiert die BrickLink-
  Stammdaten alle 7/14/30/90 Tage im Hintergrund.
- **Backup-System** (ab v0.1.16) - automatische taegliche Backups deiner
  Daten in `%APPDATA%\HBSort\backups\`. Restore mit Pre-Restore-Backup
  als Sicherheitsnetz, Auto-Neustart der App nach Wiederherstellung.
- **Verlauf + Rueckgaengig** (ab v0.1.16) - Tab "Verlauf" zeigt alle
  Aktionen (Loeschen, Verschieben, Komplettieren, Fach-Aenderungen).
  Strg+Z macht die letzte Aktion rueckgaengig.
- **Bulk-Operationen** (ab v0.1.16) - mehrere Items im Temporären
  Inventar markieren und gemeinsam loeschen oder verschieben (mit Undo-
  Support). Doppelklick auf Zeile oeffnet Details, Entf-Taste loescht
  Selektion.
- **BrickLink-Inventar-Integration** (ab v0.1.24, optional) - dein
  kompletter BL-Store-Bestand wird lokal gespiegelt (Sync per Knopf-
  druck, ein API-Call). Eigener Tab "BrickLink Inventar" mit Filter,
  Suche und Detail-Panel. Beim Sammeln einer wartenden Figur zeigt ein
  blauer **BL-Shop**-Badge welche fehlenden Teile du noch in deinem
  Shop hast - per Klick einzelne Lots reservieren (mit Shop-Position).
  Der Baubar-Tab kann zusaetzlich Figuren vorschlagen die nur mit
  BL-Ergaenzung komplett baubar waeren. Reservierte Teile lassen sich
  per **Mass-Update-Export** wieder aus dem Shop ausbuchen (XML, Upload
  bei BrickLink, dann Verifizieren in HB-Sort).

## Was ist neu in v0.1.25

Mehrere Performance- und Komfort-Iterationen, plus neue Anzeige-
Features rund um Combined Parts und den Baubar-Tab.

### Spürbar flüssigere App

- **Einstellungen öffnen sofort** statt nach ~1 Sekunde Verzögerung.
- **Sortieren bleibt zäh-frei über längere Sessions** — vorher wurde
  die App mit steigender Scan-Zahl spuerbar langsamer (vor allem im
  Temporaeren Inventar und Sortier-Tab). Wurzel: ein Cache-Statistik-
  Event, das pro Bild-Cache-Hit gefeuert hat — jetzt nur noch bei
  echten Mengen-Aenderungen, plus Throttle als Sicherheitsnetz.
- **Schnelleres Laden der Scan-Historie** durch einen Datenbank-Index
  auf der ScanEvents-Tabelle. Bei wachsender Historie spuerbar.

### Baubar-Tab: 100%-BL-Vorschläge erscheinen jetzt

Im Tab "Was kann ich bauen?" erscheinen jetzt auch Figuren die
**ausschliesslich aus deinem BL-Shop-Inventar** baubar wären —
auch wenn du gar keine passenden losen Teile im Lager hast. Aktiviert
sich über den bestehenden Toggle **"BL-Inventar beruecksichtigen"**.
Vorher fielen solche "BL-only"-Vorschläge stillschweigend raus, weil
der Filter mindestens ein Teil im HBSort-Lager verlangt hat.

Nebenbei wurden drei Skalierungs-Probleme im Baubar-Tab gefixt, die
bei wachsendem BL-Inventar auftraten (Crash bei sehr grossen Pools,
langsamer Query-Plan, vielfache Einzel-Datenbankabfragen). Der Tab
lädt jetzt auch mit ~9000 Lots im BL-Inventar in unter 1 Sekunde.

### Torso-Komponenten beim Einzelteil-Scan

Beim Scan eines montierten Torsos (oder eines anderen "Combined
Part" wie Wheels+Reifen, Turntables, Tier+Zubehör) zeigt HB-Sort
jetzt automatisch einen aufgeklappten Bereich **"Komponenten dieses
Teils"** im Scan-Result:

- Grund-Teil mit Badge "Grund-Teil"
- Alle Sub-Teile (Arme, Hände, ...) mit Bild, Farb-Swatch und Anzahl
- Bilder klickbar zum Vergroessern

Hilfreich beim Sortieren wenn du sehen willst was alles zu einem
montierten Teil gehört. Reine Anzeige aus dem lokalen Cache, keine
zusätzlichen BL-API-Aufrufe. Funktioniert generisch für alle
Combined Parts, nicht nur Torsos. Bei atomaren Teilen (ein normaler
2x4-Stein) erscheint der Bereich gar nicht.

### Dialog-Konsistenz

- **Footer-Buttons vereinheitlicht** in allen Dialogen: Abbrechen/
  Schliessen immer links, primärer Button rechts mit Akzent-Farbe.
- **Schliessen-Buttons** sind jetzt durchgaengig grau (vorher in drei
  Dialogen fälschlich mit Akzent-Farbe wie ein primärer Button).
- **BL-Shop-Badge-Beschriftung** in Bauvorschlag-Details vereinheitlicht
  (zeigt jetzt die konkrete Stueckzahl wie ueberall sonst).
- **Mehrere Tooltip- und Beschriftungs-Drifts** korrigiert.

### Spalten-Persistenz im Temporären Inventar

Sortierung und Spaltenbreiten bleiben jetzt zwischen App-Starts
erhalten. Vorher musste man die Spalten nach jedem Neustart neu
zurechtruecken.

### Bulk-Aktionen auf Lagerfächern

Im Einstellungs-Tab **Lagerfächer** kannst du jetzt markierte Fächer
sammelweise leeren oder löschen — mit Vorschau und Bestaetigungs-
Dialog. Belegte Fächer (mit Figuren oder Floating-Parts drin) werden
beim Löschen automatisch übersprungen und im Ergebnis-Report
aufgeführt.

## Was ist neu in v0.1.24

Grosse Iteration in zwei Strecken: **SortInstruction-Konsolidierung**
und **BL-Inventar-Integration**.

### SortInstruction-Modal (beta.1-3)

- **Ein einheitliches Take/Put/Plus-Modal** fuer alle 6 Sortier-Triggers
  (Bulk-Move, DismantleWizard, Single-Move, StoreFloating, Reverse-Match-
  Konsum, Wizard-Stufe-2-Save). Vorher: drei Layout-Varianten je nach
  Pfad. Jetzt: konsistente "Nimm aus X, Lege in Y, Plus optionaler
  Hinweis"-Struktur ueberall.
- **Wizard 2-stufig** beim Anlegen einer Figur aus dem Collect-Pfad.
- **Combobox-Suffix mit Belegungs-Counts** ("Box 005 (3 wartende)").

### BL-Inventar

Das BrickLink-Store-Inventar wird komplett in HB-Sort integriert. In
mehreren Wellen ueber v0.1.24 entstanden — der Workflow ist jetzt
durchgaengig vom Sync bis zum Ausbuchen.

- **Sync-Infrastruktur**: dein kompletter BrickLink-Store-Bestand wird
  in einem API-Call lokal gespiegelt (Snapshot-Replace, deine
  Reservierungen bleiben erhalten). Sync-Knopf in den Einstellungen
  und im Inventar-Tab.
- **Tab "BrickLink Inventar"**: durchsuchbares DataGrid mit Filter
  (Typ, Zustand, Suche), Detail-Panel rechts mit Grossbild,
  Lazy-Thumbnails (max 3 parallele Downloads), Spalte
  **Shop-Position** (deine BrickLink-Remarks).
- **Komplettieren mit BL-Reservierung**: pro fehlendem Teil einer
  wartenden Figur zeigt ein blauer **BL-Shop**-Badge welche Lots du
  noch hast (mit Shop-Position) — Klick reserviert genau ein Lot.
- **Baubar-Tab — BL-Erweiterung**: Checkbox **"BL-Inventar
  beruecksichtigen"** zeigt zusaetzlich Figuren die nur mit
  BL-Ergaenzung komplett baubar waeren, mit Badge "X Teile aus Shop".
- **Sortier-Tab Toast**: gescanntes Teil keiner wartenden Figur
  zuordbar aber im BL-Shop verfuegbar → Hinweis-Toast.
- **Auto-Release bei Figur-Entfernung**: BL-Reservierungen werden bei
  Figur-Loeschen/Zerlegen/Cleanup automatisch freigegeben — keine
  Geist-Reservierungen im Inventar-Tab.
- **Reservierungen einzeln verwalten**: im BL-Inventar-Detail-Panel
  pro Lot eine Sektion "Reservierungen" mit zugehoeriger Figur pro
  reserviertem Stueck und **"Aufheben"**-Knopf — einzelne Eintraege
  freigeben statt alles auf einmal.
- **Mass-Update-Export**: Knopf **"BL aktualisieren"** im
  Inventar-Tab. Erzeugt ein BrickLink-Mass-Update-XML aus deinen
  Reservierungen, beim Oeffnen wird automatisch zuerst synchronisiert
  damit das XML gegen aktuelle Mengen gebaut wird. Ablauf:
  Zwischenablage → BrickLink Mass-Update einfuegen+ausfuehren →
  zurueck → **"Verifizieren"** wandelt die Reservierungen in fest
  gesammelt um.
- **Reserviertes Teil doch gescannt**: wenn du ein Teil das du im
  BL-Shop reserviert hattest spaeter physisch scannst, ersetzt das
  echte Teil die Reservierung automatisch — HB-Sort zeigt eine
  passende Anweisung (loses Teil zurueck in den Shop, oder Tausch
  Neu/Gebraucht).

### Baubar-Vorschlaege ausblenden

Bauvorschlaege die du dauerhaft nicht mehr sehen willst, blendest du
ueber das **X** am Vorschlag aus. Ueber den Link **"Ignorierte
verwalten"** unten in der Liste oeffnet sich ein Dialog mit allen
ausgeblendeten Figuren — einzeln oder gesammelt wiederherstellbar.

### Figur-Dialoge einheitlicher

Die Figur-Dialoge (Wartende-Detail, Komplettieren-Wizard,
Bauvorschlag-Detail) sind optisch auf einen einheitlichen Stil
gebracht (gleicher Header-Block, gleiche Teile-Zeile, gleiche
Footer-Buttons) — egal aus welchem Tab du eine Figur oeffnest, der
Dialog sieht gleich aus.

### Bin-Vorschlags-Bugfixes (beta.4 + beta.9)

- **Symmetrie-Vertrag** zwischen `SuggestBinForFloatingPartAsync` und
  der UI-Combobox: Floating-Parts werden nie mehr in Bins vorgeschlagen
  die in der Combobox gar nicht angezeigt sind. Behebt zwei "Kein Fach
  frei"-Bugs (Complete-Bin der excluded-Figur, Pure-Waiting-Bin).

## Was ist neu in v0.1.19

Grosse UX-Iteration mit Fokus auf Sortier-Logik und Konsistenz.

### Sortier-Logik & Bin-Vorschlaege

- **Brickognize-Kategorie-Sortierung**: Einzelteile werden nach
  Brickognize-Kategorie (z.B. "Minifigure, Head", "Minifigure, Headgear")
  vorgeschlagen. Pro Lagerfach maximal eine Teile-Nummer pro Kategorie -
  ein Helm-Fach wird nicht mit einem zweiten Helm-Typ vermischt,
  andere Kategorien (Kopf, Hose, ...) duerfen sich ein Fach teilen.
  Identische Teile (gleiche Nummer + Farbe) stapeln sich immer.
- **Neuer Tab "Kategorien"** in den Einstellungen: hier mappst du
  Brickognize-Kategorien manuell auf feste Lagerfaecher ("alle Helme
  in Box21-3"). Ohne Mapping greift die Standard-Regel oben.
- **Konfigurierbare Lagerfach-Limits** (Complete-Figuren, wartende
  Figuren) unter *Einstellungen > Erkennung > Sortier-Workflow*.
  Defaults: max 5 Complete bzw. 3 wartende pro Fach.
- **Volle-Faecher-Hinweis**: wenn alle Faecher belegt sind, erscheint
  in MinifigDetail / PartLookup / BuildSuggestion ein orangener
  Banner mit Direkt-Link in die Lagerfach-Verwaltung.

### Workflow-Komfort

- **Direkt-Zerlegen beim Scannen**: hast du eine Figur gescannt, aber
  nur einzelne Teile davon behalten willst? Klick auf "Direkt zerlegen"
  in der Pending-View. Die markierten Teile werden als Einzelteile in
  passende Faecher einsortiert, die Figur selbst wird verworfen.
- **Sammel-Popup statt Einzel-Popup**: bei mehreren Teilen auf einmal
  (Direkt-Zerlegen, Reverse-Match mit 2+ konsumierten Teilen,
  Bulk-Verschieben) zeigt HB-Sort eine Liste mit allen "Lege X in
  Box Y"-Anweisungen auf einen Blick - tab-uebergreifend sichtbar.
  Schliesst per Enter, Klick oder Esc.
- **Lagerfach-Vorschlag pro Teil im Zerlegen-Dialog**: jedes Teil
  bekommt ein eigenes Default-Fach - wenn dasselbe Teil schon irgendwo
  liegt, waechst der Stapel; sonst greift die Kategorie-Regel.
- **Auswahl-Dialog beim "Diese Figur anlegen"** (PartLookup-Pfad):
  pro Required-Part waehlbar ob Reverse-Match-Treffer aus dem Lager
  konsumiert wird.

### Wanted-List

- **BrickLink-Wanted-XML-Format** (bewusst kein BSX, da die BrickLink-
  Webseite nur ihr eigenes XML akzeptiert).
- **Neuer "In Zwischenablage"-Button** im Wanted-Export-Dialog: kopiert
  den XML-Code direkt in die Zwischenablage, du kannst ihn auf
  <https://www.bricklink.com/v2/wanted/upload.page> im "Paste XML"-Tab
  einfuegen ohne Datei-Upload.

## Was ist neu in v0.1.18

- **Konsistente Lagerfach-Vorschlaege**: wartende Figuren bekommen ein
  eigenes Fach, Einzelteile vom gleichen Typ landen immer im gleichen
  Fach, Complete-Figuren duerfen sich ein Fach teilen.
- **Maximum Complete-Figuren pro Fach** in den Einstellungen
  konfigurierbar (Default: 5).
- **Live-Anpassung des Lagerfachs**: waehrend du Teile markierst,
  wechselt das vorgeschlagene Lagerfach automatisch - alle Teile da ->
  Complete-Fach, fehlt eins -> Wartend-Fach.
- **Enter-Taste = "In Fach legen"**: kein Maus-Klick mehr noetig - nach
  dem Markieren der Teile einfach Enter druecken.
- **Klare Anweisung wo das Item hin soll**: nach Druck auf Enter oder
  "In Fach legen"-Button erscheint mittig oben ein Hinweis welches
  Fach gemeint ist (mit Bild).

## Was ist neu in v0.1.17

- **Tastatur-Shortcuts fuer Brickognize-Vorschlaege**: nach einem Scan
  kannst du mit den Tasten 1, 2 oder 3 den entsprechenden Vorschlag
  direkt uebernehmen - kein Maus-Klick mehr noetig.
- **Statistik-Dashboard erweitert**: der Live-Stats-Tab zeigt jetzt
  zusaetzlich Lagerfach-Auslastung, Top-5-Faecher mit den meisten Items
  und die letzten Komplettierungen.
- **Beschreibung statt Notizen**: das Notiz-Feld in der Figur-Ansicht
  heisst jetzt "Beschreibung" - der Text landet beim BSX-Export im
  oeffentlichen BL-Comment-Feld und ist fuer Kaeufer sichtbar
  (Hinweise wie "kleiner Riss am Helm" hier eintragen).
- **Einzelteil-Verschieben rueckgaengig**: das Bulk-Verschieben von
  Einzelteilen kann jetzt auch via Strg+Z rueckgaengig gemacht werden.
- **DataHeal-Verbesserung**: bei einer einzelnen verlorenen Lagerfach-
  Zuordnung wird die letzte bekannte Position automatisch wiederhergestellt.
- **Layout-Fix**: die Teile-Liste in der Figur-Ansicht nutzt jetzt den
  verfuegbaren Platz, wenn du den Trenner nach unten ziehst.

## Was ist neu in v0.1.16

- **Backup-System** mit Pending-Restore-Pattern (App startet nach
  Wiederherstellung automatisch neu).
- **Verlauf-Tab + Strg+Z** fuer Undo aller wichtigen Aktionen.
- **Bulk-Loeschen + Bulk-Verschieben** im Temporären Inventar, inkl.
  wartender Figuren.
- **EmptyAsync-Warnung**: beim "Fach leeren" wird gewarnt wenn
  komplette Figuren im Bin sind (sie verlieren ihr Lagerfach).
- **Auto-BL-Import optimiert**: HTTP 304 + SHA256-Hash-Check -
  Update-Pruefung dauert jetzt ~50 ms statt 5-10 Min wenn die
  BrickStore-DB unveraendert ist.
- **Quality-of-Life**: Doppelklick oeffnet Details, Entf-Taste
  loescht, Suche findet auch Lagerfach-Labels, Default-Auswahl
  beim Scannen konfigurierbar.

## Screenshots

### Einzelteil-Erkennung mit Reverse-Match

![Einzelteil-Scan mit Brickognize, automatische Zuordnung zu wartenden Figuren](docs/screenshots/02-einzelteil-erkennung.png)

Gescannte Einzelteile werden automatisch wartenden Figuren zugeordnet
(Reverse-Match). Die rechte Seite zeigt mit welchen wartenden Figuren
das Teil kombinierbar ist.

### Temporäres Inventar mit Bulk-Aktionen

![Temporäres Inventar mit Filter, Such-Feld und Bulk-Action-Bar (Loeschen/Verschieben/Exportieren)](docs/screenshots/04-lagerliste.png)

Komplette Bestandsuebersicht mit Filtern (Status, Lagerfach,
Volltext-Suche), Multi-Select-Checkboxen und Bulk-Operationen
(ab v0.1.16). Status-Badges zeigen auf einen Blick was komplett,
wartend oder ein Einzelteil ist.

### Figur-Detail

![Detail-Dialog einer kompletten Figur mit Teile-Liste, Verschieben/Zerlegen-Buttons](docs/screenshots/05-figur-detail.png)

Pro Figur: Status, Teile-Liste mit Bildern, Verschieben in anderes
Fach, Zerlegen-Wizard, Loeschen.

### Figur zerlegen (DismantleWizard)

![DismantleWizard mit Auswahl pro Teil: behalten/verwerfen + Ziel-Lagerfach](docs/screenshots/03-figur-zerlegen.png)

Beim Zerlegen kannst du pro Teil entscheiden: in den Pool legen, einer
wartenden Figur zuordnen (UX X.25) oder verwerfen.

## Voraussetzungen

- Windows 10 oder 11
- USB-Webcam
- BrickStore-Datenbank: muss beim ersten Start importiert werden ueber
  **Einstellungen → BrickLink → Sektion *Catalog-Daten* → "Von GitHub
  importieren"**. Holt das ~12 MB grosse `downloads.zip` von
  rgriebl/brickstore-database und entpackt es in `bl_cache.db`; ohne
  diesen Schritt kennt HB-Sort die Teile-Stammdaten nicht.
- BrickLink-Consumer-Account (optional, nur fuer Live-Preise und
  Wanted-List-Export)
- Fuer den Source-Build: .NET 8 SDK (https://dotnet.microsoft.com/download)

## Installation

### Aus dem Source bauen

```powershell
git clone https://github.com/rainman19121979/HB-Sort.git
cd HB-Sort
dotnet build -c Release
dotnet run --project HBSort
```

### Setup-Installer (.exe) - empfohlen

Auf jedem GitHub-Release liegt ein `HBSort-Setup.exe`. Das ist ein
schlanker Installer (Velopack-Format) der:

- HB-Sort unter `%LOCALAPPDATA%\HBSort\` ohne Admin-Rechte installiert,
- einen Startmenue-Eintrag anlegt,
- in **Programme & Features** als "HB-Sort" auftaucht (zum
  Deinstallieren),
- **Auto-Update** unterstuetzt: spaetere Versionen werden im Hintergrund
  geladen und beim naechsten Start eingespielt.

**SmartScreen-Warnung beim ersten Start:** weil der Installer aktuell
nicht code-signiert ist, zeigt Windows ein blaues SmartScreen-Fenster
"App von unbekanntem Herausgeber". Klick auf "Weitere Informationen"
und dann "Trotzdem ausfuehren". Das ist normal fuer kostenlose Open-
Source-Hobby-Apps - Code-Signing-Zertifikate kosten 100-300 EUR/Jahr
und sind fuer dieses Projekt aktuell nicht vorgesehen.

Auto-Updates aus der laufenden App heraus haben **keine** SmartScreen-
Warnung mehr - die wird nur beim allerersten Setup-Klick gezeigt.

### Portable ZIP - keine Installation

Auf jedem Release liegt zusaetzlich `HBSort-X.Y.Z-win-x64.zip`. Das ist
ein Self-Contained Single-File-Build:

- Entpacken irgendwohin, `HBSort.exe` doppelklicken.
- Kein Eintrag in Programme & Features.
- **Kein Auto-Update** - neue Versionen musst du selbst herunterladen.
- Lauffaehig ohne .NET-Installation und ohne Admin-Rechte.

Daten landen in beiden Varianten unter `%APPDATA%\HBSort\` und sind
unabhaengig von der App-Installation - du kannst zwischen Setup und
Portable wechseln, ohne deinen Bestand zu verlieren.

## Konfiguration

Beim ersten Start die Tokens unter **Einstellungen → BrickLink** eintragen
(falls Live-Preise oder Wanted-List gewuenscht). Die Tokens werden via
Windows-DPAPI verschluesselt im AppData-Ordner abgelegt - sie sind
ausschliesslich auf demselben Windows-Benutzerprofil entschluesselbar.

Details zu jedem Bereich gibt es in der **integrierten Hilfe** (F1).

## Datenquellen & Drittanbieter

HB-Sort verlaesst sich auf drei externe Datenquellen. Sie sind klar
getrennt und alle bewusst gewaehlt - HB-Sort sammelt selbst keine
Daten zentral.

### Brickognize

- **Wofuer**: Webcam-Bild-basierte Teile- und Minifig-Erkennung (KI-Modell).
- **Link**: https://brickognize.com
- **Hinweis**: HB-Sort schickt nur das aufgenommene Webcam-Bild an die
  Brickognize-API zur Erkennung. Kein Account ist Pflicht fuer die
  Nutzung von HB-Sort - die API ist frei nutzbar mit fairem Rate-Limit.
  Mehr Infos auf brickognize.com.
- **Brickognize-Terms-of-Service**: Mit der Nutzung von HB-Sort
  akzeptierst Du implizit die Terms-of-Service von Brickognize
  (siehe brickognize.com -> "Legal" / "Terms of Service").
  Insbesondere:
  - Hochgeladene Bilder werden gemaess Brickognize-ToS Section 8
    verarbeitet (Lizenz-Grant an Brickognize zur Nutzung der
    hochgeladenen Bilder fuer u.a. Modell-Training).
  - HB-Sort ist nicht von Brickognize betrieben oder finanziert.
    Wenn Du Bedenken hast, schau Dir die ToS an oder verzichte auf
    den Webcam-Erkennungs-Schritt (HB-Sort ist auch nutzbar fuer
    manuelle Item-Eingabe).


### BrickStore-Datenbank (rgriebl/brickstore-database)

- **Wofuer**: Lokale Item-Stammdaten (Minifig-Subsets, Teile-Namen,
  Farben). HB-Sort nutzt die aufbereitete Daten-Pipeline von BrickStore
  von Robert Griebl, weil sie die offiziellen BrickLink-Daten in einem
  nutzbaren XML-Format zur Verfuegung stellt.
- **Link**: https://github.com/rgriebl/brickstore-database
- **Lizenz der Daten**: gemaess BrickStore-DB-Repo (GPL-3 / Daten-
  Lizenzhinweise dort).
- **Wichtig**: HB-Sort verteilt diese Daten NICHT mit. Sie werden beim
  ersten Setup direkt aus dem oben genannten Repo geladen.

### BrickLink-API

- **Wofuer**: Optional - aktuelle Preise (Sold/Stock-Guides) fuer
  gescannte Items.
- **Link**: https://www.bricklink.com/v3/api.page
- **Hinweis**: Erfordert einen eigenen BL-Consumer-Account beim User
  inklusive IP-Whitelist im BL-Profil. HB-Sort selbst sammelt keine
  BL-Daten zentral; alle Calls laufen direkt vom User-PC zur BL-API
  ueber dessen eigene Tokens.

## Tech-Stack

- C# 12 / .NET 8 LTS
- WPF + ModernWpfUI
- MVVM via CommunityToolkit.Mvvm
- EF Core 8 (SQLite) fuer Userdaten
- Microsoft.Data.Sqlite (raw ADO.NET) fuer den BL-Cache
- BricklinkSharp (BL-API, OAuth1)
- OpenCvSharp4 (Webcam)
- Markdig + Markdig.Wpf (Hilfe-Rendering)
- Serilog (Logging)
- Windows DPAPI (Token-Verschluesselung)

## Lizenz

HB-Sort steht unter der **GNU General Public License v3.0** - siehe
[LICENSE](LICENSE).

## Roadmap

Die geplanten Features fuer die naechsten Iterationen, der Stand der
aktuellen Version und das langfristige Backlog stehen in
[BACKLOG.md](BACKLOG.md). Items sind nach Iteration gruppiert und mit
Status-Markern (geplant / in-arbeit / erledigt) versehen.

## Mitwirken

Issues und Pull Requests willkommen. Vor groesseren Aenderungen bitte
ein Issue eroeffnen, damit wir Richtung und Scope abstimmen koennen.
Vor neuen Features lohnt ein Blick ins [BACKLOG.md](BACKLOG.md) - dort
steht meist schon ob das Thema geplant ist oder warum es ggf. bewusst
zurueckgestellt wurde.

## Entwicklungs-Hinweis

Developed with AI assistance.
