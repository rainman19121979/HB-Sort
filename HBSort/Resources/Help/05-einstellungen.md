# Einstellungen

Du erreichst die Einstellungen ueber das **Zahnrad-Icon** oben rechts
in der Header-Leiste (oder via **Strg + ,**). Sie sind seit v0.1.19
in folgende Tabs gegliedert:

1. Erkennung
2. Darstellung
3. BrickLink (mit Sektionen *API-Zugang*, *API-Nutzung*, *Preise*, *Catalog-Daten*)
4. Lagerfaecher
5. Kategorien
6. Cache
7. Export
8. Statistik
9. Backups
10. Updates
11. Hotkeys
12. Info

## Erkennung

Alles was mit dem Brickognize-Scan zu tun hat.

- **Kamera-Index** - waehlt die richtige USB-Webcam, falls mehrere
  am PC haengen.
- **Score-Schwellen** - ab welchem Score wird ein Scan akzeptiert?
  - **Auto-Schwelle** (Default 0.95): >= dieser Wert -> Scan wird ohne
    Nachfrage uebernommen.
  - **Auswahl-Schwelle** (Default 0.7): unter Auto, aber >= diesem
    Wert -> Top-3-Karten zur Auswahl.
  - **Min-Schwelle** (Default 0.5): unter diesem Wert gilt der Scan
    als "nicht erkannt".
- **Scan-Cooldown** - Pause zwischen zwei Scans, verhindert
  Doppelscans (Default 1000 ms).
- **Sound-Effekte** - akustisches Feedback (Default aus).

### Sortier-Workflow (ab v0.1.16)

**Beim Scannen einer Figur** kannst du waehlen wie die Teile-Liste
initial befuellt wird:

- *Nichts vorab abgehakt - ich klicke an was ich habe* (Default):
  Standardpfad. Du sammelst Teil fuer Teil und bestaetigst sie.
- *Alles vorab abgehakt - ich klicke ab was fehlt*: nuetzlich wenn
  du ohnehin meistens komplette Figuren hast und nur einzelne
  fehlende Teile abklickst.

Du kannst die Auswahl pro Figur weiterhin manuell anpassen - die
Setting steuert nur die Vorbelegung beim Scan.

### Maximale Complete-Figuren pro Lagerfach (ab v0.1.18)

Bestimmt wie viele zusammengebaute Figuren sich ein Lagerfach teilen
duerfen. Default: **5**.

- **1** = jede Complete-Figur bekommt ihr eigenes Fach (streng).
- **5** (Default) = praktischer Kompromiss.
- **999** = praktisch unbegrenzt zusammenpacken.

Wirkt nur auf den Default-Vorschlag. Manuell bleibst du frei.

### Maximale wartende Figuren pro Lagerfach (ab v0.1.19-beta.4)

Bestimmt wie viele wartende (noch unfertige) Figuren sich ein Fach
teilen duerfen. Default: **3**.

- **1** = jede wartende Figur bekommt ihr eigenes Fach (strikt).
- **3** (Default) = praktischer Kompromiss - bis zu 3 wartende parallel.
- **999** = praktisch unbegrenzt mischen.

## Darstellung

- **Tooltips anzeigen** - blendet alle Tooltips aus, falls sie dich
  stoeren. Wirkt sofort, ohne Neustart. Default an.

## BrickLink

Alles rund um die offizielle BL-Catalog- und Preis-API. Tab ist
scrollbar - vier Sektionen.

### Sektion: API-Zugang (Tokens)

Hier hinterlegst du deine BL-Tokens, damit HB-Sort Live-Daten und
Preise von der BrickLink-API ziehen kann. Optional - HB-Sort
funktioniert auch ohne, wenn du den BrickStore-Bulk-Import nutzt
(siehe Sektion *Catalog-Daten* weiter unten).

#### Tokens erzeugen

