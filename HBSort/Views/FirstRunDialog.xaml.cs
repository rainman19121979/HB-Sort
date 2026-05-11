using System.Windows;
using HBSort.Core.Services;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// UX X.34 v0.1.20-beta.2: Code-Behind fuer den First-Run-Dialog.
///
/// UX X.34 v0.1.20-beta.4: Lagerfaecher-Schritt ist nur noch Info-Text -
/// kein Action-Button mehr. User schliesst Welcome via "Loslegen" und
/// legt Bins entspannt in den Einstellungen an. Spart Modal-on-Modal-
/// Komplexitaet und macht den Welcome-Flow klarer.
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
