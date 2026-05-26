# Komplexitaets-Audit BL-Inventar (beta.6 - beta.9)

**Datum:** 2026-05-26
**Scope:** Neu/geaendert seit dem v0.1.24-beta.1-Audit (`docs/komplexitaets-audit-2026-05-17.md`)
**Tag-Bezug:** `main`-Head `328f04b7` (v0.1.24-beta.9 Hotfix, ungetagged)
**Auditor:** hbsort-auditor (Phase A/B/C)
**Challenger:** hbsort-challenger (Verifikation, Klassifikations-Revision)
**Methode:** Code-only Lesung mit Datei:Zeile-Belegen (Engineering-Prinzip 1.2). Challenger fuehrt direkte Stichproben mit Grep-Belegen und Edge-Case-Traces durch.

---

## Executive Summary

Die BL-Inventar-Iteration (beta.6 - beta.9) hat in vier Wellen geliefert: Sync-Infra (Phase 1), Tab-UI (Phase 2), Komplettieren-Integration mit Reservierungen (Phase 3), und zuletzt einen Suggest-vs-AvailableBins-Symmetrie-Hotfix (beta.9). Code-Qualitaet ist im Schnitt sauber — explizite XML-Doku, Concurrency-Disziplin (Semaphore, CTS, Compensating-Action-Pattern in `ApplyBlReservationAsync`), klare Layer-Trennung Service/VM/View. Der Suggest-Hotfix in `StorageBinService.cs:230-233` ist exemplarisch gut belegt (3-stufige Historie als Kommentar).

**Aber: ein kritischer Lebenszyklus-Bug** in der Reserve/Release-Buchhaltung, der nicht in Phase 3 selbst wirkt, sondern bei **jedem Loesch-/Zerlegungs-/Cleanup-Pfad** Geist-Reservierungen aufreisst.

**Befund-Klassifikation nach Challenger-Verifikation:**

| Ebene | Anzahl | Befunde |
|---|---|---|
| Kritisch (Stable-Blocker) | **1** | H1 |
| Mittel | 4 | H2, H3, H7, H9 |
| Niedrig | 3 | H4, H5, H8 |
| Sehr niedrig / theoretisch | 1 | H6 |

**Pflicht vor v0.1.24-Stable:**
1. **V1** (H1) — Release-Pflicht in `DeleteAsync`/`DismantleAsync`/`RemoveExportedMinifigsAsync`/`CleanupOldDismantled...`/`CleanupOnePartCompletes` — **kritisch**, sonst dauerhafte Datenverfaelschung im Inventar-Tab
2. **V6** (H4) — Quantity-Cap im Snapshot-Restore (1-Zeilen-Fix mit Log-Warning)
3. **Doku-Fix** — ReservedQuantity-Lifecycle-Block in `BlInventoryService.cs:15-44` ehrlich machen (Phase 2/3 sind dokumentiert aber nicht implementiert)

**v0.1.25 (Konzept-Doku-pflichtig):**
4. V3 — `IsEffectivelyComplete`/`EffectivelyMissing` konsequent (12 Drift-Stellen statt der vom Auditor zuerst geschaetzten 6)
5. V4 — `BlReservationService` extrahieren (H7+H9 zusammen, kommt mit Konzept-Doku)
6. V2 — UndoService um `BlInventoryReservation/Release` erweitern (UX-Polish, kein Datenverlust)

**Verworfen / "nicht jetzt":**
- H6 (`completedParts`-DTO ohne UI-Konsumenten) — tot
- H8 (`InventoryChanged` parallel zu `DataChanged`) — Domaene gerechtfertigt getrennt
- H5 (Reverse-Match-Konsum) — kein Trigger-Pfad aktuell, mit Phase 4 (Export) gemeinsam einsammeln

**Gut geloest, nicht anzufassen:**
- Snapshot-Replace mit ReservedQuantity-Erhalt via Pre-Read (`BlInventoryService.cs:71-79`) — sauberes Pattern
- LIFO-Release in `ReleaseAllBlReservationsAsync` (`MinifigSummaryViewModel.cs:477-509`) — korrekt mit defensiver Drift-Behandlung; zwei Race-Edge-Cases sind theoretisch moeglich (siehe Sektion 4)
- Suggest-Hotfix beta.9 (`StorageBinService.cs:207-233`) — Symmetrie zu `GetEligibleBinsWithCountsAsync(FloatingTarget, partNo=null)` ist exakt
- `EffectiveCollected`-Pattern in den **Read-Pfaden** (`InventoryListViewModel`, `BinDetailViewModel`, `WaitingDetailViewModel`, `WantedListExportService`) konsistent migriert
- BL-Inventar-Tab: Singleton-DI, `InventoryChanged`-Subscription mit Unsubscribe in Dispose, Semaphore fuer Thumbnail-Concurrency, CTS-Cancel pro Re-Load

---

## 1. Phase A — Komplexitaets-Hotspots

