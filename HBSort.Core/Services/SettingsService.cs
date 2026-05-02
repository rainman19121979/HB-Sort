using System.Text.Json;
using HBSort.Core.Models;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Implementierung des Settings-Service.
/// Liest und schreibt settings.json im AppData-Ordner.
/// Falls die Datei nicht existiert, werden Default-Werte verwendet.
/// </summary>
public class SettingsService : ISettingsService
{
    // Pfad zum App-Daten-Ordner: %APPDATA%\HBSort\
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HBSort");

    // Voller Pfad zur settings.json
    private static readonly string SettingsFilePath = Path.Combine(AppDataFolder, "settings.json");

    // JSON-Optionen: eingerückt für Lesbarkeit, case-insensitive für Robustheit
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Current { get; private set; } = new();

    public async Task LoadAsync()
    {
        try
        {
            // Falls die Datei existiert, lesen wir sie ein
            if (File.Exists(SettingsFilePath))
            {
                var json = await File.ReadAllTextAsync(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

                if (loaded != null)
                {
                    Current = loaded;
                    Log.Information("Einstellungen geladen aus {Path}", SettingsFilePath);
                    return;
                }
            }

            // Datei existiert nicht oder war leer → Defaults verwenden
            Log.Information("Keine settings.json gefunden, verwende Standardwerte");
            Current = new AppSettings();
        }
        catch (Exception ex)
        {
            // Bei Fehler: Defaults verwenden und weitermachen (App soll nicht abstürzen)
            Log.Warning(ex, "Fehler beim Laden der Einstellungen, verwende Standardwerte");
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            // Ordner anlegen falls er noch nicht existiert
            Directory.CreateDirectory(AppDataFolder);

            var json = JsonSerializer.Serialize(Current, JsonOptions);
            await File.WriteAllTextAsync(SettingsFilePath, json);

            Log.Debug("Einstellungen gespeichert nach {Path}", SettingsFilePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Speichern der Einstellungen");
        }
    }
}
