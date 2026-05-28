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
        DialogResult = true;
        Close();
    }
}