### 1.1 Hohe zyklomatische Komplexitaet

| Methode | Datei:Zeile | LOC | Branches (~) | Bewertung |
|---|---|---|---|---|
| `MinifigSummaryViewModel.ApplyBlReservationAsync` | `HBSort/ViewModels/MinifigSummaryViewModel.cs:317-407` | 91 | ~12 | Compensating-Action-Pattern (BL Reserve → DB-Update → bei Fehler Release). Macht 5+ Dinge. Kandidat fuer Service-Extraction. |
| `MinifigSummaryViewModel.ReleaseAllBlReservationsAsync` | `HBSort/ViewModels/MinifigSummaryViewModel.cs:423-541` | 119 | ~15 | LIFO-Schleife, Status-Rollback, ScanEvent-Pflege. Drei Wahrheiten parallel (Part-Feld, ScanEvents, BlInventoryLot). |
| `MinifigSummaryViewModel.LoadAsync` (inkl. Background-Tasks) | `HBSort/ViewModels/MinifigSummaryViewModel.cs:90-168` | 79 | ~10 | 3 Fire-and-Forget-Tasks + Reaktor-Block. Dicht aber lesbar. |
| `BuildSuggestionsViewModel.RefreshAsync` | `HBSort/ViewModels/BuildSuggestionsViewModel.cs:117-248` | 132 | ~14 | Grenzwertig. beta.8-Erweiterung an Schritt 5 sauber angesetzt. |
| `BlInventoryViewModel.LoadAsync` | `HBSort/ViewModels/BlInventoryViewModel.cs:158-210` | 53 | ~7 | OK. Schritt-Kommentar-Disziplin. |
| `BricklinkClient.GetInventoryAsync` | `HBSort.Core/Services/BricklinkClient.cs:356-444` | 89 | ~8 | Boilerplate wegen Rate-Limit-Gate + LogCall + Exception-Translate. Normal fuer BL-API-Wrapper. |

### 1.2 Unklare Verantwortlichkeiten

- **`MinifigSummaryViewModel` (727 LOC, vorher 359 LOC, Audit 2026-05-17 Sektion 2.3)** vereint jetzt vier orthogonale Aspekte:
  1. Daten-Lade-/Bind-VM (LoadAsync + OnPropertyChanged-Block)
  2. Image- + Color-Swatch-Loader (best-effort)
  3. BL-Reservierungs-Maschine (`ApplyBl...`, `ReleaseAllBl...`)
  4. Move-Pfad mit Legacy-Fork (H12 aus altem Audit)

  Die Reservierungs-Logik gehoert konzeptuell in einen Service. Heute ist sie an die Lifetime des Summary-Dialogs gebunden — Phase 4 (Export-Dialog) muesste den Pfad duplizieren oder dynamisch auf einen Summary-Dialog-VM zugreifen.

- **`PartCheckBox_Click`** in `HBSort/Views/MinifigSummaryDialog.xaml.cs:114-174`: orchestriert ReleaseBl → Unassign → Reload → CheckAndMarkComplete in einem Click-Handler. Zwei Service-Locator-Aufrufe (`App.Services.GetRequiredService<>`) mitten in der UI-Logik. Klassischer Layer-Bruch.

### 1.3 `EffectiveCollected`-Pattern: Konsistenz-Check

**Read-Pfade — gut migriert (5/5):**

| Stelle | Pattern |
|---|---|
| `InventoryListViewModel.cs:536` | `Count(p => p.IsEffectivelyComplete)` |
| `BinDetailViewModel.cs:174` | `Count(p => p.IsEffectivelyComplete)` |
| `WaitingDetailViewModel.cs:78,84` | `p.EffectivelyMissing` |
| `WantedListExportService.cs:65,101` | `p.EffectivelyMissing` |
| `MinifigSummaryViewModel.cs:109,382,533` | `Count(p => p.IsEffectivelyComplete)` |

**Schreib-/Check-Pfade — Formel inline statt Property (Challenger zaehlt 12 Stellen):**

| # | Datei | Zeile | Kontext |
|---|---|---|---|
| 1 | `MinifigPersistenceService.cs` | 246 | `DismantleAsync` Direkt-Assign Komplett-Check (aliasiert) |
| 2 | `MinifigPersistenceService.cs` | 546 | `CheckAndMarkCompleteAsync` |
| 3 | `MinifigPersistenceService.cs` | 752 | `PersistAndStoreAsync` isComplete |
| 4 | `PartLookupService.cs` | 71-77 | `FindWaiting`-Filter (EF-LINQ Query, NotMapped nicht uebersetzbar) |
| 5 | `PartLookupService.cs` | 136-138 | `AssignPartToMinifig` Komplett-Check (aliasiert) |
| 6 | `PartLookupService.cs` | 563-565 | `CollectMinifig` Komplett-Check |
| 7 | `PartLookupService.cs` | 782 | `CollectMinifig` 2. Variante |
| 8 | `StorageBinService.cs` | 884 | `GetEligibleBinsAsync` Reverse-Match-Bypass (EF-LINQ) |
| 9 | `StorageBinService.cs` | 991 | `GetEligibleBinsWithCountsAsync` Reverse-Match-Bypass (EF-LINQ) |
| 10 | `InvalidBinKindException.cs` | 101 | `BinKindGuard` Reverse-Match-Bypass |
| 11 | `MinifigSummaryViewModel.cs` | 344-347 | `ApplyBlReservation` Komplett-Check (aliasiert) |
| 12 | `MinifigSummaryViewModel.cs` | 512-515 | `ReleaseAll`-Status-Check (aliasiert) |

