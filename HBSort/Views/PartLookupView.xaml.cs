using System.Windows;
using System.Windows.Controls;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// Phase 5: Detail-View fuer ein erkanntes Teil (Modus B). DataContext = PartLookupViewModel.
/// Buttons fuhren ueber den IPartLookupService die jeweiligen Aktionen aus.
/// </summary>
public partial class PartLookupView : UserControl
{
    public PartLookupView()
    {
        InitializeComponent();
    }

    private ScanViewModel? GetScanViewModel()
    {
        var window = Window.GetWindow(this);
        if (window?.DataContext is MainViewModel main) return main.ScanViewModel;
        return null;
    }

    private static T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    /// <summary>Verwerfen: Pending-Part ausblenden, kein DB-Zugriff.</summary>
    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetScanViewModel();
        if (vm != null) vm.PendingPart = null;
    }

    /// <summary>"Zuordnen"-Button auf einer wartenden Figur: AssignPartToMinifigAsync.</summary>
    private async void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not WaitingMinifigMatchViewModel match) return;
        if (DataContext is not PartLookupViewModel vm) return;

        var lookup = Service<IPartLookupService>();
        var notif = Service<INotificationService>();
        vm.IsBusy = true;
        try
        {
            var completed = await lookup.AssignPartToMinifigAsync(match.TrackedMinifigPartId);
            if (completed)
            {
                notif.ShowSuccess($"Teil zugeordnet - Figur '{match.MinifigName}' jetzt KOMPLETT!");
            }
            else
            {
                notif.ShowSuccess($"Teil zu '{match.MinifigName}' zugeordnet.");
            }

            // Nach erfolgreichem Assign: PartLookup neu, damit ggf. weitere Mengen
            // dieses Teils noch zuordbar sind (Quantity>1).
            var refreshed = await lookup.LookupPartAsync(vm.BlPartNo, vm.BlColorId);
            vm.ApplyLookupResult(refreshed);

            // Wenn keine wartenden Treffer mehr: Pending ausblenden.
            if (refreshed.WaitingMatches.Count == 0)
            {
                var scan = GetScanViewModel();
                if (scan != null) scan.PendingPart = null;
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Assign-Part fehlgeschlagen");
            notif.ShowError($"Fehler beim Zuordnen: {ex.Message}");
        }
        finally
        {
            vm.IsBusy = false;
        }
    }

    /// <summary>"Lagern"-Button: AddPartToFloatingAsync.</summary>
    private async void StoreFloating_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PartLookupViewModel vm) return;
        if (vm.SelectedFloatingBin == null)
        {
            Service<INotificationService>().ShowWarning("Bitte ein Lagerfach auswaehlen.");
            return;
        }
        if (vm.FloatingQuantity <= 0)
        {
            Service<INotificationService>().ShowWarning("Anzahl muss > 0 sein.");
            return;
        }

        var lookup = Service<IPartLookupService>();
        var notif = Service<INotificationService>();
        vm.IsBusy = true;
        try
        {
            await lookup.AddPartToFloatingAsync(
                vm.BlPartNo, vm.BlColorId,
                vm.PartName, vm.ColorName,
                vm.FloatingQuantity, vm.SelectedFloatingBin.Id);

            notif.ShowSuccess(
                $"{vm.FloatingQuantity}x '{vm.PartName}' im Fach '{vm.SelectedFloatingBin.Label}' gelagert.");

            var scan = GetScanViewModel();
            if (scan != null) scan.PendingPart = null;
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Floating-Lagern fehlgeschlagen");
            notif.ShowError($"Fehler: {ex.Message}");
        }
        finally
        {
            vm.IsBusy = false;
        }
    }

    // (ShowSupersets_Click + Button entfernt - Spec UX-1 FIX 5.
    //  SupersetsDialog + ViewModel bleiben fuer evtl. spaeteren Re-Use bestehen.)

    /// <summary>
    /// "Diese Figur anlegen" auf einem BL-Catalog-Treffer: legt die Figur an,
    /// markiert das gescannte Trigger-Teil sofort als gesammelt, macht
    /// Reverse-Match gegen vorhandene Einzelteile.
    /// </summary>
    private async void CollectFromBlCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BlCatalogMatchViewModel match) return;
        if (DataContext is not PartLookupViewModel vm) return;

        var notif = Service<INotificationService>();
        var partLookup = Service<IPartLookupService>();
        var binService = Service<IStorageBinService>();

        // Default-Bin: aktuell in der Floating-Combo selektiertes Fach,
        // sonst erstes freies (Konsistenz mit dem Lager-Workflow).
        var bin = vm.SelectedFloatingBin
                  ?? await binService.GetNextFreeAsync()
                  ?? vm.AvailableBins.FirstOrDefault();
        if (bin == null)
        {
            notif.ShowWarning("Kein Lagerfach verfuegbar - bitte erst ein Fach anlegen.");
            return;
        }

        vm.IsBusy = true;
        try
        {
            var minifig = await partLookup.CollectMinifigFromSupersetAsync(
                match.BlMinifigId,
                bin.Id,
                vm.BlPartNo,
                vm.BlColorId,
                Math.Max(1, vm.Quantity));

            notif.ShowSuccess(
                $"Figur '{minifig.Name}' angelegt in '{bin.Label}' - Trigger-Teil sofort gesammelt.");

            // Pending ausblenden - Workflow ist abgeschlossen.
            var scan = GetScanViewModel();
            if (scan != null) scan.PendingPart = null;
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "CollectFromBlCatalog fehlgeschlagen");
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync("Fehler", ex.Message);
        }
        finally
        {
            vm.IsBusy = false;
        }
    }

    /// <summary>Korrektur-Dropdown: User waehlt eine andere Farbe.</summary>
    private async void Color_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not PartLookupViewModel vm) return;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not BlColor color) return;
        if (color.ColorId == vm.BlColorId) return; // unchanged

        var scan = GetScanViewModel();
        if (scan == null) return;

        await scan.RefreshPartLookupForColorAsync(color.ColorId);
    }
}
