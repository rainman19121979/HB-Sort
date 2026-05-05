# HB-Sort (Klemmbaustein-Sortier-Werkzeug)

## Quick Reference (für Claude Code)

### Häufige Kommandos

```powershell
# Build (gesamte Solution)
dotnet build

# Run (WPF-Hauptprojekt)
dotnet run --project HBSort

# Tests
dotnet test                                           # alle Tests
dotnet test --filter "FullyQualifiedName~BlCache"     # einzelne Test-Klasse
dotnet test --filter "DisplayName=TestMethodName"     # einzelner Test

# EF Core Migrationen (Startup-Projekt = HBSort, DbContext liegt in HBSort.Core)
dotnet ef migrations add <Name> --project HBSort.Core --startup-project HBSort
dotnet ef database update --project HBSort.Core --startup-project HBSort

# Release-Build (Single-File-Exe, self-contained)
dotnet publish HBSort -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

### Projekt-Struktur (3 Projekte in HBSort.slnx)

- **HBSort/** — WPF-Hauptprojekt (Views, ViewModels, App.xaml.cs mit DI-Container).
  Einstiegspunkt: `App.xaml.cs::Application_Startup`. Services werden dort in
  `ConfigureServices()` registriert — neue Services müssen dort eingetragen werden.
- **HBSort.Core/** — Reine Logik (Models, Services, Database). UI-frei, aber
  TargetFramework `net8.0-windows` wegen OpenCvSharp + DPAPI. Alle Services
  haben ein `IXxx.cs`-Interface daneben (Pflicht für Testability).
- **HBSort.Tests/** — xUnit-Tests gegen Core-Services.

### Zwei Datenbanken, zwei Zugriffsarten

- `userdata.db` — User-Daten (TrackedMinifig, StorageBin, FloatingPart, …) via
  **EF Core** (`UserDataContext`). Schema-Änderungen brauchen Migrations.
- `bl_cache.db` — BL-Stammdaten + Bulk-Import + Rate-Limiter-Log via
  **Microsoft.Data.Sqlite** (raw ADO.NET). Schema in
  `HBSort.Core/Database/BlCacheSchema.sql` (Embedded Resource, beim ersten
  Start ausgeführt). `CREATE TABLE IF NOT EXISTS`-Style — einfache Erweiterungen
  brauchen keine separate Migration.

### AppData-Ort (zum Zurücksetzen / Inspizieren)

```
%APPDATA%\HBSort\
  userdata.db          ← EF, kann gelöscht werden (App legt neu an)
  bl_cache.db          ← BL-Cache (löschen erzwingt Bulk-Reimport)
  settings.json        ← inkl. DPAPI-verschlüsselter BL-Tokens
  logs\app-*.log       ← Serilog, 30 Tage rolling
```

Beim App-Start läuft eine Auto-Migration vom alten Pfad
`%APPDATA%\LegoMinifigSorter\` (siehe `App.xaml.cs::MigrateLegacyAppDataIfNeeded`).

### Konventionen die sich nicht aus dem Code ergeben

- **Service-Pattern**: Jeder neue Service braucht ein `IXxx.cs`-Interface
  daneben + Registrierung in `App.xaml.cs::ConfigureServices`. Default ist
  `AddSingleton`; ViewModels für Dialoge sind `AddTransient`.
- **Persistence-Änderungen**: Methoden in `IMinifigPersistenceService` /
  `IStorageBinService` müssen `DataChanged` feuern, damit die Live-VMs
  (BuildSuggestions, LiveStats, WaitingDetail, RecentScans) refreshen.
- **Async-Pflicht**: Alle DB- und API-Calls sind `async`. UI darf nie blockieren
  (sonst friert die WPF-Pipeline ein).
- **Logs sind tabu für Tokens**: Klartext-BL-Tokens dürfen nie in Logs landen,
  auch nicht auf Debug-Level.
- **Dialoge**: Statt `MessageBox.Show` immer `IDialogService` (siehe
  `HBSort.Services`). `ShowQuestionAsync` für destruktive Aktionen
  ("Ja"/"Nein"), `ShowConfirmAsync` für nicht-destruktive ("OK"/"Abbrechen"),
  `ShowInfoAsync` / `ShowErrorAsync` für reine Hinweise. Aufrufe sind alle
  async — Click-Handler ggf. auf `async void` umstellen statt `.Wait()`.
- **Klickbare Bilder**: Auf jedem `<Image>` mit BL-/Cache-Bild
  `b:ImageZoom.IsEnabled="True"` setzen (Namespace
  `xmlns:b="clr-namespace:HBSort.Behaviors"`) → öffnet Modal-Overlay im
  MainWindow.
- **Primäre Aktions-Buttons**: bekommen `Style="{StaticResource AccentButtonStyle}"`
  (z.B. "Speichern", "Anlegen", "Exportieren", "Uebernehmen"). Sekundäre/
  abbrechende Buttons bekommen keinen Style.
- **Destruktive Buttons** ("Loeschen", "Fach freigeben mit Inhalt"): 
  aktuell hardcoded `Background="#D32F2F" Foreground="White"` an einzelnen
  Stellen (z.B. `MinifigSummaryDialog.xaml:189`). Ein dedizierter
  `DangerButtonStyle` ist noch nicht definiert — TODO bei nächster UI-
  Iteration. Bis dahin: bei neuen destruktiven Buttons auf das gleiche
  Hex-Pärchen zeigen, damit ein späterer Style-Switch trivial wird.
- **Dialog-Footer-Buttons**: 
  - **"Abbrechen"** für Editier-Dialoge die Eingaben verwerfen können
    (Bin anlegen, Dismantle-Wizard, Export, Settings, Detail-Eingaben).
    Mit `IsCancel="True"`. Der primäre "OK"/Aktion-Button bekommt
    `IsDefault="True"` + `Style="{StaticResource AccentButtonStyle}"`.
  - **"Schliessen"** für reine Read-Only-/Info-Dialoge ohne Eingabe-
    Verwerfen-Effekt (BinDetail, MinifigSummary, Supersets). Nur
    `IsCancel="True"` setzen — `IsDefault` zusätzlich am selben Button
    ist widersprüchlich (ESC und ENTER lösen denselben Button aus, das
    ist Doppelbelegung). Wenn der Dialog mehrere Aktions-Buttons hat,
    ist "Schliessen" der Cancel-Anker und ein anderer Button (z.B.
    "Verschieben" im MinifigSummary) bekommt das `IsDefault="True"`.
  - Reihenfolge im Footer: links Cancel/Schliessen, rechts primärer
    Button.

---

## Migrations-Notiz PROMPT 6 (2026-05-02)

Rebrickable komplett entfernt. catalog.db, ICatalogService,
CatalogService, ICatalogImporter, CatalogImporter, IMinifigLookupService,
MinifigLookupService und SplashWindow geloescht. seed-data/*.zip und
HBSort.Core/Resources/CatalogSeed/*.zip raus (~16.6 MB Code-Diet).

Stammdaten kommen jetzt ausschliesslich aus `bl_cache.db`
(BrickStore-Bulk-Import). Der ColorMatch-Pfad nutzt `bl_colors`
direkt; Brickognize-Color-id wird als BL-Color-ID interpretiert
(validiert 2026-05-02). `ColorMapping.cs` (alte RB↔BL-Tabelle)
wurde am 2026-05-04 entfernt — bei Bedarf via Git wiederherstellbar.

## Migrations-Notiz (2026-05-02)

Software umbenannt von "LegoMinifigSorter" auf "HBSort" (Code) bzw.
"HB-Sort" (UI). Grund: LEGO-Markenrechte.

- Top-Level-Folder: `C:\Projekte\LegoMinifigSorter` -> `C:\Projekte\HBSort`
- AppData: `%APPDATA%\LegoMinifigSorter` -> `%APPDATA%\HBSort`
- Auto-Migration in `App.xaml.cs::MigrateLegacyAppDataIfNeeded()`
  kopiert beim ersten Start den alten Datenbestand in den neuen Ordner.
- Backup-Tag: `pre-rename-backup-2026-05-02`.

LEGO ist eine eingetragene Marke der LEGO Gruppe; diese Software hat
keine offizielle Verbindung zur LEGO Gruppe.

## Projektzweck

Eine Windows-Desktop-Anwendung, die mir hilft, lose LEGO-Minifiguren-
Einzelteile zu identifizieren, den passenden Figuren zuzuordnen und in
temporären Lagerfächern zu sammeln, bis eine Figur komplett ist.
Anschließend soll der aktuelle BrickLink-Preis der Figur angezeigt werden,
damit ich entscheiden kann, ob ich sie als komplette Figur verkaufe oder
die Einzelteile separat anbiete.

Die Erkennung erfolgt per USB-Webcam und der Brickognize-API. Die
**Stammdaten kommen direkt von der BrickLink-API** (Catalog-Items, Subsets,
Colors). Damit ist die App durchgaengig auf BL-IDs aufgebaut, was
spaeter den Verkauf auf BL massiv vereinfacht.

## Sprache & Konventionen

- **Antworten an mich (User): immer auf Deutsch.**
- **Code-Kommentare: auf Deutsch.**
- **Variablen-/Methoden-/Klassen-Namen: auf Englisch** (Standard für C#).
- **UI-Texte:** komplett **auf Deutsch** (Tooltips, Buttons, Meldungen,
  Fehlertexte). Hardcodiert in der ersten Version – keine i18n-Resource-
  Datei, das ist Overkill.
- Ich bin **Anfänger in Programmierung**, also bitte:
  - Viele erklärende Kommentare im Code
  - Keine zu cleveren Einzeiler – lieber lesbar als kurz
  - Wenn du eine Architektur-Entscheidung triffst, kurz im Code-Kommentar
    oder Chat erklären warum
  - Wenn du eine Bibliothek einsetzt, die ich nicht kenne, einmal in
    2 Sätzen erklären was sie macht

## Tech-Stack

| Bereich | Wahl | Begründung |
|---|---|---|
| Sprache | C# 12 / .NET 8 (LTS) | Modern, Windows-nativ, exzellente IDE-Unterstützung |
| GUI-Framework | WPF | Reife, beste Doku, visueller Designer in Visual Studio |
| IDE | Visual Studio Community 2026 | Microsofts Standard für C# |
| Architekturmuster | MVVM (Model-View-ViewModel) | WPF-Standard, sauberes Trennen von UI und Logik |
| MVVM-Helfer | CommunityToolkit.Mvvm | Microsoft-Bibliothek, reduziert Boilerplate |
| Theme-Support | ModernWpfUI / System-Theme | Folgt Windows-System-Theme (Hell/Dunkel) |
| Webcam | OpenCvSharp4 + OpenCvSharp4.runtime.win | Robuster Frame-Zugriff, generischer USB-Cam-Support |
| User-Datenbank | SQLite via Entity Framework Core 8 | Eine Datei, kein Server, EF macht SQL überflüssig |
| BL-Cache-Datenbank | SQLite via Microsoft.Data.Sqlite (raw ADO.NET) | Cache fuer BL-API-Antworten, schnelle Lookups |
| BL-API-Client | **BricklinkSharp** (NuGet-Paket) | Etablierte C#-Library, OAuth1, alle Endpoints |
| ZIP-Handling | System.IO.Compression (eingebaut) | Falls noch fuer alte Catalog-Imports |
| HTTP-Client | HttpClient (eingebaut) | Standard in .NET |
| JSON | System.Text.Json (eingebaut) | Schnell, ohne Extra-Dependency |
| XML (BSX-Export) | System.Xml.Linq (eingebaut) | LINQ to XML für saubere BSX-Generierung |
| Logging | Serilog mit Sinks: Console + File | Strukturiertes Logging, einfache Konfiguration |
| Tray-Icon | H.NotifyIcon.Wpf | Modernste WPF-Tray-Lib mit MVVM-Support |
| Toasts/Notifications | eigene Implementation | Fuer Toast-Meldungen unten rechts |
| DPAPI | System.Security.Cryptography.ProtectedData | Verschluesselung der BL-Tokens (nur fuer aktuellen User entschluesselbar) |
| Build | `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` | Eine .exe, ohne .NET-Installation lauffähig |
| Versionierung | Semantic Versioning (Major.Minor.Patch) | Industriestandard |

## Datenbanken

```
%APPDATA%\HBSort\
├── userdata.db         ← Lagerfaecher, wartende Figuren, Scan-Historie
├── userdata.db.bak     ← Backup beim App-Start
├── bl_cache.db         ← BL-Stammdaten + Subsets + Colors (BrickStore-Bulk-Import + BL-API)
├── settings.json       ← Kameraindex, Schwellwerte, BL-Tokens (verschluesselt)
├── images\             ← Gecachte Bilder (BL-First, 30-Tage-Refresh)
├── scans\              ← Letzte 100 Scan-Bilder (rotiert)
└── logs\               ← Tageslogs (app-YYYY-MM-DD.log, 30 Tage)
```

PROMPT 6 (2026-05-02): `catalog.db` (Rebrickable) entfaellt komplett.
Beim ersten Start nach dem Update kann die alte Datei manuell geloescht
werden:
```
del "%APPDATA%\HBSort\catalog.db"
del "%APPDATA%\HBSort\catalog.db.bak"
```

**Datenfluss:**
- `userdata.db`: Verwaltet von EF Core (User-Daten)
- `bl_cache.db`: Direktzugriff via Microsoft.Data.Sqlite, enthaelt sowohl
  BL-API-Cache als auch den Bulk-Import aus BrickStore

**Backup-Strategie:**
- `userdata.db.bak`: Wird beim **App-Start** angelegt (überschreibt sich,
  also genau ein Backup vom vorigen Start)

## Externe APIs

### Brickognize (Erkennung)
- Base URL: `https://api.brickognize.com`
- Authentifizierung: keine, kostenlos
- Methode: POST, Multipart-Form (Feldname: `query_image`)
- **Vier separate Endpoints**:
  - `POST /predict/` – generisch (Fallback)
  - `POST /predict/parts/?predict_color=true` – nur Teile, mit Farberkennung
  - `POST /predict/figs/` – nur Minifiguren
  - `POST /predict/sets/` – nur Sets (in diesem Projekt nicht genutzt)
- Health-Check: `GET /health/`
- Vollständige Doku: siehe **`BRICKOGNIZE_API.md`** im Repo

### BrickLink Store API (Stammdaten - HAUPT-DATENQUELLE)

Die App nutzt die offizielle BL-API als primaere Datenquelle fuer alle
Catalog-Daten (Items, Subsets, Farben). Damit sind alle internen IDs
**BL-IDs** – konsistent mit dem spaeteren Verkauf.

#### Authentifizierung (OAuth1)

BL-API nutzt OAuth1 mit **vier Tokens**:
- `ConsumerKey` + `ConsumerSecret` (App-spezifisch)
- `TokenValue` + `TokenSecret` (User-spezifisch)
- IP-Whitelist erforderlich (User pflegt seine externe IP im
  BL-Consumer-Profile)

Die User registriert die Consumer-Application unter
https://www.bricklink.com/v2/api/register_consumer.page einmalig.

**Speicherung der Tokens:**
- In `settings.json` unter `bricklink.tokensEncrypted` (Base64)
- Verschluesselt per **Windows DPAPI** (ProtectedData mit DataProtectionScope.CurrentUser)
- Auch wenn jemand die settings.json kopiert, kann er die Tokens nicht
  entschluesseln (gebunden an Windows-User)
- In Settings-UI: PasswordBox-Felder mit Auge-Klick-Anzeige

#### Wichtige Endpoints (via BricklinkSharp)

```csharp
// Catalog-Item-Details
GetItemAsync(ItemType.Minifig, "arc007")
  -> name, year_released, image_url, weight, dimensions, ...

// Subsets (Teile einer Minifig oder eines Sets)
GetSubsetsAsync(ItemType.Minifig, "arc007", breakMinifigs: false)
  -> Liste der Teile mit color_id + quantity (Match Group fuer Alt.-Teile)

// Bekannte Farben fuer ein Teil (Phase 5+)
GetKnownColorsAsync(ItemType.Part, "3001")
  -> Liste der Farb-IDs in denen das Teil existiert

// Bilder
GetItemImageAsync(ItemType.Minifig, "arc007", colorId: 0)
GetPartImageForColor("3001", 11)
  -> URL zu img.bricklink.com/...
```

