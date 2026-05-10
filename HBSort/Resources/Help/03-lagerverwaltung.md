# Lagerverwaltung

## Was sind Lagerfaecher?

Ein **Lagerfach** in HB-Sort entspricht einer physischen Box, Schale
oder Schublade bei dir zu Hause. Du legst sie in den Einstellungen
(Tab *Lagerfaecher*) an und beschriftest deine echten Faecher
identisch.

Ein Fach kann enthalten:

- Eine oder mehrere **wartende Figuren** (mit ihren bisher
  gesammelten Teilen)
- **Komplette Figuren**, die du ausexportieren willst
- **Einzelteile** (Floating-Parts), die noch keiner Figur zugeordnet
  sind

Faecher haben einen Status:

- **Frei** - leer, kein Inhalt zugeordnet.
- **Belegt** - mindestens eine wartende Figur, komplette Figur oder
  ein Floating-Part liegt drin.

> **Wichtig:** Ein Fach bleibt belegt, auch wenn alle Figuren darin
> *Complete* sind. Nur ein expliziter Klick **Fach leeren** gibt das
> Fach wieder frei (z.B. nach dem BSX-Export).

### Faecher in einem Rutsch anlegen

In den Einstellungen unter *Lagerfaecher* gibt es **Bulk anlegen**.
Du kannst eine Serie wie "Box001"-"Box050" oder "SchaleA"-"SchaleZ"
auf einmal erzeugen. Bei Buchstaben wird nach Z auf AA, AB ...
fortgesetzt.

**Praefix-Format** (ab v0.1.14): Default ist `Box` (kein automatisches
Leerzeichen am Ende). Wenn du das alte Format `Box 001` mit Leerzeichen
willst, einfach `Box ` mit Leerzeichen am Ende ins Praefix-Feld tippen.

## Wartende vs. komplette Figuren

| Status | Bedeutung |
|---|---|
| **Wartend** | Mindestens ein Teil fehlt noch. Du sammelst, bis alle Teile da sind. |
| **Komplett** | Alle Teile vorhanden. Wird mit gruenem Haken markiert. Kannst du via BSX-Export weitergeben. |

Komplette Figuren werden in der **Lagerliste** in der gruenen
Sektion pro Fach angezeigt. Wartende Figuren stehen darueber mit
Fortschrittsbalken (z.B. "3/7 Teile vorhanden").

## Einzelteile (Floating-Parts)

Wenn du ein Teil scannst, das zu keiner aktuell wartenden Figur
passt, kannst du es als **Floating-Part** in einem Fach lagern. Die
Idee: spaeter kommt vielleicht eine Figur, die genau dieses Teil
braucht - und Reverse-Match findet es automatisch.

HB-Sort schlaegt beim Lagern automatisch das passende Fach vor: liegt
dasselbe Teil in derselben Farbe schon in einem Fach, wird das Fach
vorausgewaehlt - so wachsen Stapel weiter, statt sich auf mehrere
Faecher zu zerstreuen.

## Was kann ich bauen?

Im Sortier-Tab unten rechts gibt es das Tab **Was kann ich bauen?**.
Es zeigt dir alle BL-Minifiguren, deren Teile du als Floating-Parts
schon (teilweise) im Lager hast - sortiert nach Match-Quote
(absteigend).

Klick auf einen Vorschlag oeffnet einen Dialog mit:

- Bild + Name der Figur
- Komplette Teileliste mit Status (vorhanden in Box X / fehlt)
- Lagerfach-Dropdown
- Notizfeld

Klick **Figur anlegen** und HB-Sort:

- Legt die wartende Figur im gewaehlten Fach an
- Konsumiert direkt alle passenden Floating-Parts (Reverse-Match)
- Markiert sie ggf. als komplett, wenn alle Teile schon da sind

So machst du aus deinem Floating-Pool effizient neue Figuren.

## Lagerliste-Tab

Der Tab **Lagerliste** (oben in der Header-Leiste) zeigt deinen
gesamten Bestand:

- **Suchfeld** oben - Filter nach Name, BL-ID, Farbe oder Lagerfach-
  Label (ab v0.1.16 auch Lagerfach-Suche).
- **Status-Filter** - Wartend / Komplett / Floating / Alle
- **Klick auf Bilder** - oeffnet eine grosse Vorschau (siehe Kapitel
  *Tipps & Tricks*)
- **Doppelklick auf eine Zeile** (ab v0.1.16) - oeffnet die Detail-
  Ansicht (gleicher Pfad wie der "Details"-Button).
- **Markieren via Checkbox** (ab v0.1.16 auch fuer wartende Figuren) -
  mehrere Items gleichzeitig adressieren.
- **Komplette Figuren + Einzelteile** koennen markiert und in einem
  Rutsch via BSX exportiert werden (siehe Kapitel *Export & Verkauf*).

### Bulk-Aktionen (ab v0.1.16)

In der Action-Bar ueber der Liste:

- **Alle / Keine** - markiert/demarkiert alle sichtbaren Items
  (respektiert die Filter).
- **Verschieben in...** - oeffnet einen Bin-Picker, alle markierten
  Items wandern ins gewaehlte Fach. Strg+Z macht den Verschub
  rueckgaengig (bei Figuren - Einzelteile muessten manuell zurueck-
  geschoben werden). Ab v0.1.19-beta.4 erscheint nach erfolgreichem
  Verschieben ein Sammel-Popup mit pro Item einer "Lege X in
  {Ziel-Fach}"-Anweisung - auch im Lagerlist-Tab sichtbar.
- **Loeschen** (rot) - loescht alle markierten Items dauerhaft. Strg+Z
  oder der Verlauf-Tab macht das Item-fuer-Item rueckgaengig.
- **Exportieren** (gruen) - exportiert die markierten kompletten
  Figuren + Einzelteile als BSX. Wartende werden dabei uebersprungen
  und im Toast vermerkt.
- **Entf-Taste** auf der Liste loescht die Selektion (Bulk wenn
  Checkboxen markiert sind, sonst die fokussierte Zeile).

### Counter-Anzeige

In der Action-Bar siehst du *"X markiert"* oder *"X markiert
(Y exportierbar)"*. Der zweite Wert taucht nur auf wenn wartende
Figuren mit-markiert sind - sie werden zwar bei Loeschen/Verschieben
behandelt, aber nicht beim Export.
