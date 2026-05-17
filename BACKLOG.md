# HBSort Backlog

Lebendes Backlog-Dokument. Items werden bei Erledigung nicht entfernt sondern
mit Versions-Tag und Datum als "erledigt" markiert. Spaeter koennen erledigte
Items archiviert werden.

**Status-Marker:**
- 📋 geplant - im naechsten Iteration eingeplant
- 🟡 in-arbeit - aktuell in Arbeit
- ✅ erledigt - mit Versions-Tag und Datum
- 💭 Brainstorming - Konzept noch nicht final
- ⏸️ pausiert - wartet auf externes (z.B. ToS-Antwort)

---

## v0.1.20 - geplant

### Performance
- 📋 **Splash-Screen Ladeanzeige reparieren** - aktuell keine sichtbare Anzeige beim Start.
  Aufwand: ~1h
- 📋 **Settings öffnen/schließen schneller** - User-Befund: zu lange Reaktionszeit.
  Aufwand: ~1.5h (mit Profiling)
- 📋 **Popups öffnen schneller** - User-Befund + N+1-Hotspots aus Cleanup-Bericht.
  Betrifft: CollectMinifigSelectionViewModel (50-200ms), DismantleWizardViewModel
  (200-500ms), BuildSuggestionDetailViewModel (30-100ms).
  Aufwand: ~3h
- ✅ **ScanViewModel : IDisposable** - erledigt in v0.1.20-beta.5 (2026-05-12).
  FrameReceived + PendingMinifig.PropertyChanged + BinInstructionTimer + LookupCts
  werden im Dispose freigegeben; (Services as IDisposable)?.Dispose() in
  App.OnExit ruft den Pfad automatisch beim Shutdown.

### Features
- 📋 **Vollständiges Undo-System für alle DB-Mutationen**
  Aktuell undo-fähig: Move (Minifig + Floating), Delete (Minifig).
  Fehlt: FloatingPart anlegen, Direkt-Zerlegen, Reverse-Match-Konsumieren,
  BuildSuggestion-Anlegen, Bin-Operationen, Pending-Persist mit Reverse-Match.
  Aufwand: ~6-8h (eigene Sub-Iteration)
- 📋 **Bulk-Bin-Aktionen im Settings-Tab Lagerfächer**
  - Markierte löschen (nur leere Bins)
  - Markierte leeren (Inhalt raus, mit Vorschau)
  - Sicherheits-Bestätigungsdialog
  Aufwand: ~3h
- 📋 **Doppelt-Export-Tracking (Wanted-List Snapshot)**
  Snapshot vom letzten Export, Delta-Berechnung beim nächsten, "Reset-Tracking"-Button.
  Aufwand: ~3h

### Doku
- 📋 **GitHub Wiki befüllen**
  9 Hilfe-Dateien als Wiki-Pages spiegeln, Home.md mit Index, Sidebar mit Navigation.
  Aufwand: ~2h
- 📋 **Beta-Tester-Onboarding-Anleitung** im Wiki
  Aufwand: ~1h

### Brickognize-Integration (aus Mail von Piotr 2026-05-09)
- ✅ **Rate-Limit-Throttle (5 RPS)** in BrickognizeClient - erledigt in
  v0.1.20-beta.5 (2026-05-12). SemaphoreSlim + Timestamp-Throttle vor jedem
  Predict-Call; statisch damit alle Client-Instanzen denselben Token-Bucket
  teilen.
- 📋 **/feedback-Endpoint nutzen** - User-Feedback "korrekt/falsch" an Brickognize senden
  Optional, hilft Brickognize-Modell-Genauigkeit.
  Aufwand: ~1h
- 📋 **/feedback/color-Endpoint** - Farb-Feedback (laut Piotr besonders nützlich)
  Aufwand: ~1h

### Cleanup (aus Cleanup-Bericht)
- 📋 **DismantleWizard.LoadAsync vs LoadFromPendingAsync** - Helper extrahieren (~50-60 dupl. Zeilen)
  Aufwand: ~1.5h
- 📋 **BinInstructionOverlay + BinInstructionGroupOverlay** zu einem UserControl mit Mode-Switch
  Aufwand: ~2h

### Konsistenz-Drifts
- 📋 **CollectMinifigSelectionViewModel Volle-Fächer-Banner**
  Block K wurde dort nicht ergänzt (nicht user-gemeldet, aber Konsistenz-Lücke).
  Aufwand: ~30min

---

## v0.2.0 (oder später) - größere Features

### BL-Inventar-Integration
- 💭 **Konzept-Dokument: BL-Inventar einlesen + komplettieren + Mass-Update-Export**

  Workflow:
  - Inventar via BL-API holen (BricklinkSharp ist schon integriert)
  - Tab "Mein BL-Inventar" mit Liste aller Lots
  - Wartende Figuren komplettieren aus HBSort-Lager ODER BL-Inventar
  - User wählt Quelle pro Teil
  - Korrektur-Tracking: Adjustments werden in DB gesammelt
  - Export im BL-Mass-Update-Format (XML mit LOTID + QTY-Delta)
  - User uploaded XML manuell auf https://www.bricklink.com/inventoryUpdate.asp

  Phasen:
  - Phase 1: BSX-Import + Inventar-Tab (kein API)
  - Phase 2: API-Sync (BL OAuth - schon konfiguriert)
  - Phase 3: Komplettieren-Integration + Adjustment-Tracking
  - Phase 4: Mass-Update-Export

  Aufwand: ~11-13h (mit existierender BL-API-Infrastruktur)

### BL-CategoryId-Refactor (Variante B)
- 💭 **BL-CategoryId statt Brickognize-Heuristik beim Zerlegen**
  Aktuell Variante A (PartName-Heuristik) - "Unbekannt"-Fallback bei kommalosen Items.
  Variante B wäre exakter via bl_items.category_id, aber komplexerer Refactor.
  Voraussetzung: BL-CategoryId ↔ Brickognize-String-Mapping definieren.
  Aufwand: ~2-3h + Designdurchgang

---

## Backlog (ohne Iterations-Zuordnung)

