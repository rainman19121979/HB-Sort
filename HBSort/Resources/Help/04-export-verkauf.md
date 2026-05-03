# Export & Verkauf

HB-Sort kennt zwei Export-Formate, mit denen du deinen Bestand in
BrickStore oder als Wanted-List in BrickLink uebernehmen kannst.

## BSX-Export fuer BrickStore

**BSX** ist das native XML-Format von BrickStore. Du kannst damit
komplette Figuren und Einzelteile zusammen in eine Datei exportieren
und in BrickStore aufnehmen, um z.B. einen Verkauf vorzubereiten.

### So gehts

1. Tab **Lagerliste** oeffnen.
2. Rechts neben jedem Eintrag ist eine **Checkbox** (nur bei
   kompletten Figuren und Einzelteilen, nicht bei wartenden).
3. Markiere die gewuenschten Eintraege oder klick **Alle Komplette
   und Einzelteile auswaehlen**.
4. Klick **Exportieren (n)** in der Action-Bar oben.
5. Im Dialog:
   - Optional: Bemerkung eintragen (geht ins Remarks-Feld jeder
     ITEM-Zeile, Default "HBSort {Datum}").
   - Default-Ordner pruefen / aendern (wird als Default fuer naechsten
     Export gespeichert).
   - **Speichern**.
6. Nach erfolgreichem Schreiben siehst du den **Cleanup-Block**:
   - Wieviele Faecher waeren leer, wenn du die exportierten Figuren
     entfernst?
   - Optional: Figuren entfernen / Faecher freigeben.

> **Tipp:** Den Default-Ordner kannst du auch in den Einstellungen
> unter Tab *Export* fest setzen.

### Was passiert beim Cleanup?

- Komplette Figuren werden geloescht (Floating-Parts, die ueber
  Origin-Verbindung mit dieser Figur entstanden sind, bleiben
  erhalten - sie werden nur entkoppelt).
- Einzelteile werden geloescht; jeder Eintrag wird mit Quell-Fach,
  Part-No und Quantity in der Scan-Historie geloggt
  (Audit-Trail).
- Faecher, die danach leer waeren, kannst du freigeben - dann sind
  sie wieder "Frei" und stehen fuer neue Figuren bereit.

## Wanted-List-Export fuer BrickLink

Eine **Wanted-List** ist eine BrickLink-XML-Datei, die du in dein
BrickLink-Konto importieren kannst, damit BL automatisch nach
fehlenden Teilen sucht.

### So gehts

1. Tab **Lagerliste** oeffnen.
2. Klick **Wanted-List exportieren** in der Header-Leiste.
3. Im Dialog:
   - **Modus** waehlen:
     - *Alle wartenden Figuren in eine Datei* - eine grosse Liste mit
       allen fehlenden Teilen aller wartenden Figuren.
     - *Pro Figur eine eigene Datei* - getrennte Listen, falls du sie
       einzeln in BL anlegen willst.
   - Default-Ordner pruefen / aendern.
4. **Speichern**.

Die XML enthaelt pro fehlendem Teil eine `INVENTORY/ITEM`-Zeile mit
`ITEMTYPE=P`, `ITEMID`, `COLOR` und `MINQTY` - genau wie BL es
erwartet.

## Preise & Verkaufsempfehlung

HB-Sort kann dir helfen zu entscheiden, ob es sich mehr lohnt, eine
komplette Figur zu verkaufen oder die Einzelteile.

### Aktivierung

Einstellungen -> Tab *Preise*:

1. **Provider** waehlen - aktuell *BrickLink-API* (oder *None*, wenn
   du keine Preise willst).
2. **Filter** setzen - GuideType (sold/stock), Condition (Used/New),
   Region, Country, Currency.
3. **Korrekturen** - z.B. -10% Minifig / -15% Parts. So beruecksichtigst
   du Verkaufsgebuehren oder dein realistisches Verkaufsniveau.
4. **Auto-Load** aktivieren, wenn HB-Sort beim Komplett-Werden direkt
   die Preise laden soll.

### Live-Anzeige im Sortier-Tab

In der oberen rechten Box des Sortier-Tabs siehst du fuer eine
gerade gescannte Figur (Pending-Minifig):

- **Komplett-Figur-Preis** (Avg / Min / Max + Anzahl Listings)
- **Subset-Preise** als Liste mit Summe
- **Gruener Empfehlungs-Banner** mit Hinweis "Komplett verkaufen
  lohnt sich mehr (+X,XX EUR)" / "Einzelteile lohnen sich mehr" /
  "etwa gleich" (10%-Schwelle)

Per **Refresh-Button (↻)** loeschst du den Cache und ziehst die Daten
neu von der API.

### Cache

Preis-Daten werden im **bl_prices**-Cache mit Stale-While-Revalidate
gehalten:

- Cache-Hit + frisch -> sofort.
- Cache-Hit + stale -> alter Wert wird sofort angezeigt + Hintergrund-
  Refresh.
- Cache-Miss -> Live-Call.

TTL stellst du in den Einstellungen ein (Default 90 Tage fuer
Minifigs und Einzelteile). Mit *Preis-Cache leeren* in den
Einstellungen kannst du alle Preis-Eintraege entfernen.
