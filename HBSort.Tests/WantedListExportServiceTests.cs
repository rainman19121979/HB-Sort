using System.Xml.Linq;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// Strukturelle Tests fuer WantedListExportService.
///
/// UX X.32 v0.1.19-beta.6 (User-Befund): Format zurueck auf BL-Wanted-XML
/// (INVENTORY/ITEM mit Grossbuchstaben-Tags + MINQTY). BSX wurde von der
/// BrickLink-Webseite nicht akzeptiert, daher Rueckkehr zum BL-Format.
/// </summary>
public class WantedListExportServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly WantedListExportService _sut;

    public WantedListExportServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<UserDataContext>()
            .UseSqlite(_connection)
            .Options;
        using (var ctx = new UserDataContext(options))
        {
            ctx.Database.EnsureCreated();
        }
        _factory = new SimpleContextFactory(options);
        _sut = new WantedListExportService(_factory);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GenerateCombined_throws_when_no_waiting_minifigs_have_missing_parts()
    {
        await SeedAsync(TrackedMinifigStatus.Complete, ("3001", 0, 1, 1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GenerateCombinedAsync());
    }

    [Fact]
    public async Task GenerateCombined_omits_xml_declaration()
    {
        // BL-Web akzeptiert keine XML-Deklaration. Wichtigste Format-Regel.
        await SeedAsync(TrackedMinifigStatus.Waiting, ("3001", 0, 2, 0));

        var xml = await _sut.GenerateCombinedAsync();

        Assert.False(xml.Contains("<?xml", StringComparison.OrdinalIgnoreCase),
            "XML darf KEINE Deklaration enthalten - BL-Web lehnt das sonst ab.");
        // Erstes Zeichen muss Open-Tag sein (oder Whitespace gefolgt von Open-Tag).
        Assert.StartsWith("<INVENTORY>", xml.TrimStart());
    }

    [Fact]
    public async Task GenerateCombined_root_is_uppercase_INVENTORY()
    {
        await SeedAsync(TrackedMinifigStatus.Waiting, ("3001", 0, 2, 0));
        var xml = await _sut.GenerateCombinedAsync();
        var doc = XDocument.Parse(xml);
        Assert.Equal("INVENTORY", doc.Root!.Name.LocalName);
    }

    [Fact]
    public async Task GenerateCombined_each_item_has_required_BL_elements()
    {
        // Pro ITEM: ITEMTYPE, ITEMID, COLOR, MINQTY (Reihenfolge spielt fuer
        // BL keine Rolle, aber Anwesenheit ist Pflicht - MINQTY besonders,
        // sonst wird der Eintrag beim Upload ignoriert).
        await SeedAsync(TrackedMinifigStatus.Waiting,
            ("3001", 0, 2, 0),
            ("3024", 5, 3, 1));

        var xml = await _sut.GenerateCombinedAsync();
        var doc = XDocument.Parse(xml);
        var items = doc.Root!.Elements("ITEM").ToList();

        Assert.Equal(2, items.Count);
        Assert.All(items, i =>
        {
            Assert.Equal("P", i.Element("ITEMTYPE")!.Value);
            Assert.NotNull(i.Element("ITEMID"));
            Assert.NotNull(i.Element("COLOR"));
            Assert.NotNull(i.Element("MINQTY"));
        });
    }

    [Fact]
    public async Task GenerateCombined_aggregates_quantities_across_minifigs()
    {
        // Zwei Figuren brauchen je das gleiche Teil -> aggregierte MINQTY.
        await SeedAsync(TrackedMinifigStatus.Waiting, ("3001", 0, 2, 0));
        await SeedAsync(TrackedMinifigStatus.Waiting, ("3001", 0, 3, 0));

        var xml = await _sut.GenerateCombinedAsync();
        var item = XDocument.Parse(xml).Root!.Element("ITEM")!;

        Assert.Equal("3001", item.Element("ITEMID")!.Value);
        Assert.Equal("5", item.Element("MINQTY")!.Value);
    }

    [Fact]
    public async Task GenerateCombined_skips_minifigs_without_missing_parts()
    {
        await SeedAsync(TrackedMinifigStatus.Waiting, ("done1", 0, 1, 1));
        await SeedAsync(TrackedMinifigStatus.Waiting, ("loch", 0, 5, 2));

        var xml = await _sut.GenerateCombinedAsync();
        var items = XDocument.Parse(xml).Root!.Elements("ITEM").ToList();

        Assert.Single(items);
        Assert.Equal("loch", items[0].Element("ITEMID")!.Value);
        Assert.Equal("3", items[0].Element("MINQTY")!.Value);
    }

    [Fact]
    public async Task GeneratePerMinifig_returns_one_xml_file_per_waiting_minifig_with_missing()
    {
        await SeedAsync(TrackedMinifigStatus.Waiting, ("3001", 0, 2, 0));
        await SeedAsync(TrackedMinifigStatus.Waiting, ("3024", 5, 1, 0));
        // Komplette Figur: keine Datei.
        await SeedAsync(TrackedMinifigStatus.Waiting, ("3622", 11, 2, 2));

        var files = await _sut.GeneratePerMinifigAsync();

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.EndsWith(".xml", f.FileName));
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        Assert.All(files, f => Assert.DoesNotContain(f.FileName, c => invalid.Contains(c)));
        // Pro Datei: INVENTORY-Wurzel.
        Assert.All(files, f =>
            Assert.Equal("INVENTORY", XDocument.Parse(f.Xml).Root!.Name.LocalName));
    }

    [Fact]
    public async Task BL_XML_has_no_BOM_and_no_xml_declaration()
    {
        await SeedAsync(TrackedMinifigStatus.Waiting, ("3001", 0, 1, 0));

        var xml = await _sut.GenerateCombinedAsync();
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);

        // Kein BOM.
        Assert.True(bytes.Length >= 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        // Keine XML-Deklaration.
        Assert.DoesNotContain("<?xml", xml, StringComparison.OrdinalIgnoreCase);
    }

    // --- Helpers ---

    private async Task SeedAsync(
        TrackedMinifigStatus status,
        params (string PartNo, int ColorId, int Needed, int Collected)[] parts)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var blId = $"fig{Guid.NewGuid():N}".Substring(0, 8);
        var m = new TrackedMinifig
        {
            BricklinkId = blId,
            FigNum = blId,
            Name = blId,
            CreatedAt = DateTime.UtcNow,
            Status = status,
            RequiredParts = parts.Select(p => new TrackedMinifigPart
            {
                PartNumber = p.PartNo,
                ColorId = p.ColorId,
                QuantityNeeded = p.Needed,
                QuantityCollected = p.Collected
            }).ToList()
        };
        ctx.TrackedMinifigs.Add(m);
        await ctx.SaveChangesAsync();
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }
}
