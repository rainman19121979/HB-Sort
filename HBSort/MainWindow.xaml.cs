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

        // UX X.23 Diagnose v0.1.6: nach 5s das Visual-Tree absuchen ob der
        // Update-Badge-Button ueberhaupt instanziiert wurde. Wenn ja: dessen
        // Visibility/IsVisible/ActualWidth/Height/DataContext loggen.
        var diagTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        diagTimer.Tick += (_, _) =>
        {
            diagTimer.Stop();
            try
            {
                var allButtons = FindAllButtons(this);
                var updateButton = allButtons.FirstOrDefault(b =>
                    b.Command != null
                    && b.Command.GetType().Name.Contains("ApplyUpdate", StringComparison.OrdinalIgnoreCase));

                Log.Information("[DIAG-UI] Visual-Tree nach 5s: TotalButtons={Count}, UpdateButton found={Found}",
                    allButtons.Count,
                    updateButton != null);

                if (updateButton != null)
                {
                    Log.Information("[DIAG-UI] UpdateButton: Visibility={Vis} IsVisible={IsVis} ActualWidth={W} ActualHeight={H} DataContext={DC}",
                        updateButton.Visibility,
                        updateButton.IsVisible,
                        updateButton.ActualWidth,
                        updateButton.ActualHeight,
                        updateButton.DataContext?.GetType().Name ?? "null");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DIAG-UI] Visual-Tree-Inspektion fehlgeschlagen");
            }
        };
        diagTimer.Start();
    }

    /// <summary>
    /// UX X.23 Diagnose-Helfer: rekursiv alle Buttons im Visual-Tree finden.
    /// Try-Catch falls ein VisualTreeHelper-Knoten zickt.
    /// </summary>
    private static List<System.Windows.Controls.Button> FindAllButtons(System.Windows.DependencyObject parent)
    {
        var result = new List<System.Windows.Controls.Button>();
        try
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Controls.Button btn) result.Add(btn);
                result.AddRange(FindAllButtons(child));
            }
        }
        catch
        {
            // Defensiv: bestimmte Visual-Tree-Knoten lassen sich nicht traversieren
            // (z.B. wenn ein Custom-Control im Konstruktor crasht). Diagnose darf
            // nicht zum App-Stop fuehren.
        }
        return result;
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

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        Log.Information("[SPLITTER] Window_Closing called (will set e.Cancel=true and Hide)");
        SaveWindowState();
        e.Cancel = true;
        Hide();
        Log.Information("Fenster ins Tray minimiert");
    }

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) => ShowAndActivate();

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowAndActivate();

    private async void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("App wird ueber Tray-Menue beendet");
        await ExitApplicationAsync();
    }

    /// <summary>
    /// UX-Iteration X.21 Teil 3: Beenden-Button im Header.
    /// Gleicher Beendigungs-Pfad wie Tray-Exit (Settings speichern, Kamera
    /// stoppen, Tray-Icon disposen, Shutdown).
    /// </summary>
    private async void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("App wird ueber Header-Button/Strg+Q beendet");
        await ExitApplicationAsync();
    }

    /// <summary>
    /// Gemeinsamer Beendigungs-Pfad: persistiert WindowState + Settings,
    /// stoppt die Kamera, raeumt das Tray-Icon ab und faehrt die App
    /// regulaer runter (loest dann App.OnExit aus).
    /// </summary>
    private async Task ExitApplicationAsync()
    {
        SaveWindowState();
        await _settingsService.SaveAsync();
        _viewModel.ScanViewModel.StopCamera();
        TrayIcon.Dispose();
        Application.Current.Shutdown();
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

    private void ShowAndActivate()
    {
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
        RestoreFocusToWindow();
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
