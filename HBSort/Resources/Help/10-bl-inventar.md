# BrickLink-Inventar

Ab v0.1.24 spiegelt HB-Sort dein komplettes BrickLink-Store-Inventar
lokal in der Datenbank. So siehst du beim Sammeln einer wartenden
Figur sofort welche Teile du noch in deinem Shop hast — und kannst
sie fuer die Figur reservieren, ohne sie physisch aus dem Shop
herauszunehmen.

## Voraussetzung

Du brauchst BrickLink-Tokens (siehe Hilfe-Seite **Einstellungen** →
*BrickLink-API*). Ohne Tokens bleibt der Inventar-Tab leer und alle
BL-Hinweise im Sortier-Workflow sind ausgeblendet.

## Sync — Inventar holen

Es gibt zwei Stellen mit dem **Inventar-synchronisieren**-Button:

1. **Einstellungen → BrickLink → Sektion *BL-Store-Inventar*** —
   beim ersten Mal hier synchronisieren.
2. **Tab *BrickLink Inventar*** (oben rechts) — fuer Re-Syncs
   waehrend du den Bestand ansiehst.

Beide rufen denselben Service. Der Sync ist **ein** API-Call und
holt alle Lots (typisch wenige Sekunden bei ~10.000 Lots).

Was wird gespeichert pro Lot:

- BL-Artikelnummer + Farbe + Beschreibung + **Shop-Position (Remarks)**
- Menge, Stueckpreis, Zustand (Neu/Gebraucht)
- **ReservedQuantity** — wie viele du in HBSort fuer wartende Figuren
  eingeplant hast (BL weiss das nicht; das ist HB-Sort-intern)

### Snapshot-Replace mit Reservierungs-Erhalt

Jeder Sync ueberschreibt den lokalen Spiegel komplett, aber:

- **Bestehende Reservierungen bleiben erhalten**: wenn ein Lot vor
  dem Sync `ReservedQuantity=3` hatte und es noch in BL existiert,
  bekommt es nach dem Sync wieder `ReservedQuantity=3`.
- **Reservierung wird gecappt** wenn die BL-Menge gesunken ist (z.B.
  du hast 7 von 10 ausserhalb von HBSort verkauft → neue
  `Quantity=3`, eine vorhandene `ReservedQuantity=5` wird auf 3
  gecappt; der Diff wird in den Logs vermerkt).
- **Lots die nicht mehr in BL existieren** (komplett verkauft / im
  Shop geloescht) verlieren ihre Reservierung. Die zugehoerigen
  Figuren-Buchungen bleiben aber bestehen — User-sichtbar als "+N BL"
  ohne korrespondierendes Lot. Re-Sync nach Bereinigung loest das auf.

## Tab "BrickLink Inventar"

Eine durchsuchbare Tabelle mit allen Lots:

- **Suche** uebers Suchfeld: trifft Artikelnummer, Beschreibung,
  Catalog-Name, Farbe.
- **Filter** auf Typ (Part / Minifig / Set) und Zustand (Neu /
  Gebraucht).
- **Spalten** zeigen Menge, Reservierungs-Counter, Verfuegbar (=
  Menge - Reserviert), Preis in EUR, Shop-Position.
- **Detail-Panel** rechts: oeffnet beim Klick auf eine Zeile. Zeigt
  ein groesseres Bild (klickbar fuer Vergroesserung) und alle Felder.
- **Lazy-Thumbnails**: Bilder werden erst geladen wenn die Zeile in
  den sichtbaren Bereich scrollt. Schont das Netz bei 10.000+ Lots.

### Shop-Position (Remarks)

Wenn du in BrickLink die *Remarks* zu deinen Lots pflegst (z.B.
"Box A.2, Regal 3"), zeigt HB-Sort die in der Spalte
**Shop-Position** und im Detail-Panel. So findest du das Teil im
physischen Shop sofort wieder.

## Komplettieren mit BL-Inventar

### Im Detail-Dialog einer wartenden Figur

Beim Klick auf eine wartende Figur (Tab *Lagerliste* oder
*Wartende-Detail*) oeffnet sich der Detail-Dialog. Pro fehlendem
Teil siehst du jetzt:

