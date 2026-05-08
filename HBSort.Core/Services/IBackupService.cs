namespace HBSort.Core.Services;

/// <summary>
/// UX X.29 (v0.1.16): Backup-System fuer userdata.db + bl_cache.db +
/// settings.json. Backups landen als ZIP in %APPDATA%\HBSort\backups\.
/// Nutzt SQLite-Online-Backup-API (.BackupDatabase) - sicher auch wenn
/// die DBs gerade geoeffnet sind, kein VACUUM-Risiko bei grossen DBs.
/// </summary>
public interface IBackupService
{
    /// <summary>Erzeugt ein neues Backup. Liefert Datei-Info bei Erfolg.</summary>
    Task<BackupResult> CreateBackupAsync(CancellationToken ct = default);

    /// <summary>Liste der vorhandenen Backups, neueste zuerst.</summary>
    Task<List<BackupInfo>> ListBackupsAsync(CancellationToken ct = default);

    /// <summary>
    /// Stellt das angegebene Backup wieder her. Erzeugt vor dem Restore ein
    /// neues "Pre-Restore"-Backup vom aktuellen Stand. Liefert false wenn
    /// die Backup-Datei nicht existiert oder das ZIP korrupt ist.
    /// </summary>
    Task<bool> RestoreBackupAsync(string backupFileName, CancellationToken ct = default);

    /// <summary>Loescht ein einzelnes Backup. Liefert false wenn nicht gefunden.</summary>
    Task<bool> DeleteBackupAsync(string backupFileName, CancellationToken ct = default);

    /// <summary>
    /// Loescht alte Backups, behaelt nur die letzten <paramref name="keepCount"/>.
    /// Liefert die Anzahl geloeschter Backups.
    /// </summary>
    Task<int> CleanupOldBackupsAsync(int keepCount, CancellationToken ct = default);
}

public record BackupResult(string FileName, long SizeBytes, DateTime CreatedAt);

public record BackupInfo(string FileName, long SizeBytes, DateTime CreatedAt);
