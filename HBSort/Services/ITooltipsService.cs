namespace HBSort.Services;

/// <summary>
/// Steuert das globale An/Aus der UI-Tooltips (UX-Iteration X.9).
///
/// Implementiert eine schmale Faade ueber die Application-Resource
/// "TooltipsEnabled". Die Wurzel-Windows binden ihre
/// ToolTipService.IsEnabled-Property als DynamicResource an diesen
/// Schluessel - aenderungen wirken dadurch sofort ohne Neustart und
/// ohne dass einzelne UI-Stellen den Service kennen muessen.
/// </summary>
public interface ITooltipsService
{
    /// <summary>Aktueller Zustand (gespiegelt aus AppSettings.ShowTooltips).</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Schreibt den aktuellen Settings-Zustand in die Application-Resource.
    /// Wird beim App-Start (nach Settings-Load) aufgerufen, damit die
    /// Resource direkt den richtigen Wert hat, bevor das erste Fenster
    /// gezeigt wird.
    /// </summary>
    Task ApplyAsync();

    /// <summary>
    /// Live-Toggle vom SettingsWindow aufgerufen. Schreibt den neuen Wert
    /// in die Settings-Instanz UND in die Application-Resource. Die
    /// Persistierung in settings.json macht das SettingsViewModel beim
    /// "Speichern".
    /// </summary>
    void SetEnabled(bool enabled);
}
