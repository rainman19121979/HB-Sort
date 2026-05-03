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
/// Einzelteil-Info (TODO Phase 7). "Loeschen" mit Confirmation entfernt
/// Figur (DeleteAsync) oder Einzelteil (DeleteFloatingPartAsync).
/// </summary>
public partial class InventoryListView : UserControl
{
    public InventoryListView()
    {
        InitializeComponent();
    }

    private static T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is InventoryListViewModel vm) vm.ClearFilters();
    }

    /// <summary>
    /// Phase 7: nach jedem Klick auf eine Komplett-Checkbox den globalen
    /// Counter (SelectedCompleteCount) im VM neu berechnen.
    /// </summary>
    private void CompleteSelect_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is InventoryListViewModel vm) vm.RecalculateSelection();
    }

    /// <summary>
    /// Phase 7: oeffnet den BSX-Export-Dialog mit den aktuell selektierten
    /// kompletten Figuren.
    /// </summary>
    private void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InventoryListViewModel vm) return;
        var ids = vm.SelectedCompletes
            .Select(r => r.UnderlyingMinifigId!.Value)
            .ToList();
        if (ids.Count == 0) return;

        var dialog = Service<BsxExportDialog>();
        dialog.Initialize(ids);
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not InventoryRowItem row) return;
        var window = Window.GetWindow(this);

        if (row.Type == InventoryItemType.Minifig && row.UnderlyingMinifigId.HasValue)
        {
            var ctxFactory = Service<Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Database.UserDataContext>>();
            var binService = Service<IStorageBinService>();
            var notif = Service<INotificationService>();
            var persistence = Service<IMinifigPersistenceService>();
            var imgProvider = Service<IPartImageProvider>();
            var catalog = Service<IBlCatalogService>();
            var priceCalc = Service<IPriceCalculationService>();
            var settings = Service<ISettingsService>();

            var vm = new MinifigSummaryViewModel(
                row.UnderlyingMinifigId.Value, ctxFactory, binService,
                imgProvider, catalog, priceCalc, settings);
            var dialog = new MinifigSummaryDialog(vm, notif, persistence) { Owner = window };
            dialog.ShowDialog();
        }
        else
        {
            // Einzelteil-Detail-Popup: kompakte Info
            MessageBox.Show(
                $"Einzelteil:\n\n" +
                $"  Beschreibung: {row.Description}\n" +
                $"  BL-Part-No:   {row.ItemId}\n" +
                $"  Farbe:        {row.ColorName} (BL:{row.ColorId})\n" +
                $"  Anzahl:       {row.Quantity}\n" +
                $"  Lagerfach:    {row.StorageBinLabel}",
                "Einzelteil-Details", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not InventoryRowItem row) return;
        var notif = Service<INotificationService>();

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
                if (MessageBox.Show(msg, "Figur loeschen?",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                    != MessageBoxResult.Yes) return;

                var persistence = Service<IMinifigPersistenceService>();
                await persistence.DeleteAsync(row.UnderlyingMinifigId.Value);
                notif.ShowSuccess($"Figur '{row.Description}' geloescht.");
            }
            else if (row.Type == InventoryItemType.FloatingPart && row.UnderlyingFloatingId.HasValue)
            {
                var msg = $"Einzelteil '{row.Description}' x{row.Quantity} wirklich loeschen?\n\n" +
                          "Diese Aktion kann nicht rueckgaengig gemacht werden.";
                if (MessageBox.Show(msg, "Einzelteil loeschen?",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                    != MessageBoxResult.Yes) return;

                var partLookup = Service<IPartLookupService>();
                await partLookup.DeleteFloatingPartAsync(row.UnderlyingFloatingId.Value);
                notif.ShowSuccess($"Einzelteil '{row.Description}' geloescht.");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Inventory Delete fehlgeschlagen");
            MessageBox.Show(ex.Message, "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
