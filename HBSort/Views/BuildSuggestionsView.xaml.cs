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

/// <summary>Bauvorschlag-Tab. ViewModel via DataContext gebunden.</summary>
public partial class BuildSuggestionsView : UserControl
{
    public BuildSuggestionsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// v0.1.24-beta.11: Footer-Link "Ignorierte verwalten (N)" -> Dialog.
    /// Nach Schliessen wird die Vorschlagsliste refresht wenn der User
    /// mind. eine Aenderung gemacht hat (HasChanges).
    /// </summary>
    private async void ManageIgnored_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BuildSuggestionsViewModel vm) return;

        var sp = App.Services;
        var ctxFactory = sp.GetRequiredService<IDbContextFactory<UserDataContext>>();
        var blCache = sp.GetRequiredService<IBlCacheRepository>();
        var imageProvider = sp.GetRequiredService<IPartImageProvider>();

        var dialogVm = new ManageIgnoredViewModel(ctxFactory, blCache, imageProvider);
        try
        {
            await dialogVm.LoadAsync();
        }
        catch (System.Exception ex)
        {
            Log.Warning(ex, "ManageIgnored: Initial-Load fehlgeschlagen");
        }

        var dialog = new ManageIgnoredDialog(dialogVm)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();

        if (dialogVm.HasChanges)
        {
            await vm.RefreshAsync();
        }
    }

    /// <summary>
    /// Klick auf eine Bauvorschlag-Zeile: Detail-Dialog oeffnen.
    /// Bei "Figur anlegen" konsumiert IMinifigPersistenceService die FloatingParts
    /// per Reverse-Match und triggert ein DataChanged - die Liste hier
    /// refresht sich dann automatisch (die angelegte Minifig wird aus der
    /// Vorschlagsliste gefiltert weil sie jetzt getrackt ist).
    /// </summary>
    private async void Suggestion_MouseDown(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not BuildSuggestionItem item)
            return;

        var sp = App.Services;
        var cache = sp.GetRequiredService<IBlCacheRepository>();
        var ctxFactory = sp.GetRequiredService<IDbContextFactory<UserDataContext>>();
        var imageProvider = sp.GetRequiredService<IPartImageProvider>();
        var binService = sp.GetRequiredService<IStorageBinService>();
        var persistence = sp.GetRequiredService<IMinifigPersistenceService>();
        var notifications = sp.GetRequiredService<INotificationService>();
        var blInventory = sp.GetRequiredService<IBlInventoryService>();

        var dialogVm = new BuildSuggestionDetailViewModel(
            item.BricklinkId,
            item.Name,
            yearReleased: null, // BL-Item liefert YearReleased separat - wir holen es im Load
            imageUrl: item.ImageUrl,
            cache, ctxFactory, imageProvider, blInventory);

        // Subset-Liste + Bin-Liste asynchron laden bevor der Dialog gezeigt wird,
        // damit der User keine leere Maske sieht. Bei Fehler trotzdem oeffnen.
        try
        {
            await dialogVm.LoadAsync(binService);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BuildSuggestionDetail: Vorab-Laden fehlgeschlagen");
        }

        var dialog = new BuildSuggestionDetailDialog(dialogVm, persistence, notifications)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}
