using System.Globalization;
using System.Net;
using System.Reflection;
using HBSort.Core.Models.Bricklink;
using Microsoft.Data.Sqlite;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Default-Implementierung des BL-Cache-Repositorys.
/// Nutzt eine eigene SQLite-Datei (bl_cache.db) ueber Microsoft.Data.Sqlite,
/// damit die Bulk-Inserts nicht durch EF gebremst werden.
///
/// Verbindungs-Modell: Ein Connection pro Repository-Instanz (Singleton im DI).
/// SQLite-Connections sind nicht threadsafe - Operationen werden ueber einen
/// Lock serialisiert. Das ist OK weil die Aufrufe ohnehin von einzelnen
/// User-Aktionen (Scan) ausgeloest werden.
/// </summary>
public class BlCacheRepository : IBlCacheRepository, IDisposable
{
    /// <summary>Default-Pfad zur Sidecar-DB im %APPDATA%-Ordner.</summary>
    public static readonly string DefaultDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HBSort", "bl_cache.db");

    /// <summary>Embedded-Resource-Name fuer das Schema-SQL.</summary>
    private const string SchemaResourceName = "HBSort.Core.Database.BlCacheSchema.sql";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public BlCacheRepository() : this(DefaultDbPath) { }

    /// <summary>Test-/Konfigurations-Konstruktor mit eigenem DB-Pfad.</summary>
    public BlCacheRepository(string dbPath)
    {
        _dbPath = dbPath;

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        _connection = new SqliteConnection(connStr);
        _connection.Open();

        // Performance-PRAGMAs: WAL fuer bessere Concurrency, NORMAL fuer
        // schnelleres Commit (Crash-sicher genug fuer einen Cache).
        ExecutePragma("PRAGMA journal_mode = WAL;");
        ExecutePragma("PRAGMA synchronous = NORMAL;");

        EnsureSchema();
    }

    private void ExecutePragma(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        var schemaSql = LoadEmbeddedResource(SchemaResourceName);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = schemaSql;
            cmd.ExecuteNonQuery();
        }

        // Inkrementelle Migrationen fuer existierende DBs (CREATE TABLE IF NOT EXISTS
        // legt die Spalte nicht nachtraeglich an).
        EnsureColumn("bl_subsets", "is_from_supersets", "INTEGER NOT NULL DEFAULT 0");

