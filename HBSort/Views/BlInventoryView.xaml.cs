using System.Windows;
using System.Windows.Controls;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

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
    /// v0.1.24-beta.12: oeffnet den Mass-Update-Export-Dialog. VM erst
    /// generieren, dann Dialog zeigen. Bei keinen Reservierungen erscheint
    /// der Dialog mit Empty-State-Hinweis.
    /// </summary>
    private async void OpenMassUpdate_Click(object sender, RoutedEventArgs e)
    {
        var sp = App.Services;
        var inv = sp.GetRequiredService<IBlInventoryService>();
        var notify = sp.GetRequiredService<INotificationService>();

        var vm = new MassUpdateExportViewModel(inv, notify);
        try
        {
            await vm.GenerateAsync();
        }
        catch (System.Exception ex)
        {
            Log.Warning(ex, "MassUpdateExport: GenerateAsync (initial) fehlgeschlagen");
        }

        var dialog = new MassUpdateExportDialog(vm)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
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
