using System.Globalization;
using System.Net.Http;
using HBSort.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Persistenter Bild-Cache mit SQLite-Sidecar fuer LRU-Tracking.
///
/// Architektur-Hinweis:
///   - Bild-Dateien liegen flach im imagesDir.
///   - Metadaten in einer separaten image_cache.db (NICHT in userdata.db oder bl_cache.db,
///     damit Cache-Loeschen die DBs nicht beruehrt).
///   - Cache-Limit wird ueber ISettingsService abgefragt - LRU-Eviction reduziert den Cache
///     auf 90% des Limits, damit nach jedem Add nicht sofort wieder evicted werden muss.
/// </summary>
public class PersistentImageCache : IPersistentImageCache, IDisposable
{
    /// <summary>Default-Pfad zum Bild-Cache (%APPDATA%/HBSort/images/).</summary>
    public static readonly string DefaultImagesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HBSort", "images");

    /// <summary>Default-Pfad zur Sidecar-DB.</summary>
    public static readonly string DefaultMetaDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HBSort", "image_cache.db");

    /// <summary>
    /// Bei Eviction reduzieren wir auf diesen Anteil des Limits, damit nicht jeder
    /// einzelne Add eine erneute Eviction triggert.
    /// </summary>
    private const double EvictionTargetRatio = 0.90;

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly string _imagesDir;
    private readonly string _metaDbPath;
    private readonly object _lock = new();
    private readonly SqliteConnection _connection;

    public event EventHandler? StatsChanged;

    public PersistentImageCache(HttpClient http, ISettingsService settings)
        : this(http, settings, DefaultImagesDir, DefaultMetaDbPath)
    {
    }

