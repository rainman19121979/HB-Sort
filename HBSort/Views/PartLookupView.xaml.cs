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

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block K: oeffnet Settings damit der User ein
    /// neues Fach anlegen oder ein bestehendes leeren kann.
    /// </summary>
    private void OpenBinManagement_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window?.DataContext is MainViewModel main)
        {
            main.OpenSettings();
        }
    }

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

            // UX X.33 v0.1.19-beta.7 Block J: Anweisungs-Popup mit Ziel-Bin
            // der wartenden Figur. Konsistent zum StoreFloating_Click-Pfad.
            // Bin-Label ist im WaitingMinifigMatchViewModel direkt verfuegbar
            // (StorageBinLabel - null wenn die Figur kein Fach hat, dann
            // wird kein Popup gezeigt).
            var targetBinLabel = match.StorageBinLabel;
            var partImage = vm.ImageUrl;
            var scan = GetScanViewModel();
            if (scan != null && !string.IsNullOrWhiteSpace(targetBinLabel))
            {
                scan.ShowBinInstruction(targetBinLabel!, partImage);
            }

            // Nach erfolgreichem Assign: PartLookup neu, damit ggf. weitere Mengen
            // dieses Teils noch zuordbar sind (Quantity>1).
            var refreshed = await lookup.LookupPartAsync(vm.BlPartNo, vm.BlColorId);
            vm.ApplyLookupResult(refreshed);

            // Wenn keine wartenden Treffer mehr: Pending ausblenden.
            if (refreshed.WaitingMatches.Count == 0)
            {
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

    /// <summary>
    /// "Lagern"-Button: delegiert an <see cref="ScanViewModel.StoreFloatingFromPendingPartAsync"/>.
    /// UX X.33 v0.1.19-beta.7 Block M Erweiterung: gesamte Logik (Service-
    /// Calls, Auto-Mapping, Toast, Anweisungs-Popup, PendingPart=null) lebt
    /// im ScanViewModel - damit der Enter-Hotkey im MainWindow den gleichen
    /// Pfad ausloesen kann.
    /// </summary>
    private async void StoreFloating_Click(object sender, RoutedEventArgs e)
    {
        var scan = GetScanViewModel();
        if (scan == null) return;
        await scan.StoreFloatingFromPendingPartAsync();
    }

    // (ShowSupersets_Click + Button entfernt - Spec UX-1 FIX 5.
    //  SupersetsDialog + ViewModel bleiben fuer evtl. spaeteren Re-Use bestehen.)

    /// <summary>
    /// "Diese Figur anlegen" auf einem BL-Catalog-Treffer.
    ///
    /// UX X.32 v0.1.19-beta.4 Block D: Vor-Auswahl-Dialog. User markiert
    /// welche Required-Parts beim Anlegen aus dem Floating-Pool gezogen
    /// werden sollen. Trigger-Teil ist immer markiert + nicht abwaehlbar,
    /// Reverse-Match-Treffer sind vor-markiert (gruen), Rest ist demarkiert
    /// (grau / "fehlt - bleibt wartend").
    /// </summary>
    private async void CollectFromBlCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BlCatalogMatchViewModel match) return;
        if (DataContext is not PartLookupViewModel vm) return;

        var notif = Service<INotificationService>();
        var partLookup = Service<IPartLookupService>();
        var binService = Service<IStorageBinService>();
        var catalog = Service<IBlCatalogService>();
        var imageProvider = Service<IPartImageProvider>();
        var settings = App.Services.GetRequiredService<HBSort.Core.Services.ISettingsService>();
        var maxWaiting = settings.Current.MaxWaitingFiguresPerBin;

        // UX X.32 v0.1.19-beta.4 (User-Befund): kein Default-Bin mehr aus
        // dem Floating-Pool des gerade gescannten Teils. Der Selection-
        // Dialog hat jetzt seinen eigenen Bin-Picker - Default = wartende-
        // Figur-Vorschlag aus dem StorageBinService. Vorher landete die
        // neue wartende Figur faelschlicherweise im Fach des Einzelteils.
        var selectionVm = new ViewModels.CollectMinifigSelectionViewModel(
            catalog, partLookup, imageProvider, binService);

        vm.IsBusy = true;
        try
        {
            await selectionVm.LoadAsync(
                match.BlMinifigId,
                vm.BlPartNo, vm.BlColorId,
                maxWaitingLimit: maxWaiting);
        }
        catch (System.Exception loadEx)
        {
            Log.Error(loadEx, "CollectMinifigSelection LoadAsync fehlgeschlagen");
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync("Fehler", loadEx.Message);
            vm.IsBusy = false;
            return;
        }
        vm.IsBusy = false;

        if (selectionVm.AvailableBins.Count == 0)
        {
            notif.ShowWarning("Kein Lagerfach verfuegbar - bitte erst ein Fach anlegen.");
            return;
        }

        var dialog = new CollectMinifigSelectionDialog(selectionVm)
        {
            Owner = Window.GetWindow(this)
        };
        var result = dialog.ShowDialog();
        if (result != true) return; // User hat verworfen

        // User-gewaehltes Bin aus dem Dialog (Default war SuggestBinForWaiting).
        var bin = selectionVm.SelectedBin;
        if (bin == null)
        {
            notif.ShowWarning("Kein Lagerfach gewaehlt.");
            return;
        }

        // Service-Aufruf mit User-Auswahl.
        vm.IsBusy = true;
        try
        {
            var collectResult = await partLookup.CollectMinifigFromSupersetWithSelectionAsync(
                match.BlMinifigId,
                bin.Id,
                vm.BlPartNo,
                vm.BlColorId,
                Math.Max(1, vm.Quantity),
                selectionVm.SelectedPartsForReverseMatch);

            // Toast + ggf. Sammel-Popup.
            var scan = GetScanViewModel();
            var consumed = collectResult.ConsumedFloatingParts;

            if (collectResult.IsFullyComplete)
            {
                notif.ShowSuccess(
                    $"Figur '{collectResult.SavedMinifig.Name}' komplett in '{bin.Label}'!");
            }
            else
            {
                notif.ShowSuccess(
                    $"Figur '{collectResult.SavedMinifig.Name}' angelegt in '{bin.Label}'.");
            }

            // UX X.32 v0.1.19-beta.4 (User-Befund): Anweisungs-Popup analog
            // zum PersistPendingAsync-Pfad - bei 2+ konsumierten Teilen
            // Sammel-Popup, sonst Einzel-Popup ("Lege Figur in Box X").
            // Vorher kam bei 0 konsumierten Teilen NUR ein Toast - der
            // User wollte aber konsistent das gleiche Popup wie beim
            // direkten Wartend-Anlegen.
            if (scan != null)
            {
                if (consumed.Count >= 2)
                {
                    var items = new List<HBSort.ViewModels.BinInstructionItem>
                    {
                        new()
                        {
                            ItemLabel = $"Figur '{collectResult.SavedMinifig.Name}'",
                            QuantityText = "1 Stueck",
                            BinLabel = bin.Label,
                            ImageUrl = selectionVm.ImageUrl
                        }
                    };
                    items.AddRange(consumed.Select(c => new HBSort.ViewModels.BinInstructionItem
                    {
                        ItemLabel = $"{c.PartName} ({c.BlPartNo}) - {c.ColorName}",
                        QuantityText = $"{c.Quantity} Stueck (aus {c.SourceBinLabel})",
                        BinLabel = bin.Label
                    }));
                    scan.ShowBinInstructionGroup(items);
                }
                else
                {
                    // Einzel-Popup mit Bin-Label + Figur-Bild.
                    scan.ShowBinInstruction(bin.Label, selectionVm.ImageUrl);
                }
            }

            // Pending ausblenden - Workflow ist abgeschlossen.
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
