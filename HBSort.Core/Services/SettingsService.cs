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

    // JSON-Optionen: eingerueckt fuer Lesbarkeit, case-insensitive fuer Robustheit
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
                    NormalizePriceFilterEitherOr();
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
            // Bei Fehler: Defaults verwenden und weitermachen (App soll nicht abstuerzen)
            Log.Warning(ex, "Fehler beim Laden der Einstellungen, verwende Standardwerte");
            Current = new AppSettings();
        }
    }

    /// <summary>
    /// UX X.34 v0.1.20: Either-Or-Migration fuer Region/CountryCode in den
    /// Preis-Settings. Bestand-User aus v0.1.19 oder frueher haben oft beide
    /// gleichzeitig gesetzt (Defaults waren "europe" + "DE"). Der neue Provider
    /// behandelt das als Either-Or - CountryCode wins, Region wird beim
    /// API-Call ignoriert. Hier normalisieren wir die Settings persistent
    /// auf den effektiven Filter, damit das Settings-UI nicht weiter beide
    /// Werte zeigt. Save passiert beim naechsten regulaeren Settings-Save -
    /// wir loesen ihn nicht selbst aus, das ist Sache des Aufrufers.
    /// </summary>
    private void NormalizePriceFilterEitherOr()
    {
        var p = Current.Prices;
        var hasCountry = !string.IsNullOrWhiteSpace(p.CountryCode);
        var hasRegion  = !string.IsNullOrWhiteSpace(p.Region);
        if (hasCountry && hasRegion)
        {
            Log.Information(
                "Preis-Filter-Migration: Region '{OldRegion}' geloescht weil CountryCode '{Country}' gesetzt ist (Either-Or, CountryCode wins)",
                p.Region, p.CountryCode);
            p.Region = string.Empty;
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
