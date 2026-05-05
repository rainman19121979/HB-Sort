using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

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
        Log.Information("[SPLITTER] SortingView_Loaded fired - scheduling ApplyRatios via Dispatcher");

        // UX-Iteration X.21 Teil 1 Fix (2026-05-05): WPF muss zuerst seinen
        // Default-Layout-Pass machen, sonst werden unsere RowDefinitions[i].
        // Height-Setzungen ueberschrieben (Praxis-Befund: ApplyRatios direkt
        // im Loaded fuehrte dazu dass WPF danach mit den XAML-Default-Star-
        // Werten 65*/35* re-layoutete und alle drei Spalten exakt das gleiche
        // 0.6503-Verhaeltnis bekamen).
        // DispatcherPriority.Loaded laesst alle hoeher-priorisierten Layout-/
        // Render-Operationen vorher laufen, dann wirken unsere Star-Werte
        // ohne ueberschrieben zu werden.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            Log.Information("[SPLITTER] ApplyRatios via Dispatcher invoked");
            ApplySplitterRatios();
        }));
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

    // ====================================================================
    // UX-Iteration X.22 Fix (2026-05-05): WPF GridSplitter feuert
    // DragCompleted BEVOR der Layout-Pass die ActualHeight/ActualWidth-
    // Werte aktualisiert hat. Praxis-Befund 2026-05-05 20:08:56:
    //   ComputeRowRatio direkt: TopActual=584 BotActual=314 (Default!)
    //   Via Dispatcher 34ms spaeter: TopActual=400 BotActual=498 (echt)
    // Hypothese C aus den Diagnose-Logs eindeutig bestaetigt.
    //
    // Loesung: Compute + Save in jedem DragCompleted-Handler via
    // Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...) verzoegern,
    // damit WPF zuerst sein Layout-Update abschliesst.
    // ====================================================================

    /// <summary>
    /// Vertikaler Splitter zwischen Spalte 1 und 2. Persistiert beide
    /// Spalten-Verhaeltnisse, damit die Wiederherstellung konsistent ist.
    /// Save via Dispatcher (siehe X.22-Kommentar oben).
    /// </summary>
    private void VerticalSplitter1_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        Log.Information("[SPLITTER] VerticalSplitter1_DragCompleted FIRED - scheduling Save via Dispatcher");
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SaveColumnRatios));
    }

    /// <summary>Vertikaler Splitter zwischen Spalte 2 und 3.</summary>
    private void VerticalSplitter2_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        Log.Information("[SPLITTER] VerticalSplitter2_DragCompleted FIRED - scheduling Save via Dispatcher");
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SaveColumnRatios));
    }

    /// <summary>Horizontaler Splitter in Spalte 1 (Webcam / Brickognize).</summary>
    private void Col1HorizontalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        Log.Information("[SPLITTER] Col1HorizontalSplitter_DragCompleted FIRED - scheduling Save via Dispatcher");
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var ratio = ComputeRowRatio(Col1TopRow, Col1BotRow, columnNumber: 1);
            if (ratio.HasValue)
            {
                var settings = Service<ISettingsService>();
                settings.Current.WindowState.Column1HorizontalSplitterRatio = ratio.Value;
                Log.Information("[SPLITTER] SettingsSaveAsync invoked col=1 ratio={Ratio}", ratio.Value);
                _ = settings.SaveAsync();
            }
            else
            {
                Log.Information("[SPLITTER] Col1 ratio=null -> kein Save");
            }
        }));
    }

    /// <summary>Horizontaler Splitter in Spalte 2 (Detail / Tabs).</summary>
    private void Col2HorizontalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        Log.Information("[SPLITTER] Col2HorizontalSplitter_DragCompleted FIRED - scheduling Save via Dispatcher");
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var ratio = ComputeRowRatio(Col2TopRow, Col2BotRow, columnNumber: 2);
            if (ratio.HasValue)
            {
                var settings = Service<ISettingsService>();
                settings.Current.WindowState.Column2HorizontalSplitterRatio = ratio.Value;
                Log.Information("[SPLITTER] SettingsSaveAsync invoked col=2 ratio={Ratio}", ratio.Value);
                _ = settings.SaveAsync();
            }
            else
            {
                Log.Information("[SPLITTER] Col2 ratio=null -> kein Save");
            }
        }));
    }

    /// <summary>Horizontaler Splitter in Spalte 3 (BuildSuggestions / Preise).</summary>
    private void Col3HorizontalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        Log.Information("[SPLITTER] Col3HorizontalSplitter_DragCompleted FIRED - scheduling Save via Dispatcher");
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var ratio = ComputeRowRatio(Col3TopRow, Col3BotRow, columnNumber: 3);
            if (ratio.HasValue)
            {
                var settings = Service<ISettingsService>();
                settings.Current.WindowState.Column3HorizontalSplitterRatio = ratio.Value;
                Log.Information("[SPLITTER] SettingsSaveAsync invoked col=3 ratio={Ratio}", ratio.Value);
                _ = settings.SaveAsync();
            }
            else
            {
                Log.Information("[SPLITTER] Col3 ratio=null -> kein Save");
            }
        }));
    }

    /// <summary>
    /// Berechnet das Top/Bot-Verhaeltnis aus den Actual-Hoehen einer Subgrid-
    /// Row-Pair. Liefert null wenn die Werte noch nicht layouted sind.
    /// </summary>
    private static double? ComputeRowRatio(RowDefinition top, RowDefinition bot, int columnNumber)
    {
        var sum = top.ActualHeight + bot.ActualHeight;
        Log.Information("[SPLITTER] ComputeRowRatio Col={N} TopActual={Top} BotActual={Bot} Sum={Sum}",
            columnNumber, top.ActualHeight, bot.ActualHeight, sum);
        if (sum <= 0)
        {
            Log.Information("[SPLITTER] ComputeRowRatio Col={N} Sum<=0 -> returns null", columnNumber);
            return null;
        }
        var ratio = top.ActualHeight / sum;
        var clamped = Math.Clamp(ratio, MinRatio, MaxRatio);
        Log.Information("[SPLITTER] ComputeRowRatio Col={N} RawRatio={Raw} Clamped={Clamp}",
            columnNumber, ratio, clamped);
        return clamped;
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
        Log.Information("[SPLITTER] ApplySplitterRatios start: vert1={V1} vert2={V2} h1={H1} h2={H2} h3={H3}",
            ws.SplitterColumnRatio, ws.SplitterColumnRatio2,
            ws.Column1HorizontalSplitterRatio,
            ws.Column2HorizontalSplitterRatio,
            ws.Column3HorizontalSplitterRatio);

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