Davon EF-LINQ-Queries (3, koennen `[NotMapped]`-Property nicht uebersetzen): #4, #8, #9. Bleiben aus DB-Gruenden inline.

Davon Aliasing-Variante (`p.Id == part.Id ? part.QuantityCollected : p.QuantityCollected`): #1, #5, #11, #12. **Tote Defensive** — EF-Change-Tracking versorgt jeden Lese-Zugriff mit dem aktuellen Wert; das Aliasing ist unnoetig.

### 1.4 ReservedQuantity-Lebenszyklus

Der Doc-Block in `BlInventoryService.cs:15-44` beschreibt drei Phasen (Reserve → Export → Sync-nach-Export). **Code-Realitaet:**

- **Phase 1 (Reserve)**: `ReserveAsync` Z.190 — `lot.ReservedQuantity += qty`. ✓ Implementiert.
- **Phase 2 (Export)**: Doku sagt "Nach erfolgreichem Hochladen setzt der Dialog ReservedQuantity = 0 zurueck". Challenger-Grep: **keine Treffer im Export-Pfad** — Phase 2 ist DOKUMENTIERT aber NICHT IMPLEMENTIERT.
- **Phase 3 (Sync nach Export)**: haengt von Phase 2 ab → ebenfalls nicht implementiert.

→ **Doku ist Vorgriff auf Phase 4**, aktuell irrefuehrend.

### 1.5 Symmetrie-Vertrag (beta.4 + beta.9 Suggest-Hotfix)

**Stelle:** `StorageBinService.SuggestBinForFloatingPartAsync` Z.207-233.

Historie laut Code-Kommentar:
- v0.1.23: `allowedKinds = [Empty, Floating, Waiting, Complete]` mit `excludeMinifigId` (Annahme: zu zerlegende Figur macht Bin gleich frei) → Diskrepanz zu `AvailableBins`
- v0.1.24-beta.4: Complete raus, Waiting blieb → Pure-Waiting-Bin-Fall blieb buggy
- v0.1.24-beta.9: strikt `[Empty, Floating]` → exakt symmetrisch zu `GetEligibleBinsWithCountsAsync(FloatingTarget, partNo=null)`

**Andere Stellen mit aehnlichem Symmetrie-Risiko geprueft, keine offene Diskrepanz gefunden.** Der beta.9-Hotfix ist isoliert.

---

## 2. Phase B — Konkrete Befunde

Pro Befund: Auditor-Klassifikation, Challenger-Verifikation, finales Verdikt.

### H1 — Delete/Dismantle/Cleanup released keine BL-Reservierungen — **KRITISCH (bestaetigt)**

**Beleg:**
- `MinifigPersistenceService.cs:98-161` (`DeleteAsync`): `ctx.TrackedMinifigs.Remove(minifig)` Z.150. Cascade-Delete entfernt `TrackedMinifigPart`-Eintraege.
- `MinifigPersistenceService.cs:163-396` (`DismantleAsync`): `ctx.TrackedMinifigs.Remove(minifig)` Z.376.
- `MinifigPersistenceService.cs:398-413` (`CleanupOldDismantledMinifigsAsync`), `:415-432` (`CleanupOnePartCompletesAsync`), `:434-481` (`RemoveExportedMinifigsAsync`): alle gleiche `RemoveRange`-Pattern.
- **Challenger-Verifikation**: Grep `_blInventory|ReleaseAsync|IBlInventoryService` in `MinifigPersistenceService.cs` → **0 Treffer**. Konstruktor Z.45-53 nimmt **kein IBlInventoryService-Field** entgegen.

**Befund:** Bei jedem Loesch-/Cleanup-Pfad mit RequiredParts, die `QuantityReservedFromBl > 0` haben, bleibt `BlInventoryLot.ReservedQuantity` erhoeht — ohne korrespondierende Figur. Snapshot-Replace beim naechsten Sync **erhaelt** die Geist-Reservierung sogar (`BlInventoryService.cs:92-93`).

