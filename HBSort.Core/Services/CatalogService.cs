using System.Globalization;
using HBSort.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Implementierung des CatalogService.
/// Liest die catalog.db read-only.
/// </summary>
public class CatalogService : ICatalogService
{
    /// <summary>Default-Pfad zur catalog.db im AppData-Ordner.</summary>
    public static readonly string DefaultCatalogDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HBSort",
        "catalog.db");

    private readonly string _catalogDbPath;

    /// <summary>Production-Konstruktor: nutzt den AppData-Pfad.</summary>
    public CatalogService() : this(DefaultCatalogDbPath) { }

    /// <summary>Test-Konstruktor mit eigenem Pfad.</summary>
    public CatalogService(string catalogDbPath)
    {
        _catalogDbPath = catalogDbPath;
    }

    /// <summary>Erzeugt einen Connection-String fuer Read-Only-Zugriff.</summary>
    private string GetConnectionString()
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = _catalogDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public bool CatalogExists()
    {
        if (!File.Exists(_catalogDbPath)) return false;

        // Auch leere Files (0 Byte) gelten als "nicht vorhanden"
        var info = new FileInfo(_catalogDbPath);
        return info.Length > 0;
    }

    public async Task<CatalogMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        if (!CatalogExists()) return null;

        try
        {
            await using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync(cancellationToken);

            var meta = new CatalogMetadata { Source = "Embedded Seed" };

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM metadata;";

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                switch (key)
                {
                    case "imported_at":   meta.ImportedAt   = value; break;
                    case "minifig_count": meta.MinifigCount = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "part_count":    meta.PartCount    = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "color_count":   meta.ColorCount   = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "set_count":     meta.SetCount     = int.Parse(value, CultureInfo.InvariantCulture); break;
                }
            }

            return meta;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Konnte Catalog-Metadaten nicht lesen");
            return null;
        }
    }

    public async Task<List<CatalogColor>> GetAllColorsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CatalogColor>();
        if (!CatalogExists()) return result;

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, rgb, is_trans FROM colors ORDER BY name;";

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CatalogColor
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Rgb = reader.GetString(2),
                IsTransparent = reader.GetInt32(3) == 1
            });
        }

        return result;
    }

    public async Task<CatalogColor?> GetColorAsync(int colorId, CancellationToken cancellationToken = default)
    {
        if (!CatalogExists()) return null;

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, rgb, is_trans FROM colors WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", colorId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new CatalogColor
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Rgb = reader.GetString(2),
                IsTransparent = reader.GetInt32(3) == 1
            };
        }
        return null;
    }

    public async Task<CatalogColor?> GetColorByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!CatalogExists() || string.IsNullOrWhiteSpace(name)) return null;

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        // SQLite-Default-Collation fuer TEXT ist BINARY (case-sensitive). Wir nutzen
        // explizit COLLATE NOCASE damit "Yellow" und "yellow" beide matchen.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, rgb, is_trans FROM colors WHERE name = $name COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", name);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new CatalogColor
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Rgb = reader.GetString(2),
                IsTransparent = reader.GetInt32(3) == 1
            };
        }
        return null;
    }

    public async Task<CatalogMinifig?> GetMinifigAsync(string figNum, CancellationToken cancellationToken = default)
    {
        if (!CatalogExists()) return null;

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT fig_num, name, num_parts, img_url FROM minifigs WHERE fig_num = $fig_num;";
        cmd.Parameters.AddWithValue("$fig_num", figNum);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new CatalogMinifig
            {
                FigNum = reader.GetString(0),
                Name = reader.GetString(1),
                NumParts = reader.GetInt32(2),
                ImgUrl = reader.IsDBNull(3) ? null : reader.GetString(3)
            };
        }

        return null;
    }

    public async Task<List<CatalogMinifigPart>> GetMinifigPartsAsync(
        string figNum, CancellationToken cancellationToken = default)
    {
        var result = new List<CatalogMinifigPart>();
        if (!CatalogExists()) return result;

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        // Jede Minifigur hat in Rebrickable ihre eigene Inventory-Zeile,
        // deren set_num auf den fig_num verweist (set_num ist eigentlich falsch
        // benannt – die Spalte enthaelt sowohl Set-IDs als auch fig_num-Werte).
        // Wir joinen also direkt: inventories.set_num = $fig_num -> inventory_parts.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT ip.part_num, p.name, ip.color_id, c.name, c.rgb, ip.quantity, ip.is_spare, ip.img_url
            FROM inventories i
              JOIN inventory_parts ip ON ip.inventory_id = i.id
              LEFT JOIN parts p ON p.part_num = ip.part_num
              LEFT JOIN colors c ON c.id = ip.color_id
            WHERE i.set_num = $fig_num AND ip.is_spare = 0
            ORDER BY ip.color_id, ip.part_num;
        ";
        cmd.Parameters.AddWithValue("$fig_num", figNum);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CatalogMinifigPart
            {
                PartNumber = reader.GetString(0),
                PartName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                ColorId = reader.GetInt32(2),
                ColorName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                ColorRgb = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Quantity = reader.GetInt32(5),
                IsSpare = reader.GetInt32(6) == 1,
                ImgUrl = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        return result;
    }

    public async Task<List<CatalogMinifig>> SearchMinifigsByNameAsync(
        string query, int limit, CancellationToken cancellationToken = default)
    {
        var result = new List<CatalogMinifig>();
        if (!CatalogExists() || string.IsNullOrWhiteSpace(query)) return result;

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        // LIKE %query% mit COLLATE NOCASE = case-insensitive Volltextsuche im Namen.
        // SQLite hat keinen Operator-Cache fuer LIKE-Patterns, daher pruefen wir den
        // Index idx_minifigs_name nicht – mit ~16k Minifigs ist Full-Scan ohnehin <50ms.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT fig_num, name, num_parts, img_url
            FROM minifigs
            WHERE name LIKE $pattern COLLATE NOCASE
            ORDER BY name
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$pattern", $"%{query}%");
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CatalogMinifig
            {
                FigNum = reader.GetString(0),
                Name = reader.GetString(1),
                NumParts = reader.GetInt32(2),
                ImgUrl = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        }
        return result;
    }

    public async Task<CatalogPart?> GetPartByNumAsync(string partNum, CancellationToken cancellationToken = default)
    {
        if (!CatalogExists() || string.IsNullOrWhiteSpace(partNum)) return null;

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT part_num, name, part_cat_id, part_material FROM parts WHERE part_num = $part_num;";
        cmd.Parameters.AddWithValue("$part_num", partNum);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new CatalogPart
            {
                PartNum = reader.GetString(0),
                Name = reader.GetString(1),
                PartCatId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                PartMaterial = reader.IsDBNull(3) ? null : reader.GetString(3)
            };
        }
        return null;
    }
}
