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

    // (Kamera-/Modus-Auswahl-Handler entfernt: Kamera nur noch in Settings,
    //  ScanMode fest auf Auto. SwitchCameraAsync-Command bleibt im VM fuer
    //  die Settings-UI; IsModeAuto/Minifig/Part-Properties auch.)

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

    // Lagerfach-Click-Handler sind nach BinOverviewView.xaml.cs umgezogen:
    // der R2,C2-Bereich ist ein TabControl, jeder Tab kapselt sein eigenes
    // UserControl mit eigenen Handlern.

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
