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

## Allgemeine Roadmap-Items (versions-uebergreifend)

> Hinweis: Diese Sektion hiess frueher "v0.1.20 - geplant". v0.1.20 ist
> laengst stable (2026-05-12); die Items hier sind versions-uebergreifende
> Kandidaten ohne feste Zuordnung. Erledigte wandern in den Erledigt-
> Bereich, terminierte in die v0.1.25/v0.2.0-Sektionen.

### Performance
- ⚠️ **Splash-Screen Ladeanzeige reparieren** - Status zu verifizieren.
  Mehrere Splash-Fixes liefen seither (v0.1.20-beta.3 Lifecycle, v0.1.22
  Init-Phasen-Status), aber ob die urspruengliche "keine sichtbare
  Anzeige"-Beobachtung damit erledigt ist, wurde nicht gezielt geprueft.
  Beim naechsten Praxis-Test mit-beobachten. Aufwand: ~1h falls noch offen.
- ✅ **Settings oeffnen/schliessen schneller** - gefuehlt erledigt
  (2026-05-28). Die Performance-Quick-Wins (v0.1.23-beta.2) + Camera-Lazy-
  Enumeration (v0.1.22) haben die Reaktionszeit spuerbar verbessert; User
  empfindet die Settings-Latenz nicht mehr als Problem. Kein gezielter
  Fix noetig.
- ✅ **Popups oeffnen schneller** - gefuehlt erledigt (2026-05-28). Die
  DataChanged-Storm-Quick-Wins (v0.1.23-beta.2: Dispatcher.InvokeAsync +
  CancellationToken) haben die Popup-Latenz spuerbar reduziert. Strukturelle
  Wurzel-Fixes (B+E) bleiben als v0.1.25-Material gelistet, aber der
  akute Schmerz ist weg.
- ✅ **ScanViewModel : IDisposable** - erledigt in v0.1.20-beta.5 (2026-05-12).
  FrameReceived + PendingMinifig.PropertyChanged + BinInstructionTimer + LookupCts
  werden im Dispose freigegeben; (Services as IDisposable)?.Dispose() in
  App.OnExit ruft den Pfad automatisch beim Shutdown.

### Features
- ➡️ **Vollständiges Undo-System für alle DB-Mutationen** — VERSCHOBEN
  nach v0.1.25 (siehe dortige Sektion). Aktuell undo-fähig: Move (Minifig
  + Floating), Delete (Minifig). Fehlt: FloatingPart anlegen, Direkt-
  Zerlegen, Reverse-Match-Konsumieren, BuildSuggestion-Anlegen, Bin-
  Operationen, Pending-Persist mit Reverse-Match, BL-Reservierung.
  Aufwand: ~6-8h (eigene Sub-Iteration).
- ✅ **Bulk-Bin-Aktionen im Settings-Tab Lagerfächer** — erledigt in
  v0.1.25-beta.1 (`0bb0a7bd`, 2026-06-02). Markierte Faecher sammelweise
  leeren oder loeschen mit Vorschau + Bestaetigungs-Dialog; belegte Faecher
  werden beim Loeschen uebersprungen und im Ergebnis-Report sichtbar gemacht.
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
- ✅ **BinInstructionOverlay + BinInstructionGroupOverlay** zu einem
  UserControl mit Mode-Switch — erledigt in v0.1.22-beta.1
  (`refactor: BinInstruction-Overlay konsolidiert`, `e40ff4aa`).

### Konsistenz-Drifts
- ✅ **CollectMinifigSelectionViewModel Volle-Faecher-Banner** — erledigt
  in v0.1.22 (`feat: CollectMinifigSelection Volle-Faecher-Banner`,
  `4b209b51`). Block-K-Konsistenz hergestellt.

---

## v0.2.0 (oder später) - größere Features

### BL-Inventar-Integration
- ✅ **BL-Inventar einlesen + komplettieren + Mass-Update-Export** —
  KOMPLETT umgesetzt in v0.1.24-beta.6 bis beta.14 (2026-05-28). Lief
  als eigene Strecke statt als v0.2.0-Feature. Alle vier Phasen fertig:
  - Phase 1 (beta.6): BlInventoryLot-Entity + EF-Migration +
    IBlInventoryService + Settings-Sync (Snapshot-Replace,
    ReservedQuantity-Erhalt)
  - Phase 2 (beta.7): Tab "BrickLink Inventar" mit DataGrid, Filter,
    Detail-Panel, Lazy-Thumbnails, CatalogName/ColorName-Enrichment
  - Phase 3 (beta.8): Komplettieren-Integration
    (QuantityReservedFromBl, BlReserveDialog, Baubar-Tab-BL-Erweiterung,
    BL-Badge, Sortier-Tab-Toast)
  - Phase 4 (beta.12 + beta.14): Mass-Update-Export
    (GenerateMassUpdateXmlAsync, PendingExport-Entity, VerifyExportAsync
    mit Reserved→Collected-Konvertierung, Auto-Sync vor Export)
  Details im Erledigt-Bereich unten.

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
- ✅ **Spalten-Persistenz Temporäres Inventar** - Sortierung + Spaltenbreite
  merken — erledigt in v0.1.25-beta.1 (`17e06216`, 2026-06-02). Sortierung
  und Spaltenbreiten bleiben pro App-Start erhalten.
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
- 📋 **Performance-Wurzelfix B (DataChanged-Storm) — Hygiene-Herabstufung
  WIDERRUFEN** + E (RecalcBinKind-Piggyback, bleibt Hygiene). B =
  `RaiseDataChanged` aus Service-Layer raus (~20 Call-Sites über 4
  Service-Dateien, mittleres Regressions-Risiko); E =
  `RecalcBinKindAsync`-Context-Piggyback (~22 Aufrufer, alle in offenem
  Context → Piggyback trivial, ~3-4h). Strukturelle Wurzeln aus
  Diagnoser-Bericht 2026-05-14.
  **Wurde 2026-05-29 auf 💭 Hygiene herabgestuft** (Diagnoser-Bericht
  damals: alle Lade-Pfade < 165ms, kein DataChanged-Storm, disjunkte
  Listener). **Widerruf fuer B 2026-06-07** nach neuem Diagnoser-Lauf:
  bei wachsender Datenmenge (12k Image-Cache, 2k ScanEvents) wirkt B als
  **Multiplikator von PERF-4** (5 Live-VMs laden parallel pro Scan → 5×
  der Stats-Storm). Die 29.05.-Einschaetzung war bei kleinerer Datenmenge
  richtig, ist es bei wachsendem Bestand nicht mehr. **Re-Evaluierung
  NACH PERF-4-Fix sinnvoll** — wenn PERF-4 den 5×-Storm-Effekt eliminiert,
  koennte B wieder Hygiene werden; heute keine Entscheidung erzwingen,
  erst messen nach PERF-4. E bleibt Hygiene (lokal, risikoarm, nur bei
  Bedarf). Reihenfolge: PERF-4 + PERF-5 zuerst (siehe Diagnoser-Lauf
  2026-06-07 unten), dann B re-evaluieren. E-Vorbedingung siehe PERF-2.
- 📋 **Kategorie-Sperre-Diagnose-Track** — wartet auf 2-3 dokumentierte
  Praxis-Vorfaelle (Befund 3 aus v0.1.23-beta.1). Ohne Vorfall-Daten kein
  Konzept-Entscheid (Engineering-Prinzip 1.2).
