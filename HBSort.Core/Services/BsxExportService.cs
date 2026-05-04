using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using HBSort.Core.Database;
using HBSort.Core.Models.Bricklink;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Default-Implementierung des BSX-Exports. Liest die ausgewaehlten
/// TrackedMinifigs UND FloatingParts aus userdata.db und ergaenzt
/// Item-Details (CategoryId, Color-Name) bestmoeglich aus dem BL-Cache.
///
/// BSX-Format-Referenz:
/// https://www.brickstoreapp.org/files/help/bsx_format_v3.html
///
/// XML-Struktur:
///   &lt;BrickStoreXML&gt;
///     &lt;Inventory&gt;
///       &lt;Item&gt; ... ItemTypeID=M, Qty=1, ColorID=0          (komplette Figur)
///       &lt;Item&gt; ... ItemTypeID=P, Qty=N, ColorID=blColorId   (Einzelteil)
///     &lt;/Inventory&gt;
///   &lt;/BrickStoreXML&gt;
/// </summary>
public class BsxExportService : IBsxExportService
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IBlCatalogService _catalog;
    private readonly ISettingsService _settings;

    /// <summary>BL-Default-Kategorie fuer Minifigs wenn der Cache nichts liefert.</summary>
    private const int DefaultMinifigCategoryId = 65;

    /// <summary>BL-Default-Kategorie fuer Teile wenn der Cache nichts liefert.</summary>
    private const int DefaultPartCategoryId = 5;

    /// <summary>
    /// Fallback-Currency wenn die Settings keinen Wert oder einen leeren
    /// String haben. EUR passt zur europaeischen User-Basis, BL-Default
    /// fuer EU-Verkaeufer.
    /// </summary>
    private const string DefaultCurrency = "EUR";

    public BsxExportService(
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCatalogService catalog,
        ISettingsService settings)
    {
        _ctxFactory = ctxFactory;
        _catalog = catalog;
        _settings = settings;
    }

    public async Task<string> GenerateBsxAsync(
        IReadOnlyList<int> trackedMinifigIds,
        IReadOnlyList<int> floatingPartIds,
        BsxExportOptions options,
        CancellationToken ct = default)
    {
        var minifigIds = trackedMinifigIds?.ToList() ?? new List<int>();
        var floatIds = floatingPartIds?.ToList() ?? new List<int>();

        if (minifigIds.Count == 0 && floatIds.Count == 0)
            throw new ArgumentException(
                "Keine Figuren oder Einzelteile zum Export uebergeben.",
                nameof(trackedMinifigIds));

        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);

        // Minifigs laden + StorageBin mitziehen damit wir das Bin-Label
        // pro Item in <Remarks> schreiben koennen (intern, nur Verkaeufer
        // sieht das in BrickStore).
        var minifigs = minifigIds.Count == 0
            ? new List<Models.TrackedMinifig>()
            : await ctx.TrackedMinifigs.AsNoTracking()
                .Include(m => m.StorageBin)
                .Where(m => minifigIds.Contains(m.Id))
                .ToListAsync(ct);

        // Einzelteile dito mit StorageBin-Include.
        var floats = floatIds.Count == 0
            ? new List<Models.FloatingPart>()
            : await ctx.FloatingParts.AsNoTracking()
                .Include(p => p.StorageBin)
                .Where(p => floatIds.Contains(p.Id))
                .ToListAsync(ct);

        if (minifigs.Count == 0 && floats.Count == 0)
            throw new InvalidOperationException(
                "Keine der uebergebenen IDs existiert in der DB.");

        // Reihenfolge wie in den ID-Listen (Multi-Select-Reihenfolge der UI beibehalten).
        var minifigLookup = minifigs.ToDictionary(m => m.Id);
        var orderedMinifigs = minifigIds.Where(minifigLookup.ContainsKey)
            .Select(i => minifigLookup[i]).ToList();

        var floatLookup = floats.ToDictionary(p => p.Id);
        var orderedFloats = floatIds.Where(floatLookup.ContainsKey)
            .Select(i => floatLookup[i]).ToList();

        // Color-Map fuer ColorName-Anreicherung der Einzelteile.
        var colorMap = (await _catalog.GetAllColorsAsync(ct))
            .ToDictionary(c => c.ColorId);

        // BUGFIX (UX X.12, 2026-05-04): Inventory-Tag bekommt das Currency-
        // Attribut, wie es BrickStore-eigene BSX-Dateien auch tun. Wert kommt
        // aus den Preise-Settings; bei leeren/fehlenden Settings auf EUR
        // zurueckfallen. BrickLinkChangelogId wird absichtlich weggelassen -
        // HBSort kennt diesen Wert nicht und Brickstore importiert auch ohne.
        var currency = _settings.Current.Prices?.Currency;
        if (string.IsNullOrWhiteSpace(currency)) currency = DefaultCurrency;

        var inventory = new XElement("Inventory",
            new XAttribute("Currency", currency));

        // UX X.14 (2026-05-04): BrickStore-Konvention fuer Notiz-Felder:
        //   <Remarks>  = INTERN, nur Verkaeufer sieht das       -> Lagerfach
        //   <Comments> = OEFFENTLICH, jeder Kaeufer sieht das   -> User-Notiz
        // Beide Felder werden nur geschrieben wenn ein Wert vorhanden ist
        // (BrickStore-Konvention: leere optionale Felder weglassen, das
        // Reference-BSX aus BrickStore selbst enthaelt sie nur bei Inhalt).
        // Frueher stand in <Remarks> ein automatischer "HBSort {Datum}"-
        // Text - das war oeffentlich eingebraendete Reklame und wurde
        // entfernt (siehe UX X.14).

        // 1) Minifigs (ItemTypeID=M)
        foreach (var m in orderedMinifigs)
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
                Log.Debug(ex, "BSX-Export: Cache-Lookup fuer Minifig {Bl} fehlgeschlagen", blId);
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
                new XElement("Condition", options.Condition));

            AppendRemarksAndComments(item, m.StorageBin?.Label, m.UserNotes);
            inventory.Add(item);
        }

        // 2) Einzelteile (ItemTypeID=P)
        foreach (var fp in orderedFloats)
        {
            ct.ThrowIfCancellationRequested();

            // Best-effort Cache-Lookup fuer Kategorie + Color-Name.
            int categoryId = DefaultPartCategoryId;
            try
            {
                var details = await _catalog.GetPartDetailsAsync(fp.PartNumber, ct);
                if (details?.CategoryId is int cat && cat > 0) categoryId = cat;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "BSX-Export: Cache-Lookup fuer Part {Bl} fehlgeschlagen", fp.PartNumber);
            }

            colorMap.TryGetValue(fp.ColorId, out var color);
            var colorName = color?.Name ?? fp.ColorName;
            if (string.IsNullOrWhiteSpace(colorName)) colorName = $"Color {fp.ColorId}";

            var item = new XElement("Item",
                new XElement("ItemID", fp.PartNumber),
                new XElement("ItemTypeID", "P"),
                new XElement("ColorID", fp.ColorId),
                new XElement("ItemName", fp.PartName),
                new XElement("ItemTypeName", "Part"),
                new XElement("ColorName", colorName),
                new XElement("CategoryID", categoryId),
                new XElement("CategoryName", "Part"),
                new XElement("Status", options.Status),
                new XElement("Qty", fp.Quantity),
                new XElement("Price", options.DefaultPrice
                    .ToString("F4", CultureInfo.InvariantCulture)),
                new XElement("Condition", options.Condition));

            // FloatingPart hat kein User-Notes-Feld im Datenmodell, daher
            // bleibt <Comments> hier immer leer (= weggelassen).
            AppendRemarksAndComments(item, fp.StorageBin?.Label, userNotes: null);
            inventory.Add(item);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("BrickStoreXML", inventory));

        Log.Information("BSX-Export: {Mfg} Figur(en) + {Fp} Einzelteil-Eintraege generiert",
            orderedMinifigs.Count, orderedFloats.Count);

        // BUGFIX (UX X.12, 2026-05-04): NICHT ueber StringWriter rendern - der
        // ist intern UTF-16 und schreibt deshalb encoding="utf-16" in den
        // XML-Prolog, egal was wir in XDeclaration uebergeben. BrickStore
        // bricht den Import dann mit "Oeffnendes Element erwartet" ab.
        // Loesung: XmlWriter direkt auf einem MemoryStream mit
        // UTF8Encoding(false) - "false" heisst "ohne BOM". Das BrickStore-
        // Beispielfile hat ebenfalls UTF-8 ohne BOM und wir matchen exakt.
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            OmitXmlDeclaration = false
        };
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            doc.Save(writer);
        }
        // ToArray statt GetBuffer, damit nur die wirklich beschriebenen Bytes
        // zurueckkommen - GetBuffer wuerde Kapazitaets-Padding mitliefern.
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// UX X.14: schreibt &lt;Remarks&gt; (intern, Lagerfach-Label) und
    /// &lt;Comments&gt; (oeffentlich, User-Notiz) - aber nur wenn der Wert
    /// nicht leer ist. Leere optionale Felder werden weggelassen, wie es
    /// auch BrickStore selbst beim Export macht.
    /// </summary>
    private static void AppendRemarksAndComments(
        XElement item, string? binLabel, string? userNotes)
    {
        if (!string.IsNullOrWhiteSpace(binLabel))
            item.Add(new XElement("Remarks", binLabel.Trim()));
        if (!string.IsNullOrWhiteSpace(userNotes))
            item.Add(new XElement("Comments", userNotes.Trim()));
    }
}