#### Rate Limit (5000 Calls/Tag) - Eigenes Tracking + Limits

BrickLink misst das Limit als **rolling 24h-Window** (kein 00:00-Reset!).
Wir tracken die API-Calls eigenstaendig in einer SQLite-Tabelle und 
setzen **eigene, konservativere Schwellen** als BL, damit wir nie ins 
echte Limit laufen.

**Standardwerte (in Settings konfigurierbar):**

| Wert | Default | Beschreibung |
|---|---|---|
| `bricklink.softThreshold` | **1000** | Erste Warnung: gelber Toast bei diesem Stand (Rolling 24h) |
| `bricklink.hardThreshold` | **4500** | Hard-Stop: keine API-Calls mehr, nur Cache (90% von BL-Limit, sicherer Abstand) |
| `bricklink.blRealLimit` | 5000 | Nur zur Anzeige, BL's echtes Limit (nicht editierbar) |

**Tracking-Tabelle in `bl_cache.db`:**

```sql
CREATE TABLE api_call_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,        -- ISO-Datum/Zeit (UTC)
    method TEXT NOT NULL,           -- 'GetItem', 'GetSubsets', etc.
    item_type TEXT,                 -- M, P, S oder NULL
    item_no TEXT,                   -- z.B. 'arc007'
    response_time_ms INTEGER,
    status_code INTEGER,            -- 200, 404, 429, etc.
    success INTEGER NOT NULL        -- 1 oder 0
);
CREATE INDEX idx_api_call_log_timestamp ON api_call_log(timestamp);
```

**Tracking-Logik:**

```csharp
public interface IBricklinkRateLimiter
{
    /// <summary>Counter fuer Rolling-24h und Heute (ab 00:00 lokale Zeit).</summary>
    Task<RateLimitStatus> GetStatusAsync();
    
    /// <summary>Pruefen ob ein Call gemacht werden DARF (vor dem API-Call!).</summary>
    Task<bool> CanMakeCallAsync();
    
    /// <summary>Logging eines durchgefuehrten Calls (nach dem API-Call).</summary>
    Task LogCallAsync(string method, string? itemType, string? itemNo, 
        int responseTimeMs, int statusCode, bool success);
    
    /// <summary>Aelter als 7 Tage loeschen (Wartung beim App-Start).</summary>
    Task PruneOldEntriesAsync();
}

public record RateLimitStatus(
    int CallsLast24h,        // Rolling
    int CallsToday,          // Seit 00:00 lokale Zeit
    int CallsThisHour,       // Letzte 60 Min
    int SoftThreshold,
    int HardThreshold,
    int BlRealLimit,
    RateLimitState State,    // Ok / Warning / Blocked
    DateTime? OldestCallIn24h);  // damit User sieht wann das Window resettet

public enum RateLimitState
{
    Ok,         // < SoftThreshold
    Warning,    // SoftThreshold <= x < HardThreshold (gelb)
    Critical,   // HardThreshold <= x < BlRealLimit (orange, kurz vor Hard-Stop)
    Blocked     // >= HardThreshold (rot, keine API-Calls mehr)
}
```

**BlCatalogService verwendet Rate-Limiter:**

Vor jedem BL-API-Call:
1. `rateLimiter.CanMakeCallAsync()` -> false?
   - **Bei Cache-Miss**: Fehler-Strategy, return Cache-Hit (auch stale) wenn 
     vorhanden, sonst null + Toast "BL-Limit erreicht"
   - **Bei Cache-Hit**: Cache reicht eh -> kein Problem
2. Nach Call: `rateLimiter.LogCallAsync(...)`

**Toast-Notifications:**

- Bei Erreichen Soft-Threshold (z.B. 1000): EINMAL pro 24h Toast 
  "BL-API: 1000 Calls in den letzten 24h erreicht (Soft-Limit)"
- Bei Erreichen Hard-Threshold (z.B. 4500): Toast 
  "BL-API: Hard-Limit erreicht, nur noch Cache-Lookups bis das 
  Rolling-Window resettet"
- Bei tatsaechlichem 429 von BL (sollte nicht passieren wenn unsere 
  Werte stimmen): Roter Toast + Logging als Bug

**Status-Bar-Anzeige:**

Im Hauptfenster Status-Bar (kompakt):
```
BL: 47/1000 (24h)  ← Ok (gruen)
BL: 1024/4500 (24h)  ← Warning (gelb)
BL: 4502/4500 (24h) ⛔  ← Blocked (rot)
```

**Settings-Tab "BrickLink-API" Erweiterung:**

```
+-------------------------------------------------------+
| API-Nutzung                                           |
|                                                       |
| Letzte 24 Stunden (Rolling):  47 / 4500 Calls         |
| Heute (seit 00:00):           23 Calls                |
| Letzte Stunde:                3 Calls                 |
|                                                       |
| Status: Ok ✓                                          |
| Aelteste Call im 24h-Window:  vor 18 Std (resettet 6h)|
|                                                       |
| Schwellwerte:                                         |
| Soft-Warnung bei: [____1000] Calls in 24h             |
| Hard-Stop bei:    [____4500] Calls in 24h             |
| BL-Limit (fix):    5000 Calls in 24h                  |
|                                                       |
| [Schwellwerte speichern]                              |
|                                                       |
| Verlauf:                                              |
| [Mini-Diagramm: Calls pro Stunde der letzten 24h]     |
+-------------------------------------------------------+
```

**Auto-Wartung:**

Beim App-Start: `PruneOldEntriesAsync()` loescht alle Eintraege 
aelter als 7 Tage (so bleibt die Tabelle klein, ~max ein paar tausend 
Eintraege).

#### Caching (bl_cache.db) - UEBERGREIFENDE STRATEGIE

Wir verfolgen eine **aggressive, uebergreifende Cache-Strategie**: Bei
jedem `GetSubsets`-Aufruf werden die enthaltenen Teile auch in `bl_items`
gecached. Dadurch braucht ein spaeterer Einzelteil-Scan keinen neuen
API-Call, wenn das Teil in einer schon gescannten Minifig vorkam.

**Effekt:** Bei einer Sortier-Session mit 50 Minifigs (~250 verschiedene
Teile) sparen wir **5x API-Calls**: 100 statt 550.

```sql
CREATE TABLE bl_items (
    item_type TEXT NOT NULL,        -- 'M', 'P', 'S'
    item_no TEXT NOT NULL,          -- 'arc007', '3024', '75300-1'
    name TEXT NOT NULL,
    year_released INTEGER,
    image_url TEXT,
    weight REAL,
    dim_x REAL, dim_y REAL, dim_z REAL,
    category_id INTEGER,
    json_full TEXT,                 -- vollstaendige API-Antwort als JSON
    data_completeness TEXT NOT NULL, -- 'full' | 'subset'
    -- 'full'   = via GetItemAsync geholt (alle Felder vollstaendig)
    -- 'subset' = aus GetSubsets-Antwort extrahiert (Basis-Felder reichen)
    fetched_at TEXT NOT NULL,       -- ISO-Datum
    PRIMARY KEY (item_type, item_no)
);

CREATE TABLE bl_subsets (
    parent_type TEXT NOT NULL,      -- 'M', 'S'
    parent_no TEXT NOT NULL,        -- 'arc007', '75300-1'
    item_type TEXT NOT NULL,        -- 'P' (Teil) typisch
    item_no TEXT NOT NULL,          -- '3024'
    color_id INTEGER NOT NULL,      -- BL-Color-ID
    quantity INTEGER NOT NULL,
    extra_quantity INTEGER NOT NULL DEFAULT 0,  -- BL "ExtraQty"
    is_alternate INTEGER NOT NULL DEFAULT 0,    -- Alt.-Teile aus Match-Groups
    is_counterpart INTEGER NOT NULL DEFAULT 0,
    match_id INTEGER NOT NULL DEFAULT 0,        -- Match-Group-ID (BL)
    fetched_at TEXT NOT NULL,
    PRIMARY KEY (parent_type, parent_no, item_type, item_no, color_id, match_id)
);

CREATE TABLE bl_colors (
    color_id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    rgb TEXT,
    type TEXT,                      -- Solid, Transparent, Chrome, etc.
    fetched_at TEXT NOT NULL
);

-- Indexe fuer Reverse-Lookup (Phase 5: "Welche Figuren brauchen Teil X?")
CREATE INDEX idx_bl_subsets_parent ON bl_subsets(parent_type, parent_no);
CREATE INDEX idx_bl_subsets_item ON bl_subsets(item_type, item_no, color_id);
```

**Cache-Lebensdauer:**
- Items: 90 Tage (selten Aenderungen)
- Subsets: 90 Tage (sehr stabil)
- Colors: lebenslang (komplette Farb-Liste 1x am Anfang holen)

**Cache-Logik beim Schreiben:**

Bei `GetSubsetsAsync(M, "arc007")` -> Antwort hat z.B. 3 Teile:
1. **Subsets cachen**: 3 Eintraege in `bl_subsets` mit parent='M:arc007'
2. **Items cachen** (uebergreifend!):
   Fuer jedes Teil aus der Antwort einen Eintrag in `bl_items`:
   - PRIMARY-KEY-Konflikt? -> nur upsert wenn `data_completeness='subset'`
     (sonst wuerden wir 'full'-Daten ueberschreiben)
   - Bei neuem Eintrag: `data_completeness='subset'`
   - Felder: item_type, item_no, name (immer da), image_url (per Standard-URL
     aufbaubar), category_id wenn geliefert
3. **Colors cachen** (defensiv): Falls Antwort eine neue Color enthaelt,
   die noch nicht in `bl_colors` ist -> aufnehmen

Bei `GetItemAsync(P, "3024")` -> volle Item-Daten:
1. Eintrag in `bl_items` mit `data_completeness='full'`
2. Falls schon ein 'subset'-Eintrag existiert: ueberschreiben mit 'full'

**Cache-Logik beim Lesen:**

`GetItemDetailsAsync(itemType, itemNo)`:
1. Lookup `bl_items WHERE item_type=? AND item_no=?`
2. Falls `data_completeness='full'`: direkt zurueck
3. Falls `data_completeness='subset'`: pruefen ob Use-Case mehr braucht:
   - **In Phase R2-R3 reichen Subset-Daten** (Name, Bild, Kategorie)
   - **Phase 5+** koennte volles Item brauchen -> dann GetItem, upgrade
4. Falls nicht vorhanden ODER stale (>90 Tage): GetItemAsync aufrufen
5. Bei BL-Fehler/Offline: Cache-Eintrag (auch stale) zurueckgeben +
   im Log markieren "served-from-stale-cache"

`GetSubsetsAsync(parentType, parentNo)`:
1. Lookup `bl_subsets WHERE parent_type=? AND parent_no=?`
2. Falls Eintraege vorhanden und nicht stale: zurueck
3. Sonst: BL-API GetSubsets + uebergreifend cachen (siehe oben)

**Reverse-Lookup (Phase 5):**

"Welche wartenden Figuren brauchen Teil 3024 in BL-Color 11?"
```sql
SELECT DISTINCT s.parent_no
FROM bl_subsets s
WHERE s.item_type = 'P'
  AND s.item_no = '3024'
  AND s.color_id = 11
  AND s.parent_no IN (SELECT BricklinkId FROM TrackedMinifig WHERE Status = 'WAITING')
```

Schnell, Indexe sind gesetzt. Kein BL-Call noetig.

#### Fehler-Codes

| Code | Bedeutung | App-Reaktion |
|---|---|---|
| 200 | OK | Daten cachen + zurueckgeben |
| 401 | Auth fehlt/invalid | Settings-Hinweis "Tokens pruefen" |
| 404 | Item nicht in BL-Catalog | Eintrag als "nicht in BL" markieren |
| 429 | Rate Limit | Cache-Fallback + Toast |
| 5xx | BL-Server-Fehler | Cache-Fallback + Toast |
| Timeout | Netzwerk weg | Cache-Fallback + Toast |

### Eigenes BrickLink-Preis-Tool ("BL Price Tracker") - Phase 8+

(unveraendert, siehe vorige Spec-Version)

Das eigene Preis-Tool laeuft im lokalen Netz und liefert DE-spezifische
BrickLink-Preise mit eigener Pricing-Formel. Wird **nicht in Phase 1-7**
angebunden - das `IPriceProvider`-Interface wird vorbereitet, sodass die
Anbindung in Phase 8 ohne Refactoring moeglich ist.

[siehe Detail-Schema im vorigen CLAUDE.md-Abschnitt - bleibt unveraendert]

### GitHub Releases API (für Update-Check)
- URL-Pattern: `https://api.github.com/repos/{owner}/{repo}/releases/latest`
- Public-Repo, kein Token nötig
- Beim App-Start im Hintergrund pruefen (1x pro Tag)

## ID-Strategie (NEU mit BL-API)

**Konsistent BL-IDs ueberall:**

```
Brickognize → BL-ID (z.B. "arc007", "3024")
   ↓
BL-API GetItem(BL-ID) → Item-Details (gecached)
   ↓
BL-API GetSubsets(BL-ID) → Teile-Liste mit BL-Color-IDs (gecached)
   ↓
TrackedMinifig speichert BL-ID
   ↓
BSX-Export nutzt BL-ID direkt (keine Konvertierung noetig)
   ↓
Phase 8: BL-Price-Tool nutzt BL-ID + BL-Color-ID direkt
```

**Rebrickable-IDs (PROMPT 6, 2026-05-02):**
Werden nicht mehr aktiv genutzt. Die alte RB↔BL-Tabelle in
`ColorMapping.cs` wurde am 2026-05-04 entfernt (Audit M-1) — bei
Bedarf fuer kuenftige Reverse-Konvertierungen via Git wiederherstellbar.

URL-Patterns: siehe **`BRICKOGNIZE_API.md`**.

## User-Daten-Schema (in `userdata.db`, via EF Core)

```csharp
class TrackedMinifig
{
    int Id;
    string BricklinkId;       // BL-ID, z.B. "arc007" - PRIMAERE ID
    string? RebrickableId;    // optional, fuer Cross-Reference
    string Name;              // gecacht
    string? ImageUrl;
    string? LocalImagePath;
    string? UserNotes;
    DateTime CreatedAt;
    DateTime? CompletedAt;
    TrackedMinifigStatus Status; // WAITING, COMPLETE, DISMANTLED, SOLD
    int? StorageBinId;
    List<TrackedMinifigPart> RequiredParts;
}

class TrackedMinifigPart
{
    int Id;
    int TrackedMinifigId;
    string BricklinkPartNo;   // BL-Part-Nummer, z.B. "3024"
    int BricklinkColorId;     // BL-Color-ID
    string ColorName;         // gecacht
    string PartName;          // gecacht
    int QuantityNeeded;
    int QuantityCollected;
    bool IsAlternate;         // BL-Match-Group Alt.-Teil?
}

class StorageBin
{
    int Id;
    string Label;             // "Box 3", "Schale rot", ...
    DateTime CreatedAt;
    DateTime? FreedAt;        // null = belegt, sonst Zeitpunkt der Freigabe
}

class FloatingPart
{
    int Id;
    string BricklinkPartNo;
    int BricklinkColorId;
    string ColorName;
    string PartName;
    int Quantity;
    int StorageBinId;
    DateTime AddedAt;
}

class ScanEvent
{
    int Id;
    DateTime Timestamp;
    ScanType Type;            // MINIFIG_SCAN, PART_SCAN
    string? RecognizedId;
    double? Confidence;
    string? ImagePath;
    string ResultDescription;
    bool WasUndone;
}

class DailyStats
{
    DateTime Date;
    int ScanCount;
    int MinifigsCompletedCount;
    int MinifigsDismantledCount;
}
```

