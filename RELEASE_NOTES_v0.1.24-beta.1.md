# v0.1.24-beta.1 Release Notes

## Hauptfeatures

### Modal-Pattern für Post-Save-Bewegungen

Jede Operation, die physische Bin-zu-Bin-Bewegungen erfordert (Wizard-
Speichern, Bulk-Move, Dismantle, Persist-Pending, BuildSuggestion, Move
einer komplettierten Figur), zeigt jetzt ein Modal mit Take/Put-Sektionen,
das aktiv bestätigt werden muss. Kein Auto-Dismiss — du siehst was wo hin
muss und drückst Enter/OK wenn du fertig sortiert hast.

Sechs Triggers sind verkabelt:
1. Wizard-Stufe-2-Save (neue Figur anlegen)
2. Bulk-Move aus Lagerliste
3. DismantleWizard-Confirm (Figur zerlegen)
4. MinifigSummary-Move (komplette/wartende Figur verschieben)
5. BuildSuggestionDetail-Save
6. PartLookupView-StoreFloating (Einzelteil lagern)

### Wizard 2-stufig für neue Figuren

Brickognize-Scan einer neuen Figur öffnet jetzt einen 2-stufigen Wizard
(`CollectMinifigWizardDialog`):

- **Stufe 1** zeigt Required-Parts in 3 Status-Gruppen:
  - **TRIGGER** (gerade gescannt) — Goldenrod
  - **IM LAGER** (Reverse-Match, wird konsumiert) — Grün
  - **FEHLT** (keine Quelle) — Grau, mit Manuell-Markieren-Checkbox
- **Stufe 2** ist Lagerfach-Auswahl mit Bewegungs-Hinweis-Block oberhalb
  des Speichern-Buttons. Du siehst *vor* dem Speichern wieviele Bewegungen
  passieren werden, und nach dem Speichern siehst du *was genau* wo hin
  muss (Post-Save-Modal).

Strg+Z springt von Stufe 2 zurück zu Stufe 1; State bleibt erhalten.

### Reverse-Match nur auf expliziten Wunsch

Im MinifigDetailView-Pfad (wartende Figur weiterbearbeiten) werden Teile
aus dem Lager **nur noch konsumiert, wenn du explizit "Aus Fach" klickst**.
Vorher wurde automatisch reverse-matched — verwirrend, wenn man die Figur
nur ablegen wollte. Service-Pfad parametrisiert über
`consumePartsFromFloating`-Flag.

### Combobox-Suffix mit Belegungs-Counts

Alle Lagerfach-Comboboxen (Wizard, MinifigDetailView, PartLookup, Dismantle,
MinifigSummary, BuildSuggestion) zeigen jetzt Suffix wie
"(2 wartend, 3 fertig)" oder "(5 Einzelteile)" — du siehst auf einen Blick,
was im Bin liegt. Plus Limit-Filter aus Settings (`MaxWaitingFiguresPerBin`,
`MaxCompleteFiguresPerBin`): volle Bins erscheinen nicht mehr im Dropdown.

### Manuell-Markieren-Workflow

Beim Anlegen einer Figur kannst du Required-Parts, die NICHT im Lager sind,
aber physisch vorhanden (z.B. in deiner Hand), als "manuell vorhanden"
markieren. Wird als gesammelt gezählt, kein FloatingPart-Verbrauch.
Praktisch wenn du Restteile aus einem zerlegten Set sortierst.

## Technische Verbesserungen

- 589/589 Tests grün (vorher 564 vor v0.1.24 Phase 1 — 25 neue Tests)
- 0 Build-Warnings
- Service-Pfade parametrisiert (`consumePartsFromFloating`-Flag) für
  Workflow-spezifisches Verhalten (User-Wunsch 2026-05-16)
- Bilder-Loading parallelisiert (Bulk-Move + DismantleWizard) — ~10x
  schneller bei 10+ Items
- Tote Dialog-Klassen entfernt: `SupersetsDialog` + `SupersetsDialogViewModel`
- `BinPickerDialog` in eigene Datei extrahiert (vorher als `internal static
  class` in `SupersetsDialog.xaml.cs` versteckt)
- Dialog-Konvention-Audit: 6 kosmetische Inkonsistenzen gefixt
  (5x `FontSize` auf `DialogHeaderFontSize=20` harmonisiert,
  1x `IsDefault` an BSX-Export-Button ergänzt)
- `DispatcherPriority.Render`-Wrapper für reaktive Modal-Open-Latenz
- Window.Resources-Position-Lerneffekt dokumentiert (Phase 2b-Hotfix)

## Bekannte offene Punkte (v0.1.24-beta.2 / v0.1.25)

- **Klick-Optimierung Anlege-Workflow** (Design-Schema D9) — wartet auf
  Praxis-Erfahrung mit Wizard
- **Dark-Mode für Status-Brushes** — Beta-3 oder v0.1.25
- **BL-Inventar Beta 2 / Beta 3** — Komplettierungs-Integration + Mass-
  Update-Export (Beta-2/3)
- **Single-Mode-Cleanup OPEN-18** — Service-Layer-Audit Bulk vs. Single
  (v0.1.25)
- **Performance-Wurzelfixes B+E** — `RaiseDataChanged` raus aus Service-
  Layer, `RecalcBinKindAsync`-Context-Piggyback (v0.1.25)
- **Kategorie-Sperre-Diagnose** — wartet auf 2-3 protokollierte
  Praxis-Vorfälle (Befund 3 aus v0.1.23-beta.1)
