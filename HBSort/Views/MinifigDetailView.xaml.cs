using System.Windows;
using System.Windows.Controls;
using HBSort.Core.Database;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// Phase-3-Detailansicht der erkannten Minifigur.
/// Wird in das Hauptfenster eingebettet (DataContext = PendingMinifigViewModel).
/// "Verwerfen" und "Abbrechen" blenden die Detail-View aus indem sie
/// das ScanViewModel.PendingMinifig auf null setzen.
/// </summary>
public partial class MinifigDetailView : UserControl
{
    public MinifigDetailView()
    {
        InitializeComponent();
    }

    /// <summary>Holt das ScanViewModel ueber die Window-Hierarchie.</summary>
    private ScanViewModel? GetScanViewModel()
    {
        var window = Window.GetWindow(this);
        if (window?.DataContext is MainViewModel main)
        {
            return main.ScanViewModel;
        }
        return null;
    }

    /// <summary>Verwerfen: Detail-View ausblenden, keine Speicherung.</summary>
    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetScanViewModel();
        if (vm != null)
        {
            vm.PendingMinifig = null;
            vm.MinifigStatusText = string.Empty;
        }
    }

    /// <summary>"In Fach legen": triggert PersistPending im ScanViewModel.</summary>
    private async void Persist_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetScanViewModel();
        if (vm == null) return;
        // UX X.33 v0.1.19-beta.7 Block K: ohne ausgewaehltes Fach nicht
        // speichern. Die Warnung im UI hat den User schon informiert.
        if (vm.PendingMinifig?.SelectedBin == null)
        {
            var dialogs = App.Services.GetRequiredService<IDialogService>();
            await dialogs.ShowInfoAsync(
                "Kein Lagerfach ausgewaehlt",
                "Bitte zuerst ein Lagerfach auswaehlen oder ueber die Lagerfach-Verwaltung ein neues anlegen.");
            return;
        }
        await vm.PersistPendingAsync();
    }

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block K: oeffnet den Settings-Dialog.
    /// User waehlt dort den Lagerfaecher-Tab und legt manuell ein neues
    /// Fach an oder leert ein bestehendes.
    /// </summary>
    private void OpenBinManagement_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window?.DataContext is MainViewModel main)
        {
            main.OpenSettings();
        }
    }

    /// <summary>
    /// UX-Iteration X.4+: "Aus Fach uebernehmen" pro Teilezeile. Tag der
    /// Button-Sender ist das PendingPartViewModel. Der Service-Aufruf laeuft
    /// im ScanViewModel; der Refresh der UI passiert dort auch.
    /// </summary>
    private async void TakeFromBin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not PendingPartViewModel part) return;
        var vm = GetScanViewModel();
        if (vm == null) return;
        await vm.TransferFloatingPartToPendingAsync(part);
    }

    /// <summary>
    /// UX X.32 Block B (v0.1.19): "Direkt zerlegen" - oeffnet den DismantleWizard
    /// im Pending-Mode. Die Figur landet NICHT in der Lagerliste; nur die
    /// markierten Teile werden als Einzelteile in die jeweils passenden Faecher
    /// gelegt. Nach erfolgreichem Wizard-Confirm wird das Pending ausgeblendet
    /// und das Sammel-Popup mit den Bin-Anweisungen gezeigt.
    /// </summary>
    private async void DirectDismantle_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PendingMinifigViewModel pending) return;
        var scan = GetScanViewModel();
        if (scan == null) return;

        var collected = pending.Parts.Where(p => p.QuantityCollected > 0).ToList();
        if (collected.Count == 0)
        {
            App.Services.GetRequiredService<INotificationService>()
                .ShowWarning("Keine Teile markiert - nichts zu zerlegen.");
            return;
        }

        // Wizard im Pending-Mode (TrackedMinifigId=0) bauen, analog zum
        // Standard-Pfad in MinifigSummaryDialog.
        var ctxFactory = App.Services.GetRequiredService<IDbContextFactory<UserDataContext>>();
        var binService = App.Services.GetRequiredService<IStorageBinService>();
        var persistence = App.Services.GetRequiredService<IMinifigPersistenceService>();
        var notif = App.Services.GetRequiredService<INotificationService>();
        var imgProvider = App.Services.GetRequiredService<IPartImageProvider>();
        var catalog = App.Services.GetRequiredService<IBlCatalogService>();
        var partLookup = App.Services.GetRequiredService<IPartLookupService>();
        var settings = App.Services.GetRequiredService<HBSort.Core.Services.ISettingsService>();
        // UX X.33 v0.1.19-beta.7 Block M: Category-Mapping auch im Pending-
        // Mode-Wizard - PartName-Praefix-Match liefert pro Teil das gemappte
        // Bin (sonst Default-Pfad).
        var categoryMapping = App.Services.GetRequiredService<HBSort.Core.Services.ICategoryBinMappingService>();

        var wizardVm = new DismantleWizardViewModel(
            trackedMinifigId: 0,
            ctxFactory, binService, persistence,
            imgProvider, catalog, partLookup, settings, categoryMapping);

        try
        {
            await wizardVm.LoadFromPendingAsync(pending);
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "DirectDismantle LoadFromPending fehlgeschlagen");
            notif.ShowError($"Fehler beim Vorbereiten: {ex.Message}");
            return;
        }

        var dialog = new DismantleWizardDialog(wizardVm, notif)
        {
            Owner = Window.GetWindow(this)
        };

        var result = dialog.ShowDialog();
        if (result == true)
        {
            // Pending wegblenden (gleicher Pfad wie nach Persist).
            scan.PendingMinifig = null;
            scan.MinifigStatusText = string.Empty;

            // Sammel-Popup mit den Bin-Anweisungen aus dem Wizard.
            if (wizardVm.LastBinInstructionItems.Count > 0)
            {
                scan.ShowBinInstructionGroup(wizardVm.LastBinInstructionItems);
            }
        }
    }
}