- 📋 **Klick-Optimierung Anlege-Workflow** (Design-Schema D9) — wartet
  auf User-Praxis-Erfahrung mit v0.1.24-beta.1. Aufwand: ~1-2h, je nach
  Befunden.

#### Aus Audit + Arbeit der beta.11-14-Strecke (2026-05-28)

- ✅ **B1: Dialog-VMs ohne DI** — erledigt in v0.1.25-beta.1
  (`refactor: Dialog-VM-DI + Silent-Catch + DialogResult (B1/B6/B10, v0.1.25)`).
  `ManageIgnoredViewModel` + `MassUpdateExportViewModel` werden jetzt via
  `App.xaml.cs::ConfigureServices` (`AddTransient`) aufgeloest statt per `new`.
- 📋 **B2: Footer-Layout in zwei neuen Dialogen** — praezisiert durch
  ux-patterns.md-Drift-Inventur:
  - `ManageIgnoredDialog.xaml`: "Schliessen" hat `IsDefault="True"` UND
    `IsCancel="True"` zusammen (Read-Only-Verletzung) + `AccentButtonStyle`
    faelschlich auf dem Schliessen-Button.
  - `MassUpdateExportDialog.xaml`: Schliessen-Button steht hinter dem
    Primaer-Button (Reihenfolge invertiert — Schliessen gehoert links).
  Konvention: links Abbrechen/Schliessen (IsCancel), rechts primaer
  (IsDefault + AccentButtonStyle). Schwere: L-M. Aufwand: ~30min.
- ✅ **B6/B10: Kosmetik in ManageIgnored/MassUpdate-VMs** — erledigt in
  v0.1.25-beta.1 (`refactor: Dialog-VM-DI + Silent-Catch + DialogResult (B1/B6/B10, v0.1.25)`).
  Silent-Catch bekommt jetzt `Log.Debug`; `Close_Click` setzt nicht mehr
  `DialogResult=true`.
- 📋 **BL-Inventar-Sync: UPSERT statt DELETE+INSERT** — Snapshot-Replace
  loescht + schreibt aktuell alle ~9.4k Lots neu. UPSERT der geaenderten
  Lots waere schneller, spart aber nur ~15% der 2,7s Sync-Zeit (85% sind
  API-Antwortzeit). Geringer Nutzen — nur mitnehmen wenn der BL-Sync-Code
  ohnehin angefasst wird. Quelle: Auditor 2026-05-28. Aufwand: ~1-2h.
- 📋 **Vollständiges Undo-System für alle DB-Mutationen** — verschoben aus
  der allgemeinen Roadmap. Aktuell undo-faehig: Move (Minifig + Floating),
  Delete (Minifig). Fehlt: FloatingPart anlegen, Direkt-Zerlegen, Reverse-
  Match-Konsumieren, BuildSuggestion-Anlegen, Bin-Operationen, BL-
  Reservierung. WICHTIG: vor dem Bau eine Reversibilitaets-Klassifikation
  (gruen/gelb/rot) machen — ROTE Aktionen (BSX-Export, Mass-Update an BL)
  sind NICHT per Undo umkehrbar (haben die App verlassen), dafuer nur
  Warnung-vorher statt Undo-nachher. Aufwand: ~6-8h (eigene Sub-Iteration,
  Auditor-Vorlauf empfohlen).

#### Aus ux-analyst Stable-Readiness-Check (2026-05-28)

Sechs kosmetische Befunde (Severity L) plus ein strategisches Item aus
dem finalen UX-Konsistenz-Check. Alle nicht stable-blockierend — Verdikt
war STABLE-READY fuer v0.1.24.

- ✅ **UX-1: Schliessen-Button mit AccentButtonStyle in 3 Dialogen** —
  erledigt in v0.1.25-beta.1 (Kosmetik-Sweep Welle 1-3).
  praezisiert durch die Drift-Inventur in `docs/ux-patterns.md`: betrifft
  nicht nur `MinifigSummaryDialog.xaml:198-201`, sondern auch
  `BinDetailDialog.xaml:157` + `FloatingPartDetailDialog.xaml:109`. Alle
  drei haben "Schliessen" faelschlich mit `AccentButtonStyle` (Accent
  gehoert nur auf primaere Aktionen). Bei MinifigSummary zusaetzlich alle
  Buttons rechts statt links/rechts-Split. In einem Rutsch fixbar.
  Schwere: L. Aufwand: ~25min. Quelle: ux-analyst 2026-05-28 +
  ux-patterns.md-Drift-Inventur.
- ✅ **UX-2: BlReserveDialog Footer-Anordnung** — erledigt in
  v0.1.25-beta.1 (Kosmetik-Sweep). Abbrechen-Button steht
  in Grid-Spalte 1 (= rechts) statt links (`BlReserveDialog.xaml:120-124`).
  Konvention: links Abbrechen/Schliessen (IsCancel). Schwere: L. Aufwand:
  ~10min. Quelle: ux-analyst 2026-05-28 + ux-patterns.md-Drift-Inventur.
- ✅ **UX-3: BL-Inventar Tooltip-Drift** — erledigt in v0.1.25-beta.1
  (Kosmetik-Sweep). Tooltip sagt noch "aktuell
  durchgehend leer", stimmt seit beta.8 nicht mehr
  (`BlInventoryView.xaml:163`). Schwere: L. Aufwand: ~5min. Quelle:
  ux-analyst 2026-05-28.
- ✅ **UX-4: BL-Shop-Badge Beschriftung uneinheitlich** — erledigt in
  v0.1.25-beta.1 (Kosmetik-Sweep). `MinifigSummary
  Dialog.xaml:147` bindet `BlAvailability.BadgeText` (Mengen-Text, z.B.
  "3× Neu"), `BuildSuggestionDetailDialog.xaml:166` hat hartkodiert
  `Text="BL-Shop"` obwohl es dasselbe `BlAvailability`-Objekt bindet.
  **Soll: Mengen-Variante** (informativer, beide haben das Objekt eh).
  Fix ist ein reiner Binding-Tausch in BuildSuggestionDetailDialog.
  Schwere: L. Aufwand: ~10min. Quelle: ux-analyst 2026-05-28 +
  ux-patterns.md.
- ✅ **UX-5: DataHealService Log-Text-Rest "via Lagerliste -> Details"** —
  erledigt in v0.1.25-beta.1 (`refactor: Lagerliste-Reste in Log + Code-Kommentaren`).
  vergessener Lagerliste-Rest in Log-Ausgabe (`DataHealService.cs:68`).
  User-unsichtbar (Log-only), trotzdem konsistent ziehen. Schwere: L.
  Aufwand: ~2min. Quelle: ux-analyst 2026-05-28.
- ✅ **UX-6: Code-Kommentar-Drift "Lagerliste-Tab"** — vergessener Rest 
im Kommentar (`MainViewModel.cs:399`). Schwere: L. Aufwand: ~2min.
  Erledigt in v0.1.25-beta.1 (`refactor: Lagerliste-Reste in Log + Code-Kommentaren`).
