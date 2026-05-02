using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Data.Sqlite;
using Serilog;

namespace LegoMinifigSorter.Core.Services;

/// <summary>
/// Implementierung des Catalog-Imports.
///
/// Architektur-Entscheidung:
/// Wir verwenden Microsoft.Data.Sqlite direkt (nicht EF Core), weil EF bei
/// 1,5+ Mio. Zeilen Bulk-Insert deutlich zu langsam ist. Hier reden wir ueber
/// 30-90 Sekunden mit raw ADO.NET vs. mehrere Minuten mit EF.
///
/// Optimierungen:
///   - Eine einzelne Transaktion um den ganzen Import
///   - Prepared Statements (Parameter-Bindings, einmal kompiliert)
///   - PRAGMA journal_mode=MEMORY und synchronous=OFF waehrend des Imports
///     (fuer Bulk-Insert OK, weil bei Fehler eh die ganze DB verworfen wird)
///   - Indexe NACH dem Bulk-Insert anlegen (10x schneller)
///   - VACUUM und PRAGMA optimize am Ende
/// </summary>
public class CatalogImporter : ICatalogImporter
{
    /// <summary>
    /// Liste der erwarteten Tabellen-Namen (= Dateinamen ohne ".csv.zip").
    /// Reihenfolge entspricht der Reihenfolge im Import (wichtig wegen FKs zwischen
    /// inventories und inventory_parts).
    /// </summary>
    private static readonly string[] ExpectedFiles =
    {
        "colors",
        "part_categories",
        "parts",
        "part_relationships",
        "minifigs",
        "sets",
        "inventories",
        "inventory_parts",
        "inventory_minifigs",
        "inventory_sets",
        "elements"
    };

