using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// v0.1.22-beta.1 Block F: konsolidiertes Anweisungs-Overlay. Vorher zwei
/// UserControls (Single + Group) mit identischer Klick-Handler-Logik.
/// DataContext = MainViewModel; Sichtbarkeit + Mode-Switch ueber
/// MainViewModel.ScanViewModel.BinInstruction.IsVisible / .Mode.
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
    /// "Klick irgendwo schliesst", also auch innerhalb der Karte. Der OK-
    /// Button behaelt eigene Click-Handler-Prioritaet (e.Handled=true).
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
