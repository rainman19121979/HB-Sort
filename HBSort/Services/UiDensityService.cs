using System.Windows;
using HBSort.Core.Services;
using Serilog;

namespace HBSort.Services;

/// <summary>
/// Default-Implementierung des UiDensityService.
///
/// Lade-Strategie: pro Density wird die zugehoerige ResourceDictionary-Datei
/// aus dem Application-Pack-URI geladen und in
/// Application.Current.Resources.MergedDictionaries als "Density-Slot"
/// eingefuegt. Bestehender Density-Slot wird ersetzt; das Anwenden ist
/// atomar und triggert ein DynamicResource-Re-Lookup.
///
/// Identifikation des Slots: ueber das Vorhandensein der Density-Schluessel
/// (HeaderFontSize + CardPadding) – ResourceDictionary hat kein Tag-Property,
/// wir koennen also keinen Marker drauf setzen.
/// </summary>
public class UiDensityService : IUiDensityService
{
    private readonly ISettingsService _settings;

    public UiDensity Current { get; private set; } = UiDensity.Normal;

    public UiDensityService(ISettingsService settings)
    {
        _settings = settings;
    }

    public async Task ApplyAsync(UiDensity density)
    {
        // Pack-URI auf das passende ResourceDictionary
        var fileName = density switch
        {
            UiDensity.Compact     => "DensityCompact.xaml",
            UiDensity.Comfortable => "DensityComfortable.xaml",
            _                      => "DensityNormal.xaml"
        };
        var uri = new Uri($"pack://application:,,,/Resources/{fileName}", UriKind.Absolute);

        try
        {
            var dict = new ResourceDictionary { Source = uri };

            var app = Application.Current;
            if (app == null)
            {
                Log.Warning("UiDensity: Application.Current ist null – Apply uebersprungen.");
                Current = density;
                return;
            }

            // Vorhandenen Density-Slot finden + ersetzen
            var merged = app.Resources.MergedDictionaries;
            int existingIndex = -1;
            for (int i = 0; i < merged.Count; i++)
            {
                if (merged[i] is ResourceDictionary rd && IsDensityDict(rd))
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                merged[existingIndex] = dict;
            }
            else
            {
                merged.Add(dict);
            }

            Current = density;
            _settings.Current.UiDensity = density.ToString();
            await _settings.SaveAsync();

            Log.Information("UiDensity gewechselt auf {Density}", density);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UiDensity Apply fuer {Density} fehlgeschlagen", density);
        }
    }

    /// <summary>
    /// Erkennt unser Density-Dictionary anhand des Tag-Markers ODER anhand
    /// einer charakteristischen Property (HeaderFontSize). Der Tag-Vergleich
    /// klappt nicht zuverlaessig nach dynamic-Cast – Property-Pruefung
    /// ist robuster.
    /// </summary>
    private static bool IsDensityDict(ResourceDictionary rd)
    {
        return rd.Contains("HeaderFontSize") && rd.Contains("CardPadding");
    }
}
