# Einstellungen

Du erreichst die Einstellungen ueber das **Zahnrad-Icon** oben rechts
in der Header-Leiste. Sie sind in Tabs gegliedert.

## Allgemein

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
- **Tooltips anzeigen** - blendet alle Tooltips aus, falls sie dich
  stoeren. Default an.

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

### Hotkeys

Im Tab **Hotkeys** (ab v0.1.16) findest du eine Tabelle aller
Tastatur-Shortcuts (Leertaste, Strg+Z, Strg+B, F1 etc.). Die
gleiche Liste steht in der Hilfe (F1) → Kapitel "Tastatur-
Shortcuts".

User-konfigurierbare Hotkeys sind aktuell nicht vorgesehen.

## BrickLink-API

Hier hinterlegst du deine BL-Tokens, damit HB-Sort Live-Daten und
Preise von der BrickLink-API ziehen kann. Optional - HB-Sort
funktioniert auch ohne, wenn du den BrickStore-Bulk-Import nutzt
(siehe Tab *BL-Catalog-Daten*).

### Tokens erzeugen

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

### Rate-Limit-Schwellen

BL erlaubt 5000 Calls / 24h (rolling). HB-Sort haelt sich konservativ
an eigene Schwellen:

- **Soft-Warnung** (Default 1000): gelber Toast bei Erreichen.
- **Hard-Stop** (Default 4500): rot, ab da nur noch Cache.

In der Status-Leiste siehst du den aktuellen Stand
("BL: 47 / 4500 (24h)"). Mit Hover bekommst du einen Tooltip mit
Detail-Counter.

## BL-Catalog-Daten

Die schnellste Variante, um an Stammdaten zu kommen, ohne BL-API:

- **Von GitHub importieren** - laed `downloads.zip` von der
  oeffentlichen
  [BrickStore-Datenbank](https://github.com/rgriebl/brickstore-database)
  (~12 MB) und importiert direkt nach `bl_cache.db`. Internet-
  Verbindung noetig, BL-Account nicht.
- **Cache leeren / pruning** - bereinigt alte Eintraege > 90 Tage.

### Automatischer Import (ab v0.1.15)

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

## Bild-Cache

- **Limit** in MB (Default 1024).
- **BL-Bilder bevorzugen** - echte BL-Teile-Fotos statt Brickognize-
  Renderings.
- **Vorab-Cache** beim Minifig-Scan - laed nach erkannter Figur
  schon mal alle Teile-Bilder im Hintergrund.

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
aus. Status-Text zeigt Erfolg + Datei-Groesse.

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

## Preise

Konfiguration des Preis-Lookups (Phase 8). Siehe Kapitel
*Export & Verkauf*.

## Statistik

Zeigt dir Heute / 7T / 30T / Insgesamt:

- Anzahl Scans
- Komplettierte Figuren
- Zerlegte Figuren

Plus aktueller Bestand: wartende Figuren, komplette Figuren,
Floating-Parts, belegte Faecher.

## Export

Default-Ordner fuer BSX und Wanted-List setzen.
Default ist `Documents\HBSort-Export\`.

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

## Info

Versions-Info, Lizenz-Hinweise, Pfad zu `%APPDATA%\HBSort\` (DB,
settings.json, Logs).