**Trace User-Auswirkung:**
- User legt Figur an, reserviert 3 Teile aus BL-Shop (BL-Lot: `Quantity=10, ReservedQuantity=3`)
- User waehlt "Aufgeben" → `DismantleAsync` → TrackedMinifig + RequiredParts geloescht
- BL-Lot bleibt mit `Quantity=10, ReservedQuantity=3` → Verfuegbar = 7 statt 10
- Geist-Reservierung lebt; nur manueller User-Eingriff loest sie (es gibt aber keinen Pfad ausser direktem DB-Edit)

**Auswirkung:** dauerhafte Verfaelschung der "Verfuegbar"-Spalte im BL-Inventar-Tab. Phase 4 (Export) wuerde Adjustments fuer Lots schreiben, die der User gar nicht mehr braucht.

**Verdikt:** ✅ **KRITISCH** — Stable-Blocker (Datenverfaelschung).

### H2 — UndoService kennt neue ScanTypes nicht — **MITTEL (Challenger-Revision von KRITISCH)**

**Beleg:**
- `UndoService.cs:140-148` `IsUndoableType`-Switch: nur 5 ScanTypes (`Delete`, `Move`, `Complete`, `BinFreed`, `BinCreated`).
- `UndoService.cs:90-97` `UndoAsync`-Switch: gleiche 5 Types, default `(null, Array.Empty<int>())` → "Undo fuer diesen Aktions-Typ ist nicht implementiert."
- `ScanEvent.cs:97-98` definiert neu `BlInventoryReservation`, `BlInventoryRelease`.
- `MinifigSummaryViewModel.cs:363-373` schreibt korrektes `UndoData` (UndoSnapshotBlReservation Z.358).

**Trace User-Auswirkung:**
- User reserviert ein BL-Lot. Schaut in die Undo-Liste (`GetUndoableActionsAsync`) — Reservierung ist da (`!WasUndone && UndoData != null`), aber `IsUndoableType` filtert raus → erscheint **nicht** in der Liste.
- Reservierungen tauchen aber in der **History-Anzeige** auf (RecentScans-Tab), ohne Undo-Aktion.

**Challenger-Begruendung fuer Down-Grade:** UX-Mangel, kein Datenverlust. Paralleler Undo-Pfad ueber Uncheck im Summary-Dialog funktioniert (`MinifigSummaryDialog.xaml.cs:141 → ReleaseAllBlReservationsAsync`).

**Verdikt:** ✅ **MITTEL** — UX-Inkonsistenz, kein Stable-Blocker.

### H3 — Komplett-Check-Formel inline an 12 Stellen — **MITTEL (Challenger-Verschaerfung von "6+")**

**Beleg:** siehe Sektion 1.3, Tabelle mit 12 Stellen. Property `TrackedMinifigPart.IsEffectivelyComplete` (Z.84) existiert, wird in 5 Read-Pfaden genutzt, in 12 Schreib-/Check-Pfaden NICHT.

Davon:
- 3 EF-LINQ-Queries: bleiben inline (NotMapped nicht uebersetzbar) — `PartLookupService.cs:71-77`, `StorageBinService.cs:884`, `StorageBinService.cs:991`
- 4 mit Aliasing-Variante: unnoetige Defensive (`p.Id == part.Id ? part.QuantityCollected : p.QuantityCollected` etc.)
- 5 ohne Aliasing: direkter Drop-In moeglich

**Auswirkung:** Wartungs-Last bei kuenftigen Erweiterungen (Phase 4 wird die Formel an mindestens 1-2 weiteren Stellen brauchen). Bug-Wahrscheinlichkeit niedrig, aber jede Aenderung der Logik muss in 12+ Stellen synchron passieren.

**Verdikt:** ✅ **MITTEL** — Drift-Anfaelligkeit hoeher als zunaechst gemeldet.

### H4 — Snapshot-Replace ohne Quantity-Cap — **NIEDRIG (bestaetigt)**

**Beleg:** `BlInventoryService.cs:90-108`:
```csharp
var reserved = reservedByLot.TryGetValue(dto.LotId, out var r) ? r : 0;
...
ReservedQuantity = reserved,  // KEIN Math.Min(reserved, dto.Quantity)
```

**Trace:**
- User reserviert 5 von 10 Lot-Einheiten → `Quantity=10, ReservedQuantity=5`
- Inzwischen verkauft User 7 Stueck via BL ausserhalb HBSort
- Snapshot-Replace: neue `Quantity=3`, restore `ReservedQuantity=5`
- `Available = 3 - 5 = -2`
- `FindLotsForPartAsync` Z.168 (`Where(l => l.Quantity - l.ReservedQuantity > 0)`) filtert raus → kein UI-Effekt aber Daten korrupt

**Auswirkung:** UI-Pfade fangen es ab (`Available > 0`-Filter). `Reserve-Check` Z.181 (`available < qty`) blockiert neue Reservierungen → kein Doppel-Reserve-Schaden. Selbstheilung beim naechsten `ReleaseAsync`-Aufruf via LIFO-Pfad.

**Verdikt:** ✅ **NIEDRIG** — 1-Zeilen-Fix mit Log-Warning, defensive Robustheit.

