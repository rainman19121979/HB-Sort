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

            // UX X.32 v0.1.19-beta.5/6 (User-Befund): Sammel-Popup mit den
            // konsumierten FloatingParts (pro Teil "Hole aus Quell-Bin")
            // PLUS am Ende ein "Lege fertige Figur in Ziel-Fach"-Eintrag
            // (IsTargetItem=true fuer visuelle Abgrenzung im Overlay).
            // Items kommen aus result.ConsumedFloatingParts (vom Service
            // mit Quell-Bin-Label seit X.32 befuellt).
            var binLabel = _vm.SelectedBin.Bin.Label;
            var minifigImage = _vm.ImageUrl;
            var instructionItems = result.ConsumedFloatingParts
                .Select(c => new HBSort.ViewModels.BinInstructionItem
                {
                    ItemLabel = $"{c.PartName} ({c.BlPartNo}) - {c.ColorName}",
                    QuantityText = $"{c.Quantity} Stueck",
                    BinLabel = c.SourceBinLabel,
                })
                .ToList();
            // Quell-Items bewusst zuerst, dann der Ziel-Eintrag (separator
            // im Overlay erscheint vor diesem IsTargetItem=true).
            instructionItems.Add(new HBSort.ViewModels.BinInstructionItem
            {
                ItemLabel = $"Lege fertige Figur \"{_vm.Name}\" in das Ziel-Fach",
                QuantityText = "1 Stueck",
                BinLabel = binLabel,
                ImageUrl = minifigImage,
                IsTargetItem = true
            });

            // ScanViewModel ueber den Owner-Window-DataContext erreichen.
            // Owner = MainWindow -> DataContext = MainViewModel.
            var ownerWindow = Owner;
            if (ownerWindow?.DataContext is HBSort.ViewModels.MainViewModel mainVm
                && mainVm.ScanViewModel != null
                && instructionItems.Count > 0)
            {
                mainVm.ScanViewModel.ShowBinInstructionGroup(
                    instructionItems,
                    headerText: "Nimm folgende Teile aus den Faechern und lege die fertige Figur in das Ziel-Fach:");

                // UX X.32 v0.1.19-beta.6: Bilder fuer die konsumierten
                // FloatingParts async nachladen. Items sind ObservableObjects -
                // ImageUrl-Property triggert das Bild-Refresh im Overlay
                // sobald geladen. Quell-Items haben i.d.R. lokales Cache-
                // Bild ueber IPartImageProvider; bei Cache-Miss bleibt das
                // Item ohne Bild (nicht kritisch, kein User-Hinweis).
                _ = LoadConsumedImagesAsync(result.ConsumedFloatingParts, instructionItems);
            }

            DialogResult = true;
            Close();
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
    /// UX X.32 v0.1.19-beta.6: pro konsumiertem FloatingPart das Teil-Bild
    /// via IPartImageProvider laden + auf das BinInstructionItem.ImageUrl
    /// setzen. Items sind ObservableObjects - das Overlay refreshed seine
    /// Bilder automatisch sobald die Properties gesetzt werden.
    /// Erste N Items in <paramref name="items"/> entsprechen den N
    /// <paramref name="consumed"/>-Eintraegen (gleiche Reihenfolge);
    /// das letzte Item ist der Ziel-Eintrag (Figur) und wird hier nicht
    /// mehr angefasst.
    /// </summary>
    private static async Task LoadConsumedImagesAsync(
        IReadOnlyList<HBSort.Core.Services.ConsumedFloatingPartInfo> consumed,
        IReadOnlyList<HBSort.ViewModels.BinInstructionItem> items)
    {
        var imageProvider = App.Services.GetRequiredService<IPartImageProvider>();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        for (int i = 0; i < consumed.Count && i < items.Count; i++)
        {
            var c = consumed[i];
            var item = items[i];
            try
            {
                var url = await imageProvider.GetImageFileByBlAsync(
                    "P", c.BlPartNo, c.BlColorId);
                if (string.IsNullOrEmpty(url)) continue;

                if (dispatcher != null)
                    dispatcher.Invoke(() => item.ImageUrl = url);
                else
                    item.ImageUrl = url;
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
