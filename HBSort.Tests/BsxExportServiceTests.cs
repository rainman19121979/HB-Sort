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
        _sut = new BsxExportService(_factory, _catalog);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task Generate_throws_when_no_ids_passed()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.GenerateBsxAsync(Array.Empty<int>(), new BsxExportOptions()));
    }

    [Fact]
    public async Task Generate_throws_when_ids_not_in_db()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GenerateBsxAsync(new[] { 9999 }, new BsxExportOptions()));
    }

    [Fact]
    public async Task Generate_emits_BrickStoreXML_root_with_one_item_per_minifig()
    {
        var ids = await SeedAsync(("arc007", "Arctic Forscher"), ("sw0001", "Stormtrooper"));

        var xml = await _sut.GenerateBsxAsync(ids, new BsxExportOptions());
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
        var ids = await SeedAsync(("unknown01", "X"));
        // Catalog liefert nichts -> Default 65

        var xml = await _sut.GenerateBsxAsync(ids, new BsxExportOptions());
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("Inventory")!.Element("Item")!;

        Assert.Equal("65", item.Element("CategoryID")!.Value);
    }

    [Fact]
    public async Task Generate_uses_catalog_category_when_available()
    {
        var ids = await SeedAsync(("arc007", "Arctic"));
        _catalog.Items["arc007"] = new BlItem
        {
            ItemType = "M", ItemNo = "arc007", Name = "Arctic", CategoryId = 273
        };

        var xml = await _sut.GenerateBsxAsync(ids, new BsxExportOptions());
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("Inventory")!.Element("Item")!;

        Assert.Equal("273", item.Element("CategoryID")!.Value);
    }

    [Fact]
    public async Task Generate_keeps_input_id_order_in_xml()
    {
        // 3 Figuren in nicht-alphabetischer Reihenfolge anlegen,
        // dann in absichtlich gemischter Reihenfolge exportieren.
        var ids = await SeedAsync(
            ("z-fig", "Z"), ("a-fig", "A"), ("m-fig", "M"));
        var requested = new[] { ids[2], ids[0], ids[1] }; // m, z, a

        var xml = await _sut.GenerateBsxAsync(requested, new BsxExportOptions());
        var items = XDocument.Parse(xml).Root!.Element("Inventory")!.Elements("Item")
            .Select(i => i.Element("ItemID")!.Value).ToList();

        Assert.Equal(new[] { "m-fig", "z-fig", "a-fig" }, items);
    }

    [Fact]
    public async Task Generate_uses_custom_options_for_status_condition_remark()
    {
        var ids = await SeedAsync(("arc007", "Arctic"));

        var xml = await _sut.GenerateBsxAsync(ids,
            new BsxExportOptions(Condition: "N", Status: "X", Remark: "Mein Remark"));
        var item = XDocument.Parse(xml).Root!.Element("Inventory")!.Element("Item")!;

        Assert.Equal("N", item.Element("Condition")!.Value);
        Assert.Equal("X", item.Element("Status")!.Value);
        Assert.Equal("Mein Remark", item.Element("Remarks")!.Value);
    }

    // --- Helpers ---

    /// <summary>Legt Test-Minifigs an und gibt deren EF-IDs zurueck.</summary>
    private async Task<int[]> SeedAsync(params (string blId, string name)[] minifigs)
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
}
