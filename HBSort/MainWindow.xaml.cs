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
    }

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
        // Leertaste = Scan ausloesen.
        if (e.Key == Key.Space)
        {
            if (IsTextInputFocused()) return;
            var scan = _viewModel.ScanViewModel.PerformScanCommand;
            if (scan.CanExecute(null))
            {
                scan.Execute(null);
                e.Handled = true;
            }
            return;
        }

        // UX X.29 (v0.1.16): Strg+Z = globales Undo. In TextBoxen NICHT
        // greifen damit die Standard-WPF-Undo-Logik im Text-Editor weiter
        // funktioniert.
        if (e.Key == Key.Z
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            if (IsTextInputFocused()) return;
            var undo = _viewModel.UndoLastCommand;
            if (undo.CanExecute(null))
            {
                undo.Execute(null);
                e.Handled = true;
            }
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

        // UX X.27 (v0.1.14): Tasks-Geist-Fix Stufe 2.
        // Trotz OnExit-Fallback (2s) bleibt manchmal eine HBSort.exe-Instanz
        // im Hintergrund haengen - vermutlich weil OnExit gar nicht erst
        // aufgerufen wird (z.B. wenn ein non-Background-Thread den Shutdown
        // blockiert, bevor Application.Current.OnExit greift).
        // Hier in Window_Closing zusaetzlich nach 1s killen. Settings sind
        // oben schon sauber gesichert, Camera ist gestoppt - ab hier ist
        // hartes Beenden sicher.
        Task.Run(async () =>
        {
            await Task.Delay(1000);
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            catch
            {
                // Kill-Berechtigung fehlt o.ae. - dann uebernimmt OnExit-Fallback (2s).
            }
        });
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

    /// <summary>UX X.29 (v0.1.16): Header-Tab "Verlauf" geklickt -> MainTabIndex=3.</summary>
    private void MainTabHistory_Click(object sender, RoutedEventArgs e)
        => _viewModel.SwitchToHistoryTabCommand.Execute(null);

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
