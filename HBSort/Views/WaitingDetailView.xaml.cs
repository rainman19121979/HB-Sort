using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HBSort.Core.Database;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HBSort.Views;

/// <summary>
/// Wartende-Detail-Tab. ViewModel via DataContext gebunden.
///
/// UX X.19 Teil 2: Klick auf eine Karte oeffnet den
/// MinifigSummaryDialog (gleiche Optik/Logik wie der "Details"-Button
/// in der Lagerliste). Bild-Klicks gehen weiter ans ImageZoom-Behavior;
/// das Behavior setzt e.Handled=true, sodass der Click nicht zum Border
/// bubbled - kein Doppelfeuern.
/// </summary>
public partial class WaitingDetailView : UserControl
{
    public WaitingDetailView()
    {
        InitializeComponent();
    }

    private static T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    /// <summary>
    /// Click auf eine Karte: oeffnet den MinifigSummaryDialog fuer die
    /// gewaehlte wartende Figur. Tag traegt das WaitingFigureWithMissing-
    /// Item; daraus ziehen wir die MinifigId.
    /// </summary>
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not WaitingFigureWithMissing item)
            return;

        // Identische Service-Aufloesung wie in InventoryListView.Details_Click.
        // Wenn das hier zur dritten oder vierten Stelle wird, lohnt sich ein
        // gemeinsamer Helper - aktuell sind es nur zwei Aufrufstellen.
        // UX X.20 Teil 7: priceCalc + settings werden vom Summary-VM nicht mehr gebraucht.
        var ctxFactory = Service<IDbContextFactory<UserDataContext>>();
        var binService = Service<IStorageBinService>();
        var notif = Service<INotificationService>();
        var persistence = Service<IMinifigPersistenceService>();
        var imgProvider = Service<IPartImageProvider>();
        var catalog = Service<IBlCatalogService>();

        var vm = new MinifigSummaryViewModel(
            item.MinifigId, ctxFactory, binService,
            imgProvider, catalog, persistence);
        var dialog = new MinifigSummaryDialog(vm, notif, persistence)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}
