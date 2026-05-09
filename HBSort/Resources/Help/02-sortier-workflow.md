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

> **Default-Auswahl beim Scannen** (ab v0.1.16): in den Einstellungen
> → Erkennung kannst du umschalten ob Teile beim Scan vorab abgehakt
> sind ("ich klicke ab was fehlt") oder nicht ("ich klicke an was ich
> habe", Default).

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

### Smart-Storage-Suggestion

Wenn du ein Einzelteil als Floating-Part lagerst und dasselbe Teil in
derselben Farbe schon in einem Fach liegt, schlaegt HB-Sort dieses
Fach automatisch im Lagerfach-Dropdown vor und zeigt einen Hinweis:

> 📦 *Liegt schon in Box 003 (3x)*

So wachsen Stapel des gleichen Teils statt sich ueber mehrere Faecher
zu verteilen. Die Quantity wird automatisch hochgezaehlt - du legst
also kein zweites Floating-Part-Item an, sondern stapelst auf dem
bestehenden weiter.

Du kannst trotzdem manuell ein anderes Fach waehlen, wenn das Teil
woanders hin soll.

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

## Beim Zerlegen: wartende Figuren komplettieren

Beim Zerlegen einer kompletten Figur (Klick auf **Zerlegen** im Detail-
Popup) erkennt HB-Sort jetzt automatisch, ob ein frei werdendes Teil zu
einer wartenden Figur passt. Der Zerlegen-Wizard zeigt pro Teil eine
zusaetzliche Auswahl (nur wenn Treffer existieren):

- ⚪ **in Lager legen** (Default) - Teil wandert als FloatingPart in
  das gewaehlte Fach (bisheriges Verhalten).
- ⚪ **zuordnen zu wartender Figur** - Teil wird direkt einer
  wartenden Figur als gesammelt gebucht.
  - Bei genau einem Treffer: Klartext-Anzeige *"Officer (7/7) [Box 003]"*
    mit Vorschau des Fortschritts nach dem Zuordnen.
  - Bei mehreren Treffern: Dropdown - du waehlst die passende Figur aus.

Falls die wartende Figur durch die Zuordnung **komplett** wird, taucht
direkt nach dem Zerlegen ein Toast *"Figur 'X' ist jetzt komplett!"* auf
und der Status wechselt von Wartend auf Komplett.

Du kannst pro Teil unterschiedlich entscheiden: ein Teil ins Lager,
das naechste einer wartenden Figur zuordnen, das dritte verwerfen
(Checkbox aus). Die Auswahl-Spalten sind disabled wenn die Checkbox
links nicht gesetzt ist.

## Hinweis zu Brickognize-ToS

Beim Klick auf **Scannen** (oder Druecken der Leertaste) wird das
Webcam-Bild an die Brickognize-API geschickt. Mit der Nutzung
akzeptierst du die Brickognize-Terms-of-Service:

- Persoenliche, nicht-kommerzielle Nutzung
- Hochgeladene Bilder werden gemaess Brickognize-ToS Section 8
  verarbeitet (u.a. Modell-Training)
- HB-Sort ist nicht von Brickognize betrieben oder finanziert

Details siehe README oder <https://brickognize.com> -> *Legal* /
*Terms of Service*. Wenn du HB-Sort nicht mit Webcam-Erkennung
nutzen willst, kannst du Items auch manuell anlegen.

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
