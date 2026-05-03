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

        var dialogVm = new BuildSuggestionDetailViewModel(
            item.BricklinkId,
            item.Name,
            yearReleased: null, // BL-Item liefert YearReleased separat - wir holen es im Load
            imageUrl: item.ImageUrl,
            cache, ctxFactory, imageProvider);

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
