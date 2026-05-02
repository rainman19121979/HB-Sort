# Brickognize API - Referenz fuer dieses Projekt

Diese Datei dokumentiert die Brickognize-API so, wie wir sie nutzen werden.
Quelle der Erkenntnisse: Reverse-Engineering des offiziellen
brickognize-mcp Servers (https://github.com/NazarLysyi/brickognize-mcp)
sowie der OpenAPI-Doku unter https://api.brickognize.com/docs.

## Endpoints

| Endpoint | Zweck | Wann nutzen |
|---|---|---|
| `GET  /health/` | Health-Check | Beim App-Start, um zu prüfen ob der Service erreichbar ist |
| `POST /predict/` | Generische Erkennung | Wenn unklar, ob Teil oder Figur (Fallback) |
| `POST /predict/parts/?predict_color=true` | Nur Teile, **mit Farberkennung** | Modus B: Einzelteil-Scan |
| `POST /predict/figs/` | Nur Minifiguren | Modus A: Figur-Scan |
| `POST /predict/sets/` | Nur Sets | Wird in diesem Projekt nicht genutzt |

**Wichtig:** Den spezifischen Endpoint nutzen, nicht den generischen, sobald
der Modus klar ist - die Genauigkeit ist deutlich besser, weil das Modell
gezielt sucht.

**Base URL:** `https://api.brickognize.com`

## Request-Format

- Methode: `POST`
- Content-Type: `multipart/form-data`
- Form-Feldname: **`query_image`** (genau dieser Name!)
- Wert: das Bild als Datei (JPEG, PNG, oder WebP)
- Authentication: keine
- Timeout-Empfehlung: 60 Sekunden

### C#-Beispiel (HttpClient)

```csharp
using var content = new MultipartFormDataContent();
using var imageStream = File.OpenRead(imagePath);
var imageContent = new StreamContent(imageStream);
imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
content.Add(imageContent, "query_image", Path.GetFileName(imagePath));

var response = await httpClient.PostAsync(
    "https://api.brickognize.com/predict/figs/",
    content,
    cancellationToken);
response.EnsureSuccessStatusCode();
var json = await response.Content.ReadAsStringAsync(cancellationToken);
```

## Response-Format

```json
{
  "listing_id": "abc123",
  "bounding_box": {
    "left": 10,
    "upper": 20,
    "right": 110,
    "lower": 120,
    "image_width": 640,
    "image_height": 480,
    "score": 0.95
  },
  "items": [
    {
      "id": "3001",
      "name": "Brick 2 x 4",
      "img_url": "https://cdn.brickognize.com/...",
      "external_sites": [
        { "name": "BrickLink",   "url": "https://www.bricklink.com/..." },
        { "name": "Rebrickable", "url": "https://rebrickable.com/..." },
        { "name": "BrickOwl",    "url": "https://www.brickowl.com/..." }
      ],
      "category": "Brick",
      "type": "part",
      "score": 0.9
    }
  ],
  "colors": [
    { "id": "0",  "name": "Black", "score": 0.85 },
    { "id": "4",  "name": "Red",   "score": 0.10 }
  ]
}
```

### Feld-Bedeutungen

- **`listing_id`**: Eindeutige ID dieser Erkennungs-Session bei Brickognize.
  Sollten wir mit-loggen, falls man sich später bei Piotr Rybak melden muss.
- **`bounding_box`**: Wo im Bild Brickognize das Objekt erkannt hat. Können
  wir später nutzen, um im Live-Bild ein Rechteck einzublenden ("Aha, das
  hier hat es erkannt").
- **`items`**: Liste der Treffer, **bereits absteigend nach Score sortiert**.
  Top-Treffer ist `items[0]`.
- **`items[].id`**: Die Item-ID. Format ist abhaengig vom Typ - **wichtig:
  zur sicheren Aufloesung immer `external_sites` verwenden, NICHT raten**.
- **`items[].type`**: `"part"`, `"set"`, `"fig"`, oder `"sticker"`. Bestimmt
  unsere Modus-Erkennung.
- **`items[].score`**: Konfidenz 0.0 bis 1.0. Faustformel:
  - > 0.85: sehr sicher, automatisch akzeptieren
  - 0.5 - 0.85: User-Bestaetigung mit Top-3-Auswahl
  - < 0.5: "nicht sicher erkannt", manuelle Eingabe anbieten
  (Schwellwerte sind konfigurierbar in den Settings.)
- **`items[].external_sites`**: Liste von Links zu Drittanbieter-Katalogen.
  **Hier kommen wir an die Rebrickable- UND BrickLink-IDs.**
- **`colors`**: Nur beim `/predict/parts/?predict_color=true` Endpoint vorhanden.
  Liste der wahrscheinlichsten Farben mit Score.

## ID-Aufloesung aus external_sites (KRITISCH)

Statt zu raten, in welchem Format `items[].id` ist, **parsen wir die URLs in
`external_sites`** und extrahieren alle bekannten IDs:

### URL-Patterns (zu validieren beim ersten echten Test)

```
BrickLink Part:
  https://www.bricklink.com/v2/catalog/catalogitem.page?P=3001
  → BL-Part-ID = "3001"  (am Parameter "P=")

BrickLink Minifig:
  https://www.bricklink.com/v2/catalog/catalogitem.page?M=sw0001
  → BL-Minifig-ID = "sw0001"  (am Parameter "M=")

BrickLink Set:
  https://www.bricklink.com/v2/catalog/catalogitem.page?S=75300-1
  → BL-Set-ID = "75300-1"  (am Parameter "S=")

Rebrickable Part:
  https://rebrickable.com/parts/3001/...
  → part_num = "3001"

Rebrickable Minifig:
  https://rebrickable.com/minifigs/fig-001549/...
  → fig_num = "fig-001549"

Rebrickable Set:
  https://rebrickable.com/sets/75300-1/...
  → set_num = "75300-1"

BrickOwl:
  (analog, BoID extrahieren falls fuer das Preis-Tool gebraucht)
```

### Empfohlener C#-Service

```csharp
public record ExternalIds(
    string? RebrickableId,    // fig-XXXXXX  oder  part_num
    string? BricklinkId,      // sw0001  oder  3001
    string? BrickOwlId
);

public interface IExternalIdResolver
{
    ExternalIds Resolve(IEnumerable<ExternalSite> sites, BrickognizeItemType type);
}
```

Implementierung: einfache Regex pro Site-Name. Jede unbekannte URL wird
geloggt (Warning), damit wir neue Patterns ergaenzen koennen.

**Annahme bis zum ersten echten Test:** `items[].id` ist fuer Minifiguren das
BrickLink-Format. Bestaetigen wir aber durch das Loggen der ersten 3-5
echten Responses in Phase 2.

## Test-Strategie fuer Phase 2

1. Drei verschiedene Bilder bereithalten:
   - Eine bekannte Minifigur (z.B. Stormtrooper)
   - Ein bekanntes Teil (z.B. 2x4 Stein)
   - Ein "schwieriges" Teil (z.B. eine Druck-Variante)
2. Jedes Bild an den passenden Endpoint senden
3. Komplette Response in `logs/brickognize-debug.log` schreiben
4. Im Log nachsehen:
   - Welches Format hat `items[0].id`?
   - Welche `external_sites`-Eintraege kommen tatsaechlich?
   - Wie hoch sind die Score-Werte?
   - Funktioniert die Farb-Erkennung beim Teile-Endpoint?
5. Erkenntnisse hier in dieser Datei dokumentieren (im Abschnitt "Validiert
   am ...")

## Validiert am

**2026-04-30** mit echten API-Aufrufen (Modus generic, figs, parts).
Erkenntnisse aus den Live-Responses haben mehrere Annahmen aus dieser Doku
widerlegt – siehe folgende Korrekturen:

### Korrekturen gegenueber der urspruenglichen Doku

1. **`external_sites` enthaelt in echten Responses nur EINE Site, nicht drei.**
   Beobachtet wurde ausschliesslich BrickLink. Rebrickable- und BrickOwl-
   Eintraege sind in der Praxis **nicht** vorhanden. Beispiel:
   ```json
   "external_sites": [
     { "name": "bricklink",
       "url": "https://www.bricklink.com/v2/catalog/catalogitem.page?M=cty0969" }
   ]
   ```

2. **`name` ist lowercase** (`"bricklink"`, nicht `"BrickLink"` wie oben
   beschrieben). Unser Resolver entscheidet ohnehin anhand der Domain (Host),
   nicht anhand des Namens, also nicht kritisch – aber falls jemand auf
   `name` matcht: lowercase verwenden oder case-insensitive vergleichen.

3. **`bounding_box`-Koordinaten sind `double`, nicht `int`**:
   ```json
   "bounding_box": {
     "left": 114.08761596679688,
     "upper": 85.3318862915039,
     "right": 475.15484619140625,
     "lower": 363.2808532714844,
     "image_width": 640.0,
     "image_height": 480.0,
     "score": 0.9070212841033936
   }
   ```
   Im Modell **MUSS** `double` (oder `float`) verwendet werden, sonst wirft
   System.Text.Json eine `JsonException` und die ganze Response scheitert.

4. **`img_url` zeigt auf Google-CDN im WebP-Format**, z.B.:
   ```
   https://storage.googleapis.com/brickognize-static/thumbnails/v2.22/fig/cty0969/0.webp
   ```
   WPF unterstuetzt WebP nur mit installiertem
   "Microsoft Web Media Extensions" (Windows-Store-App). Ohne diese werden
   die Vorschaubilder **nicht angezeigt** (kein Crash, aber leere Image-
   Bereiche). Loesungsoptionen:
   a) User installiert die Extension einmalig, oder
   b) wir holen das Bild via HttpClient + decodieren mit System.Drawing
      / SkiaSharp / `Image.FromStream` und konvertieren zu PNG/JPEG fuer
      die Anzeige.
   Aktuell: keine Sonderbehandlung – User-Erlebnis pruefen, ggf. spaeter
   nachziehen.

5. **`listing_id`-Format**: in echten Responses `res-XXXXXXXXXXXXXXXX`
   (Hex), z.B. `res-f80740a6151e48ea`.

6. **`colors[].id` ist KEINE Rebrickable-Color-ID** (validiert 2026-04-30).
   Beispiel: Brickognize antwortete `{"id":"3","name":"Yellow","score":0.73}` –
   Rebrickable-Color-ID 3 ist aber "Dark Turquoise". Brickognize verwendet
   wahrscheinlich BrickLink-Color-IDs (BL 3 = Yellow). Konsequenz fuer uns:
   **Wir matchen ueber `colors[].name`, nicht ueber die ID.**
   Glueckliche Fuegung: die Color-Namen in Rebrickable und BrickLink sind
   weitestgehend identisch, daher klappt die Name-basierte Suche
   `SELECT * FROM colors WHERE name = ? COLLATE NOCASE` sehr zuverlaessig.

### Konsequenz fuer ID-Aufloesung (KRITISCH)

Da Brickognize fuer Minifiguren **nur die BrickLink-ID** liefert (z.B.
`cty0969`), aber unsere `catalog.db` mit `fig_num` (z.B. `fig-001549`)
arbeitet, muessen wir **selber** mappen:

- **Fuer Teile** (`type=part`): BrickLink-Part-ID == Rebrickable `part_num`
  (in den meisten Faellen identisch, z.B. `3001`). Wir koennen die BL-ID
  als Fallback fuer den Rebrickable-Lookup verwenden.
- **Fuer Minifiguren** (`type=fig`): Es gibt **keinen direkten Mapping in
  der catalog.db**. Phase 3 muss das loesen, z.B.:
  - Brickognize-Item-Namen ueber `minifigs.name`-LIKE in der catalog.db
    matchen (best-effort), oder
  - eine externe Mapping-Tabelle pflegen (BL-ID → fig_num), oder
  - akzeptieren, dass wir bei Minifiguren nur den BrickLink-Workflow
    bedienen (Preise/Export ueber BL) und die Rebrickable-Teileliste nicht
    automatisch laden koennen.

### Score-Verteilung in der Praxis

Beobachtet wurden Top-Scores zwischen 0.88 und 0.92 fuer relativ
eindeutige Aufnahmen. Die Standard-Schwellwerte (0.85 auto / 0.7
selection / 0.5 min) passen damit gut.

### Antwortzeiten in der Praxis

- Generischer Endpoint (`/predict/`): ~160 ms
- Figs-Endpoint (`/predict/figs/`): ~40 ms
- Parts-Endpoint (`/predict/parts/`): wenige Millisekunden bei leerer Response
- Health-Check (`/health/`): typischerweise &lt; 100 ms

Damit liegt alles deutlich unter dem Slow-Threshold (3000 ms) – Status
sollte praktisch immer "Online" anzeigen.

## Quellen

- OpenAPI-Doku (Swagger UI): https://api.brickognize.com/docs
- Offizieller MCP-Server (TypeScript-Quellcode):
  https://github.com/NazarLysyi/brickognize-mcp
- Interview mit dem Entwickler:
  https://casadebricks.com/the-magic-of-brickognize-for-automatic-lego-sorting-machines/
