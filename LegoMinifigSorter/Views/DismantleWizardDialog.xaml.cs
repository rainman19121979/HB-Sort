using System.Windows;
using LegoMinifigSorter.Services;
using LegoMinifigSorter.ViewModels;
using Serilog;

namespace LegoMinifigSorter.Views;

/// <summary>
/// Wizard fuer das Aufgeben einer wartenden Figur. Pro Teil entscheidet der
/// User: uebernehmen (Floating mit Origin-Markierung) oder verwerfen.
/// </summary>
public partial class DismantleWizardDialog : Window
{
    private readonly DismantleWizardViewModel _viewModel;
    private readonly INotificationService _notifications;

    /// <summary>Liefert den Dismantle-Result wenn DialogResult=true.</summary>
    public Core.Services.DismantleResult? Result { get; private set; }

    public DismantleWizardDialog(DismantleWizardViewModel viewModel, INotificationService notifications)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _notifications = notifications;
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => _viewModel.SelectAll();
    private void DeselectAll_Click(object sender, RoutedEventArgs e) => _viewModel.DeselectAll();
    private void ApplyDefault_Click(object sender, RoutedEventArgs e) => _viewModel.ApplyDefaultBin();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // Validierung: alle als-uebernommen markierten Teile brauchen ein Ziel-Fach.
        var missingBin = _viewModel.Parts.FirstOrDefault(p => p.IsKept && p.TargetBin == null);
        if (missingBin != null)
        {
            _notifications.ShowWarning($"Teil '{missingBin.PartName}': bitte ein Ziel-Fach waehlen.");
            return;
        }

        try
        {
            Result = await _viewModel.ConfirmAsync();
            var msg = Result.TotalPartsTransferred > 0
                ? $"Figur '{_viewModel.MinifigName}' zerlegt – {Result.TotalPartsTransferred} Einzelteil(e) in Pool uebernommen."
                : $"Figur '{_viewModel.MinifigName}' zerlegt (keine Teile uebernommen).";
            _notifications.ShowSuccess(msg);
            DialogResult = true;
            Close();
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Dismantle-Confirm fehlgeschlagen");
            MessageBox.Show(ex.Message, "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
