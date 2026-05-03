using System.Windows;
using System.Windows.Controls;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// Phase-3-Detailansicht der erkannten Minifigur.
/// Wird in das Hauptfenster eingebettet (DataContext = PendingMinifigViewModel).
/// "Verwerfen" und "Abbrechen" blenden die Detail-View aus indem sie
/// das ScanViewModel.PendingMinifig auf null setzen.
/// </summary>
public partial class MinifigDetailView : UserControl
{
    public MinifigDetailView()
    {
        InitializeComponent();
    }

    /// <summary>Holt das ScanViewModel ueber die Window-Hierarchie.</summary>
    private ScanViewModel? GetScanViewModel()
    {
        var window = Window.GetWindow(this);
        if (window?.DataContext is MainViewModel main)
        {
            return main.ScanViewModel;
        }
        return null;
    }

    /// <summary>Verwerfen: Detail-View ausblenden, keine Speicherung.</summary>
    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetScanViewModel();
        if (vm != null)
        {
            vm.PendingMinifig = null;
            vm.MinifigStatusText = string.Empty;
        }
    }

    /// <summary>"In Fach legen": triggert PersistPending im ScanViewModel.</summary>
    private async void Persist_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetScanViewModel();
        if (vm == null) return;
        await vm.PersistPendingAsync();
    }

    /// <summary>
    /// UX-Iteration X.4+: "Aus Fach uebernehmen" pro Teilezeile. Tag der
    /// Button-Sender ist das PendingPartViewModel. Der Service-Aufruf laeuft
    /// im ScanViewModel; der Refresh der UI passiert dort auch.
    /// </summary>
    private async void TakeFromBin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not PendingPartViewModel part) return;
        var vm = GetScanViewModel();
        if (vm == null) return;
        await vm.TransferFloatingPartToPendingAsync(part);
    }
}