### UI/UX
- 📋 **Wertabschätzung Statistik-Dashboard** - BL-Cache-Aggregation pro Bin/Figur
- 📋 **Spalten-Persistenz Lagerliste** - Sortierung + Spaltenbreite merken
- 📋 **Notiz-Feld auch für FloatingParts** - aktuell nur für TrackedMinifigs
- 📋 **Statistik mit Charts/Diagrammen** - Zeitverlauf, Top-Kategorien
- 📋 **Layout-Verhältnisse Sortier-Tab tunen** - Splitter-Defaults
- 📋 **Dunkles Theme** - System-Theme respektieren
- 📋 **User-konfigurierbare Hotkeys** - aktuell hardcoded

### System
- 📋 **DB-Ordner verschiebbar** - per Setting konfigurieren
- 📋 **Channel-Switching Settings (Stable/Beta)** - Velopack-Setup
- 📋 **SuggestBinForCompleteMinifig auch im DismantleWizard** - aktuell nur im Sortier-Tab
- 📋 **Test-Harness fuer ScanViewModel + SettingsViewModel** -
  beide haben 16+ DI-Parameter, kein existierender Test-Setup.
  Eigene Test-Infrastruktur-Iteration noetig damit VM-Tests effizient
  geschrieben werden koennen (entdeckt in v0.1.22-beta.4).
- 📋 **Tot-Code SwitchCameraAsync entfernen** - `ScanViewModel.cs:1759`,
  Command wird nirgends mehr aufgerufen (entdeckt in v0.1.22-beta.4).
- ✅ **BinPickerDialog in eigene Datei extrahieren** — erledigt in
  v0.1.24-beta.1 Phase 3 (2026-05-17). `HBSort/Views/BinPickerDialog.cs`
  enthaelt jetzt den `internal static class`-Helper; wird von
  `InventoryListView.xaml.cs` (Bulk-Move) weiterhin produktiv genutzt.
  Datei extrahiert, statt komplett zu loeschen, weil Bulk-Move einen
  schlanken Picker braucht (kein 2-stufiger Wizard).
- ✅ **SupersetsDialogViewModel.GetAllBinsAsync latenter Filter-Bug** —
  erledigt in v0.1.24-beta.1 Phase 3 (2026-05-17) via Komplett-Loeschung
  von SupersetsDialog.xaml/.xaml.cs/SupersetsDialogViewModel.cs (keine
  Aufrufer, war seit "Spec UX-1 FIX 5" UI-unreachable).
- 📋 **GitHub Actions Migration windows-latest → windows-2025-vs2026**
  Migration-Deadline Juni 2026. CI-Workflows in `.github/workflows/*.yml`
  betroffen. Nicht-blockierend, aber rechtzeitig vor Deadline updaten.
  Aufwand: ~30min (yml-Anpassung + Test-Run).

### v0.1.25 — geplant (Performance-Wurzel + Single-Mode-Cleanup + Diagnose-Track)

- ❌ **StorageBin.Kind-Drift bei Bestandsdaten** — KORRIGIERT 2026-05-17.
  Eintrag wurde basierend auf einem Lesefehler der StorageBinKind-Enum-
  Werte erstellt. Tatsaechliche Werte: Empty=0, Floating=1, Waiting=2,
  Complete=3. Die drei Bins (Box19-03, Box19-13, Box20-04) sind
  **konsistent**, kein Drift vorhanden:
  - Box19-03 Kind=1 (Floating), 0 Minifig + 3 Floating → korrekt
  - Box19-13 Kind=3 (Complete), 1 Minifig + 0 Floating → korrekt
  - Box20-04 Kind=3 (Complete), 1 Minifig + 0 Floating → korrekt

  Eintrag bleibt als Reflexions-Hinweis stehen: Engineering-Prinzip 1.2
  (Diagnose vor Aktion) gilt auch fuer Backlog-Eintraege. Vor Diagnose
  eigentlich `StorageBinKind.cs` lesen, nicht aus dem Gedaechtnis
  rekonstruieren. Siehe Komplexitaets-Audit 2026-05-17.

- 📋 **OPEN-18: Single-Mode-Cleanup** — Audit-Bedarf welche Service-
  Aufrufe Single-Item vs. Bulk sind, ggf. konsolidieren (aus Phase 2a-
  Polish Diagnose 2026-05-16). Aufwand: ~2h.
- 📋 **Performance-Wurzelfixes B + E** — `RaiseDataChanged` aus Service-
  Layer raus, `RecalcBinKindAsync`-Context-Piggyback. Strukturelle
  Wurzeln aus Diagnoser-Bericht 2026-05-14 (Quick-Wins D+C waren in
  v0.1.23-beta.2 erledigt). Aufwand: ~4-5h.
- 📋 **Kategorie-Sperre-Diagnose-Track** — wartet auf 2-3 dokumentierte
  Praxis-Vorfaelle (Befund 3 aus v0.1.23-beta.1). Ohne Vorfall-Daten kein
  Konzept-Entscheid (Engineering-Prinzip 1.2).
- 📋 **Klick-Optimierung Anlege-Workflow** (Design-Schema D9) — wartet
  auf User-Praxis-Erfahrung mit v0.1.24-beta.1. Aufwand: ~1-2h, je nach
  Befunden.

### Konzept-Items für spätere Iterationen (v0.1.25+)

