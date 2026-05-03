using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HBSort.Views;

/// <summary>
/// UserControl fuer den Sortier-Tab im Hauptfenster (Phase X).
/// Beinhaltet das 2x2-Quadranten-Layout (Webcam / Detail / Brickognize / Lagerfaecher).
///
/// DataContext wird vom Parent (TabItem) geerbt = MainViewModel.
///
/// Click-Handler operieren auf den ViewModels via DataContext und auf dem
/// parent Window via Window.GetWindow(this) – damit Dialoge ein Owner haben.
/// </summary>
public partial class SortingView : UserControl
{
    public SortingView()
    {
        InitializeComponent();
        Loaded += SortingView_Loaded;
    }

    private MainViewModel? VM => DataContext as MainViewModel;
    private static T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    private void SortingView_Loaded(object sender, RoutedEventArgs e)
    {
        // Splitter-Verhaeltnisse anwenden, sobald die UserControl im visuellen Baum ist.
        ApplySplitterRatios();
    }

    // ====================================================================
    // Modus-Buttons
    // ====================================================================

    private void ModeAuto_Click(object sender, RoutedEventArgs e) => VM?.ScanViewModel.SetMode("auto");
    private void ModeMinifig_Click(object sender, RoutedEventArgs e) => VM?.ScanViewModel.SetMode("minifig");
    private void ModePart_Click(object sender, RoutedEventArgs e) => VM?.ScanViewModel.SetMode("part");

    /// <summary>Such-Fallback (in Phase 5+ implementiert).</summary>
    private void SearchFallback_Click(object sender, RoutedEventArgs e)
    {
        Service<INotificationService>().ShowInfo("Manuelle Suche kommt spaeter.");
    }

    /// <summary>Click auf eine Top-3-Karte: triggert SelectCardAsync.</summary>
    private void ResultCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int rank && VM != null)
        {
            _ = VM.ScanViewModel.SelectCardAsync(rank - 1);
        }
    }

    // ====================================================================
    // Lagerfach-Uebersicht (R2,C2)
    // ====================================================================

    private void BinFilterOccupied_Click(object sender, RoutedEventArgs e)
        => VM?.WaitingMinifigs.SetFilter(BinOverviewFilter.OccupiedOnly);

    private void BinFilterAll_Click(object sender, RoutedEventArgs e)
        => VM?.WaitingMinifigs.SetFilter(BinOverviewFilter.All);

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

        var vm = new MinifigSummaryViewModel(row.Id, ctxFactory, binService, imgProvider, catalog);
        var dialog = new MinifigSummaryDialog(vm, notif, persistence) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Phase 7: nach jedem Klick auf eine Komplett-Checkbox den globalen
    /// Counter (SelectedCompleteCount) im VM neu berechnen.
    /// </summary>
    private void CompleteSelect_Click(object sender, RoutedEventArgs e)
    {
        VM?.WaitingMinifigs.RecalculateSelection();
    }

    /// <summary>
    /// Phase 7: oeffnet den BSX-Export-Dialog mit den aktuell selektierten
    /// kompletten Figuren.
    /// </summary>
    private void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        var ids = VM.WaitingMinifigs.SelectedCompletes.Select(c => c.Id).ToList();
        if (ids.Count == 0) return;

        var dialog = Service<BsxExportDialog>();
        dialog.Initialize(ids);
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
    }

    // ====================================================================
    // GridSplitter Persistierung
    // ====================================================================

    private void ColumnSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var settingsService = Service<ISettingsService>();
        var leftWidth = LeftCol.ActualWidth;
        var rightWidth = RightCol.ActualWidth;
        var total = leftWidth + rightWidth;
        if (total > 0)
        {
            settingsService.Current.WindowState.SplitterColumnRatio = leftWidth / total;
            _ = settingsService.SaveAsync();
        }
    }

    /// <summary>
    /// Stellt das gespeicherte Spalten-Verhaeltnis wieder her. Das Zeilen-
    /// Verhaeltnis ist fix (65/35 aus XAML-Defaults) und wird nicht mehr
    /// vom User editierbar gemacht.
    /// </summary>
    private void ApplySplitterRatios()
    {
        var ws = Service<ISettingsService>().Current.WindowState;
        var col = Math.Clamp(ws.SplitterColumnRatio, 0.1, 0.9);

        LeftCol.Width = new GridLength(col, GridUnitType.Star);
        RightCol.Width = new GridLength(1.0 - col, GridUnitType.Star);
        // TopRow / BottomRow bleiben auf den 65*/35*-Defaults aus XAML.
    }
}
