# Erste Schritte

Willkommen bei **HB-Sort**! Diese App hilft dir, lose
LEGO-Minifiguren-Einzelteile zu erkennen, der passenden Figur
zuzuordnen und in Lagerfaechern zu sammeln, bis die Figur komplett ist.

## Was du brauchst

| Element | Pflicht / optional | Wozu? |
|---|---|---|
| **USB-Webcam** | Pflicht | Damit HB-Sort Teile und Figuren scannen kann. Eine guenstige USB-Cam reicht voellig. |
| **Lagerfaecher** | Pflicht | Box, Schalen, Schubladen - irgendwas mit Beschriftung. Du verwaltest sie in der App. |
| **Internet-Verbindung** | Pflicht | Brickognize (Erkennung) und BrickLink (Stammdaten/Preise) laufen online. |
| **Brickognize** | Pflicht | Erkennt das LEGO-Teil auf dem Webcam-Bild. Kostenlos und **ohne Anmeldung**. |
| **BrickLink-Account** | Optional | Nur noetig wenn du Stammdaten/Preise direkt von der BL-API ziehen willst. Ohne Account kannst du den BrickStore-Bulk-Import nutzen (siehe unten). |
| **BrickStore** | Optional | Falls du Figuren ueber den BSX-Export weiterverkaufen willst. |

> **Tipp:** Du kannst HB-Sort auch erstmal ohne BL-Account ausprobieren -
> die wichtigsten Stammdaten kommen aus dem BrickStore-Bulk-Import.

## Erstmaliges Einrichten

1. **Webcam anschliessen** und gegebenenfalls in den Einstellungen
   (Zahnrad-Icon oben rechts -> Tab *Erkennung*) den richtigen Index
   waehlen, falls mehrere Kameras am PC haengen.

2. **BL-Catalog-Daten importieren** (Einstellungen -> Tab
   *BrickLink* -> Sektion *Catalog-Daten* -> *Von GitHub importieren*).
   Das laed einmalig ~12 MB von der oeffentlichen BrickStore-Datenbank
   und legt damit alle Catalog-Items, Subsets und Farben in deiner
   lokalen `bl_cache.db` ab. Anschliessend kann HB-Sort Stammdaten ohne
   BL-API-Calls anzeigen.

3. **Optional: BrickLink-Tokens hinterlegen** (Einstellungen -> Tab
   *BrickLink* -> Sektion *API-Zugang*). Nur noetig wenn du Live-Daten
   oder Preise direkt von der BL-API ziehen willst. Wie du Tokens
   bekommst, steht im Kapitel *Einstellungen*.

4. **Lagerfaecher anlegen** (Einstellungen -> Tab *Lagerfaecher*).
   Lege so viele Faecher an, wie du physisch hast. Du kannst sie auch
   in einem Rutsch erzeugen ("Box 001"-"Box 050"). Beschrifte deine
   physischen Faecher entsprechend, damit du nichts verwechselst.

5. **Erster Scan**: Tab *Sortieren* aktivieren, ein LEGO-Teil oder
   eine Figur vor die Webcam halten, **Leertaste** druecken.

Mehr Details zum Workflow findest du im Kapitel *Sortier-Workflow*.

## First-Run-Setup (ab v0.1.20-beta.2)

Beim allerersten Start auf einem leeren Rechner zeigt HBSort einen
Willkommens-Dialog mit drei Schritten:

### 1. BrickLink-Catalog laden (Pflicht)

HBSort holt einmalig ca. 12 MB Stammdaten (ca. 110.000 Items, Farben und
Subsets) aus der oeffentlichen BrickStore-Datenbank auf GitHub. Dauert
je nach Verbindung 30-90 Sekunden. **Ohne diesen Catalog** kann HBSort
keine Stammdaten zu gescannten Teilen anzeigen und der Sortier-Workflow
funktioniert nicht.

### 2. Lagerfaecher anlegen (empfohlen)

Du brauchst physische Boxen die HBSort als "Lagerfaecher" verwaltet.
Empfehlung: starte mit 10-30 Boxen je nach Sammlungsgroesse - du kannst
spaeter weitere anlegen oder loeschen. Klick auf "Lagerfach-Verwaltung
oeffnen" -> Settings-Dialog -> Tab "Lagerfaecher" -> "Bulk anlegen".

### 3. BrickLink-Tokens (optional)

Fuer Live-Preise und Verkaufsempfehlungen. Brauchst du nicht zum
Sortieren - kannst du jederzeit unter *Einstellungen > BrickLink >
API-Zugang* einrichten.

### Setup unterbrochen?

Wenn du den Dialog mit "Spaeter erinnern" schliesst aber Setup nicht
komplett ist, erscheint er beim naechsten App-Start wieder. Wenn du
"Loslegen" klickst nach erfolgreichem Catalog-Import, taucht der Dialog
auch dann nicht mehr auf wenn du noch keine Lagerfaecher hast - du
kannst sie jederzeit in den Einstellungen anlegen.
