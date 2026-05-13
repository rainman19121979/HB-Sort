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

---

## Externe Abhängigkeiten

### ✅ Brickognize-ToS (geklärt)
- ✅ **Brickognize-Lizenz/ToS-Klärung** - Mail an Piotr verschickt 2026-05-07
- ✅ **Antwort von Piotr** - 2026-05-09: Nutzung erlaubt, 5 RPS Limit, Attribution optional

---

## Erledigt ✅

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
| **v0.1.23** | 📋 geplant | Bin-Typ-Spalte (StorageBin.Kind als persistierte Enum), Strict-Mode, Migration. ~5h, eine Beta. |
| **v0.1.24** | 📋 geplant | Anlegen-Dialog-Redesign (systemweite Floating-Auswahl) + BL-Inventar-Integration (Mass-Update-Export). ~20h, drei Betas. |
| **v0.2.0** | 💭 Brainstorming | grosse Features aus Backlog (siehe oben) |

Konvention:
- Patch-Iteration (v0.x.Y) für Cleanup + kleine Features
- Minor-Iteration (v0.Y.0) für große Features oder Architektur-Änderungen

---

*Zuletzt aktualisiert: 2026-05-13 nach v0.1.22-Stable-Release.*
