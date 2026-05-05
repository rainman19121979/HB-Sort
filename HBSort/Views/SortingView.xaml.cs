using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HBSort.Views;

/// <summary>
/// UserControl fuer den Sortier-Tab im Hauptfenster.
///
/// Layout (UX X.19 Teil 3a): Outer-Grid mit drei Inhalts-Spalten + zwei
/// vertikalen Splittern. Jede der drei Spalten ist ein eigenes Subgrid mit
/// 65*/5/35*-RowDefinitions und einem eigenen horizontalen Splitter -
/// damit kann der User die Hoehen pro Spalte unabhaengig verstellen.
///
/// Persistierung: alle 5 Splitter-Verhaeltnisse werden in
/// AppSettings.WindowState gespeichert (UX X.19 Teil 3b, naechster Commit).
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

    /// <summary>Such-Fallback (in Phase 5+ implementiert).</summary>
    private void SearchFallback_Click(object sender, RoutedEventArgs e)
    {
        Service<INotificationService>().ShowInfo("Manuelle Suche kommt spaeter.");
    }

    /// <summary>
    /// UX X.15: Click auf den "Uebernehmen"-Button einer Top-3-Karte.
    /// </summary>
    private void SelectCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int rank && VM != null)
        {
            _ = VM.ScanViewModel.SelectCardAsync(rank - 1);
        }
    }

    // ====================================================================
    // GridSplitter Persistierung
    // (Implementierung kommt in UX X.19 Teil 3b - hier nur die Handler-
    // Stubs, damit die XAML-Refs nicht ins Leere zeigen.)
    // ====================================================================

    /// <summary>Vertikaler Splitter zwischen Spalte 1 und 2.</summary>
    private void VerticalSplitter1_DragCompleted(object sender, DragCompletedEventArgs e)
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

    /// <summary>Vertikaler Splitter zwischen Spalte 2 und 3.</summary>
    private void VerticalSplitter2_DragCompleted(object sender, DragCompletedEventArgs e)
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

    /// <summary>Horizontaler Splitter in Spalte 1 (zwischen Webcam und Brickognize).</summary>
    private void Col1HorizontalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        // Persistierung folgt in Teil 3b.
    }

    /// <summary>Horizontaler Splitter in Spalte 2 (zwischen Detail und Tabs).</summary>
    private void Col2HorizontalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        // Persistierung folgt in Teil 3b.
    }

    /// <summary>Horizontaler Splitter in Spalte 3 (zwischen BuildSuggestions und Preise).</summary>
    private void Col3HorizontalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        // Persistierung folgt in Teil 3b.
    }

    /// <summary>
    /// Stellt die gespeicherten Spalten-Verhaeltnisse wieder her. Drei Spalten:
    /// Col1, Col2 explizit, Col3 ergibt sich als Rest. Werte werden auf
    /// [0.1 .. 0.8] gekappt, damit eine Spalte nicht komplett verschwindet.
    /// Horizontale Splitter-Verhaeltnisse werden in Teil 3b ergaenzt.
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
