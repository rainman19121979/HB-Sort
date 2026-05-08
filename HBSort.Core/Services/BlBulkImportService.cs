using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using HBSort.Core.Models.Bricklink;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Bulk-Import von BL-Catalog-Daten aus dem BrickStore-XML-Format
/// (rgriebl/brickstore-database). Befuellt bl_items + bl_subsets in einem Rutsch.
///
/// Source-of-Truth: GitHub-Release "downloads.zip" (latest).
/// Fallback: lokaler Ordner mit Struktur items/M.xml, items/P.xml, M/*.xml.
/// </summary>
public class BlBulkImportService : IBlBulkImportService
{
    /// <summary>downloads.zip vom brickstore-database GitHub-Release "latest".</summary>
    private const string GitHubZipUrl =
        "https://github.com/rgriebl/brickstore-database/releases/latest/download/downloads.zip";

    /// <summary>
    /// UX X.28 (v0.1.15): Items werden in Batches von 1000 in die SQLite
    /// geschrieben. Wert ist ein Kompromiss: kleinere Batches = mehr Round-
    /// Trips, groessere = mehr RAM + Risiko bei Fehler in der Mitte. 1000
    /// hat sich beim 90k-Items-Komplett-Import als guter Wert gezeigt.
    /// </summary>
    private const int ItemUpsertBatchSize = 1000;

    private readonly IBlCacheRepository _cache;
    private readonly HttpClient _http;

    public BlBulkImportService(IBlCacheRepository cache)
        : this(cache, defaultHttpClient: null)
    {
    }

    /// <summary>
    /// UX X.29 (v0.1.16): Test-Konstruktor mit injizierbarem HttpClient.
    /// Der Default-Konstruktor erzeugt einen eigenen HttpClient; fuer Tests
    /// kann hier ein HttpClient mit MockHandler reingegeben werden. Bewusst
    /// public (statt internal + InternalsVisibleTo) - Test-Hook, kein
    /// Architektur-Risiko.
    /// </summary>
    public BlBulkImportService(IBlCacheRepository cache, HttpClient? defaultHttpClient)
    {
        _cache = cache;
        if (defaultHttpClient != null)
        {
            _http = defaultHttpClient;
        }
        else
        {
            // Eigener HttpClient mit grosszuegigem Timeout (ZIP-Download ~12 MB,
            // im schlechten Netz auch mal 1-2 min).
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("HBSort/0.5");
        }
    }