    /// <summary>Test-/Konfigurations-Konstruktor mit eigenem Cache-Verzeichnis und DB-Pfad.</summary>
    public PersistentImageCache(HttpClient http, ISettingsService settings, string imagesDir, string metaDbPath)
    {
        _http = http;
        _settings = settings;
        _imagesDir = imagesDir;
        _metaDbPath = metaDbPath;

        Directory.CreateDirectory(_imagesDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_metaDbPath)!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _metaDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        _connection.Open();

        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS cache_entries (
                cache_key TEXT PRIMARY KEY,
                file_name TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                added_at TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                source_url TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_cache_lru ON cache_entries(last_accessed_at);
        ";
        cmd.ExecuteNonQuery();
    }

    // ========================================================================
    // GetOrDownloadAsync
    // ========================================================================

    public async Task<string> GetOrDownloadAsync(string sourceUrl, string cacheKey, CancellationToken ct = default)
    {
        var localPath = Path.Combine(_imagesDir, cacheKey);

        // Cache-Hit?
        if (File.Exists(localPath) && IsRegistered(cacheKey))
        {
            TouchAccess(cacheKey);
            Log.Debug("Cache-Hit {Key}", cacheKey);
            return localPath;
        }

        // Falls die Datei zwar existiert aber kein Eintrag in der DB: Datei verwerfen
        // und neu laden, damit Stats konsistent bleiben.
        if (File.Exists(localPath) && !IsRegistered(cacheKey))
        {
            try { File.Delete(localPath); } catch { /* ignore */ }
        }

        // Download
        Log.Debug("Cache-Miss {Key} -> download {Url}", cacheKey, sourceUrl);
        var bytes = await DownloadBytesAsync(sourceUrl, ct);

        // Atomisch schreiben (.tmp -> Move)
        var tmpPath = localPath + ".tmp";
        await File.WriteAllBytesAsync(tmpPath, bytes, ct);
        if (File.Exists(localPath)) File.Delete(localPath);
        File.Move(tmpPath, localPath);

        // Metadaten registrieren + LRU-Eviction pruefen
        RegisterEntry(cacheKey, bytes.LongLength, sourceUrl);
        EvictIfOverLimit();

        StatsChanged?.Invoke(this, EventArgs.Empty);
        return localPath;
    }

    private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // ========================================================================
    // TouchAccess + Metadaten
    // ========================================================================

    public void TouchAccess(string cacheKey)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE cache_entries SET last_accessed_at = $now WHERE cache_key = $key;";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$key", cacheKey);
            cmd.ExecuteNonQuery();
        }
        // Touch ist ein leichter Stats-Wechsel (oldest_access aendert sich).
        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsRegistered(string cacheKey)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM cache_entries WHERE cache_key = $key LIMIT 1;";
            cmd.Parameters.AddWithValue("$key", cacheKey);
            using var reader = cmd.ExecuteReader();
            return reader.Read();
        }
    }

    private void RegisterEntry(string cacheKey, long sizeBytes, string sourceUrl)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO cache_entries (cache_key, file_name, size_bytes, added_at, last_accessed_at, source_url)
                VALUES ($key, $file, $size, $now, $now, $url)
                ON CONFLICT(cache_key) DO UPDATE SET
                    size_bytes = excluded.size_bytes,
                    last_accessed_at = excluded.last_accessed_at,
                    source_url = excluded.source_url;
            ";
            cmd.Parameters.AddWithValue("$key", cacheKey);
            cmd.Parameters.AddWithValue("$file", cacheKey); // Schema fuehrt file_name separat - wir nutzen denselben Wert
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$url", sourceUrl);
            cmd.ExecuteNonQuery();
        }
    }

    // ========================================================================
    // Stats + LRU-Eviction
    // ========================================================================

    public CacheStats GetStats()
    {
        lock (_lock)
        {
            long limitBytes = (long)_settings.Current.ImageCache.LimitMb * 1024L * 1024L;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(size_bytes), 0), MIN(last_accessed_at) FROM cache_entries;";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var count = reader.GetInt32(0);
                var total = reader.GetInt64(1);
                DateTime? oldest = null;
                if (!reader.IsDBNull(2))
                {
                    if (DateTime.TryParse(reader.GetString(2), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var dt))
                    {
                        oldest = dt;
                    }
                }
                return new CacheStats(count, total, limitBytes, oldest);
            }
            return new CacheStats(0, 0, limitBytes, null);
        }
    }

    /// <summary>
    /// Falls Cache groesser als Limit: aelteste Eintraege bis 90% des Limits loeschen.
    /// LimitMb = 0 -> unbegrenzt, kein Eviction.
    /// </summary>
    private void EvictIfOverLimit()
    {
        var limitMb = _settings.Current.ImageCache.LimitMb;
        if (limitMb <= 0) return; // unbegrenzt

        long limitBytes = (long)limitMb * 1024L * 1024L;
        long targetBytes = (long)(limitBytes * EvictionTargetRatio);

        long currentSize;
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM cache_entries;";
            currentSize = (long)cmd.ExecuteScalar()!;
        }

        if (currentSize <= limitBytes) return;

        Log.Information("Cache ueber Limit ({Current} > {Limit} bytes) - starte LRU-Eviction auf {Target}",
            currentSize, limitBytes, targetBytes);

        // Kandidaten in LRU-Reihenfolge holen
        var deleted = 0;
        lock (_lock)
        {
            using var selectCmd = _connection.CreateCommand();
            selectCmd.CommandText = @"
                SELECT cache_key, file_name, size_bytes
                FROM cache_entries
                ORDER BY last_accessed_at ASC;
            ";

            var toDelete = new List<(string key, string file, long size)>();
            using (var reader = selectCmd.ExecuteReader())
            {
                while (reader.Read() && currentSize > targetBytes)
                {
                    var key = reader.GetString(0);
                    var file = reader.GetString(1);
                    var size = reader.GetInt64(2);
                    toDelete.Add((key, file, size));
                    currentSize -= size;
                }
            }

            using var transaction = _connection.BeginTransaction();
            foreach (var (key, file, _) in toDelete)
            {
                // Datei loeschen (best effort)
                var path = Path.Combine(_imagesDir, file);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { Log.Warning(ex, "Konnte Cache-Datei nicht loeschen: {Path}", path); }

                using var deleteCmd = _connection.CreateCommand();
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM cache_entries WHERE cache_key = $key;";
                deleteCmd.Parameters.AddWithValue("$key", key);
                deleteCmd.ExecuteNonQuery();
                deleted++;
            }
            transaction.Commit();
        }

        if (deleted > 0)
        {
            Log.Information("Cache-Eviction: {Count} Dateien geloescht", deleted);
        }
    }

    // ========================================================================
    // ClearAsync
    // ========================================================================

    public Task<int> ClearAsync()
    {
        int deleted = 0;
        List<string> files;
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT file_name FROM cache_entries;";
            files = new List<string>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read()) files.Add(reader.GetString(0));
            }

            using var del = _connection.CreateCommand();
            del.CommandText = "DELETE FROM cache_entries;";
            del.ExecuteNonQuery();
        }

        foreach (var file in files)
        {
            var path = Path.Combine(_imagesDir, file);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Konnte Cache-Datei nicht loeschen: {Path}", path);
            }
        }

        // Auch eventuelle Waisen-Dateien (nicht in DB) ausraeumen, damit die
        // Cache-Anzeige sauber bleibt.
        try
        {
            foreach (var path in Directory.GetFiles(_imagesDir))
            {
                try { File.Delete(path); deleted++; } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        StatsChanged?.Invoke(this, EventArgs.Empty);
        // Async-Signature laut Interface - die Implementierung ist aber synchron
        // (SQLite + File-IO sind hier ohnehin blockierend). Wrapping mit FromResult
        // statt async/await spart die State-Machine.
        return Task.FromResult(deleted);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
