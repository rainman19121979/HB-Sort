using System.IO;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// UX X.29 (v0.1.16): Backup-Implementation.
///
/// Strategie:
/// 1. Online-Backup der beiden SQLite-DBs (userdata.db, bl_cache.db) ueber
///    <see cref="SqliteConnection.BackupDatabase"/> in temporaere Dateien.
///    Diese API laeuft sicher, auch wenn die Quell-DB gerade von anderen
///    Connections genutzt wird (sie sperrt nur kurz).
/// 2. settings.json wird als File-Copy uebernommen (kein DB-File).
/// 3. Alle drei Dateien werden in ein ZIP gepackt:
///    HBSort-Backup-yyyy-MM-dd-HHmmss.zip in %APPDATA%\HBSort\backups\.
/// 4. Temporaere Dateien werden geloescht.
///
/// Restore macht das Reverse: ZIP entpacken, Dateien zurueckschreiben.
/// VOR dem Restore wird ein neues Pre-Restore-Backup angelegt damit der
/// User nichts verliert wenn er versehentlich falsch wiederhergestellt hat.
/// </summary>
public class BackupService : IBackupService
{
    private readonly string _appDataFolder;
    private readonly string _backupFolder;

    private const string BackupFilePrefix = "HBSort-Backup-";
    private const string BackupFileExtension = ".zip";
    // Sekunden-Aufloesung im Dateinamen reicht fuer den User; Pre-Restore-
    // Backups die in derselben Sekunde wie das Original entstehen kriegen
    // einen "-2"/"-3"-Counter angehaengt (siehe MakeUniqueFileName).
    private const string TimestampFormat = "yyyy-MM-dd-HHmmss";

    // Dateien die ins Backup gepackt werden. Reihenfolge ist auch die
    // Reihenfolge im ZIP - macht das manuelle Inspizieren angenehmer.
    private static readonly string[] DbFiles = { "userdata.db", "bl_cache.db" };
    private static readonly string[] PlainFiles = { "settings.json" };

    public BackupService(string appDataFolder)
    {
        _appDataFolder = appDataFolder;
        _backupFolder = Path.Combine(appDataFolder, "backups");
    }

