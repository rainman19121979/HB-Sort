# HBSort Code-Check Bericht

Erstellt: 2026-05-03
Geprüfter Stand: aktueller `main` (HEAD = 27cc3df + lokale Änderungen aus dieser Session)

## Zusammenfassung

| Bereich | Status |
|---|---|
| Build (Debug) | ✅ 0 Errors, 0 Warnings |
| Build (Release) | ✅ 0 Errors, 0 Warnings |
| Tests | ✅ 115/115 bestanden (1.5s) |
| EF Migrations | ✅ Konsistent — keine pending Model-Changes |
| HBSort.Core ohne WPF-Referenzen | ✅ sauber |
| Kritische Probleme (🔴) | 0 |
| Wichtige Hinweise (🟡) | 6 |
| Nice-to-have (🟢) | 5 |

**Gesamteindruck:** Sehr ordentlicher Stand. Code baut warnungsfrei in beiden Konfigurationen, alle Tests grün, keine offensichtlichen Architektur-Verletzungen, keine Token-Leaks im Logging. Die 🟡-Punkte sind Pflege-Themen, nicht akute Bugs.

---

## 🔴 Kritisch (sollte gefixt werden)

**Keine.** Build clean, Tests grün, keine offensichtlichen Bugs gefunden.

---

## 🟡 Wichtig (sollte angeschaut werden)

### 1. `catch { }` ohne jegliche Reaktion in BinDetailViewModel
**Datei:** `HBSort/ViewModels/BinDetailViewModel.cs:118` und `:131`

```csharp
catch { }
```

Bild-Lade-Fehler werden komplett verschluckt — kein Log, kein Kommentar warum. Andere catch-Blöcke im Projekt verwenden mindestens `/* best-effort */` oder `/* ignore */`. Wenn das Image-Loading dauerhaft scheitert (z.B. PartImageProvider wirft Exception), merkt das niemand.

**Vorschlag:** `catch (Exception ex) { Log.Debug(ex, "Image-Load fuer Bin-Detail fehlgeschlagen"); }`

### 2. ServiceProvider wird beim Shutdown nicht disposed
**Datei:** `HBSort/App.xaml.cs:516-531` (`OnExit`)

Nur `cameraService?.Dispose()` wird explizit aufgerufen. `BlCacheRepository` und `PersistentImageCache` sind ebenfalls IDisposable und Singletons — die SQLite-Connections bleiben offen bis der OS-Prozess endet.

**Vorschlag:** `(Services as IDisposable)?.Dispose();` vor dem CameraService-Dispose. Der DI-Container disposed dann automatisch alle Singleton-IDisposable-Services in der richtigen Reihenfolge.

### 3. `await Task.CompletedTask`-Antipattern in ClearAllAsync
**Datei:** `HBSort.Core/Services/PersistentImageCache.cs:346`

```csharp
StatsChanged?.Invoke(this, EventArgs.Empty);
// Async-Signature beibehalten, obwohl wir hier nichts asynchrones tun.
await Task.CompletedTask;
return deleted;
```

Funktioniert, ist aber stilistisch falsch — wenn die Methode synchron ist, sollte sie `Task<int>` direkt zurückgeben (`return Task.FromResult(deleted)`), nicht `async` sein. So spart man sich die State-Machine.

**Vorschlag:** Methode aus `async` umstellen auf `Task<int>` mit `return Task.FromResult(deleted);`. Andere Repository-Methoden (BlCacheRepository) machen es bereits genau so.

### 4. `BricklinkColorId` ist effektiv tot, wird aber überall mitgeführt
**Datei:** `HBSort.Core/Models/TrackedMinifigPart.cs:24-32` und `FloatingPart.cs:18-22`

Heute (seit PROMPT 6) liefert Brickognize direkt BL-Color-IDs in `ColorId`. Das Doppelfeld `BricklinkColorId` ist nur noch Legacy für Datenbestände vor PROMPT 6.

Das durchziehende Doppelfeld macht den Code schwerer lesbar. Die zugehörigen DB-Spalten sind via Migration `20260501024314_AddBricklinkColorId` da.