- ✅ **UX-7: Button "Alle Preise holen" überlappt Überschrift** —
  erledigt in v0.1.25-beta.1 (eigene Toolbar im Bau-Vorschlaege-Panel). Im
  Baubar-Tab (Was kann ich bauen) verdeckt der Button die Überschrift
  bzw. nimmt zu viel Platz im engen rechten Panel. Layout anpassen:
  kleinerer Button, anders positionieren oder in eine Symbolleiste
  legen. Praxis-Test 2026-05-28. Schwere: L. Aufwand: ~15-30min.
  Quelle: ux-analyst 2026-05-28.
- ✅ **docs/ux-patterns.md als zentraler UX-Pattern-Katalog** — erledigt
  2026-05-29 (`63b2af7d`). 10 Patterns (Dialog-Footer, Dialog-VM, Sort
  Instruction-Modal, BL-Shop-Badge, Reserve-Flow + V5, Strict-Mode-
  Feedback, Volle-Faecher-Banner, Pending-vs-Detail-Abgrenzung, Klickbare
  Bilder, Tooltips), jeweils Regel + XAML-Beispiel + Vorbild-Datei +
  Drift-Inventur. Konsolidierte Drift-Tabelle (6 driftende vs. 6 korrekte
  Footer) als Arbeitsgrundlage fuer Welle 3. Verhindert Inkonsistenz-
  Wellen bei kuenftigen Features. Quelle: ux-analyst 2026-05-29.

#### Aus Diagnoser-Lauf 2026-05-29 (Performance-Standortbestimmung)

Vor dem geplanten B+E-Umbau lief eine Standortbestimmung. Ergebnis: B+E
sind reine Hygiene (oben herabgestuft), der einzige spuerbare Hotspot ist
ein dritter, separater Befund.

- ✅ **PERF-1: SettingsViewModel-ctor Lazy-Tab-Load** — erledigt 
  2026-05-29 (`70df47db`). ctor-Zeit von **1012ms auf 15ms** (67×) 
  durch `Task.Yield()` in 7 Refresh-Methoden (Stil-Vorbild 
  `BackupService.cs:141`) + Doppel-Call-Konsolidierung (`GetStatsAsync` 
  wurde aus `RefreshBlCacheStatsAsync` + `RefreshBrickStoreStatsAsync` 
  doppelt gerufen, jeweils 500ms; jetzt EINMAL via `RefreshStatsAsync` 
  + `ApplyBlCacheStats` / `ApplyBrickStoreStats`-Helper). Cache-Stats-
  Felder visuell verifiziert. **Diagnose-Lehre:** erste These 
  (RefreshCacheStats/GetStats-Verzeichnis-Scan) war plausibel aber 
  falsch — durch gezielte Per-Call-Messung widerlegt (9ms statt der 
  vermuteten ~900ms), dann zweite Diagnose-Runde + gezielte Messung an 
  der richtigen Stelle (`BlCacheRepository.GetStatsAsync:982`) hat die 
  wahre Wurzel mit Zahlen belegt. Mess-Vorlauf hat ~1h Pflaster-Fix 
  verhindert.
- 📋 **PERF-2: RecalcKind-Helper-Exception-Verhalten** — Vorbedingung fuer
  E (Context-Piggyback). Aktuell schlucken die RecalcKind-Helper
  Exceptions in WRN (`MinifigPersistenceService.cs:94`, `PartLookupService
  .cs:56`, `FloatingPartTransferService.cs:166`, `UndoService.cs:121`,
  `DataHealService.cs:103`). Bei gemeinsamem Context wuerde ein Recalc-
  Fehler die Haupt-Transaktion mitreissen statt isoliert geloggt zu
  werden. Bewusste Verhaltensentscheidung noetig bevor E umgesetzt werden
  kann (weiter schlucken vs. durchreichen, plus Doku im Pattern-Katalog).
  Schwere: L. Aufwand: Teil von E falls E je kommt. Quelle: Diagnoser
  2026-05-29.
- ℹ️ **PERF-3: BL-Sync 2,5-3,2s Latenz** — externe BL-API-Latenz fuer 9361
  Lots (`app-20260529.log`), kein client-seitiger Storm, kein Codefix
  moeglich. Nur als Notiz: nicht weiter verfolgen, nicht als Bug
  behandeln. Die separat gelistete UPSERT-Optimierung wuerde nur ~15%
  sparen (85% sind API-Antwortzeit). Quelle: Diagnoser 2026-05-29.

#### Aus Diagnoser-Lauf 2026-06-07 (Performance-Drift im Temp-Inventar/Sortier-Tab)

Praxis-Befund nach mehreren Tagen Nutzung von v0.1.25-beta.1: *"Je mehr
ich scanne, desto zaeher laeuft das Programm — besonders im Temporaeren
Inventar und im Sortier-Tab."* Die Diagnose belegt: die Zaehigkeit
skaliert **nicht** mit den sichtbaren Listen (TrackedMinifigs=0,
FloatingParts=88 — winzig), sondern mit den zwei monoton wachsenden
Strukturen `image_cache.db` (12.113 Eintraege, ~1 GB) und `ScanEvents`
(2.346, ohne Index). Das erklaert das "je mehr ich scanne"-Wording exakt.
Read-only-Diagnose — **naechster Schritt ist gezielte Mess-Instrumentierung,
NICHT direkter Fix** (Lehre aus PERF-1: erst messen, dann fixen).

- ✅ **PERF-4: Image-Cache-Stats-Storm (HAUPTWURZEL)** — erledigt
  2026-06-07 in zwei Commits: Fix + Mess-Code-Cleanup direkt
  hintereinander.
  
  **Vor dem Fix:** `PersistentImageCache.TouchAccess` feuerte
  `StatsChanged` bei jedem Bild-Cache-Hit (~105 Bild-Hits pro Scan,
  multipliziert ueber 5 Live-VMs). `MainViewModel.UpdateCacheStatusText`
  rief daraufhin pro Event `GetStats()` = COUNT+SUM Full-Scan ueber
  12.113 Zeilen image_cache.db auf dem UI-Thread. Storm-Spitzen bis
  63 GetStats-Calls/s, UI-Blockade-Spitzen 47-98%.
  
  **Fix (zwei Eingriffe):**
  - (①) `StatsChanged` aus `TouchAccess` entfernt — Touch aendert nur
    `last_accessed_at`, das ist fuer Count+Size irrelevant. Das Event
    feuert jetzt nur noch bei Add/Clear (echte Count/Size-Aenderungen).
  - (②) `UpdateCacheStatusText` per Throttle: GetStats wird hoechstens
    1×/s aufgerufen. Defense-in-Depth fuer den Add/Clear-Rest-Pfad.
  
  **Verifikation (Praxis-Lauf vor/nach):**
  | Metrik | Vor | Nach | Ergebnis |
  |---|---|---|---|
  | GetStats-Calls/s (Ø) | 17,8 | 1,0 | ✅ |
  | GetStats-Calls/s (Max) | 63 | 1 | ✅ |
  | Storm-Spitzen >50/s | 20 Fenster | 0 | ✅ |
  | Σ GetStats-Zeit (5 min) | 14,66s | 0,22s | 66× weniger |
  | UI-Blockade Spitze | 47-98% | ~0,7% | ✅ |
  | GetStats-Latenz/Call | 6,3ms | 6,9ms | ⟷ (③ nicht angefasst) |
  
  **③ (inkrementelles Count/Size statt Full-Scan)** bleibt Backlog-
  Reserve — bei 1 Call/s ist die 6,9ms-Latenz harmlos.
  
  **Diagnose-/Engineering-Lehre (siehe Diagnoser-Block oben):** Klassifi-
  zierungen muessen mit Datenmenge mitwachsen. Mess-Vorlauf hat den
  Fix-Pfad gerettet — die erste Diagnoser-These war "5×-Multiplikator",
  die Messung zeigte tatsaechlich ~105× pro Scan (105 Bild-Hits × 5 VMs
  abgekoppelt) und belegte ① als ehrliche Wurzel, nicht nur als Pflaster.
  Quelle: Diagnoser 2026-06-07 + Mess-Vorlauf + Praxis-Verifikation.
