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
        Log.Information("[SPLITTER] Col1HorizontalSplitter_DragCompleted FIRED");
        DiagnoseDragCompleted(sender, columnNumber: 1, Col1TopRow, Col1BotRow);

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
    }

    /// <summary>Horizontaler Splitter in Spalte 2 (Detail / Tabs).</summary>
    private void Col2HorizontalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        Log.Information("[SPLITTER] Col2HorizontalSplitter_DragCompleted FIRED");
        DiagnoseDragCompleted(sender, columnNumber: 2, Col2TopRow, Col2BotRow);

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
    }

    /// <summary>Horizontaler Splitter in Spalte 3 (BuildSuggestions / Preise).</summary>
    private void Col3HorizontalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        Log.Information("[SPLITTER] Col3HorizontalSplitter_DragCompleted FIRED");
        DiagnoseDragCompleted(sender, columnNumber: 3, Col3TopRow, Col3BotRow);

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
    }

    /// <summary>
    /// Diagnose-Helfer fuer Hypothesen B + C. Wird in jedem
    /// Col[N]HorizontalSplitter_DragCompleted aufgerufen.
    ///
    /// Hypothese B: zeigt die x:Name-Referenz (top/bot) auf das gleiche
    /// RowDefinition-Objekt, das auch im Parent-Grid des Splitters steht?
    /// Falls nein: x:Name ist auf eine andere RowDefinition gemappt als
    /// der Splitter manipuliert.
    ///
    /// Hypothese C: ist die ActualHeight zum Zeitpunkt des DragCompleted-
    /// Events bereits aktualisiert? Wir lesen die Werte direkt UND erneut
    /// per Dispatcher.BeginInvoke mit drei verschiedenen Prioritaeten.
    /// Wenn ein spaeterer Read andere Werte liefert: Layout-Pass-Race.
    /// </summary>
    private void DiagnoseDragCompleted(object sender, int columnNumber,
        RowDefinition top, RowDefinition bot)
    {
        // Hypothese B: x:Name vs Parent-Grid-RowDefinitions
        Log.Information("[DIAG-B] Col{N} via x:Name TopActual={T} BotActual={B}",
            columnNumber, top.ActualHeight, bot.ActualHeight);

        if (sender is GridSplitter splitter && splitter.Parent is Grid subGrid)
        {
            Log.Information("[DIAG-B] Col{N} via Parent.Rows.Count={C}",
                columnNumber, subGrid.RowDefinitions.Count);
            if (subGrid.RowDefinitions.Count >= 3)
            {
                var parentTop = subGrid.RowDefinitions[0];
                var parentBot = subGrid.RowDefinitions[2];
                Log.Information("[DIAG-B] Col{N} Parent.Rows[0].ActualHeight={A0} Rows[2].ActualHeight={A2}",
                    columnNumber, parentTop.ActualHeight, parentBot.ActualHeight);
                Log.Information("[DIAG-B] Col{N} HashCheck topX={Tx} parentTop={Pt} -> SameRef={Same}",
                    columnNumber, top.GetHashCode(), parentTop.GetHashCode(),
                    ReferenceEquals(top, parentTop));
                Log.Information("[DIAG-B] Col{N} HashCheck botX={Bx} parentBot={Pb} -> SameRef={Same}",
                    columnNumber, bot.GetHashCode(), parentBot.GetHashCode(),
                    ReferenceEquals(bot, parentBot));
            }
        }
        else
        {
            Log.Information("[DIAG-B] Col{N} sender ist kein GridSplitter mit Grid-Parent (Sender={S})",
                columnNumber, sender?.GetType().Name ?? "null");
        }

        // Hypothese C: Layout-Pass-Race - lesen wir nochmal spaeter
        var topRef = top;
        var botRef = bot;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Log.Information("[DIAG-C] Col{N} via BeginInvoke Input: TopActual={T} BotActual={B}",
                columnNumber, topRef.ActualHeight, botRef.ActualHeight);
        }));
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            Log.Information("[DIAG-C] Col{N} via BeginInvoke Loaded: TopActual={T} BotActual={B}",
                columnNumber, topRef.ActualHeight, botRef.ActualHeight);
        }));
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Log.Information("[DIAG-C] Col{N} via BeginInvoke Background: TopActual={T} BotActual={B}",
                columnNumber, topRef.ActualHeight, botRef.ActualHeight);
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

        Log.Information("[SPLITTER] BEFORE Vertical LeftActual={L} MidActual={M} RightActual={R}",
            LeftCol.ActualWidth, MidCol.ActualWidth, RightCol.ActualWidth);

        LeftCol.Width = new GridLength(c1, GridUnitType.Star);
        MidCol.Width = new GridLength(c2, GridUnitType.Star);
        RightCol.Width = new GridLength(c3, GridUnitType.Star);

        // --- Horizontale Splitter pro Spalte ---
        Log.Information("[SPLITTER] BEFORE Col=1 TopActual={T} BotActual={B}",
            Col1TopRow.ActualHeight, Col1BotRow.ActualHeight);
        Log.Information("[SPLITTER] ApplyRatios Col=1 loaded={R}", ws.Column1HorizontalSplitterRatio);
        ApplyRowRatio(Col1TopRow, Col1BotRow, ws.Column1HorizontalSplitterRatio);
        Log.Information("[SPLITTER] AFTER Col=1 TopActual={T} BotActual={B}",
            Col1TopRow.ActualHeight, Col1BotRow.ActualHeight);

        Log.Information("[SPLITTER] BEFORE Col=2 TopActual={T} BotActual={B}",
            Col2TopRow.ActualHeight, Col2BotRow.ActualHeight);
        Log.Information("[SPLITTER] ApplyRatios Col=2 loaded={R}", ws.Column2HorizontalSplitterRatio);
        ApplyRowRatio(Col2TopRow, Col2BotRow, ws.Column2HorizontalSplitterRatio);
        Log.Information("[SPLITTER] AFTER Col=2 TopActual={T} BotActual={B}",
            Col2TopRow.ActualHeight, Col2BotRow.ActualHeight);

        Log.Information("[SPLITTER] BEFORE Col=3 TopActual={T} BotActual={B}",
            Col3TopRow.ActualHeight, Col3BotRow.ActualHeight);
        Log.Information("[SPLITTER] ApplyRatios Col=3 loaded={R}", ws.Column3HorizontalSplitterRatio);
        ApplyRowRatio(Col3TopRow, Col3BotRow, ws.Column3HorizontalSplitterRatio);
        Log.Information("[SPLITTER] AFTER Col=3 TopActual={T} BotActual={B}",
            Col3TopRow.ActualHeight, Col3BotRow.ActualHeight);

        // Hypothese A: zeigen die x:Name-Felder auf unterschiedliche
        // RowDefinition-Instanzen, oder hat das inkrementelle Build-System
        // die Generierung kaputtgemacht?
        Log.Information("[DIAG-A] Col1TopRow.GetHashCode={H1} Col2TopRow.GetHashCode={H2} Col3TopRow.GetHashCode={H3}",
            Col1TopRow?.GetHashCode() ?? -1,
            Col2TopRow?.GetHashCode() ?? -1,
            Col3TopRow?.GetHashCode() ?? -1);
        Log.Information("[DIAG-A] Col1BotRow.GetHashCode={H1} Col2BotRow.GetHashCode={H2} Col3BotRow.GetHashCode={H3}",
            Col1BotRow?.GetHashCode() ?? -1,
            Col2BotRow?.GetHashCode() ?? -1,
            Col3BotRow?.GetHashCode() ?? -1);
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
