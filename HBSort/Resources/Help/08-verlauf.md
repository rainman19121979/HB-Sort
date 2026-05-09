# Verlauf & Rueckgaengig

Ab v0.1.16 hat HB-Sort einen eigenen **Verlauf**-Tab und eine
zentrale Rueckgaengig-Funktion (Strg+Z).

## Verlauf-Tab

Der Tab **Verlauf** (oben in der Header-Leiste) zeigt die letzten
~200 Aktionen, neueste zuerst:

- **Datum/Uhrzeit** der Aktion
- **Aktion** (Geloescht, Verschoben, Komplettiert, Fach freigegeben,
  Fach angelegt, Scan, Rueckgaengig)
- **Beschreibung** mit Kontext (Item-Name, Bin-Label etc.)
- **Status**: "Rueckgaengigmachbar" oder "Rueckgaengig {Datum}"
- **Rueckgaengig-Button** pro Zeile (sichtbar nur bei undobaren
  Aktionen die noch nicht rueckgaengig gemacht wurden)

### Filter

- **Suche** - filtert nach Beschreibungs-Text
- **Typ** - alle / nur eine bestimmte Aktions-Art
- **Datum-Range** - Von/Bis fuer einen Zeitraum

## Strg+Z (Globaler Shortcut)

Strg+Z macht ueberall in der App die letzte rueckgaengigmachbare
Aktion rueckgaengig - egal in welchem Tab du bist.

- Toast bei Erfolg: *"Rueckgaengig: ..."*
- Toast bei leerer Historie: *"Keine rueckgaengigmachbare Aktion vorhanden."*
- **Funktioniert NICHT in Texteingabe-Feldern**: dort hat der WPF-
  Standard-Editor seinen eigenen Strg+Z, damit du z.B. eine Notiz
  weiter editieren kannst.

## Welche Aktionen sind rueckgaengig?

| Aktion | Rueckgaengig? | Was passiert beim Undo |
|---|---|---|
| Figur loeschen | ja | Figur wird mit allen RequiredParts wieder angelegt |
| Einzelteil loeschen | ja | FloatingPart wird im urspruenglichen Fach wieder angelegt |
| Figur verschieben (Move) | ja | Figur wandert ins alte Fach zurueck |
| Lagerfach freigegeben | ja | FreedAt wird auf null zurueckgesetzt |
| Lagerfach angelegt | ja | Fach wird geloescht (nur wenn noch leer) |
| Manuelles Komplettieren | ja | QuantityCollected + Status zurueck |
| BSX-Export | nein | Backup nutzen falls noetig |
| BL-Daten-Import | nein | Backup nutzen falls noetig |
| Scan + Anlegen | nein | manuell die angelegte Figur loeschen |

## Doppel-Undo verhindert

Eine Aktion kann nur einmal rueckgaengig gemacht werden. Versuchst
du es ein zweites Mal, sagt HB-Sort *"Aktion wurde bereits
rueckgaengig gemacht"*. Das Undo-Event selbst wird im Verlauf als
"Rueckgaengig" geloggt - so bleibt der Audit-Trail vollstaendig.

## Was tun wenn Undo nicht reicht?

Wenn du z.B. einen ganzen BSX-Export rueckgaengig machen willst oder
einen kaputten BL-Import: nutze ein **Backup** (Settings → Backups).
Die Wiederherstellung setzt deine ganze DB auf einen frueheren Stand
zurueck.