1. Auf [bricklink.com](https://www.bricklink.com/v2/api/register_consumer.page)
   einloggen und dort "Register Consumer" anklicken.
2. **Application-Name** und deine externe IP eintragen.
3. BL gibt dir dann **vier** Werte:
   - Consumer Key
   - Consumer Secret
   - Token Value
   - Token Secret
4. Alle vier in HB-Sort eintragen und **Test-Verbindung** klicken.
5. Wenn der Test gruen ist, **Speichern**.

> **Wichtig:** HB-Sort verschluesselt die Tokens via Windows DPAPI.
> Sie sind nur fuer den aktuellen Windows-User entschluesselbar - wer
> die settings.json kopiert, kann nichts damit anfangen. Bei
> Windows-User-Wechsel oder PC-Wechsel musst du die Tokens neu
> eingeben.

### Sektion: API-Nutzung (Rate-Limit)

BL erlaubt 5000 Calls / 24h (rolling). HB-Sort haelt sich konservativ
an eigene Schwellen:

- **Soft-Warnung** (Default 1000): gelber Toast bei Erreichen.
- **Hard-Stop** (Default 4500): rot, ab da nur noch Cache.

In der Status-Leiste siehst du den aktuellen Stand
("BL: 47 / 4500 (24h)"). Mit Hover bekommst du einen Tooltip mit
Detail-Counter.

### Sektion: Preise

Konfiguration des BL-Preis-Lookups (Phase 8). Provider-Auswahl
(BrickLink-API / None), Filter (Sold/Stock, Used/New, Region,
Country, Currency), Korrekturfaktoren (z.B. -10% Minifig / -15%
Parts), Auto-vs-Manuell-Modus pro Bereich (Komplett-Preis /
Einzelteile). Cache-TTL pro Item-Typ. Mehr Details im Kapitel
*Export & Verkauf*.

#### Auto-Modus und API-Aufrufe (ab v0.1.20)

BrickLink hat ein Tageslimit von **5000 API-Aufrufen** (rolling 24h). HBSort
zaehlt eigenstaendig mit und blockt sich konservativ ab 4500 Aufrufen.

| Modus | API-Aufrufe |
|---|---|
| Komplett-Preis Auto | 1 pro Figur die du oeffnest |
| Komplett-Preis Manual | 0 - bis du auf "Preis laden" klickst |
| Einzelteile Auto | 1 **pro Teil-Typ** (kann viele sein!) |
| Einzelteile Manual | 0 - bis du auf "Teile-Preise laden" klickst |

**Empfehlung:** Manual-Modus fuer Einzelteile. Du laedst die Preise nur fuer
Figuren wo du sie wirklich brauchst (z.B. vor Verkauf). Eine Figur mit 8
Teilen braucht im Auto-Modus 8 separate API-Aufrufe.

Sobald du gegen das Limit laeufst, blockiert die App neue Aufrufe und zeigt
stale Cache-Werte - kein Datenverlust, aber Preise sind dann ein paar
Stunden alt. Status siehst du in der Status-Leiste unten rechts
("BL: X / 4500").

#### Region/Land-Filter (ab v0.1.20)

Du kannst auswaehlen aus welchem geografischen Bereich die Preise kommen
sollen:

- **Global (Default):** alle Verkaufstransaktionen weltweit. Groesste
  Datenbasis, aber Preise koennen weit auseinander gehen (USA, Asien etc.).
- **Region:** Filter auf eine Region. BL-API kennt 8 Werte:
  `europe` (ganz Europa inkl. UK/CH), `eu` (nur EU-Mitgliedsstaaten -
  VAT-pflichtige Verkaeufer), `north_america`, `south_america`, `asia`,
  `middle_east`, `africa`, `oceania`. Mittelweg zwischen lokal und global.
- **Land:** Filter auf ein spezifisches Land (z.B. DE, US, GB). Engste
  Filterung.

**Wichtig:** Region und Land sind **Either-Or**. BL-API erwartet nur einen
der beiden Filter. Setze entweder das eine oder das andere - HBSort leitet
das aus deiner RadioButton-Wahl ab. Wenn du in v0.1.19 oder frueher beide
gleichzeitig gesetzt hattest (Default war "europe" + "DE"), normalisiert
HBSort beim Update automatisch auf "Land=DE" und blendet "Region" aus.

**Bei wenig/keinen Daten:**
- Bei `Sold` (Verkaufte) bedeutet Land-Filter: nur Verkaeufe **DURCH**
  Verkaeufer aus diesem Land. Sind oft wenige -> wenig/keine Daten.
- Bei `Stock` (Aktuelle Angebote) bedeutet Land-Filter: nur Listings
  **VON** Verkaeufern aus diesem Land.

Bei wenig Daten: weniger eng filtern (Region statt Land, oder Global).

#### VAT-Modus (ab v0.1.20)

Bestimmt wie BrickLink VAT (Mehrwertsteuer) bei den abgerufenen Preisen
behandelt:

- **Brutto (Default):** Preise inkl. VAT - matcht was du auf der
  BrickLink-Webseite siehst. Empfohlen fuer fast alle User.
- **Netto:** Preise ohne VAT. Nur sinnvoll wenn du selbst VAT-
  Verkaeufer bist und deine Erloesabschaetzung netto haben willst.
- **Norwegen:** Spezial-Fall mit 25% VAT.

> **Hintergrund:** Die BL-API liefert ohne expliziten `vat`-Parameter
> standardmaessig **Netto** (Hinweis aus der offiziellen BL-API-Doku:
> *"returned price does not include VAT"*). HBSort schickt seit v0.1.20
> explizit `vat=Y` mit, sonst bekommst du gemischte Aggregate (Privat-
> Verkaeufer brutto, gewerbliche netto).

> **Wichtig:** wenn du den Modus aenderst, werden die zwischengespeicherten
> Preise nicht mitumgerechnet. Frische Werte holst du beim naechsten
> Preis-Lookup automatisch (Cache-Miss -> frischer API-Call). Bestand-
> Eintraege aus v0.1.19 oder frueher sind als "Netto" markiert (alter
> impliziter Default) - bei Wechsel auf Brutto greift der Refresh-Pfad
> automatisch.

### Sektion: Catalog-Daten

Die schnellste Variante, um an Stammdaten zu kommen, ohne BL-API:

- **Von GitHub importieren** - laed `downloads.zip` (~12 MB) von der
  oeffentlichen
  [BrickStore-Datenbank](https://github.com/rgriebl/brickstore-database)
  und importiert direkt nach `bl_cache.db`. Internet-
  Verbindung noetig, BL-Account nicht.

#### Automatischer Import (ab v0.1.15)

Wenn aktiviert, prueft HB-Sort beim App-Start ob die BrickLink-Daten
aelter als das gewaehlte Intervall sind und laed sie dann im
Hintergrund neu. Default ist **AUS** - du musst es bewusst aktivieren.

- **Letzter Import** - Zeitstempel des letzten erfolgreichen Imports
  (manuell oder Auto), oder "noch nie".
- **Automatisch im Hintergrund aktualisieren** - Toggle.
- **Intervall** - Dropdown 7 / 14 / 30 / 90 Tage. Default 30.

Beim erfolgreichen Auto-Import siehst du Toasts:
- Vorher: *"BrickLink-Daten werden im Hintergrund aktualisiert..."*
- Erfolg: *"BrickLink-Daten aktualisiert."*
- Fehler: *"Auto-Import fehlgeschlagen - bitte manuell"* (du kannst
  dann einfach "Von GitHub importieren" oben klicken).

Manueller Import oben funktioniert immer unabhaengig vom Toggle.

## Lagerfaecher

Liste aller Faecher mit Belegt/Frei-Status. Buttons:

- **Anlegen** / **Bulk anlegen**
- **Umbenennen**
- **Fach leeren** - gibt das Fach frei und loest alle Zuweisungen.
  Ab v0.1.16 mit differenzierter Confirmation:
  - Bei wartenden Figuren + Einzelteilen: normale Bestaetigung,
    Strg+Z (Verlauf-Tab) macht jede Entkopplung einzeln rueckgaengig.
  - **Bei kompletten Figuren im Fach**: gesonderte ACHTUNG-Warnung
    mit dem Hinweis dass sie ihr Lagerfach verlieren. Strg+Z stellt
    die Zuordnung pro Figur wieder her.
- **Loeschen** - nur moeglich, wenn frei.

## Kategorien

Tab seit v0.1.19-beta.7. Hier ordnest du **Brickognize-Kategorien**
(z.B. "Minifigure, Head", "Minifigure, Headgear") manuell festen
Lagerfaechern zu. Wenn du z.B. "Minifigure, Headgear" auf "Box21-3"
setzt, landen ALLE Helme in diesem Fach - egal welche Teile-Nummer,
egal welche Farbe.

**Wenn du keine Zuordnung machst** ("(kein Mapping)" im Dropdown):
greift die Standard-Regel - **maximal eine Teile-Nummer pro Brickognize-
Kategorie pro Lagerfach**. Verschiedene Kategorien (Helm + Kopf + Hose)
duerfen sich ein Fach teilen, zwei verschiedene Helme nicht.

> **Stapel-Match wins immer:** identische Teile (gleiche Teile-Nummer
> und Farbe) sammeln sich automatisch im gleichen Fach, unabhaengig vom
> Mapping. Mehrfaches Scannen vom gleichen Brick landet als Stapel im
> selben Bin.

Die Liste zeigt alle Kategorien die HB-Sort bisher gesehen hat - sie
fuellt sich organisch beim Scannen. Neue Kategorien tauchen auf, sobald
du das erste Teil dieser Kategorie scannst.

## Cache

Drei Sektionen in einem scrollbaren Tab.

### Sektion: Bild-Cache

- **Limit** in MB (Default 1024).
- **BL-Bilder bevorzugen** - echte BL-Teile-Fotos statt Brickognize-
  Renderings.
- **Vorab-Cache** beim Minifig-Scan - laed nach erkannter Figur
  schon mal alle Teile-Bilder im Hintergrund.

### Sektion: BL-Daten-Cache

Statistik (Items, Subsets, Farben in `bl_cache.db`) plus Pruning- und
Cache-Leeren-Buttons. **Cache leeren / pruning** bereinigt alte
Eintraege > 90 Tage.

### Sektion: Preis-Cache

TTL-Tage pro Item-Typ (Default 90 fuer Minifigs und Einzelteile),
Anzahl gecachter Preise plus **Preis-Cache leeren**-Button.

## Export

Default-Ordner fuer BSX und Wanted-List setzen.
Default ist `Documents\HBSort-Export\`.

## Statistik

Zeigt dir Heute / 7T / 30T / Insgesamt:

- Anzahl Scans
- Komplettierte Figuren
- Zerlegte Figuren

Plus aktueller Bestand: wartende Figuren, komplette Figuren,
Floating-Parts, belegte Faecher.

## Backups

Im Tab *Backups* kannst du deine Daten (Lagerfaecher, BL-Cache,
Einstellungen) als ZIP sichern. Backups landen unter
`%APPDATA%\HBSort\backups\`.

### Automatisch beim App-Start

Wenn aktiviert (Default an), erstellt HB-Sort beim App-Start ein neues
Backup, sofern das letzte Backup laenger als das gewaehlte Intervall her
ist (Taeglich / Woechentlich / Monatlich). Aelteste Backups werden
automatisch entfernt sobald die "Anzahl behaltener Backups" ueberschritten
wird.

### Manuell

Klick auf **Backup jetzt erstellen** loest einen sofortigen Backup-Lauf
aus. Status-Text zeigt Erfolg + Datei-Groesse. Auch via **Strg + B**
ausloesbar.

### Wiederherstellen

Klick **Wiederherstellen** an einem Backup-Eintrag oeffnet eine
Bestaetigung. Bei *Ja* legt HB-Sort:

1. ein automatisches Pre-Restore-Backup deines aktuellen Standes an
   (taucht in derselben Liste auf - wenn etwas schief geht, kannst du
   damit zurueck);
2. die Backup-Dateien in einen Pending-Ordner;
3. **startet die App automatisch neu**. Erst beim Neustart werden die
   Datenbank-Dateien tatsaechlich ersetzt - direkter Restore zur
   Laufzeit ist nicht moeglich, weil `userdata.db` und `bl_cache.db`
   gerade geoeffnet sind.

Loeschen-Button entfernt ein Backup dauerhaft (mit Bestaetigung).

## Updates

Auto-Update via GitHub Releases - aber nur wenn du HB-Sort ueber den
**Setup.exe**-Installer installiert hast. Bei Portable-ZIP-Nutzung
ist der ganze Tab deaktiviert; neue Versionen musst du dann manuell
herunterladen.

- **Beim App-Start nach Updates suchen** (Default an): einmal pro
  App-Start im Hintergrund pruefen ob ein neuer Release auf GitHub
  verfuegbar ist. Bei Treffer wird in diesem Tab eine blaue Box
  "Update verfuegbar: vX.Y.Z" angezeigt.
- **Jetzt nach Updates suchen**: manueller Check, unabhaengig von
  Auto-Check.
- **Letzte Pruefung**: Zeitstempel der letzten erfolgreichen
  Hintergrund- oder manuellen Pruefung.
- **Jetzt auf vX.Y.Z updaten**: erscheint nur wenn ein Update
  verfuegbar ist. Klick laedt die neue Version im Hintergrund
  herunter und startet die App automatisch neu.

Deine Daten unter `%APPDATA%\HBSort\` (settings.json, Datenbanken,
Logs) bleiben beim Update unveraendert - nur die App selbst wird
ausgetauscht.

## Hotkeys

Tabelle aller Tastatur-Shortcuts (Leertaste, Strg+Z, Strg+B, F1,
Strg+S/L/H/Q etc.). Die gleiche Liste steht auch in der Hilfe (F1)
unter Kapitel *Tastatur-Shortcuts*.

User-konfigurierbare Hotkeys sind aktuell nicht vorgesehen.

## Info

Versions-Info, Lizenz-Hinweise, Pfad zu `%APPDATA%\HBSort\` (DB,
settings.json, Logs).
