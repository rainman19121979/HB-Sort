using System.Windows;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort.Views;

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

    /// <summary>
    /// UX X.25: RadioButton "in Lager legen" geklickt. WPF-RadioButton-IsChecked
    /// ist OneWay-gebunden weil ein TwoWay-Binding auf zwei computed Properties
    /// (IsPutInBinMode/IsAssignToWaitingMode) Endlos-Schleifen erzeugt.
    /// Wir setzen den Mode hier explizit.
    /// </summary>
    private void ModePutInBin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ViewModels.DismantlePartItemViewModel item)
        {
            item.Mode = ViewModels.DismantlePartMode.PutInBin;
        }
    }

    private void ModeAssignToWaiting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ViewModels.DismantlePartItemViewModel item)
        {
            item.Mode = ViewModels.DismantlePartMode.AssignToWaiting;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // Validierung pro Teil:
        //  - PutInBin-Mode + IsKept=true -> TargetBin muss gesetzt sein
        //  - AssignToWaiting-Mode + IsKept=true -> SelectedMatch muss gesetzt sein
        var missingBin = _viewModel.Parts.FirstOrDefault(p =>
            p.IsKept && p.IsPutInBinMode && p.TargetBin == null);
        if (missingBin != null)
        {
            _notifications.ShowWarning($"Teil '{missingBin.PartName}': bitte ein Ziel-Fach waehlen.");
            return;
        }
        var missingMatch = _viewModel.Parts.FirstOrDefault(p =>
            p.IsKept && p.IsAssignToWaitingMode && p.SelectedMatch == null);
        if (missingMatch != null)
        {
            _notifications.ShowWarning(
                $"Teil '{missingMatch.PartName}': bitte eine wartende Figur auswaehlen.");
            return;
        }

        try
        {
            Result = await _viewModel.ConfirmAsync();

            // UX X.25: Toast-Message zusammenstellen aus FloatingPart- und
            // Direkt-Zuordnung-Counts. CompletedMinifigNames werden separat
            // genannt (max. 3 Namen, dann "+N weitere") damit der Toast nicht
            // ausufert.
            var parts = new List<string>();
            if (Result.TotalPartsTransferred > 0)
                parts.Add($"{Result.TotalPartsTransferred} Einzelteil(e) in Pool");
            if (Result.AssignedToWaitingCount > 0)
                parts.Add($"{Result.AssignedToWaitingCount} Teil(e) wartenden Figuren zugeordnet");
            var head = parts.Count > 0
                ? $"Figur '{_viewModel.MinifigName}' zerlegt - {string.Join(", ", parts)}."
                : $"Figur '{_viewModel.MinifigName}' zerlegt (keine Teile uebernommen).";
            _notifications.ShowSuccess(head);

            // Pro komplett gewordene Figur ein eigener Toast (max. 3 - sonst Spam).
            foreach (var name in Result.CompletedMinifigNames.Take(3))
            {
                _notifications.ShowSuccess($"Figur '{name}' ist jetzt komplett!");
            }
            if (Result.CompletedMinifigNames.Count > 3)
            {
                _notifications.ShowSuccess(
                    $"... und {Result.CompletedMinifigNames.Count - 3} weitere Figuren komplett.");
            }

            DialogResult = true;
            Close();
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Dismantle-Confirm fehlgeschlagen");
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync("Fehler", ex.Message);
        }
    }
}
