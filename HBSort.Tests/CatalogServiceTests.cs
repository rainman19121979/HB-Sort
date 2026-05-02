using System.IO.Compression;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer den CatalogService gegen eine kleine Sample-DB.
/// Wir bauen die DB on-the-fly via CatalogImporter (gleiche Sample-Daten wie
/// CatalogImporterTests) und testen dann die Read-Methoden des Services.
/// </summary>
public class CatalogServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly CatalogService _sut;

    public CatalogServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"lego-catsvc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "catalog.db");

        WriteSampleZipsAndImport();

        _sut = new CatalogService(_dbPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task GetMinifigAsync_returns_known_minifig()
    {
        var fig = await _sut.GetMinifigAsync("fig-000001");
        Assert.NotNull(fig);
        Assert.Equal("Test Stormtrooper", fig!.Name);
        Assert.Equal(4, fig.NumParts);
    }

    [Fact]
    public async Task GetMinifigAsync_returns_null_for_unknown()
    {
        var fig = await _sut.GetMinifigAsync("fig-999999");
        Assert.Null(fig);
    }

    [Fact]
    public async Task GetMinifigPartsAsync_filters_spare_parts()
    {
        // Sample-Daten: Inventory 1 (fig-000001) hat 4 inventory_parts,
        // davon 1 mit is_spare=True (Brick 3001 in Color 1).
        // Erwartet: 3 Eintraege, alle is_spare=0.
        var parts = await _sut.GetMinifigPartsAsync("fig-000001");

        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.False(p.IsSpare));
        // Spare-Brick darf NICHT auftauchen
        Assert.DoesNotContain(parts, p => p.PartNumber == "3001");
    }

    [Fact]
    public async Task GetMinifigPartsAsync_includes_color_rgb()
    {
        var parts = await _sut.GetMinifigPartsAsync("fig-000001");
        // Mindestens ein Teil hat eine Farbe mit RGB-Wert (z.B. Red = C91A09)
        Assert.Contains(parts, p => !string.IsNullOrEmpty(p.ColorRgb));
    }

    [Fact]
    public async Task SearchMinifigsByName_finds_by_substring_case_insensitive()
    {
        var results = await _sut.SearchMinifigsByNameAsync("storm", limit: 5);
        Assert.Single(results);
        Assert.Equal("fig-000001", results[0].FigNum);
    }

    [Fact]
    public async Task SearchMinifigsByName_returns_multiple_when_pattern_matches()
    {
        // Alle Sample-Minifigs haben "Test" im Namen
        var results = await _sut.SearchMinifigsByNameAsync("Test", limit: 10);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task SearchMinifigsByName_respects_limit()
    {
        var results = await _sut.SearchMinifigsByNameAsync("Test", limit: 2);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetPartByNumAsync_returns_known_part()
    {
        var part = await _sut.GetPartByNumAsync("3626c01");
        Assert.NotNull(part);
        Assert.Equal("Minifig Head Plain", part!.Name);
        Assert.Equal(11, part.PartCatId);
    }

    [Fact]
    public async Task GetPartByNumAsync_returns_null_for_unknown()
    {
        var part = await _sut.GetPartByNumAsync("9999999");
        Assert.Null(part);
    }

    [Fact]
    public async Task GetColorAsync_returns_known_color()
    {
        var color = await _sut.GetColorAsync(0);
        Assert.NotNull(color);
        Assert.Equal("Black", color!.Name);
        Assert.Equal("05131D", color.Rgb);
    }

    // ========================================================================
    // Sample-DB-Setup (identisch zu CatalogImporterTests, leichte Wiederholung
    // — bewusst getrennt, damit jeder Test seine eigene saubere DB hat)
    // ========================================================================

    private void WriteSampleZipsAndImport()
    {
        WriteZip("colors", """
            id,name,rgb,is_trans,num_parts,num_sets,y1,y2
            -1,[Unknown],0033B2,False,0,0,2000,2000
            0,Black,05131D,False,100,50,1957,2026
            1,Blue,0055BF,False,80,40,1957,2026
            4,Red,C91A09,False,90,45,1957,2026
            47,Trans-Clear,FCFCFC,True,30,15,1962,2026
            """);

        WriteZip("part_categories", """
            id,name
            1,Baseplates
            11,Minifig Heads
            """);

        WriteZip("parts", """
            part_num,name,part_cat_id,part_material
            3001,Brick 2 x 4,1,Plastic
            3002,Brick 2 x 3,1,Plastic
            3003,Brick 2 x 2,1,Plastic
            3004,Brick 1 x 2,1,Plastic
            3005,Brick 1 x 1,1,Plastic
            3626c01,Minifig Head Plain,11,Plastic
            3626c02,Minifig Head Plain Variant,11,Plastic
            973c01,Minifig Torso,11,Plastic
            970c01,Minifig Hip and Legs,11,Plastic
            3068b,Tile 2 x 2 with Groove,1,Plastic
            """);

        WriteZip("part_relationships", """
            rel_type,child_part_num,parent_part_num
            P,3626c02,3626c01
            """);

        WriteZip("minifigs", """
            fig_num,name,num_parts,img_url
            fig-000001,Test Stormtrooper,4,https://example.com/fig-000001.jpg
            fig-000002,Test Luke Skywalker,4,
            fig-000003,Test Han Solo,4,https://example.com/fig-000003.jpg
            """);

        WriteZip("sets", """
            set_num,name,year,theme_id,num_parts,img_url
            123-1,Test Set Alpha,2020,1,150,
            456-1,Test Set Beta,2021,2,200,https://example.com/456.jpg
            """);

        WriteZip("inventories", """
            id,version,set_num
            1,1,fig-000001
            2,1,fig-000002
            3,1,fig-000003
            """);

        WriteZip("inventory_parts", """
            inventory_id,part_num,color_id,quantity,is_spare,img_url
            1,3626c01,4,1,False,
            1,973c01,4,1,False,
            1,970c01,0,1,False,
            1,3001,1,1,True,
            2,3626c01,4,1,False,
            3,3626c01,4,1,False,
            """);

        WriteZip("inventory_minifigs", """
            inventory_id,fig_num,quantity
            """);

        WriteZip("inventory_sets", """
            inventory_id,set_num,quantity
            """);

        WriteZip("elements", """
            element_id,part_num,color_id,design_id
            300126,3001,1,
            """);

        var importer = new CatalogImporter();
        var zipPaths = Directory.GetFiles(_testDir, "*.csv.zip");
        importer.ImportFromZipsAsync(zipPaths, _dbPath).GetAwaiter().GetResult();
    }

    private void WriteZip(string fileName, string csvContent)
    {
        var clean = string.Join("\n", csvContent
            .Split('\n')
            .Select(line => line.TrimStart()))
            .Trim();

        var csvPath = Path.Combine(_testDir, $"{fileName}.csv");
        File.WriteAllText(csvPath, clean);

        var zipPath = Path.Combine(_testDir, $"{fileName}.csv.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(csvPath, $"{fileName}.csv");
    }
}
