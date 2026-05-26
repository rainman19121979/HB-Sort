using System.Windows;
using System.Windows.Controls;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// Dialog mit Detail-Info zu einer wartenden Minifigur. Bietet Aufgeben /
/// Verschieben / Schliessen.
/// Datenaenderungen feuern DataChanged via PersistenceService, damit die
/// Wartende-Liste sich automatisch refresht.
/// </summary>
public partial class MinifigSummaryDialog : Window
{
    private readonly MinifigSummaryViewModel _viewModel;
    private readonly INotificationService _notifications;
    private readonly IMinifigPersistenceService _persistence;

    public MinifigSummaryDialog(
        MinifigSummaryViewModel viewModel,
        INotificationService notifications,
        IMinifigPersistenceService persistence)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _notifications = notifications;
        _persistence = persistence;
        DataContext = _viewModel;

        // Daten initial laden, wenn das Fenster geladen ist (async erlaubt im Loaded).
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>"Loeschen": entfernt die Figur komplett. Mit harter Confirmation.</summary>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var statusLabel = _viewModel.Status switch
        {
            Core.Models.TrackedMinifigStatus.Waiting    => "wartende",
            Core.Models.TrackedMinifigStatus.Complete   => "komplette",
            Core.Models.TrackedMinifigStatus.Dismantled => "zerlegte",
            _                                            => string.Empty
        };

        var dialogs = App.Services.GetRequiredService<IDialogService>();
        var msg = $"Diese {statusLabel} Figur '{_viewModel.Name}' wirklich komplett LOESCHEN?\n\n" +
                  "Diese Aktion kann nicht rueckgaengig gemacht werden.\n\n" +
                  "Floating-Parts die aus dieser Figur stammen bleiben bestehen, " +
                  "verlieren aber die Origin-Markierung.";
        // Destruktiv -> "Ja" / "Nein"
        if (!await dialogs.ShowQuestionAsync("Figur loeschen?", msg)) return;

        try
        {
            var ok = await _persistence.DeleteAsync(_viewModel.MinifigId);
            if (ok)
            {
                _notifications.ShowSuccess($"Figur '{_viewModel.Name}' geloescht.");
                DialogResult = true;
                Close();
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Loeschen fehlgeschlagen");
            await dialogs.ShowErrorAsync("Fehler", ex.Message);
        }
    }

    private void GiveUp_Click(object sender, RoutedEventArgs e)
    {
        // Aufgeben-Wizard: User entscheidet pro Teil "uebernehmen" oder "verwerfen"
        // und waehlt das Ziel-Fach. Persistenz via IMinifigPersistenceService.DismantleAsync.
        var ctxFactory = App.Services.GetRequiredService<
            Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Database.UserDataContext>>();
        var binService = App.Services.GetRequiredService<IStorageBinService>();
        var persistence = App.Services.GetRequiredService<IMinifigPersistenceService>();
        var notif = App.Services.GetRequiredService<INotificationService>();
        var imgProvider = App.Services.GetRequiredService<IPartImageProvider>();
        var catalog = App.Services.GetRequiredService<IBlCatalogService>();
        var partLookup = App.Services.GetRequiredService<IPartLookupService>();
        var settings = App.Services.GetRequiredService<HBSort.Core.Services.ISettingsService>();
        // UX X.33 v0.1.19-beta.7 Block M: Wizard nutzt das Category-Mapping
        // ueber PartName-Praefix-Match (BL-Part-Name beginnt typischerweise
        // mit dem Brickognize-Category-String).
        var categoryMapping = App.Services.GetRequiredService<HBSort.Core.Services.ICategoryBinMappingService>();

        var wizardVm = new ViewModels.DismantleWizardViewModel(
            _viewModel.MinifigId, ctxFactory, binService, persistence,
            imgProvider, catalog, partLookup, settings, categoryMapping);
        var wizard = new DismantleWizardDialog(wizardVm, notif) { Owner = this };
        if (wizard.ShowDialog() == true)
        {
            // Wizard hat den Vorgang ausgefuehrt + Toast gezeigt - Summary-Dialog schliessen.
            DialogResult = true;
            Close();
        }
    }

