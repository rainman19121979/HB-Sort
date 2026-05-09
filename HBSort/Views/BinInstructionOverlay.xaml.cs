using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// UX X.31 Block B (v0.1.18): Anweisungs-Overlay nach erfolgreichem Persist.
/// DataContext = MainViewModel (geerbt aus dem Parent SortingView).
/// Sichtbar via DataTrigger auf MainViewModel.ScanViewModel.IsBinInstructionVisible.
/// </summary>
public partial class BinInstructionOverlay : UserControl
{
    public BinInstructionOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Hintergrund-Klick (ausserhalb des inneren Borders) schliesst.</summary>
    private void OverlayBackground_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Dismiss();
        e.Handled = true;
    }

    /// <summary>
    /// Klick auf den inneren Panel-Border schliesst ebenfalls - Spec sagt
    /// "Klick irgendwo schliesst", also auch innerhalb der Karte. Der Button
    /// behaelt eigene Click-Handler-Prioritaet (e.Handled=true beim Button).
    /// </summary>
    private void OverlayPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        Dismiss();
        e.Handled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
    }

    private void Dismiss()
    {
        if (DataContext is MainViewModel main)
        {
            main.ScanViewModel?.DismissBinInstruction();
        }
    }
}
