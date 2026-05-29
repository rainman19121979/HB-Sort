using System.Windows;
using System.Windows.Controls;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;

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
    /// v0.1.24-beta.12: oeffnet den Mass-Update-Export-Dialog.
    /// <para>v0.1.24-beta.13: Dialog wird SOFORT mit Loading-State angezeigt;
    /// der initiale BL-Sync + XML-Generate laeuft als Hintergrund-Task im VM
    /// (<see cref="MassUpdateExportViewModel.InitializeAsync"/>). Damit sieht
    /// der User waehrend des Syncs den Lade-Hinweis statt eines blockierten
    /// Buttons. Fehlerbehandlung (Auth/Rate-Limit/Netz) ist im VM gekapselt.</para>
    /// </summary>
    private void OpenMassUpdate_Click(object sender, RoutedEventArgs e)
    {
        // v0.1.25 (B1): VM ueber DI aufloesen statt per new - gleicher Stil wie
        // die uebrigen Aufloesungen im Code-Behind (App.Services.GetRequiredService).
        var vm = App.Services.GetRequiredService<MassUpdateExportViewModel>();

        // InitializeAsync feuern OHNE await: der Dialog kommt sofort hoch
        // und zeigt seinen Loading-State; sobald Sync+Generate fertig sind,
        // updaten die ObservableProperties die UI per Binding.
        // Fire-and-forget ist hier OK weil Fehler im VM via Try/Catch +
        // SyncInfoText/InfoText behandelt werden (kein Throw nach aussen).
        _ = vm.InitializeAsync();

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
