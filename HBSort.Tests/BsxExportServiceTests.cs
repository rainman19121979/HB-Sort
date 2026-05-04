using System.Xml.Linq;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer BsxExportService. Nutzt eine In-Memory-SQLite-DB fuer EF Core
/// (realistischer als der reine InMemory-Provider, weil SQLite mehr Constraints
/// durchsetzt). IBlCatalogService wird durch eine Stub-Implementation ersetzt.
/// </summary>
public class BsxExportServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly StubBlCatalog _catalog = new();
    private readonly StubSettings _settings = new();
    private readonly BsxExportService _sut;

    public BsxExportServiceTests()
    {
        // SQLite-In-Memory bleibt nur solange die Connection offen ist - daher
        // halten wir hier eine Connection offen und teilen sie ueber alle Contexts.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<UserDataContext>()
            .UseSqlite(_connection)
            .Options;

        // Tests brauchen weder Migrations noch EF-Initialisierung – EnsureCreated
        // legt das Schema einmalig an.
        using (var ctx = new UserDataContext(options))
        {
            ctx.Database.EnsureCreated();
        }

        _factory = new SimpleContextFactory(options);
        _sut = new BsxExportService(_factory, _catalog, _settings);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task Generate_throws_when_both_lists_empty()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.GenerateBsxAsync(
                Array.Empty<int>(), Array.Empty<int>(), new BsxExportOptions()));
    }

    [Fact]
    public async Task Generate_throws_when_ids_not_in_db()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GenerateBsxAsync(
                new[] { 9999 }, Array.Empty<int>(), new BsxExportOptions()));
    }

    [Fact]
    public async Task Generate_emits_BrickStoreXML_root_with_one_item_per_minifig()
    {
        var ids = await SeedMinifigsAsync(("arc007", "Arctic Forscher"), ("sw0001", "Stormtrooper"));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());
        var doc = XDocument.Parse(xml);

        Assert.Equal("BrickStoreXML", doc.Root!.Name.LocalName);
        var items = doc.Root.Element("Inventory")!.Elements("Item").ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("arc007", items[0].Element("ItemID")!.Value);
        Assert.Equal("sw0001", items[1].Element("ItemID")!.Value);
        Assert.All(items, i => Assert.Equal("M", i.Element("ItemTypeID")!.Value));
        Assert.All(items, i => Assert.Equal("1", i.Element("Qty")!.Value));
    }

    [Fact]
    public async Task Generate_uses_default_category_65_when_catalog_returns_null()
    {
        var ids = await SeedMinifigsAsync(("unknown01", "X"));
        // Catalog liefert nichts -> Default 65

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("Inventory")!.Element("Item")!;

        Assert.Equal("65", item.Element("CategoryID")!.Value);
    }

    [Fact]
    public async Task Generate_uses_catalog_category_when_available()
    {
        var ids = await SeedMinifigsAsync(("arc007", "Arctic"));
        _catalog.Items["arc007"] = new BlItem
        {
            ItemType = "M", ItemNo = "arc007", Name = "Arctic", CategoryId = 273
        };

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("Inventory")!.Element("Item")!;

        Assert.Equal("273", item.Element("CategoryID")!.Value);
    }

    [Fact]
    public async Task Generate_keeps_input_id_order_in_xml()
    {
        // 3 Figuren in nicht-alphabetischer Reihenfolge anlegen,
        // dann in absichtlich gemischter Reihenfolge exportieren.
        var ids = await SeedMinifigsAsync(
            ("z-fig", "Z"), ("a-fig", "A"), ("m-fig", "M"));
        var requested = new[] { ids[2], ids[0], ids[1] }; // m, z, a

        var xml = await _sut.GenerateBsxAsync(requested, Array.Empty<int>(), new BsxExportOptions());
        var items = XDocument.Parse(xml).Root!.Element("Inventory")!.Elements("Item")
            .Select(i => i.Element("ItemID")!.Value).ToList();

        Assert.Equal(new[] { "m-fig", "z-fig", "a-fig" }, items);
    }

    [Fact]
    public async Task Generate_uses_custom_options_for_status_and_condition()
    {
        var ids = await SeedMinifigsAsync(("arc007", "Arctic"));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(),
            new BsxExportOptions(Condition: "N", Status: "X"));
        var item = XDocument.Parse(xml).Root!.Element("Inventory")!.Element("Item")!;

        Assert.Equal("N", item.Element("Condition")!.Value);
        Assert.Equal("X", item.Element("Status")!.Value);
    }

    // ===== UX X.14: Remarks=Bin (intern), Comments=UserNotes (oeffentlich) =====

    [Fact]
    public async Task Generate_minifig_writes_bin_label_in_Remarks()
    {
        var binId = await SeedBinAsync("Box 005");
        var ids = await SeedMinifigsAsync(("arc007", "Arctic", binId, userNotes: null));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());
        var item = XDocument.Parse(xml).Root!.Element("Inventory")!.Element("Item")!;

        Assert.Equal("Box 005", item.Element("Remarks")!.Value);
        // Keine Notizen -> kein Comments-Element ueberhaupt.
        Assert.Null(item.Element("Comments"));
    }

    [Fact]
    public async Task Generate_minifig_writes_userNotes_in_Comments()
    {
        var binId = await SeedBinAsync("Box 005");
        var ids = await SeedMinifigsAsync(("arc007", "Arctic", binId, userNotes: "vergilbt"));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());
        var item = XDocument.Parse(xml).Root!.Element("Inventory")!.Element("Item")!;

        Assert.Equal("Box 005", item.Element("Remarks")!.Value);
        Assert.Equal("vergilbt", item.Element("Comments")!.Value);
    }

    [Fact]
    public async Task Generate_floatingPart_writes_bin_label_in_Remarks_and_no_Comments()
    {
        // FloatingPart hat im Datenmodell kein Notes-Feld - <Comments>
        // bleibt daher immer leer und wird weggelassen.
        var binId = await SeedBinAsync("Box 002");
        var floatIds = await SeedFloatingsInBinAsync(binId,
            ("3001", 11, 5, "Brick 2x4", "Black"));

        var xml = await _sut.GenerateBsxAsync(
            Array.Empty<int>(), floatIds, new BsxExportOptions());
        var item = XDocument.Parse(xml).Root!.Element("Inventory")!.Element("Item")!;

        Assert.Equal("Box 002", item.Element("Remarks")!.Value);
        Assert.Null(item.Element("Comments"));
    }

    [Fact]
    public async Task Generate_minifig_without_bin_or_notes_omits_both_fields()
    {
        // Defensiv: Figur ohne Bin und ohne UserNotes -> keine Remarks/Comments.
        var ids = await SeedMinifigsAsync(("arc007", "Arctic", binId: null, userNotes: null));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());
        var item = XDocument.Parse(xml).Root!.Element("Inventory")!.Element("Item")!;

        Assert.Null(item.Element("Remarks"));
        Assert.Null(item.Element("Comments"));
    }

    [Fact]
    public async Task Generate_xml_does_not_contain_HBSort_signature_or_filename_in_text_fields()
    {
        // Frueher hat HBSort " HBSort {Datum}" automatisch in <Remarks>
        // geschrieben - das war im oeffentlichen Bereich der BSX-Datei
        // sichtbar fuer Kaeufer. Jetzt darf nichts derartiges mehr drin sein.
        var binId = await SeedBinAsync("Box 005");
        var ids = await SeedMinifigsAsync(("arc007", "Arctic", binId, userNotes: null));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());

        Assert.DoesNotContain("HBSort", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".bsx", xml, StringComparison.OrdinalIgnoreCase);
    }

    // ====================================================================
    // Phase 7-Erweiterung: FloatingPart-Export (UX X.6)
    // ====================================================================

    [Fact]
    public async Task Generate_emits_part_items_for_floating_parts()
    {
        // Minifig + zwei verschiedene FloatingParts. Erwartung: 1x M-Item +
        // 2x P-Item, in dieser Reihenfolge (Minifigs zuerst, dann Parts).
        var minifigIds = await SeedMinifigsAsync(("arc007", "Arctic"));
        var floatIds = await SeedFloatingsAsync(
            ("3001", 11, 5, "Brick 2x4", "Black"),     // 5x Black
            ("3024", 0, 3, "Plate 1x1", "White"));     // 3x White

        var xml = await _sut.GenerateBsxAsync(minifigIds, floatIds, new BsxExportOptions());
        var items = XDocument.Parse(xml).Root!.Element("Inventory")!.Elements("Item").ToList();

        Assert.Equal(3, items.Count);
        Assert.Equal("M", items[0].Element("ItemTypeID")!.Value);  // Minifig zuerst
        Assert.Equal("P", items[1].Element("ItemTypeID")!.Value);
        Assert.Equal("P", items[2].Element("ItemTypeID")!.Value);

        // Erstes Part: Brick 2x4 / Black / 5x
        Assert.Equal("3001", items[1].Element("ItemID")!.Value);
        Assert.Equal("11", items[1].Element("ColorID")!.Value);
        Assert.Equal("5", items[1].Element("Qty")!.Value);
    }

    [Fact]
    public async Task Generate_works_with_only_floating_parts_no_minifigs()
    {
        // Nur Einzelteile, keine Minifigs - sollte trotzdem laufen.
        var floatIds = await SeedFloatingsAsync(
            ("3001", 11, 2, "Brick 2x4", "Black"));

        var xml = await _sut.GenerateBsxAsync(
            Array.Empty<int>(), floatIds, new BsxExportOptions());
        var items = XDocument.Parse(xml).Root!.Element("Inventory")!.Elements("Item").ToList();

        Assert.Single(items);
        Assert.Equal("P", items[0].Element("ItemTypeID")!.Value);
        Assert.Equal("3001", items[0].Element("ItemID")!.Value);
    }

    // ====================================================================
    // UX X.12 Bugfix (2026-05-04): UTF-8-Encoding ohne BOM.
    // BrickStore wirft "Oeffnendes Element erwartet" wenn der Prolog
    // encoding="utf-16" sagt - dem Provider wird dann naemlich der UTF-16-
    // kodierte Inhalt als UTF-8-Bytes vorgesetzt.
    // ====================================================================

    [Fact]
    public async Task Generate_xml_prolog_declares_utf8_not_utf16()
    {
        var ids = await SeedMinifigsAsync(("arc007", "Arctic"));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());

        // Prolog muss utf-8 deklarieren (case-insensitive, .NET schreibt lowercase).
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("utf-16", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Generate_xml_does_not_start_with_BOM_character()
    {
        var ids = await SeedMinifigsAsync(("arc007", "Arctic"));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());

        // U+FEFF wuerde die ersten 3 Bytes nach UTF-8-Encoding zu EF BB BF machen.
        Assert.NotEqual('﻿', xml[0]);
        Assert.Equal('<', xml[0]);
    }

    [Fact]
    public async Task Generate_utf8_bytes_do_not_start_with_BOM()
    {
        var ids = await SeedMinifigsAsync(("arc007", "Arctic"));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);

        // 0xEF 0xBB 0xBF waere der UTF-8-BOM, der den BrickStore-XML-Parser
        // ebenfalls ins Stolpern bringt.
        Assert.False(bytes.Length >= 3
                     && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                     "BSX-Output darf KEIN UTF-8-BOM enthalten.");
        Assert.Equal((byte)'<', bytes[0]);
    }

    [Fact]
    public async Task Generate_xml_is_parseable_by_XmlReader_without_exception()
    {
        var ids = await SeedMinifigsAsync(("arc007", "Arctic"));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());

        // XmlReader durchparsen damit wir sicher sind dass der String
        // syntaktisch sauber ist (was BrickStore auch erwartet).
        using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml));
        while (reader.Read()) { /* nur durchlaufen, keine Exception */ }
    }

    [Fact]
    public async Task Generate_inventory_has_Currency_attribute_from_settings()
    {
        _settings.Current.Prices.Currency = "USD";
        var ids = await SeedMinifigsAsync(("arc007", "Arctic"));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());
        var doc = XDocument.Parse(xml);

        var inventory = doc.Root!.Element("Inventory")!;
        Assert.Equal("USD", inventory.Attribute("Currency")!.Value);
    }

    [Fact]
    public async Task Generate_inventory_falls_back_to_EUR_when_settings_currency_empty()
    {
        _settings.Current.Prices.Currency = "";
        var ids = await SeedMinifigsAsync(("arc007", "Arctic"));

        var xml = await _sut.GenerateBsxAsync(ids, Array.Empty<int>(), new BsxExportOptions());
        var inventory = XDocument.Parse(xml).Root!.Element("Inventory")!;

        Assert.Equal("EUR", inventory.Attribute("Currency")!.Value);
    }

    [Fact]
    public async Task Generate_part_uses_floating_part_color_name_as_fallback()
    {
        // Catalog liefert keinen Color-Namen -> wir erwarten den FloatingPart.ColorName.
        var floatIds = await SeedFloatingsAsync(
            ("3001", 11, 2, "Brick 2x4", "Black"));

        var xml = await _sut.GenerateBsxAsync(
            Array.Empty<int>(), floatIds, new BsxExportOptions());
        var item = XDocument.Parse(xml).Root!.Element("Inventory")!.Element("Item")!;

        Assert.Equal("Black", item.Element("ColorName")!.Value);
    }

    // --- Helpers ---

    /// <summary>Legt einen Test-Bin an und gibt die Id zurueck.</summary>
    private async Task<int> SeedBinAsync(string label)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var bin = new StorageBin
        {
            Label = label,
            CreatedAt = DateTime.UtcNow
        };
        ctx.StorageBins.Add(bin);
        await ctx.SaveChangesAsync();
        return bin.Id;
    }

    /// <summary>Legt Test-Minifigs an und gibt deren EF-IDs zurueck (kein Bin/Notes).</summary>
    private async Task<int[]> SeedMinifigsAsync(params (string blId, string name)[] minifigs)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var ids = new List<int>();
        foreach (var (blId, name) in minifigs)
        {
            var m = new TrackedMinifig
            {
                BricklinkId = blId,
                FigNum = blId,
                Name = name,
                CreatedAt = DateTime.UtcNow,
                Status = TrackedMinifigStatus.Complete
            };
            ctx.TrackedMinifigs.Add(m);
            await ctx.SaveChangesAsync();
            ids.Add(m.Id);
        }
        return ids.ToArray();
    }

    /// <summary>
    /// UX X.14-Helper: Test-Minifigs mit Bin-Zuordnung und optionalen UserNotes.
    /// </summary>
    private async Task<int[]> SeedMinifigsAsync(
        params (string blId, string name, int? binId, string? userNotes)[] minifigs)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var ids = new List<int>();
        foreach (var (blId, name, binId, userNotes) in minifigs)
        {
            var m = new TrackedMinifig
            {
                BricklinkId = blId,
                FigNum = blId,
                Name = name,
                CreatedAt = DateTime.UtcNow,
                Status = TrackedMinifigStatus.Complete,
                StorageBinId = binId,
                UserNotes = userNotes
            };
            ctx.TrackedMinifigs.Add(m);
            await ctx.SaveChangesAsync();
            ids.Add(m.Id);
        }
        return ids.ToArray();
    }

    /// <summary>
    /// Legt Test-FloatingParts an. Bin wird automatisch angelegt damit der
    /// FK auf StorageBin nicht null ist (im Datenmodell ist StorageBinId
    /// non-null fuer FloatingPart).
    /// </summary>
    private async Task<int[]> SeedFloatingsAsync(
        params (string partNo, int colorId, int qty, string partName, string colorName)[] floats)
    {
        var binId = await SeedBinAsync($"TestBin-{Guid.NewGuid():N}");
        return await SeedFloatingsInBinAsync(binId, floats);
    }

    /// <summary>UX X.14-Helper: Floating-Parts in ein konkretes Bin anlegen.</summary>
    private async Task<int[]> SeedFloatingsInBinAsync(int binId,
        params (string partNo, int colorId, int qty, string partName, string colorName)[] floats)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var ids = new List<int>();
        foreach (var (partNo, colorId, qty, partName, colorName) in floats)
        {
            var fp = new FloatingPart
            {
                PartNumber = partNo,
                ColorId = colorId,
                Quantity = qty,
                PartName = partName,
                ColorName = colorName,
                StorageBinId = binId,
                AddedAt = DateTime.UtcNow
            };
            ctx.FloatingParts.Add(fp);
            await ctx.SaveChangesAsync();
            ids.Add(fp.Id);
        }
        return ids.ToArray();
    }

    /// <summary>Stub-Implementation: liefert nur Items aus dem Dictionary.</summary>
    private sealed class StubBlCatalog : IBlCatalogService
    {
        public Dictionary<string, BlItem?> Items { get; } = new();

        public Task<BlItem?> GetMinifigDetailsAsync(string blMinifigId, CancellationToken ct = default)
            => Task.FromResult(Items.TryGetValue(blMinifigId, out var v) ? v : null);

        // Restliche Methoden werden im BsxExport-Pfad nicht benutzt.
        public Task<List<BlSubset>> GetMinifigPartsAsync(string blMinifigId, CancellationToken ct = default)
            => Task.FromResult(new List<BlSubset>());
        public Task<BlItem?> GetPartDetailsAsync(string blPartNo, CancellationToken ct = default)
            => Task.FromResult<BlItem?>(null);
        public Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<BlColor>());
        public Task<List<string>> FindWaitingMinifigsForPartAsync(string blPartNo, int blColorId,
            IEnumerable<string> waitingMinifigIds, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
        public Task<BlCacheStats> GetCacheStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new BlCacheStats(0, 0, 0, 0, null));
        public Task ClearCacheAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> ClearStaleAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<BlSubset>> GetSupersetsAsync(string blPartNo, int blColorId, CancellationToken ct = default)
            => Task.FromResult(new List<BlSubset>());
        public Task<bool> EnsureFullSubsetsAsync(string blMinifigId, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(
            string blPartNo, int blColorId, CancellationToken ct = default)
            => Task.FromResult(new List<BlMinifigSubsetMatch>());
        public Task<List<BlColor>> GetKnownColorsAsync(string blPartNo, CancellationToken ct = default)
            => Task.FromResult(new List<BlColor>());
    }

    /// <summary>
    /// Minimaler IDbContextFactory-Adapter ueber DbContextOptions.
    /// Im Production-Code wird AddDbContextFactory genutzt; in Tests reicht das hier.
    /// </summary>
    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; } = new()
        {
            Prices = new PriceSettings { Currency = "EUR" }
        };
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }
}