## Settings.json (Erweiterung um BL-Tokens)

```json
{
    "selectedCameraIndex": 0,
    "windowState": { "width": 1920, "height": 1080, "x": 0, "y": 0,
                     "isMaximized": true },
    "scoreThresholdAuto": 0.85,
    "scoreThresholdMin": 0.5,
    "scoreThresholdShowSelection": 0.7,
    "scanCooldownMs": 1000,
    "freezeFrameMs": 1000,
    "soundEnabled": false,
    "imageCacheRefreshDays": 30,
    "imageCache": {
        "limitMb": 1024,
        "preferBricklinkImages": true,
        "preloadOnMinifigScan": true
    },
    "bricklink": {
        "tokensEncrypted": "BASE64_DPAPI_PROTECTED_BLOB",
        "rateLimitWarningThreshold": 4500,
        "cacheStaleDays": 90
    },
    "priceTool": {
        "url": "http://10.0.0.147:3000",
        "tokenEncrypted": "BASE64_DPAPI_PROTECTED_BLOB",
        "defaultCondition": "U"
    },
    "lastUpdateCheck": "2026-04-30T08:00:00Z"
}
```

**Tokens-Format vor DPAPI-Verschluesselung:**
```json
{
    "consumerKey": "...",
    "consumerSecret": "...",
    "tokenValue": "...",
    "tokenSecret": "..."
}
```

Diese 4 Werte werden als JSON serialisiert, dann via DPAPI-Encrypt zu
einem byte[] umgewandelt, dann Base64-encoded und in `tokensEncrypted`
gespeichert.

## Workflow (Kern-Spezifikation, leicht angepasst)

### Modus A: Figur scannen

1. User legt Minifigur vor Kamera, drueckt **Leertaste**
2. Frame eingefroren, 1 Sek prominent
3. Frame → Brickognize `/predict/figs/`
4. Brickognize liefert BL-ID (z.B. "arc007")
5. Score-Auswertung wie gehabt:
   - **>= 0.85**: Auto-Akzept
   - **0.5 - 0.85**: Top-3-Karten
   - **< 0.5**: Manuell
6. **NEU:** App ruft `BlCatalogService.GetMinifigDetailsAsync("arc007")`
   - Cache-Hit: Daten aus bl_cache.db
   - Cache-Miss: BL-API-Calls (GetItem + GetSubsets) + Cache speichern
7. Anzeige Teileliste:
   - BL-Bild der Minifigur
   - Liste aus bl_subsets mit BL-Part-No, BL-Color-Name, RGB, Quantity
   - Match-Groups (Alt.-Teile) erkennbar markieren
8. Reverse-Match Floating-Parts (per BL-Part-No + BL-Color-ID)
9. Notiz-Feld + Lagerfach-Auswahl (Phase 4)
10. TrackedMinifig wird mit BL-ID als PrimaryKey gespeichert
11. Falls bereits komplett durch Reverse-Match → Komplettierungs-Workflow

### Modus B: Einzelteil scannen

1. User legt Teil vor Kamera, Leertaste
2. Frame eingefroren, 1 Sek
3. Frame → Brickognize `/predict/parts/?predict_color=true`
4. Brickognize liefert BL-Part-No + Farb-Vermutung (Rebrickable-Name/ID)
5. Brickognize liefert die Color-ID bereits als BL-Color-ID
   (validiert 2026-05-02), kein separates Mapping noetig
6. User kann Farbe via Dropdown korrigieren (BL-Color-Liste aus bl_colors)
7. **Matching-Logik (strikte BL-Color-ID):**

   **Schritt 1:** Wartende Figuren mit passendem TrackedMinifigPart
   (BricklinkPartNo + BricklinkColorId match)

   **Schritt 2:** Bei mehreren / keinem Treffer entsprechend reagieren

8. **Komplettierungs-Workflow** wie gehabt

## BSX-Export (vereinfacht durch BL-IDs)

Da wir intern bereits BL-IDs verwenden, ist der BSX-Export trivial:
- BricklinkId direkt als `<ItemID>` ins XML
- BricklinkColorId direkt als `<ColorID>` ins XML
- Keine Konvertierung mehr noetig (vorher: RB→BL via ColorMapping)

```xml
<Item>
  <ItemID>arc007</ItemID>      <!-- direkt aus TrackedMinifig.BricklinkId -->
  <ItemTypeID>M</ItemTypeID>
  <ColorID>0</ColorID>
  <Qty>1</Qty>
  <Condition>U</Condition>
  <Status>I</Status>
  <ItemName>Arctic - Green, Green Cap</ItemName>
</Item>
```

## Architektur (mit BL-API-Services)

```
HBSort.sln
├── HBSort/                  (WPF-Hauptprojekt)
│   ├── App.xaml / MainWindow.xaml
│   ├── Views/
│   │   ├── ScanView, MinifigDetailView, BinManagerView, ...
│   │   ├── SettingsView (Tab Bricklink mit Token-Eingabe + Test-Button)
│   │   └── ...
│   ├── ViewModels/
│   ├── Converters/
│   └── Resources/
│
├── HBSort.Core/             (Reine Logik)
│   ├── Models/
│   ├── Services/
│   │   ├── ICameraService / CameraService.cs
│   │   ├── IBrickognizeClient / BrickognizeClient.cs
│   │   ├── IExternalIdResolver / ExternalIdResolver.cs
│   │   ├── IBricklinkClient / BricklinkClient.cs
│   │   │     OAuth1, BricklinkSharp-Wrapper
│   │   ├── IBricklinkTokenStorage / BricklinkTokenStorage.cs
│   │   │     DPAPI-Encrypt/Decrypt der Tokens
│   │   ├── IBlCatalogService / BlCatalogService.cs
│   │   │     Cache-First-Lookup gegen bl_cache.db, Fallback BL-API
│   │   ├── IBlCacheRepository / BlCacheRepository.cs
│   │   │     Direkt-SQLite-Zugriff auf bl_cache.db
│   │   ├── IMatchingService / MatchingService.cs
│   │   ├── IPriceProvider / DummyPriceProvider.cs
│   │   ├── IBsxExporter / BsxExporter.cs
│   │   ├── IUpdateChecker / GitHubUpdateChecker.cs
│   │   ├── IPartImageProvider / BricklinkImageProvider.cs
│   │   └── IPersistentImageCache / PersistentImageCache.cs
│   └── Database/
│       ├── UserDataContext.cs (EF Core)
│       └── Migrations/
│
└── HBSort.Tests/            (xUnit)
```

## Refactoring-Phasen R1-R4 ✅ (abgeschlossen)

- **R1 – BL-Tokens & Auth** ✅: `IBricklinkTokenStorage` (DPAPI),
  `IBricklinkClient`-Wrapper (lazy), Settings-Tab "BrickLink-API" mit
  PasswordBox-Feldern + Test-Button.
- **R2 – BL-Cache** ✅: `bl_cache.db` + `IBlCacheRepository` (raw SQLite) +
  `IBlCatalogService` (Cache-First, Stale-While-Revalidate).
- **R3 – Modus A komplett** ✅: ScanViewModel routet Brickognize-BL-ID
  durch BlCatalogService, MinifigDetailView zeigt Subsets, Reverse-Match
  Floating-Parts beim Speichern.
- **R4 – Cleanup** ✅ (PROMPT 6, 2026-05-02): Rebrickable-Catalog (catalog.db,
  ICatalogService, CatalogImporter, MinifigLookupService, SplashWindow,
  seed-data/) komplett entfernt; ColorMatch nutzt jetzt bl_colors;
  Brickognize-Color-id wird direkt als BL-Color-ID interpretiert.

## Bisherige Phasen 1-2.5 (bleiben gueltig)

✅ Phase 1 - WPF-Grundgeruest, Webcam, Settings, Tray
✅ Phase 1.5 - Catalog-Import (in PROMPT 6 komplett entfernt)
✅ Phase 2 - Brickognize, ID-Resolver (ColorMapping in 2026-05-04 entfernt)
✅ Phase 2.5 - BL-Bilder Hybrid, Persistent Cache, LRU

## Phase 4-8 (unveraendert in der Logik, aber mit BL-IDs)

### Phase 4 – Lagerfach-Verwaltung ✅
- `StorageBin` (Label, FreedAt-Flag) + `IStorageBinService` (Bulk-Anlegen
  mit Praefix/Suffix/Padding, Buchstaben-Inkrement A→B→…→Z→AA→…, siehe
  `BinNameGenerator` + Tests).
- BinManagerView im Settings-Tab "Lagerfaecher": Liste mit Belegt/Frei-
  Status, Umbenennen, "Fach leeren", Loeschen (nur wenn frei).
- Lagerfach-Auswahl in MinifigDetailView: Dropdown mit freien Faechern
  zuerst + Trennlinie + belegten Faechern; bei "Belegt"-Auswahl
  Bestaetigungs-Dialog. Reverse-Match Floating-Parts laeuft beim
  Speichern automatisch.
- **Wichtig**: Faecher bleiben **belegt** auch wenn alle Figuren
  COMPLETE/DISMANTLED/SOLD sind. Nur expliziter Klick "Fach leeren"
  setzt `StorageBin.FreedAt` und loest alle Minifig-Zuweisungen.
- Wartende-Figuren-Liste (rechte Spalte) gruppiert nach Lagerfach;
  Klick oeffnet Read-Only-Detail.

### Phase 5 – Matching-Logik (Modus B)
[wie gehabt, MatchingService nutzt BL-Part-No + BL-Color-ID]

### Phase 6 – Komplettierung & Statistik ✅ (PROMPT 7, 2026-05-03)
- DailyStats wird automatisch hochgezaehlt:
  - Scan + ggf. Komplettierung in `PersistAndStoreAsync` (Reverse-Match-Pfad)
  - Komplettierung in `CheckAndMarkCompleteAsync` (manueller Pfad ueber
    den Pending-Klick im Summary-Dialog)
  - Zerlegen in `DismantleAsync`
- Status=Complete loest Toast aus ("Figur 'X' ist komplett!")
- Komplette Figuren werden in der Lagerfach-Uebersicht in einer eigenen
  gruenen Sektion pro Bin angezeigt (statt Fortschrittsbalken: gruener Haken).
- "Wieder oeffnen"-Button im Summary-Dialog setzt eine komplette Figur
  zurueck auf Waiting (`ReopenAsync`); DailyStats wird absichtlich NICHT
  rueckgaengig gemacht.
- Bei Status=Complete sind die Buttons "Zerlegen" und "Verschieben"
  ausgeblendet (sind nur fuer wartende Figuren sinnvoll).
- Settings-Tab "Statistik" mit Heute/7T/30T/Insgesamt-Filter, drei
  Stat-Cards (Scans / Komplettiert / Zerlegt) und einer Bestand-Sektion
  (wartende, komplette, Floating-Parts, belegte Faecher).
- Status=Sold wird **nicht** eingefuehrt - der BSX-Export in Phase 7
  uebernimmt die Uebergabe ans richtige Lagersystem.

Historische Anmerkung zur Phase 6: Die "Komplette Figuren"-Sektion
wurde damals in die bestehende Bin-Overview integriert (pro Bin eine
Sub-Sektion mit gruener Markierung) statt als globale Top-Level-Liste,
um einen kompletten Umbau der damaligen `WaitingMinifigsViewModel`-
Klasse zu vermeiden. In UX X.15 (2026-05-04) wurde der Lagerfaecher-
Tab im Sortier-Bereich komplett entfernt — die volle Lagerliste lebt
jetzt im dedizierten Top-Tab "Lagerliste"; das gesamte VM samt
BinOverviewView ist geloescht.

### Phase 7 – BSX-Export ✅ (PROMPT 8, 2026-05-03)
- `IBsxExportService` (HBSort.Core/Services) generiert pro Figur eine
  ITEM-Zeile (ItemTypeID=M, Qty=1, Status=I/X, Condition=U/N, optionaler
  Remark). CategoryId wird best-effort aus dem BL-Cache gezogen,
  Default 65 (Minifig).
- Multi-Select-Checkbox in der Komplett-Sektion der Lagerfach-Uebersicht;
  globale Action-Bar mit "Alle Komplette / Keine / Exportieren (n)".
- `BsxExportDialog` zeigt die Auswahl + Optionen + Speicherort
  (Default: Documents/HBSort-Export/HBSort-Export-{Datum}.bsx, oder der
  Ordner aus `AppSettings.BsxExportFolder`).
- Nach erfolgreichem Schreiben: gruener Cleanup-Block mit Vorab-Berechnung
  wieviele Faecher leer waeren. User kann waehlen ob Figuren entfernt und
  ob leere Faecher freigegeben werden sollen.
- `IMinifigPersistenceService.RemoveExportedMinifigsAsync` loescht die
  Figuren (FloatingParts mit Origin werden entkoppelt, nicht geloescht)
  und schreibt einen ScanEvent als Audit-Trail.
- `IStorageBinService.FindEmptyOccupiedBinsAsync` + `ReleaseBinsAsync`
  fuer den Bin-Freigabe-Pfad.
- `AppSettings.BsxExportFolder` wird beim ersten Export auf den vom User
  gewaehlten Ordner persistiert.
- Der Default-Ordner ist auch in den Einstellungen unter dem eigenen
  Tab **"Export"** aenderbar (`SettingsWindow.xaml`, neuer TabItem
  zwischen "Statistik" und "Info"). UI: read-only TextBox + Buttons
  "Ordner waehlen..." / "Zuruecksetzen". Aenderungen werden ueber
  `SettingsViewModel.SaveBsxExportFolderImmediatelyAsync(...)` SOFORT
  in settings.json geschrieben (nicht erst beim "Speichern"-Button) -
  damit der naechste BsxExportDialog den neuen Pfad sieht. Der
  Default-Pfad ist als statische Property `DefaultBsxExportFolder`
  (= Documents\HBSort-Export\) im VM exponiert.

#### Phase 7-Erweiterung: Einzelteile-Export (UX-Iteration X.6, 2026-05-03)

Neben kompletten Figuren koennen jetzt auch lose Einzelteile (FloatingParts)
exportiert werden. Beides landet in einer einzigen BSX-Datei.

- `IBsxExportService.GenerateBsxAsync` nimmt jetzt zwei ID-Listen
  (`minifigIds`, `floatingPartIds`). Mindestens eine muss befuellt sein.
  XML schreibt zuerst die Minifigs (ItemTypeID=M), dann die Einzelteile
  (ItemTypeID=P, ColorID=blColorId, Qty=fp.Quantity, ColorName aus
  bl_colors-Cache mit Fallback auf `FloatingPart.ColorName`).
- `InventoryListView`: Auswahl-Checkbox jetzt auch bei Einzelteilen
  sichtbar (DataTrigger `Status=Floating`). Wartende bleiben bewusst
  ohne Checkbox - klare Optik signalisiert "kann nicht exportiert
  werden". `SelectAllExportable` markiert beide Typen, `Wartend` wird
  ignoriert.
- `BsxExportDialog`: zwei getrennte Sektionen "Komplette Figuren (X)"
  und "Einzelteile (Y)"; leere Sektion wird ausgeblendet. Ein Export
  + ein Cleanup-Pfad fuer beide.
- `IMinifigPersistenceService.RemoveExportedFloatingPartsAsync` loescht
  die FloatingParts und schreibt pro Eintrag einen ScanEvent vom Typ
  `FloatingPartExported`. ResultDescription enthaelt **Quell-Bin-Label,
  Part-No, Color-Id und Quantity** als Audit-Trail - damit man die
  Herkunft auch nach spaeterer Bin-Freigabe nachvollziehen kann.
- `IStorageBinService.FindBinsThatWouldBeEmptyAsync` bekam optionalen
  zweiten Parameter `floatingPartIdsToBeRemoved`. Bin gilt als "wird
  leer" nur wenn alle drin liegenden Minifigs UND alle drin liegenden
  FloatingParts in den jeweiligen Removal-Listen stehen.
- Neuer `ScanType.FloatingPartExported` (HasConversion&lt;string&gt;,
  keine DB-Migration noetig).