### H5 — Reverse-Match-Konsum erkennt BL-Reservierung nicht — **NIEDRIG aktuell, MITTEL ab Phase 4 (Challenger-Revision)**

**Beleg:** `MinifigPersistenceService.cs:697`: `var stillNeeded = required.QuantityNeeded - required.QuantityCollected;` — physisch, ohne `QuantityReservedFromBl` zu beruecksichtigen.

**Challenger-Trace:** `PersistAndStoreAsync` laeuft 1x beim Anlegen einer Figur. Zu diesem Zeitpunkt ist `QuantityReservedFromBl = 0` (das Feld wird erst durch `ApplyBlReservationAsync` im Summary-Dialog der wartenden Figur inkrementiert, also **nach** PersistAndStore). Damit gibt es **aktuell keinen Trigger-Pfad**, der den Bug ausloest.

Pfade die theoretisch aktivieren koennten:
- Phase 4 (BSX-Export-Cleanup mit Pre-Fill) — geplant
- Hypothetischer "BL-Pre-Fill-Wizard"

**Auswirkung:** Aktuell tot. In Phase 4 echter Geld-Bug (Figur als Complete markiert, Export-Pfad listet Teile als verkaufsbereit, BL-Reservierung bleibt aktiv → Doppel-Verkauf-Risiko).

**Verdikt:** ✅ **NIEDRIG aktuell** (kein Trigger) — mit Phase 4 (Export) gemeinsam loesen, dann MITTEL.

### H6 — completedParts physisch / isComplete Effective — **SEHR NIEDRIG / theoretisch (Challenger-Revision)**

**Beleg:**
- `MinifigPersistenceService.cs:728-729, 744-745`: `completedParts` wird mit `if (required.QuantityCollected >= required.QuantityNeeded)` gezaehlt — physisch.
- `MinifigPersistenceService.cs:749-753`: `isComplete` benutzt Effective-Formel.
- `MinifigPersistenceService.cs:804`: `CompletedRequiredParts = completedParts` in `PersistMinifigResult`.

**Challenger-Verifikation:** Grep `CompletedRequiredParts` in `HBSort/` → **0 UI-Konsumenten**. Nur Tests (3x) und das DTO selbst.

**Verdikt:** ✅ **SEHR NIEDRIG** — Tote DTO-Property, keine User-Auswirkung. **Empfehlung: nicht beheben** (Pflege-Aufwand uebersteigt Nutzen).

### H7 — Geschaeftslogik im Code-Behind: `PartCheckBox_Click` — **MITTEL (bestaetigt)**

**Beleg:** `MinifigSummaryDialog.xaml.cs:114-174`. 60 LOC Click-Handler mit:
1. Z.118 Service-Locator: `App.Services.GetRequiredService<IPartLookupService>()`
2. Z.124-133 Assign-Schleife
3. Z.140-152 Uncheck-Pfad mit `ReleaseAllBlReservationsAsync` + `UnassignPartFromMinifigAsync` + zwei Notification-Varianten
4. Z.155 `_viewModel.LoadAsync()` (kompletter Reload)
5. Z.159-164 `_persistence.CheckAndMarkCompleteAsync` (zweiter Service-Call)
6. Z.169 Service-Locator: `App.Services.GetRequiredService<IDialogService>()`

**Befund:** Vier orthogonale Aspekte in einem Click-Handler. Service-Locator mitten in UI-Logik bricht DI-Konvention.

**Verdikt:** ✅ **MITTEL** — Symptom einer fehlenden Sub-VM (V4).

### H8 — `InventoryChanged`-Bus parallel zu `DataChanged` — **NIEDRIG / AKZEPTABEL (Challenger-Revision)**

**Beleg:**
- `IBlInventoryService.cs:40` `event EventHandler? InventoryChanged`
- Gefeuert in `BlInventoryService.cs:122, 195, 219`
- Abonniert in `BlInventoryViewModel.cs:139` mit Dispose-Cleanup Z.146

**Challenger-Begruendung:** Getrennte Domaene ist gerechtfertigt — BL-Inventar-Lots sind nicht Teil von Minifig-Daten. Inventar-Tab interessiert sich nicht fuer Minifig-Sync. **Doppelter Refresh-Cost** bei Reserve/Release (sowohl `InventoryChanged` als auch `_persistence.RaiseDataChanged()` werden gefeuert), aber klein.

**Verdikt:** ✅ **AKZEPTABEL** — kein Cleanup-Bedarf. Bei v0.1.25 V7 (DataChanged-Refactor aus altem Audit) mitberuecksichtigen.

### H9 — `MinifigSummaryViewModel` LOC verdoppelt (359 → 727) — **MITTEL (bestaetigt)**