- ✅ **PERF-5: ScanEvents-Tabelle Timestamp-Index** — erledigt 2026-06-07
  (`a4789ec5`). `HasIndex(e => e.Timestamp).IsDescending()` in
  `UserDataContext.cs` + EF-Migration `AddScanEventTimestampIndex` (reines
  `CREATE INDEX IX_ScanEvents_Timestamp ON ScanEvents (Timestamp DESC)`).
  Hauptpfade `ORDER BY Timestamp DESC LIMIT N` profitieren
  (`RecentScansViewModel`, `UndoService.GetUndoableActionsAsync`,
  `UndoService.GetHistoryAsync`). **Type-Index NICHT angelegt:** Type
  wird nur in einer Query gefiltert (`UndoService: Type != UndoApplied`),
  SQLite filtert das auf den Top-N Timestamp-Ergebnissen schnell raus.
  **Migrations-Pfad-Lehre (CLAUDE.md-Kandidat):** `dotnet ef database
  update` zielt auf die Design-Time-Temp-DB (`design_time_userdata.db`),
  NICHT auf `%APPDATA%\HBSort\userdata.db`. Echte User-DB migriert sich
  beim App-Start via `Database.MigrateAsync()` (`App.xaml.cs:481`). Bei
  aktueller Datenmenge (~2,4k Events) Migration instant, Effekt nicht
  spuerbar — Architektur-Hygiene fuer kuenftiges Wachstum. Tests 654/654
  gruen, Praxis-Test: App-Start sauber, Verlauf + Scans + Undo wie
  gewohnt. Quelle: Diagnoser 2026-06-07.
- ℹ️ **PERF-6: Spalten-Persistenz-Save (Notiz, kein akuter Fix)** —
  `InventoryListView.xaml.cs:711` (`InventoryGrid_Sorting`) schreibt die
  ganze `settings.json` fire-and-forget bei jedem Sort-Klick. **Nicht**
  Treiber des aktuellen Symptoms (feuert nur bei aktivem Sort-Klick,
  selten — nicht "je mehr ich scanne"), aber als neuer Schreibpfad
  notiert. Falls je auffaellig: Debounce. Schwere: **L**. Quelle:
  Diagnoser 2026-06-07.

**Engineering-Lehre (2026-06-07):** Klassifizierungen (Hygiene / kritisch /
nicht-relevant) muessen mit der Datenmenge mitwachsen. Was bei kleinem
Bestand Hygiene war, kann bei groesserem Bestand zur Wurzel werden (hier:
B war am 29.05. bei kleinerer Datenmenge zu Recht Hygiene, wirkt jetzt
als 5×-Multiplikator von PERF-4). Der ehrliche Widerruf einer frueheren
Klassifizierung ist Teil der Disziplin, **kein Konsistenz-Problem**.
Konsequenz: Performance-Klassifizierungen mit der zugrundeliegenden
Datenmengen-Annahme versehen und bei Praxis-Befunden re-evaluieren statt
als endgueltig behandeln.

**Reihenfolge-Empfehlung:** (1) gezielte Mess-Instrumentierung PERF-4/5
(~30min, Implementer, NICHT fixen) → Wurzel mit Zahlen belegen; (2) PERF-4
zuerst (groesster Hebel, isolierter Anzeige-Pfad, niedrigstes Risiko);
(3) PERF-5 mitnehmen (billig, eigener Vektor); (4) B nur falls nach
PERF-4+5 noch Symptom messbar — moeglich, dass PERF-4 das Symptom allein
aufloest.

#### Aus Praxis-Nutzung v0.1.25-beta.1 (2026-06-07) — Baubar-Tab

Zwei Befunde aus mehreren Tagen Nutzung des "Was kann ich bauen"-Tabs.
Angehen NACH PERF-4 (eigene Iteration, da fremder Code-Pfad und Risiko
verwobener Sammel-Commits sonst wieder zuschlägt — Lehre beta.11-13).