#### Phase 7-Bugfix (UX-Iteration X.12, 2026-05-04) — UTF-8 ohne BOM + Currency

BrickStore brach beim Importieren mit "Oeffnendes Element erwartet" ab
- Ursache war ein falscher XML-Prolog `encoding="utf-16"`, weil
`BsxExportService` ueber einen `StringWriter` (intern UTF-16)
gerendert hat. Die Datei selbst war schon UTF-8-kodiert (UI-Layer
nutzt `UTF8Encoding(false)`), aber der widerspruechliche Prolog hat
den Parser kaputt gemacht.

**Fix in `HBSort.Core/Services/BsxExportService.cs`**:
- `StringWriter` raus.
- `XmlWriter` direkt auf einem `MemoryStream` mit
  `Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`
  (= UTF-8 OHNE BOM) + `Indent=true`, `IndentChars="  "`,
  `NewLineChars="\n"` analog zur BrickStore-Referenz.
- `Encoding.UTF8.GetString(ms.ToArray())` liefert den korrekten String
  zurueck.
- Resultat: Prolog ist `<?xml version="1.0" encoding="utf-8"?>`,
  keine BOM-Bytes davor.

**Convention**: BSX-Dateien MUESSEN UTF-8 ohne BOM sein. Wer den
Service erweitert oder einen anderen Export-Pfad baut, darf nicht ueber
`StringWriter`/`StringBuilder` rendern - der erzwingt UTF-16 im
Prolog. Direkter `MemoryStream`/`FileStream` mit `UTF8Encoding(false)`
ist Pflicht.

**Currency-Attribut auf Inventory-Tag**: BrickStore-eigene BSX schreibt
am `<Inventory>`-Tag ein `Currency`-Attribut. HBSort macht das jetzt
auch, gelesen aus `AppSettings.Prices.Currency` (Default `"EUR"` wenn
leer/null). `BrickLinkChangelogId` wird bewusst weggelassen - HBSort
kennt den Wert nicht und Brickstore importiert auch ohne. Der
`BsxExportService`-Konstruktor bekam dazu `ISettingsService` injiziert.

**Tests** in `HBSort.Tests/BsxExportServiceTests.cs`:
- Prolog deklariert utf-8 (case-insensitive) und nicht utf-16.
- String-Anfang ist `<` (kein BOM-Character).
- UTF-8-Bytes des Outputs beginnen NICHT mit `EF BB BF`.
- `XmlReader` parst den String fehlerfrei durch.
- `Inventory`-Tag hat Currency-Attribut aus Settings (Test mit "USD").
- Bei leerer Settings-Currency Fallback auf "EUR".

#### Phase 7-Iteration (UX X.14, 2026-05-04) — Remarks/Comments-Konvention

BrickStore unterscheidet beim BSX-Format zwischen zwei Notiz-Feldern
mit unterschiedlicher Sichtbarkeit:
- `<Remarks>`  = **INTERN**. Nur der Verkaeufer sieht das in seinem
  BrickStore. Geeignet fuer Lagerort, interne Codes, Notizen die der
  Kaeufer NICHT lesen soll.
- `<Comments>` = **OEFFENTLICH**. Jeder Kaeufer sieht das in der
  Listing-Oberflaeche. Geeignet fuer Zustand-Beschreibungen
  ("vergilbt", "leichte Gebrauchsspuren").

HBSort-Mapping pro Item:

| Item-Typ | `<Remarks>` (intern) | `<Comments>` (oeffentlich) |
|---|---|---|
| TrackedMinifig (Komplett) | `StorageBin.Label` | `UserNotes` (Notiz-Feld in MinifigDetailView) |
| FloatingPart (Einzelteil) | `StorageBin.Label` | (kein Notes-Feld im Datenmodell, immer leer) |

**Beide Elemente werden nur geschrieben wenn ein Wert vorhanden ist** -
analog zu Reference-BSX-Dateien aus BrickStore, die leere optionale
Felder ebenfalls weglassen. Implementation: Helper-Methode
`AppendRemarksAndComments` in `BsxExportService`.

Was vorher anders war:
- `<Remarks>` enthielt einen automatischen "HBSort {Datum}"-Text. Das
  war oeffentlich sichtbare Reklame in einem internen Feld - falsch in
  doppelter Hinsicht.
- Im Export-Dialog gab's ein Eingabefeld "Remark" das in dasselbe Feld
  geschrieben hat. Das ist mit der Konvention raus, weil <Remarks> jetzt
  pro Item automatisch das Lagerfach traegt.
- `<Comments>` wurde nie geschrieben.

Convention fuer kuenftige BSX-Erweiterungen:
- HBSort-spezifische Texte (Tool-Name, Datum, Datei-Name) duerfen in
  KEINEM XML-Feld auftauchen, auch nicht als Default. Tests
  `Generate_xml_does_not_contain_HBSort_signature_or_filename_in_text_fields`
  sichert das gegen Regression.
- Wenn fuer FloatingPart spaeter ein Notes-Feld im Datenmodell ergaenzt
  wird, kann es 1:1 in `<Comments>` mit einfliessen - der Helper ist
  schon vorbereitet (`userNotes`-Parameter).

#### Phase 7-Bugfix (UX-Iteration X.13b, 2026-05-04) — Lagerliste-Selektion ueber Property-Setter

Bug: Klick auf eine einzelne Checkbox in der Lagerliste hat zwar den
visuellen Haken gesetzt, aber `SelectedExportableCount` blieb 0 und
der Exportieren-Button ausgegraut. Erst der "Alle"-Button hat
funktioniert.

Wurzel-Ursache: WPF-DataGrid mit `IsReadOnly="True"` in Kombination
mit einer eingebetteten CheckBox in `DataGridTemplateColumn`. Der
bubbled `Click`-Event wird teilweise von der DataGrid-Cell-Mouse-
Logik verschluckt, bevor er den Code-Behind-Handler erreicht. Der
`IsChecked`-Toggle (TwoWay-Binding zu `IsSelected`) feuert weiter
ueber Mouse-Capture des Toggle-Buttons - daher der visuelle Haken -
aber der `Click`-Handler `CompleteSelect_Click`, der
`RecalculateSelection()` haette aufrufen sollen, lief nie.

**Fix per Property-Setter** (`InventoryListViewModel.cs`,
`InventoryRowItem`):
- Neues `event EventHandler? SelectionChanged` auf `InventoryRowItem`.
- Der CommunityToolkit.Mvvm-Hook `partial void OnIsSelectedChanged(bool)`
  feuert das Event - egal ob Klick, Tastatur-Space, Touch, Bulk-Aktion
  oder Code-Set.
- `InventoryListViewModel` abonniert `Items.CollectionChanged` und
  meldet sich auf jedes hinzukommende Row-Item an
  (`OnRowSelectionChanged → RecalculateSelection()`). Bei Remove
  sauber abmelden, sonst halten alte Items den Counter wachsam.
- XAML: `Click="CompleteSelect_Click"` von der CheckBox entfernt;
  Code-Behind-Handler geloescht.

**Convention**: Selektions-Updates in DataGrid-Cell-Templates IMMER
ueber Property-Setter (per `partial void OnXxxChanged`) wirken
lassen, **nicht** ueber Click-Events. Click ist bei DataGrid-Cells
unzuverlaessig.

**Tests** (`InventoryListViewModelTests.cs`): die alten 8 Regressions-
Tests waren gruen, weil sie `IsSelected` DIREKT plus manuelles
`RecalculateSelection()` aufgerufen haben - sie haben den Wiring-Bug
nie gefangen. Jetzt 5 realistische Tests die NUR `IsSelected` setzen
(ohne manuellen `RecalculateSelection`-Call):
- `Setting_IsSelected_directly_updates_SelectedExportableCount`
- `Setting_IsSelected_on_multiple_rows_increments_count`
- `Unsetting_IsSelected_decrements_count`
- `Setting_IsSelected_fires_PropertyChanged_for_SelectedExportableCount`
- `Removing_a_row_unsubscribes_so_late_changes_are_ignored` (verhindert
  Geister-Selektionen aus alten Items).

Vor dem Fix: 4/5 dieser Tests scheiterten. Nach dem Fix: alle gruen.

##### Phase 7-Bugfix (UX-Iteration X.13d, 2026-05-04) — Re-Open: tatsaechliche Wurzel-Ursache

Die X.13b-Iteration oben (SelectionChanged-Event + CollectionChanged-
Hook) war architektonisch korrekt, hat den Bug aber **nicht** behoben.
Drei statische Code-Reviews konnten nicht erklaeren warum trotz
sauberer VM-Logik die UI weiter "0 markiert" zeigte. Live-Diagnose
mit `[SELECTION]`-Logs zeigte: der `IsSelected`-Setter wurde bei
manuellem Checkbox-Klick **nie** aufgerufen, obwohl der Haken
visuell erschien.

**Echte Wurzel-Ursache**: WPF-CheckBox-IsChecked-TwoWay-Binding hat
als Default `UpdateSourceTrigger=LostFocus`. In einer
`DataGrid` mit `IsReadOnly="True"` haelt die CheckBox aber den
Tastatur-Focus nicht zuverlaessig - die DataGrid-Cell-Mouse-Logik
nimmt den Focus weg, **bevor** ein LostFocus-Event auf der CheckBox
feuern wuerde. Folge: der Source-Update-Trigger feuert nie, IsSelected
bleibt unberuehrt, SelectionChanged-Event wird nicht emittiert.
Visuelles Toggle der Checkbox kommt aus dem internen IsChecked-State,
nicht aus der Source.

**Fix**: am Binding explizit `UpdateSourceTrigger=PropertyChanged`
setzen. `InventoryListView.xaml`:
```xml
<CheckBox IsChecked="{Binding IsSelected,
                       Mode=TwoWay,
                       UpdateSourceTrigger=PropertyChanged}" .../>
```

**Convention** (zwingend bei DataGrid mit `IsReadOnly="True"`): jede
CheckBox/TextBox/Slider in einer `DataGridTemplateColumn` braucht
`UpdateSourceTrigger=PropertyChanged` am TwoWay-Binding. Ohne das
greift LostFocus, das aber in read-only-DataGrids nicht zuverlaessig
feuert. Bei DataGrids mit `IsReadOnly="False"` (Edit-Mode) ist's
zumindest fuer TextBoxen unkritisch, weil die Cell in den Edit-State
geht und der Focus dort sauber verwaltet wird.

**Diagnose-Logs** sind als `Log.Debug` im Code geblieben (siehe
`InventoryRowItem.OnIsSelectedChanged` und `RecalculateSelection`):
default Information-Level filtert sie raus, fuer kuenftige Diagnose-
Sessions kann das Log-Level temporaer angehoben werden.

#### Phase 7-Bugfix (UX-Iteration X.13c, 2026-05-04) — BSX-Export-Cleanup-Dialog

User-Bug-Bericht: nach Export von 3 Einzelteilen (0 Minifigs) aus
einem Lagerfach zeigte der Cleanup-Dialog "(keine Faecher wuerden
leer)" - Bin-Freigabe-Checkbox blieb ausgegraut.

Code-Analyse-Befund: Die Service-Logik
(`IStorageBinService.FindBinsThatWouldBeEmptyAsync` mit optionalem
zweiten Parameter `floatingPartIdsToBeRemoved`, seit UX X.6) und der
Aufruf-Pfad in `BsxExportDialog.xaml.cs::ShowCleanupBlockAsync` sind
**bereits korrekt**. Vier zusaetzliche Repro-Tests in
`StorageBinServiceTests.cs` decken das exakte User-Szenario ab und
laufen gruen:
- `FindBinsThatWouldBeEmpty_three_floatings_no_minifigs`
- `FindBinsThatWouldBeEmpty_skips_bin_with_unrelated_floating_left_behind`
- `FindBinsThatWouldBeEmpty_skips_bin_with_waiting_minifig`
- `End_to_end_release_after_floating_only_export_marks_bin_as_freed`

Die Tests beweisen dass die Logik im aktuellen Code-Stand das
User-Szenario korrekt erkennt. Der Bug-Bericht stammt vermutlich aus
einem Build vor UX X.6.

