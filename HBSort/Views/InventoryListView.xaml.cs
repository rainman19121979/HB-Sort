using System.Windows;
using System.Windows.Controls;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// Tab "Lagerliste". DataContext = InventoryListViewModel.
/// Klick auf "Details" oeffnet MinifigSummaryDialog (Figur) bzw. zeigt
/// Einzelteil-Info. "Loeschen" mit Confirmation entfernt Figur
/// (DeleteAsync) oder Einzelteil (DeleteFloatingPartAsync).
/// </summary>
public partial class InventoryListView : UserControl
{
    public InventoryListView()
    {
        InitializeComponent();

        // UX X.28 (v0.1.15): Lagerliste laed automatisch bei jedem Tab-Wechsel.
        // Vorher musste der User manuell "Aktualisieren" klicken.
        // IsVisibleChanged statt Loaded: Loaded feuert nur einmal beim ersten
        // Sichtbar-Werden, IsVisibleChanged bei jedem Visibility-Toggle. Das
        // deckt MainTab-Wechsel ab (Visibility-Binding gegen IsMainTabInventory).
        IsVisibleChanged += async (_, args) =>
        {
            if (args.NewValue is bool isVisible
                && isVisible
                && DataContext is InventoryListViewModel vm)
            {
                try { await vm.LoadAsync(); }
                catch (System.Exception ex) { Log.Warning(ex, "InventoryListView Auto-Load fehlgeschlagen"); }
            }
        };
    }

    private static T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is InventoryListViewModel vm) vm.ClearFilters();
    }

    /// <summary>
    /// Wanted-List-Export-Button: oeffnet den Export-Dialog. Der Dialog kuemmert
    /// sich um Modus-Auswahl, Ordner-Picker und das tatsaechliche Schreiben.
    /// </summary>
    private void WantedListExport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = Service<WantedListExportDialog>();
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
    }

    /// <summary>
    /// Phase 7 + UX X.6: oeffnet den BSX-Export-Dialog mit den aktuell selektierten
    /// kompletten Figuren UND Einzelteilen. Beide Listen werden separat an den
    /// Dialog uebergeben - der zeigt sie in zwei Sektionen an und exportiert beides
    /// in dieselbe BSX-Datei.
    /// </summary>
    private void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InventoryListViewModel vm) return;
        var minifigIds = vm.SelectedCompletes
            .Select(r => r.UnderlyingMinifigId!.Value)
            .ToList();
        var floatingIds = vm.SelectedFloatings
            .Select(r => r.UnderlyingFloatingId!.Value)
            .ToList();
        if (minifigIds.Count == 0 && floatingIds.Count == 0) return;

        var dialog = Service<BsxExportDialog>();
        dialog.Initialize(minifigIds, floatingIds);
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
    }

    // async void weil wir aus dem Click-Handler einen async DialogService aufrufen.
    // Synchroner Code-Pfad (Minifig-Detail-Dialog) bleibt unten unveraendert.
    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not InventoryRowItem row) return;
        var window = Window.GetWindow(this);

        if (row.Type == InventoryItemType.Minifig && row.UnderlyingMinifigId.HasValue)
        {
            // UX X.20 Teil 7: priceCalc + settings werden vom MinifigSummaryViewModel
            // nicht mehr gebraucht (Verkaufsempfehlungs-Block raus). Aufrufe entfallen.
            var ctxFactory = Service<Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Database.UserDataContext>>();
            var binService = Service<IStorageBinService>();
            var notif = Service<INotificationService>();
            var persistence = Service<IMinifigPersistenceService>();
            var imgProvider = Service<IPartImageProvider>();
            var catalog = Service<IBlCatalogService>();

            var vm = new MinifigSummaryViewModel(
                row.UnderlyingMinifigId.Value, ctxFactory, binService,
                imgProvider, catalog);
            var dialog = new MinifigSummaryDialog(vm, notif, persistence) { Owner = window };
            dialog.ShowDialog();
        }
        else
        {
            // UX X.20 Teil 1: Einzelteil-Detail-Popup als eigener Dialog im
            // Stil des MinifigSummaryDialog (statt der alten Plain-Text-
            // ShowInfoAsync-Variante).
            await System.Threading.Tasks.Task.CompletedTask; // Methode bleibt async wegen anderer Pfade.
            var imgProvider = Service<IPartImageProvider>();
            var dialog = new FloatingPartDetailDialog(row, imgProvider) { Owner = window };
            dialog.ShowDialog();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not InventoryRowItem row) return;
        var notif = Service<INotificationService>();
        var dialogs = Service<IDialogService>();

        try
        {
            if (row.Type == InventoryItemType.Minifig && row.UnderlyingMinifigId.HasValue)
            {
                var statusLabel = row.Status switch
                {
                    StatusKind.Waiting  => "Wartende",
                    StatusKind.Complete => "Komplette",
                    StatusKind.Sold     => "Verkaufte",
                    _                    => string.Empty
                };
                var msg = $"{statusLabel} Figur '{row.Description}' wirklich loeschen?\n\n" +
                          "Diese Aktion kann nicht rueckgaengig gemacht werden.";
                // Destruktiv -> "Ja" / "Nein"
                if (!await dialogs.ShowQuestionAsync("Figur loeschen?", msg)) return;

                var persistence = Service<IMinifigPersistenceService>();
                await persistence.DeleteAsync(row.UnderlyingMinifigId.Value);
                notif.ShowSuccess($"Figur '{row.Description}' geloescht.");
            }
            else if (row.Type == InventoryItemType.FloatingPart && row.UnderlyingFloatingId.HasValue)
            {
                var msg = $"Einzelteil '{row.Description}' x{row.Quantity} wirklich loeschen?\n\n" +
                          "Diese Aktion kann nicht rueckgaengig gemacht werden.";
                // Destruktiv -> "Ja" / "Nein"
                if (!await dialogs.ShowQuestionAsync("Einzelteil loeschen?", msg)) return;

                var partLookup = Service<IPartLookupService>();
                await partLookup.DeleteFloatingPartAsync(row.UnderlyingFloatingId.Value);
                notif.ShowSuccess($"Einzelteil '{row.Description}' geloescht.");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Inventory Delete fehlgeschlagen");
            await dialogs.ShowErrorAsync("Fehler", ex.Message);
        }
    }
}
