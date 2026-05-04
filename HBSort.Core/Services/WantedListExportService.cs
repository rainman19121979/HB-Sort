using System.Globalization;
using System.Text;
using System.Xml.Linq;
using HBSort.Core.Database;
using HBSort.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Default-Implementierung des Wanted-List-Exports. Liest die wartenden
/// Figuren aus userdata.db, ermittelt pro Required-Part die fehlende Menge
/// und schreibt das BrickLink-Wanted-List-XML-Format.
///
/// XML-Struktur:
/// <code>
/// &lt;INVENTORY&gt;
///   &lt;ITEM&gt;
///     &lt;ITEMTYPE&gt;P&lt;/ITEMTYPE&gt;
///     &lt;ITEMID&gt;3622&lt;/ITEMID&gt;
///     &lt;COLOR&gt;11&lt;/COLOR&gt;
///     &lt;MINQTY&gt;3&lt;/MINQTY&gt;
///   &lt;/ITEM&gt;
/// &lt;/INVENTORY&gt;
/// </code>
/// </summary>
public class WantedListExportService : IWantedListExportService
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;

    public WantedListExportService(IDbContextFactory<UserDataContext> ctxFactory)
    {
        _ctxFactory = ctxFactory;
    }

    public async Task<string> GenerateCombinedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var waiting = await LoadWaitingMinifigsAsync(ctx, ct);

        // Fehlende Mengen ueber alle wartenden Figuren aufsummieren, gruppiert
        // nach (PartNo, ColorId). So bekommt der User eine einzige Liste, in
        // der z.B. "5x Helm Black" steht statt 3 Eintraege "1x", "2x", "2x".
        var aggregated = waiting
            .SelectMany(m => m.RequiredParts)
            .Select(p => new
            {
                p.PartNumber,
                p.ColorId,
                Missing = Math.Max(0, p.QuantityNeeded - p.QuantityCollected)
            })
            .Where(x => x.Missing > 0)
            .GroupBy(x => new { x.PartNumber, x.ColorId })
            .Select(g => (PartNo: g.Key.PartNumber,
                          ColorId: g.Key.ColorId,
                          Quantity: g.Sum(x => x.Missing)))
            .OrderBy(x => x.PartNo).ThenBy(x => x.ColorId)
            .ToList();

        if (aggregated.Count == 0)
            throw new InvalidOperationException(
                "Keine fehlenden Teile in den wartenden Figuren - nichts zu exportieren.");

        var xml = BuildInventoryXml(aggregated);
        Log.Information("Wanted-List (combined): {Count} Teile-Eintraege ueber {Mfg} Figuren",
            aggregated.Count, waiting.Count);
        return xml;
    }

    public async Task<List<WantedListExportFile>> GeneratePerMinifigAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var waiting = await LoadWaitingMinifigsAsync(ctx, ct);

        var result = new List<WantedListExportFile>();
        foreach (var m in waiting)
        {
            ct.ThrowIfCancellationRequested();

            var entries = m.RequiredParts
                .Select(p => (PartNo: p.PartNumber,
                              p.ColorId,
                              Quantity: Math.Max(0, p.QuantityNeeded - p.QuantityCollected)))
                .Where(x => x.Quantity > 0)
                .OrderBy(x => x.PartNo).ThenBy(x => x.ColorId)
                .ToList();

            if (entries.Count == 0) continue; // Figur ist eigentlich schon komplett

            var xml = BuildInventoryXml(entries);
            var blId = m.BricklinkId ?? m.FigNum;
            var fileName = $"Wanted-{SanitizeFileName(blId)}-{SanitizeFileName(m.Name)}.xml";
            result.Add(new WantedListExportFile(fileName, xml));
        }

        Log.Information("Wanted-List (per Figur): {Count} Dateien generiert", result.Count);
        return result;
    }

    /// <summary>Laedt alle wartenden Figuren mit ihren Required-Parts.</summary>
    private static async Task<List<TrackedMinifig>> LoadWaitingMinifigsAsync(
        UserDataContext ctx, CancellationToken ct)
    {
        return await ctx.TrackedMinifigs.AsNoTracking()
            .Where(m => m.Status == TrackedMinifigStatus.Waiting)
            .Include(m => m.RequiredParts)
            .OrderBy(m => m.Name)
            .ToListAsync(ct);
    }

    /// <summary>Baut das INVENTORY-XML aus einer Eintragsliste.</summary>
    private static string BuildInventoryXml(
        IEnumerable<(string PartNo, int ColorId, int Quantity)> entries)
    {
        var inventory = new XElement("INVENTORY");
        foreach (var e in entries)
        {
            inventory.Add(new XElement("ITEM",
                new XElement("ITEMTYPE", "P"),
                new XElement("ITEMID", e.PartNo),
                new XElement("COLOR", e.ColorId.ToString(CultureInfo.InvariantCulture)),
                new XElement("MINQTY", e.Quantity.ToString(CultureInfo.InvariantCulture))));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            inventory);

        // XDocument.ToString liefert ohne XML-Declaration. Wir brauchen sie aber,
        // damit BrickStore/BL die Datei korrekt erkennt.
        using var sw = new StringWriter();
        doc.Save(sw);
        return sw.ToString();
    }

    /// <summary>
    /// Macht aus beliebigen Strings einen Dateinamens-tauglichen Token.
    /// Sonderzeichen werden zu '_'; Path-Separator und reservierte
    /// Windows-Zeichen sind raus.
    /// </summary>
    private static string SanitizeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "minifig";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }
        // Maximal-Laenge limitieren damit der Pfad insgesamt nicht zu lang wird.
        var clean = sb.ToString().Trim('_', ' ');
        return clean.Length > 60 ? clean.Substring(0, 60) : clean;
    }
}