**Beleg:**
- Audit 2026-05-17 Sektion 2.3: `MinifigSummaryViewModel.cs` mit **359 LOC**
- Aktuell: **727 LOC** (verifiziert via Read-Tool)
- Neu (alle aus beta.8 Phase 3):
  - `LoadBlAvailabilitiesAsync` Z.253-302 (~50 LOC)
  - `ApplyBlReservationAsync` Z.317-407 (~91 LOC)
  - `ReleaseAllBlReservationsAsync` Z.423-541 (~119 LOC)
  - `BlAvailabilityInfo`-Klasse Z.714-727
  - `SummaryPartViewModel`-Erweiterungen Z.652-693

**Befund:** Beide Reservation-Methoden sind **vollstaendige Transaktions-Pfade** mit DB-Context-Open, Service-Call, DB-Mutation, Inline-Komplett-Check, ScanEvent + UndoData, Status-Reset, UI-Property-Refresh, `Persistence.RaiseDataChanged`. Das ist Service-Schicht-Verhalten, nicht VM-Verhalten.

**Verdikt:** ✅ **MITTEL** — strukturelle Schwaeche, V4-Refactor gehoert in v0.1.25.

---

## 3. Phase C — Verbesserungs-Vorschlaege

### V1 — Release-Pflicht in `DeleteAsync`/`DismantleAsync`/`RemoveExported`/`Cleanup*` — **vor Stable**

**Befund:** H1 (kritisch).

**Vorschlag:**
1. Helper-Methode in `MinifigPersistenceService`: 
   ```csharp
   private async Task ReleaseAllBlReservationsForMinifigsAsync(
       IEnumerable<int> minifigIds, UserDataContext ctx, CancellationToken ct)
   ```
   Iteriert pro Minifig die RequiredParts mit `QuantityReservedFromBl > 0`, ruft `_blInventory.ReleaseAsync(lotId, qty)` ueber die korrespondierenden ScanEvents (LIFO).
2. Vor jedem `ctx.TrackedMinifigs.Remove(...)` in den 5 Methoden den Helper aufrufen.
3. **Konstruktor-Erweiterung:** `IBlInventoryService` als optionalen Service-Parameter aufnehmen.
4. **Sonderfall `RemoveExportedMinifigsAsync`** (Phase 4 relevant): laut Doku in `BlInventoryService.cs:25-30` ist beim Export `ReservedQuantity = 0` zu setzen, aber `Quantity` zu reduzieren (User hat das Teil im Shop verkauft). Fuer beta.10 reicht: "released alle BL-Reservierungen vor dem Delete der Figur, Quantity-Anpassung kommt mit Phase 4-Konzept".

**Aufwand:** 2-3h inkl. Tests fuer die 5 Pfade.

**Empfehlung:** **Vor Stable (beta.10 oder Release-Block).**

### V2 — `UndoService` um `BlInventoryReservation/Release` erweitern — **v0.1.25 (UX-Polish)**

**Befund:** H2 (mittel).

**Option A (Voll-Integration):** `IsUndoableType` + `UndoAsync`-Switch ergaenzen, neue Methode `UndoBlReservationAsync` (deserialisiere `UndoSnapshotBlReservation`, ruf `_blInventory.ReleaseAsync` + `TrackedMinifigPart.QuantityReservedFromBl--`).

**Option B (Doku):** ScanEvent-Doc-Block ergaenzen: "BL-Reservierungs-Events sind nicht ueber RecentScans-Undo undobar — siehe Summary-Dialog → Teil-Haekchen abwaehlen."

**Aufwand:** A 2-3h, B 30min.

**Empfehlung:** **Option A in v0.1.25** wenn V4 (BlReservationService) ohnehin gemacht wird. Bis dahin Option B als Stoplueck.

### V3 — `IsEffectivelyComplete`/`EffectivelyMissing` konsequent nutzen — **v0.1.25 (opportunistisch)**

**Befund:** H3 (mittel, 12 Stellen).