- 💭 **Bauteile-Bin als eigenes StorageBinKind** (Konzept-Idee, 2026-05-14)

  Aus Befund 3 / DB-Snapshot Bin 97: User sortiert alle Teile einer
  zerlegten Figur in ein gemeinsames Bin (5 Kategorien: Torso, Legs,
  Hair, Head, Headgear). Das ist **kein** Verstoss gegen Kategorie-Sperre,
  sondern ein **implizit existierender Bin-Typ** der im
  StorageBinKind-Enum fehlt.

  Mögliche Modellierung:
  - Neuer Enum-Wert `StorageBinKind.MinifigParts` (oder `DisassembledSet`)
  - Eigene Regel-Tabelle: welche Aktionen sind in einem Bauteile-Bin
    erlaubt (vermutlich: nur DismantleWizard-Pfad, kein direkter
    FloatingPart-Add)
  - Visualisierung im UI: Bauteile-Bins sind **"reserviert"** und werden
    bei normalen FloatingPart-Combobox-Aufrufen nicht angeboten
  - Migration: Bestands-Bins mit 3+ verschiedenen Kategorien als
    Vorschlag fuer User-Markierung anbieten

  Aufwand-Vorausschau: ~4-6h Konzept + ~4-6h Implementierung
  (eigene Iteration, NICHT in v0.1.24 oder v0.1.25-Diagnose-Track).

  Voraussetzung: Befund 3-Diagnose-Track muss zeigen, dass Bauteile-Bins
  ein wiederkehrendes Pattern sind (nicht nur Einzelfall Bin 97).

---

## Backlog — UX-Befunde aus v0.1.23-beta.1 Praxis-Test (2026-05-14)

Drei Befunde aus dem Praxis-Test, alle als Nicht-Regression klassifiziert.
Diagnose-Bericht im Chat 2026-05-14 hat bestaetigt: 0 v0.1.23-Bugs,
Code-Verhalten seit v0.1.22 unveraendert.

### Befund 1: Limit-Einstellungen wirken nur als Suggest-Hint, nicht als Combobox-Filter

**Status: in v0.1.24 Beta 1 integriert** (Befund B1 in v0.1.24-Konzept Sektion 3.2).

- Settings `MaxCompleteFiguresPerBin` (Default 5) und `MaxWaitingFiguresPerBin`
  (Default 3) werden gelesen und an `SuggestBinForCompleteMinifigAsync` /
  `SuggestBinForWaitingMinifigAsync` durchgereicht.
- Wirkung: Stack-Wachstums-Default schlaegt ein neues Bin vor, sobald das
  Limit im aktuellen Stack-Bin erreicht ist.
- User-Erwartung: Limit als harter Filter im Combobox-Dropdown (volle Bins
  ausgrauen oder verstecken).
- Aktueller Stand: Limit wirkt nur auf Default-Vorschlag, Combobox listet
  weiterhin alle typ-passenden Bins inkl. der vollen.
- Wuensch fuer v0.1.24: Combobox-Filter + Banner-Erklaerung wenn alle
  vorgeschlagenen Bins voll sind. Aufwand: ~1h.

### Befund 2: Complete-Bin als Ziel fuer wartende Figur

**Status: in v0.1.24 Beta 1 integriert** (Befund B2 in v0.1.24-Konzept Sektion 3.2).

- Aktuell: wartende Figur darf in Complete-Bin (Konzept Abschnitt 2,
  Regel-Tabelle — explizit erlaubt). Bin wechselt dann auf Waiting bis
  die neue Figur komplett wird.
- User-Beobachtung: irritiert dass "fertige" Bins als Anlege-Ziel
  vorgeschlagen werden.
- Hinweis: `SuggestBinForWaitingMinifigAsync` schliesst Complete als
  Default-Vorschlag aus (Empty oder Stack-Waiting bevorzugt). Nur die
  GetEligibleBinsAsync-Combobox-Liste enthaelt Complete-Bins.
- UX-Diskussion offen: Konzept-Aenderung "Complete-Bin auch aus Liste
  raus" wuerde Reifungspfad invers beschneiden — User muesste erst
  Complete leeren bevor Wartende dort einlagern kann. **Nicht ohne
  bewussten User-Entscheid umsetzen.**

### Befund 3: Kategorie-Sperre nicht durchgaengig wirksam — Diagnose-Track

**Status:** STOP-Empfehlung nach Challenger-Analyse (2026-05-14). Aus v0.1.24-Scope
ausgeklammert, eigener Diagnose-Track laeuft. Frueheste Konzept-Iteration: v0.1.25,
nach 2-3 dokumentierten Praxis-Vorfaellen.

**Daten-Stand DB-Snapshot 2026-05-14:**
- Mix-Kategorie-Bins: **Bin 97 (Box19-07)** mit 5 verschiedenen Kategorien
  (Torso, Legs, Hair, Head, Headgear) — gewollt sortiertes Bauteile-Set einer
  zerlegten Figur.
- Legacy-null-Records: **0 von 5** — die ursprünglich vermutete Wurzel
  "BrickognizeCategory=null" existiert in dieser DB nicht.

**Drei moegliche Erklaerungen — neu bewertet:**
- **Stapel-Match by-design** (Schritt 1 in SuggestBinForFloatingPartAsync):
  gleiche PartNumber + ColorId → Stack-Wachstum, by-design.
- **User-Mapping by-design** (Schritt 2): Kategorie ist auf den Bin gemappt → User-Wille.
- **Bauteile-Bin-Workflow**: User hat bewusst alle Teile einer zerlegten Figur
  in einem Bin gesammelt (siehe Bin 97). Das ist ein **implizit existierender
  Bin-Typ** der noch nicht im StorageBinKind-Enum modelliert ist. Siehe
  separates Backlog-Item "Bauteile-Bin als eigenes StorageBinKind".

**Verworfene Loesungs-Optionen (Challenger-Analyse):**
- Option A (Strict-Mode-Throw): REJECTED — würde Bin 97 lock-outen, gegen
  User-Sortier-Pattern.
- Option B (Warn-Modal pro Konflikt): REJECTED — Modal-Stau bei Bulk-Move.
- Option D (Legacy-Migration für null-Records): REJECTED — 0 null-Records vorhanden.

**Diagnose-Routine für naechsten "zweiter-Torso"-Vorfall:**
1. SQL-Snapshot beim Vorfall:
   ```sql
   -- 1. Welche FloatingParts liegen im Ziel-Bin
   SELECT Id, PartNumber, ColorId, BrickognizeCategory, Quantity
   FROM FloatingParts WHERE StorageBinId = <bin_id>;
   -- 2. Welche Kategorie hat der neu zu lagernde Part
   SELECT BrickognizeCategory FROM bl_items WHERE PartNumber = '<new_part>';
   -- 3. Existierende User-Mappings
   -- (aus settings.json: CategoryToBinMapping)
   ```
