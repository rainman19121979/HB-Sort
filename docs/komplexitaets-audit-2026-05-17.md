# Komplexitäts-Audit HBSort v0.1.24-beta.1

**Datum:** 2026-05-17  
**Tag:** `1dc7ae8b` (v0.1.24-beta.1 Phase 3)  
**Methode:** Sub-Agent-Choreographie — `hbsort-auditor` (primär, code-basiert) + `hbsort-challenger` (kritische Hinterfragung mit Stichproben-Verifikation)  
**Aufwand:** ~2.5h (Auditor ~2h Phase A/B/C + Challenger ~30min Verifikation/Verdikt)  
**Anlass:** User-Anliegen 2026-05-17 *"wir verstricken uns in Komplexitaet"* nach 8-Commit-Iteration in 2-3 Tagen.

---

## 1. Executive Summary

Die Befürchtung *"wir verstricken uns"* ist **teilweise berechtigt**, aber die Komplexitäts-Hotspots verteilen sich klarer als das Gefühl suggeriert:

- **3 echte strukturelle Schmerz-Stellen** (verifiziert mit Code-Belegen und User-/Maintenance-Impact): `DataChanged`-Bus über Service-Layer (H9), `DismantleWizardViewModel` mit 2 Lade- und 3 Confirm-Pfaden seit v0.1.20 (H10), UX-Inkonsistenz BuildSuggestion vs. übrige Modal-Pfade (H11).
- **7 Buchhaltungs-Hotspots** (Komplexität existiert, aber funktional sauber dokumentiert/getestet): ScanViewModel-Größe, Bin-Lifecycle-Wahrheiten, drei Reverse-Match-Pfade, parallele DTOs, 3-Modi-Overlay, BinDisplayItem-Wrapper, Suggest-Regelschichten.
- **2 verworfene Befunde** (kein realer Schmerz): "6 Status-Enums sind Wachstumstrend" (in Wahrheit Layer-Separation), "MinifigSummary Legacy-Fork" (bewusster Test-Fallback).

**Wichtiger Methoden-Anker:** Ein vorausgehender Chat-Audit hatte StorageBinKind-Werte aus dem Gedächtnis falsch rekonstruiert (vermutet Floating=3, real Floating=1). Dieser Audit wurde deshalb systematisch aus dem Code gelesen — jede Aussage hat Datei:Zeile-Beleg. Der Challenger hat stichprobenartig nachverifiziert: alle Behauptungen (LOC, Enum-Werte, Aufrufstellen, Regelschichten) hielten der Code-Realität stand. Eine Mikro-Abweichung: Auditor zählte "29 Services", Challenger fand 27 echte Interfaces + 1 Exception-Klasse = 28 Files.

**Konsolidierte Empfehlung** (in Prioritäts-Reihenfolge):

| ID | Aktion | Aufwand | Wann |
|---|---|---|---|
| V9 | Enum-Doku in CLAUDE.md (4 Klassifikations-Achsen + Mapping-Tabelle) | 1h | jetzt / v0.1.24-beta.2 |
| V5 | SortInstruction-Builder-Helper (Take-Section-GroupBy-Loop konsolidieren) | 1-2h | v0.1.24-beta.2 |
| V10 | BuildSuggestion-Migration auf SortInstruction (REVISED: 2-3h statt 30min, MinifigDetailView mitziehen) | 2-3h | v0.1.24-beta.3 |
| V12 | DismantleWizard Load-Helper extrahieren (Backlog seit v0.1.20) | 1.5h | v0.1.25 |
| V7 | `DataChanged` auf VM-Bus (strukturell, Konzept-Doku Pflicht vorher) | 4-5h + Konzept | v0.1.25 |
| V6 | `RecalculateKindAsync` via SaveChanges-Interceptor (Konzept-Doku Pflicht vorher) | 3-4h + Konzept | v0.1.25 nach V7 |
| V2 | BinInstructionViewModel Single+Group abräumen | 1-2h | v0.1.25 NACH V10 |

**Nicht jetzt** (verworfen oder DEFER): V1 ScanViewModel-Split, V3 Reverse-Match-Vereinheitlichen, V4 DTO-Vereinen, V8 Enum-Harmonisierung, V11 BinDisplayItem-Extension-Method.

**Streitpunkte zwischen Auditor und Challenger**: V3 (Auditor: 4-6h-Refactor / Challenger: STOP wegen Vertragsbruch), H3 (Auditor: Wachstumstrend-Pathologie / Challenger: korrekte Layer-Separation), V1 (Auditor: später / Challenger: DEFER ohne Datum).

---

## 2. Bestands-Inventur (Phase A)

### 2.1 Models (`HBSort.Core/Models/`)

| Datei | Persistiert? | Bemerkung |
|---|---|---|
| `AppSettings.cs` (303 LOC) | settings.json | 40+ Properties; Bricklink, Prices, AutoBackup, AutoBlImport, CategoryToBinMapping, SeenBrickognizeCategories. Sub-Class WindowState mit 7 Splitter-Properties. |
| `BricklinkTokens.cs` | DPAPI-Blob in AppSettings | 4 Felder |
| `BrickognizeModels.cs` | transient (API-DTO) | Prediction-Records |
| `CacheStats.cs` | transient | Diagnose-Record |
| `DailyStats.cs` | EF/userdata.db | Datum + 3 Counter |
| `FloatingPart.cs` (59 LOC) | EF/userdata.db | Inkl. `BrickognizeCategory` (nullable), `OriginMinifigId` (nullable+SetNull) |
| `PriceSettings.cs` | settings.json (in AppSettings) | Inkl. 3 Sub-Enums |
| `ScanEvent.cs` (90 LOC) | EF/userdata.db | Inkl. `UndoData`-JSON-Blob, `UndoneAt`, `WasUndone` |
| `StorageBin.cs` (39 LOC) | EF/userdata.db | Inkl. **persistierter `Kind`-Spalte** + `FreedAt` + `Notes` |
| `StorageBinKind.cs` (25 LOC) | n/a (Enum) | **Empty=0, Floating=1, Waiting=2, Complete=3** (explizit numeriert) |
| `TrackedMinifig.cs` (61 LOC) | EF/userdata.db | Inkl. `TrackedMinifigStatus`-Enum: Waiting / Complete / Dismantled |
| `TrackedMinifigPart.cs` (34 LOC) | EF/userdata.db | Inkl. `QuantityNeeded` + `QuantityCollected`-Track |
| `Bricklink/` Subordner | BL-Cache (raw SQLite) | BlItem, BlColor, BlSubset, RateLimitStatus etc. |
| `Pricing/` Subordner | settings.json + bl_prices | SalesRecommendation, PriceLookupOutcome |
| `Exceptions/` Subordner | n/a | Custom Exceptions |

Persistente Achsen: **drei** (userdata.db EF + bl_cache.db raw + settings.json JSON), kein DTO-Layer dazwischen.

### 2.2 Services (`HBSort.Core/Services/I*.cs`)

**27 echte Service-Interfaces** (28 Files inkl. `InvalidBinKindException`). Production-LOC gesamt: **12 851** in `HBSort.Core/Services/`.

Top-5 nach Implementierungs-LOC:

| Service | Impl. LOC | Verantwortung |
|---|---|---|
| `BlCacheRepository` | 1355 | raw SQLite gegen `bl_cache.db` |
| `MinifigPersistenceService` | 1035 | Persist + Dismantle + Move + BulkOps + Reverse-Match |
| `StorageBinService` | 1012 | CRUD + Suggest + EligibleBins + RecalcKind + Empty + Mix-Scan |
| `PartLookupService` | 819 | LookupPart + CollectMinifigFromSuperset + AddPartToFloating |
| `BlCatalogService` | 609 | Cache-First-Catalog |

