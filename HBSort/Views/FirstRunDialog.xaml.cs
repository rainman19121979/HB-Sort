using System.Windows;
using HBSort.Core.Services;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// UX X.34 v0.1.20-beta.2: Code-Behind fuer den First-Run-Dialog.
///
/// "Lagerfach-Verwaltung oeffnen" delegiert an MainViewModel.OpenSettings()
/// (analog zu BuildSuggestionDetailDialog) - der User landet im Settings-
/// Dialog, klickt auf den Lagerfaecher-Tab, legt Bins an, schliesst den
/// Settings-Dialog. Beim Schliessen refresht das ViewModel den Bin-Status.
/// </summary>
public partial class FirstRunDialog : Window
{
    private readonly FirstRunDialogViewModel _vm;
    private readonly ISettingsService _settings;

    public FirstRunDialog(FirstRunDialogViewModel vm, ISettingsService settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = _vm;
    }

    private void OpenBinSettings_Click(object sender, RoutedEventArgs e)
    {
        // MainViewModel.OpenSettings() loest den Settings-Dialog ueber das
        // MainWindow aus. Wir uebergeben unsere Owner-Reference damit der
        // Settings-Dialog ueber dem First-Run-Dialog erscheint (modal-on-modal).
        if (Owner?.DataContext is MainViewModel main)
        {
            main.OpenSettings();
        }
        // Nach dem Settings-Schliessen: Bin-Status neu pruefen damit das
        // gruene Haken-Icon erscheint. WPF blockiert hier bis Settings zu ist
        // (modal-Hierarchie via Owner).
        _ = _vm.RefreshStatusAsync();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        // "Spaeter erinnern": Setting bleibt true, Dialog erscheint beim
        // naechsten Start wieder solange Setup nicht komplett ist.
        DialogResult = false;
        Close();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        // "Loslegen": Catalog ist da (sonst waere der Button disabled).
        // Setting auf false setzen damit der Dialog auch dann nicht mehr
        // erscheint wenn die User-Daten spaeter mal weg sind (z.B. nach
        // Backup-Restore). Sobald Setup wirklich Complete ist, greift
        // der Status-Check ohnehin und der Dialog kommt nicht mehr.
        _settings.Current.ShowFirstRunDialog = false;
        await _settings.SaveAsync();

        DialogResult = true;
        Close();
    }
}