- **Blauer Badge "BL-Shop"** neben der Mengenanzeige, wenn das Teil
  in deinem BL-Shop verfuegbar ist (Tooltip: "Im BL-Shop verfuegbar:
  X× Neu, Y× Gebraucht").
- **Klick auf den Badge** → der Reservierungs-Dialog oeffnet.

### Reservierungs-Dialog — Lot waehlen

Der Dialog listet alle passenden Lots **einzeln**, getrennt nach
Zustand:

```
Neu:
  B0013 — 30×
  B0022 — 16×
Gebraucht:
  C0005 — 8×
```

- Vor jedem Lot steht die **Shop-Position** aus den BL-Remarks (oder
  "(kein Lagerplatz)" wenn leer).
- Daneben die **verfuegbare Menge** (= Quantity - bereits Reserviert).
- **Klick auf eine Lot-Karte** reserviert genau dieses Lot.

Nach der Reservierung:

- Das Teil zeigt in der Figur-Detail-Liste den Status
  **"X (+1 BL)/Y"** (z.B. "0 (+1 BL)/1" → ein BL-Teil reserviert,
  effektiv komplett).
- Der **BL-Lot-Counter** `ReservedQuantity` wird um 1 erhoeht; das
  reduziert die "Verfuegbar"-Menge im Inventar-Tab.
- Ein **ScanEvent** (Type `BlInventoryReservation`) wird im Verlauf-
  Tab angelegt (aktuell nicht ueber Strg+Z rueckgaengig — dafuer
  siehe naechster Absatz).

### Reservierung rueckgaengig

Es gibt zwei Wege, eine Reservierung wieder freizugeben:

**1. Im Figur-Detail-Dialog**: den **Haken** am Teil entfernen. Das macht
alle BL-Reservierungen fuer dieses eine Teil zurueck (LIFO — die
neueste zuerst) und setzt den gesammelt-Stand zurueck.

**2. Aus dem BrickLink-Inventar-Tab — Reservierungs-Liste**: klick im
Tab **BrickLink Inventar** ein Lot an, das eine Reservierung hat (siehst
du an der "Reserviert"-Spalte). Im Detail-Panel rechts erscheint die
Sektion **Reservierungen** mit einer Zeile pro reserviertem Stueck,
inklusive der zugehoerigen Figur. Pro Zeile gibt es einen Knopf
**"Aufheben"** — damit kannst du **eine einzelne Reservierung** gezielt
freigeben, ohne alles auf einmal zuruecknehmen zu muessen. Praktisch
auch um Altlasten ("verwaiste Reservierung — keine Figur") aufzuraeumen,
die noch aus Zeiten vor v0.1.24-beta.10 stammen.

### Reserviertes Teil doch gescannt

Du hattest ein Teil im BL-Shop fuer eine Figur reserviert, aber dann
ploetzlich liegt das gleiche Teil bei dir auf dem Tisch und du scannst
es ein? HB-Sort erkennt das und **ersetzt die Reservierung
automatisch durchs physische Teil** — die Figur bleibt komplett, du
hast aber wieder eine Einheit mehr im BL-Shop zum Verkauf.

Du bekommst dann eine Anweisung, je nach Zustand des reservierten Lots:

- **Bei einem gebrauchten Teil im Shop**: Leg das gerade gescannte Teil
  in dein BL-Shop-Fach. Erklaerung im Dialog: *"Reservierung
  aufgeloest. Figur behaelt ihr Teil, BL-Shop wieder vollstaendig."*
- **Bei einem neuen Teil im Shop** (das du eigentlich nicht in eine
  gebrauchte Figur stecken willst): HB-Sort schlaegt einen Tausch vor —
  nimm das neue Teil aus dem Figur-Fach, leg es zurueck in den Shop, und
  leg dafuer das gerade gescannte (gebrauchte) Teil zur Figur. So bleibt
  dein Shop-Bestand "Neu", die Figur bekommt das passende gebrauchte
  Teil.

Im Sortier-Tab erscheinen wartende Figuren mit BL-Reservierung mit
einem blauen Hinweis-Badge **"Reservierung — zuordnen loest auf"**, damit
du gleich siehst was beim "Zuordnen" passiert.

### Beim Aufgeben/Zerlegen einer Figur

Ab v0.1.24-beta.10 werden bei jedem Loesch-/Zerlegungs-/Cleanup-
Pfad **alle offenen BL-Reservierungen automatisch freigegeben**.
Damit bleiben keine "Geist-Reservierungen" im Inventar-Tab uebrig.

## Tab "Was kann ich bauen?" — BL-Erweiterung

Im **Baubar-Tab** gibt es die Checkbox **"BL-Inventar
beruecksichtigen"** (nur sichtbar wenn du synchronisiert hast).

- **Aus** (Default): Liste zeigt Figuren die du allein aus deinen
  HBSort-Floating-Parts bauen kannst.
- **An**: Liste erweitert sich um Figuren die mit
  **HBSort + BL-Shop** zusammen komplett baubar waeren. Diese
  Eintraege haben einen blauen Badge **"X Teile aus Shop"** und
  100%-Match. Sortierung: HBSort-baubare zuerst, BL-erweiterte
  danach (nach BL-Teile-Anzahl aufsteigend).

Klick auf einen BL-erweiterten Eintrag legt die Figur wie gewohnt
als wartende Figur an. Die fehlenden Teile zeigen automatisch den
**BL-Shop**-Badge im Detail-Dialog — du kannst sie dann einzeln aus
dem Shop reservieren.

### Vorschlaege ausblenden

Bei jedem Vorschlag in der Liste gibt es rechts ein kleines **X** —
damit blendest du eine Figur dauerhaft aus den Vorschlaegen aus.
Praktisch fuer Figuren die du explizit nicht sammeln willst, oder bei
denen du weisst dass das fehlende Teil aktuell nicht beschaffbar ist.

Unten in der Fusszeile erscheint dann ein Link **"Ignorierte verwalten
(N)"**. Klick darauf oeffnet einen Dialog **Ignorierte Bauvorschlaege
verwalten** mit allen ignorierten Figuren — pro Eintrag ein
**"Wiederherstellen"**-Knopf um eine einzelne wieder freizuschalten,
plus **"Alle wiederherstellen"** wenn du die Liste komplett leerst.

## Tab "Sortieren" — BL-Toast

Wenn du ein Einzelteil scannst und es **keiner wartenden Figur** in
deinem Lager zugeordnet werden kann, aber in deinem **BL-Shop**
liegt, kommt ein informativer Toast:

> *Teil nicht im HBSort-Lager gesucht, aber 12× Neu / 3× Gebraucht
> in deinem BrickLink-Shop verfuegbar.*

Das ist **nur ein Hinweis** — es wird nichts automatisch reserviert.
Das gescannte Teil wird ganz normal als Floating-Part eingelagert.

## Reservierte Teile aus dem BL-Shop ausbuchen (Mass-Update-Export)

Wenn du Teile fuer eine Figur reserviert hast und sie physisch aus deinem
BL-Shop entnimmst, muss BrickLink natuerlich auch wissen dass diese
Mengen nicht mehr verkaufbar sind. Dafuer gibt es den Mass-Update-Export.

Im Tab **BrickLink Inventar** oben rechts: Knopf **"BL aktualisieren"**.

### Was passiert beim Oeffnen

1. **Auto-Sync**: HB-Sort holt zuerst dein aktuelles BL-Inventar
   (kurze Ladezeit, je nach Store-Groesse 2-5 Sekunden). So wird das
   Update gegen die *aktuellen* BL-Mengen erzeugt — nicht gegen einen
   alten Stand.
2. Aus deinen offenen Reservierungen entsteht ein XML im BrickLink-
   Mass-Update-Format: pro Lot entweder eine Reduktion der Menge
   (`<QTY>-N</QTY>`) oder ein kompletter Loescheintrag (`<DELETE/>`),
   falls die ganze Lot-Menge reserviert war.
3. Du siehst das XML im Dialog plus eine Zeile *"X Lot(s) betroffen
   (Y werden geloescht, Z reduziert)"*.

### So uebertraegst du es

1. Klick auf **"In Zwischenablage kopieren"** — das XML liegt jetzt in
   deiner Zwischenablage.
2. Klick auf **"BrickLink oeffnen"** — der Standard-Browser springt auf
   die BrickLink-Mass-Update-Seite. Dort das XML einfuegen und das
   Update ausfuehren.
3. Zurueck in HB-Sort → Klick auf **"Verifizieren"**.

### Was Verifizieren macht

HB-Sort holt noch einmal das frische BL-Inventar und prueft pro
betroffenem Lot, ob die Aenderung dort wirklich angekommen ist:

- Mengen stimmen? → Erfolg.
- Lot wurde geloescht (war komplett reserviert)? → Erfolg.
- Mengen passen nicht? → Eintrag bleibt offen, du kannst noch einmal
  versuchen.

Bei erfolgreich verifizierten Lots werden die Reservierungen
**umgebucht**: das Teil zaehlt jetzt als physisch gesammelt fuer die
Figur (vorher: "im BL-Shop reserviert" — jetzt: "fest im Bestand").
Die Figur sieht das gleich aus wie vorher (effektiv komplett bleibt
komplett), aber die Buchhaltung ist sauber.

Bei vollstaendigem Erfolg synchronisiert HB-Sort am Schluss automatisch
noch einmal, damit dein lokaler Spiegel zur BL-Realitaet passt.

### Wenn etwas schief geht

- **Auto-Sync beim Oeffnen schlaegt fehl** (BrickLink offline, Tokens
  abgelaufen, Limit erreicht): der Dialog zeigt einen Hinweis dass mit
  dem zuletzt bekannten Stand exportiert wird. Pruefe in dem Fall am
  besten kurz bei BL ob deine Mengen noch passen, bevor du das XML
  hochlaedst.
- **Reservierung wurde wegen gesunkener BL-Menge angepasst**: wenn
  zwischenzeitlich jemand bei dir gekauft hat und deine Reservierung
  groesser als die neue Restmenge war, kappt HB-Sort die Reservierung
  und zeigt einen Hinweis im Dialog ("X Reservierung(en) angepasst").
  Das XML beruecksichtigt das.
- **Verifizieren schlaegt fehl** (Lot hat noch alte Menge bei BL): du
  hast wahrscheinlich vergessen das XML hochzuladen oder das Update bei
  BL nicht ausgefuehrt. Einfach den Schritt nachholen und nochmal
  klicken.