    public async Task<BlBulkImportResult> ImportFromGitHubAsync(
        string? previousEtag = null,
        string? previousContentHash = null,
        IProgress<BlBulkImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"lms_brickstore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "downloads.zip");

        try
        {
            // PHASE: Download mit ETag-Check
            progress?.Report(new BlBulkImportProgress(
                "Download", 0, 0, "Pruefe BrickStore-DB auf Updates..."));

            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubZipUrl);
            if (!string.IsNullOrEmpty(previousEtag))
            {
                // ETag-Werte werden Quoted serialisiert (z.B. "abc123" oder
                // W/"abc123"). EntityTagHeaderValue.Parse normalisiert das.
                if (EntityTagHeaderValue.TryParse(previousEtag, out var etagHeader))
                {
                    request.Headers.IfNoneMatch.Add(etagHeader);
                }
                else
                {
                    Log.Debug("Konnte ETag '{Etag}' nicht als Header parsen, ueberspringe If-None-Match",
                        previousEtag);
                }
            }

            string? newEtag = null;
            string? newContentHash = null;

            using (var resp = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (resp.StatusCode == HttpStatusCode.NotModified)
                {
                    Log.Information(
                        "Auto-BL-Import: BrickStore-DB unveraendert (HTTP 304, ETag={Etag}), ueberspringe",
                        previousEtag);
                    return new BlBulkImportResult(
                        ItemsImported: 0, InventoriesImported: 0,
                        FilesProcessed: 0, FilesSkipped: 0,
                        Duration: TimeSpan.Zero, Errors: new())
                    {
                        Skipped = true,
                        SkipReason = "Inhalt unveraendert (HTTP 304 Not Modified)",
                        NewEtag = previousEtag,
                        NewContentHash = previousContentHash
                    };
                }

                resp.EnsureSuccessStatusCode();

                // ETag fuer den naechsten Lauf merken.
                newEtag = resp.Headers.ETag?.Tag;

                // Download mit gleichzeitiger Hash-Berechnung. IncrementalHash
                // streamt bytewise - kein 37 MB-Memory-Buffer.
                var totalBytes = resp.Content.Headers.ContentLength ?? 0L;
                progress?.Report(new BlBulkImportProgress(
                    "Download", 0, totalBytes > 0 ? (int)(totalBytes / 1024) : 0,
                    "downloads.zip von GitHub..."));

                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var fs = File.Create(zipPath))
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                        hasher.AppendData(buffer, 0, read);
                        received += read;
                        if (totalBytes > 0)
                        {
                            progress?.Report(new BlBulkImportProgress(
                                "Download", (int)(received / 1024),
                                (int)(totalBytes / 1024),
                                $"{received / 1024 / 1024} / {totalBytes / 1024 / 1024} MB"));
                        }
                    }
                }
                var hashBytes = hasher.GetHashAndReset();
                newContentHash = Convert.ToHexString(hashBytes);
            }

            // Hash-Fallback-Check: wenn der Server keinen ETag liefert oder der
            // Cache-ETag verloren geht, vergleichen wir den frischen Hash mit
            // dem letzten erfolgreichen. Bei gleichem Hash ueberspringen wir
            // Entpacken + DB-Updates komplett.
            if (!string.IsNullOrEmpty(previousContentHash)
                && string.Equals(newContentHash, previousContentHash, StringComparison.OrdinalIgnoreCase))
            {
                Log.Information(
                    "Auto-BL-Import: ZIP-Inhalt unveraendert (gleicher SHA256-Hash), ueberspringe");
                return new BlBulkImportResult(
                    ItemsImported: 0, InventoriesImported: 0,
                    FilesProcessed: 0, FilesSkipped: 0,
                    Duration: TimeSpan.Zero, Errors: new())
                {
                    Skipped = true,
                    SkipReason = "Inhalt unveraendert (gleicher SHA256-Hash)",
                    NewEtag = newEtag ?? previousEtag,
                    NewContentHash = newContentHash
                };
            }

            Log.Information("BrickStore-ZIP geladen: {Size:F1} MB, ETag={Etag}, Hash={Hash}",
                new FileInfo(zipPath).Length / 1024.0 / 1024.0,
                newEtag ?? "(none)",
                newContentHash?.Substring(0, Math.Min(16, newContentHash.Length)) ?? "(none)");

            // PHASE: Entpacken (entry-by-entry mit Progress; ZIP enthaelt
            // ~37000+ Dateien, ohne Progress wirkt die App eingefroren).
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var totalEntries = archive.Entries.Count;
                progress?.Report(new BlBulkImportProgress(
                    "Entpacken", 0, totalEntries, $"0 / {totalEntries} Dateien"));

                int extracted = 0;
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    // Verzeichnis-Eintraege haben einen leeren Name - ueberspringen.
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var destPath = Path.Combine(tempDir, entry.FullName);
                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);

                    entry.ExtractToFile(destPath, overwrite: true);
                    extracted++;

                    if (extracted % 500 == 0)
                    {
                        progress?.Report(new BlBulkImportProgress(
                            "Entpacken", extracted, totalEntries,
                            $"{extracted} / {totalEntries} Dateien"));
                    }
                }
                progress?.Report(new BlBulkImportProgress(
                    "Entpacken", totalEntries, totalEntries, "fertig"));
            }
            File.Delete(zipPath);

            // Eigentlicher Import aus dem Temp-Ordner
            var folderResult = await ImportFromFolderAsync(tempDir, progress, ct);
            return folderResult with
            {
                NewEtag = newEtag,
                NewContentHash = newContentHash
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Konnte Temp-Ordner {Path} nicht loeschen", tempDir);
            }
        }
    }

    public async Task<BlBulkImportResult> ImportFromFolderAsync(
        string folder,
        IProgress<BlBulkImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var errors = new List<string>();
        var itemsImported = 0;
        var invImported = 0;
        var filesProcessed = 0;
        var filesSkipped = 0;

        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Ordner nicht gefunden: {folder}");

        // PHASE 1: Stammdaten - alle BL-Item-Types
        // (B=Book, C=Catalog, G=Gear, I=Instruction, M=Minifig, O=OriginalBox,
        //  P=Part, S=Set). BrickStore liefert pro Type genau eine items/{T}.xml.
        var itemsFolder = Path.Combine(folder, "items");
        if (Directory.Exists(itemsFolder))
        {
            foreach (var typeChar in new[] { "B", "C", "G", "I", "M", "O", "P", "S" })
            {
                ct.ThrowIfCancellationRequested();
                var xmlPath = Path.Combine(itemsFolder, $"{typeChar}.xml");
                if (!File.Exists(xmlPath)) continue;

                progress?.Report(new BlBulkImportProgress(
                    $"Stammdaten {typeChar}", 0, 0, Path.GetFileName(xmlPath)));

                try
                {
                    var imported = await ImportItemsXmlAsync(
                        xmlPath, typeChar, progress, ct);
                    itemsImported += imported;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Fehler beim Import von {Path}", xmlPath);
                    errors.Add($"items/{typeChar}.xml: {ex.Message}");
                }
            }
        }

        // PHASE 2: Inventories aus allen Type-Ordnern (M, P, S, G, B, C, I, O).
        // BrickStore legt pro Item-Type einen Ordner an mit *.xml pro Parent.
        var inventoryFolders = new[] { "M", "P", "S", "G", "B", "C", "I", "O" };

        for (int folderIdx = 0; folderIdx < inventoryFolders.Length; folderIdx++)
        {
            ct.ThrowIfCancellationRequested();
            var folderChar = inventoryFolders[folderIdx];
            var invFolder = Path.Combine(folder, folderChar);
            if (!Directory.Exists(invFolder)) continue;

            // Datei-Liste laden (kann bei 18000+ Eintraegen kurz dauern).
            progress?.Report(new BlBulkImportProgress(
                $"Vorbereite {folderChar}", 0, 0,
                $"Lese Datei-Liste aus {folderChar}/..."));

            var xmlFiles = Directory.GetFiles(invFolder, "*.xml");
            if (xmlFiles.Length == 0) continue;

            var total = xmlFiles.Length;
            var batch = new List<BlSubset>(2000);
            const int batchFlushSize = 2000;

            Log.Information("BrickStore-Import: {Count} Inventories aus '{Folder}'",
                total, folderChar);

            for (int i = 0; i < xmlFiles.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var xmlFile = xmlFiles[i];
                var parentNo = Path.GetFileNameWithoutExtension(xmlFile);

                // Progress alle 25 Files reporten - haeufiger Update fuer User-Feedback.
                if (i % 25 == 0)
                {
                    progress?.Report(new BlBulkImportProgress(
                        $"Inventories {folderChar} (Phase {folderIdx + 1}/{inventoryFolders.Length})",
                        i + 1, total, parentNo));
                }

                try
                {
                    var subsets = ParseInventoryXml(xmlFile, folderChar, parentNo);
                    batch.AddRange(subsets);
                    invImported += subsets.Count;
                    filesProcessed++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{folderChar}/{parentNo}.xml: {ex.Message}");
                    filesSkipped++;
                }

                if (batch.Count >= batchFlushSize)
                {
                    await _cache.BulkInsertSubsetsAsync(batch, ct);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await _cache.BulkInsertSubsetsAsync(batch, ct);
            }

            progress?.Report(new BlBulkImportProgress(
                $"Inventories {folderChar}",
                total, total, $"fertig ({total} Files)"));
        }

        sw.Stop();
        Log.Information(
            "BrickStore-Import fertig: {Items} Items, {Inv} Subsets in {Sec:F1}s, {Err} Fehler",
            itemsImported, invImported, sw.Elapsed.TotalSeconds, errors.Count);

        return new BlBulkImportResult(
            itemsImported, invImported, filesProcessed, filesSkipped,
            sw.Elapsed, errors);
    }

    /// <summary>
    /// Parst eine einzelne Inventory-XML (z.B. M/cty0527.xml oder S/75300-1.xml).
    /// Format: BrickStore INVENTORY mit ITEM-Bloecken (ITEMTYPE/ITEMID/QTY/COLOR/...).
    /// </summary>
    private static List<BlSubset> ParseInventoryXml(string xmlPath, string parentType, string parentNo)
    {
        var doc = XDocument.Load(xmlPath);
        var now = DateTime.UtcNow;
        var subsets = new List<BlSubset>();

        foreach (var item in doc.Descendants("ITEM"))
        {
            subsets.Add(new BlSubset
            {
                ParentType = parentType,
                ParentNo = parentNo,
                ItemType = item.Element("ITEMTYPE")?.Value ?? "P",
                ItemNo = item.Element("ITEMID")?.Value ?? string.Empty,
                ColorId = int.TryParse(item.Element("COLOR")?.Value, out var c) ? c : 0,
                Quantity = int.TryParse(item.Element("QTY")?.Value, out var q) ? q : 0,
                ExtraQuantity = (item.Element("EXTRA")?.Value == "Y") ? 1 : 0,
                IsAlternate = item.Element("ALTERNATE")?.Value == "Y",
                IsCounterpart = item.Element("COUNTERPART")?.Value == "Y",
                MatchId = int.TryParse(item.Element("MATCHID")?.Value, out var m) ? m : 0,
                IsFromSupersets = false, // BrickStore = vollstaendige echte Daten
                FetchedAt = now
            });
        }
        return subsets;
    }

    /// <summary>
    /// Stream-basiertes Parsing von items/M.xml oder items/P.xml. Datei kann gross
    /// sein (P.xml ~31 MB), daher XmlReader statt XDocument.
    /// </summary>
    private async Task<int> ImportItemsXmlAsync(
        string xmlPath, string itemType,
        IProgress<BlBulkImportProgress>? progress,
        CancellationToken ct)
    {
        var items = new List<BlItem>(1000);
        var count = 0;
        var now = DateTime.UtcNow;

        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true
        };

        using var reader = XmlReader.Create(xmlPath, settings);

        // WICHTIG: ReadOuterXmlAsync positioniert den Reader bereits hinter
        // dem gerade gelesenen Element. Wenn wir danach erneut ReadAsync()
        // rufen, wird das naechste Element uebersprungen. Mit `moveNext`
        // steuern wir explizit, ob die naechste Iteration ReadAsync() braucht.
        bool moveNext = true;
        while (true)
        {
            if (moveNext)
            {
                if (!await reader.ReadAsync()) break;
            }
            moveNext = true;
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element && reader.Name == "ITEM")
            {
                var elementXml = await reader.ReadOuterXmlAsync();
                // Reader steht jetzt bereits auf dem naechsten Knoten - beim
                // naechsten Schleifen-Durchlauf NICHT ReadAsync() rufen.
                moveNext = false;

                XElement root;
                try { root = XElement.Parse(elementXml); }
                catch { continue; }

                var itemNo = root.Element("ITEMID")?.Value
                          ?? root.Element("NUMBER")?.Value;
                if (string.IsNullOrEmpty(itemNo)) continue;

                items.Add(new BlItem
                {
                    ItemType = itemType,
                    ItemNo = itemNo,
                    // HTML-Decode: BrickStore-XML uebernimmt die BL-Namen unveraendert,
                    // also auch mit Numeric-Entities (&#40; statt "(" usw.).
                    Name = WebUtility.HtmlDecode(
                        root.Element("ITEMNAME")?.Value
                        ?? root.Element("NAME")?.Value
                        ?? string.Empty),
                    YearReleased = int.TryParse(
                        root.Element("ITEMYEAR")?.Value
                        ?? root.Element("YEAR")?.Value, out var y) ? y : null,
                    Weight = double.TryParse(
                        root.Element("ITEMWEIGHT")?.Value
                        ?? root.Element("WEIGHT")?.Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var w) ? w : null,
                    CategoryId = int.TryParse(
                        root.Element("CATEGORY")?.Value, out var cat) ? cat : null,
                    DataCompleteness = DataCompleteness.Full,
                    FetchedAt = now
                });
                count++;

                // Progress haeufig genug fuer User-Feedback (alle 500 Items).
                if (count % 500 == 0)
                {
                    progress?.Report(new BlBulkImportProgress(
                        $"Stammdaten {itemType}", count, 0, items[^1].ItemNo));
                }

                if (items.Count >= ItemUpsertBatchSize)
                {
                    await _cache.UpsertItemsAsync(items, ct);
                    items.Clear();
                }
            }
        }
        if (items.Count > 0)
            await _cache.UpsertItemsAsync(items, ct);

        Log.Information("Stammdaten {Type}: {Count} Eintraege importiert", itemType, count);
        return count;
    }
}
