using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// UX X.32 Block C (v0.1.19): Sammel-Popup-Overlay (mehrere Bin-Anweisungen
/// auf einmal). DataContext = MainViewModel (geerbt aus dem Parent
/// SortingView). Sichtbarkeit via Binding auf
/// MainViewModel.ScanViewModel.IsBinInstructionGroupVisible.
/// </summary>
public partial class BinInstructionGroupOverlay : UserControl
{
    public BinInstructionGroupOverlay()
    {
        InitializeComponent();
    }

    private void OverlayBackground_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Dismiss();
        e.Handled = true;
    }

    /// <summary>
    /// Klick auf den inneren Border schliesst auch (User-Spec: "Klick
    /// irgendwo schliesst"). Klick auf den OK-Button hat Vorrang dank
    /// Click-Handler + e.Handled.
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
            main.ScanViewModel?.DismissBinInstructionGroup();
        }
    }
}
