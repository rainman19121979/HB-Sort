using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// Verwaltet das Laden und Speichern der App-Einstellungen.
/// Die Settings werden als JSON in %APPDATA%\HBSort\settings.json gespeichert.
/// </summary>
public interface ISettingsService
{
    /// <summary>Die aktuell geladenen Einstellungen</summary>
    AppSettings Current { get; }

    /// <summary>Laedt die Einstellungen aus der Datei (oder erstellt Defaults)</summary>
    Task LoadAsync();

    /// <summary>Speichert die aktuellen Einstellungen in die Datei</summary>
    Task SaveAsync();
}
