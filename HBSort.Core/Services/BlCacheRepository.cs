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
        // UX X.34 v0.1.20: vat_mode-Spalte fuer bl_prices. Default 'N' fuer
        // Bestand (= alter Netto-Default), neue Eintraege schreiben den aktuellen
        // PriceSettings.VatMode-Wert. Hinweis: bei v0.1.20-beta.5-Migration
        // unten wird die Tabelle ggf. komplett ersetzt, dann ist diese Spalte
        // schon im neuen Schema enthalten und EnsureColumn ist no-op.
        EnsureColumn("bl_prices", "vat_mode", "TEXT NOT NULL DEFAULT 'N'");

        // v0.1.20-beta.5: country_code als Cache-Dimension + vat_mode in den PK.
        // Wenn die alte Tabelle ohne country_code-Spalte existiert: drop+create
        // mit neuem Schema (bl_prices ist reiner Cache, kein User-Datenverlust).
        MigrateBlPricesSchemaIfNeeded();

        // Bereinigung: alte Single-Row-Pseudo-Eintraege markieren die noch von vor
        // der is_from_supersets-Migration in der DB stehen. Sonst wuerde der
        // EnsureFullSubsets-Cache-Hit fuer eine 1-Eintrag-Minifig faelschlich
        // greifen und "Diese Figur anlegen" eine 1/1-Pseudo-Figur produzieren.
        MarkOrphanSingleRowSubsetsAsFromSupersets();
    }

    /// <summary>
    /// v0.1.20-beta.5: Schema-Migration fuer bl_prices. Prueft ob die
    /// country_code-Spalte existiert. Wenn nicht: alte Tabelle droppen und
    /// mit neuem PK (inkl. country_code + vat_mode) neu anlegen.
    ///
    /// bl_prices ist reiner Cache - der naechste Lookup pro Item holt die
    /// Werte frisch vom Provider. Kein User-Datenverlust (User-DB ist
    /// userdata.db, davon getrennt).
    ///
    /// Idempotent: nach dem ersten Lauf ist die Spalte da und die Methode
    /// macht nichts.
    /// </summary>
    private void MigrateBlPricesSchemaIfNeeded()
    {
        bool hasCountryCode = false;
        using (var checkCmd = _connection.CreateCommand())
        {
            // PRAGMA table_info liefert (cid, name, type, notnull, default, pk).
            checkCmd.CommandText = "PRAGMA table_info(bl_prices);";
            using var reader = checkCmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "country_code",
                        StringComparison.OrdinalIgnoreCase))
                {
                    hasCountryCode = true;
                    break;
                }
            }
        }
        if (hasCountryCode) return;

        Log.Information(
            "bl_prices-Schema veraltet (fehlende country_code-Spalte) " +
            "- Tabelle wird neu angelegt. Preis-Cache-Eintraege gehen verloren " +
            "(werden beim naechsten Lookup neu geholt).");

        // CREATE-Statement inline statt EnsureSchema rekursiv aufzurufen -
        // sauberer und ohne Risiko in den anderen Tabellen-Anlagen mitzuwirken.
        const string createSql = @"
            CREATE TABLE bl_prices (
                item_type    TEXT NOT NULL,
                item_no      TEXT NOT NULL,
                color_id     INTEGER NOT NULL DEFAULT 0,
                guide_type   TEXT NOT NULL,
                new_or_used  TEXT NOT NULL,
                region       TEXT NOT NULL DEFAULT '',
                country_code TEXT NOT NULL DEFAULT '',
                currency     TEXT NOT NULL DEFAULT 'EUR',
                vat_mode     TEXT NOT NULL DEFAULT 'Y',
                min_price    REAL,
                avg_price    REAL,
                qty_avg_price REAL,
                max_price    REAL,
                unit_quantity INTEGER DEFAULT 0,
                total_quantity INTEGER DEFAULT 0,
                fetched_at   TEXT NOT NULL,
                PRIMARY KEY (item_type, item_no, color_id, guide_type,
                             new_or_used, region, country_code, currency, vat_mode)
            );
            CREATE INDEX IF NOT EXISTS idx_bl_prices_fetched ON bl_prices(fetched_at);";

        using var transaction = _connection.BeginTransaction();
        using (var drop = _connection.CreateCommand())
        {
            drop.Transaction = transaction;
            drop.CommandText = "DROP TABLE IF EXISTS bl_prices;";
            drop.ExecuteNonQuery();
        }
        using (var create = _connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = createSql;
            create.ExecuteNonQuery();
        }
        transaction.Commit();
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

    /// <summary>
    /// UX X.32 v0.1.19-beta.4: BL-Kategorie-Id pro PartNo aus dem Cache.
    /// Chunking auf 500 Items pro IN-Klausel - SQLite-Default-Limit ist
    /// 999 Parameter, wir bleiben mit Sicherheits-Puffer drunter.
    /// </summary>
    /// <summary>
    /// v0.1.24-beta.7 Phase 2: Bulk-Lookup von Name + ImageUrl pro
    /// (ItemType, ItemNo). Gruppiert intern nach <c>item_type</c> und macht
    /// eine IN-Query je Typ mit 500er-Chunks. Bestand der BL-Inventar-Tab
    /// hat typisch 9k Parts + 300 Minifigs + 30 Sets - 3 Queries, schnell.
    /// </summary>
    public Task<Dictionary<(string ItemType, string ItemNo), BlItemSummary>> GetItemSummariesAsync(
        IEnumerable<(string ItemType, string ItemNo)> keys,
        CancellationToken ct = default)
    {
        var result = new Dictionary<(string, string), BlItemSummary>();
        if (keys == null) return Task.FromResult(result);

        // Gruppieren + Duplikate raus. Distinct pro (Type, No) damit IN-Query
        // keine doppelten Parameter hat.
        var byType = keys
            .Where(k => !string.IsNullOrWhiteSpace(k.ItemType) && !string.IsNullOrWhiteSpace(k.ItemNo))
            .GroupBy(k => k.ItemType)
            .ToDictionary(
                g => g.Key,
                g => g.Select(k => k.ItemNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        if (byType.Count == 0) return Task.FromResult(result);

        const int chunkSize = 500;
        lock (_lock)
        {
            foreach (var kv in byType)
            {
                var itemType = kv.Key;
                var nos = kv.Value;

                for (int offset = 0; offset < nos.Count; offset += chunkSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = nos.Skip(offset).Take(chunkSize).ToList();

                    using var cmd = _connection.CreateCommand();
                    cmd.Parameters.AddWithValue("$type", itemType);
                    var paramNames = new string[chunk.Count];
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var name = $"$p{i}";
                        paramNames[i] = name;
                        cmd.Parameters.AddWithValue(name, chunk[i]);
                    }
                    cmd.CommandText =
                        "SELECT item_no, name, image_url FROM bl_items " +
                        "WHERE item_type = $type " +
                        "  AND item_no IN (" + string.Join(",", paramNames) + ");";

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var no = reader.GetString(0);
                        var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        var imageUrl = reader.IsDBNull(2) ? null : reader.GetString(2);
                        // HTML-Decode konsistent zu ReadItem/GetItemNamesAsync.
                        var decoded = System.Net.WebUtility.HtmlDecode(name);
                        result[(itemType, no)] = new BlItemSummary(decoded, imageUrl);
                    }
                }
            }
        }
        return Task.FromResult(result);
    }

    public Task<Dictionary<string, string>> GetItemNamesAsync(
        IEnumerable<string> partNumbers,
        CancellationToken ct = default)
    {
        // v0.1.22-beta.1 Fix 2026-05-12: Bulk-Lookup item_no -> name fuer
        // item_type='P'. Chunking auf 500 wie GetCategoryIdsForPartsAsync.
        var input = (partNumbers ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (input.Count == 0) return Task.FromResult(result);

        const int chunkSize = 500;
        lock (_lock)
        {
            for (int offset = 0; offset < input.Count; offset += chunkSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = input.Skip(offset).Take(chunkSize).ToList();

                using var cmd = _connection.CreateCommand();
                var paramNames = new string[chunk.Count];
                for (int i = 0; i < chunk.Count; i++)
                {
                    var name = $"$p{i}";
                    paramNames[i] = name;
                    cmd.Parameters.AddWithValue(name, chunk[i]);
                }
                cmd.CommandText =
                    "SELECT item_no, name FROM bl_items " +
                    "WHERE item_type = 'P' AND name IS NOT NULL " +
                    "  AND item_no IN (" + string.Join(",", paramNames) + ");";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(1)) continue;
                    var no = reader.GetString(0);
                    var raw = reader.GetString(1);
                    // Defensiv HTML-Decode konsistent zu ReadItem (alte
                    // Bestandsdaten mit &#40; etc.).
                    result[no] = System.Net.WebUtility.HtmlDecode(raw);
                }
            }
        }

        return Task.FromResult(result);
    }

    public Task<Dictionary<string, int>> GetCategoryIdsForPartsAsync(
        IEnumerable<string> partNumbers,
        CancellationToken ct = default)
    {
        var input = (partNumbers ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (input.Count == 0) return Task.FromResult(result);

        const int chunkSize = 500;
        lock (_lock)
        {
            for (int offset = 0; offset < input.Count; offset += chunkSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = input.Skip(offset).Take(chunkSize).ToList();

                using var cmd = _connection.CreateCommand();
                // Parameter-Liste $p0, $p1, ... pro Chunk dynamisch bauen.
                var paramNames = new string[chunk.Count];
                for (int i = 0; i < chunk.Count; i++)
                {
                    var name = $"$p{i}";
                    paramNames[i] = name;
                    cmd.Parameters.AddWithValue(name, chunk[i]);
                }
                cmd.CommandText =
                    "SELECT item_no, category_id FROM bl_items " +
                    "WHERE item_type = 'P' AND category_id IS NOT NULL " +
                    "  AND item_no IN (" + string.Join(",", paramNames) + ");";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(1)) continue; // defensiv (Filter ist DB-seitig schon raus)
                    var no = reader.GetString(0);
                    var cat = reader.GetInt32(1);
                    result[no] = cat;
                }
            }
        }

        return Task.FromResult(result);
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

    public Task<Dictionary<string, List<BlSubset>>> GetSubsetsBulkAsync(
        string parentType, IReadOnlyList<string> parentNos, CancellationToken ct = default)
    {
        // BUILD-3 (N+1-Hang Baubar-Tab): Bulk-Lookup analog GetItemSummariesAsync.
        // Eine IN-Query pro 500er-Chunk statt einer Query pro Parent.
        var result = new Dictionary<string, List<BlSubset>>(StringComparer.Ordinal);
        if (parentNos == null) return Task.FromResult(result);

        // Distinct, Leere raus - damit die IN-Klausel keine doppelten Parameter
        // hat (gleicher Stil wie GetItemSummariesAsync/GetItemNamesAsync).
        var input = parentNos
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (input.Count == 0) return Task.FromResult(result);

        const int chunkSize = 500;
        lock (_lock)
        {
            for (int offset = 0; offset < input.Count; offset += chunkSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = input.Skip(offset).Take(chunkSize).ToList();

                using var cmd = _connection.CreateCommand();
                cmd.Parameters.AddWithValue("$pt", parentType);
                var paramNames = new string[chunk.Count];
                for (int i = 0; i < chunk.Count; i++)
                {
                    var name = $"$p{i}";
                    paramNames[i] = name;
                    cmd.Parameters.AddWithValue(name, chunk[i]);
                }
                // Spalten-Reihenfolge + Sortierung IDENTISCH zu GetSubsetsAsync,
                // damit die je-Parent-Liste byte-fuer-byte dieselbe ist. Wir
                // nehmen parent_no als ersten Sort-Key auf, damit die Eintraege
                // pro Parent zusammenhaengen - innerhalb eines Parents bleibt die
                // Reihenfolge (match_id, color_id, item_no) erhalten.
                cmd.CommandText = @"
                    SELECT parent_type, parent_no, item_type, item_no, color_id,
                           quantity, extra_quantity, is_alternate, is_counterpart,
                           match_id, fetched_at, is_from_supersets
                    FROM bl_subsets
                    WHERE parent_type = $pt
                      AND parent_no IN (" + string.Join(",", paramNames) + @")
                    ORDER BY parent_no, match_id, color_id, item_no;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var sub = new BlSubset
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
                    };
                    if (!result.TryGetValue(sub.ParentNo, out var list))
                    {
                        list = new List<BlSubset>();
                        result[sub.ParentNo] = list;
                    }
                    list.Add(sub);
                }
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

        // Wir schreiben die Such-Tuples in eine TEMP-Tabelle und joinen
        // gegen bl_subsets. So skaliert die Abfrage linear mit der Input-
        // Groesse, statt eine riesige OR-Liste zu bauen. Eine OR-Liste mit
        // vielen tausend Klauseln sprengt sonst das SQLite-Limit
        // "Expression tree is too large (maximum depth 1000)" - das passiert
        // real seit BUILD-1 das ganze BL-Inventar (~9.000+ Tuples) uebergibt.
        //
        // Die TEMP-Tabelle wird mit CREATE IF NOT EXISTS angelegt und zu
        // Beginn geleert (DELETE), weil die SqliteConnection langlebig und
        // geteilt ist und ueber mehrere Aufrufe wiederverwendet wird.
        var result = new List<string>();
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();

            // TEMP-Tabelle anlegen + zuruecksetzen.
            using (var setup = _connection.CreateCommand())
            {
                setup.Transaction = transaction;
                setup.CommandText = @"
                    CREATE TEMP TABLE IF NOT EXISTS temp_search_parts (
                        part_no TEXT NOT NULL,
                        color_id INTEGER NOT NULL);
                    DELETE FROM temp_search_parts;";
                setup.ExecuteNonQuery();
            }

            // Bulk-Insert aller Such-Tuples via Prepared Statement
            // (Stil-Vorbild: ReplaceSubsetsAsync).
            using (var ins = _connection.CreateCommand())
            {
                ins.Transaction = transaction;
                ins.CommandText =
                    "INSERT INTO temp_search_parts (part_no, color_id) VALUES ($pn, $cid);";
                var pPn = ins.CreateParameter(); pPn.ParameterName = "$pn"; ins.Parameters.Add(pPn);
                var pCid = ins.CreateParameter(); pCid.ParameterName = "$cid"; ins.Parameters.Add(pCid);
                ins.Prepare();

                for (int i = 0; i < parts.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    pPn.Value = parts[i].PartNo;
                    pCid.Value = parts[i].ColorId;
                    ins.ExecuteNonQuery();
                }
            }

            // BUILD-1 Perf-Fix (v0.1.25): Index auf der Such-Tabelle anlegen,
            // NACH dem Bulk-Insert und VOR der JOIN-Query.
            //
            // Ohne diesen Index waehlt SQLite bei grosser bl_subsets-Tabelle
            // einen quadratischen Join-Plan (scannt alle bl_subsets-P-Zeilen und
            // probt die index-lose Such-Tabelle pro Zeile). Gemessen auf der
            // echten DB (1,38 Mio bl_subsets-Zeilen): 43,7s ohne Index vs 0,049s
            // mit Index - bei identischem Ergebnis (3651 Parents). Der Index auf
            // (part_no, color_id) macht den Join zum indizierten Lookup.
            //
            // CREATE INDEX IF NOT EXISTS ist idempotent: die TEMP-Tabelle wird
            // ueber Aufrufe wiederverwendet (CREATE TEMP TABLE IF NOT EXISTS +
            // DELETE). Der Index ueberlebt das DELETE (nur die Zeilen werden
            // geleert) - ab dem zweiten Aufruf existiert er also schon und der
            // Aufruf ist ein No-op.
            using (var idx = _connection.CreateCommand())
            {
                idx.Transaction = transaction;
                idx.CommandText =
                    "CREATE INDEX IF NOT EXISTS temp_idx_search_parts " +
                    "ON temp_search_parts(part_no, color_id);";
                idx.ExecuteNonQuery();
            }

            // JOIN gegen bl_subsets. Exakt dieselben WHERE-Bedingungen wie
            // die alte OR-Listen-Query, damit das Ergebnis identisch bleibt:
            //   parent_type = 'M' AND item_type = 'P' AND is_from_supersets = 0.
            using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = @"
                    SELECT DISTINCT s.parent_no
                    FROM bl_subsets s
                    INNER JOIN temp_search_parts t
                        ON s.item_no = t.part_no AND s.color_id = t.color_id
                    WHERE s.parent_type = 'M'
                      AND s.item_type = 'P'
                      AND s.is_from_supersets = 0;";

                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    result.Add(reader.GetString(0));
                }
            }

            transaction.Commit();
        }
        return Task.FromResult(result);
    }

    /// <summary>
    /// Test-/Diagnose-Hook (BUILD-1 Perf-Fix, v0.1.25): liefert den
    /// EXPLAIN-QUERY-PLAN-Text der JOIN-Query aus
    /// <see cref="FindMinifigsContainingPartsAsync"/> - exakt so wie die
    /// Methode sie absetzt, auf derselben langlebigen Connection.
    ///
    /// Die TEMP-Tabelle <c>temp_search_parts</c> ist connection-lokal und
    /// ueberlebt den Transaction-Commit der vorherigen Methode. Darum spiegelt
    /// dieser Plan, ob der nach dem Bulk-Insert angelegte Index
    /// <c>temp_idx_search_parts</c> wirklich existiert: ohne Index zeigt der
    /// Plan <c>SCAN temp_search_parts</c> (quadratischer Join), mit Index
    /// <c>SEARCH temp_search_parts USING INDEX</c> (indizierter Lookup).
    ///
    /// Bewusst public (statt internal + InternalsVisibleTo) - reiner
    /// Test-Hook, kein Architektur-Risiko. Voraussetzung: vorher muss einmal
    /// <see cref="FindMinifigsContainingPartsAsync"/> gelaufen sein, damit die
    /// TEMP-Tabelle auf dieser Connection existiert.
    /// </summary>
    public List<string> GetFindMinifigsJoinQueryPlanForDiagnostics()
    {
        var plan = new List<string>();
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            // Exakt dieselbe JOIN-Query wie in FindMinifigsContainingPartsAsync,
            // nur mit EXPLAIN QUERY PLAN davor.
            cmd.CommandText = @"
                EXPLAIN QUERY PLAN
                SELECT DISTINCT s.parent_no
                FROM bl_subsets s
                INNER JOIN temp_search_parts t
                    ON s.item_no = t.part_no AND s.color_id = t.color_id
                WHERE s.parent_type = 'M'
                  AND s.item_type = 'P'
                  AND s.is_from_supersets = 0;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // EXPLAIN QUERY PLAN liefert (id, parent, notused, detail).
                // Der "detail"-Text (letzte Spalte) enthaelt SCAN/SEARCH.
                plan.Add(reader.GetString(reader.FieldCount - 1));
            }
        }
        return plan;
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
        CancellationToken ct = default,
        string vatMode = "N",
        string countryCode = "")
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
                  AND region = $rg AND country_code = $cc AND currency = $cu
                  AND vat_mode = $vat;";
            cmd.Parameters.AddWithValue("$it", itemType);
            cmd.Parameters.AddWithValue("$in", itemNo);
            cmd.Parameters.AddWithValue("$cid", colorId);
            cmd.Parameters.AddWithValue("$gt", guideType);
            cmd.Parameters.AddWithValue("$nu", newOrUsed);
            cmd.Parameters.AddWithValue("$rg", region ?? string.Empty);
            cmd.Parameters.AddWithValue("$cc", countryCode ?? string.Empty);
            cmd.Parameters.AddWithValue("$cu", currency);
            cmd.Parameters.AddWithValue("$vat", string.IsNullOrEmpty(vatMode) ? "N" : vatMode);

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
        CancellationToken ct = default,
        string vatMode = "N",
        string countryCode = "")
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO bl_prices
                    (item_type, item_no, color_id, guide_type, new_or_used,
                     region, country_code, currency, vat_mode,
                     min_price, avg_price, qty_avg_price, max_price,
                     unit_quantity, total_quantity, fetched_at)
                VALUES
                    ($it, $in, $cid, $gt, $nu, $rg, $cc, $cu, $vat,
                     $min, $avg, $qty, $max, $uq, $tq, $fet);";
            cmd.Parameters.AddWithValue("$it", itemType);
            cmd.Parameters.AddWithValue("$in", itemNo);
            cmd.Parameters.AddWithValue("$cid", colorId);
            cmd.Parameters.AddWithValue("$gt", guideType);
            cmd.Parameters.AddWithValue("$nu", newOrUsed);
            cmd.Parameters.AddWithValue("$rg", region ?? string.Empty);
            cmd.Parameters.AddWithValue("$cc", countryCode ?? string.Empty);
            cmd.Parameters.AddWithValue("$cu", currency);
            cmd.Parameters.AddWithValue("$vat", string.IsNullOrEmpty(vatMode) ? "N" : vatMode);
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
        CancellationToken ct = default,
        string vatMode = "N",
        string countryCode = "")
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
                  AND region = $rg AND country_code = $cc AND currency = $cu
                  AND vat_mode = $vat;";
            cmd.Parameters.AddWithValue("$it", itemType);
            cmd.Parameters.AddWithValue("$in", itemNo);
            cmd.Parameters.AddWithValue("$cid", colorId);
            cmd.Parameters.AddWithValue("$gt", guideType);
            cmd.Parameters.AddWithValue("$nu", newOrUsed);
            cmd.Parameters.AddWithValue("$rg", region ?? string.Empty);
            cmd.Parameters.AddWithValue("$cc", countryCode ?? string.Empty);
            cmd.Parameters.AddWithValue("$cu", currency);
            cmd.Parameters.AddWithValue("$vat", string.IsNullOrEmpty(vatMode) ? "N" : vatMode);

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
        CancellationToken ct = default,
        string vatMode = "N",
        string countryCode = "")
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM bl_prices
                WHERE item_type = $it AND item_no = $in AND color_id = $cid
                  AND guide_type = $gt AND new_or_used = $nu
                  AND region = $rg AND country_code = $cc AND currency = $cu
                  AND vat_mode = $vat;";
            cmd.Parameters.AddWithValue("$it", itemType);
            cmd.Parameters.AddWithValue("$in", itemNo);
            cmd.Parameters.AddWithValue("$cid", colorId);
            cmd.Parameters.AddWithValue("$gt", guideType);
            cmd.Parameters.AddWithValue("$nu", newOrUsed);
            cmd.Parameters.AddWithValue("$rg", region ?? string.Empty);
            cmd.Parameters.AddWithValue("$cc", countryCode ?? string.Empty);
            cmd.Parameters.AddWithValue("$cu", currency);
            cmd.Parameters.AddWithValue("$vat", string.IsNullOrEmpty(vatMode) ? "N" : vatMode);
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

    public Task<int> ClearEmptyPricesAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            // Anti-Vergiftung: Eintraege ohne jeden Preis + ohne Quantity sind
            // BL-API-Antworten ohne Treffer (z.B. wegen leere Filter, vor dem
            // null-Fix). Sie blockieren spaetere Lookups, weil der Cache-Hit
            // verhindert dass die API erneut gefragt wird.
            // COALESCE(...,0) damit auch NULL-zaehlt-als-leer fuer total_quantity.
            cmd.CommandText = @"
                DELETE FROM bl_prices
                WHERE min_price IS NULL
                  AND avg_price IS NULL
                  AND qty_avg_price IS NULL
                  AND max_price IS NULL
                  AND COALESCE(total_quantity, 0) = 0;";
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
            {
                Log.Information("bl_prices Anti-Vergiftung: {Count} leere Eintraege geloescht", deleted);
            }
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