Single-Method-Interfaces (Kandidaten für Re-Composition, aber heute legitim für Test-Mockability): `IFirstRunService`, `IExternalIdResolver`, `IDataHealService`, `ICameraService`.

Bricklink-Domäne: 7 BL-spezifische Service-Interfaces + 2 PriceProvider-Implementierungen.

### 2.3 ViewModels (`HBSort/ViewModels/`)

| VM | LOC | Bemerkung |
|---|---|---|
| `ScanViewModel.cs` | **2147** | God-Class — siehe H1 |
| `SettingsViewModel.cs` | 1631 | 16+ DI-Parameter |
| `DismantleWizardViewModel.cs` | 1116 | Inkl. `DismantlePartItemViewModel` (Z.924-1116) und 2 Modi (PutInBin/AssignToWaiting) + Pending-Mode |
| `MainViewModel.cs` | 694 | Tab-Orchestration + Settings-Routing |
| `MinifigPriceViewModel.cs` | 688 | Phase-8 BL-Preise |
| `InventoryListViewModel.cs` | 622 | Bulk-Move/Delete + StatusKind-Enum |
| `CollectMinifigWizardViewModel.cs` | 487 | Neu in v0.1.24-Phase 2a |
| `MinifigSummaryViewModel.cs` | 359 | Move-Pfad mit Legacy+Service-Fork |
| `PendingMinifigViewModel.cs` | 356 | Inkl. `ConsumedFromBins`-Tracking |
| `BuildSuggestionDetailViewModel.cs` | 298 | Nutzt Service-Auto-Konsum-Pfad |
| `BinInstructionViewModel.cs` | 246 | Drei-Modi-Overlay |
| Weitere VMs | 35-275 | normal-skaliert |

Summe: **11 050 LOC** in 26 ViewModel-Dateien. ScanViewModel ist 18.5% davon.

### 2.4 Enums (vollständig, mit REALEN Werten aus dem Code)

| Enum | Datei:Zeile | Werte (real) |
|---|---|---|
| `StorageBinKind` | `HBSort.Core/Models/StorageBinKind.cs:18-24` | **Empty=0, Floating=1, Waiting=2, Complete=3** (explizit numeriert) |
| `TrackedMinifigStatus` | `HBSort.Core/Models/TrackedMinifig.cs:56` | Waiting, Complete, Dismantled (implizit 0/1/2) |
| `ScanType` | `HBSort.Core/Models/ScanEvent.cs:77` | MinifigScan, PartScan, FloatingPartTransfer, FloatingPartExported, Delete, Move, Complete, BinFreed, BinCreated, Import, UndoApplied (11 Werte, TEXT-konvertiert via EF) |
| `BinTargetKind` | `HBSort.Core/Services/IStorageBinService.cs:260` | FloatingTarget, WaitingMinifigTarget, CompleteMinifigTarget |
| `BinInstructionMode` | `HBSort/ViewModels/BinInstructionViewModel.cs:211-217` | Single, Group, SortInstruction |
| `WizardPartStatus` | `HBSort/ViewModels/CollectMinifigWizardViewModel.cs:428` | Trigger, InStorage, Missing |
| `DismantlePartMode` | `HBSort/ViewModels/DismantleWizardViewModel.cs:915` | PutInBin, AssignToWaiting |
| `StatusKind` | `HBSort/ViewModels/InventoryListViewModel.cs:622` | Complete, Waiting, Floating (UI-Filter — KEIN Empty, weil Item-Liste) |
| `InventoryItemType` | `HBSort/ViewModels/InventoryListViewModel.cs:620` | Minifig, FloatingPart |
| `ScanMode` | `HBSort/ViewModels/ScanViewModel.cs:2139` | Auto, Minifig, Part |
| `BinFilterMode`, `BinSortMode`, `BinSequenceType`, `BrickognizeItemType`, `BrickognizeStatus`, `VatMode`, `PriceFilterMode`, `PriceLoadMode`, `SalesAdvice`, `RateLimitState`, `PriceLookupSource`, `PriceLookupNotice`, `DataCompleteness`, `FirstRunStatus`, `SettingsTab`, `ToastKind` | siehe Grep | Domänen-Enums, keine Konkurrenten zur Bin-/Status-Achse |

**Bin-/Status-relevante Enums = 6**: `StorageBinKind` (DB-Persistenz), `BinTargetKind` (Service-Filter-Vertrag), `WizardPartStatus` (Wizard-UI), `StatusKind` (Inventory-Filter-UI), `BinInstructionMode` (Overlay-Modus), `TrackedMinifigStatus` (Figur-Lifecycle). Über 4 Layer verteilt (DB / Service-DTO / VM-UI).

### 2.5 Konzepte für User

1. **Bin-Typ** mit 4 Werten (Empty/Floating/Wartend/Komplett) — sichtbar als Spalte im Lagerfächer-Tab.
2. **Reifungspfad** Wartend → Wartend+Komplett (Mix-Variante erlaubt, bleibt Waiting-Kind).
3. **Strict-Mode**: bestimmte Item-Bin-Kombinationen werfen → `InvalidBinKindException`.
4. **Reverse-Match-Bypass**: FloatingPart darf in Wartend-Bin wenn passendes Required-Part fehlt.
5. **Stapel-Match-Vorrang** (Schritt 1) und **User-Mapping-Vorrang** (Schritt 2) bei FloatingPart-Suggest.
6. **Bauteile-Bin** (existiert implizit, ist kein Enum-Wert — Backlog v0.1.25).
7. **Limits** `MaxWaitingFiguresPerBin` + `MaxCompleteFiguresPerBin` (wirken seit v0.1.24 als Combobox-Filter).
8. **Wizard 2-stufig** mit 3 Status-Gruppen (Trigger/InStorage/Missing) und Manuell-Markiert-Option.
9. **Post-Save-Modal** mit Take/Put/Plus-Sektionen, KEIN Auto-Dismiss (für 6 Trigger).
10. **Brickognize-Kategorie-Mapping** (User-Setting → Bin) + Pseudo-Kategorie "Unbekannt".

### 2.6 Konzepte intern (Entwickler-Mental-Model)

1. 3 Persistenz-Achsen (EF / raw SQLite / JSON-Settings), kein gemeinsamer DTO-Layer.
2. `StorageBin.Kind`-Spalte muss durch jeden Schreib-Pfad gepflegt werden — **7 manuelle Aufrufstellen** von `RecalculateKindAsync` (`DataHealService.cs:102`, `FloatingPartTransferService.cs:165`, `MinifigPersistenceService.cs:65`, `UndoService.cs:120`, `PartLookupService.cs:48,578,800`).
3. `IMinifigPersistenceService.DataChanged`-Event als globales Refresh-Signal — gefeuert in 6+ Service-Stellen, abonniert von 5 Live-VMs.
4. `BinKindGuard`-Statics (`EnsureBinAcceptsFloatingPart`, `EnsureBinAcceptsMinifig`) als Strict-Mode-Entry-Points (A1-A12 laut v0.1.23-Notiz).
5. `DispatcherPriority.Render`-Pattern für Modal-Open in `BinInstructionViewModel.ShowSortInstruction` (Z.171-185).
6. Reverse-Match-Konsum lebt an drei Aufrufstellen mit drei Verträgen.
7. `BinDisplayItem`-Wrapper als Adapter zwischen `StorageBin` (Service) und Combobox (View).
8. `ConsumedFromBins`-Tracking in `PendingMinifigViewModel` (Z.181) vs `ConsumedFloatingParts` in `PersistMinifigResult` (Z.287 in `IMinifigPersistenceService.cs`) — parallele Strukturen je nach Pfad.
9. `BinInstructionMode` mit 3 Modi im selben VM plus 4 paarweise State-Container.
10. ScanViewModel als Singleton im DI (Lifetime-Management → manuelles Dispose in `App.OnExit`).

