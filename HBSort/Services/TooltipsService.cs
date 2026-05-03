using System.Windows;
using HBSort.Core.Services;
using Serilog;

namespace HBSort.Services;

/// <summary>
/// Default-Implementierung des TooltipsService.
///
/// Schreibt einen bool-Wert in Application.Current.Resources unter dem
/// Schluessel <see cref="ResourceKey"/>. In MainWindow.xaml und allen
/// Dialog-Windows ist auf der Wurzel-Element-Ebene
/// ToolTipService.IsEnabled="{DynamicResource TooltipsEnabled}" gesetzt -
/// WPF-DynamicResource picked die Aenderung automatisch ueber den
/// gesamten Visual-Tree (ToolTipService.IsEnabled ist als Inheritable
/// registriert).
/// </summary>
public class TooltipsService : ITooltipsService
{
    /// <summary>Resource-Key der bool-Wert traegt. Auch in App.xaml referenziert.</summary>
    public const string ResourceKey = "TooltipsEnabled";

    private readonly ISettingsService _settings;

    public TooltipsService(ISettingsService settings)
    {
        _settings = settings;
    }

    public bool IsEnabled => _settings.Current.ShowTooltips;

    public Task ApplyAsync()
    {
        ApplyToResources(_settings.Current.ShowTooltips);
        return Task.CompletedTask;
    }

    public void SetEnabled(bool enabled)
    {
        _settings.Current.ShowTooltips = enabled;
        ApplyToResources(enabled);
        Log.Information("Tooltips global {State}", enabled ? "aktiviert" : "deaktiviert");
    }

    private static void ApplyToResources(bool enabled)
    {
        if (Application.Current == null)
        {
            // Kommt nur in Tests oder Build-Zeit-Auswertungen vor.
            Log.Debug("TooltipsService: Application.Current null - Apply uebersprungen");
            return;
        }

        Application.Current.Resources[ResourceKey] = enabled;
    }
}
