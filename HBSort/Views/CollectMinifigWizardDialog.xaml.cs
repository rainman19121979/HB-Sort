using System.Windows;
using System.Windows.Input;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// v0.1.24-beta.1 Phase 2a (Konzept 4.1.2 Rev. 3): 2-stufiger Wizard fuer
/// das Anlegen einer neuen wartenden Figur aus dem PartLookup-Pfad.
/// Ersetzt <c>CollectMinifigSelectionDialog</c>.
///
/// Save-Flow (Konzept 4.3 Trigger 1):
///   1. Service-Call <see cref="IPartLookupService.CollectMinifigFromSupersetWithSelectionAsync"/>
///      mit Reverse-Match-Liste + manuell-markierten Teilen
///   2. Post-Save-Modal via <see cref="ISortInstructionPresenter"/> -
///      User klickt aktiv weg, KEIN Auto-Dismiss.
///   3. DialogResult=true, Wizard schliesst.
///
/// Strict-Mode-Verletzung (Konzept O9): Error-Dialog erscheint, Wizard
/// bleibt offen, Bin-Liste wird neu gezogen, User waehlt erneut.
/// </summary>
public partial class CollectMinifigWizardDialog : Window
{
    private readonly CollectMinifigWizardViewModel _viewModel;
    private readonly string _triggerPartNo;
    private readonly int _triggerColorId;
    private readonly int _triggerQuantity;

    /// <summary>
    /// Wird vom Aufrufer (PartLookupView) nach erfolgreichem Speichern
    /// gesetzt - falls vorhanden, wird das gerade gescannte Pending-Part
    /// auf null gesetzt damit der Workflow abgeschlossen ist.
    /// </summary>
    public CollectMinifigResult? SaveResult { get; private set; }

    public CollectMinifigWizardDialog(
        CollectMinifigWizardViewModel viewModel,
        string triggerPartNo,
        int triggerColorId,
        int triggerQuantity)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _triggerPartNo = triggerPartNo;
        _triggerColorId = triggerColorId;
        _triggerQuantity = triggerQuantity;
        DataContext = _viewModel;
    }

    // ----- Keyboard -----

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Strg+Z -> Zurueck in Stufe 2 (Konzept: State erhalten).
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (_viewModel.IsStageTwo)
            {
                _viewModel.GoToStage1();
                e.Handled = true;
                return;
            }
        }

        // Enter: Stufe 1 -> Weiter, Stufe 2 -> Save.
        // IsDefault auf Buttons reicht NICHT immer (z.B. wenn Combobox
        // fokussiert), darum hier explizit pruefen.
        if (e.Key == Key.Enter)
        {
            // Defensive: nur reagieren wenn kein Default-Button-Handler greift.
            // IsCancel und IsDefault sind auf den Buttons gesetzt.
        }
    }

    // ----- Stufe-1-Buttons -----

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.GoToStage2();
    }

    // ----- Stufe-2-Buttons -----

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.GoToStage1();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedBin == null) return;

        var partLookup = App.Services.GetRequiredService<IPartLookupService>();
        var presenter = App.Services.GetRequiredService<ISortInstructionPresenter>();
        var dialogs = App.Services.GetRequiredService<IDialogService>();
        var imageProvider = App.Services.GetRequiredService<IPartImageProvider>();

        try
        {
            var bin = _viewModel.SelectedBin;
            var result = await partLookup.CollectMinifigFromSupersetWithSelectionAsync(
                blMinifigId: _viewModel.BricklinkId,
                storageBinId: bin.Id,
                triggerPartNo: _triggerPartNo,
                triggerColorId: _triggerColorId,
                triggerQuantity: _triggerQuantity,
                consumePartsFromFloating: _viewModel.SelectedPartsForReverseMatch,
                manuellClaimedParts: _viewModel.ManuellClaimedParts);

            SaveResult = result;

            // ConsumedFloatingPartInfo.ImageUrl wird vom Service nicht
            // befuellt - hier best-effort anreichern (analog ScanViewModel
            // Persist-Pfad). Wenn das Bild nicht ladbar ist: kein Problem,
            // das Modal zeigt halt nur Detail/Quantity ohne Bild.
            foreach (var c in result.ConsumedFloatingParts)
            {
                try
                {
                    c.ImageUrl = await imageProvider.GetImageFileByBlAsync(
                        "P", c.BlPartNo, c.BlColorId);
                }
                catch (System.Exception imgEx)
                {
                    Log.Debug(imgEx, "CollectMinifigWizard: Bild fuer {Part} nicht ladbar", c.BlPartNo);
                }
            }

            // Post-Save-Modal (Konzept 4.3 Trigger 1).
            var instruction = _viewModel.BuildSortInstructionForSave(
                result.ConsumedFloatingParts,
                bin.Label,
                result.IsFullyComplete);
            presenter.Show(instruction);

            DialogResult = true;
            Close();
        }
        catch (InvalidBinKindException strict)
        {
            // Konzept O9 Strict-Mode: Wizard bleibt offen, Bin-Liste reload.
            Log.Warning(strict, "CollectMinifigWizard: Strict-Mode-Verletzung beim Save");
            await dialogs.ShowErrorAsync(
                "Speichern nicht moeglich",
                strict.Message);
            try
            {
                await _viewModel.ReloadAvailableBinsAsync();
            }
            catch (System.Exception reloadEx)
            {
                Log.Error(reloadEx, "CollectMinifigWizard: Bin-Liste-Reload nach Strict-Verletzung fehlgeschlagen");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "CollectMinifigWizard: Save fehlgeschlagen");
            await dialogs.ShowErrorAsync("Fehler", ex.Message);
        }
    }
}
