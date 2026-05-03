using System.Windows;
using System.Windows.Controls;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HBSort.Views;

/// <summary>
/// PROMPT 11: Lagerfach-Uebersicht als eigenstaendiges UserControl.
/// Bisheriger Inhalt + Click-Handler aus SortingView extrahiert; jetzt
/// als Tab im variablen Feld unten rechts eingebunden.
///
/// DataContext = WaitingMinifigsViewModel (vom umschliessenden TabItem gesetzt).
/// </summary>
public partial class BinOverviewView : UserControl
{
    public BinOverviewView()
    {
        InitializeComponent();
    }

    private WaitingMinifigsViewModel? VM => DataContext as WaitingMinifigsViewModel;
    private static T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    private void BinFilterOccupied_Click(object sender, RoutedEventArgs e)
        => VM?.SetFilter(BinOverviewFilter.OccupiedOnly);

    private void BinFilterAll_Click(object sender, RoutedEventArgs e)
        => VM?.SetFilter(BinOverviewFilter.All);

    private void BinHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is BinOverviewItemViewModel item)
            item.Toggle();
    }

    private void BinShowDetailFromOverview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BinOverviewItemViewModel item) return;

        var binService = Service<IStorageBinService>();
        var catalog = Service<IBlCatalogService>();
        var imgProvider = Service<IPartImageProvider>();
        var vm = new BinDetailViewModel(item.Id, binService, catalog, imgProvider);
        var dialog = new BinDetailDialog(vm) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    private void WaitingMinifig_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not WaitingMinifigViewModel row) return;

        var ctxFactory = Service<Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Database.UserDataContext>>();
        var binService = Service<IStorageBinService>();
        var notif = Service<INotificationService>();
        var persistence = Service<IMinifigPersistenceService>();
        var imgProvider = Service<IPartImageProvider>();
        var catalog = Service<IBlCatalogService>();
        var priceCalc = Service<IPriceCalculationService>();
        var settings = Service<ISettingsService>();

        var vm = new MinifigSummaryViewModel(
            row.Id, ctxFactory, binService, imgProvider, catalog, priceCalc, settings);
        var dialog = new MinifigSummaryDialog(vm, notif, persistence) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }
}
