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
        _viewModel.OpenSettingsRequested += (_, args) => OpenSettingsForTab(args.InitialTab);

        // UX X.21 Teil 3: Strg+Q beendet die App. Analog zum Settings-Event -
        // das VM darf nicht selbst Application.Shutdown() rufen.
        _viewModel.ExitAppRequested += (_, _) => ExitApp_Click(this, new RoutedEventArgs());
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowState();

        // FocusTrap initial fokussieren, damit kein Control (z.B. ein Button)
        // beim Start den Fokus haelt. Sonst koennten Buttons spaeter via Leertaste
        // wieder aktiviert werden.
        FocusTrap.Focus();

        // v0.1.22-beta.1 Fix 2026-05-12: First-Run-Check und Camera-Init via
        // Dispatcher.BeginInvoke mit ContextIdle-Priority. Damit wartet die
        // Logik bis WPF alle initialen Render-Passes fuer MainWindow durch hat.
        //
        // Vorher (UX X.34 v0.1.20-beta.3): synchroner await in Loaded -
        //   - ShowDialog() blockt den UI-Thread BEVOR MainWindow gerendert ist,
        //     Welcome-Dialog erschien auf jungfraeulicher Installation vor dem
        //     noch leeren MainWindow.
        //   - InitializeCameraAsync blockierte 5-10s den Loaded-Handler,
        //     sichtbare Luecke zwischen Splash-Close und sichtbarem MainWindow.
        //
        // Mit ContextIdle:
        //   - WPF schliesst alle Render-Passes ab BEVOR die Lambda laeuft
        //   - MainWindow ist vollstaendig sichtbar wenn Welcome (falls noetig)
        //     erscheint
        //   - Camera-Init laeuft asynchron, blockt nicht mehr (User kann
        //     waehrenddessen Welcome bedienen, scannen geht erst nach
        //     "Bereit"-Status)
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await ShowFirstRunDialogIfNeededAsync();

            // v0.1.22-beta.1 Fix: Camera-Init als fire-and-forget statt await.
            // Sonst blockt der Loaded-Handler 5-10s und MainWindow rendert
            // nicht fertig. Wenn der User scannen will bevor Camera bereit
            // ist, zeigt die App schon einen Toast "Camera nicht bereit".
            _ = _viewModel.ScanViewModel.InitializeCameraAsync();

            // Brickognize-Health-Check im Hintergrund (war schon fire-and-forget).
            _ = _viewModel.RunBrickognizeHealthCheckAsync();

            _viewModel.StatusText = "Bereit";
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// UX X.34 v0.1.20-beta.2: prueft den First-Run-Status und zeigt den
    /// Onboarding-Dialog wenn das Setup unvollstaendig ist UND der User
    /// nicht aktiv "Loslegen" geklickt hat (ShowFirstRunDialog=true).
    /// Best-effort - bei Fehler still loggen, App bleibt nutzbar.
    /// </summary>
    private async Task ShowFirstRunDialogIfNeededAsync()
    {
        try
        {
            var settings = App.Services.GetRequiredService<HBSort.Core.Services.ISettingsService>();
            if (!settings.Current.ShowFirstRunDialog) return;

            var firstRun = App.Services.GetRequiredService<HBSort.Core.Services.IFirstRunService>();
            var status = await firstRun.CheckStatusAsync();
            if (status == HBSort.Core.Services.FirstRunStatus.Complete)
            {
                // Setup ist sowieso komplett - Setting auch auf false damit
                // wir den Check nicht jeden Start machen muessen.
                settings.Current.ShowFirstRunDialog = false;
                await settings.SaveAsync();
                return;
            }

            var bulkImport = App.Services.GetRequiredService<HBSort.Core.Services.IBlBulkImportService>();
            var vm = new ViewModels.FirstRunDialogViewModel(bulkImport, firstRun, status);
            var dlg = new Views.FirstRunDialog(vm, settings) { Owner = this };
            dlg.ShowDialog();
        }
        catch (System.Exception ex)
        {
            Serilog.Log.Warning(ex, "First-Run-Dialog konnte nicht gezeigt werden");
        }
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
        var scanVm = _viewModel.ScanViewModel;

        // v0.1.22-beta.1 Block F: konsolidiertes Overlay (Single + Group).
        // Beide Modi schliessen via Enter/Space/Esc; im Group-Mode werden
        // 1/2/3 zusaetzlich geblockt damit ein versehentlicher Hotkey-Klick
        // unter dem Modal keinen Scan-Vorschlag aktiviert.
        if (scanVm != null && scanVm.IsBinInstructionVisible)
        {
            if (IsTextInputFocused()) return;
            if (e.Key is Key.Enter or Key.Return or Key.Space or Key.Escape)
            {
                scanVm.DismissBinInstruction();
                e.Handled = true;
                return;
            }
            // Im Group-Mode 1/2/3 blocken (Single-Mode hat Auto-Dismiss-
            // Timer und ist transient).
            if (scanVm.BinInstruction.Mode == BinInstructionMode.Group
                && e.Key is Key.D1 or Key.D2 or Key.D3
                          or Key.NumPad1 or Key.NumPad2 or Key.NumPad3)
            {
                e.Handled = true;
                return;
            }
        }

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

        // UX X.31 Block B (v0.1.18) / UX X.33 Block M Erweiterung: Enter =
        // "In Fach legen" fuer Pending-Minifig ODER "Lagern" fuer Pending-
        // Part (Einzelteil). Spart den Mausklick beim Sortieren vieler
        // Items. Nur im Sortier-Tab + nicht in Eingabefeldern.
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            if (IsTextInputFocused()) return;
            if (!_viewModel.IsMainTabSorting) return;

            var pendingMinifig = scanVm?.PendingMinifig;
            if (pendingMinifig != null && pendingMinifig.CanPersist)
            {
                var cmd = scanVm!.PersistPendingCommand;
                if (cmd.CanExecute(null))
                {
                    cmd.Execute(null);
                    e.Handled = true;
                }
                return;
            }

            // UX X.33 v0.1.19-beta.7 Block M: Pending-Part-Pfad. Enter
            // ruft StoreFloatingFromPendingPartAsync - inkl. Auto-Mapping
            // und Anweisungs-Popup wie der Click-Handler.
            var pendingPart = scanVm?.PendingPart;
            if (pendingPart != null && !pendingPart.IsBusy && pendingPart.SelectedFloatingBin != null)
            {
                _ = scanVm!.StoreFloatingFromPendingPartAsync();
                e.Handled = true;
                return;
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
            return;
        }

        // UX X.30 Block D Bug-Fix (v0.1.17): Hotkeys 1/2/3 fuer Brickognize-
        // Vorschlaege. Vorher in SortingView_PreviewKeyDown - aber das hat
        // nur gefeuert wenn der Focus innerhalb der SortingView lag, was
        // nach einem Scan oft NICHT der Fall ist (Window-Level-Focus).
        // Hier auf MainWindow-Ebene faengt es alles. Tab-Check verhindert
        // dass die Tasten in Lagerliste/Hilfe/Verlauf reagieren.
        if (e.Key is Key.D1 or Key.NumPad1 or Key.D2 or Key.NumPad2 or Key.D3 or Key.NumPad3
            && (Keyboard.Modifiers & ModifierKeys.Control) == 0
            && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            if (IsTextInputFocused()) return;
            if (!_viewModel.IsMainTabSorting) return;

            var index = e.Key switch
            {
                Key.D1 or Key.NumPad1 => 0,
                Key.D2 or Key.NumPad2 => 1,
                Key.D3 or Key.NumPad3 => 2,
                _ => -1
            };
            var cards = _viewModel.ScanViewModel?.ResultCards;
            if (cards == null || index < 0 || index >= cards.Count) return;

            _ = _viewModel.ScanViewModel!.SelectCardAsync(index);
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
        => OpenSettingsForTab(Views.SettingsTab.Default);

    /// <summary>
    /// v0.1.22-beta.3 (2026-05-13): Settings mit Tab-Hint oeffnen. Volle-
    /// Faecher-Banner-Buttons aus den Dialogen springen so direkt auf den
    /// Lagerfaecher-Tab statt auf dem Default-Tab (Erkennung) zu landen.
    /// </summary>
    private void OpenSettingsForTab(Views.SettingsTab initialTab)
    {
        var settingsVm = App.Services.GetRequiredService<SettingsViewModel>();
        var settingsWindow = new Views.SettingsWindow(settingsVm, initialTab) { Owner = this };

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