---

## 3. Komplexitäts-Hotspots (Phase B mit Challenger-Verdikten)

### H1 — ScanViewModel God-Class

**Beleg**: `HBSort/ViewModels/ScanViewModel.cs` ist **2147 LOC**, `IDisposable`, 9 RelayCommands (`Z.377, 575, 669, 680, 688, 700, 1021, 1614, 2022`), 25 async Methoden, 3 CancellationTokenSources (`_lookupCts`, `_preloadPartImagesCts`, `_blCatalogImagesCts`). Verantwortlichkeiten umfassen Kamera, Brickognize-Calls, Top-3-Auswertung, BL-Lookup (Minifig/Part), PendingMinifig/PendingPart-State, FloatingPart-Transfer, Persist-Pipeline, Color-Auswahl, Storage-Bin-Auswahl (zwei Pfade `Z.1376/1546`), Reset-After-Save (`Z.441`).

**Größenordnung**: 2147 LOC. Doppelt so groß wie SettingsViewModel (1631 LOC). 18.5% der gesamten 11 050 VM-LOC. Disposal-Logik selbst 38 Zeilen (Z.2097-2135).

**Auditor-Verdikt**: Hotspot identifiziert. Bei jeder Iteration berührt (Phase 1, 1.5, 2a-Polish, 2b alle haben Z.1370+/1540+/1615+ angefasst). Backlog-Eintrag "Test-Harness fuer ScanViewModel" steht offen.

**Challenger-Verdikt: ⚠️ Eingeschränkt**

Verifikation bestätigt LOC und 9 RelayCommands exakt. Aber: die App heißt HB-**Sort**. Scan-Workflow ist der Kern. Eine fette Scan-VM ist erwartbar — Vergleich mit SettingsViewModel ist Apfel/Birne (Settings ist TabHost mit vielen Sub-Sektionen, Scan ist End-to-End-Workflow mit Kamera+Brickognize+Persistenz+Modal). **Kein konkreter User-Schaden, kein Production-Bug**. Bei Single-Dev-Projekt sind Merge-Konflikte irrelevant. Gegenvorschlag: nicht "Split", sondern gezielte Helper-Extraktion für wiederkehrende Patterns, falls die 2147 LOC zu 2800+ wachsen.

---

### H2 — Drei Wahrheiten im Bin-Lifecycle

**Beleg**:
- `StorageBin.cs:21` `FreedAt` (Belegt/Frei-Marker, manuell durch User über "Fach leeren")
- `StorageBin.cs:32` `Kind` (persistierte Enum-Spalte seit v0.1.23)
- `RecalculateKindAsync` in `StorageBinService.cs:685-717` zählt zur Laufzeit `waitingCount / completeCount / floatingCount` und schreibt `Kind`

**Aufrufstellen von `RecalculateKindAsync`** (7 produktive, verifiziert per Grep): `DataHealService.cs:102`, `FloatingPartTransferService.cs:165`, `MinifigPersistenceService.cs:65`, `UndoService.cs:120`, `PartLookupService.cs:48,578,800`.

**Auditor-Nuance**: Korrigiert selbst — nur **zwei persistierte Wahrheiten** (FreedAt + Kind), die dritte ist abgeleitete Beziehungs-Realität. Problem ist die 7-fache Recalc-Pflicht.

**Challenger-Verdikt: ⚠️ Eingeschränkt**

Verifikation: 7 Stellen exakt. Aber: 2 Wahrheiten + abgeleitete Beziehungs-Realität = **normale relationale Datenmodellierung**. Die 7-fache Recalc-Pflicht ist das Trade-off gegen "berechnen bei jedem Lese-Pfad" (was v0.1.22 hatte und langsam war). Pflege-Aufwand bei jedem neuen Schreib-Pfad ist real (vergessbar) — aber `DataHealService.cs:102` räumt Bestand-Drift beim App-Start auf. **Wartungs-Komplexität, kein akuter Daten-Bug**. V6 (SaveChanges-Interceptor) ist die richtige Antwort, aber Befund H2 selbst ist Buchhaltung, nicht Schmerz.

---

### H3 — Sechs Status-/Klassifikations-Enums

**Beleg** (alle Werte aus Code verifiziert, siehe 2.4):
- `StorageBinKind` (4 Werte, persistiert) — Bin-Inhalt
- `BinTargetKind` (3 Werte, transient) — Service-Filter-Vertrag
- `WizardPartStatus` (3 Werte, transient) — Wizard-Stufe-1-Klassifikation
- `StatusKind` (3 Werte, transient) — InventoryList-Spalte
- `BinInstructionMode` (3 Werte, transient) — Overlay-Modus
- `TrackedMinifigStatus` (3 Werte, persistiert) — Figur-Lifecycle

**Auditor-Befund**: `StatusKind` und `StorageBinKind` haben *fast* dieselben Werte. `WizardPartStatus.InStorage` ist semantisch nah an `StorageBinKind.Floating`. `BinTargetKind` mappt auf `StorageBinKind`-Filter-Mengen (`StorageBinService.cs:814-823`). Wachstumstrend: jede Iteration legt neue Klassifikations-Achse an.

**Challenger-Verdikt: ❌ Verworfen**

Verifikation: `StatusKind {Complete, Waiting, Floating}` vs `StorageBinKind {Empty, Floating, Waiting, Complete}` sind **NICHT identisch** (Empty fehlt in StatusKind, weil StatusKind ein Item-Filter ist, kein Bin-Status). Die 6 Enums leben in **5 unterschiedlichen Layern/Domänen** (DB-Persistence, Service-DTO, VM/UI). Zusammenführen wäre Layer-Bruch. Enums sind das saubere Mittel für distinkte Domänen — genau das fordert ENGINEERING_PRINCIPLES.md (explizit > implizit). "Wachstumstrend" ist keine Pathologie, sondern Verfeinerung. **Konkrete Schäden bei Nicht-Anfassen: null**. V9 (Doku) ist OK als Schmerzlinderung, aber V8 (Harmonisierung) wäre Layer-Bruch.

---

### H4 — Drei Pfade für Reverse-Match-Konsum mit drei Verträgen

**Beleg** (3 produktive Aufrufer):
1. **ScanViewModel.PersistPendingAsync** (`ScanViewModel.cs:1666`): MinifigDetailView-Pfad. `ConsumeFloatingParts=false` (Z.1651), Quelle = `PendingMinifig.ConsumedFromBins`-Tracking-Liste (befüllt durch explizite "Aus Fach"-Klicks via `TransferFloatingPartToPendingAsync` Z.1882). Modal: neuer SortInstruction-Pfad.
2. **BuildSuggestionDetailDialog.Create_Click** (`BuildSuggestionDetailDialog.xaml.cs:67`): "Was kann ich bauen?"-Pfad. Kein Flag (Default `true`), Service-Auto-Konsum (`MinifigPersistenceService.cs:663-728`), Quelle = `result.ConsumedFloatingParts`. Modal: **alter** Group-Mode via `ShowBinInstructionGroup` (Z.117-119).
3. **CollectMinifigWizardDialog.Save_Click** (`CollectMinifigWizardDialog.xaml.cs:111`): 2-stufiger Wizard. Ruft `IPartLookupService.CollectMinifigFromSupersetWithSelectionAsync` mit explizit ausgewählten Parts + optionalen `manuellClaimedParts`. Modal: neuer SortInstruction-Pfad.

Die `ConsumeFloatingParts`-Flag-Bedeutung ist in `IMinifigPersistenceService.cs:220-246` exhaustiv dokumentiert (XML-Doc 26 Zeilen, Engineering-Prinzip 1.4 explizit zitiert in Z.242).

