using System.IO;
using System.Windows;
using LegoMinifigSorter.Core.Services;
using LegoMinifigSorter.ViewModels;
using Serilog;

namespace LegoMinifigSorter.Views;

/// <summary>
/// Code-Behind fuer das Splash-Fenster.
/// Startet den Import nach dem Loaded-Event und schliesst sich nach Erfolg
/// (DialogResult = true). Bei Fehler bleibt es offen mit Retry-Button.
/// </summary>
public partial class SplashWindow : Window
{
    private readonly SplashViewModel _viewModel;
    private readonly string _targetDbPath;
    private CancellationTokenSource _cts = new();

    public SplashWindow(SplashViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        // Pfad zur catalog.db
        _targetDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LegoMinifigSorter",
            "catalog.db");

        Loaded += SplashWindow_Loaded;
    }

    private async void SplashWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await StartImportAsync();
    }

    /// <summary>Startet den Import. Wird auch vom Retry-Button erneut aufgerufen.</summary>
    private async Task StartImportAsync()
    {
        // Falls vorher abgebrochen wurde: neue CTS
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        var success = await _viewModel.RunImportAsync(_targetDbPath, _cts.Token);

        if (success)
        {
            // Kurz die "Fertig"-Anzeige zeigen, dann schliessen
            await Task.Delay(500);
            DialogResult = true;
            Close();
        }
        // Bei Fehler bleibt das Fenster offen, ViewModel.HasError=true
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("User hat 'Erneut versuchen' geklickt");
        await StartImportAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("User hat den Catalog-Import abgebrochen");
        _cts.Cancel();
        DialogResult = false;
        Close();
    }
}