2. Klassifizieren: Stapel-Match / User-Mapping / Bauteile-Bin / sonstiges
3. Vorfall in BACKLOG.md unter dieser Stelle protokollieren
4. Nach 2-3 protokollierten Vorfaellen: Wurzel-Verteilung klar →
   Konzept-Entscheidung fuer v0.1.25 fundiert

### Backlog-Status

- Befund 1 + 2: UX-/Roadmap-Items für v0.1.24 (siehe Versions-Plan)
- Befund 3: Diagnose-Track läuft, eigene v0.1.25-Mini-Iteration
- 0 Bugs in v0.1.23 — Code-Verhalten seit v0.1.22 unveraendert

---

## Externe Abhängigkeiten

### ✅ Brickognize-ToS (geklärt)
- ✅ **Brickognize-Lizenz/ToS-Klärung** - Mail an Piotr verschickt 2026-05-07
- ✅ **Antwort von Piotr** - 2026-05-09: Nutzung erlaubt, 5 RPS Limit, Attribution optional

---

## Erledigt ✅

### v0.1.24-beta.1 (2026-05-17) - UX-Konsistenz-Iteration (Modal-Pattern + Wizard + Cleanup)

Drei-Phasen-Iteration (Phase 1, 1.5, 1.5 Polish, 2a, 2a-Polish, 2b, 2b-Hotfix, 3),
alle praxis-getestet (grün). Tag folgt separat nach finalem Praxis-Test.

**Phase 1 — Modal-Pattern + Service-Layer** (`40c57b98`):
- ✅ **feat: Modal-Pattern für post-save Workflows** (Konzept B1) — alle
  6 Sortier-Trigger (Trigger 1-6) zeigen jetzt ein Modal mit Take/Put-
  Sektionen. Aktiv weggeklickt, KEIN Auto-Dismiss.
- ✅ **feat: GetEligibleBinsAsync Limit-Parameter** (Aufgabe D, Befund B1) —
  `waitingLimit` / `completeLimit` als optionale Parameter.
- ✅ **feat: ISortInstructionPresenter** + `DispatcherPriority.Render`-
  Pattern für reaktive Modal-Open-Performance.

**Phase 1.5 / Phase 1.5 Polish** (`a638a80c`, `39d05910`):
- ✅ Post-Save-Modal-Konsolidierung + Layout-Polish (BinLabel prominent
  in Akzent, Item-Detail in Mono, Items mit dezentem Background).

**Phase 2a — Wizard 2-stufig** (`40d14ffb`):
- ✅ **feat: Wizard 2-stufig für neue Figuren** (Aufgaben A+B+C) —
  `CollectMinifigWizardDialog` ersetzt `CollectMinifigSelectionDialog`.
  Stufe 1: Required-Parts in 3 Status-Gruppen (Trigger / Im Lager / Fehlt)
  mit Manuell-Markieren-Option. Stufe 2: Lagerfach + Bewegungs-Hinweis.
- ✅ **feat: IsManuallyClaimed Persist-Pfad** — Required-Parts die NICHT
  im Lager sind, aber physisch vorhanden, können als "manuell vorhanden"
  markiert werden (kein FloatingPart-Konsum).

**Phase 2a-Polish — Reverse-Match-Konsum-Fix** (`4b4e1ce4`):
- ✅ **fix: Reverse-Match-Auto-Konsum nur via expliziten Klick** (User-
  Wunsch 2026-05-16) — im MinifigDetailView-Pfad wird der Floating-
  Konsum jetzt parametrisiert (`consumePartsFromFloating`-Flag).

**Phase 2b — Combobox-Suffix** (`8ac0c493`):
- ✅ **feat: Combobox-Suffix mit Belegungs-Counts** (Aufgabe K, Befund B2) —
  alle Lagerfach-Comboboxen zeigen Suffix wie "(2 wartend, 3 fertig)".

**Phase 2b-Hotfix — Window.Resources-Position** (`c6845a5c`):
- ✅ **hotfix: Window.Resources an Anfang verschoben** — WPF-Parser läuft
  top-down; StaticResource-Auflösung scheiterte bei Resources am Ende.

**Phase 3 — Cleanup + Audit + Doku** (dieser Commit):
- ✅ **Tote Dialog-Klassen entfernt** (Aufgabe G) — `SupersetsDialog.xaml(.cs)`
  + `SupersetsDialogViewModel.cs` gelöscht (0 Produktiv-Aufrufer).
  `BinPickerDialog` als `internal static class` in eigene Datei
  `HBSort/Views/BinPickerDialog.cs` extrahiert (Bulk-Move-Pfad braucht ihn
  weiterhin).
- ✅ **Dialog-Konvention-Audit** (Aufgabe I) — 14 Dialoge/Overlays/Windows
  geprüft auf 8 Kriterien (ModernStyle, CenterOwner, Icon, Tooltips,
  IsCancel, IsDefault, DialogHeaderFontSize, Resources-Position). 6
  triviale Inkonsistenzen direkt gefixt (5x FontSize harmonisiert auf
  `DialogHeaderFontSize`=20, 1x `IsDefault` an BSX-Export-Button
  ergänzt). Keine substantiellen Befunde, kein Window.Resources-Bug
  außerhalb von Phase 2b-Hotfix-Stelle.

Tests: 589/589 grün (vorher 564 vor Phase 1).

### v0.1.23 (Stable, 2026-05-14) - Bin-Typ-Spalte mit Strict-Mode + Performance Quick-Wins

Sammel-Iteration aus 2 Betas. Tag `v0.1.23` zeigt auf `ac7e77ee`
(identisch mit `v0.1.23-beta.2`). Pipeline gruen, isPrerelease=false.

**Beta 1** (Tag `bfd03728`):
- ✅ **feat: Bin-Typ als persistierte Spalte** - EF-Migration
  `StorageBin.Kind` (Enum: Empty/Floating/Waiting/Complete), Auto-
  Backfill beim ersten App-Start nach Update.
