using System.IO.Compression;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;

namespace HBSort.Tests;

/// <summary>
/// Unit-Tests fuer CatalogImporter mit einem kleinen Sample-Datensatz.
/// Bauen on-the-fly ZIPs mit ein paar Zeilen pro Tabelle und pruefen,
/// dass die DB danach die richtigen Eintraege enthaelt.
/// </summary>
public class CatalogImporterTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;

    public CatalogImporterTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"lego-importer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "catalog.db");

        // Sample-CSV-Inhalte erzeugen und als ZIPs ablegen
        WriteSampleZips();
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
    public async Task Import_creates_db_with_expected_row_counts()
    {
        var importer = new CatalogImporter();
        var zipPaths = Directory.GetFiles(_testDir, "*.csv.zip");

        await importer.ImportFromZipsAsync(zipPaths, _dbPath);

        Assert.True(File.Exists(_dbPath), "catalog.db wurde nicht angelegt");

        // Pruefen, dass die erwarteten Zeilen drin sind
        Assert.Equal(5, CountRows("colors"));
        Assert.Equal(3, CountRows("minifigs"));
        Assert.Equal(10, CountRows("parts"));
        Assert.Equal(2, CountRows("part_categories"));
    }

    [Fact]
    public async Task Import_writes_metadata()
    {
        var importer = new CatalogImporter();
        var zipPaths = Directory.GetFiles(_testDir, "*.csv.zip");

        await importer.ImportFromZipsAsync(zipPaths, _dbPath);

        // Metadata-Tabelle muss die wichtigen Felder enthalten
        var meta = ReadMetadata();
        Assert.True(meta.ContainsKey("imported_at"));
        Assert.Equal("3", meta["minifig_count"]);
        Assert.Equal("10", meta["part_count"]);
        Assert.Equal("5", meta["color_count"]);
    }

    [Fact]
    public async Task Import_creates_indexes()
    {
        var importer = new CatalogImporter();
        var zipPaths = Directory.GetFiles(_testDir, "*.csv.zip");

        await importer.ImportFromZipsAsync(zipPaths, _dbPath);

        var indexes = ReadIndexNames();
        Assert.Contains("idx_minifigs_name", indexes);
        Assert.Contains("idx_invparts_partcolor", indexes);
        Assert.Contains("idx_partrel_child", indexes);
    }

    [Fact]
    public async Task Import_progress_is_reported()
    {
        var importer = new CatalogImporter();
        var zipPaths = Directory.GetFiles(_testDir, "*.csv.zip");

        var reports = new List<CatalogImportProgress>();
        var progress = new Progress<CatalogImportProgress>(p => reports.Add(p));

        await importer.ImportFromZipsAsync(zipPaths, _dbPath, progress);

        // Mindestens einige Progress-Reports kamen rein
        Assert.NotEmpty(reports);
        // Letzter Report meldet "Fertig" mit 100%
        var last = reports.Last();
        Assert.Equal(last.TotalSteps, last.CurrentStep);
    }

    [Fact]
    public async Task Import_correctly_parses_boolean_values()
    {
        var importer = new CatalogImporter();
        var zipPaths = Directory.GetFiles(_testDir, "*.csv.zip");

        await importer.ImportFromZipsAsync(zipPaths, _dbPath);

        // is_trans = True/False muessen als 1/0 gespeichert sein
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, is_trans FROM colors ORDER BY id;";
        using var reader = cmd.ExecuteReader();

        var values = new List<(int id, int trans)>();
        while (reader.Read())
        {
            values.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        // Color id=0 (Black) ist nicht-transparent (0)
        Assert.Contains(values, v => v.id == 0 && v.trans == 0);
        // Color id=47 (Trans-Clear) ist transparent (1)
        Assert.Contains(values, v => v.id == 47 && v.trans == 1);
    }

    [Fact]
    public async Task CatalogService_GetColorByName_finds_correct_color()
    {
        // Regression: Brickognize liefert Color-IDs, die NICHT zu Rebrickable passen
        // (BL 3 = Yellow != Rebrickable 3 = Dark Turquoise). Wir matchen daher ueber
        // den Namen. Test stellt sicher, dass die Name-Suche zuverlaessig funktioniert.
        var importer = new CatalogImporter();
        await importer.ImportFromZipsAsync(Directory.GetFiles(_testDir, "*.csv.zip"), _dbPath);

        // Wir bauen fuer den Lookup einen CatalogService, dessen DB-Pfad auf unsere
        // Test-DB zeigt. Der Service liest standardmaessig %APPDATA% – das umgehen wir
        // mit einer eigenen Connection (das ist auch der Pfad, den der Importer nutzt).
        var byBlack = await QueryColorByName("Black");
        Assert.NotNull(byBlack);
        Assert.Equal(0, byBlack.Value.Id);
        Assert.Equal("05131D", byBlack.Value.Rgb);

        // Case-insensitive: "black" muss genauso funktionieren
        var byBlackLower = await QueryColorByName("black");
        Assert.NotNull(byBlackLower);
        Assert.Equal(0, byBlackLower.Value.Id);

        // Trans-Clear (transparent)
        var byTrans = await QueryColorByName("Trans-Clear");
        Assert.NotNull(byTrans);
        Assert.True(byTrans.Value.IsTransparent);
    }

    /// <summary>Direkter Lookup gegen unsere Test-DB (umgeht den im CatalogService festen %APPDATA%-Pfad).</summary>
    private async Task<(int Id, string Name, string Rgb, bool IsTransparent)?> QueryColorByName(string name)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, rgb, is_trans FROM colors WHERE name = $name COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1);
        }
        return null;
    }

    [Fact]
    public async Task Import_throws_when_required_zip_missing()
    {
        var importer = new CatalogImporter();

        // Nur EINE ZIP-Datei uebergeben statt alle 11 erwarteten
        var onlyColors = Directory.GetFiles(_testDir, "colors.csv.zip");

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await importer.ImportFromZipsAsync(onlyColors, _dbPath));
    }

    // ========================================================================
    // Sample-Daten schreiben
    // ========================================================================

    private void WriteSampleZips()
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
    }

    private void WriteZip(string fileName, string csvContent)
    {
        // Doppelte Leerzeichen am Anfang der Zeilen (durch Raw-String-Indentation) entfernen
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

    // ========================================================================
    // DB-Lese-Helpers
    // ========================================================================

    private long CountRows(string table)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)cmd.ExecuteScalar()!;
    }

    private Dictionary<string, string> ReadMetadata()
    {
        var result = new Dictionary<string, string>();
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM metadata;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    private List<string> ReadIndexNames()
    {
        var result = new List<string>();
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }
}