**UX-Verbesserung am Cleanup-Dialog**: Der Text war ohnehin verwirrend.
- Header: "Export erfolgreich" (knapp).
- Body: Item-Anzahl + Datei + Frage. Floating-Origin-Detail gestrichen.
- Checkbox-Label: konkrete Bin-Namen statt nur Zahl
  ("Box 002 freigeben (wuerde leer)" / "3 Lagerfaecher freigeben
  (Box 002, Box 005, Box 008)"). Helper-Methode `BuildReleaseBinsLabel`.
- Singular/Plural je nach Anzahl korrekt formuliert.

#### Sortier-Tab UX-Politur (UX-Iteration X.19, 2026-05-05)

Drei Verbesserungen am Sortier-Tab nach UX X.18.

**Teil 1 — Lagerfach-Badge in PartLookupView**
- In der "PASSENDE WARTENDE FIGUREN"-Liste rueckte das Bin-Label
  ("(Box 005)") aus dem Klammer-Suffix in ein eigenes Badge zwischen
  Mittelspalte und "Zuordnen"-Button.
- Badge-Optik: Hintergrund `InfoBoxBackground` (#E3F2FD), 1px Border in
  `StatusInfoBrush` (#1976D2), 📦-Icon + Bin-Label fett (FontSize 12).
- Tooltip: "In dieses Fach gehoert die Figur - dort das gerade gescannte
  Teil ablegen."
- Konsistent gestylt in `WaitingDetailView.xaml` (siehe Teil 2).

**Teil 2 — Klick auf Wartende-Detail-Karte oeffnet MinifigSummaryDialog**
- Karten im Wartende-Detail-Tab sind jetzt klickbar (Cursor=Hand,
  Hover-Background-Wechsel, ToolTip "Details anzeigen"). Klick oeffnet
  denselben Dialog wie der "Details"-Button in der Lagerliste.
- Bild-Klicks gehen weiter ans `b:ImageZoom`-Behavior - das setzt schon
  `e.Handled=true`, kein Doppelfeuern auf den Border.
- Inline-Bin-Run aus dem Sub-Header rausgeflogen, durch ein Badge
  (gleiche Optik wie Teil 1) ersetzt.
- Code-Behind: `WaitingDetailView.xaml.cs::Card_Click` macht
  `Service<T>()`-Lookup analog zu `InventoryListView.Details_Click`.
  Wenn das zur dritten/vierten Stelle wird, lohnt sich ein Helper -
  aktuell sind es nur zwei Aufrufstellen.

**Teil 3 — Splitter pro Spalte unabhaengig + persistent**

*Layout-Refactor (Teil 3a):*
Vorher hatte das Outer-Grid 3 Rows + 5 Cols, mit einem GridSplitter in
Row 1 ueber ColumnSpan=3 - Spalten 1+2 teilten sich also einen
horizontalen Splitter. Spalte 3 hatte ein eigenes Subgrid mit eigenem
Splitter.

Jetzt einheitlich: Outer-Grid hat 1 Row + 5 Cols. Jede der drei
Inhalts-Spalten ist ein eigenes Subgrid mit `65*/5/35*`-RowDefinitions
und einem eigenen GridSplitter. Damit kann der User die Hoehen pro
Spalte unabhaengig verschieben.

x:Name auf den Subgrid-Rows (`Col1TopRow`/`Col1BotRow`/`Col2TopRow`/
`Col2BotRow`/`Col3TopRow`/`Col3BotRow`) damit der Code-Behind sie
referenzieren kann.

*Persistierung (Teil 3b):*
Drei neue Properties in `AppSettings.WindowState`:
- `Column1HorizontalSplitterRatio` (Anteil obere Box in Spalte 1, 0..1)
- `Column2HorizontalSplitterRatio`
- `Column3HorizontalSplitterRatio`
Default jeweils `0.65` (entspricht 65/35-Layout).

`SplitterRowRatio` aus Phase 4 ist als DEPRECATED markiert - das war
ein gemeinsames Verhaeltnis fuer das alte 2x2-Layout, wird durch die
drei Pro-Spalten-Werte abgeloest. Bleibt im POCO erhalten damit alte
settings.json beim Laden keinen Crash macht.

Persistierungs-Mechanik:
- Bei jedem `DragCompleted` der drei horizontalen Splitter:
  Top-Row.ActualHeight / (Top + Bot).ActualHeight rechnen, in Settings
  schreiben, `_ = SaveAsync()` (fire-and-forget).
- Beim `Loaded`-Event: `ApplySplitterRatios` setzt die `RowDefinition.
  Height` jeder Spalte aus den Settings.
- Defensiv: `ClampOrDefault` faengt `NaN`, Infinity und Out-of-Range-
  Werte ([0.05..0.95] erlaubt) ab und faellt auf Default 0.65 zurueck.

Backwards-Compat:
- Alte settings.json ohne diese Properties laedt sauber - System.Text.
  Json belaesst fehlende Felder auf POCO-Default.
- settings.json mit unbekannten Zukunftsfeldern wird tolerant
  geladen (Test in `WindowStateSplitterRatiosTests`).

**Tests (9 neu in `WindowStateSplitterRatiosTests.cs`):**
- Defaults = 0.65 fuer alle drei Spalten.
- Alte JSON ohne horizontale Properties laedt mit Defaults.
- Round-Trip erhaelt gesetzte Werte.
- Tolerantes Laden bei unbekannten Zukunftsfeldern.
- Out-of-range-Werte (negativ, > 1, ueber/unter Min/Max) round-trippen
  ohne Crash; Clamping passiert beim Apply im Code-Behind.

NaN/Infinity koennen vom JsonSerializer per Default nicht serialisiert
werden, lassen sich also nicht aus normalen Schreibvorgaengen in der
settings.json landen. Der `ClampOrDefault`-Pfad faengt sie trotzdem
ab als Defensiv-Schutz; ein UI-Test dafuer waere disproportional.

#### Sortier-Tab Layout final (UX-Iteration X.18, 2026-05-05)

Sortier-Tab final als 3 gleich breite Spalten mit klar verteilten
Inhalten. Das Layout ist nach dieser Iteration die Referenz-Anordnung;
kuenftige Aenderungen am Sortier-Tab sollten diese Tabelle aktualisieren.

**Endgueltige Spalten-/Box-Belegung:**

| Spalte | Oben (R0, ~65%) | Unten (R2, ~35%) |
|---|---|---|
| **1 (Col0)** | Webcam-Live + Scannen-Button | Brickognize-Top-3-Karten (mit Uebernehmen-Button + ImageZoom auf Bild) |
| **2 (Col2)** | MinifigDetailView (Pending-Minifig) ODER PartLookupView (Pending-Part), kontextabhaengig | TabControl: **Live-Stats / Wartende-Detail / Letzte Scans** (3 Tabs) |
| **3 (Col4)** | **BuildSuggestionsView** ("Was kann ich bauen?", dauerhaft sichtbar) | **MinifigPriceView** (BL-Preise, sichtbar wenn Pending-Minifig aktiv) |

**Aenderungen gegenueber UX X.16/X.17:**
- *Inhaltstausch in Spalte 3*: Preise gewandert von oben nach unten;
  obere Box jetzt mit BuildSuggestionsView befuellt.
- *Tab-Reihe in Spalte 2 reduziert*: "Was kann ich bauen?"-Tab raus
  (Inhalt jetzt dauerhaft in Spalte 3 oben). Verbleibende Tabs:
  Live-Stats, Wartende-Detail, Letzte Scans (Indizes 0-2).
- *BottomRightTabIndex-Migration*: alte Werte aus settings.json
  (UX X.15: 0..4, UX X.16: 0..3) werden auf [0..2] geclampt - ueber
  einen Tab hinausgehende Werte landen auf Live-Stats (= Index 0).
- *Horizontale GridSplitter ergaenzt*: Outer-Splitter ueber Col0+Col2
  (Spalte 1+2 gemeinsam) und Inner-Splitter im Col4-Subgrid (Spalte 3
  unabhaengig). Beide ohne Persistierung - beim naechsten App-Start
  ist wieder 65/35.
- *Spaltenbreiten*: alle drei Spalten Width="*", Default-Verhaeltnis
  in `AppSettings.WindowState.SplitterColumnRatio`/`Ratio2` ist
  jeweils 1/3 - 3 gleich breite Spalten beim Erststart, durch die
  vertikalen Splitter individuell anpassbar (Verhaeltnis persistiert).

**BuildSuggestionsView-Refresh-Verhalten**: Singleton-VM aus dem
DI-Container. Konstruktor abonniert `IMinifigPersistenceService.
DataChanged` - die Liste aktualisiert sich automatisch nach jedem
Scan/Lager/Komplettiere. Seit Audit K-2 (UX X.17) mit IDisposable +
Unsubscribe abgesichert.

**Toter Code**: kein Cleanup noetig - BinOverviewView und
WaitingMinifigsViewModel waren bereits in UX X.15 entfernt.

#### Audit-Restwelle (UX-Iteration X.17, 2026-05-04)

Folge-Iteration zu UX X.16. Alle in der Vorrunde aufgeschobenen Audit-
Findings sind jetzt abgearbeitet.

**Kritisch:**
- **K-2** Memory-Leak-Pattern an 4 Singleton-VMs behoben:
  `BuildSuggestionsViewModel`, `LiveStatsViewModel`,
  `WaitingDetailViewModel`, `RecentScansViewModel` implementieren jetzt
  `IDisposable` mit Field-basierter Subscription. ServiceProvider-Dispose
  in `App.xaml.cs::OnExit` ruft `Dispose()` automatisch.
  Tests: 4 neue in `SingletonViewModelDisposeTests.cs` mit Subscriber-
  Counter-Stub.

**Wichtig:**
- **W-5** `MinifigPersistenceService.PersistAndStoreAsync` ist jetzt mit
  10 Theory-/Fact-Tests abgedeckt (Match-Szenarien, Auto-Complete,
  Bin-Zuweisung, DailyStats-Increment, ScanEvent-Audit, Capping,
  Missing-Bin-Throw).
- **W-6** Tests fuer `PartLookupService` (15 neue: AddPartToFloating-
  Stack, Assign/Unassign, FindFloatingLocations, Delete) und
  `BricklinkApiPriceProvider` (7 neue: Konfiguration, Cache-First,
  TTL-Routing M/P, Rate-Limit-Stale-Fallback). BlBulkImportService
  bewusst weggelassen (HTTP+ZIP+XML-Mock-Aufwand disproportional).
- **W-7** `NullToImageSourceConverter` baut Bilder jetzt explizit mit
  `BitmapCacheOption.OnLoad` + `Freeze()` statt auf den WPF-Default-
  TypeConverter zu fallen. Damit lockt ein gerendertes Bild nicht
  mehr seine Disk-Datei. Plus Try-Catch fuer ungueltige URLs.
  Tests: 6 neue mit Beweis dass die Datei direkt nach Convert
  loeschbar ist.
- **W-8** `PriceSettings.CacheDays` entfernt. Provider liest jetzt die
  passenden TTL-Felder pro Item-Typ
  (`BlPriceCacheTtlMinifigDays`/`BlPriceCacheTtlPartDays`). Alte
  settings.json mit `CacheDays`-Eintrag laedt weiterhin sauber
  (System.Text.Json ignoriert unbekannte Felder); 3 neue Tests
  sichern die Migration ab.
- **W-11** `ScanResultCard` hat keine `System.Windows.Media`-Imports
  mehr. Brush-Property durch `IsTopHit`-bool ersetzt; XAML setzt die
  visuellen Stile via `DataTrigger`.

**Mittel:**
- **M-2/M-3** DateTime-UTC-Vereinheitlichung. `DailyStats.Date`,
  `LiveStatsViewModel`-Lookup, `SettingsViewModel`-Statistik-Filter
  und `BricklinkRateLimiter`-CallsToday nutzen jetzt durchgaengig
  `DateTime.UtcNow.Date`. `BrickognizeClient` Filename- + Trace-
  Stempel auf UTC. User-facing Filenamen (BSX/Wanted-Export) +
  Toast-CreatedAt bewusst lokal belassen.
  Migration-Hinweis: alte DailyStats-Eintraege im lokalen Schema
  bleiben as-is (max. ein Tag versetzt bei Zeitzonen-Wechsel,
  fuer Einzel-User-Desktop praktisch irrelevant).
- **M-8** `MigrateLegacyAppDataIfNeeded` ist jetzt async, Aufruf in
  `Application_Startup` mit `await`. Praktisch egal (laeuft einmalig
  vor App-Start), aber konsistent mit dem Rest der File-IO-Konvention.
- **M-10** Zwei dokumentierte TODOs (`BlPriceCacheService:27`,
  `HelpViewModel:24`) auf das einheitliche Format
  "TODO 2026-05 (Kategorie): ..." gebracht.
- **M-11** Verifiziert: das DB-Doppelfeld `BricklinkColorId` ist seit
  Migration `20260503110017_RemoveBricklinkColorIdField` (2026-05-03)
  bereits weg. Verbleibende `BricklinkColorId`-Properties sind
  Input-DTO-Felder (`PendingPart`, `PersistMinifigPart`) - bewusste
  Naming-Konvention damit der Caller weiss "hier BL-IDs erwartet".
- **M-12** `BinNameGeneratorTests` konsolidiert: 14 `[Fact]`-Tests
  -> 7 Theories mit insgesamt 33 InlineData-Cases. Lesbarer und
  einfacher um Edge-Cases zu erweitern.

**Nice-to-have:**
- **N-1** Acht `CornerRadius="3"`-Stellen auf `4` vereinheitlicht
  (App-Standard). `CornerRadius="6"` an MainWindow Status-Bar +
  ZoomOverlayHost bleibt - groesserer Container.
- **N-2** ImageZoom-Anpassung war ein False Positive im Audit -
  PartLookupView hat den Behavior bereits an allen Image-Elementen.
  RecentScansView zeigt nur Text-Eintraege (kein Image-Element),
  daher nicht relevant.
- **N-3..N-8** Kein Code-Change (siehe Audit-Report-Begruendungen).

**Test-Coverage**: 305/305 Tests gruen (vorher 244 nach UX X.16 +
61 neue in dieser Iteration).

**Damit ist der CODE_AUDIT_REPORT.md-Backlog komplett abgearbeitet.**
Verbleibende Audit-Hinweise sind alle bewusste Konventionen (z.B.
keine Migration der alten DailyStats-Daten), die im Code-Kommentar
oder hier dokumentiert sind.

#### Audit-Aufraeum-Welle (UX-Iteration X.16, 2026-05-04)

Folge-Iteration zum CODE_AUDIT_REPORT.md (Audit-Stand
Commit 5d09891c). Es wurden die einfachen, klar umrissenen Findings
abgearbeitet; die zwei aufwendigeren (K-2 Subscription-Pattern,
W-5 PersistAndStoreAsync-Tests) wurden bewusst auf eine spaetere
Iteration verschoben.

**Kritische Findings (alle erledigt):**
- **K-1** Umlaute/Mojibake: kompletter Sweep ueber `*.cs` und `*.xaml`
  in HBSort, HBSort.Core und HBSort.Tests. Echte Umlaute durch
  ae/oe/ue/ss ersetzt; `â€"`-Mojibake und echte Em-/En-Dashes durch
  ASCII-Minus; Smart-Quotes durch ASCII-Apostroph/Quote. Alle
  Dateien als UTF-8 ohne BOM. **UI-Symbole (✓ ✕ ⚠ → • × € 📦 ↻)
  bleiben** — die sind keine Umlaute, sondern bewusste UI-Glyphen.
  Konvention bestaetigt: Umlaute in Code/UI-Texten = ASCII; nicht-
  ASCII nur fuer funktionale Symbole.
- **K-3** FloatingPart-Performance-Index: Composite-Index
  `(PartNumber, ColorId)` an FloatingPart in
  `UserDataContext.OnModelCreating` + neue Migration
  `20260504172934_AddFloatingPartPartColorIndex`. Der haeufige
  Reverse-Match-Filter im `MinifigPersistenceService` braucht jetzt
  keinen Sequential Scan mehr. **Hinweis aus dem Audit korrigiert:**
  `OriginMinifigId` hat *bereits* einen Index aus der Migration
  `20260502035301_AddOriginMinifigIdToFloatingPart` — das war ein
  False Positive im Audit-Bericht.

**Wichtige Findings (alle erledigt):**
- **W-1** `IsCancel + IsDefault` am selben Button entfernt:
  `BinDetailDialog.xaml:151`, `SupersetsDialog.xaml:117` — beides
  reine Read-Only-Schliessen-Buttons; nur `IsCancel="True"` reicht.
- **W-2** Cancel/Schliessen-Konvention in CLAUDE.md festgeschrieben:
  "Schliessen" fuer Read-Only-Dialoge, "Abbrechen" fuer Editier-
  Dialoge; primaerer Button rechts mit `IsDefault` + AccentButton-
  Style; nicht beide Properties am selben Button.
- **W-3** `AccentButtonStyle` an primaeren Aktions-Buttons ergaenzt:
  `PartLookupView.xaml:217` "Diese Figur anlegen",
  `SortingView.xaml:340` "Uebernehmen". CLAUDE.md erweitert um Hinweis
  auf fehlenden `DangerButtonStyle` fuer destruktive Buttons (TODO).
- **W-4** Status-Brushes als zentrale Resources: 8 neue
  `SolidColorBrush`-Eintraege in `App.xaml`
  (StatusSuccessBrush/StatusWarningBrush/StatusErrorBrush/
  StatusInfoBrush + die vier Box-Backgrounds). 50 Hex-Vorkommen in
  8 Views auf `{StaticResource ...}` umgestellt.
- **W-9** Hilfe `04-export-verkauf.md` aktualisiert:
  Bemerkung-Eingabe-Hinweis raus (UX X.14), neue Sektion zu
  `<Remarks>`/`<Comments>`-Konvention, Empfehlungs-Banner-Beschreibung
  durch gruene-Markierung-Beschreibung ersetzt (UX X.15).
- **W-10** Hilfe `02-sortier-workflow.md` um Smart-Storage-Suggestion
  ergaenzt (UX X.7) — User wird auf das Stapel-Wachstum-Verhalten
  hingewiesen.

**Mittlere Findings (groesstenteils erledigt):**
- **M-1** `ColorMapping.cs` + Tests komplett geloescht (784 + ~120
  Zeilen). War seit PROMPT 6 nicht mehr im Hauptfluss; Tests sicherten
  nur sich selbst ab. CLAUDE.md-Erwaehnungen (4 Stellen) korrigiert.
- **M-6** CLAUDE.md:857: Verweis auf gestrichene
  `WaitingMinifigsViewModel`-Klasse umformuliert zu historischer
  Anmerkung mit Hinweis auf UX X.15-Loeschung.
- **M-7** Doppelte Indizes: **kein Befund** nach Verifikation. Der
  Audit hatte `HasOne...HasForeignKey...`-Konstrukte als "doppelte
  HasIndex" interpretiert — sind aber FK-Konfigurationen, von denen
  EF genau einen Index erzeugt. Snapshot-Check bestaetigt: kein
  doppelter Index pro Spalte. Kein Code-Aenderung noetig.
- **M-13** `DialogHeaderFontSize`-Resource (=20) in `App.xaml`;
  3 Header-Verwendungen umgestellt (MainWindow App-Logo,
  HelpView-Titel, BuildSuggestionDetailDialog-Titel). LiveStatsView-
  Stat-Zahlen behalten ihren hardcoded Wert (andere Semantik).
- **M-14** Padding 10 -> 12 in `BinBulkCreateDialog.xaml:102` (passt
  zum 0/2/4/6/8/12/16-System).
- **M-9** Opportunistisch: ein `// PROMPT 6`-Kommentar in
  `SettingsWindow.xaml:192` durch inhaltliche Beschreibung ersetzt
  (war beim Status-Brushes-Sweep ohnehin angefasst). PROMPT-
  Kommentare in Migration-Dateien bleiben — Migrations sind frozen.

**Bewusst aufgeschoben** (eigene Iteration):
- **K-2** DataChanged-Subscriptions bei den 4 Singleton-VMs
  (`BuildSuggestionsViewModel`, `LiveStatsViewModel`,
  `WaitingDetailViewModel`, `RecentScansViewModel`) auf `IDisposable`
  + Unsubscribe umstellen.
- **W-5** Theory-Tests fuer `MinifigPersistenceService.PersistAndStoreAsync`
  (zentrale Domain-Logik, 200+ Zeilen Reverse-Match + RequiredParts-
  Anlage).
- **W-6** Tests fuer `PartLookupService`, `BricklinkApiPriceProvider`,
  `BlBulkImportService`.
- **W-7** Eigener `BitmapImage`-Creator mit `BitmapCacheOption.OnLoad`
  im `NullToImageSourceConverter`.
- **W-8** `PriceSettings.CacheDays` in `BricklinkApiPriceProvider` auf
  neue TTL-Felder umstellen.

**Tests:** 244/244 gruen (vorher 256 — 12 ColorMapping-Tests sind
mit M-1 weggefallen). Build sauber.

#### Sortier-Tab UX-Putz (UX-Iteration X.15, 2026-05-04)

Drei kleine UX-Korrekturen im Sortier-Tab:

**Teil 1 — Gruene Markierung der besseren Preis-Variante** (oben rechts,
MinifigPriceView). Statt einer separaten Empfehlungs-Banner unter der
zweispaltigen Anzeige wird jetzt die Haelfte mit dem hoeheren Wert in
gruen (`#E8F5E9`) hinterlegt. Bei Gleichstand gewinnt die Komplett-Figur
(Default). Beide Halften muessen erfolgreich geladen sein - sonst keine
Markierung.
- Neue VM-Properties `IsCompleteWinning` / `IsPartsWinning` (computed,
  beide false bis CompleteHasPrice && PartsHasAnyPrice).
- XAML: jede Haelfte in `<Border>` mit DataTrigger gewrappt; Tooltip
  "Diese Variante bringt aktuell mehr".
- Empfehlungs-Banner samt Row entfernt; `HasRecommendation` /
  `RecommendationText` sind als deprecated Stubs erhalten (HasRecommendation
  liefert immer false).
- 6 neue Tests in `MinifigPriceViewModelTests` (komplett>parts,
  parts>komplett, gleich, nichts geladen, nur eine Haelfte, Komplett-Fehler).

**Teil 2 — Brickognize-Karten Klick-Trennung** (unten links, SortingView).
Die Top-3-Karten hatten `MouseLeftButtonUp` am ganzen Border und
gleichzeitig `b:ImageZoom.IsEnabled="True"` am Image - der Image-Klick
hat zwar `e.Handled=true` gesetzt, aber `Cursor=Hand` und der Hover-
Trigger haben suggeriert dass die ganze Karte geklickt werden soll inkl.
Bild. Jetzt eindeutig getrennt:
- Bild-Klick = nur Zoom-Overlay (Tooltip "Vergroessern").
- Neuer "Uebernehmen"-Button unter HighlightLabel triggert
  `SelectCardAsync(rank-1)` (Tooltip "Diese Erkennung uebernehmen").
- Karten-Hintergrund neutral, kein Cursor=Hand mehr; gruener Border bei
  IsSelected bleibt erhalten.
- Handler umbenannt: `ResultCard_MouseLeftButtonUp` → `SelectCard_Click`
  (RoutedEventArgs statt MouseButtonEventArgs).
- Convention: bei UI-Elementen mit `b:ImageZoom.IsEnabled="True"` darf
  der umgebende Container keinen MouseLeftButtonUp-Handler oder
  Cursor=Hand setzen - sonst entsteht eine optische Klick-Erwartung am
  Bild die mit dem Zoom-Overlay konkurriert.

**Teil 3 — "Lagerfaecher"-Tab aus Sortier-Tab entfernt** (unten mitte,
TabControl). Der Tab war redundant zum dedizierten "Lagerliste"-Top-Tab.
Das TabControl unten mitte behaelt vier Tabs: "Was kann ich bauen?",
"Live-Stats", "Wartende-Detail", "Letzte Scans".
- `BinOverviewView.xaml` + `.xaml.cs` geloescht.
- `WaitingMinifigsViewModel.cs` geloescht (inkl. `BinOverviewItemViewModel`,
  `WaitingMinifigViewModel` innere Klasse, `BinOverviewFilter` enum) -
  wurde nur von BinOverviewView genutzt.
- DI-Registrierung in `App.xaml.cs:244` entfernt.
- `MainViewModel.WaitingMinifigs`-Property + ctor-Parameter entfernt.
- `BinDetailDialog` bleibt - wird auch von `SettingsWindow` (BinManagerView)
  genutzt.

#### Smart-Storage-Suggestion beim Lagern (UX-Iteration X.7, 2026-05-03)

Beim "Als Einzelteil lagern"-Workflow in der `PartLookupView` schlaegt die
App jetzt automatisch das passende Lagerfach vor: wenn dasselbe Teil mit
gleicher Farbe schon in einem Fach liegt, wird dieses Fach im Dropdown
vorausgewaehlt - so wachsen Stapel weiter statt sich ueber mehrere Faecher
zu zerstreuen.

- Neue Methode `IFloatingPartTransferService.FindBestStorageBinSuggestionAsync(
  blPartNo, blColorId)` liefert ein `FloatingPartLocationSuggestion`-DTO
  (BinId, BinLabel, QuantityInThisBin, TotalMatchingBinsCount,
  TotalQuantityAcrossAllBins) oder null wenn das Teil noch nirgends liegt.
- Sortierung: groesste Quantity zuerst (Stapel-Wachstum); bei Gleichstand
  FIFO nach AddedAt fuer reproduzierbare Wahl.
- `ScanViewModel.LoadAvailableBinsForPendingPartAsync` ruft den Service vor
  der Default-Auswahl auf. Bei Match: das Bin wird in `SelectedFloatingBin`
  gesetzt; bei kein Match: bisheriges Verhalten (naechstes freies Fach).
- `PartLookupViewModel`-Properties: `HasMatchingFloatingBin`,
  `MatchingFloatingBinLabel`, `MatchingFloatingBinQuantity`,
  `MatchingFloatingBinCount` + computed `MatchingFloatingBinHintText`
  ("📦 Liegt schon in Box 003 (3x)" bzw. "...und 2 weiteren Faechern").
- `PartLookupView`: zusaetzlicher Hint-TextBlock unter dem Dropdown,
  Visibility-Binding auf `HasMatchingFloatingBin` (verschwindet komplett
  wenn kein Match - kein Platzhalter-Slot).
- Hochzaehl-Logik existiert bereits in
  `IPartLookupService.AddPartToFloatingAsync` (PartLookupService.cs:186-189)
  - bei Match auf (PartNumber, ColorId, StorageBinId) wird Quantity addiert
  statt einen Doppel-Eintrag anzulegen.

Gilt **nur** fuer "Als Einzelteil lagern". Die "Diese Figur anlegen"-
Workflows (BuildSuggestionDetailDialog, MinifigDetailView) schlagen
weiterhin freie Faecher vor - eine neue Figur kommt in ihr eigenes Fach.

NICHT in Phase 7 (bewusst weggelassen):
- Status=Sold (Export ist die Uebergabe ans richtige Lagersystem; eine
  parallele Sold-Markierung waere doppelte Buchhaltung).
- Eigene Verlauf-Ansicht (ScanEvents reichen als Audit-Trail; kann
  spaeter ueber eine Read-only-View sichtbar gemacht werden).

### Phase X.3 – Variables Feld unten rechts ✅ (PROMPT 11, 2026-05-03)
- Sortier-Tab unten rechts (R2,C2) ist ein TabControl mit 4 Ansichten
  (UX X.15: vorher 5, "Lagerfaecher"-Tab entfernt - die volle Lagerliste
  ist im eigenen Top-Tab "Lagerliste" verfuegbar):
  - **Was kann ich bauen?** - Reverse-Match aus dem Floating-Pool, zeigt
    BL-Minifigs deren Subsets durch die losen Teile (mind. N% Match) abgedeckt
    waeren. Slider fuer Min-Match (10..100, Default 50). Bereits getrackte
    Figuren werden ausgefiltert.
  - **Live-Stats** - Heute / Letzte 7 Tage / Aktueller Bestand + Streak-Tage.
  - **Wartende-Detail** - Liste aller wartenden Figuren mit den FEHLENDEN
    Teilen pro Figur ("3x Helm (Black) [BL:88284]").
  - **Letzte Scans** - Top-50 ScanEvents chronologisch absteigend.
- Tab-Auswahl persistiert in `AppSettings.BottomRightTabIndex`.
- Alle 4 neuen ViewModels sind Singletons + lauschen auf
  `IMinifigPersistenceService.DataChanged` -> Live-Refresh.
- Neue Repository-Method `IBlCacheRepository.FindMinifigsContainingPartsAsync`
  fuer den BuildSuggestions-Reverse-Match (filtert `is_from_supersets=0`).

### Phase 8 – BL-Preise + Verkaufsempfehlung ✅ (PROMPT 12, 2026-05-03)
- Provider-Pattern: `IPriceProvider` + `IPriceProviderFactory` + zwei
  Implementierungen (`DummyPriceProvider`, `BricklinkApiPriceProvider`).
  Factory liest Settings.Prices.Provider und liefert die passende
  Implementierung; alle Provider sind Singletons - Wechsel ist live.
- `BricklinkApiPriceProvider` nutzt `BricklinkSharp.GetPriceGuideAsync`
  mit Filtern fuer GuideType (sold/stock), Condition (Used Default),
  Region, Country, Currency. Cache-First: bl_prices in bl_cache.db
  (PRIMARY KEY ueber alle Filter-Felder). Stale-Fallback bei API-Fehler
  oder Rate-Limit-Block.
- `PriceCalculationService` aggregiert Minifig-Preis + Sum aller
  Required-Parts (qty * preis), wendet Korrekturen aus Settings an
  (-10% Minifig / -15% Parts Default), leitet `SalesAdvice` ab
  (CompleteWorthIt / PartsWorthIt / Equal / NoData; 10% Diff-Schwelle).
- `MinifigSummaryDialog` (Status=Complete + Provider!=None):
  neuer Verkaufsempfehlung-Block VOR den Buttons mit
  Provider-Info, Minifig-Preis, Teile-Summe, farbiger Empfehlung
  und "Neu laden"-Button. Auto-Load wenn Settings.Prices.AutoLoadOnComplete.
- `AppSettings.Prices` (PriceSettings) mit Defaults Provider=None.
- Neuer Settings-Tab "Preise" zwischen "BL-Catalog-Daten" und
  "Statistik" mit RadioButtons fuer Provider, Filter-Dropdowns,
  Korrektur-Inputs (mit Live-Vorschau), Cache-Tage, Auto-Load-Checkbox.
- Schema-Erweiterung: `bl_prices`-Tabelle in BlCacheSchema.sql
  (CREATE TABLE IF NOT EXISTS - automatische Migration).

NICHT in dieser Phase:
- HBPriceToolProvider (kommt wenn das eigene Tool dokumentiert ist).
- Provider-Mocks fuer Unit-Tests (zu aufwaendig fuer den Erstwurf).
- Bulk-Preis-Update fuer alle wartenden Figuren.

#### Phase 8-Erweiterung: Preise im Sortier-Tab (UX-Iteration X.8, 2026-05-03)

Bisher hat der `PriceCalculationService` nur fuer **persistierte** Figuren
funktioniert (`MinifigSummaryDialog`). Mit X.8 sind die Preise auch fuer
gerade gescannte **Pending**-Minifigs sichtbar - in der oberen rechten Box
des Sortier-Tabs (3-Spalten-Layout aus UX#5).

**Cache-Architektur:**
- Bestehende Tabelle `bl_prices` wird wiederverwendet (kein paralleles
  Schema). Repository `IBlCacheRepository` um vier Operationen erweitert:
  `GetCachedPriceWithStaleFlagAsync` (liefert IMMER Eintrag + IsStale-Flag),
  `DeletePriceAsync`, `ClearAllPricesAsync`, `GetPriceCacheCountAsync`.
- Neuer schmaler Service `IBlPriceCacheService` kapselt **Stale-While-
  Revalidate**:
  - Cache-Hit + frisch -> `Source=Cache` sofort.
  - Cache-Hit + stale  -> `Source=Stale` sofort + Hintergrund-Revalidate
    via `Task.Run` (fire-and-forget).
  - Cache-Miss         -> Provider live (`Source=Live`) + Cache-Write.
- **In-Flight-Schutz** ueber `ConcurrentDictionary<key, Task>`:
  parallele Aufrufe fuer denselben Key kollabieren auf einen einzigen
  API-Call. Auch der Refresh-Button ↻ kann beliebig oft geklickt werden
  ohne Doppel-Calls auszuloesen.
- Provider-Doppel-Read (`BricklinkApiPriceProvider` macht intern auch
  Cache-Lookup) ist bewusst akzeptiert - ~1ms Overhead, kein
  Provider-Refactor noetig.

**Settings-Erweiterung:**
- `AppSettings.Prices.BlPriceCacheTtlMinifigDays` (Default 90)
- `AppSettings.Prices.BlPriceCacheTtlPartDays` (Default 90)
- Bestehendes `CacheDays=7` mit `// DEPRECATED ab Phase 8`-Inline-Kommentar
  - wird vom neuen Pfad ignoriert, kann in spaeterer Iteration entfernt
  werden.
- Im "Preise"-Tab der Settings: zwei TTL-Inputs + neuer Cache-Verwaltungs-
  Block mit "Aktuell X Eintraege" + "Preis-Cache leeren"-Button (mit
  IDialogService-Bestaetigung).

**UI-Anbindung (obere rechte Box):**
- Neues `MinifigPriceViewModel` in `HBSort/ViewModels`. Wird vom
  `ScanViewModel` beim Pending-Minifig-Aufbau instanziiert; bei
  `PendingMinifig=null` (Verwerfen/Persistieren) automatisch geleert.
- Layout in `MinifigPriceView.xaml`: 50/50-Split.
  - LINKS: Komplett-Figur (grosser Avg + Min/Max + Listings + "Daten vom
    DD.MM.YYYY"; bei Stale: oranger Hinweis "↻ Update laeuft im Hintergrund").
  - RECHTS: Liste der Subsets als `{Quantity}× {Name}` + Subtotal-Spalte;
    Trennstrich + Summe; bei missing: "{N} Teile ohne Preis".
  - UNTEN: gruener Empfehlungs-Banner ("Komplett verkaufen lohnt sich
    mehr (+X,XX €)" / "Einzelteile lohnen sich mehr" / "etwa gleich");
    10% Schwelle.
- Refresh-Button (↻) oben rechts: ruft
  `IBlPriceCacheService.DeleteForMinifigAsync(blMinifigId, subsetSpecs)`
  und triggert `LoadAsync` neu. Wird waehrend laufendem Load disabled
  (zusaetzlich zum In-Flight-Schutz im Service).
- Sichtbarkeit der Box: nur wenn `HasMinifigPriceInfo=true` (Pending-Minifig
  aktiv). Bei Pending-Part oder kein Pending: Box bleibt leer.
- Untere rechte Box: aktuell leer, Inhalt folgt in spaeterer Iteration.

**Neue Models:**
- `CachedPriceLookup(Price, IsStale)` - Repo-Rueckgabe.
- `PriceLookupOutcome(Price, Source, FetchedAt, ErrorMessage, Notice)` +
  `PriceLookupSource` enum (Cache/Stale/Live/None) +
  `PriceLookupNotice` enum (None/Error/NotConfigured, Default None).

#### Phase 8-Bugfix (2026-05-03) — keine Preise + Binding-Errors

Zwei Bugs im Anschluss an die Phase 8-UI-Anbindung gefixt:

**1. `Cannot convert '<null>' to ImageSource` Trace-Spam.** WPFs
eingebauter `ImageSourceConverter` wirft bei null-Bindings eine
`NotSupportedException`. Trat vor allem in `MinifigDetailView`,
`SortingView`-Karten und der `InventoryListView` auf, sobald eine
neu erkannte Figur noch keine `ImageUrl` hatte.
- Neuer zentraler `HBSort/Converters/NullToImageSourceConverter.cs`
  (Resource-Key `NullToImageSource`, in `App.xaml` registriert).
- An alle 19 `<Image Source="{Binding ImageUrl}"/>`-Bindings in 13
  Views/Dialogen gehaengt (BinDetailDialog, BinOverviewView,
  BsxExportDialog, BuildSuggestionDetailDialog, BuildSuggestionsView,
  DismantleWizardDialog, InventoryListView, MinifigDetailView,
  MinifigSummaryDialog, PartLookupView, SortingView, SupersetsDialog,
  WaitingDetailView).

**2. Stilles Schweigen bei Provider="None".** Mit dem Default-Provider
"None" lieferte die obere rechte Preis-Box weder Daten noch einen
Hinweis - die Box blieb komplett leer und der User wusste nicht,
warum. Zusaetzlich griff ein Provider-Wechsel im Settings-Dialog
erst nach App-Neustart, weil `BlPriceCacheService` den Provider im
Konstruktor cached'te.
- `PriceLookupOutcome` um Notice-Enum erweitert (Default None,
  bestehende Aufrufer brauchen keine Aenderung).
- `BlPriceCacheService.IsProviderConfigured()` prueft live
  `Settings.Current.Prices.Provider`. Bei "None" -&gt; sofortige
  `Notice=NotConfigured`-Outcome mit User-Hinweis "Preise nicht
  verfuegbar - Provider noch nicht eingerichtet. Oeffne Einstellungen
  → Preise und waehle 'BL-API'.". Cache-Reads bleiben aktiv.
- **Wichtig**: der NotConfigured-Check liegt in `GetPriceCoreAsync`
  vor dem `GetLiveWithInFlightGuardAsync`-Aufruf, weil ein
  synchron-fertiger Task in der `GetOrAdd`-Factory den Stale-Eintrag
  im In-Flight-Dict liegen lassen wuerde (`finally`-`TryRemove`
  laeuft VOR dem Add). Ein Test deckt das Verhalten ab
  (`Provider_switch_in_settings_takes_effect_without_recreating_service`).
- `_provider`-Field entfernt, `_providerFactory` wird live abgefragt -
  Settings-Wechsel greifen jetzt sofort, kein Neustart noetig.
- `MinifigPriceViewModel` hebt Outcome-Notices auf View-Properties:
  `HasError` (rotes Banner) vs `HasConfigurationHint` (oranges Banner).
  `MinifigPriceView.xaml` zeigt zwei separate Banner-Reihen damit der
  Konfigurations-Hinweis nicht wie ein echter Fehler aussieht.
- TODO im Code vermerkt: klickbarer "Einstellungen oeffnen"-Button im
  Hinweis-Banner. Braucht einen `INavigationService`, kommt in
  spaeterer Iteration.

#### Phase 8-Bugfix (2026-05-04) — Korrektur-Prozente + PriceColumn in der Live-Box

Bug: Die Live-Preis-Box im Sortier-Tab (UX X.8) hat die User-Settings
gar nicht angewandt:
- `PriceColumn` (min/avg/max/qty_avg) wurde ignoriert - immer hardcoded
  `AvgPrice ?? QtyAvgPrice`.
- `CorrectionMinifigPercent` und `CorrectionPartsPercent` wurden auf
  Min/Avg/Max + Subtotals + Summe nicht angewandt - die Box zeigte
  immer rohe BL-Werte.
- Folge: was die App zeigte unterschied sich vom BL-Wert + von der
  Empfehlung im `MinifigSummaryDialog` (`PriceCalculationService`
  machte es richtig).

Architektur-Fix: gemeinsame statische Helfer-Klasse
`HBSort.Core.Services.PriceMath`:
- `PickValue(PriceResult?, string?)` - Spaltenwahl mit qty_avg-Fallback.
- `ApplyCorrection(decimal?, decimal)` - +/-X% mit kaufmaennischer
  Rundung auf 2 Nachkommastellen, null-safe.
- `ApplyCorrectionOrZero` - bequemer Wrapper fuer Summen-Pfade.

**Beide Konsumenten ziehen jetzt aus `PriceMath`** statt private
Helper zu duplizieren:
- `PriceCalculationService.CalculateForMinifigAsync` (Komplett-
  Workflow, `MinifigSummaryDialog`).
- `MinifigPriceViewModel.ApplyCompleteOutcomeToView` +
  `ApplyPartOutcomeToRow` (Live-Preis-Box im Sortier-Tab).

Damit kann der Pfad nicht mehr auseinanderlaufen.

UI-Aenderungen in `MinifigPriceView`:
- Pro Haelfte ein blauer Hint "Inkl. -10% Korrektur" /
  "Inkl. -15% Korrektur" - leer wenn Korrektur=0.
- Tooltip auf dem Komplett-Preis-Label sowie auf jeder Teile-Zeile:
  "Roh: 12,00 € • Korrigiert: 10,80 €". Tritt nur bei Korrektur != 0
  auf.
- `MinifigPriceViewModel`-Konstruktor kriegt `ISettingsService`;
  `ScanViewModel` reicht es durch.
- `UpdateRecommendation` operiert auf den korrigierten Werten - sonst
  waere die Empfehlung von der User-Korrektur entkoppelt.

GuideType-Plumbing war im Code bereits korrekt (cfg.GuideType wird in
`BricklinkApiPriceProvider` und `BlPriceCacheService` bei jedem Aufruf
live aus den Settings gelesen, das `BlCacheRepository` hat
`guide_type` in allen vier WHERE-/INSERT-Klauseln). Trotzdem zwei
Regressions-Tests dazu in `BlPriceCacheServiceTests`:
`Cache_keys_distinguish_between_GuideType_Sold_and_Stock` und
`Switching_GuideType_misses_old_cache_and_triggers_live_call`.

Tests:
- 15 neue `PriceMathTests` decken alle Spaltenwahl-Pfade + alle
  Korrektur-Prozent-Faelle (positiv/negativ/null/Rundung) ab.
- 2 neue `BlPriceCacheServiceTests` fuer Sold↔Stock.
- Bestehende `PriceCalculationServiceTests` laufen unveraendert
  weiter - `PriceCalculationService` nutzt jetzt `PriceMath` statt
  der lokalen Helfer.

Wo wirken die Settings im Stack? Aktuell:

| Setting | Wirkt in PriceCalculationService | Wirkt in MinifigPriceViewModel |
|---|---|---|
| `Provider` | `IPriceProviderFactory.GetActiveProvider()` (live) | dito (via `BlPriceCacheService`) |
| `GuideType` | `BricklinkApiPriceProvider.GetPriceAsync` + `BlCacheRepository`-Cache-Key | dito |
| `PriceColumn` | `PriceMath.PickValue` | `PriceMath.PickValue` |
| `CorrectionMinifigPercent` | `PriceMath.ApplyCorrection` (linke Seite) | `PriceMath.ApplyCorrection` (Komplett-Figur) |
| `CorrectionPartsPercent` | `PriceMath.ApplyCorrection` (rechte Seite) | `PriceMath.ApplyCorrection` (jede Teile-Zeile) |
| `Region`/`Currency`/`CountryCode` | `BricklinkApiPriceProvider` + Cache-Key | dito |
| `BlPriceCacheTtlMinifigDays`/`...PartDays` | n/a | `BlPriceCacheService.GetPriceCoreAsync` (live, jeder Aufruf) |

Alle Settings werden bei jedem Aufruf live aus
`_settings.Current.Prices` gelesen - kein Singleton-Cache mehr, kein
Restart noetig nach Settings-Wechsel.

#### Phase 8-Bugfix #3 (2026-05-04) — Auto/Manuell pro Bereich + GuideType-Contract

Zwei Aenderungen am Phase-8-System:

**TEIL A: GuideType-Contract abgesichert.** Ueber alle Aufrufer
(`BricklinkApiPriceProvider`, `BlPriceCacheService` Cache+Live,
`PriceCalculationService`, `MinifigPriceViewModel`) wird `cfg.GuideType`
genau einmal pro Lookup gelesen - es gibt nirgendwo eine Schleife oder
parallele Anfragen ueber beide Varianten. Die Beobachtung "beide
Varianten werden abgefragt" stammt vermutlich aus historischen
Cache-Eintraegen (PRIMARY KEY in `bl_prices` enthaelt `guide_type`,
also koexistieren Sold- und Stock-Eintraege fuer dasselbe Item aus
unterschiedlichen Sitzungen). Zwei neue Lock-the-Contract-Tests in
`BlPriceCacheServiceTests` sichern das gegen Regression:
- `Lookup_in_Sold_mode_only_calls_provider_with_Sold_never_with_Stock`
- `Lookup_in_Stock_mode_only_calls_provider_with_Stock_never_with_Sold`

Der `StubProvider` protokolliert dazu pro Aufruf den live aktiven
`Settings.GuideType` (`RecordedMinifigGuideTypes` /
`RecordedPartGuideTypes`).

**TEIL B: Auto vs Manuell pro Bereich.** Die obere rechte Preis-Box
laed nicht mehr automatisch beim Scannen. Stattdessen entscheidet der
User pro Bereich ob Auto oder Manuell:

- Neue Settings: `AppSettings.Prices.AutoLoadCompletePrice` und
  `AutoLoadPartsPrice`, beide vom Typ `PriceLoadMode { Manual, Auto }`,
  Default `Manual` (spart API-Calls).
- Altes `AutoLoadOnComplete`-Bool ist `DEPRECATED` (XML-Doc),
  bleibt in der settings.json fuer Backwards-Compat. Das
  `MinifigSummaryViewModel` triggert sein Auto-Load jetzt aus
  `AutoLoadCompletePrice == Auto`.
- Settings-UI: alter "Preise automatisch laden"-CheckBox raus, neuer
  Block "Preise laden" mit zwei Dropdowns + Erklaerungs-Text.

**MinifigPriceView neu gebaut**: jede Haelfte (Komplett / Einzelteile)
ist eine eigene 4-Zustands-Maschine:

| Zustand | Sichtbar |
|---|---|
| Idle    | "Preis laden"-Button mittig |
| Loading | ProgressBar + Status-Text |
| Loaded  | Preis-Anzeige + ↻-Refresh-Icon oben rechts |
| Issue   | Banner (rot=Fehler, orange=Provider-noch-nicht-eingerichtet) + "Erneut versuchen" |

Sichtbarkeiten ueber `ShowComplete*` / `ShowParts*` computed
Properties am VM, mutually exclusive. Der globale ↻-Header-Button
ist weg - jede Haelfte hat ihr eigenes Refresh-Icon.

**Cache-First-Pfad gilt in beiden Modi gleich** (User-Addendum):
- Auto-Mode: triggert `LoadCompleteCoreAsync` / `LoadPartsCoreAsync`
  direkt im Konstruktor.
- Manual-Mode: wartet auf Klick - der Klick ruft denselben Pfad auf.
- Beide gehen durch `IBlPriceCacheService` (Stale-While-Revalidate):
  Cache-Hit -> kein Provider-Call. Cache-Stale -> Stale + Hintergrund-
  Revalidate. Cache-Miss -> Provider live + Cache-Write.
- ↻-Icon ist explizit Force-Refresh: loescht den passenden Cache-
  Eintrag (per Bereich) ueber neue API-Methoden
  `IBlPriceCacheService.DeleteMinifigPriceAsync(blId)` und
  `DeletePartPricesAsync(specs)`. `DeleteForMinifigAsync` ist jetzt
  eine Convenience ueber die zwei.

**Empfehlungs-Banner** unten: nur sichtbar wenn beide Bereiche
erfolgreich geladen sind (`CompleteHasPrice && PartsHasAnyPrice`).
Solange einer der beiden idle/loading/error ist: keine Empfehlung.

**Tests** (`HBSort.Tests/MinifigPriceViewModelTests.cs`, 10 neue
Tests): Auto-Trigger im Konstruktor, Manual-Click, Cache-Hit verhindert
API-Call (in beiden Modi), Cache-Miss triggert API-Call, Refresh
loescht NUR die jeweilige Bereichs-Spalte aus dem Cache, Recommendation
nur bei beiden Halves geladen. `HBSort.Tests` bekommt eine
ProjectReference auf das WPF-Hauptprojekt damit das VM testbar wird.

### UX-Iteration X.4 ✅ (2026-05-03)

Acht UX-Verbesserungen quer durch die App:

1. **BuildSuggestions ohne Slider**: Mind.-Match-Slider raus; alle baubaren
   Vorschlaege werden gezeigt (sortiert nach Match-% absteigend, Top-N=20).

2. **BL-Wanted-List-Export**: Neuer `IWantedListExportService` + Button
   "Wanted-List exportieren" in der Lagerliste-Header. Dialog mit zwei
   Modi: "Alle wartenden Figuren in eine Datei" (aggregiert) oder "Pro
   Figur eine eigene Datei". Format: BrickLink-Wanted-List-XML
   (`INVENTORY/ITEM` mit ITEMTYPE=P, ITEMID, COLOR, MINQTY).
   Neuer `AppSettings.WantedListExportFolder` mit Fallback auf
   `BsxExportFolder` -> Documents/HBSort-Export.

3. **BuildSuggestion-Klick → Figur anlegen**: Klick auf einen Bauvorschlag
   oeffnet `BuildSuggestionDetailDialog` (Bild, Name, BL-ID, Jahr, Liste
   aller Required-Parts mit Status "Vorhanden in Box X / Fehlt", Bin-Dropdown,
   Notizen). "Figur anlegen" ruft `IMinifigPersistenceService.PersistAndStoreAsync`
   - der bestehende Reverse-Match konsumiert FloatingParts und markiert
   die Figur ggf. direkt als Complete.

4. **MinifigDetailView Scrollbar-Overlap**: Border in der Teile-Liste
   bekommt Padding-Right=18 - Quantity-Spalte rechts ist nicht mehr von
   der ScrollBar verdeckt.

5. **Sortier-Tab auf 3 gleich breite Spalten**: Aus 2 Spalten werden 3
   (jeweils `Width="*"`). Spalte 3 enthaelt zwei leere Borders im
   65/35-Verhaeltnis (Inhalte kommen spaeter). Zweiter vertikaler
   GridSplitter; neuer `AppSettings.WindowState.SplitterColumnRatio2`
   speichert den Anteil der Mittelspalte.

6. **Klickbare Bilder mit grosser Vorschau**: Neuer
   `HBSort.Behaviors.ImageZoom` (Attached Property "IsEnabled") +
   `ZoomOverlayHost` UserControl. `b:ImageZoom.IsEnabled="True"` an einem
   `<Image>` setzt Cursor=Hand und oeffnet bei Klick ein Modal-Overlay
   im MainWindow (kein extra Window -> Multi-Monitor-sicher). Schliessen
   per Klick auf Hintergrund / ESC / X-Button. Eingebaut an allen
   relevanten Image-Stellen (Brickognize-Karten, MinifigDetailView,
   PartLookupView, alle Dialog-Bilder, Lagerliste-Spalte).

7. **MessageBox -> ContentDialog ueber IDialogService**: Neuer
   `HBSort.Services.IDialogService` (`ShowInfoAsync`, `ShowErrorAsync`,
   `ShowConfirmAsync(okText, cancelText)`, `ShowQuestionAsync` =
   "Ja"/"Nein"). Implementierung baut `ModernWpf.Controls.ContentDialog`
   mit Owner=ActiveWindow. 32 von 33 MessageBox-Stellen umgestellt;
   einzige Ausnahme ist der App-Start-Fatal-Pfad in `App.xaml.cs:108`
   (laeuft vor DI-Init). Konvention:
   - Destruktive Aktionen (Loeschen, Cache leeren, Fach leeren) -> "Ja"/"Nein"
   - Nicht-destruktiv -> "OK"/"Abbrechen"
   - Click-Handler die noch sync waren wurden auf `async void` umgestellt
     (kein `.Wait()`/`.Result` -> kein UI-Thread-Block).

8. **Header-/Tab-Bereich modernisiert (Win11-Pivot-Stil)**: Die zwei
   bisherigen Header-Zeilen (Titelleiste + TabControl) sind zu einer
   Header-Zeile zusammengefasst: Logo+Version (links), Pivot-Tabs
   (Mitte), Settings-Button mit Zahnrad-Icon (rechts). Tabs sind
   RadioButtons mit dem neuen `MainTabRadioStyle` (in `App.xaml`) -
   aktiver Tab bekommt einen 3px-Akzentfarben-Indikator unten. Der
   Hauptbereich nutzt jetzt ContentControl + Visibility-Bindings statt
   TabControl, damit der TabControl-Header nicht zusaetzlich erscheint.
   Neuer `MainViewModel.MainTabIndex` (0=Sortieren, 1=Lagerliste); nicht
   persistiert.

### UX-Iteration X.5 ✅ (2026-05-03) — Floating-zu-Pending-Transfer

Pro Teil in der `MinifigDetailView` (Pending-Minifig vor "In Fach legen")
gibt es jetzt einen Button **"📦 Aus Fach"** + Bin-Label-Hinweis, sobald
ein passender FloatingPart im Pool existiert. Klick reduziert den
FloatingPart sofort um 1 (loescht wenn 0), erhoeht `QuantityCollected`
am Pending-Part und triggert einen Toast.

- Neuer `IFloatingPartTransferService` (HBSort.Core/Services):
  `FindFirstMatchAsync(blPartNo, blColorId)` und
  `TransferOneAsync(blPartNo, blColorId, targetMinifigDescription)`.
  FIFO-Auswahl nach `AddedAt`. Bin-Freigabe-Check (analog zu
  `IStorageBinService.GetFreeAsync`: leer = keine FloatingParts und
  keine wartenden Minifigs). Schreibt `ScanEvent` mit neuem Type
  `FloatingPartTransfer`. Race-Condition-sicher via TransferResult.
- `PendingPartViewModel` erweitert um `QuantityCollected` (int statt
  nur `IsCollected` bool), `HasMatchingFloatingPart`,
  `MatchingFloatingPartBinLabel`, `MatchingFloatingPartQuantity`,
  `IsTransferButtonVisible` (computed), `QuantityProgressLabel` "(2/3)".
- `ScanViewModel` ruft `RefreshFloatingMatchesForPendingAsync` beim
  Aufbau der Pending-Figur und nach jedem Transfer auf.
- Audit-Trail: jeder Transfer schreibt einen ScanEvent
  (`Type=FloatingPartTransfer`, `RecognizedId=BL-Part-No`,
  `ResultDescription` mit Quell-Fach + Ziel-Beschreibung).
- **Bewusste Spec-Entscheidung**: Wenn der User nach Transfer die
  Pending-Figur verwirft, bleibt der FloatingPart trotzdem reduziert -
  das Teil ist physisch beim User. Kein Rollback.

### UX-Iteration X.5b ✅ (2026-05-03) — ImageZoom-Lueckenschluss

Drei Image-Stellen die in UX#6 uebersehen waren bekamen
`b:ImageZoom.IsEnabled="True"` nachgereicht:
- `BinDetailDialog.xaml` zweite Bild-Stelle (FloatingParts-Liste, 50px)
- `BuildSuggestionsView.xaml` Bauvorschlag-Karten (Bild = Zoom, Rest =
  Detail-Dialog via Border-Click)
- `PartLookupView.xaml` "Moegliche BL-Figuren"-Liste (48px)

Stand: **17 von 21** sichtbaren `<Image>`-Elementen sind zoombar.
Bewusst ohne Zoom: Logo (MainWindow, SettingsWindow), Webcam-Live-Frame
(SortingView), Overlay-Bild (ZoomOverlayHost selbst).

### UX-Iteration X.9 ✅ (2026-05-03) — Integrierte Hilfe + Tooltips global

Zwei zusammenhaengende Ergaenzungen fuer User-Onboarding:

**Hilfe-Tab als dritter Haupt-Tab.** F1 oeffnet jetzt die Hilfe statt
einen Toast zu zeigen. Implementierung:
- `Markdig` + `Markdig.Wpf` als NuGet-Pakete; rendern Markdown direkt
  in ein `FlowDocument`, das in einem `FlowDocumentScrollViewer` in
  `HelpView` haengt.
- `HBSort/Resources/Help/index.json` listet alle Kapitel
  (Title/FileName/Order); 7 Markdown-Dateien (`01-erste-schritte.md`
  bis `07-faq.md`) liefern den Inhalt.
- Resources sind als WPF-Resource (Build-Action `Resource`) eingebettet
  und ueber `pack://application:,,,/Resources/Help/...` adressierbar -
  damit kann Markdig.Wpf relative Bild-Pfade aus den Markdown-Dateien
  direkt aufloesen.
- `IHelpContentService` / `HelpContentService` kapseln das Resource-
  Laden; bei Fehler wird ein Markdown-Fehler-Hinweis statt Crash
  zurueckgegeben.
- `HelpViewModel` haelt Kapitelliste + SelectedChapter; bei Auswahl-
  Wechsel wird das FlowDocument neu gebaut. Auto-Select des ersten
  Kapitels beim Tab-Wechsel.
- Layout in `HelpView.xaml`: 240px-Sidebar links (ListBox) + Flow-
  DocumentScrollViewer rechts. Hyperlink-RequestNavigate-Handler im
  Code-Behind oeffnet Links via `Process.Start` im OS-Browser.
- Such-Funktion absichtlich weggelassen (TODO im VM); kommt in einer
  spaeteren Iteration falls gewuenscht.

**Tooltips global an/ausschaltbar.** Implementierung als DynamicResource-
Anker, ohne `OverrideMetadata`-Hack:
- `AppSettings.ShowTooltips` (bool, Default true).
- `ITooltipsService` schreibt den bool in
  `Application.Current.Resources["TooltipsEnabled"]`. `App.xaml` deklariert
  den Resource-Key als `<sys:Boolean>True</sys:Boolean>`.
- Alle 11 Wurzel-Windows (MainWindow + 10 Dialoge) haben am Window-
  Element `ToolTipService.IsEnabled="{DynamicResource TooltipsEnabled}"`.
  WPFs ToolTipService.IsEnabled ist `Inheritable` - Aenderung am
  Window-Root propagiert ueber den ganzen Visual-Tree, also auch in
  alle UserControls und ContentDialogs.
- `SettingsWindow` Allgemein-Tab hat eine CheckBox "Tooltips anzeigen".
  Der Toggle wirkt sofort (live), ohne Speichern-Klick und ohne
  Neustart - persistiert wird beim regulaeren Save.
- Bestehende ~12 inkonsistente Tooltips harmonisiert (Du-Form, ASCII-
  Umlaute, vollstaendige Saetze).
- ~30 neue Tooltips ergaenzt: Tab-Header, Status-Badges (per
  `StatusTooltip`/`ProgressTooltip`-Properties am InventoryRowItem),
  Color-Swatches (per `ColorName`), Filter-Optionen, Bin-Details,
  Webcam-Bild, Brickognize-Karten, Bottom-Right-TabItems im Sortier-Tab.

### UX-Iteration X.11 ✅ (2026-05-04) — Settings-Konsolidierung + Density-Removal

Settings-Dialog von 9 auf 8 Tabs konsolidiert; Themen sind nicht mehr
fragmentiert; Darstellungsdichte komplett entfernt.

**Neue Tab-Reihenfolge** (`HBSort/Views/SettingsWindow.xaml`):

| # | Tab | Inhalt | War vorher |
|---|---|---|---|
| 1 | **Erkennung** | Kamera, Score-Schwellen, Timing, Sound | "Allgemein" (oberer Teil) |
| 2 | **Darstellung** | Tooltips-Toggle | "Allgemein" (Density-Block + Tooltips) |
| 3 | **BrickLink** | API-Zugang + API-Nutzung + Preise + Catalog-Daten | "BrickLink-API" + "BL-Catalog-Daten" + "Preise" |
| 4 | Lagerfaecher | unveraendert | unveraendert |
| 5 | **Cache** | Bild-Cache + BL-Daten-Cache + Preis-Cache | "Bild-Cache" + Cache-Sektion in BrickLink-API + Cache-Sektion in Preise |
| 6 | Export | unveraendert | unveraendert |
| 7 | Statistik | unveraendert | unveraendert |
| 8 | Info | unveraendert | unveraendert |

**BrickLink-Tab** (Sektion 3) ist scrollbar mit vier optisch getrennten
Sektionen (Separators): API-Zugang, API-Nutzung, Preise, Catalog-Daten.
Sektion-Headers FontSize=15, Sub-Headers innerhalb FontSize=13.

**Cache-Tab** (Sektion 5) hat drei Sektionen: Bild-Cache (Limit-Auswahl
inkl. Custom-Wert + Optionen), BL-Daten-Cache (Stats + Stale-/Cache-
Leeren-Buttons), Preis-Cache (TTL-Felder + Eintraege-Anzeige + Leeren).

**Darstellungsdichte komplett entfernt**:
- `HBSort/Resources/DensityCompact.xaml` + `DensityNormal.xaml` +
  `DensityComfortable.xaml` geloescht.
- `HBSort/Services/IUiDensityService.cs` + `UiDensityService.cs`
  geloescht; DI-Registrierung in `App.xaml.cs` raus.
- `App.xaml.cs::ApplyStoredUiDensityAsync` raus.
- `SettingsViewModel`: `_uiDensity`-Field, ctor-Parameter,
  `SelectedDensity`/`IsDensity*`/`ApplyUiDensityCommand` raus.
- `SettingsWindow.xaml.cs`: `DensityCompact_Click` /
  `DensityNormal_Click` / `DensityComfortable_Click`-Handler raus.
- 65 `DynamicResource`-Stellen in 5 Views (`MainWindow`,
  `PartLookupView`, `MinifigDetailView`, `SortingView`, `BinOverviewView`)
  durch feste Werte aus dem **Compact**-Profil ersetzt:
  - Schriften: Header=13, Body=11, Detail=10, Micro=9
  - Spacing: BorderPadding=8, CardPadding=6
  - Bilder: PartImage=48, MinifigImage=96, ColorSwatch=14

**Migration alte settings.json**:
- `AppSettings.UiDensity` (string) bleibt als `[DEPRECATED]`-XML-Doc
  im POCO (Default jetzt "Compact"). Wird nirgends mehr gelesen.
  Alte Werte ("Normal" / "Comfortable" aus frueheren Versionen) werden
  beim Laden ignoriert; das Compact-Layout greift unabhaengig vom
  Settings-Wert. Kann in spaeterer Iteration entfernt werden.

**Kein Datenverlust**: alle Settings-Properties (Camera-Index,
Schwellwerte, Korrekturen, BL-Tokens, BSX-Folder, ...) bleiben
unveraendert in `AppSettings`. Nur die **Anordnung** im Settings-UI
hat sich geaendert.

## Wichtige Hinweise

### Hilfe-System (UX X.9)

Alle Hilfe-Inhalte liegen unter `HBSort/Resources/Help/`:

```
HBSort/Resources/Help/
├── index.json              ← Kapitelliste (Title/FileName/Order)
├── 01-erste-schritte.md
├── 02-sortier-workflow.md
├── 03-lagerverwaltung.md
├── 04-export-verkauf.md
├── 05-einstellungen.md
├── 06-tipps.md
├── 07-faq.md
└── images/                 ← optionale Screenshots (PNG/JPG)
```

**Inhalt erweitern**:
1. Markdown-Datei in `Resources/Help/` anlegen.
2. Eintrag in `index.json` mit `title`, `fileName`, `order` ergaenzen.
3. Build (Resource-Glob im csproj packt automatisch alles ein).

**Konventionen**:
- Tonfall: freundlicher Du-Form-Anfaengerton, keine Insider-Jargon.
- Umlaute: ASCII (`ae/oe/ue/ss`), wie ueberall im Codebase.
- Bilder: `Resources/Help/images/foo.png`, im Markdown als
  `![Alt](images/foo.png)` referenzieren - Markdig.Wpf loest den
  Pfad ueber pack-URIs auf.
- Niemals Funktionen erfinden, die es nicht gibt - nur dokumentieren
  was tatsaechlich in der App existiert.

**Struktur in der App**:
- `HelpView.xaml` + `HelpViewModel` + `IHelpContentService`/
  `HelpContentService` kapseln das Laden + Rendern.
- `MainViewModel.MainTabIndex == 2` zeigt den Hilfe-Tab.
- F1-KeyBinding in `MainWindow.xaml` ruft `OpenHelpCommand` auf,
  der `MainTabIndex = 2` setzt.

### Tooltips + ShowTooltips-Schalter (UX X.9)

Globaler Schalter unter `AppSettings.ShowTooltips` (Default true).
Architektur:
- `ITooltipsService` schreibt bool in
  `Application.Current.Resources["TooltipsEnabled"]`.
- Alle 11 Window-XAMLs haben
  `ToolTipService.IsEnabled="{DynamicResource TooltipsEnabled}"` am
  Wurzel-Element. ToolTipService.IsEnabled inheritet entlang des
  Visual-Tree -> ein Wert pro Window deckt alle Children inkl.
  ContentDialogs.
- SettingsViewModel.OnShowTooltipsChanged ruft live
  `ITooltipsService.SetEnabled(value)` auf - keine Neustart noetig.

**Stil-Konvention fuer neue Tooltips**:
- Du-Form, Verb-Anfang, max. ~80 Zeichen.
- ASCII-Umlaute, Punkt am Ende.
- Aktiv: "Loescht alle Eintraege ..." - nicht "Wird geloescht ...".
- Color-Swatches kriegen `ToolTip="{Binding ColorName}"`.
- Status-Badges: ausfuehrlicher Tooltip ueber computed Property im VM
  (siehe `InventoryRowItem.StatusTooltip` als Vorlage).

### Async-First
Alle API-Calls und DB-Zugriffe sind `async`. UI darf nie blockieren.

### Caching-Philosophie
- BL-API ist die Source-of-Truth, aber langsam (200-800ms)
- bl_cache.db macht den Lookup nach erstem Mal so schnell wie catalog.db
- Cache-Stale = 90 Tage, dann erneuter API-Call
- Bei BL offline: Cache wird trotzdem genutzt (mit Hinweis)

### Token-Sicherheit
- DPAPI gebunden an Windows-User
- Bei User-Wechsel auf gleichem PC: Tokens nicht entschluesselbar
- Bei PC-Wechsel: Tokens manuell neu eingeben
- Klartext-Tokens NIEMALS in Logs (auch nicht Debug-Level)

### Fehler-Behandlung
- BL-API offline → Cache-Fallback + Toast
- Tokens fehlen/falsch → freundliche Fehlermeldung in Settings
- Rate Limit erreicht → Cache-Fallback + Warning-Toast
- Brickognize liefert BL-ID die in BL nicht existiert → Toast "Item nicht
  in BL Catalog"

### NICHT in Phase 1-7
- Anbindung BL-Preis-Tool (Phase 8)
- Verkaufs-Workflow (Phase 8+)
- Mobile/Touch-Optimierung
- Cloud-Sync
- Multi-User
- Vollautomatisches Update (nur Download)
- Internationalisierung (UI nur Deutsch)

## Offene Fragen / Annahmen

Wenn beim Implementieren etwas unklar ist:
- **Triff eine pragmatische Annahme**, kommentiere sie als Code-Kommentar
- Liste die getroffenen Annahmen am Ende jeder Phase im Chat auf
- Ich entscheide dann, ob die Annahme bleibt oder geändert werden soll
