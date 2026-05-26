using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using HBSort.Core.Database;
using HBSort.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Default-Implementierung des Wanted-List-Exports.
///
/// UX X.32 v0.1.19-beta.6 (User-Befund): Format zurueck auf BrickLink-
/// Wanted-XML (INVENTORY/ITEM mit Grossbuchstaben-Tags, MINQTY-Pflicht).
/// Vorher (beta.4-beta.5) war BSX, aber die BrickLink-Webseite akzeptiert
/// nur ihr eigenes XML-Format beim Upload. Inhalt unveraendert: pro
/// fehlendem Teil ein Eintrag, Mengen ueber wartende Figuren aggregiert.
///
/// XML-Struktur (BL-Wanted-Spec):
/// <code>
/// &lt;INVENTORY&gt;
/// &lt;ITEM&gt;
/// &lt;ITEMTYPE&gt;P&lt;/ITEMTYPE&gt;
/// &lt;ITEMID&gt;3622&lt;/ITEMID&gt;
/// &lt;COLOR&gt;11&lt;/COLOR&gt;
/// &lt;MINQTY&gt;5&lt;/MINQTY&gt;
/// &lt;/ITEM&gt;
/// ...
/// &lt;/INVENTORY&gt;
/// </code>
///
/// Konvention:
/// - KEINE XML-Deklaration (BL-Web-Upload erkennt sie nicht).
/// - UTF-8 ohne BOM.
/// - MINQTY ZWINGEND - sonst ignoriert BL den Eintrag beim Upload.
/// - Reihenfolge der Sub-Elemente: ITEMTYPE -> ITEMID -> COLOR (nur Parts)
///   -> MINQTY.
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

        // Fehlende Mengen ueber alle wartenden Figuren aggregieren.
        var aggregated = waiting
            .SelectMany(m => m.RequiredParts)
            .Select(p => new
            {
                p.PartNumber,
                p.ColorId,
                // v0.1.24-beta.8 Phase 3: BL-reservierte Teile NICHT mehr
                // exportieren (User hat sie schon im Shop, will sie nicht
                // doppelt kaufen).
                Missing = p.EffectivelyMissing
            })
            .Where(x => x.Missing > 0)
            .GroupBy(x => new { x.PartNumber, x.ColorId })
            .Select(g => new WantedItem(
                g.Key.PartNumber,
                g.Key.ColorId,
                g.Sum(x => x.Missing)))
            .OrderBy(x => x.PartNo).ThenBy(x => x.ColorId)
            .ToList();

        if (aggregated.Count == 0)
            throw new InvalidOperationException(
                "Keine fehlenden Teile in den wartenden Figuren - nichts zu exportieren.");

        var xml = BuildBlWantedXml(aggregated);
        Log.Information("Wanted-List (BL-XML combined): {Count} Teile-Eintraege ueber {Mfg} Figuren",
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
                .Select(p => new WantedItem(
                    p.PartNumber,
                    p.ColorId,
                    p.EffectivelyMissing))
                .Where(x => x.Quantity > 0)
                .OrderBy(x => x.PartNo).ThenBy(x => x.ColorId)
                .ToList();

            if (entries.Count == 0) continue;

            var xml = BuildBlWantedXml(entries);
            var blId = m.BricklinkId ?? m.FigNum;
            var fileName = $"Wanted-{SanitizeFileName(blId)}-{SanitizeFileName(m.Name)}.xml";
            result.Add(new WantedListExportFile(fileName, xml));
        }

        Log.Information("Wanted-List (BL-XML per Figur): {Count} Dateien generiert", result.Count);
        return result;
    }

    private static async Task<List<TrackedMinifig>> LoadWaitingMinifigsAsync(
        UserDataContext ctx, CancellationToken ct)
    {
        return await ctx.TrackedMinifigs.AsNoTracking()
            .Where(m => m.Status == TrackedMinifigStatus.Waiting)
            .Include(m => m.RequiredParts)
            .OrderBy(m => m.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Baut das BL-Wanted-XML aus der Item-Liste. StringBuilder statt
    /// XmlWriter weil:
    ///   1) BL akzeptiert keine XML-Deklaration. XmlWriter mit
    ///      OmitXmlDeclaration=true ist zwar moeglich, aber bei manchen
    ///      .NET-Versionen schreibt er sie trotzdem.
    ///   2) Wir wollen Pretty-Print mit jeweils einem Tag pro Zeile -
    ///      direkter Stringbau ist hier transparenter.
    /// Sonderzeichen werden via WebUtility.HtmlEncode escaped (deckt
    /// &amp;lt;, &amp;gt;, &amp;amp; ab).
    /// </summary>
    private static string BuildBlWantedXml(IEnumerable<WantedItem> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<INVENTORY>");
        foreach (var e in entries)
        {
            sb.AppendLine("<ITEM>");
            sb.AppendLine("<ITEMTYPE>P</ITEMTYPE>");
            sb.AppendLine($"<ITEMID>{Escape(e.PartNo)}</ITEMID>");
            // COLOR-Tag nur wenn sinnvoll. ColorId 0 wird oft fuer "no color
            // applicable" genutzt - BL akzeptiert das auch ohne COLOR-Tag.
            // Wir schreiben sie trotzdem mit, weil BL "0=Not Applicable"
            // explizit so im Color-Mapping definiert.
            sb.AppendLine($"<COLOR>{e.ColorId.ToString(CultureInfo.InvariantCulture)}</COLOR>");
            sb.AppendLine($"<MINQTY>{e.Quantity.ToString(CultureInfo.InvariantCulture)}</MINQTY>");
            sb.AppendLine("</ITEM>");
        }
        sb.Append("</INVENTORY>");
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        // WebUtility.HtmlEncode escaped &, <, > sowie hohe Codepoints.
        return WebUtility.HtmlEncode(s ?? string.Empty);
    }

    /// <summary>Eine Zeile in der Wanted-List - aggregiertes fehlendes Teil.</summary>
    private record WantedItem(
        string PartNo,
        int ColorId,
        int Quantity);

    /// <summary>
    /// Macht aus beliebigen Strings einen Dateinamens-tauglichen Token.
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
        var clean = sb.ToString().Trim('_', ' ');
        return clean.Length > 60 ? clean.Substring(0, 60) : clean;
    }
}
