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
Du kannst eine Serie wie "Box 001"-"Box 050" oder "Schale A"-"Schale Z"
auf einmal erzeugen. Bei Buchstaben wird nach Z auf AA, AB ...
fortgesetzt.

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

- **Suchfeld** oben - Filter nach Name, BL-ID oder Farbe
- **Status-Filter** - Wartend / Komplett / Floating / Alle
- **Klick auf Bilder** - oeffnet eine grosse Vorschau (siehe Kapitel
  *Tipps & Tricks*)
- **Komplette Figuren + Einzelteile** koennen markiert und in einem
  Rutsch via BSX exportiert werden (siehe Kapitel *Export & Verkauf*)
