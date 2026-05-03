# Sortier-Workflow

Im Tab **Sortieren** passiert die eigentliche Arbeit. HB-Sort kennt
zwei Modi:

- **Figur scannen** - eine komplette Minifigur, die du als wartend
  aufnehmen willst.
- **Einzelteil scannen** - ein einzelnes LEGO-Teil, das einer
  bestehenden wartenden Figur fehlt oder als Floating-Part im Pool
  landet.

HB-Sort erkennt automatisch, was du vor die Kamera haelst.

## Eine Figur scannen

1. Lege die Minifigur vor die Webcam.
2. Druecke die **Leertaste**.
3. Das Bild friert kurz ein und wird an Brickognize geschickt.
4. Je nach Erkennungs-Score:
   - **Sehr sicher (>= 0.95)** - die Figur wird direkt uebernommen.
   - **Mittel (0.7 - 0.95)** - du bekommst die **Top-3-Karten**
     angezeigt; klick die richtige an.
   - **Unsicher (< 0.7)** - HB-Sort sagt "nicht erkannt"; versuche es
     nochmal mit besserem Licht oder anderem Winkel.
5. Nach Auswahl siehst du:
   - Das BL-Bild der Figur
   - Die komplette Teileliste mit BL-Part-Nummer, Farbe und Anzahl
   - Ein **Lagerfach-Dropdown** (Default: erstes freies Fach)
   - Ein optionales **Notiz-Feld**
6. Klick **In Fach legen**. Die Figur landet als *Wartend* in deinem
   Lagerfach. Floating-Parts, die zur Figur passen, werden automatisch
   konsumiert (Reverse-Match) - falls bereits alle Teile da sind,
   ist die Figur sofort komplett.

> Du kannst direkt im Pending-Block einzelne Teile als "vorhanden"
> markieren oder per **Aus Fach**-Button (siehe unten) einen
> Floating-Part aus einem anderen Fach uebernehmen.

## Ein Einzelteil scannen

1. Lege das einzelne Teil vor die Webcam.
2. Druecke die **Leertaste**.
3. Brickognize erkennt Teil + Farbe.
4. HB-Sort zeigt dir das Teil mit Farb-Vorschlag. Stimmt die Farbe
   nicht? Korrigiere sie ueber das Dropdown.
5. **Matching:** HB-Sort prueft alle wartenden Figuren, die genau
   dieses Teil in dieser Farbe brauchen.
   - **Genau ein Treffer:** das Teil wird direkt der Figur zugewiesen.
     Ist die Figur damit komplett, oeffnet sich der Komplettierungs-
     Dialog.
   - **Mehrere Treffer:** du waehlst die passende Figur aus.
   - **Kein Treffer:** du kannst das Teil als **Einzelteil** in einem
     Fach ablegen (Floating-Part).

## Aus Fach uebernehmen

Beim Pending-Block einer noch nicht gespeicherten Figur taucht hinter
manchen Teilen ein Button **Aus Fach** auf - dann liegt dieses Teil
schon als Floating-Part in einem anderen Lagerfach.

Klick auf den Button:

- Reduziert den Floating-Part um 1 (loescht ihn, wenn 0 erreicht).
- Erhoeht die "Gesammelt"-Anzahl der Pending-Figur um 1.
- Loggt einen Audit-Trail-Eintrag (Quell-Fach + Ziel-Figur).

So sammelt HB-Sort automatisch Teile fuer neue Figuren ein, die schon
auf Lager liegen.

## Wartende Figuren komplettieren

Klick im Tab *Sortieren* unten rechts (oder im Detail-Tab *Wartende-
Detail*) auf eine wartende Figur, dann auf den **Pending-Klick** im
Summary-Dialog. HB-Sort prueft, ob alle Teile da sind, und markiert
die Figur ggf. als **Complete** (gruener Haken).

Beim ersten Komplettierungs-Event:

- Toast-Meldung "Figur 'X' ist komplett!"
- DailyStats werden hochgezaehlt
- Falls du in den Einstellungen *Auto-Load* aktiviert hast: Preise
  werden geladen und du siehst eine Verkaufsempfehlung
  (siehe Kapitel *Export & Verkauf*).