- ✅ **feat: Strict-Mode A1-A12** - 12 Aufrufer-Stellen pruefen Bin-Typ
  vor Persist (`BinKindGuard.Ensure...`-Methoden, `InvalidBinKindException`).
  Reifungspfad Waiting → Complete im selben Bin bleibt erlaubt.
- ✅ **feat: Bin-Typ-Spalte im Lagerfaecher-Tab sichtbar** - neue
  Spalte mit lokalisiertem Typ-Namen.
- ✅ **fix: ScanViewModel-Pending-Filter** (`Z.1231 + Z.1387`) -
  `GetAllAsync()` durch `GetEligibleBinsAsync()` ersetzt, sonst hatte
  der Direkt-Scan-Pfad Floating-Bins als Ziel fuer Wartende angeboten.
  Diagnose 2026-05-14.

**Beta 2** (Tag `ac7e77ee`, Performance-Hotfix):
- ✅ **perf D: InventoryListViewModel BeginInvoke + IDisposable**
  (`bd64dfef`) - DataChanged-Subscriber konsistent mit den anderen 4
  VMs (LiveStats/RecentScans/WaitingDetail/BuildSuggestions). Field-
  Subscription statt Inline-Lambda; Memory-Leak-Fix nebenbei
  (`IDisposable` + Unsubscribe).
- ✅ **perf C: 5 Dispatcher.Invoke → InvokeAsync + CancellationToken**
  (`ac7e77ee`) - in LoadImagesAsync-Pfaden von InventoryList/
  RecentScans/BuildSuggestions/ScanViewModel (2 Stellen). Plus
  SemaphoreSlim-Awareness in `ScanViewModel.PreloadPartImagesAsync`.
  Pro VM `_imageLoadCts` damit voriger Image-Load-Task bei neuem
  Save abgebrochen wird.

Wirkung: ~70% Latenz-Reduktion nach Save-Operation in PartLookup-
Modus. Wurzel: DataChanged-Storm mit synchronen Dispatcher.Invoke-
Aufrufen blockierte UI-Thread vor Popup-Open (Diagnoser-Bericht
2026-05-14). Strukturelle Wurzel-Fixes (B = RaiseDataChanged aus
Service-Layer raus, E = RecalcBinKindAsync-Context-Piggyback) sind
in v0.1.25 als separate Performance-Iteration vorgemerkt.

**Stable-Promotion** (2026-05-14):
- Tag `v0.1.23` auf Commit `ac7e77ee` (identisch mit beta.2).
- Pipeline Run 25869236507: alle drei Jobs gruen (setup, build-zip,
  build-velopack), keine Fehler.
- Release-Notes user-freundlich ueberschrieben, 5 Assets, kein
  Pre-Release-Flag.

Tests: 559/559 gruen ueber beide Betas hinweg.

**Praxis-Test-Befunde aus beta.1** (alle Nicht-Regression, Code-
Verhalten seit v0.1.22 unveraendert):
- Befund 1 (Limit als Suggest statt Combobox-Filter) → v0.1.24 Beta 1
- Befund 2 (Complete-Bin als Ziel fuer wartende Figur) → v0.1.24 Beta 1
- Befund 3 (Kategorie-Sperre nicht durchgaengig wirksam) → STOP nach
  Challenger-Analyse, v0.1.25 Diagnose-Track (siehe Backlog-Sektion).

**Nicht-blockierende Notice:** GitHub Actions windows-latest →
windows-2025-vs2026 Migration bis Juni 2026 (siehe Backlog/System).

### v0.1.22 (Stable, 2026-05-13) - Performance + Bin-Typ-Trennung + Camera-Lazy-Enumeration

Sammel-Iteration mit 4 Betas. Tag zeigt auf `a6fd3884`.

- ✅ **perf: Bulk-FindFloatingLocations gegen N+1** (`e8d235a0`) -
  CollectMinifigSelection + DismantleWizard: pro Required-Part-Lookup
  jetzt EINE Query statt N.
- ✅ **feat: Splash zeigt Init-Phasen-Status** (`fa72c4b7`) - statt
  fixem "Bereitet App vor..." pro Phase ein eigener Status.
- ✅ **perf: Profiling-Stopwatches an 4 Hotspots** (`aa6e01fc`) -
  `[PROFILE]`-Log-Zeilen fuer CollectMinifig/Dismantle/BuildSuggestion/
  SettingsViewModel-ctor.
- ✅ **feat: CollectMinifigSelection Volle-Faecher-Banner** (`4b209b51`) -
  Block-K-Konsistenz.
- ✅ **refactor: DismantleWizard Helper-Refactor** (`15b59159`) -
  `ReloadAvailableBinsAsync` + `ApplySmartHintIfAny` extrahiert.
- ✅ **refactor: BinInstruction-Overlay konsolidiert** (`e40ff4aa`) -
  Single + Group zu einer VM/View mit Mode-Switch.
- ✅ **feat: Bin-Typ-Trennung mit Reifungspfad** (`47ccda9e`) -
  `BinKind`-Enum + `GetBinKindAsync` + `FindBestandMixBinsAsync` +
  App-Start-Mix-Bin-Toast.
- ✅ **fix: Splash-Status sichtbar + Loaded blockt nicht mehr**
  (`06325e26`) - `Dispatcher.Invoke(Render)` + `ContextIdle` fuer
  First-Run-Dialog.
- ✅ **feat: GetItemNamesAsync Bulk-Lookup** (`5a0a6740`) - eine Query
  statt N pro Item-Name-Lookup.
- ✅ **fix: PartName beim Block-D-Anlegen befuellen** (`23bcf5af`) -
  CollectMinifigFromSuperset(WithSelection)Async holt jetzt PartName
  aus bl_items.
- ✅ **migration: TrackedMinifigPart.PartName-Backfill** (`695861a8`) -
  App-Start-Migration fuer Bestand.
- ✅ **Dependabot-Sammel-Bumps** (Welle 1-3, 5 Pakete): xunit 3.1.5,
  Serilog 4.3.1, Sinks.Console 6.1.1, Sinks.File 7.0.0, ProtectedData
  10.0.7. PRs #20-#24 (zwei davon lokal angewandt wegen Konflikt nach
  Welle 1).