**Challenger-Verdikt: ⚠️ Eingeschränkt**

Verifikation: Vertrag ist exhaustiv dokumentiert, beide Bedeutungen klar adressiert. **Das ist kein Bug, sondern ein bewusstes Design** das in v0.1.24-beta.1 Phase 2a-Polish kürzlich geschaffen wurde (vorher gab es implizite Doppel-Logik, jetzt explizites Flag). "Drei Pfade" sind eher "ein Service-API mit 3 Aufrufern, davon 2 mit gleichem Flag und 1 mit anderem". Risiko bei neuem Aufrufer: XML-Doc fängt das ab. Gegenvorschlag: statt Vereinheitlichen lieber kontrastiv testen — ein Test pro Aufrufer-Modus, der den Vertrag pinnt.

---

### H5 — Parallele Tracking-Strukturen

**Beleg**:
- `ConsumedFloatingPartInfo` in `IMinifigPersistenceService.cs:294-312` — vom Service produziert
- `PendingMinifigViewModel.ConsumedFromBin` in `PendingMinifigViewModel.cs:189-198` — vom UI produziert

Beide DTOs sind Inputs für dasselbe `SortInstruction`-Modal (Take-Sektionen). `ScanViewModel.PersistPendingAsync:1730-1745` baut Take aus VM-Liste, `CollectMinifigWizardViewModel.BuildSortInstructionForSave:378-392` baut sie aus Service-DTO. Zwei DTOs, zwei Stellen-spezifische Builder, gleicher Output-Code.

**Challenger-Verdikt: ✅ Bestätigt (aber niedrig-Schwere)**

5 von 6 Feldern überlappen exakt. Mapping-Code zwischen beiden DTOs ist Boilerplate-Overhead. Bei Schema-Änderung beides zu pflegen ist vergessbar. **Aber**: Layer-Trennung ist gewollt — Service-DTO darf keine VM-Annotations haben, VM-DTO darf ObservableObject-Properties haben. Ein gemeinsames DTO würde Layer-Bruch verursachen. V4 ist OK aber niedrig-Prio. Kein konkreter User-Schmerz.

---

### H6 — BinInstructionViewModel als Drei-Modi-Klasse

**Beleg**: `BinInstructionViewModel.cs:211-217` `BinInstructionMode { Single, Group, SortInstruction }`. Parallel:
- Single-Mode: `Label` (Z.43), `ImageUrl` (Z.47) — verwendet von `ShowSingle` (Z.97-105)
- Group-Mode: `Items` ObservableCollection (Z.61), `HeaderText` (Z.58) — verwendet von `ShowGroup` (Z.111-123)
- SortInstruction-Mode: `TakeSections` + `PutSections` ObservableCollections (Z.72-75), `PlusHint` (Z.79), `HasPlusHint`/`HasTakeSections`/`HasPutSections` Computed-Bools — verwendet von `ShowSortInstruction` (Z.159-185)

`Dismiss()` (Z.126-142) räumt alle drei Mode-States parallel.

**Challenger-Verdikt: ✅ Bestätigt**

3 Modi sind real, ABER: `Z.66-69` erklärt explizit als **bewusster Übergangs-Zustand** in v0.1.24-beta.1. Single-Mode-Cleanup ist als OPEN-18 für v0.1.25 vorgesehen. Aktuell: keiner. Dismiss() räumt korrekt. Nach V2 (v0.1.25): Code wird sauberer. Auditor's "4 paarweise State-Container" leicht überzogen — eher 3 logische Gruppen mit gemeinsamem HeaderText.

---

### H7 — BinDisplayItem-Wrapper als Adapter

**Beleg**: `BinDisplayItem` ist 1-Datei-Wrapper (`HBSort/Helpers/BinDisplayItem.cs`, 25 LOC, Record mit `StorageBin Bin` + `string DisplayText`). **6 ViewModels** halten `ObservableCollection<BinDisplayItem> AvailableBins`:
- `PendingMinifigViewModel.cs:137`
- `PartLookupViewModel.cs:76`
- `MinifigSummaryViewModel.cs:64`
- `DismantleWizardViewModel.cs:58`
- `CollectMinifigWizardViewModel.cs:84`
- `BuildSuggestionDetailViewModel.cs:50`

Plus `DismantlePartItemViewModel.TargetBin` (Z.947) als per-Part-Eintrag. 8+ Stellen mit `.Bin.Id`/`.Bin.Label`-Zugriff.

"Fremder BinDisplayItem"-Edge-Case ist in `DismantleWizardViewModel.cs:664-668` explizit dokumentiert: wenn Suggest-Service ein Bin liefert das nicht in AvailableBins ist, wird null returned statt eines "fremden Wrappers".

**Challenger-Verdikt: ⚠️ Eingeschränkt**

Auditor stellt es als Problem dar, Code zeigt: das ist **gelöst** — bewusste Konvention "wenn Suggest fremdes Bin liefert → null". Pre-Tag-Fix (v0.1.24-beta.1 Phase 2b). 6 VMs mit gleicher Wrapper-Liste = Konvention, kein Bug. V11 (Extension-Method) wäre mikroskopische Politur — Risiko Refactor-Bug höher als Nutzen. **H7 als Befund lassen, V11 ablehnen oder bewusst auf "irgendwann" verschieben**.

---

### H8 — SuggestBinForFloatingPartAsync mit 5 Regelschichten

**Beleg**: `StorageBinService.cs:143-267` (124 LOC für eine Suggest-Methode). 5 Regel-Schichten:
1. Pseudo-Kategorie-Mapping bei leerer Kategorie (Z.159-162)
2. Stapel-Match auf gleicher PartNo+ColorId (Z.170-181)
3. User-Mapping aus AppSettings (Z.189-198)
4. Pre-Filter über `StorageBinKind` mit excludeMinifigId-Switch (Z.222-225)
5. Default-Regel mit per-Bin Kollisions-Check (Z.236-260)

(Challenger zählt 4 explizite Schichten + Pre-Filter. Beide Zählungen verteidigbar.)

**Auditor-Nuance**: Trotz Komplexität gut getestet (13 Test-Aufrufer in `StorageBinServiceTests.cs:404-624`). Schichtfolge ist Konvention die User+Code teilen.

**Challenger-Verdikt: ❌ Verworfen**

Auditor selbst sagt "Gut getestet → Komplexität ist dokumentiert+getestet". Das ist die Definition von **akzeptable Komplexität**. 124 LOC mit numbered comments und exhaustive Tests ist nicht Hotspot, sondern Vorbild. **Konkrete Schäden bei Nicht-Anfassen: null**. Hotspot zurückziehen.

---

### H9 — RaiseDataChanged als globaler Refresh-Bus

**Beleg**: `IMinifigPersistenceService.cs:25` definiert `event EventHandler? DataChanged`. Gefeuert in 6+ Service-Stellen (Grep): `MinifigPersistenceService.cs:70` (RaiseDataChanged-Methode), `FloatingPartTransferService.cs:175`, `PartLookupService.cs:155,225,307,429,583,805`, `UndoService.cs:127`. 5 abonnierende Live-VMs (LiveStats, RecentScans, WaitingDetail, BuildSuggestions, InventoryList).

v0.1.23-beta.2 Hotfix musste `Dispatcher.Invoke → InvokeAsync + CancellationToken` einbauen (BACKLOG.md Z.380-396 "DataChanged-Storm"), weil parallel-feuernde DataChanged-Aufrufe den UI-Thread blockierten. Wurzelfix B+E steht in v0.1.25-Backlog.

**Challenger-Verdikt: ✅ Bestätigt**

