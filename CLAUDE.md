# HB-Sort (Klemmbaustein-Sortier-Werkzeug)

## Migrations-Notiz PROMPT 6 (2026-05-02)

Rebrickable komplett entfernt. catalog.db, ICatalogService,
CatalogService, ICatalogImporter, CatalogImporter, IMinifigLookupService,
MinifigLookupService und SplashWindow geloescht. seed-data/*.zip und
HBSort.Core/Resources/CatalogSeed/*.zip raus (~16.6 MB Code-Diet).

Stammdaten kommen jetzt ausschliesslich aus `bl_cache.db`
(BrickStore-Bulk-Import). Der ColorMatch-Pfad nutzt `bl_colors`
direkt; Brickognize-Color-id wird als BL-Color-ID interpretiert
(validiert 2026-05-02). `ColorMapping.cs` bleibt im Code als
generische RB↔BL-Tabelle, wird aber nicht mehr im Hauptfluss genutzt.

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
Werden nicht mehr aktiv genutzt. ColorMapping bleibt als generische
RB↔BL-Tabelle im Code (z.B. fuer kuenftige Reverse-Konvertierungen),
ist aber nicht mehr Teil des Hauptflusses.

URL-Patterns: siehe **`BRICKOGNIZE_API.md`**.

## ColorMapping.cs

Die generierte `ColorMapping.cs` (Rebrickable ↔ BrickLink) bleibt als
statische Tabelle im Code, wird aber im Hauptfluss nicht mehr genutzt.

PROMPT 6 (2026-05-02): Validierung hat ergeben, dass Brickognize in der
"id"-Spalte von `colors[]` BL-Color-IDs direkt liefert (id=5 = Red,
was BL-ID Red ist). Daher kein RB→BL-Mapping mehr noetig.

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
5. **NEU:** Farb-Mapping via ColorMapping.cs zu BL-Color-ID
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
│   │   ├── ColorMapping.cs (statisch generiert)
│   │   │
│   │   ├── IBricklinkClient / BricklinkClient.cs ★ NEU
│   │   │     OAuth1, BricklinkSharp-Wrapper
│   │   ├── IBricklinkTokenStorage / BricklinkTokenStorage.cs ★ NEU
│   │   │     DPAPI-Encrypt/Decrypt der Tokens
│   │   ├── IBlCatalogService / BlCatalogService.cs ★ NEU
│   │   │     Cache-First-Lookup gegen bl_cache.db, Fallback BL-API
│   │   ├── IBlCacheRepository / BlCacheRepository.cs ★ NEU
│   │   │     Direkt-SQLite-Zugriff auf bl_cache.db
│   │   │
│   │   │ (PROMPT 6 2026-05-02: ICatalogService, CatalogService,
│   │   │  ICatalogImporter, CatalogImporter, IMinifigLookupService und
│   │   │  MinifigLookupService entfernt.)
│   │   │
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
├── HBSort.Build/            (DEPRECATED - wird entfernt)
│
└── HBSort.Tests/            (xUnit)
    ├── BricklinkClientTests.cs ★ NEU
    ├── BlCatalogServiceTests.cs ★ NEU
    ├── BlCacheRepositoryTests.cs ★ NEU
    └── ... (bestehende Tests)
```

## Refactoring-Phasen (NEU)

Wir setzen das Refactoring in 4 Etappen um:

### Phase R1 – BL-Tokens & Auth (Settings)
- BricklinkSharp NuGet-Paket einbinden
- `IBricklinkTokenStorage` mit DPAPI-Encrypt/Decrypt
- Settings-Tab "BrickLink-API":
  - 4 PasswordBox-Felder (ConsumerKey, ConsumerSecret, TokenValue, TokenSecret)
  - "Tokens speichern" Button → DPAPI-Encrypt → settings.json
  - "Verbindung testen" Button → testet GetItem(Minifig, "arc007")
  - Status-Anzeige (Tokens-OK + IP-Whitelist OK + Last-Test-Result)
- `IBricklinkClient` als Wrapper um BricklinkSharp:
  - Lazy-Init: erst initialisieren wenn Tokens vorhanden
  - Bei fehlenden Tokens: deutliche Exception "Tokens nicht konfiguriert"
- **Ziel**: User kann Tokens eingeben + Test-Button funktioniert

### Phase R2 – BL-Cache + BlCatalogService
- `bl_cache.db` mit Schema anlegen (Microsoft.Data.Sqlite)
- `IBlCacheRepository`: CRUD auf bl_cache-Tabellen
- `IBlCatalogService` mit Methoden:
  - `GetMinifigDetailsAsync(string blMinifigId)` → MinifigDetails (gecached)
  - `GetMinifigPartsAsync(string blMinifigId)` → List<BlSubset> (gecached)
  - `GetPartDetailsAsync(string blPartNo)` → PartDetails
  - `GetColorsAsync()` → komplette BL-Color-Liste (1x holen, lebenslang gecached)
  - Cache-Strategie: Stale-While-Revalidate
- **Ziel**: Service kann eine Minifigur lookuppen, Daten landen im Cache,
  zweiter Call kommt aus Cache

### Phase R3 – Phase 3 final (Modus A komplett)
- ScanViewModel: Brickognize-BL-ID → BlCatalogService aufrufen
- MinifigDetailView mit Teileliste aus BL-Subsets
- Bilder via BricklinkImageProvider (war schon da)
- Reverse-Match Floating-Parts
- TrackedMinifig speichert BL-ID
- **Ziel**: Test mit Arctic-Forscher (arc007), Deathstroke-Minifig sichtbar
  mit Teileliste

### Phase R4 – Cleanup ✅ (PROMPT 6, 2026-05-02)
- catalog.db.csproj-Embedded-Resources ENTFERNT
- ICatalogService / CatalogService / ICatalogImporter / CatalogImporter
  GELOESCHT
- IMinifigLookupService / MinifigLookupService GELOESCHT
- SplashWindow / SplashViewModel (Catalog-Erstinit) GELOESCHT
- seed-data/ + HBSort.Core/Resources/CatalogSeed/ GELOESCHT (~16.6 MB)
- ColorMatch nutzt bl_colors statt catalog.db
- Brickognize-Color-id wird direkt als BL-Color-ID interpretiert
- HBSort.Build bleibt als 3-Zeilen-Stub (keine Funktion mehr)

## Bisherige Phasen 1-2.5 (bleiben gueltig)

✅ Phase 1 - WPF-Grundgeruest, Webcam, Settings, Tray
✅ Phase 1.5 - Catalog-Import (in PROMPT 6 komplett entfernt)
✅ Phase 2 - Brickognize, ID-Resolver, ColorMapping
✅ Phase 2.5 - BL-Bilder Hybrid, Persistent Cache, LRU

## Phase 4-8 (unveraendert in der Logik, aber mit BL-IDs)

### Phase 4 – Lagerfach-Verwaltung

**Ziel**: User kann Lagerfaecher anlegen, ein Fach pro gescannter
Minifigur zuweisen und mehrere Figuren + Floating-Parts pro Fach
verwalten.

#### Datenmodell (bereits in CLAUDE.md User-Daten-Schema)

`StorageBin`:
- Id (int)
- Label (string, eindeutig) - z.B. "Box 01", "Schale A", "Box-1-rot"
- CreatedAt (DateTime)
- FreedAt (DateTime?) - gesetzt wenn User explizit "Fach leeren" klickt
- Notes (string?, optional vom User)

#### Bulk-Create-Dialog

Layout:
```
+------------------------------------------------+
|  Lagerfaecher anlegen                          |
+------------------------------------------------+
|  Praefix:    [Box ]                            |
|                                                |
|  [Erweitert v]                                 |
|    Suffix (optional): [    ]                   |
|                                                |
|  Typ:        (•) Zahlen   ( ) Buchstaben       |
|                                                |
|  Bereich:    Start [1   ]  Ende [20  ]         |
|                                                |
|  Padding:    [3] Stellen  (nur bei Zahlen)     |
|              0 = ohne Padding                  |
|                                                |
|  ----                                          |
|  Vorschau (3 von 20 Beispielen):               |
|  -> Box 001                                    |
|  -> Box 002                                    |
|  -> Box 003                                    |
|  ... 17 weitere bis Box 020                    |
|                                                |
|  Es werden 20 Lagerfaecher angelegt.           |
|                                                |
|              [Abbrechen]  [Anlegen]            |
+------------------------------------------------+
```

**Logik:**

- **Zahlen**: Start/Ende sind Integers. Padding 0 = wie eingegeben,
  Padding N = mit fuehrenden Nullen auf N Stellen (z.B. Padding 3 →
  "001", "099", "100"). Padding nicht relevant fuer Zahlen >= Padding-Stellen.
- **Buchstaben**: Start/Ende sind Strings (A-Z, AA-ZZ, AAA-ZZZ usw.).
  System erkennt Laenge und zaehlt alphabetisch hoch. Bei
  unterschiedlich langen Start/Ende (z.B. Start "Z", Ende "BB") wird
  korrekt fortgesetzt: Z, AA, AB, ..., AZ, BA, BB.
- **Suffix**: optional, wird einfach hinten angefuegt. "Box-001-rot",
  "Box-002-rot" usw.
- **Vorschau**: zeigt erste 3 + Hinweis "X weitere", aktualisiert sich
  live waehrend der Eingabe.
- **Validation**:
  - Start <= Ende
  - Praefix kann leer sein (rein numerische Bezeichnungen erlaubt)
  - Bei zu vielen (>1000) Faechern: Bestaetigungs-Dialog
  - Existierende Labels: Konflikt-Pruefung, Bulk-Anlegen mit Duplikat
    nicht moeglich, Liste der Konflikte anzeigen

**Implementierung Buchstaben-Increment:**
```csharp
// "A" -> "B", "Z" -> "AA", "AZ" -> "BA", "ZZ" -> "AAA"
public static string IncrementAlpha(string s)
{
    if (string.IsNullOrEmpty(s)) return "A";
    var chars = s.ToUpperInvariant().ToCharArray();
    for (int i = chars.Length - 1; i >= 0; i--)
    {
        if (chars[i] < 'Z')
        {
            chars[i]++;
            return new string(chars);
        }
        chars[i] = 'A';
    }
    return "A" + new string(chars);
}
```

#### Einzeln-Anlegen-Dialog

Einfacher: nur ein Textfeld + OK/Abbrechen. Validation: Label nicht leer,
nicht bereits vorhanden.

#### BinManagerView (Settings-Tab "Lagerfaecher")

Liste aller Faecher mit:
- Label
- Status: Frei (gruen) / Belegt: X Figuren + Y Floating-Parts (orange)
- CreatedAt
- Aktionen: "Umbenennen", "Fach leeren" (mit Bestaetigung), "Loeschen"
  (nur moeglich wenn frei)

Buttons: "+ Bulk anlegen" / "+ Einzeln anlegen"

#### Lagerfach-Auswahl beim Minifigur-Scan

In der MinifigDetailView (statt Phase-4-Platzhalter):

```
Lagerfach: [Box 03 (frei)         v]   [Verwerfen]  [In Fach legen]

Dropdown-Inhalt:
  > Box 03 (frei)              ← vorausgewaehlt: naechstes freies
    Box 04 (frei)
    Box 05 (frei)
    ─────────                  ← Trenner
    Box 01 (Arctic-Forscher)   ← grau, belegt
    Box 02 (Stormtrooper, 2 F) ← grau, belegt
```

**Workflow "In Fach legen"-Klick:**

1. Wenn ausgewaehltes Fach FREI: direkt speichern
2. Wenn ausgewaehltes Fach BELEGT: Bestaetigungs-Dialog
   ```
   "Box 01" ist bereits belegt mit:
   - Arctic-Forscher (3 Teile)
   
   Trotzdem die neue Figur (Train-Worker) dort ablegen?
   [Abbrechen] [Trotzdem ablegen]
   ```
3. Bei Bestaetigung: TrackedMinifig + TrackedMinifigPart in userdata.db
   speichern, mit StorageBinId = ausgewaehltes Fach
4. Reverse-Match: existierende Floating-Parts mit passenden 
   BricklinkPartNo + BricklinkColorId werden der neuen Figur 
   zugeordnet (QuantityCollected hochgezaehlt)
5. Toast "Figur 'XYZ' in Box 01 abgelegt (2 Teile bereits vorhanden)"

#### Fach-Frei-Status

- Lagerfaecher bleiben **belegt** auch wenn alle Figuren COMPLETE/
  DISMANTLED/SOLD sind
- Nur durch expliziten Klick "Fach leeren" werden sie frei
- Beim Fach-Leeren: alle TrackedMinifigs mit StorageBinId = X werden
  auf StorageBinId=NULL gesetzt (sie bleiben in der DB, aber ohne Fach)
- Floating-Parts werden ebenfalls aus dem Fach geloescht
- StorageBin.FreedAt wird auf jetzt gesetzt

#### Wartende-Figuren-Liste (rechte Spalte des Hauptfensters)

Zeigt alle TrackedMinifig mit Status=WAITING, gruppiert nach Lagerfach:

```
WARTENDE FIGUREN

▼ Box 01
  [Bild] Arctic-Forscher      (2 von 3 Teilen)
  [Bild] Stormtrooper         (1 von 5 Teilen)

▼ Box 03
  [Bild] Train-Worker         (0 von 5 Teilen)

▼ (ohne Fach)
  [Bild] Sammler-Figur        (3 von 4 Teilen)
```

Click auf eine Zeile: oeffnet die Detail-Ansicht der Figur (read-only,
in Phase 6 spaeter editierbar).

#### Tests

- StorageBin Bulk-Anlegen mit verschiedenen Padding/Praefix/Suffix
- Buchstaben-Increment: A→B, Z→AA, AZ→BA, ZZ→AAA
- Konflikt-Pruefung beim Bulk-Anlegen
- Lagerfach-Auswahl + Speichern eines TrackedMinifig
- Reverse-Match Floating-Parts beim Speichern
- Fach-Leeren setzt FreedAt + entfernt Bin-Zuweisung der Minifigs
- Wartende-Figuren-Liste gruppiert korrekt

### Phase 5 – Matching-Logik (Modus B)
[wie gehabt, MatchingService nutzt BL-Part-No + BL-Color-ID]

### Phase 6 – Komplettierung & Statistik
[wie gehabt]

### Phase 7 – Polish, BSX-Export, Build
[BSX-Export ist jetzt einfacher, da BL-IDs schon da sind]

### Phase 8 – BL-Price-Tracker-Anbindung
[siehe vorige Doku]

## Wichtige Hinweise

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
