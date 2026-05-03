using System.Globalization;
using System.Xml.Linq;
using HBSort.Core.Database;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Default-Implementierung des BSX-Exports. Liest die ausgewaehlten
/// TrackedMinifigs aus der userdata.db und ergaenzt Item-Details
/// (CategoryId) bestmoeglich aus dem BL-Cache.
///
/// BSX-Format-Referenz:
/// https://www.brickstoreapp.org/files/help/bsx_format_v3.html
/// </summary>
public class BsxExportService : IBsxExportService
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IBlCatalogService _catalog;

    /// <summary>BL-Default-Kategorie fuer Minifigs wenn der Cache nichts liefert.</summary>
    private const int DefaultMinifigCategoryId = 65;

    public BsxExportService(
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCatalogService catalog)
    {
        _ctxFactory = ctxFactory;
        _catalog = catalog;
    }

    public async Task<string> GenerateBsxAsync(
        IEnumerable<int> trackedMinifigIds,
        BsxExportOptions options,
        CancellationToken ct = default)
    {
        var ids = trackedMinifigIds?.ToList() ?? new List<int>();
        if (ids.Count == 0)
            throw new ArgumentException(
                "Keine Figuren zum Export uebergeben.", nameof(trackedMinifigIds));

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var minifigs = await ctx.TrackedMinifigs.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .ToListAsync(ct);

        if (minifigs.Count == 0)
            throw new InvalidOperationException(
                "Keine der uebergebenen IDs existiert in der DB.");

        // Reihenfolge wie in der ID-Liste (Multi-Select-Reihenfolge der UI beibehalten).
        var lookup = minifigs.ToDictionary(m => m.Id);
        var ordered = ids.Where(lookup.ContainsKey).Select(i => lookup[i]).ToList();

        var inventory = new XElement("Inventory");
        var nowStr = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var remark = options.Remark ?? $"HBSort {nowStr}";

        foreach (var m in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var blId = m.BricklinkId ?? m.FigNum;

            // Best-effort Cache-Lookup fuer Kategorie. Bei Fehler -> Default.
            int categoryId = DefaultMinifigCategoryId;
            try
            {
                var details = await _catalog.GetMinifigDetailsAsync(blId, ct);
                if (details?.CategoryId is int cat && cat > 0) categoryId = cat;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "BSX-Export: Cache-Lookup fuer {Bl} fehlgeschlagen", blId);
            }

            var item = new XElement("Item",
                new XElement("ItemID", blId),
                new XElement("ItemTypeID", "M"),
                new XElement("ColorID", 0),
                new XElement("ItemName", m.Name),
                new XElement("ItemTypeName", "Minifig"),
                new XElement("ColorName", "(Not Applicable)"),
                new XElement("CategoryID", categoryId),
                new XElement("CategoryName", "Minifig"),
                new XElement("Status", options.Status),
                new XElement("Qty", 1),
                new XElement("Price", options.DefaultPrice
                    .ToString("F4", CultureInfo.InvariantCulture)),
                new XElement("Condition", options.Condition),
                new XElement("Remarks", remark));

            inventory.Add(item);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("BrickStoreXML", inventory));

        Log.Information("BSX-Export: {Count} Figur(en) generiert", ordered.Count);

        // XDocument.ToString liefert ohne XML-Declaration. Wir nutzen StringWriter
        // damit die Declaration mitgeschrieben wird.
        using var sw = new System.IO.StringWriter();
        doc.Save(sw);
        return sw.ToString();
    }
}
