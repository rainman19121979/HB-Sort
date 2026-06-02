using System.ComponentModel;
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

        // Spalten-Persistenz (analog zur Splitter-Persistenz im SortingView):
        // - Loaded   -> gespeichertes Layout wieder anwenden.
        // - Sorting  -> aktive Sortierung sofort merken (fire-and-forget Save).
        // - Unloaded -> aktuelle Spaltenbreiten einsammeln + speichern.
        // Unloaded feuert beim Tab-Wechsel weg UND beim App-Schliessen - das
        // ist der robuste Trigger fuer die Breiten (DataGrid hat kein sauberes
        // Per-Spalte-DragCompleted-Event wie der GridSplitter).
        Loaded += InventoryListView_Loaded;
        Unloaded += InventoryListView_Unloaded;
        InventoryGrid.Sorting += InventoryGrid_Sorting;
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
            var blInventory = Service<IBlInventoryService>();

            var vm = new MinifigSummaryViewModel(
                row.UnderlyingMinifigId.Value, ctxFactory, binService,
                imgProvider, catalog, persistence, blInventory);
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

            // Bin-Picker (statische Show-Methode, kein eigener Dialog noetig).
            // v0.1.24-beta.1 Phase 3 (Aufgabe G): BinPickerDialog wurde aus
            // SupersetsDialog.xaml.cs in eine eigene Datei (BinPickerDialog.cs)
            // extrahiert; SupersetsDialog selbst ist mit dem Wizard-Pfad obsolet
            // und entfallen.
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

                // v0.1.24-beta.1 (Trigger 2): Vor dem Move die Quell-Bins
                // einsammeln, damit das Post-Save-Modal eine korrekte
                // "Nimm aus X"-Sektion pro Quell-Bin zeigen kann. Vorher
                // (v0.1.23) wurde nur die Ziel-Bin-Sektion gebaut, jetzt
                // gehoeren Quell-Sektionen zum Pattern (Konzept 4.3.6).
                var ctxFactory = Service<Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Database.UserDataContext>>();

                // Pro Quell-Bin sammeln wir die Items die rauswandern.
                var sourceMap = new Dictionary<int, (string Label, List<HBSort.ViewModels.SortItemLine> Items)>();

                async System.Threading.Tasks.Task EnsureBinLabelAsync(int binId, Core.Database.UserDataContext ctx)
                {
                    if (sourceMap.ContainsKey(binId)) return;
                    var bin = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                        ctx.StorageBins.Where(x => x.Id == binId));
                    sourceMap[binId] = (bin?.Label ?? $"Fach #{binId}", new List<HBSort.ViewModels.SortItemLine>());
                }

                var putItems = new List<HBSort.ViewModels.SortItemLine>();
                // Phase 1.5 Polish (Praxis-Test 2026-05-15): FloatingParts haben
                // kein ImageUrl-Feld in der DB — fuers Modal ueber den Image-Provider
                // pro BL-ID + ColorId nachladen. Best-effort: bei Fehler bleibt das
                // Bild leer (Modal-Layout zeigt dann nur ID + Name, kein Crash).
                var imgProvider = Service<IPartImageProvider>();
                async System.Threading.Tasks.Task<string?> LoadFloatingImageAsync(string partNo, int colorId)
                {
                    try { return await imgProvider.GetImageFileByBlAsync("P", partNo, colorId); }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Bulk-Move Bild-Lookup fuer {Part}/{Color} fehlgeschlagen",
                            partNo, colorId);
                        return null;
                    }
                }
                await using (var ctx = await ctxFactory.CreateDbContextAsync())
                {
                    if (minifigIds.Count > 0)
                    {
                        var minifigs = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(
                                ctx.TrackedMinifigs.Where(m => minifigIds.Contains(m.Id))));
                        foreach (var m in minifigs)
                        {
                            var sourceBinId = m.StorageBinId ?? -1;
                            if (sourceBinId != -1)
                            {
                                await EnsureBinLabelAsync(sourceBinId, ctx);
                                sourceMap[sourceBinId].Items.Add(new HBSort.ViewModels.SortItemLine
                                {
                                    Label = $"Figur '{m.Name ?? m.BricklinkId ?? "?"}'",
                                    Detail = m.BricklinkId ?? string.Empty,
                                    QuantityText = "1x",
                                    ImageUrl = m.LocalImagePath ?? m.ImageUrl
                                });
                            }
                            putItems.Add(new HBSort.ViewModels.SortItemLine
                            {
                                Label = $"Figur '{m.Name ?? m.BricklinkId ?? "?"}'",
                                Detail = m.BricklinkId ?? string.Empty,
                                QuantityText = "1x",
                                ImageUrl = m.LocalImagePath ?? m.ImageUrl
                            });
                        }
                    }
                    if (floatingIds.Count > 0)
                    {
                        var floats = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(
                                ctx.FloatingParts.Where(p => floatingIds.Contains(p.Id))));

                        // Phase 1.5 Polish: Bilder parallel laden statt sequenziell
                        // foreach-await. Bei 10 Items: ~2s -> ~200ms.
                        // LoadFloatingImageAsync nutzt nur imgProvider, keinen ctx,
                        // daher kein Konflikt mit dem offenen DbContext.
                        var imageUrls = await System.Threading.Tasks.Task.WhenAll(
                            floats.Select(fp => LoadFloatingImageAsync(fp.PartNumber, fp.ColorId)));

                        for (int i = 0; i < floats.Count; i++)
                        {
                            var fp = floats[i];
                            var imageUrl = imageUrls[i];
                            await EnsureBinLabelAsync(fp.StorageBinId, ctx);
                            sourceMap[fp.StorageBinId].Items.Add(new HBSort.ViewModels.SortItemLine
                            {
                                Label = fp.PartName,
                                Detail = $"{fp.PartNumber} - {fp.ColorName}",
                                QuantityText = $"{fp.Quantity}x",
                                ImageUrl = imageUrl
                            });
                            putItems.Add(new HBSort.ViewModels.SortItemLine
                            {
                                Label = fp.PartName,
                                Detail = $"{fp.PartNumber} - {fp.ColorName}",
                                QuantityText = $"{fp.Quantity}x",
                                ImageUrl = imageUrl
                            });
                        }
                    }
                }

                // Quell-Bin == Ziel-Bin Eintraege filtern (kein "Nimm aus &
                // Lege in dasselbe Bin"-Doppel) — Konzept E1.
                sourceMap.Remove(targetBinId);

                var (mMov, fMov) = await persistence.MoveSelectionAsync(minifigIds, floatingIds, targetBinId);
                var notif = Service<INotificationService>();
                notif.ShowSuccess($"{mMov} Figur(en) und {fMov} Einzelteil(e) nach '{targetBin.Label}' verschoben.");

                // v0.1.24-beta.1 (Konzept 4.3): Post-Save-Modal mit Take/Put-
                // Sektionen. Take pro Quell-Bin, Put mit allen Items in das
                // Ziel-Bin.
                if (mMov + fMov > 0
                    && Window.GetWindow(this)?.DataContext is MainViewModel mainVm
                    && mainVm.ScanViewModel != null)
                {
                    var instruction = new HBSort.ViewModels.SortInstruction
                    {
                        HeaderText = "Operation erfolgreich"
                    };
                    foreach (var (_, entry) in sourceMap)
                    {
                        if (entry.Items.Count == 0) continue;
                        instruction.Take.Add(new HBSort.ViewModels.SortSection
                        {
                            BinLabel = entry.Label,
                            Items = entry.Items
                        });
                    }
                    instruction.Put.Add(new HBSort.ViewModels.SortSection
                    {
                        BinLabel = targetBin.Label,
                        Items = putItems
                    });
                    mainVm.ScanViewModel.ShowSortInstruction(instruction);
                }
            }
            catch (HBSort.Core.Services.InvalidBinKindException strict)
            {
                // v0.1.23 Strict-Mode: Ziel-Bin akzeptiert mind. ein Item nicht.
                // v0.1.24-beta.1 (Konzept E10): Sortier-Modal erscheint NICHT,
                // statt dessen Error-Dialog mit konkreter Strict-Mode-Message.
                Log.Warning(strict, "Bulk-Move: Strict-Mode-Verletzung");
                await dialogs.ShowErrorAsync("Verschieben nicht moeglich", strict.Message);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException race)
            {
                // v0.1.24-beta.1 (Konzept E10): Race-Bedingung beim Save.
                Log.Warning(race, "Bulk-Move: DB-Konflikt beim Speichern");
                await dialogs.ShowErrorAsync(
                    "Konflikt beim Speichern",
                    "Die Daten haben sich in der Zwischenzeit geaendert. Bitte Auswahl erneut treffen.");
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
            var blInventory = Service<IBlInventoryService>();
            var vm = new MinifigSummaryViewModel(row.UnderlyingMinifigId.Value, ctxFactory, binService, imgProvider, catalog, persistence, blInventory);
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

    // ====================================================================
    // Spalten-Persistenz (Breiten + Sortierung)
    //
    // Vorbild: Splitter-Persistenz im SortingView (gleiches Muster -
    // Werte in AppSettings, sofortiges fire-and-forget Save, Anwenden im
    // Loaded). Identifikation der Spalten ueber den Header-Text.
    // ====================================================================

    /// <summary>
    /// Wendet das gespeicherte Spalten-Layout (Breiten + Sortierung) beim
    /// Sichtbarwerden des Views an. Defensiv: unbekannte/ungueltige Werte
    /// werden ignoriert (siehe InventoryGridLayoutHelper).
    /// </summary>
    private void InventoryListView_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var layout = Service<ISettingsService>().Current.InventoryGridLayout;
            if (layout is null) return;

            // --- Breiten anwenden ---
            foreach (var column in InventoryGrid.Columns)
            {
                var header = column.Header as string;

                // Star-Spalten (Breite waechst mit dem Fenster) NICHT ueberschreiben -
                // die sollen flexibel bleiben. Nur Absolut-Breiten setzen.
                if (column.Width.IsStar) continue;

                var width = InventoryGridLayoutHelper.ResolveWidth(layout, header);
                if (width.HasValue)
                    column.Width = new DataGridLength(width.Value);
            }

            // --- Sortierung anwenden ---
            ApplySavedSort(layout);
        }
        catch (System.Exception ex)
        {
            // Layout-Anwenden darf nie den Tab killen - schlimmstenfalls
            // erscheint das Grid in den XAML-Defaults.
            Log.Warning(ex, "InventoryListView: Spalten-Layout konnte nicht angewendet werden");
        }
    }

    /// <summary>
    /// Stellt die gemerkte Sortierung wieder her: setzt die SortDirection auf
    /// der passenden Spalte und baut die SortDescription der CollectionView.
    /// </summary>
    private void ApplySavedSort(Core.Models.InventoryGridLayout layout)
    {
        if (string.IsNullOrEmpty(layout.SortColumnHeader)) return;

        // Spalte per Header finden (kann fehlen, wenn das Layout sich geaendert hat).
        DataGridColumn? target = null;
        foreach (var column in InventoryGrid.Columns)
        {
            if (InventoryGridLayoutHelper.TryGetSort(layout, column.Header as string, out _))
            {
                target = column;
                break;
            }
        }
        if (target is null) return;

        // SortMemberPath bestimmt nach welchem Property tatsaechlich sortiert
        // wird. TextColumns binden direkt (z.B. "Quantity"), TemplateColumns
        // setzen es explizit im XAML (SortMemberPath="ColorName" etc.).
        var sortPath = target.SortMemberPath;
        if (string.IsNullOrEmpty(sortPath)) return;

        var direction = layout.SortAscending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;

        // Pfeil im Spalten-Header anzeigen.
        target.SortDirection = direction;

        // Tatsaechliche Sortierung auf der CollectionView. ItemsSource ist die
        // ItemsView des VMs (eine ICollectionView); sonst Default-View holen.
        var view = InventoryGrid.ItemsSource as System.ComponentModel.ICollectionView
                   ?? (InventoryGrid.ItemsSource is System.Collections.IEnumerable items
                       ? System.Windows.Data.CollectionViewSource.GetDefaultView(items)
                       : null);
        if (view is not null)
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(sortPath, direction));
        }
    }

    /// <summary>
    /// Sortier-Event des DataGrids: merkt sich die neu gewaehlte Sortier-Spalte
    /// + Richtung. Das DataGrid setzt die neue SortDirection erst NACH diesem
    /// Event-Handler, deshalb leiten wir die kommende Richtung selbst ab
    /// (Standard-Toggle-Verhalten von WPF: keine -> Asc -> Desc -> Asc ...).
    /// Wir uebernehmen das Default-Sort-Verhalten (e.Handled bleibt false).
    /// </summary>
    private void InventoryGrid_Sorting(object? sender, DataGridSortingEventArgs e)
    {
        try
        {
            var header = e.Column.Header as string;
            if (string.IsNullOrEmpty(header)) return;

            // Kommende Richtung ableiten (WPF toggelt nach diesem Handler):
            // null oder Descending -> Ascending, Ascending -> Descending.
            var nextAscending = e.Column.SortDirection != ListSortDirection.Ascending;

            var settings = Service<ISettingsService>();
            settings.Current.InventoryGridLayout.SortColumnHeader = header;
            settings.Current.InventoryGridLayout.SortAscending = nextAscending;
            _ = settings.SaveAsync();
        }
        catch (System.Exception ex)
        {
            Log.Warning(ex, "InventoryListView: Sortierung konnte nicht gespeichert werden");
        }
    }

    /// <summary>
    /// Beim Verlassen des Views (Tab-Wechsel weg / App-Schliessen): die
    /// aktuellen Spaltenbreiten einsammeln und persistieren. Star-Spalten
    /// werden ausgelassen, damit "Beschreibung" weiter mitwaechst.
    /// </summary>
    private void InventoryListView_Unloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = Service<ISettingsService>();
            var layout = settings.Current.InventoryGridLayout;

            var changed = false;
            foreach (var column in InventoryGrid.Columns)
            {
                if (column.Header is not string header || string.IsNullOrEmpty(header)) continue;

                // Star-Spalten nicht speichern (sollen flexibel bleiben).
                if (column.Width.IsStar) continue;

                // ActualWidth ist die tatsaechlich gerenderte Breite nach
                // User-Drag; DisplayValue faellt darauf zurueck.
                var width = column.ActualWidth;
                if (double.IsNaN(width) || width < InventoryGridLayoutHelper.MinColumnWidth) continue;

                layout.ColumnWidths[header] = width;
                changed = true;
            }

            if (changed)
                _ = settings.SaveAsync();
        }
        catch (System.Exception ex)
        {
            Log.Warning(ex, "InventoryListView: Spaltenbreiten konnten nicht gespeichert werden");
        }
    }
}
