using System.IO;
using System.IO.Compression;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;

namespace HBSort.Tests;

/// <summary>
/// UX X.29 (v0.1.16): Tests fuer BackupService.
/// Nutzt jeweils ein temporaeres "AppData"-Verzeichnis, das nach jedem Test
/// aufgeraeumt wird. Echte SQLite-Connections - die Online-Backup-API ist
/// genau das was hier gepruefen werden soll.
/// </summary>
public class BackupServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BackupService _sut;

    public BackupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "HBSort-BackupTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sut = new BackupService(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* egal */ }
    }

    [Fact]
    public async Task CreateBackup_with_no_source_files_creates_empty_zip()
    {
        // Keine userdata.db, keine bl_cache.db, keine settings.json -> ZIP
        // wird trotzdem angelegt, ist halt leer.
        var result = await _sut.CreateBackupAsync();

        Assert.NotNull(result);
        Assert.StartsWith("HBSort-Backup-", result.FileName);
        Assert.EndsWith(".zip", result.FileName);

        var fullPath = Path.Combine(_tempDir, "backups", result.FileName);
        Assert.True(File.Exists(fullPath));

        using var zip = ZipFile.OpenRead(fullPath);
        Assert.Empty(zip.Entries);
    }

    [Fact]
    public async Task CreateBackup_packs_userdata_and_settings_into_zip()
    {
        await SeedSqliteDb(Path.Combine(_tempDir, "userdata.db"));
        await SeedSqliteDb(Path.Combine(_tempDir, "bl_cache.db"));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "settings.json"),
            "{\"hello\":\"world\"}");

        var result = await _sut.CreateBackupAsync();

        var fullPath = Path.Combine(_tempDir, "backups", result.FileName);
        using var zip = ZipFile.OpenRead(fullPath);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
        Assert.Contains("userdata.db", names);
        Assert.Contains("bl_cache.db", names);
        Assert.Contains("settings.json", names);
        Assert.Equal(3, names.Count);

        // Das gepackte settings.json ist exakt das Original.
        var settingsEntry = zip.GetEntry("settings.json")!;
        using var reader = new StreamReader(settingsEntry.Open());
        var content = await reader.ReadToEndAsync();
        Assert.Equal("{\"hello\":\"world\"}", content);
    }

    [Fact]
    public async Task ListBackups_on_empty_dir_returns_empty()
    {
        var list = await _sut.ListBackupsAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task ListBackups_returns_newest_first()
    {
        await _sut.CreateBackupAsync();
        // Mind. 1 Sekunde warten damit der Timestamp im Dateinamen unterschiedlich ist
        // (Format yyyy-MM-dd-HHmmss hat Sekunden-Aufloesung).
        await Task.Delay(1100);
        await _sut.CreateBackupAsync();
        await Task.Delay(1100);
        await _sut.CreateBackupAsync();

        var list = await _sut.ListBackupsAsync();
        Assert.Equal(3, list.Count);
        // Newest first
        Assert.True(list[0].CreatedAt >= list[1].CreatedAt);
        Assert.True(list[1].CreatedAt >= list[2].CreatedAt);
    }

    [Fact]
    public async Task CleanupOldBackups_removes_oldest_keeps_newest()
    {
        await _sut.CreateBackupAsync(); await Task.Delay(1100);
        await _sut.CreateBackupAsync(); await Task.Delay(1100);
        await _sut.CreateBackupAsync(); await Task.Delay(1100);
        await _sut.CreateBackupAsync(); await Task.Delay(1100);
        await _sut.CreateBackupAsync();

        var deleted = await _sut.CleanupOldBackupsAsync(keepCount: 2);
        Assert.Equal(3, deleted);

        var remaining = await _sut.ListBackupsAsync();
        Assert.Equal(2, remaining.Count);
    }

    [Fact]
    public async Task CleanupOldBackups_with_keepCount_zero_clamps_to_one()
    {
        await _sut.CreateBackupAsync(); await Task.Delay(1100);
        await _sut.CreateBackupAsync();

        var deleted = await _sut.CleanupOldBackupsAsync(keepCount: 0);
        // 0 wird auf 1 geclampt -> 1 Backup geloescht, 1 bleibt
        Assert.Equal(1, deleted);
        var remaining = await _sut.ListBackupsAsync();
        Assert.Single(remaining);
    }

    [Fact]
    public async Task DeleteBackup_removes_specific_file()
    {
        var b1 = await _sut.CreateBackupAsync(); await Task.Delay(1100);
        var b2 = await _sut.CreateBackupAsync();

        var ok = await _sut.DeleteBackupAsync(b1.FileName);
        Assert.True(ok);

        var remaining = await _sut.ListBackupsAsync();
        Assert.Single(remaining);
        Assert.Equal(b2.FileName, remaining[0].FileName);
    }

    [Fact]
    public async Task DeleteBackup_returns_false_for_unknown_file()
    {
        var ok = await _sut.DeleteBackupAsync("HBSort-Backup-9999-99-99-999999.zip");
        Assert.False(ok);
    }

    [Fact]
    public async Task RestoreBackup_overwrites_target_files()
    {
        // Original anlegen + Backup machen.
        var dbPath = Path.Combine(_tempDir, "userdata.db");
        await SeedSqliteDb(dbPath, "INSERT INTO test VALUES ('original')");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "settings.json"),
            "{\"version\":\"original\"}");

        var backup = await _sut.CreateBackupAsync();

        // Original modifizieren.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "settings.json"),
            "{\"version\":\"modified\"}");

        // Restore.
        var ok = await _sut.RestoreBackupAsync(backup.FileName);
        Assert.True(ok);

        // settings.json ist wieder Original.
        var settings = await File.ReadAllTextAsync(Path.Combine(_tempDir, "settings.json"));
        Assert.Equal("{\"version\":\"original\"}", settings);
    }

    [Fact]
    public async Task RestoreBackup_returns_false_for_missing_file()
    {
        var ok = await _sut.RestoreBackupAsync("HBSort-Backup-doesnotexist.zip");
        Assert.False(ok);
    }

    [Fact]
    public async Task RestoreBackup_creates_pre_restore_backup_first()
    {
        await SeedSqliteDb(Path.Combine(_tempDir, "userdata.db"));
        var first = await _sut.CreateBackupAsync();
        // Vor dem Restore: nur 1 Backup vorhanden.
        var before = await _sut.ListBackupsAsync();
        Assert.Single(before);

        await Task.Delay(1100);
        var ok = await _sut.RestoreBackupAsync(first.FileName);
        Assert.True(ok);

        // Nach dem Restore: 2 Backups (original + Pre-Restore).
        var after = await _sut.ListBackupsAsync();
        Assert.Equal(2, after.Count);
    }

    [Fact]
    public async Task Two_consecutive_CreateBackups_produce_different_files()
    {
        var first = await _sut.CreateBackupAsync();
        await Task.Delay(1100);
        var second = await _sut.CreateBackupAsync();
        Assert.NotEqual(first.FileName, second.FileName);
    }

    private static async Task SeedSqliteDb(string path, string? extraSql = null)
    {
        // Minimal-DB: eine Tabelle anlegen, optional Daten reinschieben.
        await using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS test (val TEXT);";
        cmd.ExecuteNonQuery();
        if (!string.IsNullOrEmpty(extraSql))
        {
            cmd.CommandText = extraSql;
            cmd.ExecuteNonQuery();
        }
    }
}
