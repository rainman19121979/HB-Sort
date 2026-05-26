using System.Windows;
using System.Windows.Controls;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// v0.1.24-beta.7 Phase 2: Code-Behind fuer den BL-Inventar-Tab.
/// Logik lebt in BlInventoryViewModel; hier nur die zwei Image-Events
/// fuer Lazy-Thumbnail-Loading (Code-Behind statt Behavior-Library um
/// keine neue Abhaengigkeit einzufuehren).
/// </summary>
public partial class BlInventoryView : UserControl
{
    public BlInventoryView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Lazy-Image-Trigger: feuert wenn ein Image im DataGrid einen neuen
    /// DataContext bekommt - das passiert beim Row-Recycling sobald die
    /// Row in den sichtbaren Bereich scrollt. Wir bitten die VM, das
    /// Thumbnail fuer diese Row zu laden (idempotent, max 3 parallel).
    /// </summary>
    private void Thumbnail_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is BlInventoryRow row && DataContext is BlInventoryViewModel vm)
        {
            _ = vm.EnsureThumbnailAsync(row);
        }
    }

    /// <summary>
    /// Zusatz-Trigger fuer den initialen Render-Pass: bei der allerersten
    /// Erzeugung eines Container-Elements feuert DataContextChanged
    /// manchmal vor dem Loaded, mit identischem DataContext-Wert.
    /// Loaded ist defensiv - der TryClaimImageLoadSlot-Mechanismus in
    /// der VM verhindert Doppel-Loads.
    /// </summary>
    private void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe
            && fe.DataContext is BlInventoryRow row
            && DataContext is BlInventoryViewModel vm)
        {
            _ = vm.EnsureThumbnailAsync(row);
        }
    }
}
