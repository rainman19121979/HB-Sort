using System.Windows;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// v0.1.24-beta.12 Phase 4: Dialog-Window fuer den Mass-Update-Export.
/// Reines Code-Behind - alle Logik im VM.
/// </summary>
public partial class MassUpdateExportDialog : Window
{
    public MassUpdateExportViewModel ViewModel { get; }

    public MassUpdateExportDialog(MassUpdateExportViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // v0.1.25 (B10): Schliessen ist kein Erfolg -> false (gleicher Stil
        // wie BinDetailDialog). Der Caller (BlInventoryView) wertet den
        // Rueckgabewert ohnehin nicht aus.
        DialogResult = false;
        Close();
    }
}
