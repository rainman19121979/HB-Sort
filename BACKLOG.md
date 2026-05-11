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
- 📋 **ScanViewModel : IDisposable** - aus Cleanup-Bericht Memory-Lifecycle.
  Aufwand: ~30min

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
- 📋 **Rate-Limit-Throttle (5 RPS)** in BrickognizeService
  Sicherheit gegen 429-Errors bei Bulk-Scans.
  Aufwand: ~30min
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

---

## Externe Abhängigkeiten

### ✅ Brickognize-ToS (geklärt)
- ✅ **Brickognize-Lizenz/ToS-Klärung** - Mail an Piotr verschickt 2026-05-07
- ✅ **Antwort von Piotr** - 2026-05-09: Nutzung erlaubt, 5 RPS Limit, Attribution optional

---

## Erledigt ✅

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
| **v0.1.20-beta.2** | 🟡 in-arbeit (2026-05-11) | UX X.34 - First-Run-Onboarding-Dialog |
| **v0.1.20** | 📋 geplant (Stable) | UX X.34 stable nach Beta-Praxis-Test - plus optional Performance / Undo / Bulk-Bin / Wiki / Brickognize-Throttle (siehe oben) |
| **v0.2.0** | 💭 Brainstorming | BL-Inventar-Integration (eigene große Iteration) |
| v0.2.1+ | offen | weitere Features aus Backlog |

Konvention:
- Patch-Iteration (v0.x.Y) für Cleanup + kleine Features
- Minor-Iteration (v0.Y.0) für große Features oder Architektur-Änderungen

---

*Zuletzt aktualisiert: 2026-05-11 nach v0.1.20-beta.1-Vorbereitung.*