        // Bereinigung: alte Single-Row-Pseudo-Eintraege markieren die noch von vor
        // der is_from_supersets-Migration in der DB stehen. Sonst wuerde der
        // EnsureFullSubsets-Cache-Hit fuer eine 1-Eintrag-Minifig faelschlich
        // greifen und "Diese Figur anlegen" eine 1/1-Pseudo-Figur produzieren.
        MarkOrphanSingleRowSubsetsAsFromSupersets();
    }

    /// <summary>
    /// Stellt sicher dass eine Spalte in einer Tabelle existiert. Wenn nicht:
    /// per ALTER TABLE anhaengen. Idempotent, kann bei jedem Start aufgerufen werden.
    /// </summary>
    private void EnsureColumn(string table, string column, string columnType)
    {
        // PRAGMA table_info liefert (cid, name, type, notnull, default, pk).
        bool exists = false;
        using (var checkCmd = _connection.CreateCommand())
        {
            checkCmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = checkCmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (exists) return;

        using var alterCmd = _connection.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnType};";
        alterCmd.ExecuteNonQuery();
        Log.Information("BlCache-Migration: Spalte '{Col}' zu '{Table}' hinzugefuegt", column, table);
    }

    /// <summary>
    /// Heuristische Bereinigung: alte Single-Row-Pseudo-Eintraege fuer Minifigs
    /// (parent_type='M', genau 1 Eintrag, is_from_supersets=0) werden auf
    /// is_from_supersets=1 gesetzt. So erkennt EnsureFullSubsets sie als
    /// unvollstaendig und triggert beim naechsten Lookup einen force-fetch
    /// via BL-API.
    ///
    /// Idempotent: nach dem ersten Lauf findet die Subquery 0 Treffer.
    /// Risiko bei echten 1-Teil-Minifigs ist akzeptabel: kostet maximal einen
    /// zusaetzlichen GetSubsets-Call beim naechsten Lookup.
    /// </summary>
    private void MarkOrphanSingleRowSubsetsAsFromSupersets()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE bl_subsets
            SET is_from_supersets = 1
            WHERE parent_type = 'M'
              AND is_from_supersets = 0
              AND parent_no IN (
                  SELECT parent_no
                  FROM bl_subsets
                  WHERE parent_type = 'M'
                  GROUP BY parent_no
                  HAVING COUNT(*) = 1
              );";
        var updated = cmd.ExecuteNonQuery();
        if (updated > 0)
        {
            Log.Information(
                "BlCache-Cleanup: {Count} verwaiste Single-Row-Subsets als IsFromSupersets markiert (force-fetch beim naechsten Lookup)",
                updated);
        }
    }

    private static string LoadEmbeddedResource(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Resource '{resourceName}' nicht gefunden. " +
                $"Wurde Database/BlCacheSchema.sql in der csproj als EmbeddedResource eingebunden?");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ========================================================================
    // Items
    // ========================================================================

    public Task<BlItem?> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT item_type, item_no, name, year_released, image_url, weight,
                       dim_x, dim_y, dim_z, category_id, json_full,
                       data_completeness, fetched_at
                FROM bl_items
                WHERE item_type = $type AND item_no = $no;";
            cmd.Parameters.AddWithValue("$type", itemType);
            cmd.Parameters.AddWithValue("$no", itemNo);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return Task.FromResult<BlItem?>(ReadItem(reader));
            }
            return Task.FromResult<BlItem?>(null);
        }
    }

    public Task UpsertItemAsync(BlItem item, CancellationToken ct = default)
    {
        lock (_lock)
        {
            UpsertItemNoLock(item);
        }
        return Task.CompletedTask;
    }

    public Task UpsertItemsAsync(IEnumerable<BlItem> items, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                UpsertItemNoLock(item, transaction);
            }
            transaction.Commit();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Kern-Upsert. SCHUTZ-REGEL:
    ///   * Existiert ein Eintrag mit data_completeness='full' und der neue ist 'subset'
    ///     -> kein Overwrite (full-Daten waeren sonst weg).
    ///   * Sonst: stinknormaler Upsert (Insert oder Update).
    /// Die Logik geht via "INSERT ... ON CONFLICT DO UPDATE WHERE excluded.data_completeness='full'
    /// OR existing.data_completeness <> 'full'".
    /// </summary>
    private void UpsertItemNoLock(BlItem item, SqliteTransaction? transaction = null)
    {
        using var cmd = _connection.CreateCommand();
        if (transaction != null) cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO bl_items
                (item_type, item_no, name, year_released, image_url, weight,
                 dim_x, dim_y, dim_z, category_id, json_full,
                 data_completeness, fetched_at)
            VALUES
                ($type, $no, $name, $year, $img, $w, $dx, $dy, $dz, $cat, $json, $compl, $fetched)
            ON CONFLICT(item_type, item_no) DO UPDATE SET
                name              = excluded.name,
                year_released     = excluded.year_released,
                image_url         = excluded.image_url,
                weight            = excluded.weight,
                dim_x             = excluded.dim_x,
                dim_y             = excluded.dim_y,
                dim_z             = excluded.dim_z,
                category_id       = excluded.category_id,
                json_full         = excluded.json_full,
                data_completeness = excluded.data_completeness,
                fetched_at        = excluded.fetched_at
            WHERE
                excluded.data_completeness = 'full'
                OR bl_items.data_completeness <> 'full';
        ";
        cmd.Parameters.AddWithValue("$type", item.ItemType);
        cmd.Parameters.AddWithValue("$no", item.ItemNo);
        cmd.Parameters.AddWithValue("$name", item.Name);
        cmd.Parameters.AddWithValue("$year", (object?)item.YearReleased ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$img", (object?)item.ImageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", (object?)item.Weight ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dx", (object?)item.DimX ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dy", (object?)item.DimY ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dz", (object?)item.DimZ ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cat", (object?)item.CategoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$json", (object?)item.JsonFull ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$compl", item.DataCompleteness == DataCompleteness.Full ? "full" : "subset");
        cmd.Parameters.AddWithValue("$fetched", item.FetchedAt.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public Task<bool> IsItemStaleAsync(string itemType, string itemNo, int staleDays, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT fetched_at FROM bl_items WHERE item_type = $type AND item_no = $no;";
            cmd.Parameters.AddWithValue("$type", itemType);
            cmd.Parameters.AddWithValue("$no", itemNo);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return Task.FromResult(true); // nicht vorhanden -> stale

            var fetched = ParseUtc(reader.GetString(0));
            var age = DateTime.UtcNow - fetched;
            return Task.FromResult(age.TotalDays > staleDays);
        }
    }

    private static BlItem ReadItem(SqliteDataReader r)
    {
        var compl = r.GetString(11);
        return new BlItem
        {
            ItemType = r.GetString(0),
            ItemNo = r.GetString(1),
            // Defensive HTML-Decode: deckt Bestandsdaten ab, die vor dem Boundary-Fix
            // mit Entities (&#40; statt "(") gespeichert wurden. Auf bereits dekodiertem
            // Text ist HtmlDecode ein No-Op.
            Name = WebUtility.HtmlDecode(r.GetString(2)),
            YearReleased = r.IsDBNull(3) ? null : r.GetInt32(3),
            ImageUrl = r.IsDBNull(4) ? null : r.GetString(4),
            Weight = r.IsDBNull(5) ? null : r.GetDouble(5),
            DimX = r.IsDBNull(6) ? null : r.GetDouble(6),
            DimY = r.IsDBNull(7) ? null : r.GetDouble(7),
            DimZ = r.IsDBNull(8) ? null : r.GetDouble(8),
            CategoryId = r.IsDBNull(9) ? null : r.GetInt32(9),
            JsonFull = r.IsDBNull(10) ? null : r.GetString(10),
            DataCompleteness = compl == "full" ? DataCompleteness.Full : DataCompleteness.Subset,
            FetchedAt = ParseUtc(r.GetString(12))
        };
    }

    // ========================================================================
    // Subsets
    // ========================================================================

    public Task<List<BlSubset>> GetSubsetsAsync(string parentType, string parentNo, CancellationToken ct = default)
    {
        var result = new List<BlSubset>();
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT parent_type, parent_no, item_type, item_no, color_id,
                       quantity, extra_quantity, is_alternate, is_counterpart,
                       match_id, fetched_at, is_from_supersets
                FROM bl_subsets
                WHERE parent_type = $pt AND parent_no = $pn
                ORDER BY match_id, color_id, item_no;";
            cmd.Parameters.AddWithValue("$pt", parentType);
            cmd.Parameters.AddWithValue("$pn", parentNo);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new BlSubset
                {
                    ParentType = reader.GetString(0),
                    ParentNo = reader.GetString(1),
                    ItemType = reader.GetString(2),
                    ItemNo = reader.GetString(3),
                    ColorId = reader.GetInt32(4),
                    Quantity = reader.GetInt32(5),
                    ExtraQuantity = reader.GetInt32(6),
                    IsAlternate = reader.GetInt32(7) == 1,
                    IsCounterpart = reader.GetInt32(8) == 1,
                    MatchId = reader.GetInt32(9),
                    FetchedAt = ParseUtc(reader.GetString(10)),
                    IsFromSupersets = reader.GetInt32(11) == 1
                });
            }
        }
        return Task.FromResult(result);
    }

    public Task ReplaceSubsetsAsync(string parentType, string parentNo, IEnumerable<BlSubset> subsets, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();

            // Erst alle Eintraege fuer den Parent loeschen
            using (var del = _connection.CreateCommand())
            {
                del.Transaction = transaction;
                del.CommandText = "DELETE FROM bl_subsets WHERE parent_type = $pt AND parent_no = $pn;";
                del.Parameters.AddWithValue("$pt", parentType);
                del.Parameters.AddWithValue("$pn", parentNo);
                del.ExecuteNonQuery();
            }

            // Neue einfuegen
            using (var ins = _connection.CreateCommand())
            {
                ins.Transaction = transaction;
                ins.CommandText = @"
                    INSERT INTO bl_subsets
                        (parent_type, parent_no, item_type, item_no, color_id,
                         quantity, extra_quantity, is_alternate, is_counterpart,
                         match_id, fetched_at, is_from_supersets)
                    VALUES
                        ($pt, $pn, $it, $in, $cid, $qty, $eqty, $alt, $cp, $mid, $fetched, $fromSup);";

                var pPt = ins.CreateParameter(); pPt.ParameterName = "$pt"; ins.Parameters.Add(pPt);
                var pPn = ins.CreateParameter(); pPn.ParameterName = "$pn"; ins.Parameters.Add(pPn);
                var pIt = ins.CreateParameter(); pIt.ParameterName = "$it"; ins.Parameters.Add(pIt);
                var pIn = ins.CreateParameter(); pIn.ParameterName = "$in"; ins.Parameters.Add(pIn);
                var pCid = ins.CreateParameter(); pCid.ParameterName = "$cid"; ins.Parameters.Add(pCid);
                var pQty = ins.CreateParameter(); pQty.ParameterName = "$qty"; ins.Parameters.Add(pQty);
                var pEqty = ins.CreateParameter(); pEqty.ParameterName = "$eqty"; ins.Parameters.Add(pEqty);
                var pAlt = ins.CreateParameter(); pAlt.ParameterName = "$alt"; ins.Parameters.Add(pAlt);
                var pCp = ins.CreateParameter(); pCp.ParameterName = "$cp"; ins.Parameters.Add(pCp);
                var pMid = ins.CreateParameter(); pMid.ParameterName = "$mid"; ins.Parameters.Add(pMid);
                var pFet = ins.CreateParameter(); pFet.ParameterName = "$fetched"; ins.Parameters.Add(pFet);
                var pFromSup = ins.CreateParameter(); pFromSup.ParameterName = "$fromSup"; ins.Parameters.Add(pFromSup);
                ins.Prepare();

                foreach (var s in subsets)
                {
                    ct.ThrowIfCancellationRequested();
                    pPt.Value = s.ParentType;
                    pPn.Value = s.ParentNo;
                    pIt.Value = s.ItemType;
                    pIn.Value = s.ItemNo;
                    pCid.Value = s.ColorId;
                    pQty.Value = s.Quantity;
                    pEqty.Value = s.ExtraQuantity;
                    pAlt.Value = s.IsAlternate ? 1 : 0;
                    pCp.Value = s.IsCounterpart ? 1 : 0;
                    pMid.Value = s.MatchId;
                    pFet.Value = s.FetchedAt.ToString("o", CultureInfo.InvariantCulture);
                    pFromSup.Value = s.IsFromSupersets ? 1 : 0;
                    ins.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        return Task.CompletedTask;
    }

    public Task<int> BulkInsertSubsetsAsync(IEnumerable<BlSubset> subsets, CancellationToken ct = default)
    {
        var count = 0;
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT OR REPLACE INTO bl_subsets
                    (parent_type, parent_no, item_type, item_no, color_id,
                     quantity, extra_quantity, is_alternate, is_counterpart,
                     match_id, fetched_at, is_from_supersets)
                VALUES
                    ($pt, $pn, $it, $in, $cid, $qty, $eqty, $alt, $cp,
                     $mid, $fetched, $fromSup);";

            var pPt = cmd.CreateParameter(); pPt.ParameterName = "$pt"; cmd.Parameters.Add(pPt);
            var pPn = cmd.CreateParameter(); pPn.ParameterName = "$pn"; cmd.Parameters.Add(pPn);
            var pIt = cmd.CreateParameter(); pIt.ParameterName = "$it"; cmd.Parameters.Add(pIt);
            var pIn = cmd.CreateParameter(); pIn.ParameterName = "$in"; cmd.Parameters.Add(pIn);
            var pCid = cmd.CreateParameter(); pCid.ParameterName = "$cid"; cmd.Parameters.Add(pCid);
            var pQty = cmd.CreateParameter(); pQty.ParameterName = "$qty"; cmd.Parameters.Add(pQty);
            var pEqty = cmd.CreateParameter(); pEqty.ParameterName = "$eqty"; cmd.Parameters.Add(pEqty);
            var pAlt = cmd.CreateParameter(); pAlt.ParameterName = "$alt"; cmd.Parameters.Add(pAlt);
            var pCp = cmd.CreateParameter(); pCp.ParameterName = "$cp"; cmd.Parameters.Add(pCp);
            var pMid = cmd.CreateParameter(); pMid.ParameterName = "$mid"; cmd.Parameters.Add(pMid);
            var pFet = cmd.CreateParameter(); pFet.ParameterName = "$fetched"; cmd.Parameters.Add(pFet);
            var pFromSup = cmd.CreateParameter(); pFromSup.ParameterName = "$fromSup"; cmd.Parameters.Add(pFromSup);
            cmd.Prepare();

            foreach (var s in subsets)
            {
                ct.ThrowIfCancellationRequested();
                pPt.Value = s.ParentType;
                pPn.Value = s.ParentNo;
                pIt.Value = s.ItemType;
                pIn.Value = s.ItemNo;
                pCid.Value = s.ColorId;
                pQty.Value = s.Quantity;
                pEqty.Value = s.ExtraQuantity;
                pAlt.Value = s.IsAlternate ? 1 : 0;
                pCp.Value = s.IsCounterpart ? 1 : 0;
                pMid.Value = s.MatchId;
                pFet.Value = s.FetchedAt.ToString("o", CultureInfo.InvariantCulture);
                pFromSup.Value = s.IsFromSupersets ? 1 : 0;
                cmd.ExecuteNonQuery();
                count++;
            }
            transaction.Commit();
        }
        return Task.FromResult(count);
    }

    public Task<List<string>> FindParentsByItemAsync(string itemType, string itemNo, int colorId, CancellationToken ct = default)
    {
        var result = new List<string>();
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT parent_no
                FROM bl_subsets
                WHERE item_type = $it AND item_no = $in AND color_id = $cid;";
            cmd.Parameters.AddWithValue("$it", itemType);
            cmd.Parameters.AddWithValue("$in", itemNo);
            cmd.Parameters.AddWithValue("$cid", colorId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
        }
        return Task.FromResult(result);
    }

    public Task<List<string>> FindMinifigsContainingPartsAsync(
        IReadOnlyList<(string PartNo, int ColorId)> parts,
        CancellationToken ct = default)
    {
        if (parts == null || parts.Count == 0)
            return Task.FromResult(new List<string>());

        // Wir bauen eine OR-Liste mit benannten Parametern. Der Index
        // idx_bl_subsets_item (item_type, item_no, color_id) traegt jede
        // einzelne Bedingung; bei vielen Teilen kann das langsam werden,
        // aber bei realistischen Floating-Pool-Groessen (<100 verschiedene
        // Teile) ist das vernachlaessigbar.
        var clauses = new List<string>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
            clauses.Add($"(item_no = $p{i} AND color_id = $c{i})");

        var sql = "SELECT DISTINCT parent_no FROM bl_subsets " +
                  "WHERE parent_type = 'M' AND item_type = 'P' " +
                  "  AND is_from_supersets = 0 " +
                  $"  AND ({string.Join(" OR ", clauses)});";

        var result = new List<string>();
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            for (int i = 0; i < parts.Count; i++)
            {
                cmd.Parameters.AddWithValue($"$p{i}", parts[i].PartNo);
                cmd.Parameters.AddWithValue($"$c{i}", parts[i].ColorId);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(reader.GetString(0));
            }
        }
        return Task.FromResult(result);
    }

    public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(
        string blPartNo, int blColorId, CancellationToken ct = default)
    {
        var result = new List<BlMinifigSubsetMatch>();
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT s.parent_no,
                       i.name,
                       i.image_url,
                       SUM(s.quantity) AS total_qty
                FROM bl_subsets s
                LEFT JOIN bl_items i
                    ON i.item_type = 'M' AND i.item_no = s.parent_no
                WHERE s.parent_type = 'M'
                  AND s.item_type = 'P'
                  AND s.item_no = $partNo
                  AND s.color_id = $colorId
                GROUP BY s.parent_no, i.name, i.image_url
                ORDER BY total_qty DESC, s.parent_no
                LIMIT 50;";
            cmd.Parameters.AddWithValue("$partNo", blPartNo);
            cmd.Parameters.AddWithValue("$colorId", blColorId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(new BlMinifigSubsetMatch(
                    MinifigBlId: reader.GetString(0),
                    // Defensive HTML-Decode (siehe ReadItem).
                    MinifigName: reader.IsDBNull(1) ? null : WebUtility.HtmlDecode(reader.GetString(1)),
                    MinifigImageUrl: reader.IsDBNull(2) ? null : reader.GetString(2),
                    QuantityInMinifig: reader.GetInt32(3)));
            }
        }
        return Task.FromResult(result);
    }

    // ========================================================================
    // Colors
    // ========================================================================

    public Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default)
    {
        var result = new List<BlColor>();
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT color_id, name, rgb, type, fetched_at FROM bl_colors ORDER BY name;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new BlColor
                {
                    ColorId = reader.GetInt32(0),
                    // Defensive HTML-Decode (siehe ReadItem).
                    Name = WebUtility.HtmlDecode(reader.GetString(1)),
                    Rgb = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Type = reader.IsDBNull(3) ? null : reader.GetString(3),
                    FetchedAt = ParseUtc(reader.GetString(4))
                });
            }
        }
        return Task.FromResult(result);
    }

    public Task UpsertColorsAsync(IEnumerable<BlColor> colors, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO bl_colors (color_id, name, rgb, type, fetched_at)
                VALUES ($id, $name, $rgb, $type, $fetched)
                ON CONFLICT(color_id) DO UPDATE SET
                    name = excluded.name,
                    rgb = excluded.rgb,
                    type = excluded.type,
                    fetched_at = excluded.fetched_at;";

            var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);
            var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
            var pRgb = cmd.CreateParameter(); pRgb.ParameterName = "$rgb"; cmd.Parameters.Add(pRgb);
            var pType = cmd.CreateParameter(); pType.ParameterName = "$type"; cmd.Parameters.Add(pType);
            var pFet = cmd.CreateParameter(); pFet.ParameterName = "$fetched"; cmd.Parameters.Add(pFet);
            cmd.Prepare();

            foreach (var c in colors)
            {
                ct.ThrowIfCancellationRequested();
                pId.Value = c.ColorId;
                pName.Value = c.Name;
                pRgb.Value = (object?)c.Rgb ?? DBNull.Value;
                pType.Value = (object?)c.Type ?? DBNull.Value;
                pFet.Value = c.FetchedAt.ToString("o", CultureInfo.InvariantCulture);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        return Task.CompletedTask;
    }

    // ========================================================================
    // Known-Colors (Phase 5)
    // ========================================================================

    public Task<List<int>> GetKnownColorIdsAsync(string partNo, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT color_id FROM bl_known_colors WHERE part_no = $no ORDER BY color_id;";
            var p = cmd.CreateParameter(); p.ParameterName = "$no"; p.Value = partNo; cmd.Parameters.Add(p);
            using var rdr = cmd.ExecuteReader();
            var list = new List<int>();
            while (rdr.Read())
            {
                ct.ThrowIfCancellationRequested();
                list.Add(rdr.GetInt32(0));
            }
            return Task.FromResult(list);
        }
    }

    public Task<DateTime?> GetKnownColorsFetchedAtAsync(string partNo, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT MIN(fetched_at) FROM bl_known_colors WHERE part_no = $no;";
            var p = cmd.CreateParameter(); p.ParameterName = "$no"; p.Value = partNo; cmd.Parameters.Add(p);
            var v = cmd.ExecuteScalar();
            if (v == null || v is DBNull) return Task.FromResult<DateTime?>(null);
            if (DateTime.TryParse((string)v, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dt))
                return Task.FromResult<DateTime?>(dt);
            return Task.FromResult<DateTime?>(null);
        }
    }

    public Task ReplaceKnownColorsAsync(string partNo, IEnumerable<int> colorIds, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            // Erst alte Eintraege loeschen
            using (var del = _connection.CreateCommand())
            {
                del.Transaction = transaction;
                del.CommandText = "DELETE FROM bl_known_colors WHERE part_no = $no;";
                var p = del.CreateParameter(); p.ParameterName = "$no"; p.Value = partNo; del.Parameters.Add(p);
                del.ExecuteNonQuery();
            }

            // Dann neue Eintraege schreiben
            using (var ins = _connection.CreateCommand())
            {
                ins.Transaction = transaction;
                ins.CommandText = @"
                    INSERT INTO bl_known_colors (part_no, color_id, fetched_at)
                    VALUES ($no, $cid, $fet);";
                var pNo = ins.CreateParameter(); pNo.ParameterName = "$no"; pNo.Value = partNo; ins.Parameters.Add(pNo);
                var pCid = ins.CreateParameter(); pCid.ParameterName = "$cid"; ins.Parameters.Add(pCid);
                var pFet = ins.CreateParameter(); pFet.ParameterName = "$fet";
                pFet.Value = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                ins.Parameters.Add(pFet);
                ins.Prepare();

                foreach (var cid in colorIds.Distinct())
                {
                    ct.ThrowIfCancellationRequested();
                    pCid.Value = cid;
                    ins.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        return Task.CompletedTask;
    }

    // ========================================================================
    // Maintenance
    // ========================================================================

    public Task<BlCacheStats> GetStatsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            int items, subsets, colors;
            DateTime? oldest = null;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM bl_items;";
                items = Convert.ToInt32(cmd.ExecuteScalar());
            }
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM bl_subsets;";
                subsets = Convert.ToInt32(cmd.ExecuteScalar());
            }
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM bl_colors;";
                colors = Convert.ToInt32(cmd.ExecuteScalar());
            }
            using (var cmd = _connection.CreateCommand())
            {
                // Aelteste Zeitmarke ueber alle 3 Tabellen
                cmd.CommandText = @"
                    SELECT MIN(fetched_at) FROM (
                        SELECT fetched_at FROM bl_items
                        UNION ALL
                        SELECT fetched_at FROM bl_subsets
                        UNION ALL
                        SELECT fetched_at FROM bl_colors
                    );";
                var raw = cmd.ExecuteScalar();
                if (raw != null && raw != DBNull.Value)
                {
                    oldest = ParseUtc(raw.ToString()!);
                }
            }

            long size = 0;
            try { if (File.Exists(_dbPath)) size = new FileInfo(_dbPath).Length; }
            catch { /* ignore */ }

            return Task.FromResult(new BlCacheStats(items, subsets, colors, size, oldest));
        }
    }

    public Task<int> ClearStaleAsync(int staleDays, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-staleDays).ToString("o", CultureInfo.InvariantCulture);
        int total = 0;
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var table in new[] { "bl_items", "bl_subsets" })
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"DELETE FROM {table} WHERE fetched_at < $cutoff;";
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                total += cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        Log.Information("BL-Cache: {Count} stale Eintraege geloescht (>{Days} Tage)", total, staleDays);
        return Task.FromResult(total);
    }

    public Task ClearAllAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var table in new[] { "bl_subsets", "bl_items", "bl_colors" })
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"DELETE FROM {table};";
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        Log.Information("BL-Cache komplett geleert");
        return Task.CompletedTask;
    }

    // ========================================================================
    // API-Call-Log (Phase R2.5)
    // ========================================================================

    public Task LogApiCallAsync(string method, string? itemType, string? itemNo,
        int responseTimeMs, int statusCode, bool success, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO api_call_log
                    (timestamp, method, item_type, item_no, response_time_ms, status_code, success)
                VALUES
                    ($ts, $method, $type, $no, $rt, $sc, $ok);";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$method", method);
            cmd.Parameters.AddWithValue("$type", (object?)itemType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$no", (object?)itemNo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rt", responseTimeMs);
            cmd.Parameters.AddWithValue("$sc", statusCode);
            cmd.Parameters.AddWithValue("$ok", success ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<int> GetCallCountInWindowAsync(TimeSpan window, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.Subtract(window).ToString("o", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM api_call_log WHERE timestamp >= $cut;";
            cmd.Parameters.AddWithValue("$cut", cutoff);
            return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    public Task<int> GetCallCountSinceAsync(DateTime since, CancellationToken ct = default)
    {
        // Caller uebergibt typisch lokales "00:00 heute" - wir vergleichen UTC-ISO,
        // also vorher zu UTC umwandeln (DateTime.Kind=Local nehmen wir an).
        var sinceUtc = since.Kind == DateTimeKind.Utc ? since : since.ToUniversalTime();
        var cutoff = sinceUtc.ToString("o", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM api_call_log WHERE timestamp >= $cut;";
            cmd.Parameters.AddWithValue("$cut", cutoff);
            return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    public Task<DateTime?> GetOldestCallInWindowAsync(TimeSpan window, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.Subtract(window).ToString("o", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT MIN(timestamp) FROM api_call_log WHERE timestamp >= $cut;";
            cmd.Parameters.AddWithValue("$cut", cutoff);
            var raw = cmd.ExecuteScalar();
            if (raw == null || raw == DBNull.Value) return Task.FromResult<DateTime?>(null);
            return Task.FromResult<DateTime?>(ParseUtc(raw.ToString()!));
        }
    }

    public Task<int> PruneApiCallLogAsync(int olderThanDays = 7, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-olderThanDays).ToString("o", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM api_call_log WHERE timestamp < $cut;";
            cmd.Parameters.AddWithValue("$cut", cutoff);
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
                Log.Information("api_call_log: {Count} alte Eintraege geloescht (>{Days}d)", deleted, olderThanDays);
            return Task.FromResult(deleted);
        }
    }

    // ========================================================================
    // Phase 8: Preis-Cache
    // ========================================================================

    public Task<Models.Pricing.PriceResult?> GetCachedPriceAsync(
        string itemType, string itemNo, int colorId,
        string guideType, string newOrUsed,
        string region, string currency,
        int staleDays,
        CancellationToken ct = default)
    {
        Models.Pricing.PriceResult? result = null;
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT min_price, avg_price, qty_avg_price, max_price,
                       unit_quantity, total_quantity, fetched_at
                FROM bl_prices
                WHERE item_type = $it AND item_no = $in AND color_id = $cid
                  AND guide_type = $gt AND new_or_used = $nu
                  AND region = $rg AND currency = $cu;";
            cmd.Parameters.AddWithValue("$it", itemType);
            cmd.Parameters.AddWithValue("$in", itemNo);
            cmd.Parameters.AddWithValue("$cid", colorId);
            cmd.Parameters.AddWithValue("$gt", guideType);
            cmd.Parameters.AddWithValue("$nu", newOrUsed);
            cmd.Parameters.AddWithValue("$rg", region ?? string.Empty);
            cmd.Parameters.AddWithValue("$cu", currency);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return Task.FromResult<Models.Pricing.PriceResult?>(null);

            var fetchedAt = ParseUtc(reader.GetString(6));

            // Stale-Check: bei staleDays>0 verwerfen wenn aelter (Aufrufer holt neu).
            if (staleDays > 0)
            {
                var ageDays = (DateTime.UtcNow - fetchedAt).TotalDays;
                if (ageDays > staleDays) return Task.FromResult<Models.Pricing.PriceResult?>(null);
            }

            result = new Models.Pricing.PriceResult
            {
                MinPrice      = reader.IsDBNull(0) ? null : (decimal?)reader.GetDouble(0),
                AvgPrice      = reader.IsDBNull(1) ? null : (decimal?)reader.GetDouble(1),
                QtyAvgPrice   = reader.IsDBNull(2) ? null : (decimal?)reader.GetDouble(2),
                MaxPrice      = reader.IsDBNull(3) ? null : (decimal?)reader.GetDouble(3),
                UnitQuantity  = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                TotalQuantity = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Currency      = currency,
                FetchedAt     = fetchedAt
            };
        }
        return Task.FromResult<Models.Pricing.PriceResult?>(result);
    }

    public Task UpsertPriceAsync(
        string itemType, string itemNo, int colorId,
        string guideType, string newOrUsed,
        string region, string currency,
        Models.Pricing.PriceResult price,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO bl_prices
                    (item_type, item_no, color_id, guide_type, new_or_used,
                     region, currency,
                     min_price, avg_price, qty_avg_price, max_price,
                     unit_quantity, total_quantity, fetched_at)
                VALUES
                    ($it, $in, $cid, $gt, $nu, $rg, $cu,
                     $min, $avg, $qty, $max, $uq, $tq, $fet);";
            cmd.Parameters.AddWithValue("$it", itemType);
            cmd.Parameters.AddWithValue("$in", itemNo);
            cmd.Parameters.AddWithValue("$cid", colorId);
            cmd.Parameters.AddWithValue("$gt", guideType);
            cmd.Parameters.AddWithValue("$nu", newOrUsed);
            cmd.Parameters.AddWithValue("$rg", region ?? string.Empty);
            cmd.Parameters.AddWithValue("$cu", currency);
            cmd.Parameters.AddWithValue("$min", (object?)(double?)price.MinPrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$avg", (object?)(double?)price.AvgPrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$qty", (object?)(double?)price.QtyAvgPrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$max", (object?)(double?)price.MaxPrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$uq", price.UnitQuantity);
            cmd.Parameters.AddWithValue("$tq", price.TotalQuantity);
            // FetchedAt aus dem PriceResult uebernehmen - der Aufrufer (i.d.R.
            // der Provider direkt nach dem API-Call) setzt den Wert auf "jetzt".
            // Tests koennen einen aelteren FetchedAt mitschicken um Stale-
            // Verhalten zu pruefen, ohne SQL-Direktzugriff.
            var fetchedAtToWrite = price.FetchedAt == default
                ? DateTime.UtcNow
                : price.FetchedAt.ToUniversalTime();
            cmd.Parameters.AddWithValue("$fet",
                fetchedAtToWrite.ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    // ========================================================================
    // Phase 8 / UX#12 Stale-While-Revalidate
    // ========================================================================

    public Task<Models.Pricing.CachedPriceLookup?> GetCachedPriceWithStaleFlagAsync(
        string itemType, string itemNo, int colorId,
        string guideType, string newOrUsed,
        string region, string currency,
        int ttlDays,
        CancellationToken ct = default)
    {
        Models.Pricing.CachedPriceLookup? result = null;
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT min_price, avg_price, qty_avg_price, max_price,
                       unit_quantity, total_quantity, fetched_at
                FROM bl_prices
                WHERE item_type = $it AND item_no = $in AND color_id = $cid
                  AND guide_type = $gt AND new_or_used = $nu
                  AND region = $rg AND currency = $cu;";
            cmd.Parameters.AddWithValue("$it", itemType);
            cmd.Parameters.AddWithValue("$in", itemNo);
            cmd.Parameters.AddWithValue("$cid", colorId);
            cmd.Parameters.AddWithValue("$gt", guideType);
            cmd.Parameters.AddWithValue("$nu", newOrUsed);
            cmd.Parameters.AddWithValue("$rg", region ?? string.Empty);
            cmd.Parameters.AddWithValue("$cu", currency);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<Models.Pricing.CachedPriceLookup?>(null);

            var fetchedAt = ParseUtc(reader.GetString(6));
            // ttlDays &lt;= 0 ist ungueltig - wir behandeln es als "alles ist frisch"
            // (sonst waere jeder Eintrag stale). Aufrufer sollte normalisieren.
            var maxAge = TimeSpan.FromDays(Math.Max(1, ttlDays));
            var isStale = (DateTime.UtcNow - fetchedAt) > maxAge;

            var price = new Models.Pricing.PriceResult
            {
                MinPrice      = reader.IsDBNull(0) ? null : (decimal?)reader.GetDouble(0),
                AvgPrice      = reader.IsDBNull(1) ? null : (decimal?)reader.GetDouble(1),
                QtyAvgPrice   = reader.IsDBNull(2) ? null : (decimal?)reader.GetDouble(2),
                MaxPrice      = reader.IsDBNull(3) ? null : (decimal?)reader.GetDouble(3),
                UnitQuantity  = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                TotalQuantity = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Currency      = currency,
                FetchedAt     = fetchedAt
            };
            result = new Models.Pricing.CachedPriceLookup(price, isStale);
        }
        return Task.FromResult<Models.Pricing.CachedPriceLookup?>(result);
    }

    public Task<bool> DeletePriceAsync(
        string itemType, string itemNo, int colorId,
        string guideType, string newOrUsed,
        string region, string currency,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM bl_prices
                WHERE item_type = $it AND item_no = $in AND color_id = $cid
                  AND guide_type = $gt AND new_or_used = $nu
                  AND region = $rg AND currency = $cu;";
            cmd.Parameters.AddWithValue("$it", itemType);
            cmd.Parameters.AddWithValue("$in", itemNo);
            cmd.Parameters.AddWithValue("$cid", colorId);
            cmd.Parameters.AddWithValue("$gt", guideType);
            cmd.Parameters.AddWithValue("$nu", newOrUsed);
            cmd.Parameters.AddWithValue("$rg", region ?? string.Empty);
            cmd.Parameters.AddWithValue("$cu", currency);
            var deleted = cmd.ExecuteNonQuery();
            return Task.FromResult(deleted > 0);
        }
    }

    public Task<int> ClearAllPricesAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM bl_prices;";
            var deleted = cmd.ExecuteNonQuery();
            Log.Information("bl_prices komplett geleert: {Count} Eintraege geloescht", deleted);
            return Task.FromResult(deleted);
        }
    }

    public Task<int> GetPriceCacheCountAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM bl_prices;";
            return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static DateTime ParseUtc(string isoString)
    {
        // RoundtripKind respektiert das eingebettete K-Suffix (Z fuer UTC) -
        // die kombinierten Flags AssumeUniversal+RoundtripKind sind nicht erlaubt.
        return DateTime.TryParse(isoString, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime()
            : DateTime.MinValue;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
