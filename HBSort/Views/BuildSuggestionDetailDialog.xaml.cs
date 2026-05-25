using System.Windows;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// Code-Behind fuer den BuildSuggestionDetailDialog.
/// Wird vom BuildSuggestionsView aufgerufen wenn der User auf einen Bauvorschlag
/// klickt. Beim "Figur anlegen"-Klick mappen wir das ViewModel auf einen
/// PersistMinifigInput und uebergeben es an IMinifigPersistenceService -
/// der Reverse-Match (FloatingParts konsumieren) passiert dort.
/// </summary>
public partial class BuildSuggestionDetailDialog : Window
{
    private readonly BuildSuggestionDetailViewModel _vm;
    private readonly IMinifigPersistenceService _persistence;
    private readonly INotificationService _notifications;

    public BuildSuggestionDetailDialog(
        BuildSuggestionDetailViewModel vm,
        IMinifigPersistenceService persistence,
        INotificationService notifications)
    {
        InitializeComponent();
        _vm = vm;
        _persistence = persistence;
        _notifications = notifications;
        DataContext = _vm;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedBin == null) return;

        _vm.IsCreating = true;
        try
        {
            // PersistMinifigInput aus dem VM aufbauen. Wichtig: QuantityCollected = 0
            // pro Teil, weil der Reverse-Match im Service die Quantities aus dem
            // FloatingPool aufaddiert. Wenn wir die "Have"-Werte hier vorab setzen
            // wuerden, wuerden sie bei voll-vorhandenen Teilen doppelt zaehlen.
            var input = new PersistMinifigInput
            {
                BricklinkId = _vm.BricklinkId,
                Name = _vm.Name,
                ImageUrl = _vm.ImageUrl,
                LocalImagePath = _vm.ImageUrl,
                UserNotes = _vm.UserNotes,
                StorageBinId = _vm.SelectedBin.Bin.Id,
                Confidence = null,           // Bauvorschlag hat keine Brickognize-Konfidenz
                ScanImagePath = null,
                RequiredParts = _vm.Parts.Select(p => new PersistMinifigPart
                {
                    BricklinkPartNo = p.BlPartNo,
                    BricklinkColorId = p.ColorId,
                    PartName = p.PartName,
                    ColorName = p.ColorName,
                    QuantityNeeded = p.QuantityNeeded,
                    QuantityCollected = 0
                }).ToList()
            };

            var result = await _persistence.PersistAndStoreAsync(input);

            // Toast-Meldung mit Vorhanden/Komplett-Info.
            if (result.IsFullyComplete)
            {
                _notifications.ShowSuccess(
                    $"Figur '{_vm.Name}' direkt KOMPLETT angelegt im Fach '{_vm.SelectedBin.Bin.Label}' " +
                    $"({result.ReverseMatchedFloating} Teile aus dem Pool uebernommen).");
            }
            else
            {
                _notifications.ShowSuccess(
                    $"Figur '{_vm.Name}' im Fach '{_vm.SelectedBin.Bin.Label}' angelegt " +
                    $"({result.ReverseMatchedFloating} Teile bereits vorhanden).");
            }

            // v0.1.24-beta.3 (V10): Migration von Legacy-Group-Mode auf
            // SortInstruction. Pre-fetch der Quell-Bilder VOR dem DTO-Bau,
            // weil SortItemLine kein ObservableObject ist - Late-Binding
            // wuerde die Bilder im Modal nicht refreshen. Identisch zum
            // DismantleWizardDialog-Pattern (await ImageLoadTask vor
            // BuildSortInstructionFromState).
            await LoadConsumedImagesAsync(result.ConsumedFloatingParts);

            var binLabel = _vm.SelectedBin.Bin.Label;
            var minifigImage = _vm.ImageUrl;
            var instruction = new HBSort.ViewModels.SortInstruction
            {
                HeaderText = result.IsFullyComplete
                    ? $"Figur '{_vm.Name}' komplett angelegt"
                    : "Operation erfolgreich"
            };
            HBSort.ViewModels.SortInstructionBuilder.AddTakeSections(
                instruction, result.ConsumedFloatingParts);
            HBSort.ViewModels.SortInstructionBuilder.AddMinifigPut(
                instruction, binLabel, _vm.Name, _vm.BricklinkId, minifigImage);

            DialogResult = true;
            Close();

            // ISortInstructionPresenter loest den Owner.DataContext-Walk ab
            // (Phase 1.5-Pattern). App.Services-Service-Locator weil dieser
            // Dialog kein DI-Konstruktor-Parameter dafuer hat.
            var presenter = App.Services.GetRequiredService<ISortInstructionPresenter>();
            presenter.Show(instruction);
        }
        catch (HBSort.Core.Services.InvalidBinKindException strict)
        {
            // v0.1.23 Strict-Mode: Ziel-Bin akzeptiert die Figur nicht.
            Log.Warning(strict, "BuildSuggestion: Strict-Mode-Verletzung");
            _notifications.ShowError(strict.Message);
            _vm.IsCreating = false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BuildSuggestion: Figur anlegen fehlgeschlagen");
            _notifications.ShowError($"Fehler beim Anlegen: {ex.Message}");
            _vm.IsCreating = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block O.5 (Pre-Tag): schliesst diesen modalen
    /// Dialog (DialogResult=false, kein Datenverlust - User hat noch nichts
    /// angelegt) und oeffnet die Settings ueber das MainWindow. Konsistent
    /// zum OpenBinManagement-Pattern in MinifigDetailView/PartLookupView,
    /// aber mit vorherigem Close weil hier ein modaler Dialog ueber dem
    /// MainWindow liegt - Settings-Dialog kann sonst nicht ohne Konflikt
    /// erscheinen. User waehlt den Bauvorschlag nach dem Settings-Schliessen
    /// einfach erneut aus dem "Was kann ich bauen?"-Tab.
    /// </summary>
    private void OpenBinManagement_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        DialogResult = false;
        Close();

        if (owner?.DataContext is HBSort.ViewModels.MainViewModel main)
        {
            // v0.1.22-beta.3: direkt auf Lagerfaecher-Tab springen
            main.OpenSettingsOnTab(SettingsTab.Lagerfaecher);
        }
    }

    /// <summary>
    /// v0.1.24-beta.3 (V10): pre-fetcht die Quell-Teil-Bilder via
    /// IPartImageProvider und schreibt sie direkt in
    /// <c>ConsumedFloatingPartInfo.ImageUrl</c> jedes Eintrags. Wird VOR
    /// dem SortInstruction-DTO-Bau awaited, weil <c>SortItemLine</c> kein
    /// ObservableObject ist und ein nachtraegliches Set die Modal-Bilder
    /// nicht refreshen wuerde. Cache-Hits sind schnelle Disk-Reads;
    /// Cache-Miss = kein Bild (Default), kein User-Hinweis noetig.
    /// </summary>
    private static async Task LoadConsumedImagesAsync(
        IReadOnlyList<HBSort.Core.Services.ConsumedFloatingPartInfo> consumed)
    {
        var imageProvider = App.Services.GetRequiredService<IPartImageProvider>();

        foreach (var c in consumed)
        {
            try
            {
                var url = await imageProvider.GetImageFileByBlAsync(
                    "P", c.BlPartNo, c.BlColorId);
                if (!string.IsNullOrEmpty(url))
                    c.ImageUrl = url;
            }
            catch (Exception ex)
            {
                Log.Debug(ex,
                    "BuildSuggestion: Bild fuer konsumiertes Teil {Part}/{Color} nicht ladbar",
                    c.BlPartNo, c.BlColorId);
            }
        }
    }
}
