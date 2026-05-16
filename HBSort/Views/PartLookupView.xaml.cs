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
            // v0.1.22-beta.3: direkt auf Lagerfaecher-Tab springen.
            main.OpenSettingsOnTab(HBSort.Views.SettingsTab.Lagerfaecher);
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
    /// v0.1.24-beta.1 Phase 2a (Konzept 4.1.2 Rev. 3): nutzt den 2-stufigen
    /// <see cref="CollectMinifigWizardDialog"/>. Stufe 1 zeigt Required-Parts
    /// in 3 Status-Gruppen (Trigger / Im Lager / Fehlt), Stufe 2 waehlt
    /// Lagerfach + Speichern. Post-Save-Modal kommt vom Wizard selbst -
    /// User klickt aktiv weg (KEIN Auto-Dismiss).
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

        var wizardVm = new ViewModels.CollectMinifigWizardViewModel(
            catalog, partLookup, imageProvider, binService,
            maxWaitingLimit: maxWaiting);

        vm.IsBusy = true;
        try
        {
            await wizardVm.LoadAsync(
                match.BlMinifigId,
                vm.BlPartNo, vm.BlColorId);
        }
        catch (System.Exception loadEx)
        {
            Log.Error(loadEx, "CollectMinifigWizard LoadAsync fehlgeschlagen");
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync("Fehler", loadEx.Message);
            vm.IsBusy = false;
            return;
        }
        vm.IsBusy = false;

        if (wizardVm.AvailableBins.Count == 0)
        {
            notif.ShowWarning("Kein Lagerfach verfuegbar - bitte erst ein Fach anlegen.");
            return;
        }

        // Wizard kapselt Service-Call + Post-Save-Modal. Strict-Mode-Errors
        // bleiben im Wizard (User waehlt neu, kein DialogResult=true).
        var triggerQty = Math.Max(1, vm.Quantity);
        var dialog = new CollectMinifigWizardDialog(
            wizardVm, vm.BlPartNo, vm.BlColorId, triggerQty)
        {
            Owner = Window.GetWindow(this)
        };
        var result = dialog.ShowDialog();
        if (result != true || dialog.SaveResult == null) return; // User hat verworfen

        var collectResult = dialog.SaveResult;
        var binLabel = wizardVm.SelectedBin?.Bin.Label ?? string.Empty;

        // Toast als zusaetzliche Bestaetigung (Post-Save-Modal hat der
        // Wizard selbst schon gezeigt).
        if (collectResult.IsFullyComplete)
        {
            notif.ShowSuccess(
                $"Figur '{collectResult.SavedMinifig.Name}' komplett in '{binLabel}'!");
        }
        else
        {
            notif.ShowSuccess(
                $"Figur '{collectResult.SavedMinifig.Name}' angelegt in '{binLabel}'.");
        }

        // Pending ausblenden - Workflow ist abgeschlossen.
        var scan = GetScanViewModel();
        if (scan != null) scan.PendingPart = null;
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