Schichten-Bruch ist real (Service kennt UI-Refresh-Semantik). v0.1.23-beta.2 Hotfix ist Beweis: DataChanged-Storm hat tatsächlich Performance-Probleme verursacht. v0.1.25 plant explizit strukturellen Fix (V7). **Befund ist im richtigen Track** — gehört nach v0.1.25, nicht in v0.1.24-beta.2.

---

### H10 — DismantleWizardViewModel mit zwei Modi (1116 LOC)

**Beleg**: `DismantleWizardViewModel.cs` (1116 LOC) enthält 3 Klassen: `DismantleWizardViewModel` (Z.22-922), `DismantlePartItemViewModel` (Z.924-1116), `DismantlePartMode`-Enum (Z.915).

- 2 Lade-Pfade: `LoadAsync` (Z.103), `LoadFromPendingAsync` (Pending-Mode)
- 3 Confirm-Pfade verifiziert (Challenger): Standard (`Z.707`), PendingMode (`Z.753`), PendingModeLegacy (`Z.848`)
- Pro Part-Item zwei Modi (`PutInBin` vs `AssignToWaiting`)

Backlog-Eintrag (Z.66 in BACKLOG.md) "DismantleWizard.LoadAsync vs LoadFromPendingAsync Helper extrahieren (~50-60 dupl. Zeilen)" steht **seit v0.1.20** offen. v0.1.22-beta.1 Block E extrahierte zwei Helper (`ReloadAvailableBinsAsync` Z.318, `ApplySmartHintIfAny` Z.337) — Restdoppelung heute noch sichtbar.

**Challenger-Verdikt: ✅ Bestätigt**

**Echter Schmerz**. Helper-Refactor-Backlog seit v0.1.20. Direkt-Zerlegen (PendingMode) wurde nachträglich angeflanscht. Maintenance-Friction: Änderung am Confirm-Pfad muss in 2-3 Stellen synchron erfolgen (siehe Drift-Fix-Kommentar Z.484). Bug-Risiko: "Drift" wurde bereits einmal Bug gefixt (UX X.33). **Echter Refactor-Wert**. V12 verbleibt sinnvoll.

---

### H11 — BuildSuggestionDetail nutzt veraltetes Modal-Pattern

**Beleg**: `BuildSuggestionDetailDialog.xaml.cs:117-119` ruft `mainVm.ScanViewModel.ShowBinInstructionGroup(instructionItems, headerText: "...")` — nutzt **alten Group-Mode** des `BinInstructionViewModel`. Die anderen Trigger wurden in Phase 1 auf den neuen `SortInstruction`-Pfad migriert.

Auditor-Beobachtung: "Es gibt **keine** Doku-Notiz im Code dass dies Absicht ist." Im `BinInstructionViewModel.cs:67-69` steht: *"Single- und Group-Mode bleiben in v0.1.24 unveraendert erhalten ... Group-Mode wird von BuildSuggestionDetailDialog noch genutzt"* — Pattern-Inkonsistenz ist also bewusst, aber Begründung fehlt.

**Challenger-Verdikt: 🔄 Neu formuliert**

Verifikation: An der Aufrufstelle BuildSuggestionDetailDialog.xaml.cs:117 ist **kein Kommentar**, warum Group-Mode bewusst gewählt wurde. Die Begründung lebt im Konsumenten-File (BinInstructionViewModel.cs:67-69) als rückwärts gerichtete Verweis-Begründung.

**Wichtiger neuer Befund vom Challenger**: Es gibt **einen zweiten Aufrufer** des Group-Mode-Pfads: `MinifigDetailView.xaml.cs:161` (`scan.ShowBinInstructionGroup(wizardVm.LastBinInstructionItems)`). **V10 "BuildSuggestion auf SortInstruction migrieren" löst nur die Hälfte** — MinifigDetailView würde Group-Mode weiter brauchen.

**Reformulierung**: "BuildSuggestionDetail UND MinifigDetailView nutzen veralteten Group-Mode-Pfad. V10 unterträgt den Aufwand: nicht 30min-1h sondern eher 2-3h für beide Pfade." User sieht beim Bauen aus dem "Was kann ich bauen?"-Tab ein anders aussehendes Modal als bei den anderen 5 Triggers — UX-Inkonsistenz ist real.

---

### H12 — MinifigSummaryViewModel hat Service + Legacy-Inline-Fork

**Beleg**: `MinifigSummaryViewModel.cs:261-320` `MoveToAsync` hat zwei Branches:
- Wenn `_persistence != null` → `MoveSelectionAsync` via Service (Z.263-281)
- Sonst Legacy-Pfad mit direktem `ctx.SaveChangesAsync` und Inline-UndoSnapshot-Erstellung (Z.282-319)

Legacy-Pfad schreibt ScanEvents nicht durch Service-Layer und ruft kein `RecalculateKindAsync` auf (würde Strict-Mode + Kind-Drift verursachen). Pattern findet sich auch in `DismantleWizardViewModel.cs:765` (`ConfirmPendingModeLegacyAsync`).

**Challenger-Verdikt: 🔄 Neu formuliert**

Verifikation: Legacy-Pfad ist explizit als **Test-Pfad** dokumentiert (Z.283: "Legacy-Pfad fuer Tests ohne Persistence-Service"). Das ist **bewusstes Test-Fallback-Pattern** — wenn der Test den Persistence-Service nicht injiziert, fällt die VM auf direkten DB-Zugriff zurück. Hässlich, aber funktional korrekt.

**Reformulierung**: "Test-Fallback-Pattern in MoveToAsync sollte entweder (a) eliminiert werden durch Mock-Persistence in Tests, oder (b) klarer als Test-Only markiert werden mit Throw in Production wenn `_persistence==null`. Aktuelle Form 'silent fallback' ist Mittelweg ohne klare Linie." Tests könnten Bug verstecken (Legacy-Pfad hat keine Strict-Mode-Behandlung).

---

## 4. Vereinfachungs-Vorschläge (Phase C mit Challenger-Verdikten)

### V1 — ScanViewModel-Split

**Auditor-Vorschlag**: ScanViewModel in 3 Teile splitten (Kamera+Brickognize / MinifigPendingController / FloatingPartScanController) + gemeinsamer State über `IScanState`. Aufwand 8-12h, hoch-Risiko, **später**.

**Challenger-Verdikt: ❌ Verworfen (jetzt) / DEFER**

- 8-12h hoch-Risiko-Aufwand für "doppelt so groß wie nächstes VM"-Buchhaltung
- 3 neue VMs + IScanState-Interface = neue Schichten-Komplexität
- Mediator/Event-Bus zwischen Sub-VMs nötig
- View muss DataContext-Hierarchie umlernen
- Owner-Dialog-Zugriffe (z.B. `BuildSuggestionDetailDialog.xaml.cs:113`) müssen mitziehen
- Bestehende Tests gegen ScanViewModel brechen alle
- **Reifegrad**: niedrig. Keine konkrete Split-Strategie genannt.

**Empfehlung**: **DEFER bis Wachstum > 2800 LOC oder konkreter User-Schmerz**. Eine 2147-LOC-VM ist groß aber nicht pathologisch für den Kern-Workflow einer Sortier-App.

---

### V2 — BinInstructionViewModel Single+Group abräumen

**Auditor-Vorschlag**: Nach V10-Migration: Single+Group-Mode entfernen, ShowSingle/ShowGroup raus. 1-2h, v0.1.25 OPEN-18.

**Challenger-Verdikt: ⚠️ Eingeschränkt**

Pre-Condition: V10 (BuildSuggestion-Migration) **alleine reicht nicht** — `MinifigDetailView.xaml.cs:161` ist zweiter Group-Mode-Konsument. **REVISE** — Pre-Condition ergänzen: "alle Group-Mode-Konsumenten migriert" (mind. 2 Aufrufer prüfen). Dann v0.1.25 OPEN-18 OK.