**Vorschlag:**
- 4 Aliasing-Stellen (#1, #5, #11, #12) auf direkten Property-Zugriff vereinfachen (EF-Change-Tracking traegt aktuelle Werte)
- 5 nicht-aliasierte in-memory Stellen auf `IsEffectivelyComplete` umstellen
- 3 EF-LINQ-Stellen mit Kommentar `// IsEffectivelyComplete: NotMapped, daher inline` markieren

**Aufwand:** 1h (mechanisch + bestehende Tests laufen ohne Aenderung).

**Empfehlung:** **v0.1.25 opportunistisch** — beim naechsten Touch der jeweiligen Stelle ersetzen.

### V4 — `BlReservationService` extrahieren — **v0.1.25 (mit Konzept-Doku)**

**Befund:** H7 + H9 (mittel).

**Vorschlag:** Neuer `IBlReservationService` mit:
- `Task<BlReservationResult> ApplyReservationAsync(int trackedMinifigPartId, int lotId, CancellationToken)`
- `Task<int> ReleaseAllForPartAsync(int trackedMinifigPartId, CancellationToken)`
- `Task<int> ReleaseAllForMinifigAsync(int minifigId, CancellationToken)` (fuer V1)

`MinifigSummaryViewModel` und `MinifigPersistenceService` reduzieren sich auf Aufrufe. Service-Tests gegen InMemory-DB werden moeglich.

**Aufwand:** 4-5h.

**Empfehlung:** **v0.1.25 mit Konzept-Doku** — strukturell, nicht hotfixbar.

### V5 — Reverse-Match released ueberschuessige BL-Reservierungen — **mit Phase 4**

**Befund:** H5.

**Vorschlag:** Nach Konsum-Schritt in `MinifigPersistenceService.cs:728`:
```csharp
if (required.QuantityCollected >= required.QuantityNeeded 
    && required.QuantityReservedFromBl > 0)
{
    var releaseQty = Math.Min(required.QuantityReservedFromBl,
        required.QuantityCollected + required.QuantityReservedFromBl - required.QuantityNeeded);
    // Release ueber BlReservationService.ReleaseAllForPartAsync
}
```

**Aufwand:** 1h. Tests fuer 4-5 Szenarien.

**Empfehlung:** **v0.1.25 mit Phase 4 Konzept** — Pflicht bevor Export-Pfad live geht.

### V6 — Snapshot-Replace cappt `ReservedQuantity` auf neue `Quantity` — **beta.10**

**Befund:** H4 (niedrig).

**Vorschlag:** `BlInventoryService.cs:92`:
```csharp
var rawReserved = reservedByLot.TryGetValue(dto.LotId, out var r) ? r : 0;
var reserved = Math.Min(rawReserved, dto.Quantity);
if (reserved < rawReserved)
    Log.Warning("Snapshot-Replace Lot {LotId}: ReservedQuantity gecappt {Old} -> {New} (Quantity={Q})",
        dto.LotId, rawReserved, reserved, dto.Quantity);
```

**Aufwand:** 30min.

**Empfehlung:** **beta.10** — kostenlose Robustheit.

### V7 — Race-Edge-Cases in `ReleaseAllBlReservationsAsync` haerten — **v0.1.25**

**Edge-Cases (vom Challenger identifiziert):**
1. **Lot nicht mehr existiert (Snapshot-Replace hat es verloren):** `ReleaseAsync` gibt `false` zurueck, LIFO-Loop `continue`, ScanEvent bleibt offen, `released`-Counter zu klein → `QuantityReservedFromBl` nur teilweise reduziert.
2. **Race zwischen Release und Save:** Z.488 `ev.WasUndone = true` aber `SaveChangesAsync` Z.522 erst nach allen Loops. Bei Exception zwischen ReleaseAsync und SaveChanges → BL-Reservierung released, ScanEvent noch `WasUndone=false`.

**Vorschlag:** `SaveChangesAsync` direkt nach jeder erfolgreichen Release-Operation statt am Ende. Bei Lot-weg-Case: defensive Snapshot-Compensation (Part-Feld auf Max(0, current - matching.Count) setzen).

**Aufwand:** 1h.

**Empfehlung:** **v0.1.25** mit V4-Refactor.

### Doku-Fix — `ReservedQuantity`-Lifecycle in `BlInventoryService.cs` ehrlich machen — **beta.10**

**Befund:** Sektion 1.4.

**Vorschlag:** Doc-Block Z.15-44 ergaenzen: "Phase 2 (Export) und Phase 3 (Sync nach Export) sind in Phase 4 (BSX-Export-Konzept) geplant, aber in der aktuellen v0.1.24-beta.9 NICHT implementiert. Die Reservierungs-Buchhaltung haengt aktuell nur an manuellem User-Uncheck via Summary-Dialog."

**Aufwand:** 5min.

---

## 4. Verworfen / nicht jetzt

- **H6** (`completedParts`-DTO ohne UI-Konsumenten) — tot, nicht beheben.
- **H8** (`InventoryChanged` parallel zu `DataChanged`) — Domaene gerechtfertigt getrennt, kein Cleanup-Bedarf.
- **InventoryChanged-Bus konsolidieren mit DataChanged** — kein eigener Vorschlag; bei v0.1.25-V7 aus altem Audit (DataChanged-Refactor) mit-anpassen wenn sinnvoll.
- **Service-Locator in Dialog-Code-Behinds** — existiert bereits in vielen anderen Dialogen (alter Audit-Buchhaltungs-Hotspot). Kein neuer Schmerz durch beta.6-9 — bei strukturellem Cleanup einsammeln.

---

## 5. Konsolidierte Empfehlungstabelle

| ID | Maßnahme | Befund | Aufwand | Wann |
|---|---|---|---|---|
| V1 | Release-Pflicht in DeleteAsync/DismantleAsync/Cleanup*/RemoveExported | H1 | 2-3h | **vor Stable** |
| V6 | Quantity-Cap im Snapshot-Restore | H4 | 30min | **vor Stable** (beta.10) |
| Doku | Lifecycle-Block ehrlich machen | - | 5min | **vor Stable** |
| V3 | IsEffectivelyComplete konsequent (12 Stellen) | H3 | 1h | v0.1.25 opportunistisch |
| V4 | BlReservationService extrahieren | H7+H9 | 4-5h | v0.1.25 mit Konzept |
| V2 | UndoService erweitern (Option A) | H2 | 2-3h | v0.1.25 mit V4 |
| V7 | LIFO-Release Race-Edges haerten | LIFO-Edge | 1h | v0.1.25 |
| V5 | Reverse-Match released BL-Ueberschuesse | H5 | 1h | **vor Phase 4** |

**Stable-Block-Frage:** Soll H1 vor v0.1.24-Stable behoben werden? **Empfehlung: ja** — sonst waechst die Daten-Inkonsistenz monoton und Phase 4 (Export) macht es schwerer aufzuraeumen.

---

## 6. Code-Pfade fuer Folge-Iterationen

- `HBSort.Core/Models/TrackedMinifigPart.cs:80-88` — `EffectiveCollected`/`IsEffectivelyComplete`/`EffectivelyMissing`-Properties (V3-Anker)
- `HBSort.Core/Services/BlInventoryService.cs:62-124` — Sync-Replace (V6, Doku-Fix), Z.174-221 Reserve/Release-Atomik (gut geloest)
- `HBSort.Core/Services/MinifigPersistenceService.cs` — Z.98 (DeleteAsync), Z.163 (DismantleAsync), Z.398 (CleanupOldDismantled), Z.415 (CleanupOnePart), Z.434 (RemoveExportedMinifigs) (V1-Anker, 5 Pfade); Z.666-731 Reverse-Match (V5-Anker)
- `HBSort.Core/Services/UndoService.cs:90-98, 140-148` (V2-Anker)
- `HBSort/ViewModels/MinifigSummaryViewModel.cs` — Z.317-407 `ApplyBlReservationAsync` (V4+V7-Anker), Z.423-541 `ReleaseAllBlReservationsAsync` (V4-Anker)
- `HBSort/Views/MinifigSummaryDialog.xaml.cs:114-174` (H7, V4-Anker)
- `HBSort.Core/Services/PartLookupService.cs` — Z.124-138 (V3-Anker), Z.563-565, 782 (V3-Anker)
- `HBSort.Core/Services/StorageBinService.cs:207-273` — Suggest-Hotfix beta.9 (gut geloest), Z.880-995 RecalcKind Reife-Check (V3-Anker)
- `HBSort.Core/Models/ScanEvent.cs:77-99` (V2-Anker)
- `HBSort.Core/Services/UndoSnapshots.cs:92-102` `UndoSnapshotBlReservation` (V2-Anker)
- `HBSort/ViewModels/BuildSuggestionsViewModel.cs:281-349` `BuildBlCompletableAsync` (sauber, kein Befund)

---

## 7. Begrenzungen dieses Audits

- **Phase 4 (Export) Code-Pfade** sind noch nicht geliefert. Befunde H1 + H5 sind als "vor Phase 4 fixen" bewertet, ohne dass der Phase-4-Code existiert.
- **Race-Conditions im `InventoryChanged`-Bus** (paralleles Sync+Reserve): nicht stress-getestet. Theoretisch via EF/SQLite-Serialisierung OK.
- **Konsumenten von `PersistMinifigResult.CompletedRequiredParts`** (H6): Challenger-Grep zeigt 0 UI-Konsumenten — Wert ist tot.
- **LIFO-Release-Edge-Cases** (V7): zwei theoretische Race-Pfade identifiziert (Lot-weg, Race zwischen Release und Save). In der Praxis selten, aber stuck-state moeglich.

---

## 8. Audit-Methode-Selbstreflexion

**Auditor:** Drei Klassifikations-Fehler im ersten Durchlauf (H2 zu hoch, H5 ohne Trigger-Check, H6 mit theoretischem Schaden-Schein). Inline-Stellen-Zaehlung (H3) war 6 statt 12 — unterschaetzt um Faktor 2.

**Challenger:** Hat alle 9 Befunde nachgepruef, 3 Klassifikationen revidiert, 1 verschaerft. Praxis-Trace fuer kritische Befunde (H1, H5) hat Schwere-Bewertung praezisiert.

**Konsens-Ergebnis:** 1 kritischer Stable-Blocker (H1), 4 mittel-eingestufte strukturelle Punkte (H2, H3, H7, H9), 3 niedrige Robustheits-Hotspots (H4, H5, H8), 1 toter Befund (H6). Kein verworfenes/falsch-positives Finding.

---

*Audit erstellt von hbsort-auditor (Phase A/B/C) und hbsort-challenger (Verifikation), 2026-05-26.*
*Vorgaenger-Audit: `docs/komplexitaets-audit-2026-05-17.md`.*
*Naechster Audit-Trigger: nach Phase 4 (Export) oder v0.1.25.*