- ✅ **feat: GetEligibleBinsAsync typ-gefilterte Bin-Liste** (`3b9bab85`).
- ✅ **fix: Combobox-Filter in 4 Dialogen** (`2a3f6739`, `07234d61`,
  `d74be9a7`, `654f1f1c`) - CollectMinifigSelection, MinifigSummary,
  DismantleWizard, BuildSuggestionDetail.
- ✅ **feat: SettingsWindow akzeptiert initialTab** (`93f7d96f`) +
  **fix: Banner-Button springt auf Lagerfaecher-Tab** (`b562256a`).
- ✅ **fix: Camera-Init zurueck in Splash-Pfad** (`defd3750`, `455a855b`) -
  beta.3-Erstversuch hatte das vergessen.
- ✅ **feat: Camera-Lazy-Enumeration** (`a6fd3884`) - App-Start ~15s
  schneller. GetAvailableCameras() laeuft nur noch lazy im Settings-
  Tab.

Tests: 548/548 gruen.

Konvention-Updates: `OpenSettingsRequested`-Event hat `OpenSettings
EventArgs` mit `InitialTab`-Property; Banner-Buttons springen direkt
auf den Lagerfaecher-Tab via `OpenSettingsOnTab(SettingsTab.Lagerfaecher)`.

### v0.1.21 (Hotfix, 2026-05-12) - bl_colors Cache-First + colors.xml im BulkImporter

User-Befund: ohne BL-Tokens scheitert der erste Scan an einer
BricklinkAuthException, obwohl das Onboarding "Tokens optional"
verspricht.

- ✅ **fix: BlCatalogService.GetAllColorsAsync Cache-First** (`6acc5aa5`):
  prueft vor dem API-Fallback `IBricklinkTokenStorage.HasTokens()`. Bei
  leerem Cache + keinen Tokens: leere Liste statt `BricklinkAuthException`.
  Graceful degradation - Farb-Namen werden ohne Tokens nicht angezeigt,
  aber das Matching laeuft (ID-basiert) durch.
- ✅ **fix: BlBulkImportService importiert colors.xml**
  (`da44f163`): vorher hat der Importer ausschliesslich `items/*.xml`
  geparsed - `colors.xml` im Root des BrickStore-ZIP wurde ignoriert,
  `bl_colors` blieb nach jedem Import leer. Neue Phase 1b parsed
  `colors.xml` (~215 Eintraege) und schreibt via UpsertColorsAsync.
- ✅ **fix: Auto-Reimport-Trigger fuer alte v0.1.20-Installs**
  (`da44f163`): `TryReimportCatalogIfColorsMissingAsync` als
  fire-and-forget-Startup-Hook. Bei `bl_items > 0 && bl_colors == 0`:
  Reimport mit ETag-Check im Hintergrund + Toast.
- ✅ **ci: AssemblyVersion-Padding fuer Patch-Tags** (`e9465fe1`):
  setup-Step paddet jetzt assembly_version auf 4 Felder (.NET-Pflicht),
  build-Steps lesen den Wert ohne weitere Manipulation. Erlaubt
  zukuenftige 4-Field-Tags im build-zip-Pfad, ABER build-velopack bleibt
  3-Field-only (siehe Versions-Schema-Konvention in CLAUDE.md).

Versions-Sprung-Hinweis: urspruenglich als v0.1.20.1 geplant, wegen
Velopack-3-Stelligkeit als Minor-Bump v0.1.21 released.

542/542 Tests gruen (vorher 539 + 3 neu in BlCatalogServiceTests).

### v0.1.20-beta.5 (UX X.34, 2026-05-12) - bl_prices Cache-Fix + Brickognize-Throttle + ScanViewModel-Dispose

Drei Cleanup-Fixes in einem Beta-Release.

- ✅ **bl_prices Cache-Fix**: country_code war keine Cache-Dimension - Country=DE
  und Global teilten sich denselben Cache-Eintrag (region="" in beiden Faellen)
  und ueberschrieben sich gegenseitig. Stille Vergiftung ohne API-Fehler,
  praktisch sichtbar in app-20260511.log. Schema-Migration drop+create der
  bl_prices-Tabelle (reiner Cache, kein User-Datenverlust). vat_mode wandert
  in den Primary Key, Either-Or-Logik (Country wins ueber Region) wird auch
  im BlPriceCacheService angewandt damit Service-Reads die Provider-Writes
  finden.
- ✅ **Brickognize 5-RPS-Throttle**: SemaphoreSlim + Timestamp-Throttle vor
  jedem Predict-Call (Health-Check ausgenommen). 5 RPS Limit gemaess Piotr-
  Absprache 2026-05-09. Statisch damit alle Client-Instanzen denselben
  Bucket teilen.
- ✅ **ScanViewModel : IDisposable**: FrameReceived + PendingMinifig.
  PropertyChanged + BinInstructionTimer + LookupCts werden beim Dispose
  freigegeben. App.OnExit ruft (Services as IDisposable)?.Dispose() schon -
  greift damit automatisch beim Shutdown.
- ✅ **Preise mit 4 Nachkommastellen**: PriceMath.ApplyCorrection rundet
  auf 4 Stellen (statt 2), MinifigPriceViewModel formatiert mit "N4" und
  RecalculatePartsTotal rundet ebenfalls auf 4. BL liefert Preise mit
  4 Stellen Praezision - kleine Standardteile wie 0,0234 EUR EUR
  behalten ihre Aussagekraft (vorher zu 0,02 EUR abgeschnitten).

539/539 Tests gruen (vorher 536, +3 neu: 2 BlCacheRepositoryTests + 1 PriceMathTests).

### v0.1.20-beta.4 (UX X.34, 2026-05-11) - Welcome-Dialog vereinfacht
- ✅ **Lagerfaecher-Schritt nur als Hinweis**: Button "Lagerfach-Verwaltung
  oeffnen" entfernt - vorher Modal-on-Modal mit Refresh-Logik beim Schliessen.
  Jetzt reiner Info-Text: User schliesst Welcome via "Loslegen" und legt
  Bins entspannt in den Einstellungen an (`Einstellungen > Lagerfaecher >
  Bulk anlegen`).
