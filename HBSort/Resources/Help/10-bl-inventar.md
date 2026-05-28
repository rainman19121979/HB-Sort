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

Im Figur-Detail-Dialog: den **Haken** am Teil entfernen. Das macht:

- Alle BL-Reservierungen fuer dieses Teil zurueck (LIFO — neueste
  zuerst).
- Setzt `QuantityCollected` zurueck auf 0 (wie bisher beim Haken-
  Entfernen).
- Loggt einen Release-ScanEvent pro freigegebener Reservierung.

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

## Tab "Sortieren" — BL-Toast

Wenn du ein Einzelteil scannst und es **keiner wartenden Figur** in
deinem Lager zugeordnet werden kann, aber in deinem **BL-Shop**
liegt, kommt ein informativer Toast:

> *Teil nicht im HBSort-Lager gesucht, aber 12× Neu / 3× Gebraucht
> in deinem BrickLink-Shop verfuegbar.*

Das ist **nur ein Hinweis** — es wird nichts automatisch reserviert.
Das gescannte Teil wird ganz normal als Floating-Part eingelagert.

## Was Phase 4 bringt (noch nicht implementiert)

Aktuell endet der BL-Inventar-Workflow beim Reservieren. **Phase 4
(geplant fuer v0.1.25 oder spaeter)** soll:

- BSX/Mass-Update-Export der Reservierungen nach BL: `Quantity` im
  Lot wird reduziert, `ReservedQuantity` zurueckgesetzt.
- Der naechste Sync holt dann den reduzierten BL-Stand und alles
  passt wieder zusammen.

Bis Phase 4 muss der User Reservierungen entweder manuell im Summary-
Dialog aufheben oder die Figur aufgeben (was v0.1.24-beta.10 korrekt
freigibt).
