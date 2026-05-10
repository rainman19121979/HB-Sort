using System.Windows;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// UX X.32 v0.1.19-beta.4 Block D: Auswahl-Dialog vor "Diese Figur anlegen".
/// Zeigt Required-Parts mit Vor-Markierung und laesst den User waehlen
/// welche Teile beim Anlegen aus dem Floating-Pool gezogen werden sollen.
///
/// DialogResult=true wenn "Figur anlegen" geklickt wurde - der Caller
/// liest dann <see cref="CollectMinifigSelectionViewModel.SelectedPartsForReverseMatch"/>.
/// </summary>
public partial class CollectMinifigSelectionDialog : Window
{
    private readonly CollectMinifigSelectionViewModel _viewModel;

    public CollectMinifigSelectionDialog(CollectMinifigSelectionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