- ✅ **BUILD-1: 100%-BL-Vorschlaege erscheinen im Baubar-Tab** —
  erledigt 2026-06-07 in zwei Commits: `cac686c4` (Perf-Schicht: Wurzel-
  Fix + Index + N+1-Bulk) + `b0ce7898` (Feature: Pool-Erweiterung).
  Praxis-verifiziert mit Holger's echtem 9k-BL-Inventar.

  **Wurzel:** Der Kandidaten-Pool wurde ausschliesslich aus dem
  FloatingPool aufgebaut. Eine Figur ohne HBSort-Teile erzeugte keinen
  OR-Treffer in `FindMinifigsContainingPartsAsync` und wurde nie
  evaluiert. Plus: Early-Return bei `floats.Count == 0` machte den Tab
  komplett leer, auch bei vollem BL-Inventar.

  **Fix (Feature-Schicht in `b0ce7898`):**
  - Neue Methode `IBlInventoryService.GetAvailablePartTuplesAsync` (Lots
    mit `Quantity - ReservedQuantity > 0`, ColorId=null wird
    uebersprungen weil Subset-Teile immer eine konkrete ColorId tragen)
  - Bei aktivem Toggle Pool-Union (FloatingPool ∪ BL-Tuples), dann
    normaler Reverse-Lookup
  - Early-Return aufgeweicht: leerer Pool + Toggle AN evaluiert trotzdem,
    Treffer landen in Kat.2 (Shop-completable, MatchPercent=100,
    `IsBlShopAddition=true`)
  - Toggle-AUS-Vertrag bleibt exakt erhalten (Regressionsschutz-Test)

  **Drei Skalierungs-Wurzeln vorab gefixt (`cac686c4`):**
  Beim Verbinden mit Holger's echtem 9k-BL-Inventar wurden drei
  unabhaengige Skalierungs-Achsen sichtbar, die zusammen wirken:
  - **Crash-Wurzel:** `FindMinifigsContainingPartsAsync` baute OR-Listen
    (`WHERE (PartNo='A' AND ColorId=1) OR ...`). Bei 9000 Tuples ueber
    1000 Expression-Tree-Nesting-Levels → `SQLite Error 1`. Fix: OR-Liste
    durch TEMP-Tabelle + INNER JOIN ersetzt (Stil analog
    `ReplaceSubsetsAsync`).
  - **Index-Wurzel:** Nach dem Temp-Tabelle-Umbau kein Crash mehr, aber
    JOIN gegen die ~1,4-Mio-zeilige `bl_subsets`-Tabelle ohne Index auf
    der Temp-Tabelle erzeugte einen **quadratischen Scan**: 43,7s auf
    Holger's echter DB. Fix: `CREATE INDEX IF NOT EXISTS
    temp_idx_search_parts ON temp_search_parts(part_no, color_id)` nach
    dem Insert, vor dem JOIN. Eine Zeile, **1.400× Hebel** (43,7s →
    0,037s, identisch 3651 Parents).
  - **N+1-Wurzel:** Bei Pool-Erweiterung lieferte der Reverse-Lookup
    ~3651 Minifig-Kandidaten. Die VM iterierte mit `GetSubsetsAsync` +
    `GetItemAsync` pro Kandidat = ~7000 serialisierte fake-async-Queries
    auf dem UI-Thread → mehrere Sekunden bis Minuten UI-Hang. Fix: neue
    Bulk-Methode `IBlCacheRepository.GetSubsetsBulkAsync` (500er-Chunks
    via IN-Klausel) + bestehende `GetItemSummariesAsync` aktiv genutzt.
    Match-Berechnung iteriert jetzt im Speicher gegen vorgeladene
    Dictionaries. Analog `AnalyzeBlShopHelpAsync` mit
    `GetAvailableQuantitiesAsync`.

  **Tests (insgesamt 672 gruen, +20 vs. v0.1.25-beta.1-Baseline):**
  - Charakterisierungs-Tests in `BuildSuggestionsViewModelTests.cs`
    (`a89ce231`, separater Commit als Sicherheitsnetz): 10 Invarianten-
    Tests + 1 Charakterisierungs-Test der die alte Bug-Lage festschreibt.
    Der wurde im Fix-Commit invertiert (→
    `BlOnlyFigure_withToggleOn_appearsAsShopCompletable`) + 4 neue Soll-
    Tests.
  - Reproduktions-Test fuer den SQLite-Expression-Tree-Crash (2000
    Tuples gegen alten Code → SqliteException, gegen Fix → gruen).
  - **EXPLAIN-QUERY-PLAN-Test** als strukturelle Sicherheit fuer den
    Temp-Index: assertiert dass der JOIN-Plan `SEARCH ... USING INDEX`
    enthaelt, nicht `SCAN`. Daten-unabhaengig, faengt Regression wenn
    jemand das `CREATE INDEX` versehentlich rauslöscht. Bessere
    Strategie als Timing-Tests bei SQLite-Skalierungs-Fragen.
  - End-to-End-Skalierungs-Test fuer die N+1-Bulk-Umstellung (50
    Minifigs, ~250 Subsets, ~5000 BL-Tuples, leerer FloatingPool — der
    Test haengt am BUILD-1-Feature, lebt also in `b0ce7898`).

  **Transparenz-Hinweis:** `GetFindMinifigsJoinQueryPlanForDiagnostics`
  ist eine test-only API-Erweiterung am `BlCacheRepository`. Konvention-
  konsistent zu `BlBulkImportService`. Sollte langfristig durch
  `InternalsVisibleTo` ersetzt werden — kleines Backlog-Item.

  **Engineering-Lehre der Skalierungs-Strecke (2026-06-07):** Heute drei
  aufeinanderfolgende Iterationen mit gruenen Tests, die in der Praxis
  alle umfielen — jede an einer anderen Skalierungs-Achse:

  | Anlauf | Was Tests pruefen | Was Praxis-Daten enthielten |
  |---|---|---|
  | 1. Implementer-Fix | Kleine Pool-Groessen | 9k-BL-Inventar |
  | 2. Temp-Tabelle | 250 bl_subsets-Eintraege | 1,4 Mio bl_subsets |
  | 3. Bulk-Lookup vor Index-Fix | Synthetisches Tempfile | Plan-Wechsel ab Grossenordnung |

  **Lehre:** Skalierungs-Tests muessen entweder den ECHTEN Datenpfad mit
  realistischer Datenmenge UND Datentopologie simulieren — oder
  strukturell pruefen (EXPLAIN QUERY PLAN, Bulk-Methode-Aufruf-Count)
  statt zeitlich. Synthetische Test-DB mit 250 Subset-Eintraegen
  reproduziert nicht denselben Query-Plan wie 1,4 Mio Zeilen.
  Strukturelle Asserts sind CI-tauglich und daten-unabhaengig.
  Quelle: User-Praxis 2026-06-07 + drei Diagnoser-Laeufe + Praxis-
  Verifikation auf echter 9k-BL-DB.

- ✅ **Torso-/Combined-Part-Komponenten im Scan-Result** — erledigt
  2026-06-07 (`c5deda7c`). Beim Scan eines montierten Torsos (oder
  anderen "Combined Parts" wie Wheels+Reifen, Turntables) zeigt der
  PartLookupView einen aufgeklappten Expander "Komponenten dieses
  Teils" mit allen Sub-Teilen aus bl_subsets (parent_type='P'). Reine
  Anzeige aus lokalem Cache, KEIN BL-API-Call.
  
  **Inhalte:**
  - DTO `PartComponent` (record) + `GetPartComponentsAsync` in
    IBlCatalogService/BlCatalogService
  - Direkter Pfad + Reverse-Fallback via FindParentsByItemAsync
    (Robustness-Reserve)
  - Filter `is_alternate=0 AND is_counterpart=0 AND is_from_supersets=0`
  - PartComponentViewModel-Wrapper mit Bild + Farb-Swatch + Menge
  - Expander default aufgeklappt (User-Hauptanwendungsfall ist Torso-
    Scan, will Komponenten sofort sehen)
  - Grund-Teil mit Badge "Grund-Teil" gekennzeichnet (Self-Reference-
    Heuristik: ItemNo ist Praefix von parent_no)
  
  **Zwei nachgezogene Konsistenz-Fixes:**
  - **B3 (Bild-Fallback):** Beim Grund-Teil mit ColorId=0 nutzt der
    Bild-Lookup die Scan-Farbe (`pending.BlColorId`) als Fallback.
    Wurzel: bare-Torsos haben oft keine Bilder unter Color=0 in BL,
    nur unter konkreten Farben (per Log + BL-Catalog-Screenshot
    verifiziert: 973pb1727 unter `/PN/0/` = 404, unter `/PN/85/` =
    existiert).
  - **B3.5 (Color-Anzeige):** Konsequent dazu wird auch ColorId/
    ColorName/Swatch des Grund-Teils auf die Scan-Farbe gesetzt —
    sonst zeigt der Expander Bild in konkreter Farbe, aber
    Anzeige-Farbe "(Not Applicable)". Praxis-Befund + Fix in selber
    Iteration.
  
  **Brickognize-ID-Form geklaert (per Praxis-Test):**
  Bei montiertem Torso liefert Brickognize die **complete-ID**
  (z.B. `973pb1727c01`), nicht die bare-ID. Der direkte Pfad
  `GetSubsetsAsync("P", id)` reicht. Der Reverse-Fallback ist
  Robustness-Reserve. Diese Info dauerhaft im Code-Kommentar +
  hier festgehalten fuer kuenftige Subset-Features.
  
  **Tests:** 5 Core-Tests fuer GetPartComponentsAsync (atomar, direkt,
  Filter, Reverse-Fallback, IsBaseItem-Flag). 677/677 gruen.
  
  Pattern-konform (docs/ux-patterns.md): klickbare Bilder via
  b:ImageZoom, keine neuen Buttons → kein Footer-Drift. Quelle:
  User-Wunsch + Konzept docs/v0.1.25-konzept-torso-komponenten.md.

- 📋 **BUILD-2: Wartende Figuren als zusätzliche Quelle** (Feature,
  konzeptionell). Aktuell betrachtet der Baubar-Tab zwei Quellen:
  HBSort-Lager (FloatingPart-Stock) + BL-Shop-Inventar. Wartende Figuren
  (TrackedMinifig.Status=Waiting) tauchen nicht als Teile-Quelle auf.
  User-Wunsch: wartende Figuren sollen auch zählen — deren bereits
  gesammelte Teile könnten für Vorschläge zu anderen Figuren genutzt
  werden.
  **WICHTIG: Wechselwirkungen mit Reservierungs-/Sortier-Logik müssen
  vor Bau geklärt werden:**
  - Wenn Teile aus wartender Figur A als Quelle für neue Figur B
    "gezählt" werden — was passiert, wenn A später selbst komplett
    wird? Wem gehören die Teile dann?
  - Reservierung nötig (analog BL-Reservierung)? Oder rein virtuelle
    Anzeige ("könnte gebaut werden wenn man A ausschlachtet")?
  - Was ist mit Required-Parts in A die noch fehlen (Waiting-Status hat
    eh nur Teil-Bestand)? Zählt nur das was A bereits hat?
  - Wenn User in Baubar einen Vorschlag anlegt, der A ausschlachtet:
    explizite Bestätigung nötig ("Figur A wird dadurch zurück auf
    weniger gesammelte Teile gesetzt")?
  Empfehlung: erst ux-analyst Mode B + ggf. challenger über das Konzept
  laufen lassen, weil das mehr ist als BUILD-1. Quelle: User-Praxis
  2026-06-07. Schwere: M (sinnvolle Erweiterung, aber konzeptionell).
  Aufwand: Konzept ~30-45min + Bau ~2-3h, je nach Reservierungs-
  Entscheidung.

Reihenfolge-Empfehlung BUILD-1 vor BUILD-2: BUILD-1 ist ein klar
abgegrenzter Bug-Fix, BUILD-2 braucht Konzept-Vorlauf. Im Idealfall:
BUILD-1 als eigene kleine Iteration nach PERF-4, dann BUILD-2 mit
Konzept + Bau in einer weiteren.

- 📋 **BUILD-3: Wartende Figuren tauchen nicht als baubar auf, obwohl
  Lager+BL die fehlenden Teile haben** (User-Praxis 2026-06-07, Lesart
  vom User bestaetigt). Eine wartende Figur (Status=Waiting) hat
  typischerweise 3 von 5 Teilen gesammelt; die fehlenden 2 koennten
  aus dem HBSort-Lager (FloatingPart-Stock) oder dem BL-Inventar
  ergaenzt werden. Aktuell erscheint diese Figur NICHT im Baubar-Tab
  als komplettierbarer Vorschlag.
  
  **Status:** Konzept liegt vor in
  `docs/v0.1.25-konzept-build3-wartende-figuren-baubar.md` (lokal-only,
  gitignored). **Wartet auf User-Freigabe** — ux-analyst-Empfehlung ist
  **Modell (b): gruenes "jetzt fertigstellbar"-Badge im bestehenden
  WaitingDetailView-Tab**, NICHT als Eintrag im Baubar-Tab. Begruendung:
  WaitingDetailView zeigt schon wartende Figuren mit effektiv fehlenden
  Teilen — deckt ~80% des Use-Case ab. Modell (b) ergaenzt nur die
  Deckbarkeits-Anzeige ("liesse sich JETZT mit Lager+BL fertigstellen"),
  reine Anzeige, keine Buchung. Eliminiert beide Risiken strukturell:
  Doppel-UI mit Komplettieren-Workflow + Reservierungs-Kollision.
  
  KISS-Verdikt des ux-analyst: grenzwertiger Komfort-Gewinn,
  BUILD-1 war die echte Funktions-Luecke. Aufwand Modell (b) ~1,5h.
  Eigener Datenpfad (TrackedMinifigPart/EffectiveCollected), teilt
  keinen Code mit BUILD-1.

- 📋 **PERF-7: BuildSuggestionsScalingTests Wall-Clock-Assert ersetzen** —
  Nebenbefund 2026-06-07 beim B3.5-Implementer-Lauf: der Skalierungs-
  Test in HBSort.Tests (Commit `b0ce7898`) nutzt einen Timing-Assert
  (`<2s`) und flakt unter Parallel-Last (mal 2,60s, mal gruen). Auf CI
  wird er irgendwann rot blinken obwohl nichts kaputt ist.
  
  **Ironie:** das ist exakt das Muster gegen das die heute-frische
  Engineering-Lehre warnt ("Skalierungs-Tests muessen strukturell
  pruefen statt zeitlich"). Der Test ist ein Lehre-an-uns-selbst-Punkt.
  
  Fix-Empfehlung: Timing-Assert durch strukturellen ersetzen, z.B.
  Query-Call-Counter ("RefreshAsync ruft GetSubsets/GetItem max.
  O(Chunks)-mal statt O(Kandidaten)-mal"). Pruefen den N+1-Fix
  deterministisch ohne Zeitschranke. Aufwand: ~30min. Schwere: L
  (aktuell nur Test-Flakiness, kein Produkt-Bug). Kein Druck — aber
  bevor es die Pipeline rot blinken laesst aufnehmen.
  Quelle: Implementer-Bericht 2026-06-07.

#### Aus Praxis-Nutzung (PERF-5-Test, 2026-06-07) — Beschreibung-Spalte

Waehrend des PERF-5-Praxis-Tests im Temporaeren Inventar aufgefallen.
Nicht durch die Migration ausgeloest — betrifft die UI-Spalten-Darstellung
unabhaengig.

- 📋 **UI-1: Beschreibung-Spalte mehrzeilig mit fettem Teiletyp-Praefix**
  — die Beschreibung-Spalte im Temporaeren Inventar laeuft aktuell
  einzeilig und wird dadurch sehr breit (siehe Praxis-Screenshot: Texte
  wie "Torso Hospital Lab Coat, Open Collar, Stethoscope, Pocket Pen,
  and Thermometer Pattern / White Arms / Yellow Hands" verdraengen alle
  anderen Spalten nach rechts). User-Wunsch:
  - Teiletyp-Praefix ("Torso" / "Minifig" / "Helmet" etc.) als **erstes
    Wort fett**
  - Restlicher Text **mehrzeilig** umgebrochen darunter
  - Spalte kann dadurch deutlich schmaler werden, Lesbarkeit steigt
  
  Offene USER-ENTSCHEIDUNGEN (vor Bau zu klaeren):
  - **Geltungsbereich**: nur Temporaeres Inventar, oder auch BL-Inventar-
    Tab, Verlauf-Tab, andere DataGrids mit Beschreibung?
  - **Zeilen-Cap**: TextWrap unbegrenzt oder max 3 Zeilen mit Ellipse?
  - **Praefix-Definition**: nur das erste Wort fett? Oder "bis zum
    ersten Komma"? Oder ein dedizierter Teiletyp-Lookup (z.B.
    PartName-Heuristik aus CategoryBinMappingService.DeriveCategoryFromPartName)?
  Schwere: M (Lesbarkeit + Platz). Aufwand: ~1-2h Bau + 30min
  Konzept-Klaerung. Quelle: User-Praxis 2026-06-07.

- 📋 **UI-2: Spalten-Layout im Temporaeren Inventar kommt "komisch"
  hoch** — User-Befund 2026-06-07: "die Tabelle vom Inventar ist komisch
  gewesen, habe sie zurecht gezogen". Nicht-spezifizierte Layout-
  Auffaelligkeit beim Oeffnen des Tabs — koennte mehrere Ursachen haben:
  - Spalten-Persistenz vom letzten App-Lauf inkompatibel mit aktuellem
    Spalten-Layout (neue Spalte hinzugekommen?)
  - Defaultbreiten nicht gut gewaehlt fuer aktuelle Beschreibung-Spalte
    (siehe UI-1 — verschwindet ggf. komplett wenn UI-1 umgesetzt ist)
  - Anderer DataGrid-Effekt
  Erst BEOBACHTEN bei kuenftigen Tab-Oeffnungen ob es reproduzierbar ist,
  DANN diagnostizieren. Wenn UI-1 umgesetzt wird, koennte sich UI-2
  miterledigen. Schwere: L bis ungewiss. Aufwand: ungewiss — erst Reprol
  belegen.

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

### v0.1.25-beta.1 (2026-06-02, Tag auf `0bb0a7bd`) - Sammel-Beta: Performance + Persistenz + Bulk-Bin + UI-Politur

Sammel-Beta mit fuenf praxis-getesteten Inhalten seit v0.1.24. 652/652 Tests
gruen. Pipeline (Run 26832417719) alle drei Jobs gruen, isPrerelease=true.

- ✅ **PERF-1: SettingsViewModel-ctor Lazy-Tab-Load** (`70df47db`) — ctor-Zeit
  von 1012ms auf 15ms (67×). Details im PERF-1-Eintrag oben.
- ✅ **UX-Pattern-Katalog `docs/ux-patterns.md`** (`63b2af7d`) — 10 Patterns +
  Drift-Inventur, verbindliche Referenz fuer kuenftige UI-Arbeit.
- ✅ **Kosmetik-Sweep Welle 1-3** (UX-1..7 + B1/B6/B10, 11 Befunde) —
  Schliessen-Buttons/Footer/Tooltips vereinheitlicht, BL-Shop-Badge-Beschriftung,
  Dialog-VM-DI, Silent-Catch-Logging, Bau-Vorschlaege-Toolbar (UX-7).
- ✅ **Spalten-Persistenz im Temporaeren Inventar** (`17e06216`) — Sortierung +
  Spaltenbreiten bleiben pro App-Start erhalten.
- ✅ **Bulk-Bin-Aktionen im Settings-Tab Lagerfaecher** (`0bb0a7bd`) — markierte
  Faecher sammelweise leeren/loeschen mit Vorschau + Bestaetigung; belegte
  Faecher werden beim Loeschen uebersprungen und im Ergebnis-Report aufgefuehrt.

Bewusst NICHT in beta.1: B2 (Footer-Layout in ManageIgnored/MassUpdate-Dialog)
bleibt als offenes 📋-Item. PERF-2 dokumentiert (Vorbedingung fuer E),
B+E auf Hygiene-Status herabgestuft.

### v0.1.24-beta.15 (2026-05-28) - UI-Politur: Tab-Umbenennung + Reihenfolge

- ✅ **Tab "Lagerliste" → "Temporäres Inventar" umbenannt** (`97a53c63`) —
  klare Abgrenzung zum "BrickLink Inventar"-Tab. Sichtbare UI-Texte,
  Tooltips, View-Überschrift, Hotkey-Hinweise, Hilfe-Dateien (03/04/06/
  07/09/10), README. Interne Code-Namen (InventoryListView etc.) +
  "Lagerfach"-Begriffe bewusst unverändert.
- ✅ **Tab-Reihenfolge** auf Workflow-Logik: Sortieren | Temporäres
  Inventar | BrickLink Inventar | Verlauf | Hilfe. Index-Referenzen
  (F1→Hilfe, Strg+S/L/H) angepasst, Hotkeys praxisgetestet.

### v0.1.24-beta.11 bis beta.14 (2026-05-28) - BL-Inventar Phase 3-4 + Dialog-Vereinheitlichung

Gemeinsamer Commit `c060c4b3` (beta.11-13, 47 Files) + `b3a8e044`
(beta.14). Verwoben entwickelt, daher als Sammel-Commit (siehe
Engineering-Lehre unten). 616/616 Tests grün.

**beta.11 — Dialog-Vereinheitlichung + Baubar-Erweiterungen:**
- ✅ Figur-Dialoge auf einheitliche Optik (MinifigSummary als Referenz).
- ✅ BuildSuggestions: zwei Anzeige-Modi (HBSort / mit BL-Shop),
  persistenter Ignorieren-Button (`IgnoredBuildSuggestion`-Entity +
  Migration), ManageIgnored-Dialog, "Alle Preise holen".
- ✅ BuildSuggestionDetailDialog: Quellen-Auswahl pro Teil (Lager/Shop),
  Anlegen mit BL-Reservierung in einem Rutsch + SortInstruction.
- ✅ `ReleaseSingleReservationAsync` + `GetReservationsForLotAsync`
  (Reservierungs-Sektion im BL-Inventar-Detail-Panel).

**beta.12 — BL-Inventar Phase 4 Mass-Update-Export:**
- ✅ `GenerateMassUpdateXmlAsync` (DELETE / QTY -N), `PendingExport`-
  Entity + Migration, MassUpdateExportDialog.
- ✅ `VerifyExportAsync`: Erfolg/Fehlschlag pro Lot,
  Reserved→Collected-Konvertierung, Auto-Resync nach vollem Erfolg.
- ✅ `ScanType.BlReservationConvertedToCollected`.

**beta.13 — Kandidaten-Filter + V5-Auflösung:**
- ✅ Kandidaten-Filter von Effective auf physische Lücke
  (`QuantityCollected < QuantityNeeded`) umgestellt — Figuren mit
  BL-Reservierung erscheinen wieder als Scan-Kandidaten, V5 erreichbar.
- ✅ V5 Reverse-Match-Auflösung: physisches Teil ersetzt BL-Reservierung
  mit Anweisung (U-Lot: loses Teil zurück in BL-Shop-Fach; N-Lot: Tausch).
- ✅ `Status != Dismantled` defensiv an 4 Filter-Stellen
  (PartLookupService.cs:84, StorageBinService.cs:887/998,
  InvalidBinKindException.cs:104) + 4 neue Tests.

**beta.14 — Auto-Sync vor Export** (`b3a8e044`):
- ✅ MassUpdateExportDialog synct beim Öffnen automatisch das Inventar
  bevor das XML erzeugt wird (gegen aktuelle BL-Mengen).
- ✅ `SyncInventoryAsync` liefert `BlInventorySyncResult` (LotCount,
  Restored, Capped, Lost, SyncedAt) statt int. Loading-State, 3 Stale-
  Fallback-Pfade (Auth/RateLimit/Generic), Cap-Hinweis im SyncInfoText.

**beta.10 — Stable-Blocker** (`ce65bf75`, war schon getaggt):
- ✅ `ReleaseAllForMinifigsAsync` vor allen 6 Lösch-/Zerlegungs-/Cleanup-
  Pfaden (Audit-Befund H1) — keine Geist-Reservierungen.
- ✅ Quantity-Cap im Snapshot-Restore (H4).

**Engineering-Lehre (2026-05-28):** beta.11-13 wurden verwoben
entwickelt und erst spaet committet — die nachtraegliche Trennung in
saubere Einzel-Commits scheiterte an Cross-Iteration-Build-
Abhaengigkeiten (gemeinsame Service-Files). Konsequenz fuer die
Zukunft: nach jedem gruenen Praxis-Test sofort committen, nicht
mehrere Iterationen aufstauen (verstaerkt Prinzip 3.1).

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
| **v0.1.24-beta.1** | ✅ released (Phase 1+1.5+2a+2a-Polish+2b+2b-Hotfix+3, 2026-05-15..17) | Modal-Pattern + Wizard 2-stufig + IsManuallyClaimed + Combobox-Suffix + tote Dialoge entfernt + Audit. 589 Tests grün. |
| **v0.1.24-beta.6** | ✅ gemerged (2026-05-28) | BL-Inventar Phase 1: BlInventoryLot-Entity + Migration + IBlInventoryService + Settings-Sync. |
| **v0.1.24-beta.7** | ✅ gemerged (2026-05-28) | BL-Inventar Phase 2: Tab "BrickLink Inventar" (DataGrid, Filter, Detail-Panel, Lazy-Thumbnails). |
| **v0.1.24-beta.8** | ✅ gemerged (2026-05-28) | BL-Inventar Phase 3: Komplettieren-Integration (QuantityReservedFromBl, BlReserveDialog, Baubar-BL, Badge, Toast). |
| **v0.1.24-beta.10** | ✅ getaggt (`ce65bf75`) | Stable-Blocker: ReleaseAllForMinifigsAsync vor 6 Lösch-Pfaden (H1) + Quantity-Cap (H4). |
| **v0.1.24-beta.11-13** | ✅ gemerged (`c060c4b3`, 2026-05-28) | Dialog-Vereinheitlichung + Ignorieren + BL-Inventar Phase 4 Export + V5-Filter-Fix + Dismantled-Defensive. 604→616 Tests. |
| **v0.1.24-beta.14** | ✅ gemerged (`b3a8e044`, 2026-05-28) | Auto-Sync vor Mass-Update-Export (BlInventorySyncResult, Stale-Fallbacks, Cap-Hinweis). 616 Tests grün. |
| **v0.1.24-beta.15** | ✅ gemerged (`97a53c63`, 2026-05-28) | UI-Politur: Tab "Temporäres Inventar" + Tab-Reihenfolge + Endanwender-Doku-Update. |
| **v0.1.24** | ✅ stable (2026-05-29, Tag `e3b8f9ac`) | BL-Shop-Integration: Inventar-Sync, Reservieren beim Komplettieren, Mass-Update-Export, V5-Reservierungs-Aufloesung, Dialog-Vereinheitlichung, "Temporaeres Inventar"-Tab. |
| **v0.1.25-beta.1** | ✅ released (2026-06-02, Tag `0bb0a7bd`) | Sammel-Beta: PERF-1 (Settings-ctor 1012ms→15ms), UX-Pattern-Katalog, Kosmetik-Sweep Welle 1-3 (UX-1..7 + B1/B6/B10), Spalten-Persistenz Temp-Inventar, Bulk-Bin-Aktionen. 652 Tests grün, isPrerelease=true. |
| **v0.1.25** | 📋 in-arbeit (Tag-Kandidat: v0.1.25-beta.2) | **PERF-4** + **PERF-5** + **BUILD-1** + **Torso-Komponenten** alle erledigt + dokumentiert, ungetaggt. Performance-Track: GetStats-Calls/s 17,8 → 1,0 (∼18×), UI-Blockade-Spitze 47-98% → <1%, ScanEvents-Queries indiziert, FindMinifigsContainingParts 43,7s → 0,037s (1.400× ueber Temp-Index), BuildSuggestions N+1 → Bulk-Load. BUILD-1: 100%-BL-Vorschlaege erscheinen im Baubar-Tab. Torso-Komponenten: Combined-Part-Subteile sichtbar beim Scan inkl. Color-Konsistenz fuer Grund-Teile. Naechster Schritt: beta.2 taggen. Offene Kandidaten: BUILD-3 (Konzept liegt vor, wartet auf User-Freigabe — Modell b empfohlen), UI-1 (Beschreibung-Spalte, 3 User-Entscheidungen offen), B2 (Footer-Layout), PERF-7 (Wall-Clock-Test ersetzen), B-Re-Evaluierung (subjektiv beim Sortieren ueber mehrere Tage). |
| **v0.2.0** | 💭 Brainstorming | grosse Features aus Backlog (siehe oben) |

Konvention:
- Patch-Iteration (v0.x.Y) für Cleanup + kleine Features
- Minor-Iteration (v0.Y.0) für große Features oder Architektur-Änderungen

---

*Zuletzt aktualisiert: 2026-06-07 — **PERF-4 + PERF-5 + BUILD-1 +
Torso-Komponenten** alle vier erledigt und dokumentiert, ungetaggt auf
`main`. Performance-Track-Bilanz: GetStats-Calls/s 17,8 → 1,0 (~18×,
PERF-4), ScanEvents indiziert (PERF-5), FindMinifigsContainingParts
43,7s → 0,037s (1.400× ueber Temp-Index, im BUILD-1-Track entdeckt),
BuildSuggestions-N+1 → Bulk-Load (im BUILD-1-Track entdeckt). BUILD-1-
Feature: 100%-BL-Vorschlaege erscheinen jetzt im Baubar-Tab. Torso-
Feature: Combined-Part-Subteile sichtbar beim Scan inkl. Bild- und
Color-Konsistenz fuer Grund-Teile (B3 + B3.5 nachgezogen in selber
Iteration). Vier Skalierungs-Wurzeln an einem Tag entdeckt + behoben,
Engineering-Lehre dauerhaft im BUILD-1-Eintrag dokumentiert.

**Naechster Schritt: v0.1.25-beta.2 taggen** — vier substanzielle Inhalte
(PERF-4 + PERF-5 + BUILD-1 + Torso) als "Performance + Bau-Verbesserungen
+ Komponenten-Anzeige"-Update. Velopack zieht das automatisch auf die
User-Installationen. v0.1.25-beta.1 (`0bb0a7bd`, 2026-06-02) bleibt
released.

**Offene v0.1.25-Kandidaten:**
- BUILD-3 (Konzept liegt vor in docs/, Modell b empfohlen, wartet auf
  User-Freigabe)
- UI-1 (Beschreibung-Spalte mehrzeilig, 3 USER-ENTSCHEIDUNGEN offen)
- UI-2 (komisches Spalten-Layout, beobachten ob reproduzierbar —
  koennte sich mit UI-1 miterledigen)
- B2 (Footer-Layout in ManageIgnored/MassUpdate-Dialog, ~30min)
- PERF-7 (BuildSuggestionsScalingTests Wall-Clock → strukturell, ~30min)
- B-Re-Evaluierung (subjektiv beim Sortieren ueber mehrere Tage, kein
  eigener Diagnose-Lauf noetig)
- OPEN-18 Single-Mode-Cleanup, BUILD-2, Bauteile-Bin-Konzept,
  UPSERT-Sync-Optimierung, vollstaendiges Undo-System (alle eigene
  Iterationen mit unterschiedlichem Aufwand)*
