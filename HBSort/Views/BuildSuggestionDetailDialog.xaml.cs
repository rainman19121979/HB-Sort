using System.Windows;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
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
                StorageBinId = _vm.SelectedBin.Id,
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
                    $"Figur '{_vm.Name}' direkt KOMPLETT angelegt im Fach '{_vm.SelectedBin.Label}' " +
                    $"({result.ReverseMatchedFloating} Teile aus dem Pool uebernommen).");
            }
            else
            {
                _notifications.ShowSuccess(
                    $"Figur '{_vm.Name}' im Fach '{_vm.SelectedBin.Label}' angelegt " +
                    $"({result.ReverseMatchedFloating} Teile bereits vorhanden).");
            }

            DialogResult = true;
            Close();
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
}