**Vorschlag (separat, Migration nötig):** Feldwerte in `ColorId` migrieren wo `ColorId == 0 && BricklinkColorId.HasValue`, dann das Feld + DB-Spalte in einer Migration entfernen. Nicht eilig, aber technische Schuld.

### 5. PROMPT-Nummer-Kommentare im ganzen Code (~30 Stellen)
**Dateien:** quer durch HBSort/ und HBSort.Core/, siehe Zählung im vorigen Aufräum-Durchgang

`// PROMPT 6 (...)`, `// PROMPT 11: ...` etc. erklären, in welchem User-Prompt eine Änderung kam. Das ist git-log-Information die im Code rumliegt und mit der Zeit immer kryptischer wird (User wird nach 100 PROMPTs nicht mehr wissen was PROMPT 11 war).

**Vorschlag:** Bei nächster Berührung dieser Stellen: Nummer raus, Kommentar inhaltlich umformulieren falls noch sinnvoll. Nicht in einem Rutsch — sondern beim nächsten Anfassen.

### 6. Fehlende Tests für 9 Services
**Pfad:** `HBSort.Tests/`

Tests existieren für 11 von 21 Services. Fehlend:
- `BlBulkImportService` (großer GitHub-Download + XML-Parser)
- `BricklinkApiPriceProvider` (komplexer Cache + Stale-Fallback)
- `BsxExportService` (XML-Generierung — gut testbar mit Snapshot-Tests)
- `MinifigPersistenceService` (kompliziertester Service der App, EF-Core)
- `PartLookupService`
- `PriceCalculationService` (Preis-Aggregation, Korrekturfaktoren — sehr testbar)
- `SettingsService` (JSON-Roundtrip + corrupt-file-Verhalten)
- `StorageBinService` (CRUD + Reverse-Match-Logik)
- `CameraService` (Hardware, OK)
- `DummyPriceProvider` (trivial, OK)

**Vorschlag:** Mindestens `PriceCalculationService`, `BsxExportService` und `BinNameGenerator`-Style-Test für `StorageBinService` ergänzen — das sind reine Logik-Services ohne IO-Komplexität.

---

## 🟢 Nice-to-have (optional)

### 7. `.Result` nach `await Task.WhenAll(...)`
**Dateien:** `HBSort.Core/Services/PriceCalculationService.cs:79-80`, `HBSort/ViewModels/ScanViewModel.cs:735-737`

Funktioniert (Tasks sind nach `WhenAll` garantiert fertig), aber stilistisch wäre `await detailsTask` lesbarer als `detailsTask.Result`. Kein Bug, kein Deadlock-Risiko.

### 8. Fehlendes ConfigureAwait(false)
**Treffer:** 0 in der ganzen Codebase

In WPF ist das tatsächlich meist unnötig (Sync-Context wird oft gebraucht), aber in `HBSort.Core` (UI-frei) wäre `ConfigureAwait(false)` korrekter — schadet aber nicht.

**Vorschlag:** Ignorieren oder gezielt in Hot-Pfaden ergänzen. Kein Handlungsdruck.

### 9. 28× `App.Services.GetRequiredService<T>()` als Service-Locator
**Dateien:** 7 Code-Behind-Files (SettingsWindow, MainWindow, SortingView, BinOverviewView etc.)

In WPF-Code-Behind ist Service-Locator pragmatisch akzeptiert, weil Konstruktor-DI bei Windows/UserControls schwer ist. Das Muster ist konsistent angewendet.

**Vorschlag:** Lassen — Refactoring auf "VM injiziert Service und Click-Handler ruft VM-Method" wäre sauberer aber großer Aufwand für wenig Gewinn.

### 10. `BL-Tokens werden in PasswordBox als Klartext-TextBox angezeigt`
**Datei:** `HBSort/Views/SettingsWindow.xaml:490-507`

Die 4 Token-Felder sind `TextBox`, nicht `PasswordBox`. Im Tab steht ein Hinweis dazu ("Tokens sind als reiner Text sichtbar"). Bewusste Entscheidung, aber Shoulder-Surfing-Risiko bei Live-Demos/Screen-Sharing.

