using System.Windows;
using HBSort.Core.Services;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// Bulk-Create-Dialog fuer Lagerfaecher. Live-Vorschau via ViewModel,
/// Conflict-Handling und Bestaetigung bei grossen Mengen.
/// Bei Erfolg: DialogResult=true und GeneratedLabels enthaelt die neuen Labels.
/// </summary>
public partial class BinBulkCreateDialog : Window
{
    private readonly BinBulkCreateViewModel _viewModel;

    /// <summary>Neu angelegte Labels (nur gesetzt wenn DialogResult=true).</summary>
    public List<string>? GeneratedLabels => _viewModel.GeneratedLabels;

    public BinBulkCreateDialog(BinBulkCreateViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    // RadioButton-Klicks setzen den Typ im ViewModel.
    // Wir koennten zwar Two-Way-Binding nutzen, aber RadioButtons mit Enum-Binding
    // sind in WPF umstaendlich - Click-Handler ist hier sauberer.
    private void TypeNumbers_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SequenceType = BinSequenceType.Numbers;
    }

    private void TypeLetters_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SequenceType = BinSequenceType.Letters;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.CreateAsync();
        // GeneratedLabels wird vom VM gesetzt sobald die Anlage durch ist
        // (auch bei 0 erstellten Faechern - dann ist die Liste leer aber nicht null).
        if (_viewModel.GeneratedLabels != null)
        {
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
