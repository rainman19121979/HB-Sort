using System.Windows;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// v0.1.24-beta.11: Dialog zum Verwalten der persistent ignorierten
/// Bauvorschlaege. VM-getrieben - HasChanges wird vom Caller geprueft
/// um nach Schliessen einen Refresh auszuloesen.
/// </summary>
public partial class ManageIgnoredDialog : Window
{
    public ManageIgnoredViewModel ViewModel { get; }

    public ManageIgnoredDialog(ManageIgnoredViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // v0.1.25 (B10): Schliessen ist kein Erfolg -> false (stil-konsistent
        // zu MassUpdateExportDialog + BinDetailDialog). Der Caller wertet
        // ohnehin dialogVm.HasChanges aus, nicht den DialogResult.
        DialogResult = false;
        Close();
    }
}