- ✅ VM-Cleanup: HasBins / BinsStatusIcon / RefreshStatusAsync entfernt -
  weniger State, klarerer Flow.

### v0.1.20-beta.3 (UX X.34, 2026-05-11) - Bug-Fix Splash/Welcome
Iteration mit zwei Runden weil der erste Fix einen Folge-Befund hatte.

**Runde 1 (erste beta.3, zurueckgezogen):**
- Splash hatte `Topmost="True"` + `AllowsTransparency="True"` - WPF
  haelt den Z-Order auch kurz nach `Close()` noch im Compositor.
  Welcome-Dialog wurde dahinter verdeckt.
- Fix: `Topmost="False"` + Dispatcher-Flush vor `mainWindow.Show()`.

**Runde 2 (aktuelle beta.3, User-Befund nach Runde 1):**
- Splash verschwand zu frueh - dann 5-10s "leere Pause" bevor Welcome
  erschien. Ursache: `Window_Loaded` rief `InitializeCameraAsync` (5-10s
  blocking) VOR dem Welcome-Dialog auf.
- Fix: Reihenfolge in `Window_Loaded` umgedreht. **First-Run-Dialog
  ZUERST**, Camera-Init danach. Welcome erscheint jetzt sofort nach
  Splash; Camera-Init laeuft im Hintergrund waehrend User Welcome
  bedient (Camera ist erst nach "Loslegen" relevant).

### v0.1.20-beta.2 (UX X.34, 2026-05-11) - First-Run-Onboarding
- ✅ **First-Run-Dialog**: erscheint beim ersten App-Start auf einer
  leeren Installation. Zwei Pflicht-Schritte (BL-Catalog laden + Lager-
  faecher anlegen) plus optionaler BL-Tokens-Hinweis.
- ✅ `IFirstRunService` mit Status-Erkennung (Catalog leer? Bins leer?).
- ✅ `AppSettings.ShowFirstRunDialog` (Default true) - bei "Loslegen"
  auf false gesetzt damit der Dialog danach nicht mehr stoert.
- ✅ Hilfe-Doku Kapitel "First-Run-Setup" ergaenzt.

### v0.1.20-beta.1 (UX X.34, 2026-05-11) - Beta-Iteration
BL-Preis-Lookup-Sammelpaket. Urspruenglich als v0.1.19.1-Hotfix geplant,
in Beta-Iteration konsolidiert weil mehrere voneinander abhaengige
Aenderungen am Cache-Schema + Provider-Logik.

- ✅ **VAT=Y (Brutto) als Default** fuer BL-Preis-Lookups - matcht jetzt die
  BrickLink-Webseite-Preise. Vorher kein vat-Parameter -> Library-Default
  Netto -> gemischte Aggregate (Privat brutto, gewerblich netto). Verkaufs-
  empfehlungen sind damit konsistent.
- ✅ Neuer Settings-Eintrag *BrickLink → Preise → VAT-Modus* mit Dropdown
  Brutto/Netto/Norwegen.
- ✅ Cache-Schema erweitert: `bl_prices.vat_mode`-Spalte (lazy migration -
  Bestand-Eintraege als Netto markiert, Refresh beim naechsten Lookup).
- ✅ ProviderLabel im UI zeigt "Brutto"/"Netto"/"NO".
- ✅ **AutoLoad-API-Warnung**: Banner unter den Settings-Dropdowns (Komplett-
  Preis / Einzelteile-Preise) wenn Auto aktiviert ist - erklaert API-Aufrufe-
  Verbrauch (Komplett=1/Figur, Einzelteile=1 PRO TEIL-TYP).
- ✅ **Region/Land Either-Or-Fix**: BL-API erwartet nur einen Filter; vorher
  haben wir beide geschickt -> Sold + DE lieferte oft 0 Treffer auch ohne
  expliziten Wunsch. Neue RadioButton-Gruppe (Global / Region / Land) im
  Settings-Tab. Default jetzt Global. Migration normalisiert alte
  Beide-gesetzt-Konfigurationen beim Settings-Load (CountryCode wins).
- ✅ **Region-Liste vollstaendig** gemaess offizieller BL-API-Doku:
  `eu` (nur EU-Mitglieder), `middle_east`, `oceania` ergaenzt.
- ✅ **Leer-Filter null-Fix**: BricklinkSharp.GetPriceGuideAsync mit Empty-
  Strings sendet `&country_code=&region=` an die BL-API - das wird nicht
  als "weglassen" behandelt und liefert leere Resultate. Provider wandelt
  jetzt Empty -> null vor dem Aufruf.
- ✅ **Cache-Anti-Vergiftung**: Provider schreibt **nichts** in den Cache
  wenn das API-Resultat komplett leer ist (alle Preise null + Quantity=0).
  Plus One-Shot-Cleanup beim App-Start: `ClearEmptyPricesAsync` raeumt
  Bestand-Vergiftung aus dem Pre-Fix-Stand auf.

### v0.1.19 (UX X.33, 2026-05-10)
- ✅ Cleanup-Iteration (Drifts 1-3, Sold-Status raus, Settings-Bereinigung)
- ✅ Sortier-Logik-Refactor: max 1 PartId pro Brickognize-Kategorie pro Bin
- ✅ Brickognize-Category-Mapping (User-Override im neuen Settings-Tab "Kategorien")
- ✅ Volle-Fächer-Warnung in MinifigDetail / PartLookup / DismantleWizard / BuildSuggestion
- ✅ BuildSuggestion-Sammel-Popup mit Bildern + Target-Item-Markierung
- ✅ Sold-Status-Pfade entfernt (13 Verzweigungen) - keine "Sold"-Variante mehr
- ✅ Deprecated Settings entfernt (UiDensity, PriceToolUrl, SplitterRowRatio, AutoLoadOnComplete)
- ✅ EF-Migration: BrickognizeCategory-Spalte an FloatingPart
- ✅ Backfill-Migration für Bestand-FloatingParts ohne Kategorie
- ✅ Diagnose-Dateien aus Repo entfernt + .gitignore-Pattern (`*_DIAGNOSE_*.md`,
  `*_PRAXIS_TEST.md`, `*_TESTANLEITUNG.md`, `.dbinspect/`, `.release-notes-*.md`)