    /// <summary>
    /// Phase 5: Click auf Teile-Checkbox. Toggelt zwischen Assigned (collected=needed)
    /// und Unassigned (collected=0). Nutzt IPartLookupService.
    /// </summary>
    private async void PartCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not SummaryPartViewModel partVm) return;

        var lookup = App.Services.GetRequiredService<IPartLookupService>();
        // CheckBox.IsChecked ist nach dem Click bereits umgeschaltet (true = jetzt geklickt).
        bool nowChecked = cb.IsChecked == true;

        try
        {
            if (nowChecked)
            {
                // Assign: increment um 1, ggf. mehrfach bis QuantityNeeded.
                while (partVm.QuantityCollected < partVm.QuantityNeeded)
                {
                    var done = await lookup.AssignPartToMinifigAsync(partVm.Id);
                    partVm.QuantityCollected = Math.Min(partVm.QuantityCollected + 1, partVm.QuantityNeeded);
                    if (done) break; // Figur komplett -> Schleife endet
                }
                _notifications.ShowSuccess($"'{partVm.PartName}' als gefunden markiert.");
            }
            else
            {
                // v0.1.24-beta.8 Phase 3 (Fix 4): wenn das Teil BL-Reservierungen
                // hat, diese ZUERST aufheben (Shop-Bestand freigeben + ScanEvent).
                // Danach den physischen Stand zuruecksetzen wie bisher.
                int releasedBl = 0;
                if (partVm.QuantityReservedFromBl > 0)
                {
                    releasedBl = await _viewModel.ReleaseAllBlReservationsAsync(partVm);
                }
                await lookup.UnassignPartFromMinifigAsync(partVm.Id);
                partVm.QuantityCollected = 0;
                if (releasedBl > 0)
                    _notifications.ShowInfo(
                        $"Markierung fuer '{partVm.PartName}' aufgehoben. " +
                        $"{releasedBl} BL-Shop-Reservierung(en) freigegeben.");
                else
                    _notifications.ShowInfo($"Markierung fuer '{partVm.PartName}' aufgehoben.");
            }
            // Dialog-Daten neu laden, damit auch ProgressLabel/CompletedParts stimmen.
            await _viewModel.LoadAsync();

            // Phase 6: nach manueller Mengen-Aenderung pruefen ob die Figur jetzt
            // erst komplett ist - dann Status setzen + Toast + UI nachladen.
            var nowComplete = await _persistence.CheckAndMarkCompleteAsync(_viewModel.MinifigId);
            if (nowComplete)
            {
                _notifications.ShowSuccess($"Figur '{_viewModel.Name}' ist komplett!");
                await _viewModel.LoadAsync();
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "PartCheckBox toggle fehlgeschlagen");
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync("Fehler", ex.Message);
            // Visuellen Zustand zurueckdrehen
            cb.IsChecked = !nowChecked;
        }
    }

    // UX X.20 Teil 7: "Wieder oeffnen"-Button + Reopen_Click-Handler entfernt.
    // Status-Wechsel laeuft jetzt automatisch ueber das Toggling der Teile-
    // Haekchen (siehe PartCheckBox_Click + PartLookupService.UnassignPart...).

    /// <summary>
    /// v0.1.24-beta.8 Phase 3: Klick auf das BL-Shop-Badge eines fehlenden
    /// Required-Parts. Oeffnet den Reservierungs-Dialog; bei Bestaetigung
    /// laeuft die Reservierung ueber den VM-Pfad (Lot reservieren +
    /// QuantityReservedFromBl++ + ScanEvent + Komplett-Check).
    /// </summary>
    private async void BlBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement el
            || el.Tag is not SummaryPartViewModel partVm) return;
        if (partVm.BlAvailability is not { HasAny: true } availability) return;

        var dialog = new BlReserveDialog(partVm, _viewModel.Name, availability)
        {
            Owner = this
        };
        var ok = dialog.ShowDialog();
        if (ok != true || dialog.SelectedLot == null) return;

        try
        {
            var success = await _viewModel.ApplyBlReservationAsync(partVm, dialog.SelectedLot);
            if (success)
            {
                var conditionLabel = dialog.SelectedLot.Condition == "N" ? "Neu" : "Gebraucht";
                _notifications.ShowSuccess(
                    $"'{partVm.PartName}' ({conditionLabel}) im BL-Shop fuer '{_viewModel.Name}' reserviert.");
            }
            else
            {
                _notifications.ShowError(
                    "Reservierung fehlgeschlagen - das Lot ist eventuell schon verbraucht. " +
                    "Bitte BL-Inventar neu synchronisieren.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BlBadge_Click: Reservierung geworfen");
            _notifications.ShowError($"Fehler bei der Reservierung: {ex.Message}");
        }
    }

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.MoveTarget == null)
        {
            _notifications.ShowWarning("Bitte ein Ziel-Fach auswaehlen.");
            return;
        }

        // v0.1.24-beta.1 (Konzept 4.3 Trigger 4): Quell-Bin-Label vor dem
        // Move merken — der LoadAsync-Stand des VM hat das aktuelle Fach
        // schon geladen (BinLabel-Property). Damit baut sich das Post-Save-
        // Modal mit korrekter "Nimm aus X / Lege in Y"-Anweisung.
        var oldBinLabel = _viewModel.BinLabel;
        var targetBinLabel = _viewModel.MoveTarget.Bin.Label;
        var minifigName = _viewModel.Name;
        var bricklinkId = _viewModel.BricklinkId;
        var imageUrl = _viewModel.ImageUrl;

        try
        {
            var ok = await _viewModel.MoveToAsync(_viewModel.MoveTarget.Bin.Id);
            if (ok)
            {
                _notifications.ShowSuccess(
                    $"Figur '{minifigName}' in Fach '{targetBinLabel}' verschoben.");
                _persistence.RaiseDataChanged();
                DialogResult = true;
                Close();

                // v0.1.24-beta.1: Post-Save-Modal mit Take/Put-Sektionen.
                // Phase 1.5: ISortInstructionPresenter statt Owner-Cast -
                // funktioniert auch wenn dieser Dialog selbst nicht direkt
                // vom MainWindow geoeffnet wird (konsistent mit den
                // verschachtelten Wizard-Pfaden).
                var item = new HBSort.ViewModels.SortItemLine
                {
                    Label = $"Figur '{minifigName}'",
                    Detail = bricklinkId,
                    QuantityText = "1x",
                    ImageUrl = imageUrl
                };
                var instruction = new HBSort.ViewModels.SortInstruction
                {
                    HeaderText = "Operation erfolgreich"
                };
                instruction.Take.Add(new HBSort.ViewModels.SortSection
                {
                    BinLabel = oldBinLabel,
                    Items = new List<HBSort.ViewModels.SortItemLine> { item }
                });
                instruction.Put.Add(new HBSort.ViewModels.SortSection
                {
                    BinLabel = targetBinLabel,
                    Items = new List<HBSort.ViewModels.SortItemLine> { item }
                });
                var presenter = App.Services.GetRequiredService<ISortInstructionPresenter>();
                presenter.Show(instruction);
            }
        }
        catch (HBSort.Core.Services.InvalidBinKindException strict)
        {
            // v0.1.23 Strict-Mode: Ziel-Bin akzeptiert die Figur nicht.
            // v0.1.24-beta.1 (Konzept E10): Sortier-Modal erscheint NICHT,
            // statt dessen Error-Dialog mit konkreter Strict-Mode-Message.
            Log.Warning(strict, "Verschieben: Strict-Mode-Verletzung");
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync("Verschieben nicht moeglich", strict.Message);
            try { await _viewModel.LoadAsync(); }
            catch (Exception reloadEx) { Log.Warning(reloadEx, "Reload nach Strict-Mode-Fehler fehlgeschlagen"); }
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException race)
        {
            // v0.1.24-beta.1 (Konzept E10): Race-Bedingung beim Save.
            Log.Warning(race, "Verschieben: DB-Konflikt beim Speichern");
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync(
                    "Konflikt beim Speichern",
                    "Die Daten haben sich in der Zwischenzeit geaendert. Bitte Auswahl erneut treffen.");
            try { await _viewModel.LoadAsync(); }
            catch (Exception reloadEx) { Log.Warning(reloadEx, "Reload nach Konflikt fehlgeschlagen"); }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Verschieben fehlgeschlagen");
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync("Verschieben", ex.Message);
        }
    }
}
