using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    /// <summary>
    /// UX X.15: Click auf den "Uebernehmen"-Button einer Top-3-Karte.
    /// Vorher hing der Click am ganzen Border und ueberlappte sich mit dem
    /// Image-Zoom; jetzt ist die Auswahl explizit.
    /// </summary>
    private void SelectCard_Click(object sender, RoutedEventArgs e)
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

    /// <summary>
    /// Splitter zwischen Col1 (Webcam) und Col2 (Detail). Persistiert das
    /// Verhaeltnis Col1 / Total in WindowState.SplitterColumnRatio.
    /// </summary>
    private void ColumnSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var settingsService = Service<ISettingsService>();
        var total = LeftCol.ActualWidth + MidCol.ActualWidth + RightCol.ActualWidth;
        if (total <= 0) return;

        settingsService.Current.WindowState.SplitterColumnRatio =
            LeftCol.ActualWidth / total;
        settingsService.Current.WindowState.SplitterColumnRatio2 =
            MidCol.ActualWidth / total;
        _ = settingsService.SaveAsync();
    }

    /// <summary>
    /// Splitter zwischen Col2 (Detail) und Col3 (neue Boxen). Persistiert
    /// beide Anteile damit die Wiederherstellung konsistent bleibt.
    /// </summary>
    private void ColumnSplitter2_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var settingsService = Service<ISettingsService>();
        var total = LeftCol.ActualWidth + MidCol.ActualWidth + RightCol.ActualWidth;
        if (total <= 0) return;

        settingsService.Current.WindowState.SplitterColumnRatio =
            LeftCol.ActualWidth / total;
        settingsService.Current.WindowState.SplitterColumnRatio2 =
            MidCol.ActualWidth / total;
        _ = settingsService.SaveAsync();
    }

    /// <summary>
    /// Stellt die gespeicherten Spalten-Verhaeltnisse wieder her. Drei Spalten:
    /// Col1, Col2 explizit, Col3 ergibt sich als Rest. Werte werden auf
    /// [0.1 .. 0.8] gekappt, damit eine Spalte nicht komplett verschwindet.
    /// Das Zeilen-Verhaeltnis (65/35) ist fix aus den XAML-Defaults.
    /// </summary>
    private void ApplySplitterRatios()
    {
        var ws = Service<ISettingsService>().Current.WindowState;
        var c1 = Math.Clamp(ws.SplitterColumnRatio, 0.1, 0.8);
        var c2 = Math.Clamp(ws.SplitterColumnRatio2, 0.1, 0.8);
        // Sicherstellen dass die Summe < 0.95 ist (sonst Col3 < 5%).
        if (c1 + c2 > 0.9) { c1 = c2 = 1.0 / 3.0; }
        var c3 = 1.0 - c1 - c2;

        LeftCol.Width = new GridLength(c1, GridUnitType.Star);
        MidCol.Width = new GridLength(c2, GridUnitType.Star);
        RightCol.Width = new GridLength(c3, GridUnitType.Star);
    }
}
