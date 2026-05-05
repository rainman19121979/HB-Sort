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
/// Layout (UX X.19 Teil 3a refactor): Outer-Grid mit drei Inhalts-Spalten +
/// zwei vertikalen Splittern. Jede Spalte ist ein eigenes Subgrid mit
/// 65*/5/35*-RowDefinitions und einem eigenen horizontalen Splitter.
///
/// Persistierung (UX X.19 Teil 3b):
/// - Vertikale Splitter -> WindowState.SplitterColumnRatio + Ratio2
///   (waren schon vor X.19 da).
/// - Horizontale Splitter pro Spalte -> WindowState.Column1/2/3HorizontalSplitterRatio.
/// - Schreiben: bei jedem DragCompleted; SettingsService.SaveAsync ist async,
///   wir fire-and-forget mit "_ ="; passt fuer ein gelegentliches Drag-End.
/// - Lesen: in ApplySplitterRatios beim Loaded-Event mit Defensiv-Clamp
///   auf [0.05..0.95]; NaN/Out-of-Range fallen auf den Default 0.65 zurueck.
/// </summary>
public partial class SortingView : UserControl
{
    /// <summary>Default-Anteil der oberen Box pro Spalte. 65% = altes Verhaeltnis.</summary>
    private const double DefaultHorizontalRatio = 0.65;

    /// <summary>Min/Max-Clamp damit eine Box nicht ganz verschwindet.</summary>
    private const double MinRatio = 0.05;
    private const double MaxRatio = 0.95;

    public SortingView()
    {
        InitializeComponent();
        Loaded += SortingView_Loaded;
    }

