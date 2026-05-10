# FAQ

## Brickognize zeigt "Offline" - was tun?

In der Status-Leiste unten rechts ist eine LED neben dem Brickognize-
Status:

- **Gruen** - Erkennung laeuft.
- **Rot** - keine Verbindung.

Pruefe:

1. **Internet-Verbindung** - bist du online?
2. **Firewall** - blockt sie `api.brickognize.com`?
3. Wenn beides ok ist, ist Brickognize selbst kurz nicht erreichbar.
   Einfach ein paar Minuten warten - die LED springt automatisch
   wieder auf gruen, sobald die API antwortet.

## BL-API-Fehler

In der Status-Leiste siehst du den BL-Counter. Mogliche Probleme:

- **"Tokens fehlen"** - Einstellungen -> *BrickLink-API* -> Tokens
  hinterlegen.
- **"Authorization failed"** - Tokens stimmen nicht. Pruefe ob du
  Consumer Key + Secret + Token Value + Token Secret korrekt
  eingegeben hast und deine externe IP im BL-Consumer-Profile
  eingetragen ist.
- **"Hard-Limit erreicht"** - du hast die selbst gesetzte
  Soft/Hard-Schwelle ueberschritten. HB-Sort blockt weitere Calls
  und nutzt nur noch den Cache. Schwellen siehst du in der
  Status-Leiste; sie resetten rolling ueber 24h.
- **HTTP 429** - BL-eigenes Rate-Limit. Sollte nicht passieren, da
  unsere Schwellen konservativer sind. Wenn doch: kurz warten und
  Toast melden.

## Eine Figur wurde versehentlich als komplett markiert

1. Auf die Figur klicken (Lagerliste oder Tab *Wartende-Detail*).
2. Im Summary-Dialog auf **Wieder oeffnen**.
3. Die Figur ist wieder *Wartend*; DailyStats werden absichtlich
   nicht zurueckgerechnet.

## Ich habe ein Lagerfach geleert, will aber den Inhalt zurueck

Wartende und komplette Figuren werden beim **Fach leeren** *nicht*
geloescht - sie werden nur vom Fach abgekoppelt (StorageBinId=null).
Du findest sie weiter in der Lagerliste und kannst sie einem neuen
Fach zuweisen.

Floating-Parts werden allerdings beim Cleanup-Block nach BSX-Export
**geloescht**. Wenn du einen davon zurueckhaben willst: scanne das
physische Teil neu ein - HB-Sort legt es als neuen Floating-Part an.

## Tooltips nerven - kann ich sie ausschalten?

Ja: Einstellungen -> *Darstellung* -> Schalter **Tooltips anzeigen**
ausstellen. Wirkt sofort, ohne Neustart.

## Kann ich HB-Sort ohne BL-Tokens benutzen?

Ja - mit Einschraenkungen. Stammdaten ziehst du dann ueber den
**BrickStore-Bulk-Import** (Einstellungen -> *BL-Catalog-Daten* ->
*Von GitHub importieren*). Damit funktioniert:

- Brickognize-Erkennung
- Anzeige der Subsets, Bilder und Farb-Listen
- BSX-Export, Wanted-List-Export

Ohne Tokens nicht moeglich:

- **Live-Preise** (Provider *BrickLink-API*)
- Live-Updates der Catalog-Daten (z.B. neue Figuren, die noch nicht
  im BrickStore-Dump waren)

## Wo liegen meine Daten?

Unter `%APPDATA%\HBSort\`:

- `userdata.db` - alle deine Figuren, Faecher, Floating-Parts,
  Scan-Historie
- `userdata.db.bak` - Backup vom letzten App-Start
- `bl_cache.db` - BL-Stammdaten + Bulk-Import + Preise +
  Rate-Limiter-Log
- `settings.json` - inkl. DPAPI-verschluesselter Tokens
- `images\` - gecachte Teil-Bilder (LRU-Eviction)
- `scans\` - letzte 100 Scan-Bilder
- `logs\` - Tageslogs (30 Tage Rotation)

Den Pfad kannst du auch in den Einstellungen unter Tab *Info*
einsehen.

## Wie mache ich ein Backup?

Einfach den ganzen Ordner `%APPDATA%\HBSort\` kopieren - das ist alles.
Nach Wiederherstellung musst du nur die BL-Tokens neu eingeben (die
sind an den Windows-User gebunden und nicht uebertragbar).

## Die App stuerzt beim Start ab - was tun?

1. Pruefe `%APPDATA%\HBSort\logs\app-{Datum}.log` - dort steht der
   Fehler.
2. Falls die `userdata.db` korrupt ist: ersetze sie durch
   `userdata.db.bak`.
3. Falls auch das Backup defekt ist: lass dich nicht entmutigen -
   HB-Sort legt eine frische DB an, du verlierst nur den Bestand.
   Tokens und Settings bleiben in der `settings.json`.
4. Bei wiederholten Crashes: GitHub-Issue mit Log-Auszug.