    public async Task<BackupResult> CreateBackupAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_backupFolder);

        var createdAt = DateTime.UtcNow;
        var (fileName, fullPath) = MakeUniqueFileName(createdAt);

        // Temp-Verzeichnis fuer die Online-Backup-Kopien der DBs.
        var tempDir = Path.Combine(Path.GetTempPath(),
            "HBSort-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var entries = new List<(string SourcePath, string EntryName)>();

            // 1. SQLite-DBs via Online-Backup-API kopieren.
            foreach (var dbName in DbFiles)
            {
                ct.ThrowIfCancellationRequested();
                var sourceDb = Path.Combine(_appDataFolder, dbName);
                if (!File.Exists(sourceDb))
                {
                    Log.Debug("Backup: {Db} existiert nicht, ueberspringen", sourceDb);
                    continue;
                }

                var tempDb = Path.Combine(tempDir, dbName);
                await BackupSqliteAsync(sourceDb, tempDb, ct);
                entries.Add((tempDb, dbName));
            }

            // 2. settings.json (und ggf. weitere Plain-Files) als Copy.
            foreach (var plainName in PlainFiles)
            {
                ct.ThrowIfCancellationRequested();
                var source = Path.Combine(_appDataFolder, plainName);
                if (!File.Exists(source))
                {
                    Log.Debug("Backup: {File} existiert nicht, ueberspringen", source);
                    continue;
                }
                entries.Add((source, plainName));
            }

            // 3. ZIP packen.
            using (var zip = ZipFile.Open(fullPath, ZipArchiveMode.Create))
            {
                foreach (var (path, entryName) in entries)
                {
                    zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
                }
            }

            var size = new FileInfo(fullPath).Length;
            Log.Information("Backup erstellt: {File} ({Size} Bytes, {Count} Dateien)",
                fileName, size, entries.Count);
            return new BackupResult(fileName, size, createdAt);
        }
        catch
        {
            // Bei Fehler: das halbfertige ZIP loeschen damit's nicht in der
            // Liste auftaucht.
            try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { /* egal */ }
            throw;
        }
        finally
        {
            // Temp-Dir aufraeumen.
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { Log.Debug(ex, "Backup: Temp-Cleanup fehlgeschlagen"); }
        }
    }

    public async Task<List<BackupInfo>> ListBackupsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_backupFolder))
            return new List<BackupInfo>();

        // Synchron - Verzeichnis-Iteration ist schnell. Async-Wrap nur fuer
        // Interface-Konsistenz.
        await Task.Yield();

        return Directory.GetFiles(_backupFolder, BackupFilePrefix + "*" + BackupFileExtension)
            .Select(path =>
            {
                var fi = new FileInfo(path);
                var ts = ParseTimestampFromFileName(fi.Name) ?? fi.CreationTimeUtc;
                return new BackupInfo(fi.Name, fi.Length, ts);
            })
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    public async Task<bool> RestoreBackupAsync(string backupFileName, CancellationToken ct = default)
    {
        var backupPath = Path.Combine(_backupFolder, backupFileName);
        if (!File.Exists(backupPath))
        {
            Log.Warning("Restore: Backup-Datei {File} nicht gefunden", backupFileName);
            return false;
        }

        // Vor dem Restore ein Pre-Restore-Backup anlegen, damit ein
        // versehentlicher Klick nichts unwiederbringbar zerstoert.
        try
        {
            await CreateBackupAsync(ct);
            Log.Information("Restore: Pre-Restore-Backup erfolgreich erzeugt");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Restore: Pre-Restore-Backup fehlgeschlagen - " +
                "Restore wird trotzdem fortgesetzt");
        }

        try
        {
            using var zip = ZipFile.OpenRead(backupPath);

            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var targetPath = Path.Combine(_appDataFolder, entry.FullName);

                // Falls die Ziel-Datei vorhanden + offen ist (z.B. WAL aktiv):
                // wir akzeptieren dass beim naechsten App-Start die WAL-
                // Restore-Logik von SQLite das wieder zusammenfuehrt.
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                // Vor dem Ueberschreiben: alte Datei via .restore-old-Suffix
                // umbenennen, damit Windows den File-Lock loslaesst (falls eine
                // App noch lebt). Best-effort.
                if (File.Exists(targetPath))
                {
                    try
                    {
                        var oldPath = targetPath + ".restore-old-" + Guid.NewGuid().ToString("N");
                        File.Move(targetPath, oldPath);
                        try { File.Delete(oldPath); } catch { /* spaeter aufraeumen */ }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Restore: Konnte {File} nicht weg-renamen, " +
                            "versuche direkt zu ueberschreiben", targetPath);
                    }
                }

                entry.ExtractToFile(targetPath, overwrite: true);
            }

            Log.Information("Restore aus '{File}' erfolgreich", backupFileName);
            return true;
        }
        catch (InvalidDataException ex)
        {
            Log.Error(ex, "Restore: Backup-ZIP {File} ist beschaedigt", backupFileName);
            return false;
        }
    }

    public async Task<bool> DeleteBackupAsync(string backupFileName, CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        var backupPath = Path.Combine(_backupFolder, backupFileName);
        if (!File.Exists(backupPath))
            return false;
        File.Delete(backupPath);
        Log.Information("Backup geloescht: {File}", backupFileName);
        return true;
    }

    public async Task<int> CleanupOldBackupsAsync(int keepCount, CancellationToken ct = default)
    {
        if (keepCount < 1) keepCount = 1; // mind. 1 immer behalten
        var backups = await ListBackupsAsync(ct);
        if (backups.Count <= keepCount) return 0;

        var toDelete = backups.Skip(keepCount).ToList();
        var deleted = 0;
        foreach (var b in toDelete)
        {
            try
            {
                File.Delete(Path.Combine(_backupFolder, b.FileName));
                deleted++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Cleanup: Konnte alten Backup {File} nicht loeschen", b.FileName);
            }
        }
        if (deleted > 0)
            Log.Information("Cleanup: {Count} alte Backups geloescht (behalten: {Keep})",
                deleted, keepCount);
        return deleted;
    }

    private static async Task BackupSqliteAsync(string sourceDb, string targetDb, CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        // Online-Backup-API: kopiert die DB Page fuer Page, kann auch laufen
        // wenn die Quell-DB von anderen Connections gerade benutzt wird.
        // Damit kein VACUUM noetig ist und keine WAL-Files verloren gehen.
        //
        // Pooling=False ist Pflicht: ohne das haelt Microsoft.Data.Sqlite das
        // File-Handle nach Dispose() im Connection-Pool offen, und das
        // anschliessende Lesen via ZipFile.CreateEntryFromFile wirft
        // System.IO.IOException "wird von einem anderen Prozess genutzt".
        var sourceCs = $"Data Source={sourceDb};Mode=ReadOnly;Pooling=False";
        var targetCs = $"Data Source={targetDb};Pooling=False";
        using (var source = new SqliteConnection(sourceCs))
        using (var target = new SqliteConnection(targetCs))
        {
            source.Open();
            target.Open();
            source.BackupDatabase(target);
        }

        // Defensiv: Connection-Pool fuer beide Dateien explizit leeren - falls
        // irgendwo doch noch ein Handle haengt (z.B. weil ein anderer Test im
        // gleichen Prozess die DB schon mal mit Pooling=true geoeffnet hat).
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Liefert einen eindeutigen Dateinamen + Pfad fuer ein Backup zum
    /// gegebenen Zeitstempel. Bei Konflikt (gleiche Sekunde) wird "-2",
    /// "-3", ... angehaengt. Verhindert dass zwei in derselben Sekunde
    /// erzeugte Backups einander ueberschreiben (passiert z.B. wenn das
    /// User-Backup direkt von einem Pre-Restore-Backup gefolgt wird).
    /// </summary>
    private (string FileName, string FullPath) MakeUniqueFileName(DateTime createdAt)
    {
        var basis = BackupFilePrefix + createdAt.ToString(TimestampFormat);
        var fileName = basis + BackupFileExtension;
        var fullPath = Path.Combine(_backupFolder, fileName);

        var counter = 2;
        while (File.Exists(fullPath))
        {
            fileName = basis + "-" + counter + BackupFileExtension;
            fullPath = Path.Combine(_backupFolder, fileName);
            counter++;
            if (counter > 999) // pragmatischer Schutz gegen Endlos-Schleife
                throw new InvalidOperationException(
                    "Konnte keinen eindeutigen Backup-Dateinamen finden.");
        }
        return (fileName, fullPath);
    }

    private static DateTime? ParseTimestampFromFileName(string fileName)
    {
        // HBSort-Backup-2026-05-08-153022.zip oder HBSort-Backup-2026-05-08-153022-2.zip
        if (!fileName.StartsWith(BackupFilePrefix, StringComparison.Ordinal))
            return null;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ts = stem.Substring(BackupFilePrefix.Length);
        // Counter-Suffix abschneiden, falls vorhanden (-2/-3/...).
        var dashIdx = ts.LastIndexOf('-');
        if (dashIdx > 0 && dashIdx < ts.Length - 1
            && int.TryParse(ts.Substring(dashIdx + 1), out _)
            && ts.Length - dashIdx <= 4)
        {
            // Letzter Teil ist eine kleine Zahl (1-3 Stellen) -> Counter-Suffix.
            // Aber nur wenn der davor liegende Teil ein gueltiger Timestamp ist.
            var maybeTs = ts.Substring(0, dashIdx);
            if (DateTime.TryParseExact(maybeTs, TimestampFormat,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsedTs))
            {
                return parsedTs;
            }
        }
        return DateTime.TryParseExact(ts, TimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed) ? parsed : null;
    }
}
