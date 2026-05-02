using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Xml;
using System.Xml.Linq;
using LegoMinifigSorter.Core.Models.Bricklink;
using Serilog;

namespace LegoMinifigSorter.Core.Services;

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

    private readonly IBlCacheRepository _cache;
    private readonly HttpClient _http;

    public BlBulkImportService(IBlCacheRepository cache)
    {
        _cache = cache;
        // Eigener HttpClient mit grosszuegigem Timeout (ZIP-Download ~12 MB,
        // im schlechten Netz auch mal 1-2 min).
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LegoMinifigSorter/0.5");
    }

    public async Task<BlBulkImportResult> ImportFromGitHubAsync(
        IProgress<BlBulkImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"lms_brickstore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "downloads.zip");

        try
        {
            // PHASE: Download
            progress?.Report(new BlBulkImportProgress(
                "Download", 0, 0, "downloads.zip von GitHub..."));

            using (var resp = await _http.GetAsync(GitHubZipUrl,
                HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var totalBytes = resp.Content.Headers.ContentLength ?? 0L;

                await using var fs = File.Create(zipPath);
                await using var src = await resp.Content.ReadAsStreamAsync(ct);

                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
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

            Log.Information("BrickStore-ZIP geladen: {Size:F1} MB",
                new FileInfo(zipPath).Length / 1024.0 / 1024.0);

            // PHASE: Entpacken
            progress?.Report(new BlBulkImportProgress(
                "Entpacken", 0, 0, "downloads.zip..."));
            ZipFile.ExtractToDirectory(zipPath, tempDir);
            File.Delete(zipPath);

            // Eigentlicher Import aus dem Temp-Ordner
            return await ImportFromFolderAsync(tempDir, progress, ct);
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

        // PHASE 1: Stammdaten aus items/M.xml + items/P.xml
        var itemsFolder = Path.Combine(folder, "items");
        if (Directory.Exists(itemsFolder))
        {
            foreach (var typeChar in new[] { "M", "P" })
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

        // PHASE 2: Inventories aus M\*.xml
        var minifigFolder = Path.Combine(folder, "M");
        if (Directory.Exists(minifigFolder))
        {
            var xmlFiles = Directory.GetFiles(minifigFolder, "*.xml");
            var total = xmlFiles.Length;
            var batch = new List<BlSubset>(2000);
            const int batchFlushSize = 2000;

            Log.Information("BrickStore-Import: {Count} Minifig-Inventories werden importiert", total);

            for (int i = 0; i < xmlFiles.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var xmlFile = xmlFiles[i];
                var minifigId = Path.GetFileNameWithoutExtension(xmlFile);

                if (i % 50 == 0)
                {
                    progress?.Report(new BlBulkImportProgress(
                        "Inventories", i + 1, total, minifigId));
                }

                try
                {
                    var subsets = ParseInventoryXml(xmlFile, minifigId);
                    batch.AddRange(subsets);
                    invImported += subsets.Count;
                    filesProcessed++;
                }
                catch (Exception ex)
                {
                    errors.Add($"M/{minifigId}.xml: {ex.Message}");
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
                "Inventories", total, total, "fertig"));
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
    /// Parst eine einzelne Minifig-Inventory-XML (z.B. M/cty0527.xml).
    /// Format: BrickStore INVENTORY mit ITEM-Bloecken (ITEMTYPE/ITEMID/QTY/COLOR/...).
    /// </summary>
    private static List<BlSubset> ParseInventoryXml(string xmlPath, string parentNo)
    {
        var doc = XDocument.Load(xmlPath);
        var now = DateTime.UtcNow;
        var subsets = new List<BlSubset>();

        foreach (var item in doc.Descendants("ITEM"))
        {
            subsets.Add(new BlSubset
            {
                ParentType = "M",
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
        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element && reader.Name == "ITEM")
            {
                var elementXml = await reader.ReadOuterXmlAsync();
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
                    Name = root.Element("ITEMNAME")?.Value
                        ?? root.Element("NAME")?.Value
                        ?? string.Empty,
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

                if (items.Count >= 1000)
                {
                    await _cache.UpsertItemsAsync(items, ct);
                    progress?.Report(new BlBulkImportProgress(
                        $"Stammdaten {itemType}", count, 0, items[^1].ItemNo));
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
