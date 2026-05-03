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
- **Freeze-Frame-Dauer** - wie lange das Bild nach dem Scan eingefroren
  bleibt (Default 1000 ms).
- **UI-Dichte** - Kompakt / Normal / Komfortabel.
- **Sound-Effekte** - akustisches Feedback (Default aus).
- **Tooltips anzeigen** - blendet alle Tooltips aus, falls sie dich
  stoeren. Default an.

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

## Bild-Cache

- **Limit** in MB (Default 1024).
- **BL-Bilder bevorzugen** - echte BL-Teile-Fotos statt Brickognize-
  Renderings.
- **Vorab-Cache** beim Minifig-Scan - laed nach erkannter Figur
  schon mal alle Teile-Bilder im Hintergrund.

## Lagerfaecher

Liste aller Faecher mit Belegt/Frei-Status. Buttons:

- **Anlegen** / **Bulk anlegen**
- **Umbenennen**
- **Fach leeren** - gibt das Fach frei und loest alle Zuweisungen.
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

## Info

Versions-Info, Lizenz-Hinweise, Pfad zu `%APPDATA%\HBSort\` (DB,
settings.json, Logs).