**Vorschlag:** Eventuell `PasswordBox` mit Auge-Toggle (so wie es CLAUDE.md ursprünglich geplant hatte). Niedrige Priorität.

### 11. CLAUDE.md erwähnt "BSX-Export-Sektion im Statistik-Tab"
**Datei:** `CLAUDE.md` Phase 7 (~Zeile 850)

Phase-7-Notiz sagt "im Statistik-Tab gibt es eine BSX-Export-Sektion" — wir haben die aber gerade in einen eigenen Tab "Export" verschoben. Doppelte Aussage zur neuen Tab-Lösung weiter unten in derselben Phase.

**Vorschlag:** Den älteren Satz löschen oder umformulieren.

---

## ✅ Was gut läuft

- **Build & Tests**: 0 Warnings in beiden Konfigurationen, alle 115 Tests grün
- **Architektur-Trennung**: HBSort.Core hat keine einzige `System.Windows.*`/`PresentationFramework`-Referenz. Sauber UI-frei (außer im AppSettings-Klassennamen `WindowState`, der ein eigenes POCO ist und nichts mit `System.Windows.WindowState` zu tun hat)
- **DI durchgezogen**: Keine `new BricklinkClient()`-Aufrufe oder Ähnliches im Hauptprojekt — alle Services kommen über den Container
- **EF-Migrations konsistent**: `dotnet ef migrations has-pending-model-changes` meldet "No changes" — Model und Migrations sind synchron
- **Tokens sicher behandelt**: 0 Treffer für Log-Statements die `tokens.ConsumerKey/Secret/...` enthalten — DPAPI-Tokens landen nirgends im Log
- **bl_cache.db Schema**: Stimmt mit CLAUDE.md überein (alle dort beschriebenen Tabellen sind in `BlCacheSchema.sql` vorhanden — bl_items, bl_subsets, bl_colors, bl_known_colors, api_call_log, bl_prices)
- **Async sauber durchgezogen**: Alle 30 `async void`-Methoden sind legitime Event-Handler (XAML-Click, Application_Startup) — kein einziger im Service-Layer
- **`throw ex;` (Stack-Trace-Killer)**: 0 Treffer
- **Image-Loading**: `BitmapCacheOption.OnLoad` + `Freeze()` korrekt verwendet (`ScanViewModel.cs:1271`)
- **Auskommentierter Code-Müll**: 0 Treffer (sehr saubere Codebase)
- **Magic Numbers bei kritischen Settings**: Alle relevanten Werte (Timeouts, Schwellen, Cache-Dauer) sind in Settings oder als `private static readonly TimeSpan` Konstanten definiert
- **Settings-Robustheit**: `SettingsService.LoadAsync` fängt Exceptions und fällt auf Defaults zurück — corrupt settings.json bricht die App nicht

---

## Empfehlung Reihenfolge

Wenn du was angehen willst, in dieser Reihenfolge — von "schnellste Wirkung" zu "gröbster Aufwand":

1. **🟡 #1** (BinDetailViewModel `catch {}`) — 2 Zeilen, beseitigt stilles Schweigen bei Image-Lade-Fehlern.
2. **🟡 #2** (ServiceProvider-Dispose im OnExit) — 1 Zeile, sauberes Shutdown.
3. **🟡 #3** (`await Task.CompletedTask`-Antipattern) — 4 Zeilen, korrigiert Async-Stil-Bruch.
4. **🟢 #11** (CLAUDE.md doppelte BSX-Statistik-Aussage) — 1 Satz, hält Doku konsistent.
5. **🟡 #6** (mindestens 2-3 fehlende Service-Tests ergänzen) — größerer Block, bringt Robustheit.
6. **🟡 #5** (PROMPT-Kommentare schrittweise abräumen) — laufend bei Berührung.
7. **🟡 #4** (BricklinkColorId-Doppelfeld migrieren) — größtes Stück, braucht Migration. Nicht eilig.
8. **🟢 #7-10** — kannst du komplett ignorieren bis dich was stört.

Sag Bescheid, was wir angehen — oder ob du einzelne Punkte erst diskutieren willst.