---

### V3 — Reverse-Match-Konsum auf einen Vertrag

**Auditor-Vorschlag**: Einheitliches DTO `ReverseMatchInput { AutoConsume, ManuallyClaimed, AlreadyConsumed }`. Service entscheidet einheitlich. 4-6h, mittel-Risiko, v0.1.25.

**Challenger-Verdikt: ❌ Verworfen (STOP)**

**Was geht durch BuildSuggestion-Vertragsbruch kaputt** (User-Frage):
- BuildSuggestion-Flow nutzt `ConsumeFloatingParts=true` (Default) — Aufrufer gibt `QuantityCollected=0` und verlässt sich darauf dass Service alle passenden FloatingParts selbst sucht und konsumiert. Das ist **Pflicht des Service** (Reverse-Match nach Bin-Suche im FloatingPool).
- PersistPending-Flow nutzt `ConsumeFloatingParts=false` — Aufrufer hat schon explizit per "Aus Fach"-Button die FloatingParts via `TransferFloatingPartToPendingAsync` verschoben, `QuantityCollected` ist im Input vorbefüllt. Service darf NICHT nochmal suchen.

Wenn man "auf einen Vertrag" vereinheitlicht, geht **eines der beiden Semantiken verloren**:
- Variante A (immer Service-Konsum): PersistPending würde **doppelt konsumieren** (User-Klicks + Service findet dieselben FloatingParts)
- Variante B (nie Service-Konsum): BuildSuggestion würde **nichts konsumieren** weil Aufrufer kein Reverse-Match macht. Figur wäre ohne Teile angelegt.

Die einzige saubere Vereinheitlichung wäre: **BuildSuggestion macht selbst Reverse-Match vor Persist-Call** und übergibt vorbefüllte `QuantityCollected`. Aber dann muss die Match-Logik in den Aufrufer wandern → **Code-Duplikat statt Reduktion**.

**Empfehlung**: **STOP**. Der vermeintliche Vertragsbruch ist eine bewusste Asymmetrie zwischen zwei Use-Cases mit verschiedenen Vor-Bedingungen.

---

### V4 — Konsum-Tracking-DTOs vereinen

**Auditor-Vorschlag**: `ConsumedFloatingPartInfo` und `PendingMinifigViewModel.ConsumedFromBin` zu gemeinsamem Type. 2-3h, niedrig-Risiko.

**Challenger-Verdikt: ⚠️ Eingeschränkt**

Layer-Bruch wenn gemeinsames DTO im Core liegt aber von VM-Properties (ObservableObject) benutzt wird. Wenn es im UI-Layer bleibt, muss Service-Output gemappt werden — dann ist es kein Vereinen. ImageUrl-Refresh-Mechanismus (PropertyChanged-Trigger im Overlay) wäre im Service-DTO unsauber. **DEFER** bis V7 (DataChanged auf VM-Bus) Refactoring läuft.

---

### V5 — SortInstruction-Builder-Helper

**Auditor-Vorschlag**: `SortInstructionBuilder.BuildTakeFromConsumed(IEnumerable<ConsumedRecord>)` + `BuildPutForMinifig(...)`. 1-2h, sehr niedrig-Risiko.

**Challenger-Verdikt: ✅ Bestätigt — PROCEED**

Reiner Helper. Pflege-Risiko minimal. **Reifegrad: hoch.** Kann mit V4 oder allein in v0.1.24-beta.2 laufen.

---

### V6 — RecalcBinKindAsync via SaveChanges-Interceptor

**Auditor-Vorschlag**: EF-Core SaveChanges-Interceptor pflegt `Kind` zentral. 3-4h, mittel-Risiko, v0.1.25 Performance-Track.

**Challenger-Verdikt: ⚠️ Eingeschränkt — REVISE**

Interceptor sieht alle SaveChanges, muss FloatingPart/Minifig-Mutations erkennen und betroffene Bin-IDs sammeln. Mehrfach-Save in einem Context würde mehrfach interceptieren — Race oder Doppel-Recalc. **Strict-Mode (`InvalidBinKindException`) wird heute vor SaveChanges geprüft. Interceptor läuft danach** — Strict-Mode-Pfad müsste komplett umstrukturiert werden. EF Core Interceptor + transaction-scope debug ist nicht-trivial. **3-4h ist optimistisch**. Konzept-Doku Pflicht bevor implementiert.

---

### V7 — DataChanged auf VM-Bus (CommunityToolkit IMessenger)

**Auditor-Vorschlag**: `IMinifigPersistenceService.DataChanged` raus, stattdessen UI-Layer-EventAggregator. 4-5h, mittel-Risiko, v0.1.25.

**Challenger-Verdikt: ✅ Bestätigt — PROCEED als v0.1.25-Eintrag**

Migration von 14 Feuer-Stellen + 5 Subscribern auf neuen Bus muss in einer Iteration laufen, sonst Live-Refresh-Bug. WeakReference vs Strong-Subscription muss äquivalent funktionieren. Tests müssen neuen Bus mocken — alle bestehenden DataChanged-Mocks (~6 Test-Files) anpassen. Schichten-Bruch ist real, v0.1.25-Track sauber. **Konzept-Doku Pflicht bevor implementiert.**

---

### V8 — StatusKind ↔ StorageBinKind harmonisieren

**Auditor-Vorschlag**: `InventoryListViewModel.StatusKind` durch Funktion `(StorageBinKind, TrackedMinifigStatus?) → DisplayLabel` ersetzen. 1-2h, später.

**Challenger-Verdikt: ❌ Verworfen (STOP)**

`StatusKind` ist Inventory-**Item-Filter** (ist die Zeile Komplett/Wartend/Floating), `StorageBinKind` ist Bin-Persistenz-Typ. Unterschiedliche Werte (StatusKind hat KEIN Empty). Harmonisieren würde Inventory-Filter-Code zerschießen. Viel UI-Code mit StatusKind-Switch würde umgebaut werden müssen. **Auditor selbst sagt "kosmetisch"** — heißt: stop und nie wieder anfassen.

---

### V9 — Vier Klassifikations-Enums dokumentieren

**Auditor-Vorschlag**: Doku in CLAUDE.md die die 4-6 Enums als bewusste Achsen erklärt, mit Mapping-Tabelle. 1h, jetzt.

**Challenger-Verdikt: ✅ Bestätigt — PROCEED**

Kostenlose Schmerzlinderung. Wenn der Auditor die Enums verwechseln kann (StatusKind vs StorageBinKind), dann auch der nächste Reviewer.

---

### V10 — BuildSuggestionDetail auf SortInstruction-Modal migrieren

**Auditor-Vorschlag**: `BuildSuggestionDetailDialog.xaml.cs:117` von `ShowBinInstructionGroup` auf `presenter.Show(instruction)`. 30min-1h, niedrig-Risiko, v0.1.24-beta.2.

**Challenger-Verdikt: ⚠️ Eingeschränkt — REVISE**

Auditor unterschlägt: `MinifigDetailView.xaml.cs:161` ist zweiter Group-Mode-Konsument. **V2 (Single+Group abräumen) kann erst NACH V10 + MinifigDetailView-Migration laufen.** SortInstruction-Aufbau für "Nimm aus Fächern X+Y, lege Figur in Z" braucht TakeSections+PutSections-Logik die heute in BinInstructionItem-Liste flacher ist. **2-3h realistischer**. MinifigDetailView mitscope-en oder explizit ausschließen.

---

### V11 — BinDisplayItem-Extension-Method

**Auditor-Vorschlag**: Extension `IEnumerable<StorageBinWithCounts>.ToBinDisplayItems()`. 30min, jederzeit.

**Challenger-Verdikt: ❌ Verworfen (STOP)**

