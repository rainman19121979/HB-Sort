using System.Windows.Controls;

namespace HBSort.Views;

/// <summary>
/// UX#12: View fuer die obere rechte Box im Sortier-Tab. DataContext =
/// MinifigPriceViewModel (vom ScanViewModel beim Pending-Aufbau erzeugt).
/// Code-Behind absichtlich leer - alles laeuft ueber Bindings + RelayCommand.
/// </summary>
public partial class MinifigPriceView : UserControl
{
    public MinifigPriceView()
    {
        InitializeComponent();
    }
}
