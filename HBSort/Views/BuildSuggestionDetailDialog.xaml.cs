using System.Windows;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// Code-Behind fuer den BuildSuggestionDetailDialog.
///
/// <para>v0.1.24-beta.11: Quellen-Auswahl pro Teil (Intern/BL-Shop) und
/// einheitliche Anlege-Aktion. Beim Klick auf "Figur anlegen":</para>
/// <list type="number">
///   <item>PersistAndStoreAsync mit Per-Part-SkipReverseMatch (true fuer
///   BL-Wahl) — Service konsumiert Floats nur fuer User-gewaehlte Internal-Parts.</item>
///   <item>Pro ReservedLot: <see cref="IBlInventoryService.ReserveForPartAsync"/>
///   reserviert Lot + erhoeht QuantityReservedFromBl + legt ScanEvent an.</item>
///   <item>SortInstruction mit drei Sektion-Typen (Internal Take, BL-Shop Take, Put).</item>
/// </list>
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

    /// <summary>
    /// v0.1.24-beta.11: Klick auf das "Aus Shop"-Badge eines Teils — oeffnet
    /// den bestehenden BlReserveDialog (Lot-Picker), nach Auswahl wird das
    /// Lot in <see cref="BuildSuggestionPartViewModel.ReservedLot"/> gemerkt
    /// (noch NICHT in der DB, das passiert erst beim Anlegen).
    /// </summary>
    private void BlBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el
            || el.Tag is not BuildSuggestionPartViewModel partVm) return;
        if (partVm.BlAvailability is not { HasAny: true } availability) return;

        var dialog = new BlReserveDialog(partVm.PartName, _vm.Name, availability)
        {
            Owner = this
        };
        var ok = dialog.ShowDialog();
        if (ok != true || dialog.SelectedLot == null) return;

        // Lot vormerken; Reservierung im BL-Service passiert erst beim Anlegen.
        // (Quelle wird automatisch ueber HasInternal/ReservedLot bestimmt -
        //  Spec v0.1.24-beta.11 Anpassung: kein UseInternal-Toggle mehr.)
        partVm.ReservedLot = dialog.SelectedLot;
    }

    /// <summary>
    /// v0.1.24-beta.11: Klick auf "✓ Shop"-Pill entfernt die Vor-Merkung.
    /// Wenn intern verfuegbar ist, faellt die Anzeige automatisch wieder
    /// auf den "Vorhanden in Box X"-Status zurueck.
    /// </summary>
    private void ClearReservation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el
            || el.Tag is not BuildSuggestionPartViewModel partVm) return;
        partVm.ReservedLot = null;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedBin == null) return;

        _vm.IsCreating = true;
        try
        {
            // 1) PersistMinifigInput aus dem VM aufbauen.
            //    SkipReverseMatch=true fuer Teile die der User auf BL-Shop
            //    umgeschaltet hat — der Service tastet diese FloatingParts
            //    nicht an. Andere Teile (UseInternal=true || gar nicht
            //    verfuegbar): Service macht seinen normalen Reverse-Match.
            var input = new PersistMinifigInput
            {
                BricklinkId = _vm.BricklinkId,
                Name = _vm.Name,
                ImageUrl = _vm.ImageUrl,
                LocalImagePath = _vm.ImageUrl,
                UserNotes = _vm.UserNotes,
                StorageBinId = _vm.SelectedBin.Bin.Id,
                Confidence = null,
                ScanImagePath = null,
                RequiredParts = _vm.Parts.Select(p => new PersistMinifigPart
                {
                    BricklinkPartNo = p.BlPartNo,
                    BricklinkColorId = p.ColorId,
                    PartName = p.PartName,
                    ColorName = p.ColorName,
                    QuantityNeeded = p.QuantityNeeded,
                    QuantityCollected = 0,
                    // v0.1.24-beta.11 (Spec-Anpassung): ReservedLot impliziert
                    // bereits Skip — der Shop-Picker ist nur sichtbar wenn
                    // intern leer ist (HasInternal=false).
                    SkipReverseMatch = p.ReservedLot != null
                }).ToList()
            };

            var result = await _persistence.PersistAndStoreAsync(input);

            // 2) BL-Reservierungen anwenden (post-persist). Pro VM-Part mit
            //    gewaehltem Lot: ReserveForPartAsync ueber den Service — der
            //    reserviert das Lot, erhoeht QuantityReservedFromBl und legt
            //    den ScanEvent an. TrackedMinifigPart-Id aus result.SavedMinifig
            //    matchen per (PartNo, ColorId).
            var blInventory = App.Services.GetRequiredService<IBlInventoryService>();
            var blShopTakes = new List<HBSort.ViewModels.BlShopTake>();
            var partLookup = result.SavedMinifig.RequiredParts
                .ToDictionary(rp => (rp.PartNumber, rp.ColorId), rp => rp);
            foreach (var p in _vm.Parts)
            {
                if (p.ReservedLot == null) continue;
                if (!partLookup.TryGetValue((p.BlPartNo, p.ColorId), out var dbPart))
                {
                    Log.Warning(
                        "BuildSuggestion-Reserve: TrackedMinifigPart fuer {Part}/{Color} nicht gefunden — Lot {Lot} bleibt frei",
                        p.BlPartNo, p.ColorId, p.ReservedLot.LotId);
                    continue;
                }
                var ok = await blInventory.ReserveForPartAsync(dbPart.Id, p.ReservedLot.LotId);
                if (!ok)
                {
                    _notifications.ShowError(
                        $"BL-Reservierung fuer '{p.PartName}' (Lot {p.ReservedLot.LotId}) fehlgeschlagen — eventuell inzwischen verbraucht. Bitte BL-Inventar synchronisieren.");
                    continue;
                }
                // Take-Section fuer die SortInstruction sammeln.
                blShopTakes.Add(new HBSort.ViewModels.BlShopTake
                {
                    BlPartNo = p.BlPartNo,
                    PartName = p.PartName,
                    ColorName = p.ColorName,
                    Remarks = p.ReservedLot.Remarks,
                    Condition = p.ReservedLot.Condition,
                    Quantity = 1,
                    ImageUrl = p.ImageUrl
                });
            }

            // 3) Toast-Meldung mit Vorhanden/Komplett-Info.
            var internalCount = result.ReverseMatchedFloating;
            var shopCount = blShopTakes.Count;
            // Komplett-Status nach Reserve neu pruefen (Effective-Formel):
            // result.IsFullyComplete bezieht sich auf physisch+InitialReserved,
            // unsere Reserve passiert NACHHER — pruefen ob mit unseren Adds
            // alle Required-Parts effektiv gedeckt sind.
            var nowComplete = await IsEffectivelyCompleteAsync(result.SavedMinifig.Id);
            if (nowComplete)
                _notifications.ShowSuccess(
                    $"Figur '{_vm.Name}' komplett angelegt im Fach '{_vm.SelectedBin.Bin.Label}' " +
                    $"({internalCount} aus Lager, {shopCount} aus BL-Shop reserviert).");
            else
                _notifications.ShowSuccess(
                    $"Figur '{_vm.Name}' angelegt im Fach '{_vm.SelectedBin.Bin.Label}' " +
                    $"({internalCount} aus Lager, {shopCount} aus BL-Shop reserviert).");

            // 4) SortInstruction aufbauen: Pre-Fetch Quell-Bilder, dann
            //    Take (Internal) + Take (BL-Shop) + Put (Ziel-Fach).
            await LoadConsumedImagesAsync(result.ConsumedFloatingParts);
            var instruction = new HBSort.ViewModels.SortInstruction
            {
                HeaderText = nowComplete
                    ? $"Figur '{_vm.Name}' komplett angelegt"
                    : "Operation erfolgreich"
            };
            HBSort.ViewModels.SortInstructionBuilder.AddTakeSections(
                instruction, result.ConsumedFloatingParts);
            HBSort.ViewModels.SortInstructionBuilder.AddBlShopTakeSections(
                instruction, blShopTakes);
            HBSort.ViewModels.SortInstructionBuilder.AddMinifigPut(
                instruction, _vm.SelectedBin.Bin.Label, _vm.Name,
                _vm.BricklinkId, _vm.ImageUrl);

            DialogResult = true;
            Close();

            var presenter = App.Services.GetRequiredService<ISortInstructionPresenter>();
            presenter.Show(instruction);
        }
        catch (HBSort.Core.Services.InvalidBinKindException strict)
        {
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

    /// <summary>
    /// v0.1.24-beta.11: nach der BL-Reservierung pruefen ob die Figur jetzt
    /// effektiv komplett ist (physisch + BL-reserviert >= QuantityNeeded).
    /// Frische DB-Query — Service-Status wurde nach unserer Reservierung
    /// nicht neu berechnet.
    /// </summary>
    private static async Task<bool> IsEffectivelyCompleteAsync(int minifigId)
    {
        try
        {
            var ctxFactory = App.Services
                .GetRequiredService<IDbContextFactory<UserDataContext>>();
            await using var ctx = await ctxFactory.CreateDbContextAsync();
            var minifig = await ctx.TrackedMinifigs
                .AsNoTracking()
                .Include(m => m.RequiredParts)
                .FirstOrDefaultAsync(m => m.Id == minifigId);
            if (minifig == null || minifig.RequiredParts.Count == 0) return false;
            return minifig.RequiredParts.All(p => p.IsEffectivelyComplete);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "IsEffectivelyCompleteAsync({Id}) geworfen", minifigId);
            return false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block O.5: schliesst den modalen Dialog
    /// und oeffnet die Settings (Lagerfaecher-Tab).
    /// </summary>
    private void OpenBinManagement_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        DialogResult = false;
        Close();

        if (owner?.DataContext is HBSort.ViewModels.MainViewModel main)
        {
            main.OpenSettingsOnTab(SettingsTab.Lagerfaecher);
        }
    }

    /// <summary>
    /// v0.1.24-beta.3 (V10): pre-fetcht die Quell-Teil-Bilder via
    /// IPartImageProvider und schreibt sie direkt in
    /// <c>ConsumedFloatingPartInfo.ImageUrl</c> jedes Eintrags. Wird VOR
    /// dem SortInstruction-DTO-Bau awaited, weil <c>SortItemLine</c> kein
    /// ObservableObject ist und ein nachtraegliches Set die Modal-Bilder
    /// nicht refreshen wuerde.
    /// </summary>
    private static async Task LoadConsumedImagesAsync(
        IReadOnlyList<ConsumedFloatingPartInfo> consumed)
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
