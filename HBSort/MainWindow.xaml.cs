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

    private void Window_Closing(object sender, CancelEventArgs e)
    {
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
        SaveWindowState();
        await _settingsService.SaveAsync();
        _viewModel.ScanViewModel.StopCamera();
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }

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
