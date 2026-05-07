using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort;

/// <summary>
/// Code-Behind fuer das Hauptfenster.
/// Phase X: Hauptbereich auf TabControl umgestellt; das 2x2-Quadranten-Layout
/// liegt jetzt in <see cref="Views.SortingView"/>. Diese Klasse haelt nur noch
/// Window-Level-Logik (Title-Bar, Tray, KeyBindings, WindowState).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notifications;

    public MainWindow(MainViewModel viewModel, ISettingsService settingsService, INotificationService notifications)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settingsService = settingsService;
        _notifications = notifications;

        DataContext = _viewModel;

        // UX X.20 Teil 6: Strg+, oeffnet Einstellungen. Das VM feuert das Event,
        // weil es nicht selbst Window-Instanzen erstellen darf.
        _viewModel.OpenSettingsRequested += (_, _) => OpenSettings_Click(this, new RoutedEventArgs());

        // UX X.21 Teil 3: Strg+Q beendet die App. Analog zum Settings-Event -
        // das VM darf nicht selbst Application.Shutdown() rufen.
        _viewModel.ExitAppRequested += (_, _) => ExitApp_Click(this, new RoutedEventArgs());

        // UX X.23 Diagnose v0.1.6: zeigt ob das Window die PropertyChanged-Events
        // vom MainViewModel ueberhaupt sieht. Wenn das Window's Sicht auf das
        // Event fehlt, ist [ObservableProperty]-Source-Generator nicht eingewebt
        // oder das DataContext-Binding zeigt auf ein anderes Objekt.
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HasUpdateAvailable)
                || e.PropertyName == nameof(MainViewModel.AvailableUpdateVersion))
            {
                Log.Information("[DIAG-UI] WINDOW received PropertyChanged: {PropName} (Sender={SenderType})",
                    e.PropertyName,
                    s?.GetType().Name ?? "null");
            }

            // UX X.24 Fix-Versuch: bei HasUpdateAvailable-Aenderung explizit
            // Layout-Pass anstossen. WPF macht das normalerweise selbst, aber
            // bei Visibility=Collapsed -> Visible im StackPanel kann es zu
            // Layout-Caching kommen (Slot wurde initial nicht reserviert).
            // Praxis-Befund v0.1.6: Button war im Visual-Tree, blitzte beim
            // Beenden kurz auf - klassisches Layout-Race-Symptom.
            if (e.PropertyName == nameof(MainViewModel.HasUpdateAvailable))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InvalidateVisual();
                    UpdateLayout();
                    Log.Information("[DIAG-UI] Window UpdateLayout() nach HasUpdateAvailable-Aenderung getriggert");
                    // v0.1.11: Direkt nach UpdateLayout den Badge-State loggen.
                    // Wenn Opacity=0 hier obwohl HasUpdateAvailable=true: DataTrigger
                    // greift nicht (Style.BasedOn-Konflikt mit ModernWpf?).
                    // Wenn Opacity=1 hier aber visuell nichts da: Layout/Z-Order-Problem.
                    LogUpdateBadgeState("PostUpdateLayout");
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowState();

        // FocusTrap initial fokussieren, damit kein Control (z.B. ein Button)
        // beim Start den Fokus haelt. Sonst koennten Buttons spaeter via Leertaste
        // wieder aktiviert werden.
        FocusTrap.Focus();

        await _viewModel.ScanViewModel.InitializeCameraAsync();

        // Brickognize-Health-Check im Hintergrund (Fire-and-forget).
        _ = _viewModel.RunBrickognizeHealthCheckAsync();

        _viewModel.StatusText = "Bereit";

        // v0.1.11 Diagnose: FindName statt FindAllButtons-Filter (alter Filter
        // matcht nie weil [RelayCommand] AsyncRelayCommand erzeugt, nicht
        // ApplyUpdateCommand). Pro Snapshot loggen wir den vollen visuellen
        // Zustand des Buttons + alle Parent-Frameworkelement-Properties.
        // Drei Snapshot-Zeitpunkte:
        //   T+5s: Initial-Layout fertig, HasUpdateAvailable noch undefined oder false
        //   T+15s: nach Background-Update-Check + UpdateLayout-Trigger
        //   T+30s: ggf. nach 2. Layout-Pass
        var diagTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        var snapshotCount = 0;
        diagTimer.Tick += (_, _) =>
        {
            snapshotCount++;
            LogUpdateBadgeState($"T+{snapshotCount * 5}s");
            if (snapshotCount >= 6) diagTimer.Stop(); // bis 30s
        };
        diagTimer.Start();
    }

    /// <summary>
    /// v0.1.11 Diagnose: Update-Badge-Button per FindName direkt holen
    /// (deterministisch, kein Command-Type-Filter mehr) und alle relevanten
    /// Properties loggen. Plus Parent-Chain bis zum Window-Root.
    /// </summary>
    private void LogUpdateBadgeState(string label)
    {
        try
        {
            var btn = FindName("UpdateBadgeButton") as System.Windows.Controls.Button;
            if (btn == null)
            {
                Log.Information("[DIAG-UI] {Label}: UpdateBadgeButton via FindName=NULL", label);
                return;
            }

            Log.Information("[DIAG-UI] {Label} UpdateBadgeButton: Vis={Vis} IsVisible={IsVis} Opacity={Op} IsHitTestVisible={Hit} ActualW={AW} ActualH={AH} DC={DC}",
                label,
                btn.Visibility,
                btn.IsVisible,
                btn.Opacity,
                btn.IsHitTestVisible,
                btn.ActualWidth,
                btn.ActualHeight,
                btn.DataContext?.GetType().Name ?? "null");

            // Pro Parent FrameworkElement: alle relevanten Layout-Properties.
            System.Windows.DependencyObject? current = btn;
            int depth = 0;
            while (current != null && depth < 10)
            {
                if (current is System.Windows.FrameworkElement fe)
                {
                    Log.Information("[DIAG-UI] {Label} Parent[{Depth}]: {Type} Vis={Vis} IsVis={IsVis} Opacity={Op} W={W} H={H} ActW={AW} ActH={AH}",
                        label, depth, current.GetType().Name,
                        fe.Visibility, fe.IsVisible, fe.Opacity,
                        fe.Width, fe.Height,
                        fe.ActualWidth, fe.ActualHeight);
                }
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                depth++;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DIAG-UI] LogUpdateBadgeState ({Label}) fehlgeschlagen", label);
        }
    }

    // v0.1.11: alter FindAllButtons-Helper entfernt - wurde nur fuer den
    // kaputten Filter b.Command.GetType().Name.Contains("ApplyUpdate")
    // genutzt. RelayCommand-Generator erzeugt AsyncRelayCommand, nicht
    // ApplyUpdateCommand - der Filter hat nie gematcht. Ersetzt durch
    // FindName("UpdateBadgeButton") in LogUpdateBadgeState.

    /// <summary>
    /// Setzt den Tastatur-Fokus zurueck auf das Hauptfenster (FocusTrap),
    /// damit Buttons nicht den Fokus behalten und unsere Window.InputBindings
    /// (Leertaste = Scan etc.) zuverlaessig feuern.
    /// </summary>
    private void RestoreFocusToWindow()
    {
        FocusTrap.Focus();
        Keyboard.Focus(FocusTrap);
    }

    /// <summary>
    /// UX X.20 Teil 6a: Leertaste-Bug. Wenn der User auf eine Karte (Border),
    /// einen RadioButton oder einen TabItem-Header klickt, behaelt das
    /// Element den Tastatur-Fokus. Die Window.InputBinding fuer Space greift
    /// dann NICHT, weil Border/RadioButton/TabItem die Leertaste als eigene
    /// Aktion behandeln (Toggle / IsChecked / SelectedItem-Cycle).
    ///
    /// Der PreviewKeyDown-Handler greift bevor die Standard-WPF-Logik die
    /// Leertaste an Border etc. weiterleitet. Wir feuern PerformScan und
    /// markieren das Event als handled - aber NUR wenn das fokussierte
    /// Element keine echte Text-Eingabe ist (TextBox / PasswordBox /
    /// editierbare ComboBox), da der User dort sonst kein Leerzeichen mehr
    /// eingeben koennte.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        if (IsTextInputFocused()) return;

        var cmd = _viewModel.ScanViewModel.PerformScanCommand;
        if (cmd.CanExecute(null))
        {
            cmd.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// True wenn das aktuell tastatur-fokussierte Element echte Texteingabe
    /// braucht. Wir sperren in dem Fall den Leertaste-Shortcut, damit der
    /// User dort weiter Leerzeichen tippen kann.
    /// </summary>
    private static bool IsTextInputFocused()
    {
        var focused = Keyboard.FocusedElement;
        if (focused is System.Windows.Controls.TextBox) return true;
        if (focused is System.Windows.Controls.PasswordBox) return true;
        if (focused is System.Windows.Controls.ComboBox cb && cb.IsEditable) return true;
        return false;
    }

    /// <summary>
    /// UX X.24: Single-Point-of-Truth fuer App-Beenden. Alle drei Pfade
    /// (X-Klick, Header-Beenden-Button, Strg+Q) landen hier weil
    /// ExitApp_Click jetzt nur noch this.Close() aufruft.
    ///
    /// Synchroner Cleanup: SaveWindowState (POCO), SaveAsync via
    /// Task.Run().Wait() (vermeidet UI-Thread-Capture-Deadlock), StopCamera.
    /// Kein e.Cancel - Window darf zu, OnExit laeuft danach automatisch.
    /// </summary>
    private void Window_Closing(object sender, CancelEventArgs e)
    {
        Log.Information("Window_Closing - App wird beendet");
        SaveWindowState();
        // Task.Run-Wrap: SettingsService.SaveAsync hat ein File.WriteAllTextAsync
        // dessen await sonst den UI-SyncContext capturen wuerde. Durch
        // Task.Run laeuft die Continuation auf einem ThreadPool-Thread, also
        // kein Deadlock beim .Wait() vom UI-Thread.
        try { Task.Run(() => _settingsService.SaveAsync()).Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Log.Warning(ex, "Window_Closing: SaveAsync fehlgeschlagen"); }
        _viewModel.ScanViewModel.StopCamera();
    }

    /// <summary>
    /// UX-Iteration X.21 Teil 3 / X.24: Beenden-Button im Header / Strg+Q.
    /// Triggert Close() das wiederum Window_Closing aufruft - dort liegt
    /// der echte Cleanup-Code.
    /// </summary>
    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("App wird ueber Header-Button/Strg+Q beendet");
        Close();
    }

    /// <summary>Header-Tab "Sortieren" geklickt -> MainTabIndex=0.</summary>
    private void MainTabSorting_Click(object sender, RoutedEventArgs e)
        => _viewModel.MainTabIndex = 0;

    /// <summary>Header-Tab "Lagerliste" geklickt -> MainTabIndex=1.</summary>
    private void MainTabInventory_Click(object sender, RoutedEventArgs e)
        => _viewModel.MainTabIndex = 1;

    /// <summary>Header-Tab "Hilfe" geklickt -> MainTabIndex=2.</summary>
    private void MainTabHelp_Click(object sender, RoutedEventArgs e)
        => _viewModel.MainTabIndex = 2;

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var settingsVm = App.Services.GetRequiredService<SettingsViewModel>();
        var settingsWindow = new Views.SettingsWindow(settingsVm) { Owner = this };

        try
        {
            if (settingsWindow.ShowDialog() == true)
            {
                _ = _viewModel.ScanViewModel.StartCameraAsync(_settingsService.Current.SelectedCameraIndex);
            }
        }
        finally
        {
            RestoreFocusToWindow();
        }
    }

    private void SaveWindowState()
    {
        var ws = _settingsService.Current.WindowState;
        ws.IsMaximized = WindowState == System.Windows.WindowState.Maximized;

        if (WindowState == System.Windows.WindowState.Normal)
        {
            ws.Width = Width;
            ws.Height = Height;
            ws.X = Left;
            ws.Y = Top;
        }
    }

    private void RestoreWindowState()
    {
        var ws = _settingsService.Current.WindowState;

        if (ws.IsMaximized)
        {
            WindowState = System.Windows.WindowState.Maximized;
        }
        else
        {
            WindowState = System.Windows.WindowState.Normal;
            Width = ws.Width;
            Height = ws.Height;
            Left = ws.X;
            Top = ws.Y;
        }
    }
}
