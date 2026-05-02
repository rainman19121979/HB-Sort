using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel fuer das Splash-Fenster waehrend des Catalog-Imports.
/// Zeigt Fortschritt und Status-Text. Bei Fehler kann der User "Erneut versuchen".
/// </summary>
public partial class SplashViewModel : ObservableObject, IProgress<CatalogImportProgress>
{
    private readonly ICatalogImporter _catalogImporter;

    /// <summary>Sichtbarer Status-Text (wird der Progress-Bar angezeigt).</summary>
    [ObservableProperty]
    private string _statusText = "Wird gestartet...";

    /// <summary>Aktueller Fortschritt in Prozent (0-100).</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>Untertext mit Schritt-Anzeige z.B. "5 / 15".</summary>
    [ObservableProperty]
    private string _stepText = string.Empty;

    /// <summary>True, wenn der Import erfolgreich war.</summary>
    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>True, wenn der Import fehlgeschlagen ist (zeigt "Erneut versuchen").</summary>
    [ObservableProperty]
    private bool _hasError;

    /// <summary>Fehlertext (sichtbar wenn HasError = true).</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public SplashViewModel(ICatalogImporter catalogImporter)
    {
        _catalogImporter = catalogImporter;
    }

    /// <summary>
    /// Implementierung von IProgress: wird vom Importer aufgerufen.
    /// Der Importer ruft das auf einem Worker-Thread auf, deswegen muessen wir
    /// die UI-Properties via Dispatcher updaten – das macht ObservableObject
    /// glaube ich von selbst nicht, aber WPF-Bindings dispatchen
    /// PropertyChanged-Events automatisch in den UI-Thread, solange die
    /// PropertyChanged-Notification von einem Hintergrundthread aus gefeuert wird.
    /// Sicherheitshalber dispatchen wir trotzdem.
    /// </summary>
    public void Report(CatalogImportProgress value)
    {
        // Update-Aufruf in den UI-Thread dispatchen
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => UpdateFromProgress(value));
        }
        else
        {
            UpdateFromProgress(value);
        }
    }

    private void UpdateFromProgress(CatalogImportProgress p)
    {
        StatusText = p.Message;
        StepText = $"Schritt {p.CurrentStep} von {p.TotalSteps}";
        if (p.TotalSteps > 0)
        {
            ProgressPercent = Math.Min(100, (double)p.CurrentStep / p.TotalSteps * 100.0);
        }
    }

    /// <summary>
    /// Fuehrt den Import aus Embedded Resources durch.
    /// Wird vom SplashWindow.xaml.cs nach dem Loaded-Event aufgerufen.
    /// </summary>
    public async Task<bool> RunImportAsync(string targetDbPath, CancellationToken cancellationToken)
    {
        HasError = false;
        ErrorMessage = string.Empty;

        try
        {
            Log.Information("Starte Catalog-Import (Splash)");
            await _catalogImporter.ImportFromEmbeddedAsync(targetDbPath, this, cancellationToken);
            IsCompleted = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Information("Catalog-Import abgebrochen");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Catalog-Import fehlgeschlagen");
            HasError = true;
            ErrorMessage = ex.Message;

            // Falls schon eine kaputte DB liegt, wegraeumen damit der naechste Versuch frisch beginnt
            try
            {
                if (File.Exists(targetDbPath)) File.Delete(targetDbPath);
            }
            catch { /* best effort */ }

            return false;
        }
    }
}