30min Refactor mit Risiko: wenn Extension bestehende AvailableBins-Initialisierung leicht anders macht, brechen Combobox-Bindings reference-equality-basiert. 6 VMs umstellen + 6 Test-Suites verifizieren. 30min ist illusorisch. **Risk-Reward stimmt nicht. 6× 6 Zeilen Boilerplate ist akzeptabel.**

---

### V12 — DismantleWizard Load-Helper extrahieren

**Auditor-Vorschlag**: Backlog-Item seit v0.1.20: "Helper extrahieren (~50-60 dupl. Zeilen)". 1.5h, niedrig-Risiko, später.

**Challenger-Verdikt: ✅ Bestätigt — PROCEED**

Klassisches Refactor. "Identische Logik in LoadAsync und LoadFromPendingAsync" ist schon kommentiert — Refactor-Kandidat dokumentiert. Pending-Mode hat genug Unterschiede dass Helper-API über Parameter genau gedacht sein muss. **3 Confirm-Pfade sind nicht im Scope** (eigene Iteration). Backlog seit v0.1.20 = länger als jedes andere Aufschub-Item. **v0.1.25 oder Filler-Item zwischen Releases.**

---

## 5. Konsolidierte Empfehlung

### Tragfähige Optionen (in Prioritäts-Reihenfolge)

| ID | Empfehlung | Aufwand (realistisch) | Wann |
|---|---|---|---|
| **V9** | Enum-Doku in CLAUDE.md (4-6 Klassifikations-Achsen + Mapping-Tabelle) | 1h | **jetzt** (v0.1.24-beta.2) |
| **V5** | SortInstruction-Builder-Helper | 1-2h | v0.1.24-beta.2 als Polish |
| **V10** | BuildSuggestion-Migration (REVISIERT: 2-3h, MinifigDetailView mitziehen) | 2-3h | v0.1.24-beta.3 nach Mini-Konzept |
| **V12** | DismantleWizard Load-Helper extrahieren | 1.5h | v0.1.25 |
| **V7** | DataChanged auf VM-Bus | 4-5h + Konzept-Doku | v0.1.25 (strukturell, nicht hotfixbar) |
| **V6** | SaveChanges-Interceptor für RecalcKind | 3-4h **wenn Konzept** | v0.1.25 nach Konzept-Schreiben |
| **V2** | Single+Group abräumen | 1-2h | v0.1.25 NACH V10 + MinifigDetailView |

### Hotspots: "Echter Schmerz" vs "Buchhaltung"

**Echter Schmerz** (User- oder Maintenance-Impact verifiziert):
- **H9** (DataChanged-Bus) — Performance-Hotfix war nötig, v0.1.25 plant strukturellen Fix
- **H10** (DismantleWizard 1116 LOC mit Drift-Risiko) — Backlog seit v0.1.20, schon einmal Bug
- **H11** (UX-Inkonsistenz Group-Mode in 2 Aufrufstellen) — User sieht anderes Modal beim Bauen vs Sortieren

**Buchhaltung** (Komplexität existiert aber funktional ok):
- **H1** (ScanViewModel 2147 LOC) — Konsequenz der App-Domäne, nicht Pathologie
- **H2** (Bin-Lifecycle-Wahrheiten) — normales relationales Modell mit Recalc-Disziplin
- **H4** (3 Reverse-Match-Pfade) — bewusst dokumentiert, ConsumeFloatingParts-Flag exhaustiv
- **H5** (parallele DTOs) — Layer-Trennung gewollt
- **H6** (3 Modi in BinInstructionVM) — Übergangszustand, OPEN-18 geplant
- **H7** (BinDisplayItem-Wrapper) — Konvention, "Fremder-Wrapper" ist bereits abgefangen
- **H8** (5 Schichten in Suggest) — dokumentiert + 13 Test-Aufrufer

**Verworfen**:
- **H3** (6 Enums) — Layer-Separation, kein Wachstumstrend-Problem
- **H12** (Service+Legacy-Fork) — bewusstes Test-Fallback, neu zu formulieren

### Aufgabe ohne expliziten Befund (vom Challenger genannt)

**Konkrete Klärung MinifigSummary.MoveToAsync Legacy-Pfad** (H12 Reformulierung): entweder (a) Mock-Persistence in Tests einführen und Legacy-Pfad löschen, oder (b) `throw new InvalidOperationException("Persistence-Service nicht injiziert")` in Production wenn `_persistence==null`. Aktuelle "silent fallback" ist Mittelweg ohne klare Linie.

---

## 6. Reihenfolge-Plan

### Sofort (v0.1.24-beta.2)

1. **V9** — Enum-Doku in CLAUDE.md (1h). Schmerzlinderung, verhindert dass nächste Iteration eine 7. Achse anlegt.
2. **V5** — SortInstruction-Builder-Helper (1-2h). Polish, niedrig-Risiko.

### Im Lauf von v0.1.24-beta.3 (Mini-Konzept-Pflicht)

3. **V10 (REVISED)** — BuildSuggestion + MinifigDetailView auf SortInstruction migrieren (2-3h). Mini-Konzept vorher: welche TakeSections+PutSections-Struktur für Group-Mode-Use-Cases?

### v0.1.25 (Performance + Cleanup-Track, Konzept-Doku-Pflicht für strukturelle Items)

4. **V12** — DismantleWizard Load-Helper extrahieren (1.5h). Backlog seit v0.1.20, einfach.
5. **V7** — DataChanged auf VM-Bus (4-5h + Konzept). Strukturell. Voraussetzung: Konzept-Doku.
6. **V6** — SaveChanges-Interceptor für RecalcKind (3-4h + Konzept). Voraussetzung: V7 fertig + Konzept zu Strict-Mode-Integration.
7. **V2** — Single+Group abräumen (1-2h). Pre-Condition: V10 vollständig (beide Aufrufer migriert).
8. **OPEN-18 Begleitend**: H12-Klärung MinifigSummary Legacy-Pfad.

### Nicht jetzt (verworfen oder DEFER ohne Datum)

- **V1** (ScanViewModel-Split): DEFER bis > 2800 LOC oder konkreter User-Schmerz
- **V3** (Reverse-Match-Vereinheitlichen): STOP — Vertragsbruch unmöglich ohne Code-Duplikation
- **V4** (DTO-Vereinen): DEFER bis V7 läuft
- **V8** (Enum-Harmonisierung): STOP — Layer-Bruch
- **V11** (BinDisplayItem-Extension-Method): STOP — Risk-Reward stimmt nicht

---

## 7. Streitpunkte / Uneinigkeit zwischen Auditor und Challenger

### Klare Uneinigkeit (3)

**V3 (Reverse-Match-Konsum vereinheitlichen)**:
- *Auditor*: 4-6h, mittel-Risiko, v0.1.25
- *Challenger*: **STOP** — Vertragsbruch unmöglich, weil die zwei Modi unterschiedliche Vor-Bedingungen haben. Vereinheitlichung würde Code-Duplikation erzwingen, nicht reduzieren.

**H3 (sechs Enums als Wachstumstrend)**:
- *Auditor*: Wachstumstrend "jede Iteration neue Achse" ist problematisch
- *Challenger*: Layer-Separation in 5 unterschiedlichen Schichten ist korrekt. ENGINEERING_PRINCIPLES.md 1.4 (explizit > implizit) wird nicht voll gewürdigt.

**V1 (ScanViewModel-Split)**:
- *Auditor*: 8-12h "später"
- *Challenger*: **DEFER ohne Datum** — die 2147 LOC sind Konsequenz der Kern-Workflow-Eigenschaft, kein technischer Schmerz mit User-Konsequenz.

### Leichte Differenzen (3)

**V10 (BuildSuggestion-Migration)**:
- *Auditor*: 30min-1h
- *Challenger*: 2-3h wegen MinifigDetailView (2. Aufrufer den Auditor nicht erwähnt)

