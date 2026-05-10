# Tipps & Tricks

## Tastatur-Kuerzel

| Taste | Funktion |
|---|---|
| `Leertaste` | Scan ausloesen (im Sortier-Tab) |
| `Q` | Mehrfach-Scan-Dialog oeffnen |
| `Strg + Z` | Letzten Scan rueckgaengig machen |
| `F1` | Hilfe-Tab oeffnen *(diese Ansicht hier)* |
| `Esc` | Modal-Dialog / Bild-Vergroesserung schliessen |
| `Strg + S` | Sortieren-Tab aktivieren |
| `Strg + L` | Lagerliste-Tab aktivieren |
| `Strg + H` | Verlauf-Tab aktivieren (Hilfe ist auf F1) |
| `Strg + ,` | Einstellungen oeffnen |
| `Strg + B` | Backup jetzt erstellen |
| `Strg + Q` | App beenden (gleicher Pfad wie der Beenden-Button im Header) |
| `Alt + F4` | Windows-Standard: beendet die App. Identisch zu Strg+Q und dem Header-Beenden-Button. |

> **Leertaste-Hinweis:** Das Scannen funktioniert auch wenn du gerade
> auf eine Karte oder einen RadioButton geklickt hast. Nur in
> Eingabefeldern (Suchfeld, Mengen-Feld, Notizen) bleibt die Leertaste
> ein normales Leerzeichen.

> **Brickognize-Vorschlaege uebernehmen** (ab v0.1.17): Die Tasten
> 1, 2 oder 3 uebernehmen den entsprechenden Treffer aus den Top-3-
> Karten unter dem Webcam-Bild. In Eingabe-Feldern bleibt die Taste
> ein normales Zeichen.
>
> Bewusst NICHT belegt (Konflikt mit Standard-WPF in TextBoxen/Lists):
> +/- fuer Anzahl-Felder, Pfeiltasten/Pos1/Ende, Entf-Taste in
> Lagerlisten.

## Lagerliste effizient nutzen

### Suchfeld

Im Tab **Lagerliste** oben gibts ein Suchfeld. Es filtert live nach:

- Name (z.B. "arctic")
- BL-ID (z.B. "arc007")
- Farbe (z.B. "red", "schwarz")
- Lagerfach-Label

Gross-/Kleinschreibung ist egal. Mit X loeschst du die Suche wieder.

### Status-Filter

Direkt neben dem Suchfeld kannst du nach Status filtern:

- **Alle**
- **Wartend** - nur unvollstaendige Figuren
- **Komplett** - nur komplette Figuren
- **Floating** - nur Einzelteile

### Mehrfach-Auswahl + Export

Komplette Figuren und Einzelteile haben jeweils eine Checkbox. Wartende
nicht (du kannst sie nicht exportieren). **Alle Komplette und
Einzelteile auswaehlen** markiert beides auf einmal.

## Klickbare Bilder

Fast jedes Bild in HB-Sort ist klickbar - der Cursor wird zum
Pfeil-Symbol mit Lupe. Klick oeffnet eine **grosse Vorschau** als
Modal-Overlay; Hintergrund-Klick / `Esc` / X schliesst es wieder.

So kannst du z.B. ein Teil im Vergleich mit einem grossen BL-Bild
genauer pruefen, bevor du den Farb-Korrektur-Dialog benutzt.

## "Was kann ich bauen?"-Tab nutzen

Im Sortier-Tab unten rechts ist das Tab **Was kann ich bauen?** -
ein Reverse-Match aus deinem Floating-Pool. Es zeigt dir alle
BL-Minifiguren, deren Teile du schon (teilweise) besitzt. Praktisch
nach einem grossen Sortier-Lauf, um zu sehen, was sich aus deinem
Bestand neu zusammenbauen laesst.

Klick auf eine Karte oeffnet einen Detail-Dialog mit allen Teilen
und Status (vorhanden / fehlt). **Figur anlegen** macht aus dem
Vorschlag direkt eine wartende Figur und konsumiert die passenden
Floating-Parts.

## Erkennungsprobleme?

Wenn Brickognize nicht erkennt:

- **Licht** - gleichmaessig, nicht zu hell, keine harten Schatten.
- **Hintergrund** - moeglichst neutral, einfarbig (weisses Papier,
  graue Pappe).
- **Abstand** - Teil sollte einen Grossteil des Frames ausfuellen,
  aber nicht angeschnitten sein.
- **Winkel** - bei Minifiguren frontal; bei Teilen die Seite, die am
  charakteristischsten ist.
- **Score-Schwellen** in den Einstellungen senken, wenn du sehr
  oft "nicht erkannt" bekommst (Vorsicht: dann gibts mehr
  Fehl-Treffer).

## Schnell-Workflow

Wenn du eine ganze Box voll Teile sortierst:

1. **Lagerfaecher** vorher anlegen (Bulk).
2. **Brickognize-Status** in der Status-Leiste pruefen (gruene LED).
3. Teile-Stapel in Reichweite legen.
4. Ein Teil hinhalten -> **Leertaste** -> Match-Logik macht den Rest.
5. Bei Floating-Parts den vorgeschlagenen Bin uebernehmen (HB-Sort
   schlaegt automatisch das Fach vor, in dem dasselbe Teil schon
   liegt).
6. Zwischendurch im Tab **Was kann ich bauen?** neue Figuren
   anlegen, sobald genug Teile da sind.

So kommst du auf 50+ Scans pro Stunde, ohne die Maus anzufassen.