    private MainViewModel? VM => DataContext as MainViewModel;
    private static T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    private void SortingView_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySplitterRatios();
    }

    /// <summary>Such-Fallback (in Phase 5+ implementiert).</summary>
    private void SearchFallback_Click(object sender, RoutedEventArgs e)
    {
        Service<INotificationService>().ShowInfo("Manuelle Suche kommt spaeter.");
    }

    /// <summary>UX X.15: Click auf den "Uebernehmen"-Button einer Top-3-Karte.</summary>
    private void SelectCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int rank && VM != null)
        {
            _ = VM.ScanViewModel.SelectCardAsync(rank - 1);
        }
    }

    // ====================================================================
    // GridSplitter Persistierung
    // ====================================================================

    /// <summary>
    /// Vertikaler Splitter zwischen Spalte 1 und 2. Persistiert beide
    /// Spalten-Verhaeltnisse, damit die Wiederherstellung konsistent ist.
    /// </summary>
    private void VerticalSplitter1_DragCompleted(object sender, DragCompletedEventArgs e)
        => SaveColumnRatios();

    /// <summary>Vertikaler Splitter zwischen Spalte 2 und 3.</summary>
    private void VerticalSplitter2_DragCompleted(object sender, DragCompletedEventArgs e)
        => SaveColumnRatios();

    /// <summary>Horizontaler Splitter in Spalte 1 (Webcam / Brickognize).</summary>
    private void Col1HorizontalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var ratio = ComputeRowRatio(Col1TopRow, Col1BotRow);
        if (ratio.HasValue)
        {
            var settings = Service<ISettingsService>();
            settings.Current.WindowState.Column1HorizontalSplitterRatio = ratio.Value;
            _ = settings.SaveAsync();
        }
    }

    /// <summary>Horizontaler Splitter in Spalte 2 (Detail / Tabs).</summary>
    private void Col2HorizontalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var ratio = ComputeRowRatio(Col2TopRow, Col2BotRow);
        if (ratio.HasValue)
        {
            var settings = Service<ISettingsService>();
            settings.Current.WindowState.Column2HorizontalSplitterRatio = ratio.Value;
            _ = settings.SaveAsync();
        }
    }

    /// <summary>Horizontaler Splitter in Spalte 3 (BuildSuggestions / Preise).</summary>
    private void Col3HorizontalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var ratio = ComputeRowRatio(Col3TopRow, Col3BotRow);
        if (ratio.HasValue)
        {
            var settings = Service<ISettingsService>();
            settings.Current.WindowState.Column3HorizontalSplitterRatio = ratio.Value;
            _ = settings.SaveAsync();
        }
    }

    /// <summary>
    /// Berechnet das Top/Bot-Verhaeltnis aus den Actual-Hoehen einer Subgrid-
    /// Row-Pair. Liefert null wenn die Werte noch nicht layouted sind.
    /// </summary>
    private static double? ComputeRowRatio(RowDefinition top, RowDefinition bot)
    {
        var sum = top.ActualHeight + bot.ActualHeight;
        if (sum <= 0) return null;
        var ratio = top.ActualHeight / sum;
        return Math.Clamp(ratio, MinRatio, MaxRatio);
    }

    /// <summary>
    /// Schreibt Spalten-Verhaeltnisse aus den ActualWidth-Werten in die Settings.
    /// Aufgerufen aus beiden vertikalen Splittern, weil das Verhaeltnis Col2/Col3
    /// von beiden Splittern beeinflusst wird.
    /// </summary>
    private void SaveColumnRatios()
    {
        var settings = Service<ISettingsService>();
        var total = LeftCol.ActualWidth + MidCol.ActualWidth + RightCol.ActualWidth;
        if (total <= 0) return;

        settings.Current.WindowState.SplitterColumnRatio = LeftCol.ActualWidth / total;
        settings.Current.WindowState.SplitterColumnRatio2 = MidCol.ActualWidth / total;
        _ = settings.SaveAsync();
    }

    /// <summary>
    /// Stellt alle gespeicherten Splitter-Verhaeltnisse wieder her.
    /// Defensiv: NaN, Werte ausserhalb [0.05..0.95] und unsinnige Spalten-
    /// Summen fallen auf Default zurueck.
    /// </summary>
    private void ApplySplitterRatios()
    {
        var ws = Service<ISettingsService>().Current.WindowState;

        // --- Vertikale Splitter (Spalten-Breiten) ---
        var c1 = ClampOrDefault(ws.SplitterColumnRatio, 1.0 / 3.0);
        var c2 = ClampOrDefault(ws.SplitterColumnRatio2, 1.0 / 3.0);
        if (c1 + c2 > 0.9) { c1 = c2 = 1.0 / 3.0; }
        var c3 = 1.0 - c1 - c2;

        LeftCol.Width = new GridLength(c1, GridUnitType.Star);
        MidCol.Width = new GridLength(c2, GridUnitType.Star);
        RightCol.Width = new GridLength(c3, GridUnitType.Star);

        // --- Horizontale Splitter pro Spalte ---
        ApplyRowRatio(Col1TopRow, Col1BotRow, ws.Column1HorizontalSplitterRatio);
        ApplyRowRatio(Col2TopRow, Col2BotRow, ws.Column2HorizontalSplitterRatio);
        ApplyRowRatio(Col3TopRow, Col3BotRow, ws.Column3HorizontalSplitterRatio);
    }

    /// <summary>
    /// Wendet ein Top/Bot-Verhaeltnis auf zwei RowDefinitions an. Bei
    /// ungueltigen Werten (NaN, &lt;0.05, &gt;0.95) Default 0.65.
    /// </summary>
    private static void ApplyRowRatio(RowDefinition top, RowDefinition bot, double ratio)
    {
        var topRatio = ClampOrDefault(ratio, DefaultHorizontalRatio);
        var botRatio = 1.0 - topRatio;
        top.Height = new GridLength(topRatio, GridUnitType.Star);
        bot.Height = new GridLength(botRatio, GridUnitType.Star);
    }

    /// <summary>NaN- und Range-Schutz fuer geladene Splitter-Werte.</summary>
    private static double ClampOrDefault(double value, double defaultValue)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return defaultValue;
        if (value < MinRatio || value > MaxRatio) return defaultValue;
        return value;
    }
}