    public async Task ImportFromEmbeddedAsync(
        string targetDbPath,
        IProgress<CatalogImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Log.Information("Catalog-Import aus Embedded Resources gestartet");

        // ZIPs aus Embedded Resources in einen temporaeren Ordner extrahieren
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"lego-catalog-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            ExtractEmbeddedZips(tempDir);

            // Pfade zu den nun lokalen ZIPs sammeln
            var zipPaths = ExpectedFiles
                .Select(f => Path.Combine(tempDir, $"{f}.csv.zip"))
                .Where(File.Exists)
                .ToList();

            await ImportFromZipsAsync(zipPaths, targetDbPath, progress, cancellationToken);

            Log.Information("Catalog-Import aus Embedded Resources erfolgreich abgeschlossen");
        }
        finally
        {
            // Temp-Ordner aufraeumen (best effort)
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { Log.Warning(ex, "Konnte Temp-Ordner nicht loeschen: {Path}", tempDir); }
        }
    }

    public async Task ImportFromZipsAsync(
        IEnumerable<string> zipPaths,
        string targetDbPath,
        IProgress<CatalogImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var zipList = zipPaths.ToList();

        // Schritt-Anzahl: Schema anlegen + 11 CSV-Importe + Indexe + Metadata + Optimize
        // = 1 + ExpectedFiles.Length + 1 + 1 + 1 = 15
        const int extraSteps = 4;
        var totalSteps = ExpectedFiles.Length + extraSteps;
        var currentStep = 0;

        // Falls die Ziel-DB existiert, vorher loeschen (wir bauen frisch auf)
        if (File.Exists(targetDbPath))
        {
            File.Delete(targetDbPath);
        }

        // Verbindung zur neuen DB oeffnen
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = targetDbPath,
            // Mode = ReadWriteCreate ist Default, aber explizit fuer Klarheit:
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Performance-PRAGMAs fuer Bulk-Insert.
        // synchronous=OFF: bei Crash ist die DB evtl. korrupt, aber wir schmeissen
        // sie dann eh weg und importieren neu.
        ExecutePragmas(connection,
            "PRAGMA journal_mode = MEMORY;",
            "PRAGMA synchronous = OFF;",
            "PRAGMA temp_store = MEMORY;",
            "PRAGMA cache_size = -65536;"); // 64 MB Cache

        // === Schritt 1: Schema anlegen ===
        ReportProgress(progress, ++currentStep, totalSteps, "Erstelle Tabellen-Schema...");
        CreateSchema(connection);

        // === Schritte 2-12: CSV-Importe (in Transaktion!) ===
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var rowCounts = new Dictionary<string, long>();

        try
        {
            foreach (var fileName in ExpectedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var zipPath = zipList.FirstOrDefault(p =>
                    Path.GetFileName(p).Equals($"{fileName}.csv.zip", StringComparison.OrdinalIgnoreCase));

                if (zipPath == null)
                {
                    throw new FileNotFoundException(
                        $"Erwartete Datei '{fileName}.csv.zip' wurde nicht uebergeben.");
                }

                ReportProgress(progress, ++currentStep, totalSteps,
                    $"Importiere {fileName}...");

                var rows = await ImportCsvFromZipAsync(connection, transaction, fileName, zipPath, progress, currentStep, totalSteps, cancellationToken);
                rowCounts[fileName] = rows;

                Log.Debug("Import {File}: {Rows} Zeilen", fileName, rows);
            }

            // === Schritt 13: Indexe anlegen ===
            ReportProgress(progress, ++currentStep, totalSteps, "Lege Indexe an...");
            CreateIndexes(connection, transaction);

            // === Schritt 14: Metadaten schreiben ===
            ReportProgress(progress, ++currentStep, totalSteps, "Schreibe Metadaten...");
            WriteMetadata(connection, transaction, rowCounts);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        // === Schritt 15: Optimieren ===
        ReportProgress(progress, ++currentStep, totalSteps, "Optimiere Datenbank...");
        ExecutePragmas(connection,
            "PRAGMA optimize;",
            "PRAGMA journal_mode = DELETE;", // zurueck auf normal fuer spaeteren Lese-Betrieb
            "PRAGMA synchronous = NORMAL;");

        // VACUUM ausserhalb einer Transaktion
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "VACUUM;";
            cmd.ExecuteNonQuery();
        }

        ReportProgress(progress, totalSteps, totalSteps, "Fertig.");

        Log.Information("Catalog-Import abgeschlossen: {Counts}",
            string.Join(", ", rowCounts.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    // ========================================================================
    // Hilfsmethoden
    // ========================================================================

    /// <summary>
    /// Extrahiert die in der Core-DLL eingebetteten ZIPs in einen Zielordner.
    /// </summary>
    private static void ExtractEmbeddedZips(string targetDir)
    {
        var assembly = typeof(CatalogImporter).Assembly;
        // Praefix wie in der csproj definiert
        const string prefix = "LegoMinifigSorter.Core.Resources.CatalogSeed.";

        foreach (var fileName in ExpectedFiles)
        {
            var resourceName = $"{prefix}{fileName}.csv.zip";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new FileNotFoundException(
                    $"Embedded Resource '{resourceName}' nicht gefunden. " +
                    $"Wurde die ZIP in der .csproj als EmbeddedResource eingebunden?");
            }

            var outPath = Path.Combine(targetDir, $"{fileName}.csv.zip");
            using var outFile = File.Create(outPath);
            stream.CopyTo(outFile);
        }

        Log.Debug("{Count} Embedded-ZIPs extrahiert nach {Path}", ExpectedFiles.Length, targetDir);
    }

    /// <summary>Fuehrt mehrere PRAGMA-Statements aus.</summary>
    private static void ExecutePragmas(SqliteConnection connection, params string[] pragmas)
    {
        foreach (var pragma in pragmas)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = pragma;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Legt das komplette catalog.db-Schema an (siehe CLAUDE.md).
    /// Indexe werden hier NICHT erstellt (kommen erst nach dem Bulk-Insert).
    /// </summary>
    private static void CreateSchema(SqliteConnection connection)
    {
        const string sql = @"
            CREATE TABLE metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE colors (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                rgb TEXT NOT NULL,
                is_trans INTEGER NOT NULL
            );

            CREATE TABLE part_categories (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
            );

            CREATE TABLE parts (
                part_num TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                part_cat_id INTEGER,
                part_material TEXT
            );

            CREATE TABLE part_relationships (
                rel_type TEXT NOT NULL,
                child_part_num TEXT NOT NULL,
                parent_part_num TEXT NOT NULL
            );

            CREATE TABLE minifigs (
                fig_num TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                num_parts INTEGER NOT NULL,
                img_url TEXT
            );

            CREATE TABLE sets (
                set_num TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                year INTEGER,
                theme_id INTEGER,
                num_parts INTEGER,
                img_url TEXT
            );

            CREATE TABLE inventories (
                id INTEGER PRIMARY KEY,
                version INTEGER,
                set_num TEXT NOT NULL
            );

            CREATE TABLE inventory_parts (
                inventory_id INTEGER NOT NULL,
                part_num TEXT NOT NULL,
                color_id INTEGER NOT NULL,
                quantity INTEGER NOT NULL,
                is_spare INTEGER NOT NULL,
                img_url TEXT
            );

            CREATE TABLE inventory_minifigs (
                inventory_id INTEGER NOT NULL,
                fig_num TEXT NOT NULL,
                quantity INTEGER NOT NULL
            );

            CREATE TABLE inventory_sets (
                inventory_id INTEGER NOT NULL,
                set_num TEXT NOT NULL,
                quantity INTEGER NOT NULL
            );

            CREATE TABLE elements (
                element_id TEXT PRIMARY KEY,
                part_num TEXT NOT NULL,
                color_id INTEGER NOT NULL,
                design_id TEXT
            );
        ";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Legt die Indexe an. Wird NACH dem Bulk-Insert ausgefuehrt – das ist
    /// um ein Vielfaches schneller, weil SQLite den Index sonst bei jedem
    /// INSERT aktualisieren muesste.
    /// </summary>
    private static void CreateIndexes(SqliteConnection connection, SqliteTransaction transaction)
    {
        const string sql = @"
            CREATE INDEX idx_partrel_child       ON part_relationships(child_part_num);
            CREATE INDEX idx_partrel_parent      ON part_relationships(parent_part_num);
            CREATE INDEX idx_minifigs_name       ON minifigs(name);
            CREATE INDEX idx_inventories_setnum  ON inventories(set_num);
            CREATE INDEX idx_invparts_inventory  ON inventory_parts(inventory_id);
            CREATE INDEX idx_invparts_partcolor  ON inventory_parts(part_num, color_id);
            CREATE INDEX idx_invmf_fignum        ON inventory_minifigs(fig_num);
        ";

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Schreibt Metadaten ueber den Import in die metadata-Tabelle.
    /// Die App liest diese spaeter unter Settings -> Tab Katalog.
    /// </summary>
    private static void WriteMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IDictionary<string, long> rowCounts)
    {
        // Lokaler Helper, weil GetValueOrDefault auf IDictionary<string,long>
        // in .NET 8 nicht direkt verfuegbar ist (nur fuer IReadOnlyDictionary).
        long Get(string k) => rowCounts.TryGetValue(k, out var v) ? v : 0;

        var meta = new Dictionary<string, string>
        {
            ["imported_at"]   = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["minifig_count"] = Get("minifigs").ToString(CultureInfo.InvariantCulture),
            ["part_count"]    = Get("parts").ToString(CultureInfo.InvariantCulture),
            ["color_count"]   = Get("colors").ToString(CultureInfo.InvariantCulture),
            ["set_count"]     = Get("sets").ToString(CultureInfo.InvariantCulture),
            ["schema_version"] = "1"
        };

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO metadata (key, value) VALUES ($key, $value);";
        var pKey = cmd.CreateParameter(); pKey.ParameterName = "$key";
        var pVal = cmd.CreateParameter(); pVal.ParameterName = "$value";
        cmd.Parameters.Add(pKey);
        cmd.Parameters.Add(pVal);

        foreach (var (key, value) in meta)
        {
            pKey.Value = key;
            pVal.Value = value;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Importiert eine einzelne CSV-Datei aus einer ZIP-Datei.
    /// Streamt zeilenweise mit CsvHelper (kein voller File-Read in den RAM)
    /// und nutzt Prepared Statements pro Tabelle.
    /// </summary>
    private static async Task<long> ImportCsvFromZipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string zipPath,
        IProgress<CatalogImportProgress>? progress,
        int currentStep,
        int totalSteps,
        CancellationToken cancellationToken)
    {
        using var zipFile = ZipFile.OpenRead(zipPath);

        // Wir gehen davon aus, dass im ZIP genau ein CSV liegt
        var entry = zipFile.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            throw new InvalidDataException(
                $"In {Path.GetFileName(zipPath)} wurde keine .csv-Datei gefunden.");
        }

        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            // Rebrickable-CSVs sind Standard-Comma-Separated, koennen aber Anfuehrungszeichen
            // enthalten (z.B. Set-Namen mit Komma). CsvHelper handelt das korrekt.
        });

        await csv.ReadAsync();
        csv.ReadHeader();

        // Prepared Statement und Parameter-Liste pro Tabelle aufbauen
        using var cmd = BuildInsertCommand(connection, transaction, tableName);

        long rowCount = 0;
        const int progressInterval = 5000;

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            FillParameters(cmd, tableName, csv);
            cmd.ExecuteNonQuery();
            rowCount++;

            // Alle 5000 Zeilen UI updaten – haeufiger waere unnoetiger Overhead
            if (rowCount % progressInterval == 0 && progress != null)
            {
                progress.Report(new CatalogImportProgress
                {
                    CurrentStep = currentStep,
                    TotalSteps = totalSteps,
                    Message = $"Importiere {tableName}... {rowCount:N0} Zeilen",
                    RowsProcessed = rowCount
                });
            }
        }

        return rowCount;
    }

    /// <summary>
    /// Erzeugt das SQL-INSERT-Statement fuer eine bestimmte Tabelle, einmal,
    /// und legt die Parameter an. Wird dann fuer jede Zeile mit FillParameters
    /// neu befuellt.
    /// </summary>
    private static SqliteCommand BuildInsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName)
    {
        var (sql, paramNames) = GetInsertSqlForTable(tableName);

        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;

        foreach (var name in paramNames)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            cmd.Parameters.Add(p);
        }
        // CommandText ist final, jetzt einmal preparen
        cmd.Prepare();
        return cmd;
    }

    /// <summary>
    /// Liefert das passende INSERT-Statement plus Parameter-Namen pro Tabelle.
    /// </summary>
    private static (string sql, string[] paramNames) GetInsertSqlForTable(string tableName)
    {
        return tableName switch
        {
            "colors" => (
                "INSERT INTO colors (id, name, rgb, is_trans) VALUES ($id, $name, $rgb, $is_trans);",
                new[] { "$id", "$name", "$rgb", "$is_trans" }),

            "part_categories" => (
                "INSERT INTO part_categories (id, name) VALUES ($id, $name);",
                new[] { "$id", "$name" }),

            "parts" => (
                "INSERT INTO parts (part_num, name, part_cat_id, part_material) VALUES ($part_num, $name, $part_cat_id, $part_material);",
                new[] { "$part_num", "$name", "$part_cat_id", "$part_material" }),

            "part_relationships" => (
                "INSERT INTO part_relationships (rel_type, child_part_num, parent_part_num) VALUES ($rel_type, $child, $parent);",
                new[] { "$rel_type", "$child", "$parent" }),

            "minifigs" => (
                "INSERT INTO minifigs (fig_num, name, num_parts, img_url) VALUES ($fig_num, $name, $num_parts, $img_url);",
                new[] { "$fig_num", "$name", "$num_parts", "$img_url" }),

            "sets" => (
                "INSERT INTO sets (set_num, name, year, theme_id, num_parts, img_url) VALUES ($set_num, $name, $year, $theme_id, $num_parts, $img_url);",
                new[] { "$set_num", "$name", "$year", "$theme_id", "$num_parts", "$img_url" }),

            "inventories" => (
                "INSERT INTO inventories (id, version, set_num) VALUES ($id, $version, $set_num);",
                new[] { "$id", "$version", "$set_num" }),

            "inventory_parts" => (
                "INSERT INTO inventory_parts (inventory_id, part_num, color_id, quantity, is_spare, img_url) VALUES ($inv, $part_num, $color_id, $qty, $is_spare, $img_url);",
                new[] { "$inv", "$part_num", "$color_id", "$qty", "$is_spare", "$img_url" }),

            "inventory_minifigs" => (
                "INSERT INTO inventory_minifigs (inventory_id, fig_num, quantity) VALUES ($inv, $fig_num, $qty);",
                new[] { "$inv", "$fig_num", "$qty" }),

            "inventory_sets" => (
                "INSERT INTO inventory_sets (inventory_id, set_num, quantity) VALUES ($inv, $set_num, $qty);",
                new[] { "$inv", "$set_num", "$qty" }),

            "elements" => (
                "INSERT INTO elements (element_id, part_num, color_id, design_id) VALUES ($eid, $part_num, $color_id, $design_id);",
                new[] { "$eid", "$part_num", "$color_id", "$design_id" }),

            _ => throw new ArgumentException($"Unbekannte Tabelle '{tableName}'")
        };
    }

    /// <summary>
    /// Liest die aktuelle CSV-Zeile aus dem CsvReader und schreibt die Werte
    /// in die Parameter des prepared Commands. Pro Tabelle eigene Logik.
    /// </summary>
    private static void FillParameters(SqliteCommand cmd, string tableName, CsvReader csv)
    {
        switch (tableName)
        {
            case "colors":
                cmd.Parameters["$id"].Value       = ParseInt(csv.GetField("id"));
                cmd.Parameters["$name"].Value     = csv.GetField("name") ?? string.Empty;
                cmd.Parameters["$rgb"].Value      = csv.GetField("rgb") ?? string.Empty;
                cmd.Parameters["$is_trans"].Value = ParseBool(csv.GetField("is_trans"));
                break;

            case "part_categories":
                cmd.Parameters["$id"].Value   = ParseInt(csv.GetField("id"));
                cmd.Parameters["$name"].Value = csv.GetField("name") ?? string.Empty;
                break;

            case "parts":
                cmd.Parameters["$part_num"].Value      = csv.GetField("part_num") ?? string.Empty;
                cmd.Parameters["$name"].Value          = csv.GetField("name") ?? string.Empty;
                cmd.Parameters["$part_cat_id"].Value   = ParseNullableInt(csv.GetField("part_cat_id"));
                cmd.Parameters["$part_material"].Value = NullableString(csv.GetField("part_material"));
                break;

            case "part_relationships":
                cmd.Parameters["$rel_type"].Value = csv.GetField("rel_type") ?? string.Empty;
                cmd.Parameters["$child"].Value    = csv.GetField("child_part_num") ?? string.Empty;
                cmd.Parameters["$parent"].Value   = csv.GetField("parent_part_num") ?? string.Empty;
                break;

            case "minifigs":
                cmd.Parameters["$fig_num"].Value   = csv.GetField("fig_num") ?? string.Empty;
                cmd.Parameters["$name"].Value      = csv.GetField("name") ?? string.Empty;
                cmd.Parameters["$num_parts"].Value = ParseInt(csv.GetField("num_parts"));
                cmd.Parameters["$img_url"].Value   = NullableString(csv.GetField("img_url"));
                break;

            case "sets":
                cmd.Parameters["$set_num"].Value   = csv.GetField("set_num") ?? string.Empty;
                cmd.Parameters["$name"].Value      = csv.GetField("name") ?? string.Empty;
                cmd.Parameters["$year"].Value      = ParseNullableInt(csv.GetField("year"));
                cmd.Parameters["$theme_id"].Value  = ParseNullableInt(csv.GetField("theme_id"));
                cmd.Parameters["$num_parts"].Value = ParseNullableInt(csv.GetField("num_parts"));
                cmd.Parameters["$img_url"].Value   = NullableString(csv.GetField("img_url"));
                break;

            case "inventories":
                cmd.Parameters["$id"].Value      = ParseInt(csv.GetField("id"));
                cmd.Parameters["$version"].Value = ParseNullableInt(csv.GetField("version"));
                cmd.Parameters["$set_num"].Value = csv.GetField("set_num") ?? string.Empty;
                break;

            case "inventory_parts":
                cmd.Parameters["$inv"].Value      = ParseInt(csv.GetField("inventory_id"));
                cmd.Parameters["$part_num"].Value = csv.GetField("part_num") ?? string.Empty;
                cmd.Parameters["$color_id"].Value = ParseInt(csv.GetField("color_id"));
                cmd.Parameters["$qty"].Value      = ParseInt(csv.GetField("quantity"));
                cmd.Parameters["$is_spare"].Value = ParseBool(csv.GetField("is_spare"));
                cmd.Parameters["$img_url"].Value  = NullableString(csv.GetField("img_url"));
                break;

            case "inventory_minifigs":
                cmd.Parameters["$inv"].Value     = ParseInt(csv.GetField("inventory_id"));
                cmd.Parameters["$fig_num"].Value = csv.GetField("fig_num") ?? string.Empty;
                cmd.Parameters["$qty"].Value     = ParseInt(csv.GetField("quantity"));
                break;

            case "inventory_sets":
                cmd.Parameters["$inv"].Value     = ParseInt(csv.GetField("inventory_id"));
                cmd.Parameters["$set_num"].Value = csv.GetField("set_num") ?? string.Empty;
                cmd.Parameters["$qty"].Value     = ParseInt(csv.GetField("quantity"));
                break;

            case "elements":
                cmd.Parameters["$eid"].Value       = csv.GetField("element_id") ?? string.Empty;
                cmd.Parameters["$part_num"].Value  = csv.GetField("part_num") ?? string.Empty;
                cmd.Parameters["$color_id"].Value  = ParseInt(csv.GetField("color_id"));
                cmd.Parameters["$design_id"].Value = NullableString(csv.GetField("design_id"));
                break;
        }
    }

    // --- Wert-Konvertierungen (Rebrickable nutzt "True"/"False" und leere Strings) ---

    private static int ParseInt(string? value)
        => int.Parse(value ?? "0", CultureInfo.InvariantCulture);

    private static object ParseNullableInt(string? value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : int.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Rebrickable schreibt Booleans als "True"/"False" (CSV) bzw. teilweise auch
    /// als "t"/"f" oder "0"/"1". Wir akzeptieren alles und speichern als 0/1 in SQLite.
    /// </summary>
    private static int ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "t" or "1" or "yes" or "y" => 1,
            _ => 0
        };
    }

    /// <summary>Gibt DBNull zurueck wenn der String leer ist, sonst den String.</summary>
    private static object NullableString(string? value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static void ReportProgress(
        IProgress<CatalogImportProgress>? progress,
        int current, int total, string message)
    {
        progress?.Report(new CatalogImportProgress
        {
            CurrentStep = current,
            TotalSteps = total,
            Message = message
        });
    }
}