**H7 (BinDisplayItem)**:
- *Auditor*: stellt als Problem dar
- *Challenger*: Code-Lese zeigt bewusst gelöst (`Z.664-668` "Fremder-Wrapper" return null)

**H12 (Legacy-Fork)**:
- *Auditor*: "toter Code in Production"
- *Challenger*: bewusster Test-Fallback. Reformulierung statt Cleanup.

### Wo Auditor recht hat (Challenger validiert)

- **Daten-Basis**: alle Code-Stellen, Zeilennummern, Enum-Werte exakt verifiziert. Kein Chat-Audit-Lesefehler-Pattern.
- **H9 + V7**: DataChanged-Bus ist echter struktureller Schmerz, v0.1.25-Track ist korrekt
- **H10 + V12**: DismantleWizard ist echter Refactor-Kandidat seit v0.1.20, "Drift" wurde schon einmal Bug
- **H11**: UX-Inkonsistenz ist real, auch wenn V10 unterschätzt wurde
- **V5, V9**: niedrig-hängende Früchte, sollten in v0.1.24-beta.2 rein

### Challenger-Selbst-Reflexion

12 Hotspots: 4× Bestätigt, 5× Eingeschränkt, 2× Verworfen, 1× Neu formuliert.  
12 Optionen: 3× PROCEED, 4× Eingeschränkt/REVISE, 3× DEFER, 2× STOP.

Verteilung ist nicht "zu zahm" (wären alle ✅), nicht "zu rebellisch" (wären alle ❌). Schwerpunkt ist: **Audit-Daten sind gut, aber 30-40% der Vorschläge haben Hidden Costs die Auditor nicht adressiert** — insbesondere Layer-Bruch-Risiken bei DTO-Vereinen und Vertrags-Bedingungen bei Reverse-Match.

---

## 8. Methoden-Hinweis: Anti-Pattern Lesefehler

Ein vorausgehender **Chat-Claude-Audit** hat `StorageBinKind`-Werte aus dem Gedächtnis rekonstruiert und Floating=3 angenommen. Real: **Floating=1, Waiting=2, Complete=3** (`StorageBinKind.cs:18-24`). Dadurch wurde ein Backlog-Eintrag "StorageBin.Kind-Drift bei Bestandsdaten" erstellt, der **kein realer Bug war** — die drei DB-Bins waren konsistent. Korrektur-Notiz in `BACKLOG.md:147-159`.

**Lesson**: Engineering-Prinzip 1.2 ("Diagnose vor Aktion") gilt auch für Audit-Befunde. Vor jeder enumerischen Aussage: `Read` auf die Enum-Datei statt aus dem Kontext zu schlussfolgern. Dieser Audit hat das versucht — jede Enum-Auflistung in 2.4 ist mit Datei:Zeile belegt. Für Methoden-Verhalten und Aufrufer-Zahlen wurden Grep-Belege genutzt. Der Challenger hat stichprobenartig nachverifiziert (Tabelle in 0.0 zeigt: alle Behauptungen hielten, nur Mikro-Abweichungen).

### Empfehlung für künftige Audits

1. **Code lesen, nicht erinnern.** Pflicht: jede Enum-/Methode-/LOC-Aussage mit Datei:Zeile-Beleg.
2. **Challenger-Pass mit Stichproben-Verifikation.** Nicht nur Verdikt, sondern Re-Read der zentralen Auditor-Befunde.
3. **Bei jedem Hotspot fragen**: "konkreter User-Schaden bei Nicht-Anfassen?" — wenn keiner: Buchhaltung, nicht Hotspot.
4. **Bei jedem Refactor-Vorschlag fragen**: "Hidden Costs? Layer-Bruch? Was muss mitziehen?"

---

## 9. Begrenzungen dieses Audits

- **Brickognize-Category-Domäne** (vom Chat-Audit als "5 Konzepte" markiert): nicht abschließend verifiziert. Auditor hat `ICategoryBinMappingService.cs` gelesen, sah die "Unbekannt"-Pseudo-Kategorie und das Seen-/Mapping-Dictionary-Pärchen, aber 5 Konzepte (wie Chat-Audit sagte) nicht eindeutig identifiziert. Markiert als "nicht abschließend verifizierbar mit der Lesetiefe".
- **Aufrufer-Inventur für V1/V3** wäre tiefer machbar (genaue Auflistung statt "Singleton + Direkt-Konsumenten").
- **Konzept-Doku `docs/v0.1.24-konzept-ux-konsistenz.md`** (2075 LOC) nur abschnittsweise gelesen (Sektion 4.3 + 8 + 9).
- **Performance-Auswirkung von H6/H7** (Wrapper-Overhead bei 100+ Bins) nicht mit Logs/Profiling verifiziert — nur strukturell beobachtet.

---

## 10. Code-Pfade für Folge-Iterationen

Wichtigste Stellen (alle absolut), die bei den Top-7-Empfehlungen angefasst werden:

- `C:\Projekte\HBSort\HBSort.Core\Models\StorageBinKind.cs` — Enum-Werte-Referenz
- `C:\Projekte\HBSort\HBSort\ViewModels\ScanViewModel.cs` — Z.1666 PersistPending, Z.1730 SortInstruction-Builder, Z.1882 TransferFloatingPart
- `C:\Projekte\HBSort\HBSort\ViewModels\DismantleWizardViewModel.cs` — Z.103 LoadAsync, Z.480 LoadFromPendingAsync (V12-Helper-Extraktion), Z.707/753/848 (3 Confirm-Pfade)
- `C:\Projekte\HBSort\HBSort\ViewModels\BinInstructionViewModel.cs` — Z.66-69 (Migrations-Begründungs-Anker), Z.211-217 (Mode-Enum), Z.97-185 (3 Show-Methoden)
- `C:\Projekte\HBSort\HBSort\ViewModels\MinifigSummaryViewModel.cs` — Z.261-320 (Service+Legacy-Fork, H12)
- `C:\Projekte\HBSort\HBSort\ViewModels\InventoryListViewModel.cs` — Z.622 StatusKind
- `C:\Projekte\HBSort\HBSort.Core\Services\IMinifigPersistenceService.cs` — Z.25 DataChanged, Z.220-246 ConsumeFloatingParts XML-Doc, Z.294-312 ConsumedFloatingPartInfo
- `C:\Projekte\HBSort\HBSort.Core\Services\StorageBinService.cs` — Z.143-267 SuggestBinForFloatingPartAsync (H8), Z.685-717 RecalculateKindAsync (V6-Anker)
- `C:\Projekte\HBSort\HBSort.Core\Services\IStorageBinService.cs` — Z.260 BinTargetKind
- `C:\Projekte\HBSort\HBSort\Views\BuildSuggestionDetailDialog.xaml.cs` — Z.117 ShowBinInstructionGroup (V10-Migration-Stelle 1)
- `C:\Projekte\HBSort\HBSort\Views\MinifigDetailView.xaml.cs` — Z.161 zweite ShowBinInstructionGroup (V10-Migration-Stelle 2, vom Challenger neu identifiziert)
- `C:\Projekte\HBSort\HBSort\Helpers\BinDisplayItem.cs` — Wrapper-Definition (H7/V11-Anker)
- `C:\Projekte\HBSort\HBSort\ViewModels\PendingMinifigViewModel.cs` — Z.181-198 ConsumedFromBins (H5/V4-Anker)

---

*Audit erstellt von hbsort-auditor (Phase A/B/C) + hbsort-challenger (Verdikt-Pass), synthetisiert 2026-05-17. Tag-Bezug: `1dc7ae8b` v0.1.24-beta.1 Phase 3. Methode dokumentiert in Sektion 8.*
