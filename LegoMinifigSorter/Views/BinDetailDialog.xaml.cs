using System.Windows;
using LegoMinifigSorter.ViewModels;

namespace LegoMinifigSorter.Views;

/// <summary>
/// Dialog mit den Inhalten eines Lagerfaches: Figuren + Floating-Parts
/// (mit Bild + Color-Swatch). Wird von Settings -> Tab Lagerfaecher
/// per Klick auf den "Inhalt"-Button geoeffnet.
/// </summary>
public partial class BinDetailDialog : Window
{
    private readonly BinDetailViewModel _viewModel;

    public BinDetailDialog(BinDetailViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