- ✅ Doku-Sweep: 18 Befunde aus Pre-Stable-Audit gefixt (3 Tooltips, 10 Hilfe, 5 README)
- ✅ Velopack-Auto-Update beta.7 → stable funktioniert
- ✅ Pre-Push-Sicherheits-Audit: keine Tokens / DB-Files / User-Pfade im Repo

### v0.1.18 (UX X.31, 2026-05-09)
- ✅ Bin-Vorschlag-Konsistenz + Enter-Hotkey + Anweisungs-Popup
- ✅ Live-Wechsel via PendingMinifigViewModel.IsAllCollected

### v0.1.17 (UX X.30)
- ✅ FloatingMove-Undo, DataHeal-Auto, Notiz→Beschreibung-Umbenennung
- ✅ MaxHeight=320-Entfernung, Statistik-Dashboard erweitert
- ✅ Brickognize-Hotkeys 1/2/3 (MainWindow-Scope)

### v0.1.16 (UX X.29, 2026-05-09)
- ✅ Backup-System + Verlauf+Undo + Bulk-Operationen
- ✅ Default-Auswahl-Setting + Auto-BL-Import-Effizienz-Fix

---

## Versions-Plan

| Version | Stand | Inhalt |
|---|---|---|
| **v0.1.19** | ✅ stable (2026-05-10) | UX X.33 - Cleanup + Sortier-Logik-Refactor + Doku-Sweep |
| **v0.1.20-beta.1** | ✅ released (2026-05-11) | UX X.34 - BL-Preis-Lookup-Sammelpaket (VAT, Either-Or, Cache-Anti-Vergiftung) |
| **v0.1.20-beta.2** | ✅ released (2026-05-11) | UX X.34 - First-Run-Onboarding-Dialog |
| **v0.1.20-beta.3** | ✅ released (2026-05-11) | UX X.34 - Splash-Welcome-Lifecycle-Fix |
| **v0.1.20-beta.4** | ✅ released (2026-05-11) | UX X.34 - Welcome-Dialog Lagerfaecher-Schritt nur als Hinweis |
| **v0.1.20-beta.5** | ✅ released (2026-05-12) | UX X.34 - bl_prices Cache-Fix + Brickognize-Throttle + ScanViewModel-Dispose + 4-Nachkommastellen-Preise |
| **v0.1.20** | ✅ released (Stable, 2026-05-12) | UX X.34 stable - alle beta.1..beta.5-Aenderungen konsolidiert |
| **v0.1.21** | ✅ released (Stable, 2026-05-12) | Hotfix - bl_colors Cache-First + colors.xml im BulkImporter + Auto-Reimport-Trigger + CI-Padding-Fix |
| **v0.1.22-beta.1..4** | ✅ released (2026-05-12/13) | Performance + Bin-Typ-Trennung + Splash-Fix + PartName-Fix + Dependabot + Camera-Lazy-Enumeration |
| **v0.1.22** | ✅ released (Stable, 2026-05-13) | beta.1..beta.4 konsolidiert |
| **v0.1.23-beta.1** | ✅ released (2026-05-14) | Bin-Typ-Spalte (StorageBin.Kind als persistierte Enum), Strict-Mode, Migration, ScanViewModel-Pending-Filter. Tag auf bfd03728. |
| **v0.1.23-beta.2** | ✅ released (2026-05-14) | Performance-Hotfix: Quick-Wins D+C aus Diagnoser-Bericht (Dispatcher.Invoke→InvokeAsync + CancellationToken in 5 LoadImagesAsync-Pfaden + InventoryListViewModel BeginInvoke-Konsistenz, IDisposable-Pattern). Tag auf ac7e77ee. |
| **v0.1.23** | ✅ released (Stable, 2026-05-14) | beta.1 + beta.2 konsolidiert. Tag auf ac7e77ee (identisch mit beta.2). Pipeline grün, isPrerelease=false. |
| **v0.1.24-beta.1** | ✅ released (Phase 1+1.5+2a+2a-Polish+2b+2b-Hotfix+3, 2026-05-15..17) | Modal-Pattern + Wizard 2-stufig + IsManuallyClaimed + Combobox-Suffix + tote Dialoge entfernt + Audit. 589 Tests grün. Tag folgt nach finalem Praxis-Test. |
| **v0.1.24-beta.2** | 📋 geplant | BL-Inventar Beta 2 (Komplettierungs-Integration) + Klick-Optimierung Anlege-Workflow (wartet auf Praxis-Befund). |
| **v0.1.24-beta.3** | 📋 geplant | BL-Inventar Beta 3 (Mass-Update-Export) + Dark-Mode-Status-Brushes + Polish + Praxis-Audit. |
| **v0.1.25** | 💭 Brainstorming | Performance-Wurzel-Fixes (B+E aus Diagnoser-Bericht: RaiseDataChanged aus Service-Layer raus, RecalcBinKindAsync-Context-Piggyback, ~4-5h) + OPEN-18 Single-Mode-Cleanup + Kategorie-Sperre + Bauteile-Bin-Konzept. Voraussetzung Kategorie-Track: 2-3 protokollierte Praxis-Vorfaelle aus Diagnose-Track (Befund 3). |
| **v0.2.0** | 💭 Brainstorming | grosse Features aus Backlog (siehe oben) |

Konvention:
- Patch-Iteration (v0.x.Y) für Cleanup + kleine Features
- Minor-Iteration (v0.Y.0) für große Features oder Architektur-Änderungen

---

*Zuletzt aktualisiert: 2026-05-17 nach v0.1.24-beta.1 Phase 3 (Cleanup + Dialog-Audit + Doku). Naechster Schritt: finaler Praxis-Test, dann Tag `v0.1.24-beta.1`. Danach v0.1.24-beta.2 (BL-Inventar Beta 2 + Klick-Optimierung).*
