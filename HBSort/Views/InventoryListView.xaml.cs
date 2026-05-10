using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    ///
    /// UX X.29 Block C Nachbesserung (v0.1.16): Wartende Figuren in der
    /// Selektion werden uebersprungen und im Hinweis-Toast vermerkt - sonst
    /// koennte der User markieren, exportieren und sich wundern warum
    /// Wartende fehlen.
    /// </summary>
    private async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InventoryListViewModel vm) return;
        var minifigIds = vm.SelectedCompletes
            .Select(r => r.UnderlyingMinifigId!.Value)
            .ToList();
        var floatingIds = vm.SelectedFloatings
            .Select(r => r.UnderlyingFloatingId!.Value)
            .ToList();

        // Wartende-Anteil zaehlen (fuer User-Hinweis).
        var waitingSelected = vm.SelectedTotalCount - vm.SelectedExportableCount;

        if (minifigIds.Count == 0 && floatingIds.Count == 0)
        {
            // Kein einziger exportierbarer Eintrag in der Auswahl - moeglich
            // wenn nur Wartende markiert sind.
            if (waitingSelected > 0)
            {
                await Service<IDialogService>().ShowInfoAsync(
                    "Keine exportierbaren Items",
                    $"In der Auswahl sind {waitingSelected} wartende Figur(en) - " +
                    "diese koennen nicht exportiert werden, weil sie noch nicht " +
                    "alle Teile haben. Markiere komplette Figuren oder Einzelteile.");
            }
            return;
        }

        // Hinweis-Toast falls Wartende uebersprungen werden.
        if (waitingSelected > 0)
        {
            Service<INotificationService>().ShowInfo(
                $"{waitingSelected} wartende Figur(en) werden uebersprungen - nur " +
                $"komplette Figuren und Einzelteile koennen exportiert werden.");
        }

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

    /// <summary>
    /// UX X.29 Block C (v0.1.16): Doppelklick auf eine Zeile oeffnet die
    /// Detail-Ansicht (gleicher Pfad wie der Details-Button).
    /// </summary>
    private void InventoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Klick auf einen Button im Cell-Template darf nicht das Doppelklick
        // auf die Zeile triggern.
        if (e.OriginalSource is FrameworkElement fe
            && (FindAncestor<Button>(fe) != null))
            return;
        if (InventoryGrid.SelectedItem is InventoryRowItem row)
        {
            OpenDetailsForRow(row);
            e.Handled = true;
        }
    }

    /// <summary>
    /// UX X.29 Block C (v0.1.16): Entf-Taste loescht die selektierten Items.
    /// Wenn Multi-Select via Checkboxen aktiv: Bulk-Loeschen.
    /// Sonst: einzelne markierte Zeile via DataGrid.SelectedItem.
    /// In TextBoxen (z.B. Such-Feld) NICHT greifen.
    /// </summary>
    private async void InventoryGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox) return;
        if (DataContext is not InventoryListViewModel vm) return;

        // UX X.29 Block C Nachbesserung: Bulk-Loeschen wirkt jetzt auch auf
        // Wartende. SelectedTotalCount enthaelt alle markierten Items.
        if (vm.SelectedTotalCount > 0)
        {
            await BulkDeleteAsync(vm);
            e.Handled = true;
            return;
        }

        if (InventoryGrid.SelectedItem is InventoryRowItem row)
        {
            await DeleteSingleAsync(row);
            e.Handled = true;
        }
    }

    /// <summary>UX X.29 Block C: Toolbar-Button Bulk-Loeschen.</summary>
    private async void BulkDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        if (DataContext is not InventoryListViewModel vm) return;
        if (!b.IsEnabled) return;
        b.IsEnabled = false;
        try
        {
            await BulkDeleteAsync(vm);
        }
        finally
        {
            b.IsEnabled = true;
        }
    }

    private async Task BulkDeleteAsync(InventoryListViewModel vm)
    {
        // UX X.29 Block C Nachbesserung: SelectedMinifigIds umfasst Wartende +
        // Complete - Bulk-Loeschen wirkt jetzt auch auf wartende Figuren.
        var minifigIds = vm.SelectedMinifigIds.ToList();
        var floatingIds = vm.SelectedFloatingPartIds.ToList();
        var total = minifigIds.Count + floatingIds.Count;
        if (total == 0) return;

        var dialogs = Service<IDialogService>();
        var msg = $"{total} markierte(s) Item(s) loeschen?\n\n" +
                  $"  - {minifigIds.Count} Figur(en) (wartend + komplett)\n" +
                  $"  - {floatingIds.Count} Einzelteil-Eintrag/Eintraege\n\n" +
                  $"Strg+Z (oder Verlauf-Tab) macht die Aktion rueckgaengig.";
        if (!await dialogs.ShowQuestionAsync("Markierte loeschen?", msg)) return;

        try
        {
            var persistence = Service<IMinifigPersistenceService>();
            var (mDel, fDel) = await persistence.DeleteSelectionAsync(minifigIds, floatingIds);
            var notif = Service<INotificationService>();
            notif.ShowSuccess($"{mDel} Figur(en) und {fDel} Einzelteil(e) geloescht. Strg+Z fuer Rueckgaengig.");
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Bulk-Delete fehlgeschlagen");
            await dialogs.ShowErrorAsync("Fehler", ex.Message);
        }
    }

    /// <summary>UX X.29 Block C: Toolbar-Button Bulk-Verschieben.</summary>
    private async void BulkMove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        if (DataContext is not InventoryListViewModel vm) return;
        if (!b.IsEnabled) return;
        b.IsEnabled = false;
        try
        {
            // UX X.29 Block C Nachbesserung: Bulk-Verschieben wirkt auch auf
            // Wartende - SelectedMinifigIds enthaelt Wartend + Complete.
            var minifigIds = vm.SelectedMinifigIds.ToList();
            var floatingIds = vm.SelectedFloatingPartIds.ToList();
            var total = minifigIds.Count + floatingIds.Count;
            if (total == 0) return;

            var binService = Service<IStorageBinService>();
            var allBins = await binService.GetAllAsync();
            if (allBins.Count == 0)
            {
                await Service<IDialogService>().ShowInfoAsync(
                    "Keine Lagerfaecher",
                    "Es sind noch keine Lagerfaecher angelegt. Lege erst eines an in Einstellungen -> Lagerfaecher.");
                return;
            }

            // Bin-Picker (Phase-5-Helper aus SupersetsDialog.xaml.cs - statische
            // Show-Methode, kein eigener Dialog noetig).
            var targetBin = BinPickerDialog.Show(
                Window.GetWindow(this)!,
                allBins,
                defaultBin: null);
            if (targetBin == null) return;
            var targetBinId = targetBin.Id;

            var dialogs = Service<IDialogService>();
            var msg = $"{total} markierte(s) Item(s) in '{targetBin.Label}' verschieben?\n\n" +
                      $"  - {minifigIds.Count} Figur(en)\n" +
                      $"  - {floatingIds.Count} Einzelteil-Eintrag/Eintraege\n\n" +
                      $"Strg+Z macht die Verschiebung von Figuren rueckgaengig. " +
                      $"Einzelteile muessten manuell zurueckverschoben werden.";
            if (!await dialogs.ShowConfirmAsync("Bulk-Verschieben?", msg, okText: "Verschieben", cancelText: "Abbrechen")) return;

            try
            {
                var persistence = Service<IMinifigPersistenceService>();

                // UX X.32 v0.1.19-beta.4 Block E: Vorab Item-Labels einsammeln
                // damit das Sammel-Popup nach dem Move ein "Lege X in Box Y"
                // pro bewegtem Item zeigen kann. Wir lesen die DB BEVOR der
                // Move passiert - die Labels sind sonst nach dem Update
                // identisch zur Ziel-Bin-Spalte und wir koennen die User-
                // Anweisung nicht mehr formulieren.
                var ctxFactory = Service<Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Database.UserDataContext>>();
                var movedItems = new List<HBSort.ViewModels.BinInstructionItem>();
                await using (var ctx = await ctxFactory.CreateDbContextAsync())
                {
                    if (minifigIds.Count > 0)
                    {
                        var minifigs = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(
                                ctx.TrackedMinifigs.Where(m => minifigIds.Contains(m.Id))));
                        foreach (var m in minifigs)
                        {
                            movedItems.Add(new HBSort.ViewModels.BinInstructionItem
                            {
                                ItemLabel = $"Figur '{m.Name ?? m.BricklinkId ?? "?"}'",
                                QuantityText = "1 Stueck",
                                BinLabel = targetBin.Label
                            });
                        }
                    }
                    if (floatingIds.Count > 0)
                    {
                        var floats = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(
                                ctx.FloatingParts.Where(p => floatingIds.Contains(p.Id))));
                        foreach (var fp in floats)
                        {
                            movedItems.Add(new HBSort.ViewModels.BinInstructionItem
                            {
                                ItemLabel = $"{fp.PartName} ({fp.PartNumber}) - {fp.ColorName}",
                                QuantityText = $"{fp.Quantity} Stueck",
                                BinLabel = targetBin.Label
                            });
                        }
                    }
                }

                var (mMov, fMov) = await persistence.MoveSelectionAsync(minifigIds, floatingIds, targetBinId);
                var notif = Service<INotificationService>();
                notif.ShowSuccess($"{mMov} Figur(en) und {fMov} Einzelteil(e) nach '{targetBin.Label}' verschoben.");

                // Sammel-Popup mit Item-Liste. Nur ab 1 bewegten Item zeigen
                // (bei 0 macht ein Popup keinen Sinn).
                if (movedItems.Count > 0
                    && Window.GetWindow(this)?.DataContext is MainViewModel mainVm
                    && mainVm.ScanViewModel != null)
                {
                    mainVm.ScanViewModel.ShowBinInstructionGroup(movedItems);
                }
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "Bulk-Move fehlgeschlagen");
                await dialogs.ShowErrorAsync("Fehler", ex.Message);
            }
        }
        finally
        {
            b.IsEnabled = true;
        }
    }

    /// <summary>Hilfsmethode: oeffnet Details-Dialog fuer eine Zeile.</summary>
    private void OpenDetailsForRow(InventoryRowItem row)
    {
        var window = Window.GetWindow(this);
        if (row.Type == InventoryItemType.Minifig && row.UnderlyingMinifigId.HasValue)
        {
            var ctxFactory = Service<Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Database.UserDataContext>>();
            var binService = Service<IStorageBinService>();
            var notif = Service<INotificationService>();
            var persistence = Service<IMinifigPersistenceService>();
            var imgProvider = Service<IPartImageProvider>();
            var catalog = Service<IBlCatalogService>();
            var vm = new MinifigSummaryViewModel(row.UnderlyingMinifigId.Value, ctxFactory, binService, imgProvider, catalog);
            var dialog = new MinifigSummaryDialog(vm, notif, persistence) { Owner = window };
            dialog.ShowDialog();
        }
        else if (row.Type == InventoryItemType.FloatingPart && row.UnderlyingFloatingId.HasValue)
        {
            var imgProvider = Service<IPartImageProvider>();
            var dialog = new FloatingPartDetailDialog(row, imgProvider) { Owner = window };
            dialog.ShowDialog();
        }
    }

    /// <summary>Hilfsmethode: einzelne Zeile loeschen (Pfad analog Loeschen-Button).</summary>
    private async Task DeleteSingleAsync(InventoryRowItem row)
    {
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
                var msg = $"{statusLabel} Figur '{row.Description}' loeschen?\n\nStrg+Z macht die Aktion rueckgaengig.";
                if (!await dialogs.ShowQuestionAsync("Figur loeschen?", msg)) return;
                await Service<IMinifigPersistenceService>().DeleteAsync(row.UnderlyingMinifigId.Value);
                notif.ShowSuccess($"Figur '{row.Description}' geloescht.");
            }
            else if (row.Type == InventoryItemType.FloatingPart && row.UnderlyingFloatingId.HasValue)
            {
                var msg = $"Einzelteil '{row.Description}' x{row.Quantity} loeschen?\n\nStrg+Z macht die Aktion rueckgaengig.";
                if (!await dialogs.ShowQuestionAsync("Einzelteil loeschen?", msg)) return;
                await Service<IPartLookupService>().DeleteFloatingPartAsync(row.UnderlyingFloatingId.Value);
                notif.ShowSuccess($"Einzelteil '{row.Description}' geloescht.");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Inventory Delete fehlgeschlagen");
            await dialogs.ShowErrorAsync("Fehler", ex.Message);
        }
    }

    /// <summary>Sucht den naechsten Vorfahren eines bestimmten Typs im Visual-/Logical-Tree.</summary>
    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not InventoryRowItem row) return;

        // UX X.28 (v0.1.15) Bug-Fix: ModernWpfUI ContentDialog erlaubt nur
        // EINEN offenen Dialog gleichzeitig. Schnelles Mehrfach-Klicken auf
        // verschiedene Loeschen-Buttons hat eine InvalidOperationException
        // ausgeloest. Button waehrend des Delete-Vorgangs disablen schuetzt
        // davor + ist UX-konsistent (User sieht "der Button reagiert nicht").
        if (!b.IsEnabled) return;
        b.IsEnabled = false;

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
        finally
        {
            // Button wieder freigeben (auch im Fehler-Fall + bei abgebrochener
            // Confirmation). Falls der Button im Visual-Tree ersetzt wurde
            // (z.B. weil die Liste neu geladen hat): kein Effekt, schadet nicht.
            b.IsEnabled = true;
        }
    }
}
